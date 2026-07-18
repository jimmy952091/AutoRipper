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
using MediaRipperEncoder.Models;
using Newtonsoft.Json;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Disk-backed record of the encode queue's UNFINISHED work, so an unexpected exit never
    /// strands encodes.
    ///
    /// Why this exists: the queue was memory-only. If AutoRipper closed for ANY reason — a
    /// Windows update reboot overnight, a power cut, a crash, or simply installing a new version —
    /// every queued encode was lost. The ripped files were still sitting in the scratch folder,
    /// but nothing remembered what they were or where they belonged, and this project's rule is
    /// that metadata is never re-derived from a filename. The user had to redo the work by hand.
    /// This hits a STANDALONE user hardest (one machine, no second copy of the queue anywhere),
    /// which is the most common way the app is run.
    ///
    /// Both roles share this: the local pipeline and the encoder-server node each own an
    /// <see cref="EncodeQueue"/>, so they persist to separate files (see <paramref name="role"/>)
    /// and recover independently.
    ///
    /// Only PENDING work is saved (queued, plus anything that was mid-encode when we died — its
    /// partial output is discarded and it re-runs from the start). Finished and failed jobs are
    /// history for the re-encode list, not work owed, so they are not resumed.
    /// </summary>
    public static class EncodeQueueStore
    {
        private static readonly object FileLock = new object();

        /// <summary>Path of the queue file for a role ("local" or "server").</summary>
        public static string PathFor(string role)
        {
            string safe = string.IsNullOrWhiteSpace(role) ? "local" : role.Trim().ToLowerInvariant();
            return Path.Combine(AppInfo.AppDataFolder, "encodequeue-" + safe + ".json");
        }

        /// <summary>
        /// Reads the unfinished jobs saved by the last session. Jobs whose INPUT file no longer
        /// exists are dropped (there is nothing left to encode); everything else comes back as
        /// Queued, ready to re-run. Returns empty on any error — recovery must never block startup.
        /// </summary>
        public static List<EncodeJob> Load(string role)
        {
            var usable = new List<EncodeJob>();
            try
            {
                string path = PathFor(role);
                if (!File.Exists(path)) { return usable; }

                string json;
                lock (FileLock) { json = File.ReadAllText(path); }

                var loaded = JsonConvert.DeserializeObject<List<EncodeJob>>(json);
                if (loaded == null) { return usable; }

                int missing = 0;
                foreach (EncodeJob job in loaded)
                {
                    if (job == null || string.IsNullOrEmpty(job.InputFile)) { continue; }
                    if (!File.Exists(job.InputFile))
                    {
                        missing++;
                        Logger.Info("Encode queue: dropping resumed job '" + job.DisplayName +
                                    "' — its source file is gone (" + job.InputFile + ").");
                        continue;
                    }
                    // Anything interrupted mid-encode restarts cleanly; its partial staging file
                    // is simply overwritten by the new run.
                    job.Status = EncodeStatus.Queued;
                    job.ProgressPercent = 0;
                    job.Error = "";
                    job.CurrentOperation = "Recovered from the previous session";
                    usable.Add(job);
                }

                if (usable.Count > 0 || missing > 0)
                {
                    Logger.Info("Encode queue (" + role + "): recovered " + usable.Count +
                                " unfinished encode(s) from the last session" +
                                (missing > 0 ? " (" + missing + " skipped — source file missing)" : "") + ".");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Encode queue (" + role + "): couldn't read the saved queue; starting empty.", ex);
                return new List<EncodeJob>();
            }
            return usable;
        }

        /// <summary>Records the unfinished jobs. Best-effort — never throws to the caller.</summary>
        public static void Save(string role, IEnumerable<EncodeJob> jobs)
        {
            try
            {
                AppInfo.EnsureAppDataFolder();
                var list = new List<EncodeJob>(jobs ?? new List<EncodeJob>());
                string json = JsonConvert.SerializeObject(list, Formatting.Indented);

                // Temp-file-and-swap: an interrupted write can't leave an unreadable queue behind.
                string path = PathFor(role);
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
                Logger.Error("Encode queue (" + role + "): couldn't save the queue (encoding continues).", ex);
            }
        }
    }
}
