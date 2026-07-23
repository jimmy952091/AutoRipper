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

namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// A point-in-time status report for ONE AutoRipper instance, sent to the dashboard host and
    /// rendered as a tile in the web page. Deliberately schema-light (like NetMessage) so a newer
    /// reporter and an older host — or vice versa — degrade gracefully instead of failing to parse.
    ///
    /// SECURITY: this object is served to any authenticated browser on the LAN, so it must NEVER
    /// carry secrets. No shared secret, no OMDb/TheTVDB API keys, no full filesystem paths — only
    /// display labels and file NAMES. The dashboard tests assert this explicitly.
    /// </summary>
    public class DashboardSnapshot
    {
        /// <summary>
        /// Stable key identifying the reporting instance: machine name + role. Two instances on the
        /// same machine with different roles (unusual, but possible) stay distinct; a reconnecting
        /// instance reuses its key so it updates its tile rather than spawning a duplicate.
        /// </summary>
        [JsonProperty("instanceId")]
        public string InstanceId { get; set; }

        [JsonProperty("machineName")]
        public string MachineName { get; set; }

        /// <summary>"Standalone", "Encoder Server", or "Ripper" — human-readable role label.</summary>
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("appVersion")]
        public string AppVersion { get; set; }

        /// <summary>Unix seconds when this snapshot was produced (host uses it for liveness).</summary>
        [JsonProperty("sentAtUnix")]
        public long SentAtUnix { get; set; }

        // --- Rip side (blank/empty on a pure Encoder Server) ---

        [JsonProperty("rip")]
        public RipStatusView Rip { get; set; }

        // --- Encode side ---

        [JsonProperty("encode")]
        public EncodeStatusView Encode { get; set; }

        // --- Connection / role context ---

        /// <summary>
        /// For a Ripper: true if connected to its encoder server, false if reconnecting, null if the
        /// concept doesn't apply (Standalone / Encoder Server). Kept nullable so the tile can hide
        /// the indicator entirely rather than show a misleading "disconnected".
        /// </summary>
        [JsonProperty("remoteConnected")]
        public bool? RemoteConnected { get; set; }

        /// <summary>For a Ripper: the encoder host it targets. For a Server: a short roster string.</summary>
        [JsonProperty("connectionInfo")]
        public string ConnectionInfo { get; set; }

        /// <summary>Most recent error surfaced by this instance, or empty. Display only.</summary>
        [JsonProperty("lastError")]
        public string LastError { get; set; }

        /// <summary>
        /// True when this instance accepts disc setup from the dashboard (Phase B). Off for the
        /// Encoder Server role (no drive), and user-disableable per machine. The page only shows
        /// "Set up disc…" on tiles that advertise it.
        /// </summary>
        [JsonProperty("allowsRemoteSetup")]
        public bool AllowsRemoteSetup { get; set; }

        public DashboardSnapshot()
        {
            InstanceId = "";
            MachineName = "";
            Role = "";
            AppVersion = "";
            ConnectionInfo = "";
            LastError = "";
            Rip = new RipStatusView();
            Encode = new EncodeStatusView();
        }
    }

    /// <summary>Rip-queue summary for a tile: what's ripping now and how much is waiting.</summary>
    public class RipStatusView
    {
        /// <summary>True when a disc is actively ripping right now.</summary>
        [JsonProperty("active")]
        public bool Active { get; set; }

        /// <summary>Title/label of the disc or title currently ripping (display text).</summary>
        [JsonProperty("current")]
        public string Current { get; set; }

        [JsonProperty("percent")]
        public int Percent { get; set; }

        /// <summary>Short operation text ("Ripping title 2", "Ripped; disc ejected", etc.).</summary>
        [JsonProperty("operation")]
        public string Operation { get; set; }

        /// <summary>How many rip jobs are still queued (not counting the one in progress).</summary>
        [JsonProperty("queued")]
        public int Queued { get; set; }

        public RipStatusView()
        {
            Current = "";
            Operation = "";
        }
    }

    /// <summary>Encode-queue summary for a tile.</summary>
    public class EncodeStatusView
    {
        [JsonProperty("active")]
        public bool Active { get; set; }

        /// <summary>File NAME (not full path) currently encoding, or its display label.</summary>
        [JsonProperty("current")]
        public string Current { get; set; }

        [JsonProperty("percent")]
        public int Percent { get; set; }

        [JsonProperty("operation")]
        public string Operation { get; set; }

        [JsonProperty("queued")]
        public int Queued { get; set; }

        [JsonProperty("done")]
        public int Done { get; set; }

        [JsonProperty("failed")]
        public int Failed { get; set; }

        public EncodeStatusView()
        {
            Current = "";
            Operation = "";
        }
    }
}
