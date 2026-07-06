namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// One candidate match returned by a metadata lookup (OMDb movie or TheTVDB series),
    /// shown to the user to confirm before anything is locked in. The whole point of this
    /// type is the confirm step: we NEVER silently auto-pick a match, because a wrong match
    /// means wrong placement in a permanent library.
    /// </summary>
    public class MetadataCandidate
    {
        /// <summary>
        /// Provider-specific id. For movies this is the IMDb id (e.g. "tt0119174"); for TV
        /// this is the TheTVDB series id. This id is what gets stored so we never re-guess.
        /// </summary>
        public string ProviderId { get; set; }

        public string Title { get; set; }

        /// <summary>Release/first-aired year, or "" if unknown. Helps disambiguate remakes.</summary>
        public string Year { get; set; }

        /// <summary>True for a TV series candidate, false for a movie candidate.</summary>
        public bool IsSeries { get; set; }

        /// <summary>
        /// Extra disambiguation text shown in the confirm list (plot snippet, network,
        /// alternate titles) so the user can tell near-identical results apart.
        /// </summary>
        public string Detail { get; set; }

        /// <summary>Poster/artwork URL if the provider supplied one (optional, may be "").</summary>
        public string PosterUrl { get; set; }

        public MetadataCandidate()
        {
            ProviderId = "";
            Title = "";
            Year = "";
            Detail = "";
            PosterUrl = "";
        }

        public override string ToString()
        {
            string year = string.IsNullOrEmpty(Year) ? "" : " (" + Year + ")";
            return Title + year;
        }
    }
}
