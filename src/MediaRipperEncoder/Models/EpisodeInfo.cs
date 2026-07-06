namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// One episode from a series' season list (pulled from TheTVDB). This is the authoritative
    /// episode naming source — MakeMKV cannot supply it, because a disc has no idea which
    /// generic title is which episode.
    /// </summary>
    public class EpisodeInfo
    {
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Name { get; set; }

        /// <summary>First-aired date as text (may be ""), shown only to help the user map tracks.</summary>
        public string Aired { get; set; }

        public EpisodeInfo()
        {
            Name = "";
            Aired = "";
        }

        /// <summary>e.g. "S01E03 — The Ed-Touchables"</summary>
        public override string ToString()
        {
            string code = "S" + SeasonNumber.ToString("00") + "E" + EpisodeNumber.ToString("00");
            return string.IsNullOrEmpty(Name) ? code : code + " — " + Name;
        }
    }
}
