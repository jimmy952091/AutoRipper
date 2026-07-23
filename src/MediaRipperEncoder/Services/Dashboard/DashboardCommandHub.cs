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
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// The dashboard HOST's command exchange for remote disc setup: a logged-in browser enqueues a
    /// command for an instance; the instance picks it up inside the response to its next status
    /// report (so commands need no listener on the instance side and flow through the existing,
    /// authenticated 2-second report cycle); the instance posts the result back; the browser polls
    /// for it.
    ///
    /// Timeouts are explicit so nothing hangs forever in the browser:
    ///  - a command not PICKED UP promptly fails ("instance isn't reporting") — the instance is
    ///    offline or was closed,
    ///  - a picked-up command that never returns fails after a generous window (a Blu-ray scan can
    ///    genuinely take minutes),
    ///  - finished results are kept briefly for the browser to collect, then pruned.
    /// Thread-safe: browser requests, report cycles, and result posts all land here concurrently.
    /// </summary>
    public class DashboardCommandHub
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly TimeSpan _pickupTimeout;
        private readonly TimeSpan _completionTimeout;
        private readonly TimeSpan _resultRetention;

        private enum State { Queued, Dispatched, Done }

        private sealed class Entry
        {
            public DashCommand Command;
            public State State;
            public DateTime ChangedUtc;     // when it entered the current state
            public bool Ok;
            public string Error = "";
            public JObject Result;
        }

        public DashboardCommandHub()
            : this(TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(6), TimeSpan.FromMinutes(10))
        {
        }

        /// <summary>Timeouts injectable for tests; production uses the defaults above.</summary>
        public DashboardCommandHub(TimeSpan pickupTimeout, TimeSpan completionTimeout, TimeSpan resultRetention)
        {
            _pickupTimeout = pickupTimeout;
            _completionTimeout = completionTimeout;
            _resultRetention = resultRetention;
        }

        /// <summary>Queues a command for an instance. Returns the command id the browser polls on.</summary>
        public string Enqueue(string instanceId, string action, JObject args)
        {
            var cmd = new DashCommand
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                InstanceId = instanceId ?? "",
                Action = action ?? "",
                Args = args ?? new JObject()
            };
            lock (_lock)
            {
                Sweep();
                _byId[cmd.Id] = new Entry { Command = cmd, State = State.Queued, ChangedUtc = DateTime.UtcNow };
            }
            return cmd.Id;
        }

        /// <summary>
        /// Hands over every queued command addressed to this instance (called while building the
        /// response to its status report). Dispatched commands start the completion clock.
        /// </summary>
        public List<DashCommand> TakeCommandsFor(string instanceId)
        {
            var taken = new List<DashCommand>();
            if (string.IsNullOrEmpty(instanceId)) { return taken; }
            lock (_lock)
            {
                Sweep();
                foreach (Entry e in _byId.Values)
                {
                    if (e.State == State.Queued &&
                        string.Equals(e.Command.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        e.State = State.Dispatched;
                        e.ChangedUtc = DateTime.UtcNow;
                        taken.Add(e.Command);
                    }
                }
            }
            return taken;
        }

        /// <summary>Records an instance's result for a command. Unknown/expired ids are ignored.</summary>
        public void Complete(DashCommandResult result)
        {
            if (result == null || string.IsNullOrEmpty(result.Id)) { return; }
            lock (_lock)
            {
                Entry e;
                if (!_byId.TryGetValue(result.Id, out e)) { return; }
                e.State = State.Done;
                e.ChangedUtc = DateTime.UtcNow;
                e.Ok = result.Ok;
                e.Error = result.Error ?? "";
                e.Result = result.Result ?? new JObject();
            }
        }

        /// <summary>
        /// Browser poll: the command's current status as a JSON-ready object:
        /// { found, done, ok, error, result }. A timed-out command reports done=true, ok=false.
        /// </summary>
        public JObject Query(string id)
        {
            var o = new JObject();
            lock (_lock)
            {
                Sweep();
                Entry e;
                if (id == null || !_byId.TryGetValue(id, out e))
                {
                    o["found"] = false;
                    o["done"] = true;
                    o["ok"] = false;
                    o["error"] = "That request expired or was never made.";
                    return o;
                }
                o["found"] = true;
                o["done"] = e.State == State.Done;
                o["ok"] = e.State == State.Done && e.Ok;
                o["error"] = e.Error;
                o["result"] = e.Result ?? new JObject();
                return o;
            }
        }

        /// <summary>
        /// Expires stale entries in place. Called under the lock from every public method, so no
        /// timer thread is needed — staleness only matters when someone is looking.
        /// </summary>
        private void Sweep()
        {
            DateTime now = DateTime.UtcNow;
            List<string> drop = null;
            foreach (KeyValuePair<string, Entry> kv in _byId)
            {
                Entry e = kv.Value;
                if (e.State == State.Queued && now - e.ChangedUtc > _pickupTimeout)
                {
                    e.State = State.Done;
                    e.Ok = false;
                    e.Error = "The machine didn't pick this up — it may be offline or not reporting to this dashboard.";
                    e.ChangedUtc = now;
                }
                else if (e.State == State.Dispatched && now - e.ChangedUtc > _completionTimeout)
                {
                    e.State = State.Done;
                    e.Ok = false;
                    e.Error = "The machine picked this up but never answered (timed out).";
                    e.ChangedUtc = now;
                }
                else if (e.State == State.Done && now - e.ChangedUtc > _resultRetention)
                {
                    if (drop == null) { drop = new List<string>(); }
                    drop.Add(kv.Key);
                }
            }
            if (drop != null)
            {
                foreach (string key in drop) { _byId.Remove(key); }
            }
        }
    }
}
