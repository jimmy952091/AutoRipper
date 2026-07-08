using System;
using System.Security.Cryptography;
using System.Text;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// The shared-secret gate for the LAN session. Uses an HMAC-SHA256 challenge-response so a
    /// connecting peer proves it holds the same secret WITHOUT ever sending the secret:
    ///
    ///   1. server generates a random per-connection nonce and sends it (AUTH_CHALLENGE)
    ///   2. client returns HMAC-SHA256(secret, nonce) as hex (AUTH_RESPONSE)
    ///   3. server computes the same HMAC and compares in constant time
    ///
    /// A random port scanner that finds the open port can't answer the challenge and is dropped. A
    /// passive sniffer sees only a nonce + a hash, neither of which reveals the secret, and the
    /// per-connection nonce means a captured response can't be replayed to a different challenge.
    ///
    /// This is a LAN gate, NOT transport encryption — payloads after the handshake are still
    /// plaintext. The rule (enforced by the server refusing to start without a secret, and by
    /// documentation) is: don't port-forward this to the internet.
    /// </summary>
    public static class NodeAuth
    {
        /// <summary>A fresh random 32-byte nonce as hex, unique per connection.</summary>
        public static string GenerateNonce()
        {
            byte[] bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(bytes);
            }
            return ToHex(bytes);
        }

        /// <summary>The proof a client sends: HMAC-SHA256(secret, nonce) as hex.</summary>
        public static string ComputeProof(string sharedSecret, string nonce)
        {
            byte[] key = Encoding.UTF8.GetBytes(sharedSecret ?? "");
            using (var hmac = new HMACSHA256(key))
            {
                byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(nonce ?? ""));
                return ToHex(hash);
            }
        }

        /// <summary>
        /// Verifies a client's proof against the expected value in constant time, so a network
        /// attacker can't learn the correct proof byte-by-byte from response-timing differences.
        /// </summary>
        public static bool VerifyProof(string sharedSecret, string nonce, string providedProof)
        {
            string expected = ComputeProof(sharedSecret, nonce);
            return ConstantTimeEquals(expected, providedProof ?? "");
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            // Compare every character regardless of mismatches so total time doesn't leak how much
            // of the proof was correct. Length difference still short-circuits, which is fine — the
            // proof length is fixed and public (64 hex chars).
            if (a.Length != b.Length) { return false; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { sb.Append(b.ToString("x2")); }
            return sb.ToString();
        }
    }
}
