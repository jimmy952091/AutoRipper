using System.Collections.Generic;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// Abstraction over the metadata sources so the UI can be built and tested against a
    /// mock before real API keys exist, then switched to the live OMDb/TheTVDB provider with
    /// no UI changes. Every method returns candidates/episodes for the user to confirm —
    /// none of them commit anything.
    /// </summary>
    public interface IMetadataProvider
    {
        /// <summary>Search movies (OMDb). Year may be "" to search without a year filter.</summary>
        Task<List<MetadataCandidate>> SearchMoviesAsync(string title, string year);

        /// <summary>Search TV series (TheTVDB) by name.</summary>
        Task<List<MetadataCandidate>> SearchSeriesAsync(string name);

        /// <summary>
        /// Pull the episode list for one season of a confirmed series (TheTVDB), in the given
        /// ordering scheme (aired / DVD / absolute).
        /// </summary>
        Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, int season, EpisodeOrder order);

        /// <summary>
        /// Returns the confirmed series' name in the user's preferred language (e.g. the English
        /// title of an anime), or empty string to keep the name already shown. Lets the app force
        /// the show/folder name to one language without the user renaming folders afterward.
        /// </summary>
        Task<string> GetSeriesNameAsync(string seriesId);
    }
}
