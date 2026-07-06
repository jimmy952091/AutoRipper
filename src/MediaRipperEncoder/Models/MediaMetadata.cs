using System.Collections.Generic;

namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// The confirmed metadata package for one disc. This is the single source of truth that
    /// the project standard requires to travel with a job through ripping, encoding, and file
    /// placement — it is NEVER re-derived from a filename later.
    ///
    /// It captures three things:
    ///  1. What the disc is (type + media type).
    ///  2. The confirmed provider identity (IMDb id for a movie, TheTVDB id for a series),
    ///     so naming is authoritative rather than guessed.
    ///  3. Which HandBrake preset to encode it with (general vs. animation).
    /// </summary>
    public class MediaMetadata
    {
        public DiscType DiscType { get; set; }
        public MediaType MediaType { get; set; }

        /// <summary>
        /// Internal HandBrake preset name to encode this disc with (e.g. "Heto Default" or
        /// "Heto Default Animation"). Chosen per disc because the library mixes live-action
        /// and animation. This is the -Z value HandBrakeCLI receives.
        /// </summary>
        public string PresetName { get; set; }

        // --- Movie fields (MediaType == Movie) ---
        public string MovieTitle { get; set; }
        public string Year { get; set; }
        public string ImdbId { get; set; }

        // --- TV fields (MediaType == TvShow) ---
        public string ShowName { get; set; }
        public int SeasonNumber { get; set; }
        public int DiscNumber { get; set; }
        public string TvdbSeriesId { get; set; }

        /// <summary>Per-title decisions from the confirmation screen (TV episode mapping / excludes).</summary>
        public List<TitleMapping> TitleMappings { get; set; }

        /// <summary>
        /// True only after the user has explicitly confirmed a provider match. Processing is
        /// blocked until this is set, so nothing lands in the library on a guess.
        /// </summary>
        public bool MatchConfirmed { get; set; }

        public MediaMetadata()
        {
            PresetName = "";
            MovieTitle = "";
            Year = "";
            ImdbId = "";
            ShowName = "";
            TitleMappings = new List<TitleMapping>();
        }
    }
}
