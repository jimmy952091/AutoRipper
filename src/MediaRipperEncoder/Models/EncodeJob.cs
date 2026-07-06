using System;

namespace MediaRipperEncoder.Models
{
    public enum EncodeStatus
    {
        Queued,
        Encoding,
        Completed,
        Failed
    }

    /// <summary>
    /// One HandBrake encode: take an input file (a .mkv from the rip queue, or any file the
    /// user adds) and produce an encoded output using the configured preset. Retained in the
    /// encode queue's list even after finishing so it can be re-selected and re-encoded.
    /// </summary>
    public class EncodeJob
    {
        public Guid Id { get; private set; }

        public string InputFile { get; set; }
        public string OutputFile { get; set; }

        /// <summary>Friendly label for the UI (episode/title name once Phase 5 supplies it).</summary>
        public string DisplayName { get; set; }

        // --- Per-job preset override (Phase 7) ---
        // The library mixes live-action and animation, so each disc picks a preset. When these
        // are set they override the encode queue's default preset for this job only.

        /// <summary>Preset .json file to import for this job, or empty to use the queue default.</summary>
        public string PresetPath { get; set; }

        /// <summary>Preset name (-Z) for this job, or empty to use the queue default.</summary>
        public string PresetName { get; set; }

        // --- Placement target (Phase 7) ---

        /// <summary>
        /// Where the encoded file should ultimately land in the library. The job encodes to a
        /// staging path (<see cref="OutputFile"/>) first, then the pipeline moves it here with
        /// overwrite protection. Empty for ad-hoc encodes that stay where they are.
        /// </summary>
        public string FinalTargetPath { get; set; }

        /// <summary>MakeMKV title index this encode came from (for display/diagnostics).</summary>
        public int TitleIndex { get; set; }

        /// <summary>
        /// The human title to embed in the finished file's Title tag (what VLC / Explorer show),
        /// e.g. "Solo Leveling - S01E009 (Episode Name)". Kept with original punctuation (not the
        /// filesystem-sanitized file name) and carried on the job so re-encodes reuse it. Empty
        /// falls back to the file name without extension.
        /// </summary>
        public string EmbeddedTitle { get; set; }

        public EncodeStatus Status { get; set; }
        public int ProgressPercent { get; set; }
        public string CurrentOperation { get; set; }
        public string Error { get; set; }

        /// <summary>UI checkbox state for the "Re-encode selected" action.</summary>
        public bool Selected { get; set; }

        public EncodeJob()
        {
            Id = Guid.NewGuid();
            InputFile = "";
            OutputFile = "";
            DisplayName = "";
            PresetPath = "";
            PresetName = "";
            FinalTargetPath = "";
            CurrentOperation = "";
            Error = "";
            Status = EncodeStatus.Queued;
        }

        public string ShortId
        {
            get { return Id.ToString().Substring(0, 8); }
        }
    }
}
