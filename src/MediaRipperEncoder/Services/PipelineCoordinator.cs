using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Ties the whole pipeline together: a confirmed disc becomes a rip job; each ripped file
    /// becomes an encode job (FIFO, appended to the back); each finished encode is moved into
    /// the Plex/Jellyfin library with overwrite protection. The confirmed metadata package
    /// travels with the jobs the entire way — nothing is re-derived from a filename.
    ///
    /// It owns the two independent queues so ripping and encoding run in parallel, and it never
    /// lets one item's failure stop the rest (that guarantee lives in the queues themselves).
    ///
    /// The pure planning decisions (which titles to rip, mapping a ripped file back to its
    /// title, choosing the preset, and computing the target path) are static helpers so they
    /// can be unit-tested without discs or subprocesses.
    /// </summary>
    public class PipelineCoordinator : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly MakeMkvService _makeMkv;
        private readonly RipQueue _ripQueue;
        private readonly HandBrakeService _handBrake;
        private readonly EncodeQueue _encodeQueue;

        // Shared naming/placement planner (also used by the remote encoder server, so a file
        // encoded on another machine is named + placed identically).
        private readonly EncodeJobPlanner _planner;

        // RipperClient role only: hands ripped files to the encoder server instead of the local
        // encode queue. Null in Standalone/EncoderServer roles. Each remote job gets a local
        // "shadow" EncodeJob so it shows in the same encode list, updated from the client's events.
        private readonly Services.Net.RemoteEncodeClient _remote;
        private readonly System.Collections.Generic.Dictionary<string, EncodeJob> _remoteShadows =
            new System.Collections.Generic.Dictionary<string, EncodeJob>();
        private readonly object _shadowLock = new object();

        /// <summary>True when this instance offloads encoding to a remote server (RipperClient role).</summary>
        public bool IsRemoteRipper { get { return _remote != null; } }

        /// <summary>Remote encoder connection state changed (RipperClient role). Background thread.</summary>
        public event Action<bool> RemoteConnectionChanged;

        /// <summary>Rip job status/progress changed (fires on a background thread).</summary>
        public event Action<RipJob> RipJobUpdated;

        /// <summary>A single title within a rip job changed (fires on a background thread).</summary>
        public event Action<RipJob, RipTitleResult> RipTitleUpdated;

        /// <summary>Encode job status/progress changed (fires on a background thread).</summary>
        public event Action<EncodeJob> EncodeJobUpdated;

        /// <summary>A finished encode was placed (or skipped) in the library.</summary>
        public event Action<EncodeJob, PlacementResult> FilePlaced;

        /// <summary>A network/mapped-source rip finished and the disc must be changed by hand
        /// (no auto-eject on a remote drive). Fires on a background thread.</summary>
        public event Action<RipJob> ManualDiscChangeRequested;

        /// <summary>
        /// Set by the UI to decide what to do when a target file already exists. Invoked on a
        /// background thread — the implementer must marshal to the UI thread. If left null, the
        /// pipeline defaults to KeepBoth so it NEVER silently overwrites a user's media.
        /// </summary>
        public Func<PlacementPlan, ConflictResolution> ResolveConflict;

        public PipelineCoordinator(AppSettings settings)
        {
            _settings = settings;

            _makeMkv = new MakeMkvService(settings.MakeMkvCliPath);
            _ripQueue = new RipQueue(_makeMkv);

            _handBrake = new HandBrakeService(settings.HandBrakeCliPath, settings.HandBrakePresetPath);
            _encodeQueue = new EncodeQueue(_handBrake);

            _planner = EncodeJobPlanner.FromSettings(settings);

            _ripQueue.JobUpdated += j => { var h = RipJobUpdated; if (h != null) h(j); };
            _ripQueue.TitleUpdated += (j, t) => { var h = RipTitleUpdated; if (h != null) h(j, t); };
            _ripQueue.JobRippedSuccessfully += OnRipDone;
            _ripQueue.ManualDiscChangeRequested += j => { var h = ManualDiscChangeRequested; if (h != null) h(j); };
            _encodeQueue.JobUpdated += j => { var h = EncodeJobUpdated; if (h != null) h(j); };
            _encodeQueue.JobEncodedSuccessfully += OnEncodeDone;

            // RipperClient: stand up the remote encoder link. Requires a server host + shared
            // secret; without them we stay local (and the UI surfaces that it's misconfigured).
            if (settings.NodeRole == NodeRole.RipperClient &&
                !string.IsNullOrWhiteSpace(settings.NodeServerHost) &&
                !string.IsNullOrWhiteSpace(settings.NodeSharedSecret))
            {
                _remote = new Services.Net.RemoteEncodeClient(
                    settings.NodeServerHost, settings.NodePort, settings.NodeSharedSecret, Environment.MachineName);
                _remote.ConnectionChanged += up => { var h = RemoteConnectionChanged; if (h != null) h(up); };
                _remote.JobStatusChanged += OnRemoteJobStatus;
                _remote.UploadProgress += OnRemoteUploadProgress;
                _remote.Start();
                Logger.Info("PipelineCoordinator: RipperClient mode — encoding offloaded to " +
                            settings.NodeServerHost + ":" + settings.NodePort + ".");
            }
        }

        /// <summary>Exposed so the UI can run a disc scan through the same MakeMKV service.</summary>
        public MakeMkvService MakeMkv { get { return _makeMkv; } }

        /// <summary>
        /// Stops the rip currently in progress (the "Stop rip" button). The stopped title (and any
        /// still-queued titles in that job) are marked failed so they can be retried, and the disc
        /// is left in the drive — not ejected — so it can be cleaned and re-ripped. Queued discs
        /// behind it still run.
        /// </summary>
        public void CancelCurrentRip()
        {
            _ripQueue.CancelCurrent();
        }

        /// <summary>Exposed so the UI's "Re-encode selected" can re-queue jobs.</summary>
        public EncodeQueue EncodeQueue { get { return _encodeQueue; } }

        /// <summary>
        /// Re-queues specific failed titles from an earlier job as a fresh rip, reusing the same
        /// disc and output folder. Intended for the UI's "Retry failed" button after a scratched
        /// disc failed one or two titles. The disc must still be in the drive (partial/failed rips
        /// deliberately don't auto-eject).
        /// </summary>
        public RipJob RetryTitles(RipJob original, List<int> titleIndices)
        {
            var job = new RipJob
            {
                DiscIndex = original.DiscIndex,
                DriveLetter = original.DriveLetter,
                SourceSpec = original.SourceSpec,
                ManualDiscChange = original.ManualDiscChange,
                Metadata = original.Metadata,
                DiscLabel = original.DiscLabel + " (retry)",
                OutputDirectory = original.OutputDirectory,
                MinLengthSeconds = original.MinLengthSeconds,
                TitleIndices = new List<int>(titleIndices),
                TitleResults = BuildTitleResults(original.Metadata, titleIndices)
            };
            _ripQueue.Enqueue(job);
            return job;
        }

        /// <summary>
        /// Queues a confirmed audio CD: the checked tracks ride the rip queue (built-in raw
        /// reader -> WAV) and then the encode queue (built-in FLAC/MP3/WAV encode + tags +
        /// placement) — the same two-queue flow as a video disc, per the one-flow design.
        /// </summary>
        public RipJob StartMusicJob(Models.MusicRelease release, Models.AudioCdToc toc,
            string driveLetter, string formatId)
        {
            var results = new List<RipTitleResult>();
            foreach (Models.AudioTrack track in release.Tracks)
            {
                if (!track.Selected) { continue; }
                results.Add(new RipTitleResult
                {
                    TitleIndex = track.Number,
                    Label = "Track " + track.Number.ToString("00") + "  " + track.Title +
                            "  (" + track.LengthText + ")"
                });
            }

            var job = new RipJob
            {
                Kind = JobKind.Music,
                DriveLetter = driveLetter,
                Toc = toc,
                Release = release,
                AudioFormatId = formatId,
                DiscLabel = release.Artist + " — " + release.Album +
                            (string.IsNullOrEmpty(release.Year) ? "" : " (" + release.Year + ")") + " [CD]",
                OutputDirectory = Path.Combine(_settings.TempFolder,
                    "cd_" + Guid.NewGuid().ToString("N").Substring(0, 8)),
                TitleResults = results,
                // TitleIndices non-empty so RipAllTitles stays false (not meaningful for CDs).
                TitleIndices = new List<int> { -1 }
            };
            _ripQueue.Enqueue(job);
            return job;
        }

        /// <summary>
        /// Queues a confirmed disc for ripping. <paramref name="discTitles"/> is the scan
        /// result, needed to pick the main feature for a movie (the movie screen has no
        /// per-title grid).
        /// </summary>
        public RipJob StartDiscJob(MediaMetadata meta, List<DiscTitle> discTitles, int discIndex, string driveLetter,
            string sourceSpec = null, bool manualDiscChange = false)
        {
            List<int> indices = SelectTitlesToRip(meta, discTitles);
            var job = new RipJob
            {
                DiscIndex = discIndex,
                DriveLetter = driveLetter,
                SourceSpec = sourceSpec ?? "",
                ManualDiscChange = manualDiscChange,
                Metadata = meta,
                DiscLabel = DescribeDisc(meta),
                OutputDirectory = Path.Combine(_settings.TempFolder,
                    "rip_" + Guid.NewGuid().ToString("N").Substring(0, 8)),
                TitleIndices = indices,
                TitleResults = BuildTitleResults(meta, indices)
            };
            _ripQueue.Enqueue(job);
            return job;
        }

        /// <summary>Builds the per-title rows for a job, labelling each with its episode where known.</summary>
        private static List<RipTitleResult> BuildTitleResults(MediaMetadata meta, List<int> titleIndices)
        {
            var results = new List<RipTitleResult>();
            if (titleIndices == null) { return results; }
            foreach (int idx in titleIndices)
            {
                results.Add(new RipTitleResult { TitleIndex = idx, Label = BuildTitleLabel(meta, idx) });
            }
            return results;
        }

        /// <summary>A human label for a title row: "Title 03  S01E03 Pop Goes the Ed" when known.</summary>
        private static string BuildTitleLabel(MediaMetadata meta, int titleIndex)
        {
            string baseLabel = "Title " + titleIndex.ToString("00");
            if (meta == null) { return baseLabel; }

            // Multi-movie disc: label each title with its confirmed film.
            if (meta.MediaType == MediaType.Movie)
            {
                TitleMapping mv = EncodeJobPlanner.FindMapping(meta, titleIndex);
                if (mv != null && mv.Kind == TitleKind.Movie && !string.IsNullOrEmpty(mv.MovieTitle))
                {
                    return baseLabel + "  " + mv.MovieTitle +
                        (string.IsNullOrEmpty(mv.MovieYear) ? "" : " (" + mv.MovieYear + ")");
                }
                return baseLabel;
            }

            if (meta.MediaType != MediaType.TvShow) { return baseLabel; }

            TitleMapping m = EncodeJobPlanner.FindMapping(meta, titleIndex);
            if (m == null || m.Episodes == null || m.Episodes.Count == 0) { return baseLabel; }

            string code = "S" + meta.SeasonNumber.ToString("00") + "E" + m.FirstEpisodeNumber.ToString("000");
            if (m.IsMultiEpisode) { code += "-E" + m.LastEpisodeNumber.ToString("000"); }
            string name = m.Episodes[0].Name;
            return baseLabel + "  " + code + (string.IsNullOrEmpty(name) ? "" : " " + name);
        }

        // ---------------- rip -> encode hand-off ----------------

        private void OnRipDone(RipJob job)
        {
            if (job.Kind == JobKind.Music)
            {
                OnMusicRipDone(job);
                return;
            }

            MediaMetadata meta = job.Metadata;
            if (meta == null)
            {
                Logger.Error("Ripped job " + job.ShortId + " had no metadata; can't build encodes.");
                return;
            }

            foreach (string mkv in job.OutputFiles)
            {
                int titleIndex = ParseTitleIndex(mkv);

                // Unmapped titles (unchecked extras / duplicate play-all) are skipped in BOTH modes.
                LibraryTarget target = _planner.BuildTargetFor(meta, titleIndex);
                if (target == null)
                {
                    Logger.Info("Skipping ripped file (no episode/feature mapping): " + mkv);
                    continue;
                }

                if (_remote != null)
                {
                    SubmitToRemote(meta, mkv, titleIndex, target.FileName);
                }
                else
                {
                    _encodeQueue.Enqueue(_planner.BuildEncodeJob(meta, mkv, titleIndex)); // FIFO local encode.
                }
            }
        }

        /// <summary>
        /// Music hand-off: each ripped WAV ("NN.wav") becomes an encode job for its release
        /// track. Cover art is fetched once per disc here (already on a background thread) and
        /// attached to every track's job. Music always encodes LOCALLY, even in RipperClient
        /// mode — a FLAC/MP3 encode takes seconds, so shipping WAVs to the encode server would
        /// cost more in transfer than it saves.
        /// </summary>
        private void OnMusicRipDone(RipJob job)
        {
            byte[] cover = null;
            try
            {
                cover = new Music.MusicBrainzClient().GetCoverArtAsync(job.Release.ReleaseId)
                    .GetAwaiter().GetResult();
                Logger.Info(cover != null
                    ? "Cover art fetched (" + cover.Length + " bytes) for " + job.Release.Album
                    : "No cover art in the archive for " + job.Release.Album + " (tracks unaffected).");
            }
            catch (Exception ex)
            {
                Logger.Error("Cover art fetch failed (tracks unaffected).", ex);
            }

            string extension = Music.MusicFormat.ById(job.AudioFormatId).Extension;

            foreach (string wav in job.OutputFiles)
            {
                int trackNumber;
                if (!int.TryParse(Path.GetFileNameWithoutExtension(wav), out trackNumber)) { continue; }

                Models.AudioTrack track = null;
                foreach (Models.AudioTrack t in job.Release.Tracks)
                {
                    if (t.Number == trackNumber) { track = t; break; }
                }
                if (track == null)
                {
                    Logger.Error("Ripped " + wav + " has no matching release track; skipping.");
                    continue;
                }

                LibraryTarget target = Music.MusicPathBuilder.BuildTrack(
                    _settings.MusicRoot, job.Release, track, extension);

                _encodeQueue.Enqueue(new EncodeJob
                {
                    Kind = JobKind.Music,
                    InputFile = wav,
                    OutputFile = Path.Combine(_settings.TempFolder,
                        "enc_" + Guid.NewGuid().ToString("N").Substring(0, 8), target.FileName),
                    FinalTargetPath = target.FullPath,
                    Release = job.Release,
                    Track = track,
                    CoverArt = cover,
                    AudioFormatId = job.AudioFormatId,
                    TitleIndex = trackNumber,
                    DisplayName = target.FileName
                });
            }
        }

        // ---------------- RipperClient: offload to the encoder server ----------------

        private void SubmitToRemote(MediaMetadata meta, string mkv, int titleIndex, string displayName)
        {
            long size = 0;
            try { size = new FileInfo(mkv).Length; } catch { /* best-effort */ }

            string clientJobId = Guid.NewGuid().ToString("N").Substring(0, 12);

            // Shadow job so the remote work shows in the same encode list the local mode uses.
            var shadow = new EncodeJob
            {
                InputFile = mkv,
                DisplayName = displayName,
                TitleIndex = titleIndex,
                Status = EncodeStatus.Queued,
                CurrentOperation = "Queued for remote encode"
            };
            lock (_shadowLock) { _remoteShadows[clientJobId] = shadow; }
            RaiseEncode(shadow);

            var request = new Services.Net.RemoteEncodeRequest
            {
                Metadata = meta,
                TitleIndex = titleIndex,
                UseAnimationPreset = _planner.IsAnimationChoice(meta),
                SourceFileName = Path.GetFileName(mkv),
                FileSize = size,
                ClientJobId = clientJobId
            };
            _remote.SubmitJob(request, mkv);
        }

        private void OnRemoteUploadProgress(string clientJobId, long sent, long total)
        {
            EncodeJob shadow = Shadow(clientJobId);
            if (shadow == null) { return; }
            int pct = total > 0 ? (int)(sent * 100 / total) : 0;
            shadow.Status = EncodeStatus.Encoding; // "in flight" from the user's view
            shadow.ProgressPercent = 0;            // encode % is separate; don't fake it during upload
            shadow.CurrentOperation = "Uploading to server… " + pct + "%";
            RaiseEncode(shadow);
        }

        private void OnRemoteJobStatus(Services.Net.RemoteJobStatus s)
        {
            EncodeJob shadow = Shadow(s.ClientJobId);
            if (shadow == null) { return; }

            EncodeStatus status;
            if (!Enum.TryParse(s.Status, out status)) { status = EncodeStatus.Encoding; }
            shadow.Status = status;
            shadow.ProgressPercent = s.Percent;

            if (s.Done)
            {
                shadow.CurrentOperation = s.Ok
                    ? "Encoded on server → " + s.FinalPath
                    : "Server error: " + s.Error;
                if (!s.Ok) { shadow.Error = s.Error; }
            }
            else
            {
                shadow.CurrentOperation = string.IsNullOrEmpty(s.Operation) ? "Encoding on server…" : s.Operation;
            }
            RaiseEncode(shadow);
        }

        private EncodeJob Shadow(string clientJobId)
        {
            lock (_shadowLock)
            {
                EncodeJob j;
                return _remoteShadows.TryGetValue(clientJobId ?? "", out j) ? j : null;
            }
        }

        private void RaiseEncode(EncodeJob job)
        {
            var h = EncodeJobUpdated; if (h != null) { h(job); }
        }

        // ---------------- encode -> placement ----------------

        private void OnEncodeDone(EncodeJob job)
        {
            if (string.IsNullOrEmpty(job.FinalTargetPath))
            {
                return; // ad-hoc encode with no library target — leave it where it is.
            }

            // Shared tag-and-place step (same one the remote encoder server uses).
            PlacementResult result = EncodeFinisher.FinishAndPlace(job, ResolveConflict);

            Action<EncodeJob, PlacementResult> handler = FilePlaced;
            if (handler != null) { handler(job, result); }
        }

        private static string DescribeDisc(MediaMetadata meta)
        {
            if (meta.MediaType == MediaType.Movie)
            {
                // Multi-movie disc: list the films (e.g. "Babe (1995) + Beethoven (1992)").
                if (meta.TitleMappings != null && meta.TitleMappings.Exists(m => m.Kind == TitleKind.Movie))
                {
                    var names = new List<string>();
                    foreach (TitleMapping m in meta.TitleMappings)
                    {
                        if (m.Include && m.Kind == TitleKind.Movie)
                        {
                            names.Add(m.MovieTitle + (string.IsNullOrEmpty(m.MovieYear) ? "" : " (" + m.MovieYear + ")"));
                        }
                    }
                    if (names.Count > 0) { return string.Join(" + ", names); }
                }
                return meta.MovieTitle + (string.IsNullOrEmpty(meta.Year) ? "" : " (" + meta.Year + ")");
            }
            return meta.ShowName + " S" + meta.SeasonNumber.ToString("00") + " (disc " + meta.DiscNumber + ")";
        }

        // ================= pure, testable planning helpers =================

        /// <summary>
        /// Which MakeMKV title indices to rip. For TV that's every included, non-ignored title;
        /// for a movie it's the single longest title (the main feature).
        /// </summary>
        public static List<int> SelectTitlesToRip(MediaMetadata meta, List<DiscTitle> discTitles)
        {
            var indices = new List<int>();

            if (meta.MediaType == MediaType.TvShow && meta.TitleMappings != null)
            {
                foreach (TitleMapping m in meta.TitleMappings)
                {
                    if (m.Include && m.Kind != TitleKind.Ignore)
                    {
                        indices.Add(m.TitleIndex);
                    }
                }
                return indices;
            }

            // Multi-movie (double-feature) disc: rip each included, confirmed movie title.
            if (meta.MediaType == MediaType.Movie && meta.TitleMappings != null &&
                meta.TitleMappings.Exists(m => m.Kind == TitleKind.Movie))
            {
                foreach (TitleMapping m in meta.TitleMappings)
                {
                    if (m.Include && m.Kind == TitleKind.Movie) { indices.Add(m.TitleIndex); }
                }
                return indices;
            }

            // Single-movie disc (or anything else): rip only the longest title.
            DiscTitle longest = SelectLongestTitle(discTitles);
            if (longest != null) { indices.Add(longest.Index); }
            return indices;
        }

        /// <summary>Returns the disc title with the greatest duration, or null if none.</summary>
        public static DiscTitle SelectLongestTitle(List<DiscTitle> discTitles)
        {
            if (discTitles == null) { return null; }
            DiscTitle best = null;
            int bestSeconds = -1;
            foreach (DiscTitle t in discTitles)
            {
                int s = ParseDurationSeconds(t.Duration);
                if (s > bestSeconds)
                {
                    bestSeconds = s;
                    best = t;
                }
            }
            return best;
        }

        /// <summary>
        /// Extracts the MakeMKV title index from an output filename. MakeMKV names files like
        /// "&lt;label&gt;_t01.mkv", so we read the number after "_t". Returns -1 if not found.
        /// </summary>
        public static int ParseTitleIndex(string mkvPath)
        {
            if (string.IsNullOrEmpty(mkvPath)) { return -1; }
            string name = Path.GetFileNameWithoutExtension(mkvPath);
            Match m = Regex.Match(name, @"_t(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                // Some MakeMKV builds put the token mid-name; try anywhere as a fallback.
                m = Regex.Match(name, @"_t(\d+)", RegexOptions.IgnoreCase);
            }
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int idx))
            {
                return idx;
            }
            return -1;
        }

        /// <summary>Parses "H:MM:SS" or "MM:SS" into total seconds. Returns 0 on failure.</summary>
        public static int ParseDurationSeconds(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration)) { return 0; }
            string[] parts = duration.Trim().Split(':');
            int total = 0;
            foreach (string part in parts)
            {
                int value;
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    return 0;
                }
                total = total * 60 + value;
            }
            return total;
        }

        public void Dispose()
        {
            if (_remote != null) { _remote.Dispose(); }
            _ripQueue.Dispose();
            _encodeQueue.Dispose();
        }
    }
}
