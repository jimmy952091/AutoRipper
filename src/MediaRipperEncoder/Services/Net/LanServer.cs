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

            /// <summary>UTC ticks of the last message/file bytes received from this client —
            /// the liveness signal that separates a dead session from a healthy one.</summary>
            public long LastActivityTicks;
        }

        /// <summary>
        /// How recently a session must have spoken to count as ALIVE. Healthy clients heartbeat
        /// every ~10 s and mark activity continuously during transfers, so 30 s of silence means
        /// the connection died (e.g. a modem/router reboot killed it without a FIN).
        /// </summary>
        public const int LivenessWindowMs = 30000;

        // Live authenticated clients. Guarded by _clientsLock; iterate on a snapshot.
        private readonly List<ClientSession> _clients = new List<ClientSession>();
        private readonly object _clientsLock = new object();

        /// <summary>Raised (on that connection's thread) for every non-handshake message from a client.</summary>
        public event Action<NetMessage, PeerConnection> MessageReceived;

        /// <summary>Raised when a client completes the handshake / disconnects: (client name, its connection).</summary>
        public event Action<string, PeerConnection> ClientConnected;
        public event Action<string, PeerConnection> ClientDisconnected;

        /// <summary>A connection-security event the server-node user should SEE (dead session
        /// replaced after an outage; a same-name connection refused because the original is
        /// still alive — possible name spoof). Raised on a connection thread.</summary>
        public event Action<string> Notice;

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
        /// Marks a client as alive right now. The message pump does this automatically, but
        /// during a FILE TRANSFER the pump thread is inside the raw byte read — the host calls
        /// this from the transfer's progress callback so an uploading ripper never looks
        /// "silent" (and can't be displaced by a same-name connection mid-upload).
        /// </summary>
        public void MarkActivity(PeerConnection conn)
        {
            lock (_clientsLock)
            {
                foreach (ClientSession s in _clients)
                {
                    if (ReferenceEquals(s.Conn, conn))
                    {
                        System.Threading.Interlocked.Exchange(ref s.LastActivityTicks, DateTime.UtcNow.Ticks);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// The live connection for a client name, or null if that client isn't connected right now.
        /// Job ownership and the transfer line are keyed by NAME, so a client that reboots and
        /// reconnects picks its progress pushes and its place in line back up on the new connection.
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
                    "connection dialog (the same value on every machine).");
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

        // A healthy client heartbeats every ~10 s when idle and streams bytes when busy, so a long
        // TOTAL silence means the connection is dead (e.g. a router reboot killed it without a
        // FIN — TCP alone would wait forever). The read then throws, the session ends, and the
        // seat is reclaimed.
        //
        // These windows are DELIBERATELY GENEROUS. While one ripper streams a multi-gigabyte file
        // over WiFi it can saturate the link, and control traffic to the OTHER connections is
        // delayed far longer than idle chatter ever would be. A tight limit aborted those healthy
        // idle sessions ("connection aborted by the software in your host machine"), which churned
        // the whole fleet. Being slow to reap a truly dead session costs nothing now that the
        // transfer line is keyed by machine name — a reconnecting ripper reclaims its own seat and
        // its place in line, so a lingering ghost can no longer block or displace anybody.
        public const int ClientSilenceTimeoutMs = 300000;
        private const int ClientSendTimeoutMs = 120000;

        private void ServeClient(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = ClientSilenceTimeoutMs;
            client.SendTimeout = ClientSendTimeoutMs;
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

                // LIVENESS-CHECKED GHOST REPLACEMENT: a ripper reconnecting after a network
                // outage may still have its OLD session here — that TCP connection died without
                // a FIN, so its reads just sit waiting, and the ghost occupies a seat. But a
                // same-name connection can also be an attacker (who somehow got the shared
                // secret) or a misnamed second machine trying to KICK a healthy ripper off. The
                // old session's own recent activity separates the two cases: healthy clients
                // heartbeat every ~10 s, so a session silent for 30+ s is a corpse — replace it;
                // a session that spoke recently is ALIVE — refuse the newcomer and tell the user.
                ClientSession ghost = null;
                bool refusedAsAlive = false;
                lock (_clientsLock)
                {
                    foreach (ClientSession s in _clients)
                    {
                        if (string.Equals(s.Name, clientName, StringComparison.OrdinalIgnoreCase))
                        {
                            long silentMs = (DateTime.UtcNow.Ticks -
                                System.Threading.Interlocked.Read(ref s.LastActivityTicks)) / TimeSpan.TicksPerMillisecond;
                            if (silentMs < LivenessWindowMs)
                            {
                                refusedAsAlive = true;  // original is healthy — refuse the newcomer
                            }
                            else
                            {
                                ghost = s;              // original is dead — reclaim its seat
                                _clients.Remove(s);
                            }
                            break;
                        }
                    }
                }

                if (refusedAsAlive)
                {
                    string warning = "Refused a connection claiming to be '" + clientName + "' — that " +
                        "ripper is ALREADY connected and actively talking. If that machine did not just " +
                        "try to reconnect, another device on your network may be using its name.";
                    Logger.Error("LanServer: " + warning);
                    var alert = Notice; if (alert != null) { alert(warning); }
                    try
                    {
                        conn.Write(new NetMessage(MsgType.ServerFull)
                            .With("max", _maxClients)
                            .With("server", _serverName)
                            .With("reason", "name-active"));
                    }
                    catch { /* peer may already be gone */ }
                    return;
                }

                if (ghost != null)
                {
                    string info = "Ripper '" + clientName + "' reconnected after an outage — its dead " +
                                  "previous session was replaced.";
                    Logger.Info("LanServer: " + info);
                    var notice = Notice; if (notice != null) { notice(info); }
                    // Disposing wakes the ghost's serving thread out of its blocked read; that
                    // thread then runs its normal disconnect cleanup (gate slot, pending queues).
                    try { ghost.Conn.Dispose(); } catch { /* already dead — that's the theory */ }
                }

                // Capacity gate AFTER auth, so only a holder of the shared secret can even learn
                // the server is full. Registering inside the lock makes check+add atomic — two
                // clients racing for the last seat can't both win it.
                var session = new ClientSession
                {
                    Name = clientName,
                    Conn = conn,
                    LastActivityTicks = DateTime.UtcNow.Ticks
                };
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
                        // Anything received proves the client is alive (heartbeats included).
                        System.Threading.Interlocked.Exchange(ref session.LastActivityTicks, DateTime.UtcNow.Ticks);
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
