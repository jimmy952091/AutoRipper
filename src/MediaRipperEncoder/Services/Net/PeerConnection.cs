using System;
using System.IO;
using System.Net.Sockets;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// A thin wrapper around a connected TCP socket's stream that reads/writes framed
    /// <see cref="NetMessage"/>s via <see cref="MessageCodec"/>. Writes are lock-guarded so a
    /// background sender (e.g. progress updates) can't interleave bytes with another writer on the
    /// same connection. Reads are expected to happen from a single dedicated thread.
    /// </summary>
    public class PeerConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly object _writeLock = new object();

        public PeerConnection(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        /// <summary>Reads the next framed message, or null when the peer cleanly closed the connection.</summary>
        public NetMessage Read()
        {
            return MessageCodec.Read(_stream);
        }

        /// <summary>Writes one framed message. Thread-safe against other Write calls on this connection.</summary>
        public void Write(NetMessage message)
        {
            lock (_writeLock)
            {
                MessageCodec.Write(_stream, message);
            }
        }

        /// <summary>
        /// Adjusts the socket read timeout. The server keeps a tight idle limit (dead sessions
        /// must be reaped) but raises it hugely around a bulk file receive — a slow sender's WiFi
        /// may legally stall far longer than idle heartbeat silence ever could.
        /// </summary>
        public void SetReceiveTimeout(int milliseconds)
        {
            try { _client.ReceiveTimeout = milliseconds; }
            catch { /* socket closing — session is ending anyway */ }
        }

        /// <summary>The raw stream, for streaming file bytes outside the message framing (later phase).</summary>
        public Stream RawStream { get { return _stream; } }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { /* ignore */ }
            try { _client.Close(); } catch { /* ignore */ }
        }
    }
}
