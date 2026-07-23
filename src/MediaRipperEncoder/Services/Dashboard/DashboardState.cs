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
using System.IO;
using System.Reflection;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// Maintains THIS instance's current status as a <see cref="DashboardSnapshot"/>, updated from
    /// the same rip/encode/connection events the main window already handles. Thread-safe: the
    /// pipeline raises its events on background threads, and the dashboard reporter reads the
    /// snapshot from its own timer thread, so every access is under one lock.
    ///
    /// Deliberately decoupled from the UI and the pipeline internals — the main form just forwards
    /// each event it already receives into the matching Update* method here. That keeps status
    /// collection from ever interfering with the actual ripping/encoding work.
    /// </summary>
    public class DashboardState
    {
        private readonly object _lock = new object();
        private readonly AppSettings _settings;

        // Minimal per-job summaries, snapshotted from the job at event time so we never read a
        // live job object that a background thread is mutating.
        private readonly Dictionary<Guid, RipEntry> _rip = new Dictionary<Guid, RipEntry>();
        private readonly Dictionary<Guid, EncEntry> _enc = new Dictionary<Guid, EncEntry>();

        private bool? _remoteConnected;
        private string _connectionInfo = "";
        private string _lastError = "";
        private volatile bool _allowsRemoteSetup;

        /// <summary>Set true by the main window when a RemoteSetupAgent is active on this instance.</summary>
        public bool AllowsRemoteSetup
        {
            get { return _allowsRemoteSetup; }
            set { _allowsRemoteSetup = value; }
        }

        private sealed class RipEntry
        {
            public RipStatus Status;
            public int Percent;
            public string Title = "";
            public string Operation = "";
        }

        private sealed class EncEntry
        {
            public EncodeStatus Status;
            public int Percent;
            public string Name = "";
            public string Operation = "";
        }

        public DashboardState(AppSettings settings)
        {
            _settings = settings;
        }

        // ---- feed methods (called from the main form's existing event handlers) ----

        public void UpdateRip(RipJob job)
        {
            if (job == null) { return; }
            lock (_lock)
            {
                _rip[job.Id] = new RipEntry
                {
                    Status = job.Status,
                    Percent = Clamp(job.ProgressPercent),
                    Title = RipTitleOf(job),
                    Operation = job.CurrentOperation ?? ""
                };
                RememberError(job.Error);
            }
        }

        public void UpdateEncode(EncodeJob job)
        {
            if (job == null) { return; }
            lock (_lock)
            {
                _enc[job.Id] = new EncEntry
                {
                    Status = job.Status,
                    Percent = Clamp(job.ProgressPercent),
                    Name = EncodeNameOf(job),
                    Operation = job.CurrentOperation ?? ""
                };
                RememberError(job.Error);
            }
        }

        /// <summary>Ripper role: connected to the encoder server, or reconnecting.</summary>
        public void SetRemoteConnection(bool connected, string info)
        {
            lock (_lock)
            {
                _remoteConnected = connected;
                if (info != null) { _connectionInfo = info; }
            }
        }

        /// <summary>Encoder Server role: a short roster string for the tile (e.g. "2/3 rippers").</summary>
        public void SetConnectionInfo(string info)
        {
            lock (_lock) { _connectionInfo = info ?? ""; }
        }

        // ---- read ----

        /// <summary>A fresh snapshot stamped with the current time. Never returns null.</summary>
        public DashboardSnapshot Snapshot()
        {
            lock (_lock)
            {
                var snap = new DashboardSnapshot
                {
                    MachineName = Environment.MachineName,
                    Role = RoleLabel(_settings.NodeRole),
                    AppVersion = VersionText(),
                    SentAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    RemoteConnected = _remoteConnected,
                    ConnectionInfo = _connectionInfo,
                    LastError = _lastError,
                    AllowsRemoteSetup = _allowsRemoteSetup
                };
                snap.InstanceId = snap.MachineName + " / " + snap.Role;

                // Rip section
                foreach (RipEntry r in _rip.Values)
                {
                    if (r.Status == RipStatus.Queued) { snap.Rip.Queued++; }
                    if (r.Status == RipStatus.Ripping && !snap.Rip.Active)
                    {
                        snap.Rip.Active = true;
                        snap.Rip.Current = r.Title;
                        snap.Rip.Percent = r.Percent;
                        snap.Rip.Operation = r.Operation;
                    }
                }

                // Encode section
                foreach (EncEntry e in _enc.Values)
                {
                    switch (e.Status)
                    {
                        case EncodeStatus.Queued: snap.Encode.Queued++; break;
                        case EncodeStatus.Completed: snap.Encode.Done++; break;
                        case EncodeStatus.Failed: snap.Encode.Failed++; break;
                    }
                    if (e.Status == EncodeStatus.Encoding && !snap.Encode.Active)
                    {
                        snap.Encode.Active = true;
                        snap.Encode.Current = e.Name;
                        snap.Encode.Percent = e.Percent;
                        snap.Encode.Operation = e.Operation;
                    }
                }

                return snap;
            }
        }

        // ---- helpers ----

        private void RememberError(string error)
        {
            if (!string.IsNullOrWhiteSpace(error)) { _lastError = error.Trim(); }
        }

        private static string RipTitleOf(RipJob job)
        {
            if (!string.IsNullOrWhiteSpace(job.DiscLabel)) { return job.DiscLabel; }
            return "Disc " + job.ShortId;
        }

        private static string EncodeNameOf(EncodeJob job)
        {
            if (!string.IsNullOrWhiteSpace(job.DisplayName)) { return job.DisplayName; }
            // Fall back to the input file NAME only — never the full path (may reveal folder layout).
            if (!string.IsNullOrWhiteSpace(job.InputFile))
            {
                try { return Path.GetFileName(job.InputFile); } catch { /* fall through */ }
            }
            return "Encode " + job.ShortId;
        }

        private static string RoleLabel(NodeRole role)
        {
            switch (role)
            {
                case NodeRole.EncoderServer: return "Encoder Server";
                case NodeRole.RipperClient: return "Ripper";
                default: return "Standalone";
            }
        }

        private static string VersionText()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                string text = v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
                return v.Revision > 0 ? text + "." + v.Revision.ToString("00") : text;
            }
            catch
            {
                return "";
            }
        }

        private static int Clamp(int p)
        {
            if (p < 0) { return 0; }
            if (p > 100) { return 100; }
            return p;
        }
    }
}
