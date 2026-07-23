// AutoRipper — automated disc ripping/encoding for Plex, Jellyfin, and other media servers.
// Copyright (C) 2026 James Spurgeon (heto.black@gmail.com)
//
// This program is free software: you can redistribute it and/or modify it under the terms of
// the GNU Affero General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License along with this
// program. If not, see <https://www.gnu.org/licenses/>.

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// Action names for remote disc setup (Phase B). String constants rather than an enum for the
    /// same reason as MsgType: an older instance receiving an unknown action fails that one command
    /// with a clear error instead of failing to parse the whole report cycle.
    /// </summary>
    public static class DashAction
    {
        public const string Scan = "scan";                 // scan the disc in this instance's drive/source
        public const string SearchMovies = "searchMovies"; // OMDb lookup with the instance's key
        public const string SearchSeries = "searchSeries"; // TheTVDB lookup with the instance's key
        public const string Episodes = "episodes";         // one season's episode list (+ preferred-language name)
        public const string MusicLookup = "musicLookup";   // MusicBrainz releases for the scanned audio CD
        public const string Process = "process";           // build the metadata package and queue the rip

        // Quick per-machine controls (no wizard): mirror the desktop buttons of the same names.
        public const string StopRip = "stopRip";           // stop the rip in progress (title marked failed)
        public const string RetryFailed = "retryFailed";   // re-queue every failed title
        public const string Eject = "eject";               // eject the disc tray
    }

    /// <summary>
    /// One remote-control command, created by the dashboard host when a logged-in browser asks an
    /// instance to do something, delivered to the instance inside the (signed) response to its next
    /// status report. Args is a free-form JSON object per action — schema-light like NetMessage.
    /// </summary>
    public class DashCommand
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>The instance this command is addressed to (its snapshot InstanceId).</summary>
        [JsonProperty("instanceId")]
        public string InstanceId { get; set; }

        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("args")]
        public JObject Args { get; set; }

        public DashCommand()
        {
            Id = "";
            InstanceId = "";
            Action = "";
            Args = new JObject();
        }

        public string ArgString(string key)
        {
            JToken t = Args != null ? Args[key] : null;
            return t == null || t.Type == JTokenType.Null ? "" : (string)t;
        }

        public int ArgInt(string key, int fallback = 0)
        {
            JToken t = Args != null ? Args[key] : null;
            if (t == null || t.Type == JTokenType.Null) { return fallback; }
            try { return (int)t; } catch { return fallback; }
        }

        public bool ArgBool(string key, bool fallback = false)
        {
            JToken t = Args != null ? Args[key] : null;
            if (t == null || t.Type == JTokenType.Null) { return fallback; }
            try { return (bool)t; } catch { return fallback; }
        }
    }

    /// <summary>
    /// The outcome of one command, posted back to the host by the instance (HMAC-signed like a
    /// status report) and then polled by the browser.
    /// </summary>
    public class DashCommandResult
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("ok")]
        public bool Ok { get; set; }

        /// <summary>Human-readable failure reason (shown verbatim in the browser). Empty on success.</summary>
        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>Action-specific payload (scan titles, candidates, episodes, queued-job label…).</summary>
        [JsonProperty("result")]
        public JObject Result { get; set; }

        public DashCommandResult()
        {
            Id = "";
            Error = "";
            Result = new JObject();
        }
    }
}
