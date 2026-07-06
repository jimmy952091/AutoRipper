using System.Collections.Generic;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// The live provider used in production: movies come from OMDb, TV series/episodes from
    /// TheTVDB. Implements the same interface as the mock so the UI is identical either way.
    /// </summary>
    public class OnlineMetadataProvider : IMetadataProvider
    {
        private readonly OmdbClient _omdb;
        private readonly TheTvdbClient _tvdb;

        public OnlineMetadataProvider(AppSettings settings)
        {
            _omdb = new OmdbClient(settings.OmdbApiKey);
            _tvdb = new TheTvdbClient(settings.TheTvdbApiKey, settings.TheTvdbPin, settings.MetadataLanguage);
        }

        public Task<List<MetadataCandidate>> SearchMoviesAsync(string title, string year)
        {
            return _omdb.SearchMoviesAsync(title, year);
        }

        public Task<List<MetadataCandidate>> SearchSeriesAsync(string name)
        {
            return _tvdb.SearchSeriesAsync(name);
        }

        public Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, int season, EpisodeOrder order)
        {
            return _tvdb.GetEpisodesAsync(seriesId, season, order);
        }

        public Task<string> GetSeriesNameAsync(string seriesId)
        {
            return _tvdb.GetSeriesNameAsync(seriesId);
        }
    }
}
