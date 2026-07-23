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

using System;
using System.Collections.Generic;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// The dashboard HOST's live roster: the latest snapshot from each reporting instance, keyed by
    /// its instance id, with a last-seen timestamp so an instance that stops reporting (powered off,
    /// crashed, updated) drops off the board instead of lingering as a stale "ripping…" tile.
    ///
    /// This is the same liveness idea the encode server already uses for ripper sessions, applied to
    /// status reporting. Thread-safe: many reporter connections write while the web server reads.
    /// </summary>
    public class DashboardRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _byInstance =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private sealed class Entry
        {
            public DashboardSnapshot Snapshot;
            public DateTime LastSeenUtc;
        }

        /// <summary>
        /// Record (or refresh) an instance's snapshot. Ignored if it has no instance id, so a
        /// malformed report can never create a ghost tile.
        /// </summary>
        public void Ingest(DashboardSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.InstanceId)) { return; }
            lock (_lock)
            {
                _byInstance[snapshot.InstanceId] = new Entry
                {
                    Snapshot = snapshot,
                    LastSeenUtc = DateTime.UtcNow
                };
            }
        }

        /// <summary>
        /// Every instance seen within <paramref name="maxAge"/>, ordered stably for a steady tile
        /// layout: Encoder Server(s) first, then everything else by machine name. Stale entries are
        /// both filtered out AND pruned from the map so it can't grow without bound.
        /// </summary>
        public List<DashboardSnapshot> LiveSnapshots(TimeSpan maxAge)
        {
            DateTime cutoff = DateTime.UtcNow - maxAge;
            var live = new List<DashboardSnapshot>();
            lock (_lock)
            {
                var stale = new List<string>();
                foreach (KeyValuePair<string, Entry> kv in _byInstance)
                {
                    if (kv.Value.LastSeenUtc >= cutoff) { live.Add(kv.Value.Snapshot); }
                    else { stale.Add(kv.Key); }
                }
                foreach (string key in stale) { _byInstance.Remove(key); }
            }

            live.Sort(CompareForDisplay);
            return live;
        }

        private static int CompareForDisplay(DashboardSnapshot a, DashboardSnapshot b)
        {
            // Encoder Server tiles sort first so a fleet's hub is always the top-left tile.
            bool aServer = string.Equals(a.Role, "Encoder Server", StringComparison.OrdinalIgnoreCase);
            bool bServer = string.Equals(b.Role, "Encoder Server", StringComparison.OrdinalIgnoreCase);
            if (aServer != bServer) { return aServer ? -1 : 1; }

            int byName = string.Compare(a.MachineName, b.MachineName, StringComparison.OrdinalIgnoreCase);
            if (byName != 0) { return byName; }
            return string.Compare(a.InstanceId, b.InstanceId, StringComparison.OrdinalIgnoreCase);
        }
    }
}
