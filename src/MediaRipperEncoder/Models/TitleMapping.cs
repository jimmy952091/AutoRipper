using System.Collections.Generic;

namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// The user's confirmed decision about a single disc title: what it is, and (for TV) which
    /// episode(s) it maps to. Produced by the Phase 5 per-title confirmation screen and carried
    /// with the job so file placement (Phase 6) never has to re-guess from a filename.
    ///
    /// A title can cover MORE THAN ONE episode. Segmented cartoons (e.g. Ed, Edd n Eddy) put two
    /// named 11-minute segments in one ~22-minute disc title, which maps to two TheTVDB episodes
    /// and is written as a Plex/Jellyfin multi-episode file: S01E01-E02.
    /// </summary>
    public class TitleMapping
    {
        /// <summary>MakeMKV title index this mapping describes.</summary>
        public int TitleIndex { get; set; }

        /// <summary>Duration text, carried purely for display on the confirm screen.</summary>
        public string Duration { get; set; }

        /// <summary>
        /// Whether this title is included in processing. Unchecking it excludes special
        /// features / duplicate "play all" titles so they aren't encoded or mislabeled.
        /// </summary>
        public bool Include { get; set; }

        public TitleKind Kind { get; set; }

        public int SeasonNumber { get; set; }

        // --- Movie fields (Kind == Movie), used for a MULTI-MOVIE disc (e.g. a Babe + Beethoven
        // double feature), where each mapped title is a distinct film with its own confirmed
        // identity and lands in its own Movies/<Title> (<Year>)/ folder. Empty for a single-movie
        // disc (that path uses the top-level MediaMetadata.MovieTitle/Year/ImdbId instead). ---

        public string MovieTitle { get; set; }
        public string MovieYear { get; set; }
        public string MovieImdbId { get; set; }

        /// <summary>
        /// The episode(s) this title covers, in order. One entry for a normal episode; two or
        /// more for a segmented title that becomes a multi-episode file. Empty for extras/ignored.
        /// </summary>
        public List<EpisodeInfo> Episodes { get; set; }

        public TitleMapping()
        {
            Duration = "";
            Include = true;
            Kind = TitleKind.Episode;
            Episodes = new List<EpisodeInfo>();
            MovieTitle = "";
            MovieYear = "";
            MovieImdbId = "";
        }

        /// <summary>True when this mapping is a confirmed movie (multi-movie disc).</summary>
        public bool IsConfirmedMovie
        {
            get { return Kind == TitleKind.Movie && !string.IsNullOrEmpty(MovieImdbId); }
        }

        /// <summary>True when this title covers more than one episode (a multi-episode file).</summary>
        public bool IsMultiEpisode
        {
            get { return Episodes != null && Episodes.Count > 1; }
        }

        public int FirstEpisodeNumber
        {
            get { return (Episodes != null && Episodes.Count > 0) ? Episodes[0].EpisodeNumber : 0; }
        }

        public int LastEpisodeNumber
        {
            get { return (Episodes != null && Episodes.Count > 0) ? Episodes[Episodes.Count - 1].EpisodeNumber : 0; }
        }
    }
}
