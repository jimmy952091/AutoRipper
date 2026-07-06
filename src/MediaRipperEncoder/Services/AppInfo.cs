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
        /// Folder-safe internal id, used for the %AppData% subfolder and the assembly name.
        /// Deliberately kept stable (NOT renamed to "AutoRipper") so existing users' saved
        /// settings/logs aren't orphaned and the build/test harnesses keep resolving the
        /// assembly. The user-facing name is <see cref="DisplayName"/>.
        /// </summary>
        public const string AppName = "MediaRipperEncoder";

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
        /// Makes sure the AppData folder exists. Safe to call repeatedly; does nothing
        /// if it's already there.
        /// </summary>
        public static void EnsureAppDataFolder()
        {
            Directory.CreateDirectory(AppDataFolder);
        }
    }
}
