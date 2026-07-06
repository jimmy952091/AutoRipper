using System;
using System.Collections.Generic;

namespace MediaRipperEncoder.Models
{
    public enum RipStatus
    {
        Queued,
        Ripping,
        Completed,
        Failed
    }

    /// <summary>
    /// A single queued rip: rip THESE titles from THIS disc to THIS folder. Carries its own
    /// live status/progress so the UI can display the queue. Later phases attach the
    /// confirmed metadata package and hand the produced files to the encode queue.
    /// </summary>
    public class RipJob
    {
        public Guid Id { get; private set; }

        // --- Source ---
        public int DiscIndex { get; set; }        // MakeMKV disc index
        public string DriveLetter { get; set; }   // Windows letter, for the post-rip eject
        public string DiscLabel { get; set; }     // for display

        /// <summary>
        /// Explicit MakeMKV source spec (e.g. file:"Z:\") for the advanced network/mapped-drive
        /// path. Empty means rip the local drive at <see cref="DiscIndex"/> (the normal case).
        /// </summary>
        public string SourceSpec { get; set; }

        /// <summary>
        /// True for a remote/mapped source that can't be ejected from this machine. The queue skips
        /// auto-eject and instead the UI prompts the user to change the disc on the shared drive.
        /// </summary>
        public bool ManualDiscChange { get; set; }

        /// <summary>Title indices to rip. Empty list means "all titles".</summary>
        public List<int> TitleIndices { get; set; }

        /// <summary>Skip titles shorter than this (seconds); filters out menus/junk.</summary>
        public int MinLengthSeconds { get; set; }

        // --- Destination ---
        public string OutputDirectory { get; set; }

        // --- Live state ---
        public RipStatus Status { get; set; }
        public int ProgressPercent { get; set; }
        public string CurrentOperation { get; set; }
        public string Error { get; set; }

        /// <summary>Files MakeMKV actually produced, handed to the encode queue in Phase 4.</summary>
        public List<string> OutputFiles { get; set; }

        /// <summary>
        /// Per-title status/outcome, one entry per title this job rips. Drives the per-title
        /// rows in the UI and lets failed titles be retried individually. Populated when the
        /// job is created; if left empty for a specific-titles job the rip fills it in from
        /// <see cref="TitleIndices"/>.
        /// </summary>
        public List<RipTitleResult> TitleResults { get; set; }

        /// <summary>
        /// The confirmed metadata package for this disc. Per the project standard it travels
        /// WITH the job through rip -> encode -> placement and is never re-derived from a
        /// filename. Set by the pipeline when a disc is queued from the metadata screen.
        /// </summary>
        public MediaMetadata Metadata { get; set; }

        public RipJob()
        {
            Id = Guid.NewGuid();
            TitleIndices = new List<int>();
            MinLengthSeconds = 120; // sensible default: ignore sub-2-minute menu loops/junk
            DiscLabel = "";
            SourceSpec = "";
            CurrentOperation = "";
            Error = "";
            OutputFiles = new List<string>();
            TitleResults = new List<RipTitleResult>();
            Status = RipStatus.Queued;
        }

        public bool RipAllTitles
        {
            get { return TitleIndices == null || TitleIndices.Count == 0; }
        }

        public string ShortId
        {
            get { return Id.ToString().Substring(0, 8); }
        }
    }
}
