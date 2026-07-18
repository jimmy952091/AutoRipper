/*
 * AutoRipper — rips and encodes physical media into Plex/Jellyfin-ready libraries.
 * Copyright (C) 2026 Heto <heto.black@gmail.com>
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the
 * GNU Affero General Public License as published by the Free Software Foundation, either version 3
 * of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
 * even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License along with this program.
 * If not, see <https://www.gnu.org/licenses/>.
 */
using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// Serializes incoming file transfers across multiple ripper clients: exactly ONE client may
    /// stream a file to the server at a time; the rest wait in a strict FIFO line. This keeps a
    /// multi-ripper setup from saturating the server's network/disk with interleaved 5-30 GB
    /// transfers — each rip arrives at full speed, in the order the clients asked.
    ///
    /// OWNERSHIP IS BY MACHINE NAME, NOT BY CONNECTION. A ripper that drops and reconnects gets a
    /// brand-new socket object, so connection-keyed ownership treated it as a stranger: it was
    /// appended to the BACK of the line while its old ticket lingered until the server noticed the
    /// death (up to the silence timeout). With several rippers reconnecting during one long slow
    /// transfer, the line filled with phantoms and the waiting machines' reported positions only
    /// ever CLIMBED (observed live: 11 -> 15 while one laptop uploaded over WiFi). Keyed by name,
    /// a reconnecting machine RECLAIMS its existing place — duplicates are impossible.
    ///
    /// Pure bookkeeping (no sockets), so the grant/queue/disconnect rules are unit-testable. All
    /// methods are thread-safe and return the notifications the caller should deliver — the gate
    /// itself never touches the wire.
    /// </summary>
    public class TransferGate
    {
        /// <summary>One notification to push to a client: either "go ahead" or "you are Nth in line".</summary>
        public class Notice
        {
            /// <summary>The client MACHINE NAME this notice is for (resolve to a live connection when sending).</summary>
            public string Owner;
            public string JobId;
            public bool Granted;
            /// <summary>1-based place in line when not granted (1 = next after the current transfer).</summary>
            public int Position;
        }

        private class Ticket
        {
            public string Owner;
            public string JobId;
        }

        private readonly object _lock = new object();
        private readonly Queue<Ticket> _waiting = new Queue<Ticket>();
        private string _holder;
        private string _holderJobId;

        private static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether <paramref name="owner"/> currently holds the transfer slot.</summary>
        public bool IsHolder(string owner)
        {
            lock (_lock) { return _holder != null && Same(_holder, owner); }
        }

        /// <summary>The machine currently transferring, or null. For the server UI/log.</summary>
        public string CurrentHolder
        {
            get { lock (_lock) { return _holder; } }
        }

        /// <summary>
        /// A client asks to send a file. If the slot is free it is granted immediately; otherwise
        /// the client is queued FIFO. The returned notice is for THIS requester.
        ///
        /// IDEMPOTENT PER MACHINE: one ripper holds at most ONE place in line, no matter how many
        /// times it re-asks (job resubmitted after a hiccup, or a whole new connection after a
        /// reconnect). Re-requesting UPDATES the existing ticket in place rather than adding one.
        /// </summary>
        public Notice Request(string owner, string jobId)
        {
            lock (_lock)
            {
                if (_holder != null && Same(_holder, owner))
                {
                    // Already ours (re-request after a resubmit/reconnect) — refresh the job id.
                    _holderJobId = jobId;
                    return new Notice { Owner = owner, JobId = jobId, Granted = true };
                }

                if (_holder == null)
                {
                    _holder = owner;
                    _holderJobId = jobId;
                    Monitor.PulseAll(_lock);
                    return new Notice { Owner = owner, JobId = jobId, Granted = true };
                }

                int position = 1;
                foreach (Ticket t in _waiting)
                {
                    if (Same(t.Owner, owner))
                    {
                        t.JobId = jobId; // same seat in line, newest job id
                        return new Notice { Owner = owner, JobId = jobId, Granted = false, Position = position };
                    }
                    position++;
                }

                _waiting.Enqueue(new Ticket { Owner = owner, JobId = jobId });
                return new Notice { Owner = owner, JobId = jobId, Granted = false, Position = _waiting.Count };
            }
        }

        /// <summary>
        /// Blocks until <paramref name="owner"/> holds the slot — the compatibility path for an
        /// older client that streams FILE_BEGIN without asking first. Its connection thread parks
        /// here and TCP backpressure stalls the sender until it's this client's turn.
        /// </summary>
        public void WaitUntilHolder(string owner, string jobId)
        {
            lock (_lock)
            {
                if (_holder != null && Same(_holder, owner)) { return; }
                if (_holder == null)
                {
                    _holder = owner;
                    _holderJobId = jobId;
                    return;
                }
                // Take (or refresh) a single place in line, then wait for it to come up.
                bool queued = false;
                foreach (Ticket t in _waiting)
                {
                    if (Same(t.Owner, owner)) { t.JobId = jobId; queued = true; break; }
                }
                if (!queued) { _waiting.Enqueue(new Ticket { Owner = owner, JobId = jobId }); }

                while (_holder == null || !Same(_holder, owner))
                {
                    Monitor.Wait(_lock);
                }
            }
        }

        /// <summary>
        /// The holder finished (or failed) its transfer. The slot passes to the next ticket in
        /// line; everyone still waiting gets a refreshed position. Returns the notices to deliver
        /// (possibly empty). A release by a non-holder is ignored (defensive).
        /// </summary>
        public List<Notice> Release(string owner)
        {
            lock (_lock)
            {
                if (_holder == null || !Same(_holder, owner)) { return new List<Notice>(); }
                _holder = null;
                _holderJobId = null;
                return PromoteNextLocked();
            }
        }

        /// <summary>
        /// A client is gone for good: drop its ticket, and if it held the slot, pass the slot on.
        /// Callers should NOT call this for a machine that has already reconnected under the same
        /// name — by design that machine keeps its place in line.
        /// Returns the notices to deliver to the remaining clients.
        /// </summary>
        public List<Notice> RemoveOwner(string owner)
        {
            lock (_lock)
            {
                // Rebuild the line without the departed client's ticket, preserving order.
                var keep = new List<Ticket>();
                foreach (Ticket t in _waiting)
                {
                    if (!Same(t.Owner, owner)) { keep.Add(t); }
                }
                bool lineChanged = keep.Count != _waiting.Count;
                _waiting.Clear();
                foreach (Ticket t in keep) { _waiting.Enqueue(t); }

                if (_holder != null && Same(_holder, owner))
                {
                    _holder = null;
                    _holderJobId = null;
                    return PromoteNextLocked();
                }

                // Holder unchanged, but positions shifted for whoever was behind the departed client.
                return lineChanged ? RepositionLocked() : new List<Notice>();
            }
        }

        /// <summary>Grants the slot to the head of the line and refreshes everyone else's position. Call under _lock.</summary>
        private List<Notice> PromoteNextLocked()
        {
            var notices = new List<Notice>();
            if (_waiting.Count > 0)
            {
                Ticket next = _waiting.Dequeue();
                _holder = next.Owner;
                _holderJobId = next.JobId;
                notices.Add(new Notice { Owner = next.Owner, JobId = next.JobId, Granted = true });
            }
            notices.AddRange(RepositionLocked());
            // Wake any legacy client thread parked in WaitUntilHolder.
            Monitor.PulseAll(_lock);
            return notices;
        }

        /// <summary>Position updates for everyone still waiting. Call under _lock.</summary>
        private List<Notice> RepositionLocked()
        {
            var notices = new List<Notice>();
            int position = 1;
            foreach (Ticket t in _waiting)
            {
                notices.Add(new Notice { Owner = t.Owner, JobId = t.JobId, Granted = false, Position = position });
                position++;
            }
            return notices;
        }
    }
}
