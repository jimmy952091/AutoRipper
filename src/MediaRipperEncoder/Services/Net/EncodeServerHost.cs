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
    ///   FILE_BEGIN + raw bytes         -> receive into a CONTAINED staging path, verify SHA-256
    ///   plan via the SHARED EncodeJobPlanner (server's own roots/presets, same naming as local)
    ///   enqueue on the real EncodeQueue -> PROGRESS pushed to the client as it encodes
    ///   finished -> EncodeFinisher (tag + overwrite-safe placement) -> JOB_DONE to the client
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

        // Jobs whose JOB_SUBMIT arrived but whose file hasn't. The protocol is serial (one client,
        // one file at a time), so a simple FIFO matches files to submissions.
        private readonly Queue<RemoteEncodeRequest> _awaitingFile = new Queue<RemoteEncodeRequest>();
        private readonly object _stateLock = new object();

        // EncodeJob.Id -> the client's job id, so PROGRESS/JOB_DONE can reference the id the
        // ripper side knows.
        private readonly Dictionary<Guid, string> _clientJobIds = new Dictionary<Guid, string>();

        /// <summary>Mirror of job updates for the server node's own UI (name, percent, operation).</summary>
        public event Action<EncodeJob> JobUpdated;

        public EncodeServerHost(AppSettings settings)
        {
            _settings = settings;

            _handBrake = new HandBrakeService(settings.HandBrakeCliPath, settings.HandBrakePresetPath);
            _encodeQueue = new EncodeQueue(_handBrake);
            _planner = EncodeJobPlanner.FromSettings(settings);

            // All received files land under here and ONLY here.
            _stagingRoot = Path.Combine(string.IsNullOrEmpty(settings.TempFolder)
                ? Path.GetTempPath() : settings.TempFolder, "remote_inbox");

            _server = new LanServer(Environment.MachineName, null, settings.NodeSharedSecret);
            _server.MessageReceived += OnMessage;
            _server.ClientDisconnected += name =>
                Logger.Info("EncodeServerHost: ripper '" + name + "' disconnected; queued/running encodes continue.");

            _encodeQueue.JobUpdated += OnQueueJobUpdated;
            _encodeQueue.JobEncodedSuccessfully += OnEncodeDone;
        }

        public void Start()
        {
            Directory.CreateDirectory(_stagingRoot);
            _server.Start(_settings.NodePort);
            Logger.Info("EncodeServerHost: ready to receive encode jobs (staging: " + _stagingRoot + ").");
        }

        // ---------------- incoming messages ----------------

        private void OnMessage(NetMessage msg, PeerConnection conn)
        {
            try
            {
                if (msg.Type == MsgType.JobSubmit) { HandleJobSubmit(msg, conn); }
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

            lock (_stateLock) { _awaitingFile.Enqueue(request); }
            conn.Write(new NetMessage(MsgType.JobAccepted).With("jobId", request.ClientJobId ?? ""));
            Logger.Info("EncodeServerHost: accepted job " + request.ClientJobId +
                        " ('" + request.SourceFileName + "', awaiting file).");
        }

        private void HandleFileBegin(NetMessage msg, PeerConnection conn)
        {
            RemoteEncodeRequest request = null;
            lock (_stateLock)
            {
                if (_awaitingFile.Count > 0) { request = _awaitingFile.Dequeue(); }
            }

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

            bool ok = FileTransfer.ReceiveFile(conn, msg, dest);
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

            lock (_stateLock) { _clientJobIds[job.Id] = request.ClientJobId ?? ""; }
            _encodeQueue.Enqueue(job);
        }

        // ---------------- queue events -> push to client ----------------

        private void OnQueueJobUpdated(EncodeJob job)
        {
            var mirror = JobUpdated; if (mirror != null) { mirror(job); }

            string clientJobId = ClientJobIdFor(job.Id);
            if (clientJobId == null) { return; } // not a remote job

            PeerConnection client = _server.CurrentClient;
            if (client == null) { return; } // ripper offline; encodes continue, updates resume on rejoin

            try
            {
                client.Write(new NetMessage(MsgType.Progress)
                    .With("jobId", clientJobId)
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
                result = EncodeFinisher.FinishAndPlace(job, null);
                ok = result.Outcome != PlacementOutcome.Failed;
                if (!ok) { error = "Encoded, but library placement failed (see server log)."; }
            }
            catch (Exception ex)
            {
                Logger.Error("EncodeServerHost: finish/placement threw for " + job.ShortId + ".", ex);
                error = "Encoded, but tagging/placement threw: " + ex.Message;
            }

            // The received staging input is no longer needed once encoded.
            TryDelete(job.InputFile);

            string clientJobId = ClientJobIdFor(job.Id);
            PeerConnection client = _server.CurrentClient;
            if (clientJobId != null && client != null)
            {
                try
                {
                    client.Write(new NetMessage(MsgType.JobDone)
                        .With("jobId", clientJobId)
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

        private string ClientJobIdFor(Guid encodeJobId)
        {
            lock (_stateLock)
            {
                string id;
                return _clientJobIds.TryGetValue(encodeJobId, out id) ? id : null;
            }
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
