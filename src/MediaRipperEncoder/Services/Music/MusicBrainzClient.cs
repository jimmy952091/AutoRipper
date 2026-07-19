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
        private const string UserAgent = "AutoRipper/0.2.4.05 (heto.black@gmail.com)";

        private readonly HttpClient _http;

        /// <summary>
        /// Set once the normal Windows TLS path has failed with a "secure channel" error and the
        /// BouncyCastle fallback transport (<see cref="LegacyTlsHttp"/>) worked — subsequent
        /// requests go straight to the fallback instead of re-failing first. Static because the
        /// OS capability it reflects is machine-wide, not per-client-instance.
        /// </summary>
        private static volatile bool _preferLegacyTransport;
        private static bool _legacySwitchLogged;

        /// <summary>
        /// The exception behind the most recent discid/TOC lookup that returned an empty list
        /// because of a NETWORK failure — null when the disc genuinely isn't in the database.
        /// Lets the UI distinguish "unknown disc, try typing it" (normal) from "couldn't reach
        /// MusicBrainz at all" (e.g. the Windows 7 TLS limitation), which need different advice.
        /// </summary>
        public Exception LastLookupFailure { get; private set; }

        public MusicBrainzClient(HttpClient http = null)
        {
            _http = http ?? SharedHttp.Client;
        }

        // ---- lookups ----

        /// <summary>Exact lookup by Disc ID. Empty list = disc not in the database (use the fallback).</summary>
        public async Task<List<MusicRelease>> LookupByDiscIdAsync(string discId)
        {
            // NOTE: inc must be exactly this — adding "discids" here is HTTP 400 on the discid
            // resource (verified against the live API), and the disc lists come back by default.
            string url = BaseUrl + "/discid/" + Uri.EscapeDataString(discId ?? "") +
                         "?fmt=json&inc=artist-credits+recordings";
            string json;
            LastLookupFailure = null;
            try
            {
                json = await GetAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A 404 here just means "unknown disc" — that's the normal fallback path, not an
                // error. Anything else (TLS, missing root cert, DNS) gets the FULL exception chain
                // logged: the outer message alone ("An error occurred while sending the request")
                // told us nothing when a Win7 machine couldn't reach MusicBrainz.
                Logger.Info("MusicBrainz discid lookup returned nothing: " + DescribeChain(ex));
                if (!ex.Message.Contains("(HTTP")) { LastLookupFailure = ex; }
                return new List<MusicRelease>();
            }
            return ParseDiscIdResponse(json, discId);
        }

        /// <summary>
        /// Fuzzy TOC lookup: when the exact Disc ID is unknown (this specific pressing was never
        /// submitted — common for budget reissues), MusicBrainz can still match the disc by its
        /// raw table of contents against similar track layouts. Automatic second step after a
        /// Disc ID miss, before asking the user to type anything.
        /// </summary>
        public async Task<List<MusicRelease>> LookupByTocAsync(Models.AudioCdToc toc)
        {
            // toc format: firstTrack+trackCount+leadout+offset1+offset2+...
            var sb = new System.Text.StringBuilder();
            sb.Append(toc.FirstTrack).Append('+').Append(toc.TrackCount).Append('+').Append(toc.LeadOutOffset);
            foreach (int offset in toc.TrackOffsets) { sb.Append('+').Append(offset); }

            string url = BaseUrl + "/discid/-?toc=" + sb +
                         "&fmt=json&inc=artist-credits+recordings";
            string json;
            LastLookupFailure = null;
            try
            {
                json = await GetAsync(url).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Info("MusicBrainz TOC lookup returned nothing: " + DescribeChain(ex));
                if (!ex.Message.Contains("(HTTP")) { LastLookupFailure = ex; }
                return new List<MusicRelease>();
            }
            return ParseDiscIdResponse(json, null);
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
            // The /release resource DOES accept discids in inc (unlike /discid — see above).
            string url = BaseUrl + "/release/" + Uri.EscapeDataString(releaseId ?? "") +
                         "?fmt=json&inc=artist-credits+recordings+discids";
            string json = await GetAsync(url).ConfigureAwait(false);
            return ParseRelease(JObject.Parse(json), discIdToMatch);
        }

        /// <summary>Front cover art (500px) for a release, or null when the archive has none.</summary>
        public async Task<byte[]> GetCoverArtAsync(string releaseId)
        {
            string url = CoverArtUrl + Uri.EscapeDataString(releaseId ?? "") + "/front-500";
            try
            {
                if (!_preferLegacyTransport)
                {
                    try
                    {
                        using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            req.Headers.Add("User-Agent", UserAgent);
                            HttpResponseMessage resp = await _http.SendAsync(req).ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode) { return null; }
                            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (LegacyTlsHttp.LooksLikeSecureChannelFailure(ex))
                    {
                        SwitchToLegacyTransport(ex);
                    }
                }

                // Windows TLS can't reach these hosts (Win7) — fetch over the built-in
                // BouncyCastle transport instead. It follows the archive.org redirect itself.
                LegacyTlsHttp.FetchResult result = await Task.Run(
                    () => LegacyTlsHttp.Get(url, UserAgent)).ConfigureAwait(false);
                return result.StatusCode >= 200 && result.StatusCode < 300 ? result.Body : null;
            }
            catch (Exception ex)
            {
                Logger.Info("Cover art unavailable for " + releaseId + " (" + ex.Message + ").");
                return null;
            }
        }

        /// <summary>
        /// Flattens an exception chain into one diagnosable line — the inner exceptions are where
        /// HTTPS failures explain themselves (e.g. "The remote certificate is invalid" = a missing
        /// root certificate on old Windows; "secure channel" = TLS negotiation).
        /// </summary>
        public static string DescribeChain(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            Exception current = ex;
            while (current != null)
            {
                if (sb.Length > 0) { sb.Append(" -> "); }
                sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
                current = current.InnerException;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Turns a lookup failure into a message the user can act on. The big case: Windows 7's
        /// SChannel lacks the modern cipher suites musicbrainz.org requires (they were added in
        /// Windows 8), so a FULLY UPDATED Win7 box still fails the TLS handshake with "Could not
        /// create SSL/TLS secure channel". Confirmed on real fully-patched Win7 hardware
        /// (2026-07-15). .NET delegates TLS to Windows, so no app-side fix exists — say that
        /// plainly instead of parroting the raw error and letting the user chase a ghost.
        /// </summary>
        public static string FriendlyLookupError(Exception ex)
        {
            string chain = DescribeChain(ex);
            bool tlsFailure = chain.IndexOf("secure channel", StringComparison.OrdinalIgnoreCase) >= 0;

            // 6.1 = Windows 7 / Server 2008 R2. A net48 app without a Win8+ manifest can see 6.2
            // reported on anything newer, but it only ever sees 6.1 when it really is Windows 7.
            Version os = Environment.OSVersion.Version;
            bool isWindows7 = os.Major == 6 && os.Minor <= 1;

            if (tlsFailure && isWindows7)
            {
                // Windows 7's SChannel can't reach MusicBrainz, and normally AutoRipper's
                // built-in modern-TLS fallback (LegacyTlsHttp) takes over silently — so if this
                // message is showing, the FALLBACK failed too (network down, firewall, proxy).
                return "Couldn't reach MusicBrainz. Windows 7's own secure-connection support " +
                       "can't talk to it (an OS limit), and AutoRipper's built-in fallback " +
                       "connection also failed — check your internet connection and the log " +
                       "(%AppData%\\AutoRipper\\logs) for details.";
            }
            if (tlsFailure)
            {
                return "Couldn't make a secure connection to MusicBrainz (TLS handshake failed). " +
                       "Details: " + chain;
            }
            return chain;
        }

        private async Task<string> GetAsync(string url)
        {
            if (!_preferLegacyTransport)
            {
                try
                {
                    return await GetViaWindowsTlsAsync(url).ConfigureAwait(false);
                }
                catch (Exception ex) when (LegacyTlsHttp.LooksLikeSecureChannelFailure(ex))
                {
                    // Windows' own TLS can't negotiate with this server (Windows 7's SChannel
                    // lacks the needed cipher suites). Retry over the built-in BouncyCastle
                    // transport — the "translator" — and stay on it for future requests.
                    SwitchToLegacyTransport(ex);
                }
            }

            LegacyTlsHttp.FetchResult result = await Task.Run(
                () => LegacyTlsHttp.Get(url, UserAgent, "application/json")).ConfigureAwait(false);
            if (result.StatusCode < 200 || result.StatusCode >= 300)
            {
                // Same message shape as the normal path, so "(HTTP 404)" keeps meaning
                // "unknown disc, use the fallback lookup" to every caller.
                throw new Exception("MusicBrainz request failed (HTTP " + result.StatusCode + ").");
            }
            return result.BodyText;
        }

        private async Task<string> GetViaWindowsTlsAsync(string url)
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

        private static void SwitchToLegacyTransport(Exception cause)
        {
            _preferLegacyTransport = true;
            if (!_legacySwitchLogged)
            {
                _legacySwitchLogged = true;
                Logger.Info("MusicBrainz: Windows' TLS can't reach the server (" + cause.Message +
                            ") — switching to AutoRipper's built-in modern-TLS transport. This is " +
                            "expected on Windows 7; lookups will work normally through it.");
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
