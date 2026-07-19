using System;
using System.Collections.Generic;
using System.IO;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// The encoder-server node's brain: ties the authenticated <see cref="LanServer"/> to the real
    /// encode pipeline. Flow per job:
    ///
    ///   JOB_SUBMIT (metadata package)  -> validate, remember as pending, reply JOB_ACCEPTED
    ///   SEND_REQUEST                   -> transfer slot free? SEND_GRANT : SEND_WAIT (position in line)
    ///   FILE_BEGIN + raw bytes         -> receive into a CONTAINED staging path, verify SHA-256
    ///   plan via the SHARED EncodeJobPlanner (server's own roots/presets, same naming as local)
    ///   enqueue on the real EncodeQueue -> PROGRESS pushed to the owning client as it encodes
    ///   finished -> EncodeFinisher (tag + overwrite-safe placement) -> JOB_DONE to the owning client
    ///
    /// MULTI-CLIENT: several ripper clients can hold sessions at once (up to the configured limit),
    /// each with its own pending-job queue, but a <see cref="TransferGate"/> serializes the actual
    /// file transfers — exactly one ripper streams at a time and the rest wait FIFO, so the server's
    /// network/disk sees one full-speed transfer instead of several crawling ones. Encode jobs are
    /// queued in the order their files finish arriving (the same strict-FIFO rule as local encodes).
    /// Job ownership is tracked by CLIENT NAME, so a ripper that reboots and reconnects picks its
    /// progress updates back up.
    ///
    /// Security posture: the client's metadata package decides only NAMES within the server's own
    /// library roots (via the planner + sanitizer); client-supplied file names are flattened to a
    /// bare name inside the staging folder (<see cref="SafeStagingPath"/>), so no remote input can
    /// path-traverse outside the folders this server already owns. Combined with the shared-secret
    /// handshake in LanServer, a random scanner can't reach any of this code at all.
    /// </summary>
    public class EncodeServerHost : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly LanServer _server;
        private readonly HandBrakeService _handBrake;
        private readonly EncodeQueue _encodeQueue;
        private readonly EncodeJobPlanner _planner;
        private readonly string _stagingRoot;
        private readonly TransferGate _gate = new TransferGate();

        /// <summary>Read-stall tolerance while receiving a file (user-requested: slow systems).</summary>
        private const int BulkReceiveTimeoutMs = 1800000;

        // Per-connection FIFO of jobs whose JOB_SUBMIT arrived but whose file hasn't. Each client's
        // own protocol is serial (submit -> send -> submit -> send), so matching its next FILE_BEGIN
        // to the head of ITS queue is exact — and one client's jobs can never pair with another's file.
        private readonly Dictionary<PeerConnection, Queue<RemoteEncodeRequest>> _awaitingByConn =
            new Dictionary<PeerConnection, Queue<RemoteEncodeRequest>>();
        private readonly object _stateLock = new object();

        private class JobOwner
        {
            public string ClientJobId;
            public string ClientName;
        }

        // EncodeJob.Id -> which client's job it is (by NAME, so reconnects re-attach).
        private readonly Dictionary<Guid, JobOwner> _jobOwners = new Dictionary<Guid, JobOwner>();

        // Connection -> client name, for routing and for cleanup on disconnect.
        private readonly Dictionary<PeerConnection, string> _connNames =
            new Dictionary<PeerConnection, string>();

        /// <summary>Mirror of job updates for the server node's own UI (name, percent, operation).</summary>
        public event Action<EncodeJob> JobUpdated;

        /// <summary>Connected-ripper roster changed: (count, max, names) — for the server node's UI.</summary>
        public event Action<int, int, string[]> ClientsChanged;

        /// <summary>Connection-security notice the server-node user should see in the UI (dead
        /// session replaced after an outage; a same-name connection refused as a possible spoof).</summary>
        public event Action<string> ServerNotice;

        public EncodeServerHost(AppSettings settings)
        {
            _settings = settings;

            _handBrake = new HandBrakeService(settings.HandBrakeCliPath, settings.HandBrakePresetPath);
            _encodeQueue = new EncodeQueue(_handBrake, "server");
            _planner = EncodeJobPlanner.FromSettings(settings);

            // All received files land under here and ONLY here.
            _stagingRoot = Path.Combine(string.IsNullOrEmpty(settings.TempFolder)
                ? Path.GetTempPath() : settings.TempFolder, "remote_inbox");

            _server = new LanServer(Environment.MachineName, null, settings.NodeSharedSecret,
                Math.Max(1, settings.NodeMaxClients));
            _server.MessageReceived += OnMessage;
            _server.ClientConnected += OnClientConnected;
            _server.ClientDisconnected += OnClientDisconnected;
            _server.Notice += text => { var h = ServerNotice; if (h != null) { h(text); } };

            _encodeQueue.JobUpdated += OnQueueJobUpdated;
            _encodeQueue.JobEncodedSuccessfully += OnEncodeDone;
        }

        public void Start()
        {
            Directory.CreateDirectory(_stagingRoot);

            // Re-queue anything this server was still encoding when it last closed. The received
            // files are already in the staging inbox, so nothing has to be re-ripped or re-sent.
            int resumed = _encodeQueue.ResumePersisted();
            if (resumed > 0)
            {
                Logger.Info("EncodeServerHost: resumed " + resumed + " unfinished encode(s) from the previous session.");
            }

            _server.Start(_settings.NodePort);
            Logger.Info("EncodeServerHost: ready to receive encode jobs from up to " +
                        _server.MaxClients + " ripper(s) (staging: " + _stagingRoot + ").");
        }

        // ---------------- client lifecycle ----------------

        private void OnClientConnected(string name, PeerConnection conn)
        {
            lock (_stateLock)
            {
                _awaitingByConn[conn] = new Queue<RemoteEncodeRequest>();
                _connNames[conn] = name;
            }
            RaiseClientsChanged();
        }

        private void OnClientDisconnected(string name, PeerConnection conn)
        {
            int dropped;
            lock (_stateLock)
            {
                Queue<RemoteEncodeRequest> pending;
                dropped = _awaitingByConn.TryGetValue(conn, out pending) ? pending.Count : 0;
                _awaitingByConn.Remove(conn);
                _connNames.Remove(conn);
            }
            if (dropped > 0)
            {
                // Its files never arrived; the client's outbox still holds those jobs and will
                // resubmit them whole after it reconnects, so nothing is lost — just noted.
                Logger.Info("EncodeServerHost: '" + name + "' disconnected with " + dropped +
                            " submitted job(s) awaiting files; it will resubmit on reconnect.");
            }
            Logger.Info("EncodeServerHost: ripper '" + name + "' disconnected; queued/running encodes continue.");

            // Surrender its place in the transfer line ONLY if the machine is really gone. If it
            // has already reconnected under the same name (the common case during a flaky link),
            // that new session inherits the place — otherwise the reconnect would go to the back
            // of the line while this stale cleanup fired, which is how phantom tickets used to
            // pile up and push everyone's position UP.
            if (_server.FindClientByName(name) == null)
            {
                DeliverGateNotices(_gate.RemoveOwner(name));
            }
            else
            {
                Logger.Info("EncodeServerHost: '" + name + "' already reconnected; it keeps its place in the transfer line.");
            }
            RaiseClientsChanged();
        }

        private void RaiseClientsChanged()
        {
            var handler = ClientsChanged;
            if (handler != null)
            {
                handler(_server.ConnectedCount, _server.MaxClients, _server.ConnectedNames);
            }
        }

        // ---------------- incoming messages ----------------

        private void OnMessage(NetMessage msg, PeerConnection conn)
        {
            try
            {
                if (msg.Type == MsgType.JobSubmit) { HandleJobSubmit(msg, conn); }
                else if (msg.Type == MsgType.SendRequest) { HandleSendRequest(msg, conn); }
                else if (msg.Type == MsgType.FileBegin) { HandleFileBegin(msg, conn); }
                else { Logger.Info("EncodeServerHost: ignoring unknown message type '" + msg.Type + "'."); }
            }
            catch (Exception ex)
            {
                // One bad message must not kill the whole session thread.
                Logger.Error("EncodeServerHost: error handling " + msg.Type + ".", ex);
            }
        }

        private void HandleJobSubmit(NetMessage msg, PeerConnection conn)
        {
            RemoteEncodeRequest request = RemoteJobProtocol.ParseJobSubmit(msg);

            // Trust nothing implicitly: a job with no confirmed metadata is refused, same as the
            // local pipeline refuses to process without a confirmed match.
            if (request.Metadata == null || !request.Metadata.MatchConfirmed)
            {
                Logger.Error("EncodeServerHost: rejected job (metadata missing or unconfirmed).");
                conn.Write(new NetMessage(MsgType.JobDone)
                    .With("jobId", request.ClientJobId ?? "")
                    .With("ok", false)
                    .With("error", "Job rejected: metadata package missing or not confirmed."));
                return;
            }

            // Backstop for the "Untitled Movie" incident: a confirmed package with a BLANK name
            // would encode for hours and then land under a meaningless folder. Refuse it now,
            // loudly, on the RIPPER's screen — where the person who can fix it is sitting.
            string nameProblem = DescribeMetadataNameProblem(request.Metadata);
            if (nameProblem != null)
            {
                Logger.Error("EncodeServerHost: rejected job " + request.ClientJobId + " — " + nameProblem);
                conn.Write(new NetMessage(MsgType.JobDone)
                    .With("jobId", request.ClientJobId ?? "")
                    .With("ok", false)
                    .With("error", "Job rejected: " + nameProblem));
                return;
            }

            lock (_stateLock)
            {
                Queue<RemoteEncodeRequest> pending;
                if (!_awaitingByConn.TryGetValue(conn, out pending))
                {
                    // Shouldn't happen (connected event registers the queue), but never lose a job to it.
                    pending = new Queue<RemoteEncodeRequest>();
                    _awaitingByConn[conn] = pending;
                }
                pending.Enqueue(request);
            }
            conn.Write(new NetMessage(MsgType.JobAccepted).With("jobId", request.ClientJobId ?? ""));
            // Log WHAT the package says it is — when a library file comes out misnamed, this line
            // proves whether the name was already wrong on arrival or got lost later (pairing).
            Logger.Info("EncodeServerHost: accepted job " + request.ClientJobId +
                        " ('" + request.SourceFileName + "' from '" + NameOf(conn) + "', " +
                        DescribePackage(request.Metadata) + ", awaiting file).");
        }

        /// <summary>
        /// A ripper wants to stream its next file. Grant the transfer slot if free, otherwise tell
        /// it its place in line — its UI shows "waiting to send" instead of a stalled progress bar.
        /// </summary>
        private void HandleSendRequest(NetMessage msg, PeerConnection conn)
        {
            string jobId = msg.GetString("jobId");
            TransferGate.Notice notice = _gate.Request(NameOf(conn), jobId);
            DeliverGateNotices(new List<TransferGate.Notice> { notice });
            if (!notice.Granted)
            {
                Logger.Info("EncodeServerHost: '" + NameOf(conn) + "' queued to send (position " +
                            notice.Position + ") — another ripper is transferring.");
            }
        }

        private void HandleFileBegin(NetMessage msg, PeerConnection conn)
        {
            RemoteEncodeRequest request = null;
            lock (_stateLock)
            {
                Queue<RemoteEncodeRequest> pending;
                if (_awaitingByConn.TryGetValue(conn, out pending) && pending.Count > 0)
                {
                    request = pending.Dequeue();
                }
            }

            // COMPATIBILITY: an older client streams FILE_BEGIN without SEND_REQUEST. Park its
            // connection thread until the slot frees — TCP backpressure stalls the sender, so the
            // one-transfer-at-a-time rule holds for old clients too (just without the nice status).
            if (!_gate.IsHolder(NameOf(conn)))
            {
                Logger.Info("EncodeServerHost: FILE_BEGIN from '" + NameOf(conn) +
                            "' without a granted slot (older client?); serializing via backpressure.");
                _gate.WaitUntilHolder(NameOf(conn), request != null ? request.ClientJobId : "");
            }

            try
            {
                if (request == null)
                {
                    Logger.Error("EncodeServerHost: FILE_BEGIN with no pending job; draining and ignoring.");
                    // Must still consume the announced bytes or the framing desynchronizes.
                    FileTransfer.ReceiveFile(conn, msg, SafeStagingPath(_stagingRoot, "orphan_" + Guid.NewGuid().ToString("N")));
                    return;
                }

                // CONTAINMENT: the client's name is flattened to a bare file name inside our staging
                // folder. A malicious "..\..\evil" can't escape it.
                string dest = SafeStagingPath(_stagingRoot,
                    Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + (msg.GetString("name") ?? ""));

                // The progress callback marks the uploader ALIVE throughout the transfer — its
                // message pump is busy with raw bytes, so without this a long upload would look
                // like silence and the client could be displaced by a same-name connection.
                // 30-minute stall tolerance for the bulk receive (slow WiFi rippers), then back
                // to the tight idle limit for heartbeat-covered control traffic.
                conn.SetReceiveTimeout(BulkReceiveTimeoutMs);
                bool ok;
                try
                {
                    ok = FileTransfer.ReceiveFile(conn, msg, dest,
                        (done, total) => _server.MarkActivity(conn));
                }
                finally
                {
                    conn.SetReceiveTimeout(LanServer.ClientSilenceTimeoutMs);
                }
                if (!ok)
                {
                    conn.Write(new NetMessage(MsgType.JobDone)
                        .With("jobId", request.ClientJobId ?? "")
                        .With("ok", false)
                        .With("error", "File transfer failed integrity check; please resend."));
                    return;
                }

                // Plan with the SHARED planner: identical naming/placement to a local encode, but using
                // THIS machine's library roots and preset files.
                EncodeJob job = _planner.BuildEncodeJob(request.Metadata, dest, request.TitleIndex);
                if (job == null)
                {
                    Logger.Error("EncodeServerHost: job " + request.ClientJobId + " had no usable title mapping; refusing.");
                    conn.Write(new NetMessage(MsgType.JobDone)
                        .With("jobId", request.ClientJobId ?? "")
                        .With("ok", false)
                        .With("error", "No episode/feature mapping for this title; nothing to encode."));
                    TryDelete(dest);
                    return;
                }

                lock (_stateLock)
                {
                    _jobOwners[job.Id] = new JobOwner
                    {
                        ClientJobId = request.ClientJobId ?? "",
                        ClientName = NameOf(conn)
                    };
                }
                // The other half of the misnamed-file receipt: what this job will be placed AS.
                Logger.Info("EncodeServerHost: job " + request.ClientJobId + " planned -> " + job.FinalTargetPath);
                _encodeQueue.Enqueue(job);
            }
            finally
            {
                // Success or failure, this transfer is over — pass the slot to the next ripper in line.
                DeliverGateNotices(_gate.Release(NameOf(conn)));
            }
        }

        /// <summary>Pushes gate decisions (go-ahead / new position) to the clients they belong to.</summary>
        private void DeliverGateNotices(List<TransferGate.Notice> notices)
        {
            var pending = new Queue<TransferGate.Notice>(notices);
            while (pending.Count > 0)
            {
                TransferGate.Notice n = pending.Dequeue();
                // Resolve the machine name to its CURRENT live connection — a notice may be
                // delivered after that ripper reconnected on a fresh socket.
                PeerConnection target = _server.FindClientByName(n.Owner);
                if (target == null)
                {
                    if (n.Granted)
                    {
                        Logger.Info("EncodeServerHost: '" + n.Owner + "' holds the transfer slot but is " +
                                    "offline right now; it keeps its turn until it reconnects or is reaped.");
                    }
                    continue;
                }
                try
                {
                    target.Write(n.Granted
                        ? new NetMessage(MsgType.SendGrant).With("jobId", n.JobId ?? "")
                        : new NetMessage(MsgType.SendWait).With("jobId", n.JobId ?? "").With("position", n.Position));
                }
                catch (Exception ex)
                {
                    Logger.Info("EncodeServerHost: gate notice push failed (" + ex.Message + ").");
                    if (n.Granted)
                    {
                        // An undeliverable GRANT means the slot went to a dead connection — the
                        // whole line would sit frozen until disconnect detection catches up.
                        // Evict that owner NOW and pass the slot on immediately.
                        Logger.Info("EncodeServerHost: granted client unreachable — passing the transfer slot on.");
                        foreach (TransferGate.Notice next in _gate.RemoveOwner(n.Owner))
                        {
                            pending.Enqueue(next);
                        }
                    }
                }
            }
        }

        private string NameOf(PeerConnection conn)
        {
            lock (_stateLock)
            {
                string name;
                return _connNames.TryGetValue(conn, out name) ? name : "(unknown)";
            }
        }

        // ---------------- queue events -> push to owning client ----------------

        private void OnQueueJobUpdated(EncodeJob job)
        {
            var mirror = JobUpdated; if (mirror != null) { mirror(job); }

            JobOwner owner = OwnerFor(job.Id);
            if (owner == null) { return; } // not a remote job

            PeerConnection client = _server.FindClientByName(owner.ClientName);
            if (client == null) { return; } // that ripper is offline; encodes continue, updates resume on rejoin

            try
            {
                client.Write(new NetMessage(MsgType.Progress)
                    .With("jobId", owner.ClientJobId)
                    .With("percent", job.ProgressPercent)
                    .With("status", job.Status.ToString())
                    .With("operation", job.CurrentOperation ?? ""));
            }
            catch (Exception ex)
            {
                // A push failing (client mid-disconnect) must never disturb the encode.
                Logger.Info("EncodeServerHost: progress push failed (" + ex.Message + "); client likely disconnecting.");
            }
        }

        private void OnEncodeDone(EncodeJob job)
        {
            PlacementResult result = null;
            bool ok = false;
            string error = "";
            try
            {
                // Same tag-and-place as local; null resolver = KeepBoth (never silently overwrite),
                // correct for a possibly-headless server node.
                result = EncodeFinisher.FinishAndPlace(job, null, _settings.TempFolder);
                ok = result.Outcome != PlacementOutcome.Failed;
                if (!ok)
                {
                    // Tell the RIPPER's user why and where — they may never look at the server.
                    error = "Encoded, but library placement failed (" +
                            (result.Error != null ? result.Error.Message : "see server log") +
                            "). The finished file is safe on the server at: " + job.OutputFile;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("EncodeServerHost: finish/placement threw for " + job.ShortId + ".", ex);
                error = "Encoded, but tagging/placement threw: " + ex.Message;
            }

            // Refresh the server's own list: EncodeFinisher has just rewritten CurrentOperation
            // to the real library destination ("Placed -> ..."), but nothing had re-raised the
            // update, so the row kept showing the staging path it was encoded to.
            var mirror = JobUpdated;
            if (mirror != null) { mirror(job); }

            // The received staging input is no longer needed once encoded.
            TryDelete(job.InputFile);

            JobOwner owner = OwnerFor(job.Id);
            PeerConnection client = owner != null ? _server.FindClientByName(owner.ClientName) : null;
            if (owner != null && client != null)
            {
                try
                {
                    client.Write(new NetMessage(MsgType.JobDone)
                        .With("jobId", owner.ClientJobId)
                        .With("ok", ok)
                        .With("finalPath", result != null && result.FinalPath != null ? result.FinalPath : "")
                        .With("error", error));
                }
                catch (Exception ex)
                {
                    Logger.Info("EncodeServerHost: JOB_DONE push failed (" + ex.Message + ").");
                }
            }
        }

        private JobOwner OwnerFor(Guid encodeJobId)
        {
            lock (_stateLock)
            {
                JobOwner owner;
                return _jobOwners.TryGetValue(encodeJobId, out owner) ? owner : null;
            }
        }

        /// <summary>One-line description of what a metadata package claims to be, for the log.</summary>
        public static string DescribePackage(MediaMetadata meta)
        {
            if (meta == null) { return "package: (none)"; }
            if (meta.MediaType == MediaType.Movie)
            {
                int mapped = 0;
                if (meta.TitleMappings != null)
                {
                    foreach (TitleMapping m in meta.TitleMappings)
                    {
                        if (m.Kind == TitleKind.Movie && m.Include) { mapped++; }
                    }
                }
                string title = string.IsNullOrWhiteSpace(meta.MovieTitle) ? "(blank)" : meta.MovieTitle;
                return mapped > 0
                    ? "package: movie x" + mapped + " (multi-movie mappings)"
                    : "package: movie '" + title + "' (" + (meta.Year ?? "") + ")";
            }
            if (meta.MediaType == MediaType.TvShow)
            {
                string show = string.IsNullOrWhiteSpace(meta.ShowName) ? "(blank)" : meta.ShowName;
                return "package: tv '" + show + "' S" + meta.SeasonNumber;
            }
            return "package: " + meta.MediaType;
        }

        /// <summary>
        /// Returns a human-readable reason a confirmed metadata package still can't be safely
        /// placed (blank movie title / show name — the "Untitled Movie" bug), or null when the
        /// names are usable. Public + static so it's unit-testable.
        /// </summary>
        public static string DescribeMetadataNameProblem(MediaMetadata meta)
        {
            if (meta.MediaType == MediaType.Movie)
            {
                bool anyMovieMappings = false;
                if (meta.TitleMappings != null)
                {
                    foreach (TitleMapping m in meta.TitleMappings)
                    {
                        if (m.Kind != TitleKind.Movie || !m.Include) { continue; }
                        anyMovieMappings = true;
                        if (string.IsNullOrWhiteSpace(m.MovieTitle))
                        {
                            return "a disc title is mapped to a movie with a BLANK name (it would " +
                                   "be placed as 'Untitled Movie'). Re-run the lookup on the ripper.";
                        }
                    }
                }
                if (!anyMovieMappings && string.IsNullOrWhiteSpace(meta.MovieTitle))
                {
                    return "the movie title is blank (it would be placed as 'Untitled Movie'). " +
                           "Re-run the lookup on the ripper.";
                }
            }
            else if (meta.MediaType == MediaType.TvShow && string.IsNullOrWhiteSpace(meta.ShowName))
            {
                return "the show name is blank (episodes would be placed under 'Unknown Show'). " +
                       "Re-run the lookup on the ripper.";
            }
            return null;
        }

        // ---------------- containment ----------------

        /// <summary>
        /// Builds a path for a remotely supplied file name that is GUARANTEED to stay inside
        /// <paramref name="stagingRoot"/>: the name is flattened to its final component, illegal
        /// characters are stripped, and the resolved absolute path is verified to still be under
        /// the root (belt and braces). Public + static so it's directly unit-testable.
        /// </summary>
        public static string SafeStagingPath(string stagingRoot, string clientSuppliedName)
        {
            // Flatten any path structure ("..\..\evil.mkv", "C:\x\y.mkv", "a/b/c.mkv") to the bare
            // final name. Then strip characters Windows forbids in a file name.
            string bare = (clientSuppliedName ?? "").Replace('/', '\\');
            int lastSep = bare.LastIndexOf('\\');
            if (lastSep >= 0) { bare = bare.Substring(lastSep + 1); }

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                bare = bare.Replace(c.ToString(), "");
            }
            bare = bare.Trim().TrimStart('.'); // no dot-relative trickery, no empty-extension games
            if (bare.Length == 0) { bare = "upload.bin"; }

            string rootFull = Path.GetFullPath(stagingRoot);
            string candidate = Path.GetFullPath(Path.Combine(rootFull, bare));

            // Belt and braces: even after flattening, verify the resolved path is inside the root.
            string rootWithSep = rootFull.EndsWith("\\") ? rootFull : rootFull + "\\";
            if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Refusing staging path outside the inbox: '" + clientSuppliedName + "'.");
            }
            return candidate;
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) { File.Delete(path); } }
            catch { /* staging cleanup is best-effort */ }
        }

        public void Dispose()
        {
            _server.Dispose();
            _encodeQueue.Dispose();
        }
    }
}
