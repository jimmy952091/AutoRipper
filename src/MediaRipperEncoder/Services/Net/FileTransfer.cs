using System;
using System.IO;
using System.Security.Cryptography;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// Streams a ripped file from the ripper client to the encoder server over an established
    /// <see cref="PeerConnection"/>. The protocol is:
    ///
    ///   1. sender writes a FILE_BEGIN control message carrying { name, size, sha256 }
    ///   2. sender writes exactly `size` RAW bytes to the connection's stream (no per-chunk framing,
    ///      no base64 — the receiver already knows the exact byte count from FILE_BEGIN)
    ///   3. receiver reads exactly `size` bytes, hashing as it goes, and compares to the sha256
    ///
    /// Sending raw bytes (rather than base64 inside JSON messages) avoids ~33% transfer bloat and the
    /// 16 MB per-message cap. Because the raw bytes are interleaved on the same socket as framed
    /// control messages, the transfer MUST own the connection for its duration — no other thread may
    /// write to it mid-file. Our protocol is serial (one client, one file at a time), so that holds.
    ///
    /// The SHA-256 check means a truncated or corrupted transfer is DETECTED, not silently encoded
    /// into a bad library file — consistent with the project's "never assume success" standard.
    /// </summary>
    public static class FileTransfer
    {
        private const int ChunkSize = 64 * 1024;

        /// <summary>
        /// Sends <paramref name="filePath"/> to the peer. Returns the SHA-256 (hex) that was sent, so
        /// the caller can log/compare. <paramref name="onProgress"/> reports (bytesSent, totalBytes).
        /// </summary>
        public static string SendFile(PeerConnection conn, string filePath, string logicalName,
            Action<long, long> onProgress = null)
        {
            var info = new FileInfo(filePath);
            long size = info.Length;
            string hash = ComputeSha256(filePath);

            conn.Write(new NetMessage(MsgType.FileBegin)
                .With("name", logicalName ?? Path.GetFileName(filePath))
                .With("size", size)
                .With("sha256", hash));

            Stream raw = conn.RawStream;
            using (FileStream fs = File.OpenRead(filePath))
            {
                byte[] buffer = new byte[ChunkSize];
                long sent = 0;
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    raw.Write(buffer, 0, read);
                    sent += read;
                    if (onProgress != null) { onProgress(sent, size); }
                }
                raw.Flush();
            }

            Logger.Info("FileTransfer: sent '" + logicalName + "' (" + size + " bytes, sha256 " +
                        hash.Substring(0, 8) + "...).");
            return hash;
        }

        /// <summary>
        /// Receives a file described by a just-read FILE_BEGIN message, writing it to
        /// <paramref name="destPath"/>. Returns true only if exactly `size` bytes arrived AND the
        /// SHA-256 matches. On mismatch the partial file is deleted so it can't be mistaken for good.
        /// </summary>
        public static bool ReceiveFile(PeerConnection conn, NetMessage fileBegin, string destPath,
            Action<long, long> onProgress = null)
        {
            long size = fileBegin.GetLong("size");
            string expected = fileBegin.GetString("sha256");

            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

            Stream raw = conn.RawStream;
            bool ok = false;
            try
            {
                using (var sha = SHA256.Create())
                using (FileStream fs = File.Create(destPath))
                {
                    byte[] buffer = new byte[ChunkSize];
                    long remaining = size;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int read = raw.Read(buffer, 0, want);
                        if (read <= 0)
                        {
                            throw new EndOfStreamException("Connection closed with " + remaining +
                                " of " + size + " bytes still expected.");
                        }
                        fs.Write(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        remaining -= read;
                        if (onProgress != null) { onProgress(size - remaining, size); }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);

                    string actual = ToHex(sha.Hash);
                    ok = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                    if (!ok)
                    {
                        Logger.Error("FileTransfer: sha256 MISMATCH for '" + destPath +
                            "' (expected " + Short(expected) + ", got " + Short(actual) + ").");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("FileTransfer: receive failed for '" + destPath + "'.", ex);
                ok = false;
            }

            if (!ok)
            {
                try { if (File.Exists(destPath)) { File.Delete(destPath); } }
                catch { /* leave it; the mismatch is already logged */ }
            }
            else
            {
                Logger.Info("FileTransfer: received '" + destPath + "' OK (" + size + " bytes).");
            }
            return ok;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                return ToHex(sha.ComputeHash(fs));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { sb.Append(b.ToString("x2")); }
            return sb.ToString();
        }

        private static string Short(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "(none)" : (hash.Length > 8 ? hash.Substring(0, 8) + "..." : hash);
        }
    }
}
