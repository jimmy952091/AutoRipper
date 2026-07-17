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
using System.Windows.Forms;
using MediaRipperEncoder.Forms;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Carry settings over from the old MediaRipperEncoder AppData folder (pre-rename
            // builds) BEFORE the first settings load, or existing users would see the setup
            // wizard again after updating.
            AppInfo.MigrateLegacyAppData();

            Logger.Info("Application starting.");
            WarnIfStaleLooseCopy();
            AppSettings settings = SettingsStore.Load();
            ThemeManager.Initialize(settings.Theme);

            // "First run" = no saved settings yet, or setup was never completed. In either
            // case the user must go through the wizard before reaching the main window.
            bool firstRun = !SettingsStore.Exists() || !settings.SetupCompleted;

            // Welcome screen: always on first run; afterwards only if the user hasn't ticked
            // "Don't show this again".
            if (firstRun || settings.ShowWelcomeOnStartup)
            {
                using (var welcome = new WelcomeForm())
                {
                    if (welcome.ShowDialog() != DialogResult.OK)
                    {
                        Logger.Info("User closed the welcome screen; exiting.");
                        return;
                    }

                    // Persist the "don't show again" choice immediately.
                    settings.ShowWelcomeOnStartup = !welcome.DontShowAgain;
                    TrySave(settings);
                }
            }

            // First-run setup wizard. If the user cancels it, we can't run without configured
            // tools, so exit cleanly rather than opening a non-functional main window.
            if (firstRun)
            {
                using (var wizard = new SetupWizardForm(settings))
                {
                    if (wizard.ShowDialog() != DialogResult.OK)
                    {
                        Logger.Info("User cancelled first-run setup; exiting.");
                        return;
                    }
                }

                // The wizard saved the settings; reload the authoritative copy from disk.
                settings = SettingsStore.Load();
            }

            Logger.Info("Opening main window.");
            Application.Run(new MainForm(settings));
        }

        /// <summary>
        /// Warns when THIS exe is an outdated loose copy (an old copied folder) while a NEWER
        /// version is properly installed — the trap where a user updates via the MSI, then
        /// launches a stale shortcut and sees the old version in About, concluding the update
        /// failed. Quiet in every legitimate case: running the installed copy itself, running a
        /// same-or-newer loose copy (a dev build), or no MSI install present at all.
        /// </summary>
        private static void WarnIfStaleLooseCopy()
        {
            try
            {
                Version installed;
                string installLocation;
                if (!UninstallService.TryGetInstalledInfo(out installed, out installLocation)) { return; }

                Version running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (running >= installed) { return; }

                if (string.IsNullOrWhiteSpace(installLocation))
                {
                    // Installs made before InstallLocation was recorded — the MSI's fixed default.
                    installLocation = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AutoRipper");
                }
                string runningDir = System.IO.Path.GetDirectoryName(Application.ExecutablePath) ?? "";
                if (string.Equals(runningDir.TrimEnd('\\'), installLocation.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return; // we ARE the installed copy (mid-upgrade oddity); nothing to say
                }

                string exe = System.IO.Path.Combine(installLocation, "AutoRipper.exe");
                Logger.Info("Stale loose copy detected: running " + running + " from " + runningDir +
                            " while " + installed + " is installed at " + installLocation + ".");
                MessageBox.Show(
                    "This copy of AutoRipper is version " + Trim(running) + ", running from:\r\n" +
                    runningDir + "\r\n\r\n" +
                    "But version " + Trim(installed) + " is installed at:\r\n" + installLocation + "\r\n\r\n" +
                    "You are probably launching an old shortcut to a copied folder. Use the " +
                    "installed version (" + exe + ") and delete this old folder — updates only " +
                    "ever refresh the installed copy.",
                    "An update is installed — but this isn't it",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                // A version check must never stop the app from starting.
                Logger.Error("Loose-copy version check failed (continuing normally).", ex);
            }
        }

        private static string Trim(Version v)
        {
            return v.Major + "." + v.Minor + "." + Math.Max(v.Build, 0);
        }

        /// <summary>
        /// Saves settings but doesn't abort startup if the save fails — a failure to persist
        /// the welcome preference shouldn't stop the user from using the app.
        /// </summary>
        private static void TrySave(AppSettings settings)
        {
            try
            {
                SettingsStore.Save(settings);
            }
            catch (Exception ex)
            {
                Logger.Error("Non-fatal: couldn't save settings during startup.", ex);
            }
        }
    }
}
