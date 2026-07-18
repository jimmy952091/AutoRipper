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

        /// <summary>
        /// Protocol level the server announced in HELLO_ACK. 1 = original (send files unasked);
        /// 2+ = server expects SEND_REQUEST and answers SEND_GRANT/SEND_WAIT. Lets a new client
        /// keep working against an older server.
        /// </summary>
        public int ServerProtocol { get; private set; }

        /// <summary>
        /// Human-readable reason the last <see cref="Connect"/> returned false ("" if none) — e.g.
        /// the server was full — so the UI can say WHY it's waiting instead of a generic "reconnecting".
        /// </summary>
        public string LastFailure { get; private set; }

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
                // SendTimeout: writing into a connection that died without a FIN (WiFi blip)
                // otherwise BLOCKS forever once the TCP buffer fills — which froze a mid-upload
                // ripper solid. With a limit, the write throws, the session drops, and the
                // reconnect/resend logic does its job. Reads deliberately have NO timeout: waits
                // can be legitimately long (queued behind another ripper's transfer), and
                // liveness is proven by periodic heartbeat WRITES instead.
                _tcp = new TcpClient { NoDelay = true, SendTimeout = 30000 };

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
                    LastFailure = "The server rejected our shared secret. Check every machine uses the same value.";
                    Logger.Error("LanClient: server rejected our shared secret. Check every machine uses the same value.");
                    Dispose();
                    return false;
                }
                if (ack != null && ack.Type == MsgType.ServerFull)
                {
                    if (ack.GetString("reason") == "name-active")
                    {
                        // The server sees an ACTIVE session with this machine's name. Either this
                        // is a duplicate machine name on the LAN, or something is impersonating
                        // this ripper — either way the user must know, not just see "waiting".
                        LastFailure = "The server refused this connection: a ripper named '" +
                                      _clientName + "' is already actively connected. If this " +
                                      "machine just lost its connection, it will get in once the " +
                                      "old session dies; otherwise another device on the network " +
                                      "may be using this machine's name.";
                    }
                    else
                    {
                        // Every seat is taken. Not an error — we just retry on the normal
                        // backoff until another ripper disconnects.
                        LastFailure = "The encoder server is at its ripper limit (" + ack.GetInt("max") +
                                      "). Waiting for a slot to free up...";
                    }
                    Logger.Info("LanClient: " + LastFailure);
                    Dispose();
                    return false;
                }
                if (ack == null || ack.Type != MsgType.HelloAck)
                {
                    LastFailure = "The server did not complete the handshake.";
                    Logger.Error("LanClient: server did not return HELLO_ACK.");
                    Dispose();
                    return false;
                }

                ServerName = ack.GetString("server");
                SessionId = ack.GetString("session");
                ServerProtocol = ack.GetInt("proto", 1);
                LastFailure = "";
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

        /// <summary>
        /// Adjusts the socket write timeout. Control messages keep a tight limit (a dead link
        /// should fail fast), but BULK FILE STREAMING must tolerate long stalls: while the server
        /// is encoding, its disk is saturated and its receive rate can stall well past 30 s —
        /// a tight limit there ABORTED healthy multi-gigabyte transfers mid-file (seen live:
        /// 6m44s into a movie upload, "connection aborted by the software in your host machine").
        /// </summary>
        public void SetSendTimeout(int milliseconds)
        {
            try { if (_tcp != null) { _tcp.SendTimeout = milliseconds; } }
            catch { /* socket already closed — the session is ending anyway */ }
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
