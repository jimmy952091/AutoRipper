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
            if (meta == null || meta.MediaType != MediaType.TvShow) { return baseLabel; }

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
            MediaMetadata meta = job.Metadata;
            if (meta == null)
            {
                Logger.Error("Ripped job " + job.ShortId + " had no metadata; can't build encodes.");
                return;
            }

            foreach (string mkv in job.OutputFiles)
            {
                int titleIndex = ParseTitleIndex(mkv);
                EncodeJob ej = _planner.BuildEncodeJob(meta, mkv, titleIndex);
                if (ej != null)
                {
                    _encodeQueue.Enqueue(ej); // FIFO: appended to the back.
                }
                else
                {
                    Logger.Info("Skipping ripped file (no episode/feature mapping): " + mkv);
                }
            }
        }

        // ---------------- encode -> placement ----------------

        private void OnEncodeDone(EncodeJob job)
        {
            if (string.IsNullOrEmpty(job.FinalTargetPath))
            {
                return; // ad-hoc encode with no library target — leave it where it is.
            }

            // Stamp the embedded Title tag (e.g. "Solo Leveling - S01E009 (Episode Name)") so VLC
            // and Explorer show the show + episode instead of MakeMKV's disc label. Computed when
            // the job was built (carries original punctuation + show-name prefix); falls back to the
            // file name for older/ad-hoc jobs. Done on the staged file before it's moved.
            string embeddedTitle = string.IsNullOrEmpty(job.EmbeddedTitle)
                ? MediaTagWriter.TitleFromFileName(job.FinalTargetPath)
                : job.EmbeddedTitle;
            MediaTagWriter.SetTitle(job.OutputFile, embeddedTitle);

            var target = new LibraryTarget
            {
                Folder = Path.GetDirectoryName(job.FinalTargetPath),
                FileName = Path.GetFileName(job.FinalTargetPath),
                FullPath = job.FinalTargetPath
            };

            PlacementPlan plan = LibraryPlacer.Plan(job.OutputFile, target);

            ConflictResolution resolution = ConflictResolution.KeepBoth;
            if (plan.TargetExists)
            {
                Func<PlacementPlan, ConflictResolution> resolver = ResolveConflict;
                resolution = resolver != null ? resolver(plan) : ConflictResolution.KeepBoth;
            }

            PlacementResult result = LibraryPlacer.Commit(plan, resolution);

            // NOTE: we deliberately leave job.OutputFile pointing at the staging path so a later
            // "Re-encode" re-stages and goes back through this overwrite-protected placement,
            // rather than writing straight into the library. The final location is reported via
            // CurrentOperation instead.
            if (result.Outcome == PlacementOutcome.Skipped)
            {
                job.CurrentOperation = "Kept existing library file (skipped).";
            }
            else if (result.Outcome == PlacementOutcome.Failed)
            {
                job.CurrentOperation = "Encoded OK, but placing into the library FAILED.";
            }
            else
            {
                job.CurrentOperation = "Placed -> " + result.FinalPath;
            }

            Action<EncodeJob, PlacementResult> handler = FilePlaced;
            if (handler != null) { handler(job, result); }
        }

        private static string DescribeDisc(MediaMetadata meta)
        {
            if (meta.MediaType == MediaType.Movie)
            {
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

            // Movie (or anything else): rip only the longest title.
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
            _ripQueue.Dispose();
            _encodeQueue.Dispose();
        }
    }
}
