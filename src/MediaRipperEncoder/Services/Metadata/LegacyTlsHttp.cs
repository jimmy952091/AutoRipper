/*
 * AutoRipper — rips and encodes physical media into Plex/Jellyfin-ready libraries.
 * Copyright (C) 2026 Heto <heto.black@gmail.com>
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the
 * GNU Affero General Public License as published by the Free Software Foundation, either version 3
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
 * even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License along with this program.
 * If not, see <https://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// The Windows 7 "translator": an HTTPS GET client whose TLS is done by BouncyCastle
    /// (a managed TLS engine with its own modern cipher suites) instead of Windows SChannel.
    ///
    /// Why it exists: Windows 7's SChannel lacks the ECDHE+AES-GCM cipher suites that
    /// musicbrainz.org's servers require, so even a fully patched Win7 machine fails the TLS
    /// handshake ("Could not create SSL/TLS secure channel") — an OS limit no app setting can
    /// fix. .NET normally delegates TLS to the OS; this class doesn't. Callers use the normal
    /// HttpClient path FIRST and fall back here only when it fails with a secure-channel error,
    /// so modern Windows keeps its OS TLS stack (which also gets OS security updates).
    ///
    /// STRICT BY DESIGN — this is a translator, never a proxy:
    ///  - Only HTTPS, and only to an allow-listed set of metadata hosts (MusicBrainz, Cover Art
    ///    Archive, and the archive.org hosts its images redirect to). Anything else is refused
    ///    before a socket is opened, including on redirects.
    ///  - The server certificate is FULLY validated: the chain must build to a Windows-trusted
    ///    root (or, exactly like the normal path, to one of the app's pinned fallback roots —
    ///    see <see cref="SharedHttp.PinnedTrustAnchors"/>), and the certificate must actually
    ///    name the host we dialed (SAN match, single-label wildcards only). No bypass mode.
    /// </summary>
    public static class LegacyTlsHttp
    {
        private const int ConnectTimeoutMs = 15000;
        private const int IoTimeoutMs = 20000;
        private const int MaxRedirects = 5;
        private const int MaxResponseBytes = 32 * 1024 * 1024; // cover art is well under this

        /// <summary>Hosts this transport may contact — exact name or a subdomain of one.</summary>
        private static readonly string[] AllowedHostSuffixes =
        {
            "musicbrainz.org",
            "coverartarchive.org",
            "archive.org" // Cover Art Archive images 30x-redirect to ia*.us.archive.org
        };

        public class FetchResult
        {
            public int StatusCode;
            public byte[] Body;
            public string BodyText { get { return Encoding.UTF8.GetString(Body ?? new byte[0]); } }
        }

        /// <summary>True when an exception chain is the SChannel "can't negotiate TLS" failure —
        /// the one condition this fallback transport exists for.</summary>
        public static bool LooksLikeSecureChannelFailure(Exception ex)
        {
            Exception current = ex;
            while (current != null)
            {
                string m = current.Message ?? "";
                if (m.IndexOf("secure channel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.IndexOf("SEC_E_", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                current = current.InnerException;
            }
            return false;
        }

        /// <summary>Whether <paramref name="host"/> is on the allowlist. Public for tests.</summary>
        public static bool IsAllowedHost(string host)
        {
            if (string.IsNullOrEmpty(host)) { return false; }
            host = host.TrimEnd('.').ToLowerInvariant();
            foreach (string allowed in AllowedHostSuffixes)
            {
                if (host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Performs an HTTPS GET over BouncyCastle TLS, following redirects within the allowlist.
        /// Throws on transport/validation errors; HTTP error statuses are returned, not thrown,
        /// so callers keep their existing 404-means-unknown-disc handling.
        /// </summary>
        public static FetchResult Get(string url, string userAgent, string accept = null)
        {
            var uri = new Uri(url);
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("LegacyTlsHttp only speaks https ('" + uri + "').");
                }
                if (!IsAllowedHost(uri.Host))
                {
                    throw new InvalidOperationException(
                        "Refusing to contact '" + uri.Host + "' — not an allow-listed metadata host.");
                }

                FetchResult result;
                string location;
                FetchOnce(uri, userAgent, accept, out result, out location);

                if (location != null)
                {
                    // 301/302/303/307/308 — resolve relative targets against the current URL and
                    // loop; the allowlist check at the top of the loop covers every hop.
                    uri = Uri.IsWellFormedUriString(location, UriKind.Absolute)
                        ? new Uri(location)
                        : new Uri(uri, location);
                    continue;
                }
                return result;
            }
            throw new InvalidOperationException("Too many redirects fetching '" + url + "'.");
        }

        private static void FetchOnce(Uri uri, string userAgent, string accept,
            out FetchResult result, out string redirectLocation)
        {
            using (var tcp = new TcpClient())
            {
                // Connect with an explicit timeout (TcpClient.Connect alone has none on net48).
                IAsyncResult ar = tcp.BeginConnect(uri.Host, uri.Port > 0 ? uri.Port : 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    tcp.Close();
                    throw new IOException("Timed out connecting to " + uri.Host + ".");
                }
                tcp.EndConnect(ar);
                tcp.NoDelay = true;
                NetworkStream network = tcp.GetStream();
                network.ReadTimeout = IoTimeoutMs;
                network.WriteTimeout = IoTimeoutMs;

                var protocol = new TlsClientProtocol(network);
                var tlsClient = new StrictTlsClient(uri.Host);
                protocol.Connect(tlsClient); // handshake + certificate validation happen here

                Stream tls = protocol.Stream;

                var request = new StringBuilder();
                request.Append("GET ").Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
                request.Append("Host: ").Append(uri.Host).Append("\r\n");
                request.Append("User-Agent: ").Append(userAgent ?? "AutoRipper").Append("\r\n");
                if (!string.IsNullOrEmpty(accept)) { request.Append("Accept: ").Append(accept).Append("\r\n"); }
                // identity = no gzip: tiny JSON responses aren't worth a decompressor here.
                request.Append("Accept-Encoding: identity\r\n");
                request.Append("Connection: close\r\n\r\n");
                byte[] requestBytes = Encoding.ASCII.GetBytes(request.ToString());
                tls.Write(requestBytes, 0, requestBytes.Length);
                tls.Flush();

                // Connection: close — read until the server ends the stream, then parse.
                byte[] raw = ReadAll(tls);
                try { protocol.Close(); } catch { /* server closed first; that's the plan */ }

                ParseResponse(raw, out result, out redirectLocation);
            }
        }

        private static byte[] ReadAll(Stream stream)
        {
            using (var buffer = new MemoryStream())
            {
                byte[] chunk = new byte[16 * 1024];
                int read;
                try
                {
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                        if (buffer.Length > MaxResponseBytes)
                        {
                            throw new IOException("Response exceeded the " + MaxResponseBytes + "-byte cap.");
                        }
                    }
                }
                catch (TlsNoCloseNotifyException)
                {
                    // Some servers drop the socket without a TLS close_notify after
                    // Connection: close. We have the full HTTP response (framing below
                    // verifies it), so treat this as end-of-stream, not an error.
                }
                return buffer.ToArray();
            }
        }

        /// <summary>Splits status/headers/body, decoding chunked bodies. Public for offline tests.</summary>
        public static void ParseResponse(byte[] raw, out FetchResult result, out string redirectLocation)
        {
            int headerEnd = FindDoubleCrlf(raw);
            if (headerEnd < 0) { throw new IOException("Malformed HTTP response (no header terminator)."); }

            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) { throw new IOException("Malformed HTTP response (no status line)."); }

            // "HTTP/1.1 200 OK"
            string[] statusParts = lines[0].Split(' ');
            int status;
            if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out status))
            {
                throw new IOException("Malformed HTTP status line: '" + lines[0] + "'.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
                }
            }

            int bodyStart = headerEnd + 4;
            byte[] body = new byte[raw.Length - bodyStart];
            Array.Copy(raw, bodyStart, body, 0, body.Length);

            string transferEncoding;
            if (headers.TryGetValue("Transfer-Encoding", out transferEncoding) &&
                transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body = DecodeChunked(body);
            }
            else
            {
                string lengthText;
                long declared;
                if (headers.TryGetValue("Content-Length", out lengthText) &&
                    long.TryParse(lengthText, out declared) && body.Length != declared)
                {
                    throw new IOException("Truncated HTTP body: got " + body.Length +
                                          " bytes, Content-Length said " + declared + ".");
                }
            }

            string location;
            bool isRedirect = status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
            redirectLocation = isRedirect && headers.TryGetValue("Location", out location) ? location : null;
            result = new FetchResult { StatusCode = status, Body = body };
        }

        /// <summary>Decodes an HTTP/1.1 chunked body. Public for offline tests.</summary>
        public static byte[] DecodeChunked(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                int pos = 0;
                while (true)
                {
                    int lineEnd = FindCrlf(data, pos);
                    if (lineEnd < 0) { throw new IOException("Malformed chunked body (no size line)."); }
                    string sizeLine = Encoding.ASCII.GetString(data, pos, lineEnd - pos);
                    int semi = sizeLine.IndexOf(';'); // chunk extensions are legal; ignore them
                    if (semi >= 0) { sizeLine = sizeLine.Substring(0, semi); }
                    int size = Convert.ToInt32(sizeLine.Trim(), 16);
                    pos = lineEnd + 2;
                    if (size == 0) { break; } // terminal chunk; trailers (if any) are ignored
                    if (pos + size > data.Length) { throw new IOException("Truncated chunk in HTTP body."); }
                    output.Write(data, pos, size);
                    pos += size + 2; // skip the chunk's trailing CRLF
                }
                return output.ToArray();
            }
        }

        private static int FindDoubleCrlf(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10) { return i; }
            }
            return -1;
        }

        private static int FindCrlf(byte[] data, int start)
        {
            for (int i = start; i + 1 < data.Length; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10) { return i; }
            }
            return -1;
        }

        // ---------------- TLS client with strict validation ----------------

        /// <summary>
        /// BouncyCastle TLS client: offers BC's default modern cipher suites (TLS 1.2/1.3 with
        /// ECDHE + AES-GCM/ChaCha — exactly what Win7's SChannel is missing), sends SNI, and
        /// REJECTS the connection unless the server's certificate chain verifies and names us.
        /// </summary>
        private sealed class StrictTlsClient : DefaultTlsClient
        {
            private readonly string _host;

            public StrictTlsClient(string host)
                : base(new BcTlsCrypto(new SecureRandom()))
            {
                _host = host;
            }

            protected override IList<ServerName> GetSniServerNames()
            {
                return new List<ServerName>
                {
                    new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(_host))
                };
            }

            public override TlsAuthentication GetAuthentication()
            {
                return new StrictAuthentication(_host);
            }
        }

        private sealed class StrictAuthentication : TlsAuthentication
        {
            private readonly string _host;

            public StrictAuthentication(string host) { _host = host; }

            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
                Certificate chain = serverCertificate != null ? serverCertificate.Certificate : null;
                if (chain == null || chain.IsEmpty)
                {
                    throw new IOException("Server presented no certificate.");
                }

                var certs = new X509Certificate2[chain.Length];
                for (int i = 0; i < chain.Length; i++)
                {
                    certs[i] = new X509Certificate2(chain.GetCertificateAt(i).GetEncoded());
                }

                ValidateChain(_host, certs);
            }

            public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest)
            {
                return null; // we never do TLS client auth
            }
        }

        /// <summary>
        /// Full certificate validation, mirroring what SslStream would do PLUS the app's pinned
        /// fallback roots: (1) the chain must build to a Windows-trusted root, or — only when the
        /// sole problem is an unknown root — to one of the exact pinned anchors (the same rule as
        /// <see cref="SharedHttp.ValidateWithPinnedRootFallback"/>); (2) the leaf must name the
        /// dialed host. Throws on any failure so the TLS handshake aborts.
        /// </summary>
        private static void ValidateChain(string host, X509Certificate2[] certs)
        {
            X509Certificate2 leaf = certs[0];

            bool trusted;
            using (var chain = new X509Chain())
            {
                for (int i = 1; i < certs.Length; i++) { chain.ChainPolicy.ExtraStore.Add(certs[i]); }
                // Matches the platform default for HttpClient (no CRL fetch), which old machines
                // couldn't reach anyway.
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                trusted = chain.Build(leaf);

                if (!trusted && OnlyRootTrustProblems(chain))
                {
                    // Same second chance as the normal path: rebuild allowing unknown CAs, then
                    // accept ONLY if the resulting root is byte-identical to a pinned anchor.
                    using (var rebuilt = new X509Chain())
                    {
                        for (int i = 1; i < certs.Length; i++) { rebuilt.ChainPolicy.ExtraStore.Add(certs[i]); }
                        foreach (X509Certificate2 pinned in SharedHttp.PinnedTrustAnchors)
                        {
                            rebuilt.ChainPolicy.ExtraStore.Add(pinned);
                        }
                        rebuilt.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                        rebuilt.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                        if (rebuilt.Build(leaf))
                        {
                            X509Certificate2 root =
                                rebuilt.ChainElements[rebuilt.ChainElements.Count - 1].Certificate;
                            foreach (X509Certificate2 pinned in SharedHttp.PinnedTrustAnchors)
                            {
                                if (root.Thumbprint == pinned.Thumbprint)
                                {
                                    trusted = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            if (!trusted)
            {
                throw new IOException("Certificate chain for '" + host + "' is not trusted.");
            }

            if (!CertificateNamesHost(leaf, host))
            {
                throw new IOException("Certificate does not name the host '" + host + "'.");
            }
        }

        private static bool OnlyRootTrustProblems(X509Chain chain)
        {
            foreach (X509ChainStatus status in chain.ChainStatus)
            {
                if (status.Status != X509ChainStatusFlags.UntrustedRoot &&
                    status.Status != X509ChainStatusFlags.PartialChain &&
                    status.Status != X509ChainStatusFlags.OfflineRevocation &&
                    status.Status != X509ChainStatusFlags.RevocationStatusUnknown &&
                    status.Status != X509ChainStatusFlags.NoError)
                {
                    return false; // expired, wrong usage, etc. — never eligible for the pinned retry
                }
            }
            return true;
        }

        /// <summary>Checks the certificate's Subject Alternative Names against the host.</summary>
        private static bool CertificateNamesHost(X509Certificate2 leaf, string host)
        {
            // Parse SANs with BouncyCastle (locale-independent, unlike X509Certificate2's
            // human-readable extension formatting).
            var parser = new Org.BouncyCastle.X509.X509CertificateParser();
            Org.BouncyCastle.X509.X509Certificate bcCert = parser.ReadCertificate(leaf.RawData);
            var names = bcCert.GetSubjectAlternativeNames();
            if (names == null) { return false; } // public CAs always issue SANs; no SANs = fail

            foreach (var entry in names)
            {
                // Each SAN entry is (GeneralName tag, value); tag 2 = dNSName.
                var list = entry as System.Collections.IList;
                if (list == null || list.Count < 2) { continue; }
                int tag;
                try { tag = Convert.ToInt32(list[0]); } catch { continue; }
                if (tag != 2) { continue; }
                if (HostMatchesPattern(host, list[1] as string)) { return true; }
            }
            return false;
        }

        /// <summary>RFC 6125-style match: exact, or a single leftmost wildcard label. Public for tests.</summary>
        public static bool HostMatchesPattern(string host, string pattern)
        {
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pattern)) { return false; }
            host = host.TrimEnd('.').ToLowerInvariant();
            pattern = pattern.TrimEnd('.').ToLowerInvariant();

            if (host == pattern) { return true; }

            // "*.example.org" matches "www.example.org" but NOT "example.org" or "a.b.example.org".
            if (pattern.StartsWith("*."))
            {
                string suffix = pattern.Substring(1); // ".example.org"
                if (!host.EndsWith(suffix, StringComparison.Ordinal)) { return false; }
                string leftLabel = host.Substring(0, host.Length - suffix.Length);
                return leftLabel.Length > 0 && leftLabel.IndexOf('.') < 0;
            }
            return false;
        }
    }
}
