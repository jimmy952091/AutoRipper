using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// Canned metadata provider for building and testing the Phase 5 UI without API keys or a
    /// network connection. Deliberately returns MULTIPLE candidates for some queries so the
    /// confirm-the-match flow (which must never auto-pick) can be exercised, and returns a
    /// realistic multi-episode season so the per-title mapping screen has data to work with.
    /// </summary>
    public class MockMetadataProvider : IMetadataProvider
    {
        public Task<List<MetadataCandidate>> SearchMoviesAsync(string title, string year)
        {
            var results = new List<MetadataCandidate>();
            string t = (title ?? "").Trim();

            // Return two near-identical results to force a real confirm choice.
            results.Add(new MetadataCandidate
            {
                ProviderId = "tt0107290",
                Title = string.IsNullOrEmpty(t) ? "Jurassic Park" : t,
                Year = string.IsNullOrEmpty(year) ? "1993" : year,
                IsSeries = false,
                Detail = "Sci-fi / adventure — original film"
            });
            results.Add(new MetadataCandidate
            {
                ProviderId = "tt0369610",
                Title = string.IsNullOrEmpty(t) ? "Jurassic World" : t,
                Year = "2015",
                IsSeries = false,
                Detail = "Sci-fi / adventure — reboot (disambiguation test)"
            });

            return Task.FromResult(results);
        }

        // The mock has no real runtime data; 0 means "unknown", so the multi-movie auto-matcher
        // cleanly falls back to manual row selection when running keyless.
        public Task<int> GetMovieRuntimeMinutesAsync(string imdbId)
        {
            return Task.FromResult(0);
        }

        public Task<List<MetadataCandidate>> SearchSeriesAsync(string name)
        {
            var results = new List<MetadataCandidate>
            {
                new MetadataCandidate
                {
                    ProviderId = "78749",
                    Title = string.IsNullOrEmpty(name) ? "Ed, Edd n Eddy" : name.Trim(),
                    Year = "1999",
                    IsSeries = true,
                    Detail = "Animation, Cartoon Network"
                },
                new MetadataCandidate
                {
                    ProviderId = "999999",
                    Title = (string.IsNullOrEmpty(name) ? "Ed, Edd n Eddy" : name.Trim()) + " (Reboot)",
                    Year = "2020",
                    IsSeries = true,
                    Detail = "Alternate result (disambiguation test)"
                }
            };
            return Task.FromResult(results);
        }

        public Task<string> GetSeriesNameAsync(string seriesId)
        {
            // Mock has no translation source; empty means "keep the confirmed name".
            return Task.FromResult("");
        }

        public Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, int season, EpisodeOrder order)
        {
            // Two different orderings so the DVD-order picker is demonstrable. The DVD list
            // deliberately leads with the pairings from the user's real Ed Edd n Eddy booklet
            // (Pop Goes the Ed / Over Your Ed first, etc.) so the mapping visibly matches the disc.
            string[] aired =
            {
                "The Ed-Touchables", "Nagged to Ed", "Pop Goes the Ed", "Over Your Ed",
                "Sir Ed-a-Lot", "A Pinch to Grow an Ed", "Read All About Ed",
                "Quick Shot Ed", "Who, What, Where, Ed", "Keeping up with the Eds",
                "Dawn of the Eds", "Fool on the Ed", "A Boy and His Ed"
            };

            string[] dvd =
            {
                "Pop Goes the Ed", "Over Your Ed", "Dawn of the Eds", "Virt-Ed-Go",
                "Ed Too Many", "Ed-n-Seek", "The Ed-Touchables", "Nagged to Ed",
                "A Pinch to Grow an Ed", "Sir Ed-a-Lot", "Read All About Ed",
                "Quick Shot Ed", "Keeping up with the Eds", "Fool on the Ed"
            };

            string[] names = order == EpisodeOrder.Dvd ? dvd : aired;

            var list = new List<EpisodeInfo>();
            for (int i = 0; i < names.Length; i++)
            {
                list.Add(new EpisodeInfo
                {
                    SeasonNumber = season,
                    EpisodeNumber = i + 1,
                    Name = names[i],
                    Aired = "1999"
                });
            }
            return Task.FromResult(list);
        }
    }
}
