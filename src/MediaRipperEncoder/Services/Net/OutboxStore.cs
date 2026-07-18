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
using System.IO;
using MediaRipperEncoder.Services;
using Newtonsoft.Json;

namespace MediaRipperEncoder.Services.Net
{
    /// <summary>
    /// One ripped file still owed to the encoder server: the confirmed metadata package plus the
    /// local path of the file to send.
    /// </summary>
    public class OutboxEntry
    {
        public RemoteEncodeRequest Request { get; set; }
        public string FilePath { get; set; }
    }

    /// <summary>
    /// Disk-backed record of the RipperClient's pending transfers, so a restart never loses
    /// finished work.
    ///
    /// Why this exists: the outbox used to be memory-only. Closing AutoRipper — which is exactly
    /// what installing an update requires — discarded every rip that hadn't finished uploading.
    /// The .mkv files were still on disk, but the app no longer knew they existed OR which
    /// confirmed movie/episode they were, and the project rule is that metadata must never be
    /// re-derived from a filename. The only way back was to rip the disc again. Persisting the
    /// queue turns that into a non-event: the jobs are simply resumed on the next launch.
    ///
    /// Design notes:
    ///  - Written on every change (queue depth is tiny; a few KB of JSON), so a crash or a
    ///    power cut is no worse than a clean exit.
    ///  - Saves are best-effort and NEVER throw into the caller: failing to record the queue
    ///    must not break an in-flight transfer.
    ///  - Load drops entries whose file has since vanished (user cleared the scratch folder),
    ///    saying so in the log rather than retrying something that can't succeed.
    /// </summary>
    public static class OutboxStore
    {
        private static readonly object FileLock = new object();

        /// <summary>Reads the persisted outbox. Returns an empty list when there's nothing (or on any error).</summary>
        public static List<OutboxEntry> Load()
        {
            var usable = new List<OutboxEntry>();
            try
            {
                string path = AppInfo.OutboxFilePath;
                if (!File.Exists(path)) { return usable; }

                string json;
                lock (FileLock) { json = File.ReadAllText(path); }

                var loaded = JsonConvert.DeserializeObject<List<OutboxEntry>>(json);
                if (loaded == null) { return usable; }

                int missing = 0;
                foreach (OutboxEntry entry in loaded)
                {
                    if (entry == null || entry.Request == null || string.IsNullOrEmpty(entry.FilePath))
                    {
                        continue;
                    }
                    if (!File.Exists(entry.FilePath))
                    {
                        // The ripped file is gone (scratch folder cleared, drive unplugged). We
                        // can't send what doesn't exist — drop it rather than retry forever.
                        missing++;
                        Logger.Info("Outbox: dropping resumed job " + entry.Request.ClientJobId +
                                    " — its ripped file is no longer on disk (" + entry.FilePath + ").");
                        continue;
                    }
                    usable.Add(entry);
                }

                if (usable.Count > 0 || missing > 0)
                {
                    Logger.Info("Outbox: resumed " + usable.Count + " pending transfer(s) from the last session" +
                                (missing > 0 ? " (" + missing + " skipped — file missing)" : "") + ".");
                }
            }
            catch (Exception ex)
            {
                // A corrupt/unreadable outbox must never stop the app from starting.
                Logger.Error("Outbox: couldn't read the saved transfer queue; starting empty.", ex);
                return new List<OutboxEntry>();
            }
            return usable;
        }

        /// <summary>Records the current pending transfers. Best-effort — never throws to the caller.</summary>
        public static void Save(IEnumerable<OutboxEntry> entries)
        {
            try
            {
                AppInfo.EnsureAppDataFolder();
                var list = new List<OutboxEntry>(entries ?? new List<OutboxEntry>());
                string json = JsonConvert.SerializeObject(list, Formatting.Indented);

                // Write to a temp file and swap, so an interrupted write can't leave a half-file
                // that would be unreadable on the next launch.
                string path = AppInfo.OutboxFilePath;
                string temp = path + ".tmp";
                lock (FileLock)
                {
                    File.WriteAllText(temp, json);
                    if (File.Exists(path)) { File.Delete(path); }
                    File.Move(temp, path);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Outbox: couldn't save the pending transfer queue (transfers continue).", ex);
            }
        }
    }
}
