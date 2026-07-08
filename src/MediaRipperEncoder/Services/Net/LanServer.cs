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
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        /// <summary>Raised (on the connection thread) for every non-handshake message from the client.</summary>
        public event Action<NetMessage, PeerConnection> MessageReceived;

        /// <summary>Raised when a client completes the handshake, and again when it disconnects.</summary>
        public event Action<string> ClientConnected;
        public event Action<string> ClientDisconnected;

        public LanServer(string serverName, string sessionId)
        {
            _serverName = string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName;
            // A stable session id lets a client that reconnects prove it's rejoining the same session.
            _sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        }

        public string SessionId { get { return _sessionId; } }
        public bool IsRunning { get { return _running; } }

        public void Start(int port)
        {
            if (_running) { return; }
            _running = true;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Logger.Info("LanServer listening on port " + port + " (session " + _sessionId + ").");

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
                // Expect HELLO first; reply HELLO_ACK with our identity + the session id.
                NetMessage hello = conn.Read();
                if (hello == null || hello.Type != MsgType.Hello)
                {
                    Logger.Error("LanServer: first message was not HELLO; dropping connection.");
                    return;
                }

                string clientName = hello.GetString("name");
                conn.Write(new NetMessage(MsgType.HelloAck)
                    .With("server", _serverName)
                    .With("session", _sessionId));

                Logger.Info("LanServer: client '" + clientName + "' connected.");
                var connected = ClientConnected; if (connected != null) { connected(clientName); }

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
