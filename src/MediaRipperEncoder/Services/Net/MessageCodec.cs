using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// Frames <see cref="NetMessage"/>s on a byte stream so both ends always agree where one message
    /// ends and the next begins — TCP is a stream, not a series of messages, so we must frame them
    /// ourselves. Wire format per message:
    ///
    ///     [4 bytes: big-endian UInt32 length N] [N bytes: UTF-8 JSON]
    ///
    /// Works over any Stream, so the exact same code path is exercised by a MemoryStream in unit
    /// tests and by a live NetworkStream over the LAN. Synchronous blocking I/O is fine here: each
    /// connection is serviced on its own dedicated thread.
    /// </summary>
    public static class MessageCodec
    {
        // Reject absurd lengths so a corrupt/hostile stream can't make us allocate gigabytes. A
        // control message is tiny; even a base64 file chunk stays well under this.
        public const int MaxMessageBytes = 16 * 1024 * 1024; // 16 MB

        public static void Write(Stream stream, NetMessage message)
        {
            if (stream == null) { throw new ArgumentNullException("stream"); }
            if (message == null) { throw new ArgumentNullException("message"); }

            string json = JsonConvert.SerializeObject(message);
            byte[] payload = Encoding.UTF8.GetBytes(json);

            if (payload.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException("Outgoing message exceeds " + MaxMessageBytes + " bytes.");
            }

            byte[] prefix = LengthPrefix(payload.Length);
            stream.Write(prefix, 0, prefix.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        /// <summary>
        /// Reads one framed message. Returns null on a clean end-of-stream (peer closed the
        /// connection between messages), which callers treat as "connection ended," not an error.
        /// Throws on a malformed frame or an over-length message.
        /// </summary>
        public static NetMessage Read(Stream stream)
        {
            if (stream == null) { throw new ArgumentNullException("stream"); }

            byte[] prefix = new byte[4];
            if (!TryReadExactly(stream, prefix, 4))
            {
                return null; // clean EOF at a message boundary
            }

            int length = LengthFromPrefix(prefix);
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException("Framed message length out of range: " + length);
            }

            byte[] payload = new byte[length];
            if (!TryReadExactly(stream, payload, length))
            {
                throw new EndOfStreamException("Stream ended partway through a message body.");
            }

            string json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject<NetMessage>(json);
        }

        // Big-endian so the framing is explicit and endianness-independent across machines.
        private static byte[] LengthPrefix(int length)
        {
            return new[]
            {
                (byte)((length >> 24) & 0xFF),
                (byte)((length >> 16) & 0xFF),
                (byte)((length >> 8) & 0xFF),
                (byte)(length & 0xFF)
            };
        }

        private static int LengthFromPrefix(byte[] p)
        {
            return (p[0] << 24) | (p[1] << 16) | (p[2] << 8) | p[3];
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes, looping because a single Stream.Read may
        /// return fewer bytes than asked (very common on a network stream). Returns false only if
        /// the stream ends before ANY of the bytes arrive (clean EOF); a partial read throws via
        /// the caller.
        /// </summary>
        private static bool TryReadExactly(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    if (offset == 0) { return false; } // nothing read at all -> clean EOF
                    throw new EndOfStreamException("Stream ended after " + offset + " of " + count + " bytes.");
                }
                offset += read;
            }
            return true;
        }
    }
}
