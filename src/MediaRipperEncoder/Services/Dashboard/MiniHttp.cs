// AutoRipper — automated disc ripping/encoding for Plex, Jellyfin, and other media servers.
// Copyright (C) 2026 James Spurgeon (heto.black@gmail.com)
//
// This program is free software: you can redistribute it and/or modify it under the terms of
// the GNU Affero General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License along with this
// program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// A deliberately tiny HTTP/1.1 request reader + response writer built directly on a byte
    /// stream. We use this instead of <c>System.Net.HttpListener</c> for one important reason:
    /// HttpListener needs a URL-ACL reservation (or administrator rights) to listen on anything but
    /// localhost, and a core goal of the dashboard is to run WITHOUT elevation — the same lesson the
    /// Windows 7 eject saga taught us. A plain TcpListener socket bind on a high port needs no such
    /// privilege, so the dashboard server sits on TcpListener and speaks just enough HTTP here.
    ///
    /// Scope is intentionally small: no chunked transfer, no keep-alive (every response says
    /// Connection: close), no multipart. That's all the dashboard needs, and less surface to get
    /// wrong. Header and body sizes are capped so a bad/hostile client can't make us allocate wildly.
    /// </summary>
    public static class MiniHttp
    {
        private const int MaxHeaderBytes = 64 * 1024;      // 64 KB of request headers is plenty
        private const int MaxBodyBytes = 2 * 1024 * 1024;  // 2 MB — status reports are a few KB

        /// <summary>A parsed request. Header and cookie keys are lower-cased for easy lookup.</summary>
        public class Request
        {
            public string Method = "";
            public string Path = "";
            public string Query = "";
            public string Body = "";
            public readonly Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Cookies =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public string Header(string name)
            {
                string v;
                return Headers.TryGetValue(name, out v) ? v : "";
            }

            public string Cookie(string name)
            {
                string v;
                return Cookies.TryGetValue(name, out v) ? v : "";
            }
        }

        /// <summary>
        /// Reads and parses one request from the stream. Returns null if the connection closed or the
        /// request was malformed / oversized (the caller should then just close the socket).
        /// </summary>
        public static Request ReadRequest(Stream stream)
        {
            byte[] headerBytes = ReadUntilHeaderEnd(stream);
            if (headerBytes == null) { return null; }

            string headerText = Encoding.ASCII.GetString(headerBytes);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || lines[0].Length == 0) { return null; }

            // Request line: METHOD SP PATH SP HTTP/x.y
            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2) { return null; }

            var req = new Request { Method = parts[0].ToUpperInvariant() };
            string target = parts[1];
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                req.Path = target.Substring(0, q);
                req.Query = target.Substring(q + 1);
            }
            else
            {
                req.Path = target;
            }

            // Header lines until the first blank line.
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) { break; }
                int colon = line.IndexOf(':');
                if (colon <= 0) { continue; }
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                req.Headers[name] = value;
            }

            ParseCookies(req);

            // Body, if a length was declared.
            int contentLength = 0;
            string cl = req.Header("Content-Length");
            if (!string.IsNullOrEmpty(cl) && int.TryParse(cl, out contentLength) && contentLength > 0)
            {
                if (contentLength > MaxBodyBytes) { return null; }
                byte[] body = ReadExact(stream, contentLength);
                if (body == null) { return null; }
                req.Body = Encoding.UTF8.GetString(body);
            }

            return req;
        }

        private static void ParseCookies(Request req)
        {
            string cookieHeader = req.Header("Cookie");
            if (string.IsNullOrEmpty(cookieHeader)) { return; }
            foreach (string pair in cookieHeader.Split(';'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) { continue; }
                string name = pair.Substring(0, eq).Trim();
                string value = pair.Substring(eq + 1).Trim();
                if (name.Length > 0) { req.Cookies[name] = value; }
            }
        }

        /// <summary>Reads bytes up to and including the CRLFCRLF that ends the header block.</summary>
        private static byte[] ReadUntilHeaderEnd(Stream stream)
        {
            var buffer = new MemoryStream();
            int state = 0; // tracks the \r \n \r \n sequence
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                buffer.WriteByte((byte)b);
                if (buffer.Length > MaxHeaderBytes) { return null; }

                // Advance the terminator state machine.
                if (state == 0 && b == '\r') { state = 1; }
                else if (state == 1 && b == '\n') { state = 2; }
                else if (state == 2 && b == '\r') { state = 3; }
                else if (state == 3 && b == '\n') { return buffer.ToArray(); }
                else { state = (b == '\r') ? 1 : 0; }
            }
            return null; // connection closed before a complete header block
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) { return null; } // closed early
                read += n;
            }
            return buffer;
        }

        // ---- responses ----

        /// <summary>Writes a complete HTTP/1.1 response and flushes it. Connection is always closed.</summary>
        public static void WriteResponse(Stream stream, int statusCode, string statusText,
            string contentType, byte[] body, IEnumerable<string> extraHeaders = null)
        {
            body = body ?? new byte[0];
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(statusText).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Connection: close\r\n");
            // Status pages must never be cached, and no MIME sniffing.
            head.Append("Cache-Control: no-store\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            if (extraHeaders != null)
            {
                foreach (string h in extraHeaders)
                {
                    if (!string.IsNullOrEmpty(h)) { head.Append(h).Append("\r\n"); }
                }
            }
            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (body.Length > 0) { stream.Write(body, 0, body.Length); }
            stream.Flush();
        }

        public static void WriteText(Stream stream, int statusCode, string statusText, string text,
            IEnumerable<string> extraHeaders = null)
        {
            WriteResponse(stream, statusCode, statusText, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes(text ?? ""), extraHeaders);
        }

        public static void WriteJson(Stream stream, int statusCode, string statusText, string json,
            IEnumerable<string> extraHeaders = null)
        {
            WriteResponse(stream, statusCode, statusText, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(json ?? ""), extraHeaders);
        }

        public static void WriteHtml(Stream stream, int statusCode, string statusText, string html,
            IEnumerable<string> extraHeaders = null)
        {
            WriteResponse(stream, statusCode, statusText, "text/html; charset=utf-8",
                Encoding.UTF8.GetBytes(html ?? ""), extraHeaders);
        }
    }
}
