using System;
using System.IO;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Central place for the app's identity and the on-disk locations it uses.
    /// Keeping these in one spot means nothing else has to hard-code folder names.
    /// </summary>
    public static class AppInfo
    {
        /// <summary>
        /// Folder-safe name for the %AppData% subfolder. Renamed to match the product (a binary
        /// that installs under one name and runs under another looks suspicious); settings saved
        /// by older builds are carried over by <see cref="MigrateLegacyAppData"/>.
        /// </summary>
        public const string AppName = "AutoRipper";

        /// <summary>The pre-rename AppData folder name (written by older builds).</summary>
        private const string LegacyAppName = "MediaRipperEncoder";

        /// <summary>Human-friendly product name shown in window titles and messages.</summary>
        public const string DisplayName = "AutoRipper";

        /// <summary>%AppData%\MediaRipperEncoder — where settings and logs live.</summary>
        public static string AppDataFolder
        {
            get
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(roaming, AppName);
            }
        }

        /// <summary>Full path to the JSON settings file.</summary>
        public static string SettingsFilePath
        {
            get { return Path.Combine(AppDataFolder, "settings.json"); }
        }

        /// <summary>Folder where daily log files are written.</summary>
        public static string LogFolder
        {
            get { return Path.Combine(AppDataFolder, "logs"); }
        }

        /// <summary>
        /// Full path to the pending-transfer outbox. A RipperClient records here every ripped file
        /// it still owes the encoder server, so closing the app (to install an update, say) never
        /// strands finished rips — they are picked back up on the next launch instead of having to
        /// be ripped all over again.
        /// </summary>
        public static string OutboxFilePath
        {
            get { return Path.Combine(AppDataFolder, "outbox.json"); }
        }

        /// <summary>
        /// Makes sure the AppData folder exists. Safe to call repeatedly; does nothing
        /// if it's already there.
        /// </summary>
        public static void EnsureAppDataFolder()
        {
            Directory.CreateDirectory(AppDataFolder);
        }

        /// <summary>
        /// One-time carry-over of settings/logs from the old %AppData%\MediaRipperEncoder folder
        /// into %AppData%\AutoRipper. COPIES rather than moves, so older builds still pointing at
        /// the old folder keep working during a mixed-version transition. Runs before settings are
        /// first loaded; if it fails, the app just starts with fresh defaults (never crashes).
        /// </summary>
        public static void MigrateLegacyAppData()
        {
            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string oldDir = Path.Combine(roaming, LegacyAppName);
                string newDir = Path.Combine(roaming, AppName);

                // Only migrate into a folder that doesn't exist yet — never overwrite settings
                // the new-name build has already written.
                if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) { return; }

                CopyTree(oldDir, newDir);
            }
            catch
            {
                // Fresh defaults are an acceptable fallback; a failed migration must not block startup.
            }
        }

        private static void CopyTree(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
            }
            foreach (string sub in Directory.GetDirectories(sourceDir))
            {
                CopyTree(sub, Path.Combine(destDir, Path.GetFileName(sub)));
            }
        }
    }
}
