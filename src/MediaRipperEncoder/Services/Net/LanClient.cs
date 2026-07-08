using System;
using System.Net.Sockets;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// The ripper-client side of the LAN session. Connects to the encoder server, completes the
    /// HELLO handshake, and exposes the connection for sending jobs / receiving progress. Records
    /// the server's session id so a later reconnect can request a REJOIN of the same session.
    /// </summary>
    public class LanClient : IDisposable
    {
        private readonly string _clientName;
        private readonly string _sharedSecret;
        private TcpClient _tcp;
        private PeerConnection _conn;

        public string ServerName { get; private set; }
        public string SessionId { get; private set; }
        public bool IsConnected { get { return _conn != null; } }

        /// <summary>The live connection, for streaming files (see <see cref="FileTransfer"/>). Null until connected.</summary>
        public PeerConnection Connection { get { return _conn; } }

        public LanClient(string clientName, string sharedSecret)
        {
            _clientName = string.IsNullOrWhiteSpace(clientName) ? Environment.MachineName : clientName;
            _sharedSecret = sharedSecret ?? "";
        }

        /// <summary>
        /// Connects and performs the handshake. Returns true on success. Throws nothing on a normal
        /// failure to connect — returns false and logs — so the UI can show a friendly "can't reach
        /// the server" message.
        /// </summary>
        public bool Connect(string host, int port, int timeoutMs = 8000)
        {
            try
            {
                _tcp = new TcpClient { NoDelay = true };

                // TcpClient.Connect has no timeout overload on net48; use the async begin/end with a wait.
                IAsyncResult ar = _tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    _tcp.Close();
                    Logger.Error("LanClient: timed out connecting to " + host + ":" + port);
                    return false;
                }
                _tcp.EndConnect(ar);

                _conn = new PeerConnection(_tcp);
                _conn.Write(new NetMessage(MsgType.Hello)
                    .With("name", _clientName)
                    .With("version", "1"));

                // Answer the server's shared-secret challenge (HMAC of its nonce). The secret never
                // goes on the wire — only the proof does.
                NetMessage challenge = _conn.Read();
                if (challenge == null || challenge.Type != MsgType.AuthChallenge)
                {
                    Logger.Error("LanClient: expected AUTH_CHALLENGE from server.");
                    Dispose();
                    return false;
                }
                string proof = NodeAuth.ComputeProof(_sharedSecret, challenge.GetString("nonce"));
                _conn.Write(new NetMessage(MsgType.AuthResponse).With("proof", proof));

                NetMessage ack = _conn.Read();
                if (ack != null && ack.Type == MsgType.AuthFail)
                {
                    Logger.Error("LanClient: server rejected our shared secret. Check both machines use the same value.");
                    Dispose();
                    return false;
                }
                if (ack == null || ack.Type != MsgType.HelloAck)
                {
                    Logger.Error("LanClient: server did not return HELLO_ACK.");
                    Dispose();
                    return false;
                }

                ServerName = ack.GetString("server");
                SessionId = ack.GetString("session");
                Logger.Info("LanClient: connected to '" + ServerName + "' (session " + SessionId + ").");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("LanClient: connect to " + host + ":" + port + " failed.", ex);
                Dispose();
                return false;
            }
        }

        /// <summary>Sends a message to the server.</summary>
        public void Send(NetMessage message)
        {
            if (_conn == null) { throw new InvalidOperationException("Not connected."); }
            _conn.Write(message);
        }

        /// <summary>Reads the next message from the server (blocking), or null when it disconnects.</summary>
        public NetMessage Receive()
        {
            if (_conn == null) { throw new InvalidOperationException("Not connected."); }
            return _conn.Read();
        }

        public void Dispose()
        {
            try { if (_conn != null) { _conn.Dispose(); } }
            catch { /* ignore */ }
            finally { _conn = null; }
            try { if (_tcp != null) { _tcp.Close(); } }
            catch { /* ignore */ }
        }
    }
}
