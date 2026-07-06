using System;
using System.IO;
using MediaRipperEncoder.Models;
using Newtonsoft.Json;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> as human-readable JSON in %AppData%.
    ///
    /// Design choices, both driven by the "reliability over cleverness" standard:
    ///  - Load never throws. A missing or corrupt settings file returns fresh defaults
    ///    (and logs the problem) rather than crashing the app on startup.
    ///  - Save writes to a temporary file first and then swaps it into place, so a crash
    ///    or power loss mid-write can't leave a half-written settings file that would be
    ///    unreadable next launch.
    /// </summary>
    public static class SettingsStore
    {
        /// <summary>True if a settings file already exists on disk.</summary>
        public static bool Exists()
        {
            return File.Exists(AppInfo.SettingsFilePath);
        }

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(AppInfo.SettingsFilePath))
                {
                    return new AppSettings();
                }

                string json = File.ReadAllText(AppInfo.SettingsFilePath);
                AppSettings settings = JsonConvert.DeserializeObject<AppSettings>(json);

                // DeserializeObject returns null for an empty/whitespace file.
                return settings ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to read settings; falling back to defaults.", ex);
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            try
            {
                AppInfo.EnsureAppDataFolder();

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);

                // Write to a temp file in the same folder, then replace atomically. Writing
                // to the same folder keeps it on the same volume so the move is a fast,
                // safe rename rather than a cross-drive copy.
                string finalPath = AppInfo.SettingsFilePath;
                string tempPath = finalPath + ".tmp";

                File.WriteAllText(tempPath, json);

                if (File.Exists(finalPath))
                {
                    // File.Replace swaps temp -> final in one step and cleans up the temp.
                    File.Replace(tempPath, finalPath, null);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }

                Logger.Info("Settings saved to " + finalPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to save settings.", ex);
                // Rethrow so the UI can tell the user their settings didn't persist,
                // rather than silently pretending the save worked.
                throw;
            }
        }
    }
}
