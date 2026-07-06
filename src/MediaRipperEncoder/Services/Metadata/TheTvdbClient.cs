using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Metadata
{
    /// <summary>
    /// TV series + episode lists via TheTVDB v4 API (https://thetvdb.com). Flow:
    ///   1. POST /login with the API key (and optional subscriber PIN) to get a bearer token.
    ///   2. GET /search?type=series to find the series.
    ///   3. GET /series/{id}/episodes/default (paged) for the episode list, filtered to season.
    ///
    /// Network calls and JSON parsing are separated so the parsers can be unit-tested against
    /// captured sample payloads without a key or a network connection.
    /// </summary>
    public class TheTvdbClient
    {
        private const string BaseUrl = "https://api4.thetvdb.com/v4";
        private const int MaxEpisodePages = 25; // safety cap so paging can't loop forever

        private readonly string _apiKey;
        private readonly string _pin;
        private readonly string _language;   // TheTVDB ISO 639-3 code, e.g. "eng"
        private readonly HttpClient _http;
        private string _token;

        public TheTvdbClient(string apiKey, string pin, string language = "eng", HttpClient http = null)
        {
            _apiKey = apiKey ?? "";
            _pin = pin ?? "";
            _language = string.IsNullOrWhiteSpace(language) ? "eng" : language.Trim();
            _http = http ?? SharedHttp.Client;
        }

        // ----- Authentication -----

        private async Task EnsureTokenAsync()
        {
            if (!string.IsNullOrEmpty(_token)) { return; }

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "No TheTVDB API key configured. Add a key (thetvdb.com) in Settings to look up TV shows.");
            }

            var body = new JObject { ["apikey"] = _apiKey };
            if (!string.IsNullOrWhiteSpace(_pin)) { body["pin"] = _pin; }

            string responseJson;
            try
            {
                var content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                HttpResponseMessage resp = await _http.PostAsync(BaseUrl + "/login", content).ConfigureAwait(false);
                responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new Exception("TheTVDB login was rejected (HTTP " + (int)resp.StatusCode +
                        "). Double-check your API key in Settings.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("TheTVDB login failed.", ex);
                throw;
            }

            _token = ParseLoginToken(responseJson);
            if (string.IsNullOrEmpty(_token))
            {
                throw new Exception("TheTVDB login did not return a token. Check your API key/PIN.");
            }
        }

        public static string ParseLoginToken(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { return ""; }
            JObject root = JObject.Parse(json);
            return (string)(root["data"]?["token"]) ?? "";
        }

        private async Task<string> AuthGetAsync(string url)
        {
            await EnsureTokenAsync().ConfigureAwait(false);
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Add("Authorization", "Bearer " + _token);
                HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new Exception("TheTVDB request failed (HTTP " + (int)resp.StatusCode + ").");
                }
                return json;
            }
        }

        // ----- Series search -----

        public async Task<List<MetadataCandidate>> SearchSeriesAsync(string name)
        {
            string url = BaseUrl + "/search?type=series&query=" + Uri.EscapeDataString(name ?? "");
            string json = await AuthGetAsync(url).ConfigureAwait(false);
            return ParseSearch(json);
        }

        public static List<MetadataCandidate> ParseSearch(string json)
        {
            var results = new List<MetadataCandidate>();
            if (string.IsNullOrWhiteSpace(json)) { return results; }

            JObject root = JObject.Parse(json);
            JArray data = root["data"] as JArray;
            if (data == null) { return results; }

            foreach (JToken item in data)
            {
                // TheTVDB returns the numeric id as "tvdb_id"; fall back to "id" if needed.
                string id = (string)item["tvdb_id"];
                if (string.IsNullOrEmpty(id)) { id = (string)item["id"]; }

                string network = (string)item["network"];
                string overview = (string)item["overview"];
                string detail = network;
                if (!string.IsNullOrEmpty(overview))
                {
                    string trimmed = overview.Length > 90 ? overview.Substring(0, 90) + "..." : overview;
                    detail = string.IsNullOrEmpty(network) ? trimmed : network + " — " + trimmed;
                }

                results.Add(new MetadataCandidate
                {
                    ProviderId = id ?? "",
                    Title = (string)item["name"] ?? "",
                    Year = (string)item["year"] ?? "",
                    IsSeries = true,
                    PosterUrl = (string)item["image_url"] ?? "",
                    Detail = detail ?? ""
                });
            }
            return results;
        }

        // ----- Episodes -----

        /// <summary>
        /// Maps our ordering enum to TheTVDB's season-type path segment. "default" is the
        /// series' own default order (almost always aired) and is guaranteed to exist, so we
        /// use it for Aired rather than "official", which some series leave unpopulated.
        /// </summary>
        public static string SeasonTypePath(EpisodeOrder order)
        {
            switch (order)
            {
                case EpisodeOrder.Dvd: return "dvd";
                case EpisodeOrder.Absolute: return "absolute";
                default: return "default";
            }
        }

        public async Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, int season, EpisodeOrder order)
        {
            List<EpisodeInfo> forSeason = await FetchSeasonAsync(seriesId, season, SeasonTypePath(order))
                .ConfigureAwait(false);

            // If a non-aired order simply isn't populated for this series, TheTVDB returns
            // nothing — fall back to the default (aired) order so the user still gets a list,
            // rather than an empty grid with no explanation.
            if (forSeason.Count == 0 && order != EpisodeOrder.Aired)
            {
                Logger.Info("TheTVDB '" + SeasonTypePath(order) +
                    "' order was empty for series " + seriesId + "; falling back to default order.");
                forSeason = await FetchSeasonAsync(seriesId, season, "default").ConfigureAwait(false);
            }

            forSeason.Sort((a, b) => a.EpisodeNumber.CompareTo(b.EpisodeNumber));
            return forSeason;
        }

        private async Task<List<EpisodeInfo>> FetchSeasonAsync(string seriesId, int season, string seasonType)
        {
            var forSeason = new List<EpisodeInfo>();
            int page = 0;

            while (page < MaxEpisodePages)
            {
                // Append the language segment so episode names come back translated (e.g. the
                // English names for an anime). TheTVDB falls back to the original name per-episode
                // when a translation is missing.
                string url = BaseUrl + "/series/" + Uri.EscapeDataString(seriesId ?? "") +
                             "/episodes/" + seasonType + "/" + Uri.EscapeDataString(_language) +
                             "?page=" + page;

                string json;
                try
                {
                    json = await AuthGetAsync(url).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A few series have no record in the requested language and TheTVDB errors
                    // rather than falling back. Drop the language segment and retry untranslated
                    // so the user still gets an episode list.
                    Logger.Info("TheTVDB translated episodes failed for '" + _language +
                        "' (" + ex.Message + "); retrying without a language.");
                    string plainUrl = BaseUrl + "/series/" + Uri.EscapeDataString(seriesId ?? "") +
                                      "/episodes/" + seasonType + "?page=" + page;
                    json = await AuthGetAsync(plainUrl).ConfigureAwait(false);
                }

                bool hasNext;
                List<EpisodeInfo> pageEpisodes = ParseEpisodesPage(json, out hasNext);

                foreach (EpisodeInfo ep in pageEpisodes)
                {
                    if (ep.SeasonNumber == season) { forSeason.Add(ep); }
                }

                if (!hasNext) { break; }
                page++;
            }

            return forSeason;
        }

        /// <summary>
        /// Returns the series name translated into the configured language (e.g. the English
        /// title of an anime), or empty string if unavailable. Uses the per-series translations
        /// endpoint. Never throws — a lookup failure just means "keep the name we already have".
        /// </summary>
        public async Task<string> GetSeriesNameAsync(string seriesId)
        {
            if (string.IsNullOrWhiteSpace(seriesId)) { return ""; }
            try
            {
                string url = BaseUrl + "/series/" + Uri.EscapeDataString(seriesId) +
                             "/translations/" + Uri.EscapeDataString(_language);
                string json = await AuthGetAsync(url).ConfigureAwait(false);
                return ParseTranslationName(json);
            }
            catch (Exception ex)
            {
                Logger.Info("TheTVDB series name translation to '" + _language +
                    "' unavailable for " + seriesId + " (" + ex.Message + "); keeping original.");
                return "";
            }
        }

        /// <summary>Extracts data.name from a translations response. Pure, for testability.</summary>
        public static string ParseTranslationName(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { return ""; }
            JObject root = JObject.Parse(json);
            return (string)(root["data"]?["name"]) ?? "";
        }

        /// <summary>
        /// Parses one page of the episodes response. Returns every episode on the page (caller
        /// filters by season) and reports via <paramref name="hasNextPage"/> whether more pages
        /// exist. Pure function for testability.
        /// </summary>
        public static List<EpisodeInfo> ParseEpisodesPage(string json, out bool hasNextPage)
        {
            hasNextPage = false;
            var list = new List<EpisodeInfo>();
            if (string.IsNullOrWhiteSpace(json)) { return list; }

            JObject root = JObject.Parse(json);

            // "next" is null on the last page, a URL/number otherwise.
            JToken next = root["links"]?["next"];
            hasNextPage = next != null && next.Type != JTokenType.Null &&
                          !string.IsNullOrEmpty(next.ToString());

            JArray episodes = root["data"]?["episodes"] as JArray;
            if (episodes == null) { return list; }

            foreach (JToken ep in episodes)
            {
                int? season = (int?)ep["seasonNumber"];
                int? number = (int?)ep["number"];
                if (season == null || number == null) { continue; }

                list.Add(new EpisodeInfo
                {
                    SeasonNumber = season.Value,
                    EpisodeNumber = number.Value,
                    Name = (string)ep["name"] ?? "",
                    Aired = (string)ep["aired"] ?? ""
                });
            }
            return list;
        }
    }
}
