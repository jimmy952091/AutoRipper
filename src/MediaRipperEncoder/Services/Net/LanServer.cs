using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// The encoder-server side of the LAN session. Listens for a single ripper client, completes
    /// the HELLO handshake, and then hands each received <see cref="NetMessage"/> to a callback.
    /// One client at a time by design (one ripper feeds one encoder).
    ///
    /// This is the transport skeleton: connection lifecycle + handshake + message pump. The encode
    /// queue integration (receiving files, reporting progress) layers on top via the message
    /// callback. LAN-only and unauthenticated by design.
    /// </summary>
    public class LanServer : IDisposable
    {
        private readonly string _serverName;
        private readonly string _sessionId;
        private readonly string _sharedSecret;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        /// <summary>Raised (on the connection thread) for every non-handshake message from the client.</summary>
        public event Action<NetMessage, PeerConnection> MessageReceived;

        /// <summary>Raised when a client completes the handshake, and again when it disconnects.</summary>
        public event Action<string> ClientConnected;
        public event Action<string> ClientDisconnected;

        public LanServer(string serverName, string sessionId, string sharedSecret)
        {
            _serverName = string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName;
            // A stable session id lets a client that reconnects prove it's rejoining the same session.
            _sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
            _sharedSecret = sharedSecret ?? "";
        }

        public string SessionId { get { return _sessionId; } }
        public bool IsRunning { get { return _running; } }

        /// <summary>
        /// The currently connected (authenticated) client, or null. Lets the host push
        /// PROGRESS/JOB_DONE from encode worker threads; PeerConnection writes are lock-guarded.
        /// </summary>
        public PeerConnection CurrentClient { get; private set; }

        public void Start(int port)
        {
            if (_running) { return; }

            // Fail safe: never listen without a shared secret, which would accept any connection.
            if (string.IsNullOrEmpty(_sharedSecret))
            {
                throw new InvalidOperationException(
                    "Refusing to start the encoder server without a shared secret. Set one on the " +
                    "Advanced settings tab (the same value on both machines).");
            }

            _running = true;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Logger.Info("LanServer listening on port " + port + " (session " + _sessionId + ").");
            Logger.Info("SECURITY NOTE: this session is designed for LAN use only. The shared-secret " +
                        "handshake blocks unauthorized peers, but traffic is not encrypted — do NOT " +
                        "port-forward this port to the internet.");

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "LanServer-Accept" };
            _acceptThread.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (_running) { Logger.Error("LanServer accept failed.", ex); }
                    return;
                }

                // Serve this client until it disconnects, then loop to accept the next (e.g. after a
                // client reboot). Blocking here is fine — one client at a time is the design.
                try { ServeClient(client); }
                catch (Exception ex) { Logger.Error("LanServer client session ended with error.", ex); }
            }
        }

        private void ServeClient(TcpClient client)
        {
            client.NoDelay = true;
            using (client)
            using (var conn = new PeerConnection(client))
            {
                // Expect HELLO first.
                NetMessage hello = conn.Read();
                if (hello == null || hello.Type != MsgType.Hello)
                {
                    Logger.Error("LanServer: first message was not HELLO; dropping connection.");
                    return;
                }
                string clientName = hello.GetString("name");

                // Shared-secret gate BEFORE anything else: challenge with a random nonce and require
                // a valid HMAC proof. A scanner/unauthorized peer is dropped here, having learned
                // nothing and been able to do nothing.
                string nonce = NodeAuth.GenerateNonce();
                conn.Write(new NetMessage(MsgType.AuthChallenge).With("nonce", nonce));

                NetMessage authResp = conn.Read();
                string proof = authResp != null ? authResp.GetString("proof") : "";
                if (authResp == null || authResp.Type != MsgType.AuthResponse ||
                    !NodeAuth.VerifyProof(_sharedSecret, nonce, proof))
                {
                    Logger.Error("LanServer: auth FAILED for '" + clientName + "' — wrong/absent shared secret. Dropping.");
                    try { conn.Write(new NetMessage(MsgType.AuthFail)); } catch { /* peer may already be gone */ }
                    return;
                }

                conn.Write(new NetMessage(MsgType.HelloAck)
                    .With("server", _serverName)
                    .With("session", _sessionId));

                Logger.Info("LanServer: client '" + clientName + "' connected.");
                CurrentClient = conn;
                var connected = ClientConnected; if (connected != null) { connected(clientName); }

                try
                {
                    // Message pump until the peer closes.
                    NetMessage msg;
                    while ((msg = conn.Read()) != null)
                    {
                        if (msg.Type == MsgType.Heartbeat)
                        {
                            conn.Write(new NetMessage(MsgType.Heartbeat));
                            continue;
                        }
                        var handler = MessageReceived; if (handler != null) { handler(msg, conn); }
                    }
                }
                finally
                {
                    CurrentClient = null;
                }

                Logger.Info("LanServer: client '" + clientName + "' disconnected.");
                var disc = ClientDisconnected; if (disc != null) { disc(clientName); }
            }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_listener != null) { _listener.Stop(); } }
            catch { /* already stopped */ }
        }
    }
}
