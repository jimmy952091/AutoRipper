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

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Tidies raw rips out of the scratch folder once they are safely dealt with, so a machine
    /// that rips disc after disc doesn't slowly fill its drive and force the user to clean up by
    /// hand between discs.
    ///
    /// This deletes real user files, so it is deliberately paranoid:
    ///  - it refuses to touch anything that is not INSIDE the configured scratch folder, so a
    ///    mis-set path or an odd job can never reach into a library or anywhere else;
    ///  - it never removes the scratch folder itself, only an emptied per-rip subfolder;
    ///  - every deletion is logged, and any failure is swallowed (housekeeping must never break
    ///    a rip or an encode).
    /// Callers must only invoke this AFTER the file's content is safe somewhere else — encoded
    /// and placed in the library, or fully transferred and checksum-verified by the server.
    /// </summary>
    public static class ScratchCleaner
    {
        private const char Sep = '\\';

        /// <summary>
        /// Deletes <paramref name="filePath"/> if it lives under <paramref name="scratchRoot"/>,
        /// then removes its parent folder if that leaves it empty. Returns true if the file was
        /// deleted. Safe to call with anything — it simply declines when the rules aren't met.
        /// </summary>
        public static bool RemoveHandledRip(string filePath, string scratchRoot, string reason)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(scratchRoot)) { return false; }

            try
            {
                if (!File.Exists(filePath)) { return false; }
                if (!IsInsideFolder(filePath, scratchRoot))
                {
                    // Not our scratch space — leave it strictly alone.
                    Logger.Info("Scratch cleanup skipped (outside the scratch folder): " + filePath);
                    return false;
                }

                string folder = Path.GetDirectoryName(filePath);
                File.Delete(filePath);
                Logger.Info("Scratch cleanup: deleted " + filePath + " (" + reason + ").");

                RemoveFolderIfEmpty(folder, scratchRoot);
                return true;
            }
            catch (Exception ex)
            {
                // Never let housekeeping disturb the pipeline — the file simply stays.
                Logger.Info("Scratch cleanup couldn't delete " + filePath + " (" + ex.Message + "); leaving it.");
                return false;
            }
        }

        /// <summary>
        /// Sweeps the per-job staging folder an encoded file was just moved OUT of. The finished
        /// file goes to the library, leaving `Scratch\enc_&lt;id&gt;` empty behind it; without this
        /// those empty folders accumulate one per encode forever. Does nothing when the folder
        /// still holds anything, is outside the scratch root, or IS the scratch root.
        /// </summary>
        public static void RemoveEmptyStagingFolder(string stagedFilePath, string scratchRoot)
        {
            if (string.IsNullOrWhiteSpace(stagedFilePath) || string.IsNullOrWhiteSpace(scratchRoot)) { return; }
            try { RemoveFolderIfEmpty(Path.GetDirectoryName(stagedFilePath), scratchRoot); }
            catch { /* housekeeping is never allowed to disturb the pipeline */ }
        }

        /// <summary>Removes a now-empty per-rip subfolder, never the scratch root itself.</summary>
        private static void RemoveFolderIfEmpty(string folder, string scratchRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { return; }
                if (SamePath(folder, scratchRoot)) { return; }        // never the root itself
                if (!IsInsideFolder(folder, scratchRoot)) { return; }

                // Anything still inside (another title from the same disc, say) means keep it.
                using (IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator())
                {
                    if (entries.MoveNext()) { return; }
                }

                Directory.Delete(folder);
                Logger.Info("Scratch cleanup: removed the now-empty folder " + folder + ".");
            }
            catch (Exception ex)
            {
                Logger.Info("Scratch cleanup left the folder " + folder + " in place (" + ex.Message + ").");
            }
        }

        /// <summary>True when <paramref name="path"/> resolves to somewhere beneath <paramref name="root"/>.</summary>
        public static bool IsInsideFolder(string path, string root)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string fullRoot = Path.GetFullPath(root);
                if (fullRoot.Length == 0) { return false; }
                if (fullRoot[fullRoot.Length - 1] != Sep) { fullRoot += Sep; }
                return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Sep),
                    Path.GetFullPath(b).TrimEnd(Sep),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
