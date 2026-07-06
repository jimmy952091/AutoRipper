namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// The outcome of ripping ONE title within a disc job. A disc job rips several titles;
    /// tracking each one individually lets the UI show exactly which titles succeeded, lets the
    /// rip continue past a bad title instead of abandoning the rest, and lets the user retry only
    /// the failures (a scratched disc commonly fails just one or two titles while the rest are fine).
    /// Reuses <see cref="RipStatus"/> for its state.
    /// </summary>
    public class RipTitleResult
    {
        /// <summary>MakeMKV title index this row rips.</summary>
        public int TitleIndex { get; set; }

        /// <summary>Display label, e.g. "Title 03  S01E03 Pop Goes the Ed".</summary>
        public string Label { get; set; }

        public RipStatus Status { get; set; }
        public int ProgressPercent { get; set; }

        /// <summary>The .mkv this title produced, on success. Empty otherwise.</summary>
        public string OutputFile { get; set; }

        /// <summary>Why this title failed, on failure. Empty otherwise.</summary>
        public string Error { get; set; }

        public RipTitleResult()
        {
            Label = "";
            OutputFile = "";
            Error = "";
            Status = RipStatus.Queued;
        }
    }
}
