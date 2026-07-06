namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// One title (a rippable stream) on a disc, as reported by MakeMKV's title-info scan.
    /// MakeMKV exposes disc contents as generic "Title 0, Title 1, ..." — it is NOT
    /// episode- or feature-aware. The user (Phase 5) decides what each title actually is.
    /// </summary>
    public class DiscTitle
    {
        /// <summary>MakeMKV title index — the value used to rip this specific title.</summary>
        public int Index { get; set; }

        /// <summary>Display name MakeMKV assigns (often the source file name).</summary>
        public string Name { get; set; }

        /// <summary>Human-readable duration, e.g. "1:39:10".</summary>
        public string Duration { get; set; }

        /// <summary>Approximate size in bytes (for showing the user what they'll rip).</summary>
        public long SizeBytes { get; set; }

        /// <summary>Chapter count, useful for spotting the main feature vs. extras.</summary>
        public int ChapterCount { get; set; }

        /// <summary>The .mkv filename MakeMKV will write for this title.</summary>
        public string OutputFileName { get; set; }

        /// <summary>UI selection state — whether this title is chosen to be ripped.</summary>
        public bool Selected { get; set; }

        public DiscTitle()
        {
            Name = "";
            Duration = "";
            OutputFileName = "";
            Selected = true; // default to selected; user unchecks unwanted titles
        }

        public override string ToString()
        {
            string dur = string.IsNullOrEmpty(Duration) ? "?" : Duration;
            string size = SizeBytes > 0
                ? " — " + (SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.0") + " GB"
                : "";
            string chapters = ChapterCount > 0 ? " — " + ChapterCount + " ch" : "";
            return "Title " + Index + "  (" + dur + ")" + chapters + size;
        }
    }
}
