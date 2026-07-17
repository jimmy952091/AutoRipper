using System;
using System.Collections.Generic;
using System.Threading;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>Progress/completion info for one remote job, as shown in the ripper's UI.</summary>
    public class RemoteJobStatus
    {
        public string ClientJobId { get; set; }
        public int Percent { get; set; }
        public string Status { get; set; }
        public string Operation { get; set; }
        public bool Done { get; set; }
        public bool Ok { get; set; }
        public string FinalPath { get; set; }
        public string Error { get; set; }

        public RemoteJobStatus()
        {
            ClientJobId = "";
            Status = "";
            Operation = "";
            FinalPath = "";
            Error = "";
        }
    }

    /// <summary>
    /// The ripper-client side of the distributed session. Owns a background session thread that:
    ///   - connects to the encoder server (and RECONNECTS with backoff if the link drops — the
    ///     "local PC rebooted / WiFi blipped" resilience),
    ///   - sends queued jobs one at a time: JOB_SUBMIT -> wait JOB_ACCEPTED -> stream the file,
    ///   - receives PROGRESS / JOB_DONE pushes and raises them as events for the remote panel.
    ///
    /// Jobs are held in an outbox until their file is fully transferred, so a job submitted while
    /// offline (or interrupted mid-transfer) is sent/resent after reconnect. Encodes finished
    /// while we were away are re-reported by the server on its next update push.
    /// </summary>
    public class RemoteEncodeClient : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _secret;
        private readonly string _clientName;

        // Outbox: jobs whose file hasn't been fully transferred yet. Head is in flight.
        private readonly Queue<PendingSubmit> _outbox = new Queue<PendingSubmit>();
        private readonly object _outboxLock = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private Thread _sessionThread;
        private volatile bool _running;
        private volatile bool _connected;
        private int _idleTicks;

        private class PendingSubmit
        {
            public RemoteEncodeRequest Request;
            public string FilePath;
        }

        /// <summary>Connection state changed (true = connected+authenticated). Background thread.</summary>
        public event Action<bool> ConnectionChanged;

        /// <summary>A PROGRESS or JOB_DONE update arrived for a job. Background thread.</summary>
        public event Action<RemoteJobStatus> JobStatusChanged;

        /// <summary>Upload progress for the file currently being transferred (jobId, sent, total).</summary>
        public event Action<string, long, long> UploadProgress;

        /// <summary>
        /// A human-readable connection notice worth showing in the status bar — e.g. "the server
        /// is at its ripper limit; waiting for a slot". Background thread.
        /// </summary>
        public event Action<string> Notice;

        public bool IsConnected { get { return _connected; } }

        public RemoteEncodeClient(string host, int port, string sharedSecret, string clientName = null)
        {
            _host = host;
            _port = port;
            _secret = sharedSecret ?? "";
            _clientName = string.IsNullOrWhiteSpace(clientName) ? Environment.MachineName : clientName;
        }

        public void Start()
        {
            if (_running) { return; }
            _running = true;
            _sessionThread = new Thread(SessionLoop) { IsBackground = true, Name = "RemoteEncode-Session" };
            _sessionThread.Start();
        }

        /// <summary>
        /// Queues one ripped file for remote encoding. Returns immediately; transfer happens on the
        /// session thread (and survives reconnects). Safe to call while offline.
        /// </summary>
        public void SubmitJob(RemoteEncodeRequest request, string filePath)
        {
            if (string.IsNullOrEmpty(request.ClientJobId))
            {
                request.ClientJobId = Guid.NewGuid().ToString("N").Substring(0, 12);
            }
            lock (_outboxLock)
            {
                _outbox.Enqueue(new PendingSubmit { Request = request, FilePath = filePath });
            }
            _wake.Set();
        }

        /// <summary>Number of jobs still waiting to be transferred to the server.</summary>
        public int PendingCount
        {
            get { lock (_outboxLock) { return _outbox.Count; } }
        }

        // ---------------- session thread ----------------

        private void SessionLoop()
        {
            int backoffMs = 1000;

            while (_running)
            {
                using (var client = new LanClient(_clientName, _secret))
                {
                    if (!client.Connect(_host, _port))
                    {
                        // Server unreachable — wait (with capped backoff) and try again. This is the
                        // reboot-resilience: we just keep knocking until the server is back. If the
                        // server gave a REASON (e.g. it's full), surface it so the user isn't staring
                        // at a bare "reconnecting…".
                        if (!string.IsNullOrEmpty(client.LastFailure))
                        {
                            var notice = Notice; if (notice != null) { notice(client.LastFailure); }
                        }
                        SetConnected(false);
                        if (WaitOrStop(backoffMs)) { return; }
                        backoffMs = Math.Min(backoffMs * 2, 15000);
                        continue;
                    }

                    backoffMs = 1000;
                    SetConnected(true);
                    Logger.Info("RemoteEncodeClient: session up with '" + client.ServerName + "'.");

                    try
                    {
                        PumpSession(client);
                    }
                    catch (Exception ex)
                    {
                        Logger.Info("RemoteEncodeClient: session dropped (" + ex.Message + "); will reconnect.");
                    }
                    SetConnected(false);
                }
            }
        }

        /// <summary>
        /// Services one live connection: alternates between sending the next outbox job and
        /// draining incoming pushes. Single thread does both, so writes never interleave with a
        /// file transfer. Returns when the connection dies (caller reconnects).
        /// </summary>
        private void PumpSession(LanClient client)
        {
            while (_running)
            {
                PendingSubmit next = null;
                lock (_outboxLock)
                {
                    if (_outbox.Count > 0) { next = _outbox.Peek(); }
                }

                if (next != null)
                {
                    SendOneJob(client, next);
                    // Only now that the file is fully transferred is the job the server's
                    // responsibility — remove it from the outbox.
                    lock (_outboxLock) { _outbox.Dequeue(); }
                    continue;
                }

                // Idle: check for pushes without committing to a blocking read. A read TIMEOUT
                // could fire mid-frame and desynchronize the message framing, so instead we only
                // call Receive when bytes are already waiting (DataAvailable), and otherwise nap
                // briefly and re-check the outbox. Once a frame has started arriving, Receive
                // blocks until it completes, keeping framing intact.
                var network = client.Connection.RawStream as System.Net.Sockets.NetworkStream;
                if (network != null && !network.DataAvailable)
                {
                    // Also lets us notice a dead link eventually: heartbeat every ~10s of idle.
                    if (++_idleTicks >= 40)
                    {
                        _idleTicks = 0;
                        client.Send(new NetMessage(MsgType.Heartbeat)); // echo consumed below
                    }
                    _wake.WaitOne(250);
                    continue;
                }
                _idleTicks = 0;

                NetMessage msg = client.Receive();
                if (msg == null) { throw new System.IO.IOException("Server closed the connection."); }
                if (msg.Type == MsgType.Heartbeat) { continue; } // our echo — link is alive
                HandlePush(msg);
            }
        }

        private void SendOneJob(LanClient client, PendingSubmit pending)
        {
            string jobId = pending.Request.ClientJobId;
            Logger.Info("RemoteEncodeClient: submitting job " + jobId + " (" + pending.FilePath + ").");

            client.Send(RemoteJobProtocol.BuildJobSubmit(pending.Request));

            // Wait for JOB_ACCEPTED (the server may interleave PROGRESS pushes for earlier jobs —
            // handle those while waiting).
            while (true)
            {
                NetMessage msg = ReceiveKeepingLinkAlive(client);
                if (msg == null) { throw new System.IO.IOException("Connection lost awaiting JOB_ACCEPTED."); }
                if (msg.Type == MsgType.Heartbeat) { continue; } // our liveness echo
                if (msg.Type == MsgType.JobAccepted) { break; }
                if (msg.Type == MsgType.JobDone && msg.GetString("jobId") == jobId)
                {
                    // Rejected outright (e.g. unconfirmed metadata). Report and drop.
                    HandlePush(msg);
                    return;
                }
                HandlePush(msg);
            }

            // MULTI-RIPPER ETIQUETTE (protocol 2+): ask for the transfer slot before streaming.
            // If another ripper is mid-transfer the server parks us in a FIFO line and tells us our
            // position, which we surface in the UI — much clearer than a progress bar frozen at 0%.
            // An older (protocol 1) server doesn't know SEND_REQUEST, so we skip straight to sending.
            if (client.ServerProtocol >= 2)
            {
                client.Send(new NetMessage(MsgType.SendRequest).With("jobId", jobId));
                bool granted = false;
                while (!granted)
                {
                    NetMessage msg = ReceiveKeepingLinkAlive(client);
                    if (msg == null) { throw new System.IO.IOException("Connection lost awaiting the transfer slot."); }
                    if (msg.Type == MsgType.SendGrant) { granted = true; }
                    else if (msg.Type == MsgType.SendWait && msg.GetString("jobId") == jobId)
                    {
                        int position = msg.GetInt("position");
                        Logger.Info("RemoteEncodeClient: waiting to send " + jobId +
                                    " (position " + position + " — another ripper is transferring).");
                        Raise(new RemoteJobStatus
                        {
                            ClientJobId = jobId,
                            Status = "Waiting to send",
                            Operation = "Another ripper is sending — position " + position + " in line"
                        });
                    }
                    else if (msg.Type == MsgType.Heartbeat) { /* our idle echo arriving late */ }
                    else { HandlePush(msg); } // PROGRESS/JOB_DONE for earlier jobs keep flowing while we wait
                }
                Raise(new RemoteJobStatus { ClientJobId = jobId, Status = "Sending", Operation = "Transferring file to the encoder server" });
            }

            // Stream the file. If this throws (link died mid-transfer), the job stays at the head
            // of the outbox and is re-sent whole on reconnect; the server's SHA-256 check makes a
            // truncated first attempt harmless.
            var up = UploadProgress;
            FileTransfer.SendFile(client.Connection, pending.FilePath, pending.Request.SourceFileName,
                (sent, total) => { if (up != null) { up(jobId, sent, total); } });
        }

        /// <summary>
        /// Receives the next message, but while NOTHING is arriving, sends a heartbeat every
        /// ~10 s so a dead link is DETECTED instead of waited on forever. A wait for the
        /// transfer slot can legitimately last as long as another ripper's whole upload; without
        /// this, a WiFi blip during that wait (or awaiting JOB_ACCEPTED) left the session thread
        /// blocked on a read that would never return. The heartbeat WRITE is what fails on a
        /// dead connection (LanClient sets a send timeout), which throws us out to the
        /// reconnect logic. Only reads when a frame has started arriving, so framing stays intact.
        /// </summary>
        private NetMessage ReceiveKeepingLinkAlive(LanClient client)
        {
            var network = client.Connection.RawStream as System.Net.Sockets.NetworkStream;
            int idleMs = 0;
            while (network != null && !network.DataAvailable)
            {
                if (!_running) { throw new System.IO.IOException("Client is shutting down."); }
                Thread.Sleep(250);
                idleMs += 250;
                if (idleMs >= 10000)
                {
                    idleMs = 0;
                    client.Send(new NetMessage(MsgType.Heartbeat));
                }
            }
            return client.Receive();
        }

        private void HandlePush(NetMessage msg)
        {
            if (msg.Type == MsgType.Progress)
            {
                Raise(new RemoteJobStatus
                {
                    ClientJobId = msg.GetString("jobId"),
                    Percent = msg.GetInt("percent"),
                    Status = msg.GetString("status"),
                    Operation = msg.GetString("operation")
                });
            }
            else if (msg.Type == MsgType.JobDone)
            {
                bool ok = msg.Data["ok"] != null && (bool)msg.Data["ok"];
                Raise(new RemoteJobStatus
                {
                    ClientJobId = msg.GetString("jobId"),
                    Percent = ok ? 100 : 0,
                    Done = true,
                    Ok = ok,
                    Status = ok ? "Completed" : "Failed",
                    FinalPath = msg.GetString("finalPath"),
                    Error = msg.GetString("error")
                });
            }
            else
            {
                Logger.Info("RemoteEncodeClient: ignoring push '" + msg.Type + "'.");
            }
        }

        private void Raise(RemoteJobStatus status)
        {
            var handler = JobStatusChanged;
            if (handler != null) { handler(status); }
        }

        private void SetConnected(bool value)
        {
            if (_connected == value) { return; }
            _connected = value;
            var handler = ConnectionChanged;
            if (handler != null) { handler(value); }
        }

        /// <summary>Waits up to <paramref name="ms"/>, returning true if we should stop running.</summary>
        private bool WaitOrStop(int ms)
        {
            _wake.WaitOne(ms);
            return !_running;
        }

        public void Dispose()
        {
            _running = false;
            _wake.Set();
            // The session thread is a background thread reading with timeouts; give it a moment.
            try { if (_sessionThread != null) { _sessionThread.Join(1500); } }
            catch { /* shutting down */ }
        }
    }
}
