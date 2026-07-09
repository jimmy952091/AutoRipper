using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services.Metadata;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// MusicBrainz lookups for audio CDs. Free, no API key — MusicBrainz just requires a
    /// meaningful User-Agent identifying the application. Flow:
    ///   1. Disc ID lookup (exact): /ws/2/discid/&lt;id&gt; returns the releases this exact
    ///      pressing appears on.
    ///   2. Typed fallback (fuzzy): /ws/2/release?query=... when the disc isn't in the database,
    ///      followed by a detail fetch for the chosen release.
    /// Cover art comes from the Cover Art Archive keyed by the release MBID.
    ///
    /// JSON parsing is split into pure static methods so it's testable offline against sample
    /// payloads, same as the OMDb/TheTVDB clients.
    /// </summary>
    public class MusicBrainzClient
    {
        private const string BaseUrl = "https://musicbrainz.org/ws/2";
        private const string CoverArtUrl = "https://coverartarchive.org/release/";
        private const string UserAgent = "AutoRipper/0.1.0 (heto.black@gmail.com)";

        private readonly HttpClient _http;

        public MusicBrainzClient(HttpClient http = null)
        {
            _http = http ?? SharedHttp.Client;
        }

        // ---- lookups ----

        /// <summary>Exact lookup by Disc ID. Empty list = disc not in the database (use the fallback).</summary>
        public async Task<List<MusicRelease>> LookupByDiscIdAsync(string discId)
        {
            string url = BaseUrl + "/discid/" + Uri.EscapeDataString(discId ?? "") +
                         "?fmt=json&inc=artist-credits+recordings+discids";
            string json;
            try
            {
                json = await GetAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A 404 here just means "unknown disc" — that's the normal fallback path, not an error.
                Logger.Info("MusicBrainz discid lookup returned nothing (" + ex.Message + ").");
                return new List<MusicRelease>();
            }
            return ParseDiscIdResponse(json, discId);
        }

        /// <summary>Fuzzy fallback: search releases by typed artist + album.</summary>
        public async Task<List<MusicRelease>> SearchReleasesAsync(string artist, string album)
        {
            string query = "release:\"" + (album ?? "").Replace("\"", "") + "\"" +
                           (string.IsNullOrWhiteSpace(artist) ? "" :
                            " AND artist:\"" + artist.Replace("\"", "") + "\"");
            string url = BaseUrl + "/release?fmt=json&limit=8&query=" + Uri.EscapeDataString(query);
            string json = await GetAsync(url).ConfigureAwait(false);
            return ParseSearchResponse(json);
        }

        /// <summary>Fills in the track list for a release chosen from the fuzzy search.</summary>
        public async Task<MusicRelease> GetReleaseDetailAsync(string releaseId, string discIdToMatch)
        {
            string url = BaseUrl + "/release/" + Uri.EscapeDataString(releaseId ?? "") +
                         "?fmt=json&inc=artist-credits+recordings+discids";
            string json = await GetAsync(url).ConfigureAwait(false);
            return ParseRelease(JObject.Parse(json), discIdToMatch);
        }

        /// <summary>Front cover art (500px) for a release, or null when the archive has none.</summary>
        public async Task<byte[]> GetCoverArtAsync(string releaseId)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, CoverArtUrl +
                    Uri.EscapeDataString(releaseId ?? "") + "/front-500"))
                {
                    req.Headers.Add("User-Agent", UserAgent);
                    HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) { return null; }
                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Info("Cover art unavailable for " + releaseId + " (" + ex.Message + ").");
                return null;
            }
        }

        private async Task<string> GetAsync(string url)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Add("User-Agent", UserAgent);
                req.Headers.Add("Accept", "application/json");
                HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    throw new Exception("MusicBrainz request failed (HTTP " + (int)resp.StatusCode + ").");
                }
                return body;
            }
        }

        // ---- pure parsers (unit-tested offline) ----

        public static List<MusicRelease> ParseDiscIdResponse(string json, string discId)
        {
            var results = new List<MusicRelease>();
            if (string.IsNullOrWhiteSpace(json)) { return results; }

            JObject root = JObject.Parse(json);
            var releases = root["releases"] as JArray;
            if (releases == null) { return results; }

            foreach (JToken release in releases)
            {
                MusicRelease parsed = ParseRelease(release as JObject, discId);
                if (parsed != null) { results.Add(parsed); }
            }
            return results;
        }

        public static List<MusicRelease> ParseSearchResponse(string json)
        {
            var results = new List<MusicRelease>();
            if (string.IsNullOrWhiteSpace(json)) { return results; }

            JObject root = JObject.Parse(json);
            var releases = root["releases"] as JArray;
            if (releases == null) { return results; }

            foreach (JToken r in releases)
            {
                // Search results are shallow (no track lists) — enough for the confirm dialog;
                // GetReleaseDetailAsync fills in tracks for the chosen one.
                results.Add(new MusicRelease
                {
                    ReleaseId = (string)r["id"] ?? "",
                    Album = (string)r["title"] ?? "",
                    Artist = JoinArtistCredit(r["artist-credit"] as JArray),
                    Year = YearOf((string)r["date"]),
                    Detail = DescribeRelease(r)
                });
            }
            return results;
        }

        /// <summary>
        /// Parses one full release object. When <paramref name="discId"/> is given and the release
        /// has several discs, the medium whose disc list contains that id supplies the track list
        /// and disc number — that's how "disc 2 of 2" gets numbered correctly.
        /// </summary>
        public static MusicRelease ParseRelease(JObject release, string discId)
        {
            if (release == null) { return null; }

            var result = new MusicRelease
            {
                ReleaseId = (string)release["id"] ?? "",
                Album = (string)release["title"] ?? "",
                Artist = JoinArtistCredit(release["artist-credit"] as JArray),
                Year = YearOf((string)release["date"]),
                Detail = DescribeRelease(release)
            };

            var media = release["media"] as JArray;
            if (media == null || media.Count == 0) { return result; }

            result.DiscCount = media.Count;

            JToken medium = media[0];
            if (!string.IsNullOrEmpty(discId))
            {
                foreach (JToken m in media)
                {
                    var discs = m["discs"] as JArray;
                    if (discs == null) { continue; }
                    foreach (JToken d in discs)
                    {
                        if ((string)d["id"] == discId) { medium = m; break; }
                    }
                }
            }

            result.DiscNumber = (int?)medium["position"] ?? 1;

            var tracks = medium["tracks"] as JArray;
            if (tracks != null)
            {
                foreach (JToken t in tracks)
                {
                    int? lengthMs = (int?)t["length"];
                    result.Tracks.Add(new AudioTrack
                    {
                        Number = (int?)t["position"] ?? result.Tracks.Count + 1,
                        Title = (string)t["title"] ?? "",
                        LengthSeconds = lengthMs.HasValue ? lengthMs.Value / 1000 : 0,
                        Selected = true
                    });
                }
            }
            return result;
        }

        public static string JoinArtistCredit(JArray artistCredit)
        {
            if (artistCredit == null) { return ""; }
            var sb = new System.Text.StringBuilder();
            foreach (JToken credit in artistCredit)
            {
                sb.Append((string)credit["name"] ?? "");
                sb.Append((string)credit["joinphrase"] ?? "");
            }
            return sb.ToString();
        }

        private static string YearOf(string date)
        {
            return !string.IsNullOrEmpty(date) && date.Length >= 4 ? date.Substring(0, 4) : "";
        }

        private static string DescribeRelease(JToken release)
        {
            var parts = new List<string>();
            string country = (string)release["country"];
            string date = (string)release["date"];
            var media = release["media"] as JArray;
            if (!string.IsNullOrEmpty(date)) { parts.Add(date); }
            if (!string.IsNullOrEmpty(country)) { parts.Add(country); }
            if (media != null && media.Count > 0)
            {
                string format = (string)media[0]["format"];
                parts.Add(media.Count + " disc(s)" + (string.IsNullOrEmpty(format) ? "" : " " + format));
            }
            int? trackCount = (int?)release["track-count"];
            if (trackCount.HasValue) { parts.Add(trackCount.Value + " tracks"); }
            return string.Join(" — ", parts);
        }
    }
}
