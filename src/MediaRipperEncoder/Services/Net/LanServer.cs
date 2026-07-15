using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// The encoder-server side of the LAN session. Listens for ripper clients (up to a configurable
    /// maximum), completes the HELLO + shared-secret handshake with each, and then hands every
    /// received <see cref="NetMessage"/> to a callback along with the connection it came from.
    ///
    /// Each authenticated client is served on its own thread, so several rippers can hold live
    /// sessions at once; a client arriving beyond the limit is told SERVER_FULL (after passing
    /// auth) and dropped, and its reconnect backoff retries until a seat frees up. Serializing the
    /// actual FILE TRANSFERS across clients is the host's job (see <see cref="TransferGate"/>) —
    /// this class is just the transport: lifecycle + handshake + per-connection message pump.
    /// LAN-only by design.
    /// </summary>
    public class LanServer : IDisposable
    {
        private readonly string _serverName;
        private readonly string _sessionId;
        private readonly string _sharedSecret;
        private readonly int _maxClients;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private class ClientSession
        {
            public string Name;
            public PeerConnection Conn;
        }

        // Live authenticated clients. Guarded by _clientsLock; iterate on a snapshot.
        private readonly List<ClientSession> _clients = new List<ClientSession>();
        private readonly object _clientsLock = new object();

        /// <summary>Raised (on that connection's thread) for every non-handshake message from a client.</summary>
        public event Action<NetMessage, PeerConnection> MessageReceived;

        /// <summary>Raised when a client completes the handshake / disconnects: (client name, its connection).</summary>
        public event Action<string, PeerConnection> ClientConnected;
        public event Action<string, PeerConnection> ClientDisconnected;

        public LanServer(string serverName, string sessionId, string sharedSecret, int maxClients = 1)
        {
            _serverName = string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName;
            // A stable session id lets a client that reconnects prove it's rejoining the same session.
            _sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
            _sharedSecret = sharedSecret ?? "";
            _maxClients = Math.Max(1, maxClients);
        }

        public string SessionId { get { return _sessionId; } }
        public bool IsRunning { get { return _running; } }
        public int MaxClients { get { return _maxClients; } }

        public int ConnectedCount
        {
            get { lock (_clientsLock) { return _clients.Count; } }
        }

        /// <summary>Names of the currently connected clients, in connect order (for the server UI).</summary>
        public string[] ConnectedNames
        {
            get
            {
                lock (_clientsLock)
                {
                    var names = new string[_clients.Count];
                    for (int i = 0; i < _clients.Count; i++) { names[i] = _clients[i].Name; }
                    return names;
                }
            }
        }

        /// <summary>
        /// The live connection for a client name, or null if that client isn't connected right now.
        /// Job ownership is tracked by NAME so a client that reboots and reconnects picks its
        /// progress pushes back up on the new connection.
        /// </summary>
        public PeerConnection FindClientByName(string name)
        {
            lock (_clientsLock)
            {
                foreach (ClientSession s in _clients)
                {
                    if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) { return s.Conn; }
                }
            }
            return null;
        }

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
            Logger.Info("LanServer listening on port " + port + " (session " + _sessionId +
                        ", up to " + _maxClients + " client(s)).");
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

                // Each client gets its own thread so one ripper's session (or its blocked file
                // transfer) never stops another ripper from connecting or being serviced.
                var worker = new Thread(() =>
                {
                    try { ServeClient(client); }
                    catch (Exception ex) { Logger.Error("LanServer client session ended with error.", ex); }
                })
                { IsBackground = true, Name = "LanServer-Client" };
                worker.Start();
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

                // Capacity gate AFTER auth, so only a holder of the shared secret can even learn
                // the server is full. Registering inside the lock makes check+add atomic — two
                // clients racing for the last seat can't both win it.
                var session = new ClientSession { Name = clientName, Conn = conn };
                bool full;
                lock (_clientsLock)
                {
                    full = _clients.Count >= _maxClients;
                    if (!full) { _clients.Add(session); }
                }
                if (full)
                {
                    Logger.Info("LanServer: '" + clientName + "' turned away — client limit (" +
                                _maxClients + ") reached. It will retry automatically.");
                    try
                    {
                        conn.Write(new NetMessage(MsgType.ServerFull)
                            .With("max", _maxClients)
                            .With("server", _serverName));
                    }
                    catch { /* peer may already be gone */ }
                    return;
                }

                try
                {
                    conn.Write(new NetMessage(MsgType.HelloAck)
                        .With("server", _serverName)
                        .With("session", _sessionId)
                        .With("proto", 2)); // proto 2 = this server speaks SEND_REQUEST/SEND_GRANT

                    Logger.Info("LanServer: client '" + clientName + "' connected (" +
                                ConnectedCount + "/" + _maxClients + ").");
                    var connected = ClientConnected; if (connected != null) { connected(clientName, conn); }

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
                    lock (_clientsLock) { _clients.Remove(session); }
                }

                Logger.Info("LanServer: client '" + clientName + "' disconnected (" +
                            ConnectedCount + "/" + _maxClients + ").");
                var disc = ClientDisconnected; if (disc != null) { disc(clientName, conn); }
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
