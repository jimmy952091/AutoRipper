using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// Movie lookups via OMDb (https://www.omdbapi.com) — used as an IMDb proxy since IMDb has
    /// no public API. Needs a free API key. Network/response parsing are kept separate so the
    /// parser can be unit-tested against captured sample JSON without hitting the network.
    /// </summary>
    public class OmdbClient
    {
        private const string BaseUrl = "https://www.omdbapi.com/";
        private readonly string _apiKey;
        private readonly HttpClient _http;

        public OmdbClient(string apiKey, HttpClient http = null)
        {
            _apiKey = apiKey ?? "";
            _http = http ?? SharedHttp.Client;
        }

        /// <summary>
        /// Searches movies by title (and optional year). Returns an empty list on "no results"
        /// and throws only on hard failures (bad key, network down) so the UI can distinguish
        /// "nothing found" from "lookup broke".
        /// </summary>
        public async Task<List<MetadataCandidate>> SearchMoviesAsync(string title, string year)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "No OMDb API key configured. Add a free key (omdbapi.com) in Settings to look up movies.");
            }

            string url = BaseUrl + "?apikey=" + Uri.EscapeDataString(_apiKey) +
                         "&type=movie&s=" + Uri.EscapeDataString(title ?? "");
            if (!string.IsNullOrWhiteSpace(year))
            {
                url += "&y=" + Uri.EscapeDataString(year.Trim());
            }

            string json;
            try
            {
                json = await _http.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("OMDb request failed.", ex);
                throw new Exception("Couldn't reach OMDb. Check your internet connection.", ex);
            }

            return ParseSearch(json);
        }

        /// <summary>
        /// Parses an OMDb search response. Pure function (no network) so it's directly testable.
        /// OMDb signals "no results" with {"Response":"False","Error":"Movie not found!"} —
        /// that's returned as an empty list, not an exception.
        /// </summary>
        public static List<MetadataCandidate> ParseSearch(string json)
        {
            var results = new List<MetadataCandidate>();
            if (string.IsNullOrWhiteSpace(json)) { return results; }

            JObject root = JObject.Parse(json);

            if (!string.Equals((string)root["Response"], "True", StringComparison.OrdinalIgnoreCase))
            {
                // "Movie not found!" and similar => no candidates (not an error the user must fix).
                return results;
            }

            JArray search = root["Search"] as JArray;
            if (search == null) { return results; }

            foreach (JToken item in search)
            {
                string poster = (string)item["Poster"];
                results.Add(new MetadataCandidate
                {
                    ProviderId = (string)item["imdbID"] ?? "",
                    Title = (string)item["Title"] ?? "",
                    Year = (string)item["Year"] ?? "",
                    IsSeries = false,
                    PosterUrl = (poster == "N/A") ? "" : (poster ?? ""),
                    Detail = "IMDb " + ((string)item["imdbID"] ?? "")
                });
            }
            return results;
        }
    }
}
