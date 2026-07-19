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
using System.IO.Compression;
using System.Reflection;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Bundles everything needed to diagnose a problem into ONE .zip on the user's Desktop, ready
    /// to drag onto a GitHub issue.
    ///
    /// Why a zip specifically: GitHub does NOT accept `.log` or `.json` attachments — a user
    /// trying to attach a log file is told the type isn't supported, and usually gives up or
    /// pastes a wall of text instead. A `.zip` is accepted, so packaging everything into one is
    /// the difference between getting diagnostics and not.
    ///
    /// PRIVACY: a GitHub issue is PUBLIC, and these files name the user's machine, folder paths
    /// and every disc they've ripped. Nothing is uploaded by this class — it only writes a file
    /// the user chooses to share — and the UI lists exactly what goes in before creating it.
    /// settings.json is deliberately NEVER included: that's where the OMDb/TheTVDB API keys live.
    /// </summary>
    public static class DiagnosticsPackager
    {
        /// <summary>How many days of daily logs to include. Recent ones are what actually matter.</summary>
        private const int DefaultLogDays = 3;

        /// <summary>One file that will be included, for showing the user before anything is written.</summary>
        public class Item
        {
            public string Name;
            public string FullPath;
            public long Bytes;
        }

        /// <summary>
        /// Lists what a report would contain, WITHOUT writing anything — so the UI can show the
        /// user exactly what they'd be sharing before they commit to it.
        /// </summary>
        public static List<Item> Preview(int logDays = DefaultLogDays)
        {
            var items = new List<Item>();
            TryAdd(items, AppInfo.OutboxFilePath, "outbox.json");
            TryAdd(items, EncodeQueueStore.PathFor("local"), "encodequeue-local.json");
            TryAdd(items, EncodeQueueStore.PathFor("server"), "encodequeue-server.json");

            foreach (string log in RecentLogs(logDays))
            {
                TryAdd(items, log, "logs/" + Path.GetFileName(log));
            }
            return items;
        }

        /// <summary>
        /// Writes the report zip to the Desktop and returns its full path. Includes a summary of
        /// the app/OS/role so the report is useful even before anyone opens the logs.
        /// </summary>
        public static string CreateReport(AppSettings settings, string userDescription, int logDays = DefaultLogDays)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string name = "AutoRipper-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip";
            string zipPath = Path.Combine(desktop, name);

            using (FileStream stream = File.Create(zipPath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteTextEntry(zip, "report.txt", BuildSummary(settings, userDescription));

                foreach (Item item in Preview(logDays))
                {
                    try
                    {
                        zip.CreateEntryFromFile(item.FullPath, item.Name);
                    }
                    catch (Exception ex)
                    {
                        // A locked or vanished file must not sink the whole report.
                        WriteTextEntry(zip, item.Name + ".MISSING.txt",
                            "Couldn't include this file: " + ex.Message);
                    }
                }
            }

            Logger.Info("Diagnostics report written to " + zipPath + ".");
            return zipPath;
        }

        /// <summary>The human-readable header: versions, role, and the user's own description.</summary>
        public static string BuildSummary(AppSettings settings, string userDescription)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("AutoRipper diagnostics report");
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            sb.AppendLine("What the user reported:");
            sb.AppendLine(string.IsNullOrWhiteSpace(userDescription) ? "(nothing written)" : userDescription.Trim());
            sb.AppendLine();

            sb.AppendLine("AutoRipper version: " + VersionString());
            sb.AppendLine("Windows: " + Environment.OSVersion.Version + " (" +
                          (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + ")");
            sb.AppendLine(".NET runtime: " + Environment.Version);
            sb.AppendLine();

            if (settings != null)
            {
                sb.AppendLine("Role: " + settings.NodeRole);
                if (settings.NodeRole != NodeRole.Standalone)
                {
                    sb.AppendLine("  Server host: " + Describe(settings.NodeServerHost));
                    sb.AppendLine("  Port: " + settings.NodePort);
                    sb.AppendLine("  Max ripper clients: " + settings.NodeMaxClients);
                    sb.AppendLine("  (shared secret deliberately NOT included)");
                }
                sb.AppendLine("MakeMKV configured: " + YesNo(settings.MakeMkvCliPath));
                sb.AppendLine("HandBrakeCLI configured: " + YesNo(settings.HandBrakeCliPath));
                sb.AppendLine("Presets — general: " + YesNo(settings.HandBrakePresetPath) +
                              ", animation: " + YesNo(settings.HandBrakeAnimationPresetPath) +
                              ", UHD: " + YesNo(settings.HandBrakeUhdPresetPath) +
                              ", UHD animation: " + YesNo(settings.HandBrakeUhdAnimationPresetPath));
                sb.AppendLine("OMDb key set: " + YesNo(settings.OmdbApiKey) +
                              " | TheTVDB key set: " + YesNo(settings.TheTvdbApiKey) +
                              "   (the keys themselves are NOT included)");
                sb.AppendLine("Delete scratch after hand-off: " + settings.DeleteScratchAfterHandoff);
                sb.AppendLine("Network rip source enabled: " + settings.NetworkRipEnabled);
            }

            sb.AppendLine();
            sb.AppendLine("NOTE: if this is a multi-machine (fleet) setup, the encoder SERVER's own");
            sb.AppendLine("logs are usually needed too — generate a report on that machine as well.");
            return sb.ToString();
        }

        // ---- helpers ----

        /// <summary>The newest daily log files, most recent first.</summary>
        private static List<string> RecentLogs(int days)
        {
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(AppInfo.LogFolder)) { return found; }
                var files = new List<string>(Directory.GetFiles(AppInfo.LogFolder, "app-*.log"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                files.Reverse();                       // names are date-ordered, so newest first
                int take = Math.Max(1, days);
                for (int i = 0; i < files.Count && i < take; i++) { found.Add(files[i]); }
            }
            catch (Exception ex)
            {
                Logger.Info("Diagnostics: couldn't list the log folder (" + ex.Message + ").");
            }
            return found;
        }

        private static void TryAdd(List<Item> items, string path, string entryName)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return; }
                items.Add(new Item { Name = entryName, FullPath = path, Bytes = new FileInfo(path).Length });
            }
            catch { /* a file we can't stat simply isn't offered */ }
        }

        private static void WriteTextEntry(ZipArchive zip, string entryName, string text)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName);
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(text);
            }
        }

        private static string VersionString()
        {
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                string text = v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
                return v.Revision > 0 ? text + "." + v.Revision.ToString("00") : text;
            }
            catch { return "(unknown)"; }
        }

        private static string YesNo(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "no" : "yes";
        }

        /// <summary>Host names can be personal; include it but keep it obviously optional to redact.</summary>
        private static string Describe(string host)
        {
            return string.IsNullOrWhiteSpace(host) ? "(not set)" : host;
        }
    }
}
