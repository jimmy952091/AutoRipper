using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Clean, complete uninstall — the "leave nothing behind" path the user asked for after Revo
    /// found leftovers a plain MSI uninstall left. The MSI removes program files + shortcuts, but
    /// by convention leaves user data (settings/logs) and per-user HKCU markers; this fills that
    /// gap so an opt-in full removal really does erase everything AutoRipper created.
    /// </summary>
    public static class UninstallService
    {
        // Every registry/AppData location AutoRipper (or its pre-rename self) has ever created.
        private static readonly string[] AppDataNames = { "AutoRipper", "MediaRipperEncoder" };
        private static readonly string[] HkcuSubKeys = { @"Software\AutoRipper", @"Software\MediaRipperEncoder" };

        /// <summary>
        /// Deletes AutoRipper's user data: both the current and legacy %AppData% folders (settings
        /// + logs) and both HKCU registry markers. Best-effort per item — one failure never stops
        /// the rest. Returns a human summary of what was removed / couldn't be.
        /// </summary>
        public static string PurgeUserData()
        {
            var removed = new System.Collections.Generic.List<string>();
            var failed = new System.Collections.Generic.List<string>();

            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string name in AppDataNames)
            {
                string dir = Path.Combine(roaming, name);
                if (!Directory.Exists(dir)) { continue; }
                try { Directory.Delete(dir, recursive: true); removed.Add(dir); }
                catch (Exception ex) { failed.Add(dir + " (" + ex.Message + ")"); }
            }

            foreach (string subKey in HkcuSubKeys)
            {
                try
                {
                    using (RegistryKey software = Registry.CurrentUser.OpenSubKey(@"Software", writable: true))
                    {
                        string leaf = subKey.Substring(subKey.IndexOf('\\') + 1);
                        if (software != null && software.OpenSubKey(leaf) != null)
                        {
                            software.DeleteSubKeyTree(leaf);
                            removed.Add(@"HKCU\" + subKey);
                        }
                    }
                }
                catch (Exception ex) { failed.Add(@"HKCU\" + subKey + " (" + ex.Message + ")"); }
            }

            string summary = "Removed:\r\n  " + (removed.Count > 0 ? string.Join("\r\n  ", removed) : "(nothing found)");
            if (failed.Count > 0) { summary += "\r\n\r\nCouldn't remove:\r\n  " + string.Join("\r\n  ", failed); }
            Logger.Info("Uninstall purge: removed " + removed.Count + ", failed " + failed.Count + ".");
            return summary;
        }

        /// <summary>
        /// Finds the MSI uninstall command for AutoRipper by scanning the Windows uninstall registry
        /// (both 64- and 32-bit views) for our DisplayName. Returns null if the app isn't installed
        /// via the MSI (e.g. run from a copied folder), in which case only the data purge applies.
        /// </summary>
        public static string FindMsiUninstallCommand()
        {
            const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (RegistryKey uninstall = baseKey.OpenSubKey(uninstallPath))
                    {
                        if (uninstall == null) { continue; }
                        foreach (string name in uninstall.GetSubKeyNames())
                        {
                            using (RegistryKey entry = uninstall.OpenSubKey(name))
                            {
                                if (entry == null) { continue; }
                                string display = entry.GetValue("DisplayName") as string;
                                if (string.Equals(display, AppInfo.DisplayName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return entry.GetValue("UninstallString") as string;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Scanning uninstall registry (" + view + ") failed.", ex);
                }
            }
            return null;
        }

        /// <summary>
        /// Best-effort removal of a taskbar pin for this exe. Windows gives installers no
        /// supported way to unpin, so the MSI can't do this — but the app CAN ask the shell to
        /// unpin itself before uninstalling, via the same verb the right-click menu uses.
        /// Failure is fine (nothing pinned, older Windows) — a dead pin is cosmetic; we just try.
        /// Uses reflection-based COM so no extra assembly references are needed.
        /// </summary>
        public static void TryUnpinFromTaskbar(string exePath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) { return; }
                object shell = Activator.CreateInstance(shellType);

                object folder = shellType.InvokeMember("Namespace",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell,
                    new object[] { Path.GetDirectoryName(exePath) });
                if (folder == null) { return; }

                object item = folder.GetType().InvokeMember("ParseName",
                    System.Reflection.BindingFlags.InvokeMethod, null, folder,
                    new object[] { Path.GetFileName(exePath) });
                if (item == null) { return; }

                // "taskbarunpin" is the canonical (language-independent) verb name.
                item.GetType().InvokeMember("InvokeVerb",
                    System.Reflection.BindingFlags.InvokeMethod, null, item,
                    new object[] { "taskbarunpin" });
                Logger.Info("Uninstall: asked the shell to unpin " + exePath + " from the taskbar.");
            }
            catch (Exception ex)
            {
                Logger.Info("Uninstall: taskbar unpin skipped (" + ex.Message + ").");
            }
        }

        /// <summary>
        /// Runs the uninstall. If <paramref name="purgeUserData"/>, user data is deleted FIRST
        /// (while we can still read our own folders), then the MSI removal is launched. The
        /// caller must hard-exit the app immediately after this returns.
        /// </summary>
        public static void RunUninstall(bool purgeUserData, out string dataSummary, out bool msiLaunched)
        {
            dataSummary = purgeUserData ? PurgeUserData() : "";
            msiLaunched = false;

            string uninstallCommand = FindMsiUninstallCommand();
            if (string.IsNullOrEmpty(uninstallCommand)) { return; }

            // Unpin our own taskbar icon while we still exist — after msiexec deletes the exe,
            // the pin would be a dead button pointing at nothing.
            TryUnpinFromTaskbar(System.Windows.Forms.Application.ExecutablePath);

            try
            {
                // UninstallString is typically: MsiExec.exe /X{PRODUCT-CODE}. Split exe from args
                // and add a quiet-but-visible progress UI so the user sees it finish.
                string exe, args;
                ParseCommand(uninstallCommand, out exe, out args);
                if (args.IndexOf("/qb", StringComparison.OrdinalIgnoreCase) < 0 &&
                    args.IndexOf("/quiet", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    args += " /qb";
                }

                // Launch through cmd with a ~2 s delay so THIS process is fully gone before
                // msiexec reaches its file-removal pass. 0.1.0 started msiexec directly and the
                // still-exiting exe was seen as "in use" — the program files got left behind.
                string delayed = "/c ping -n 3 127.0.0.1 >nul & \"" + exe + "\" " + args;
                Process.Start(new ProcessStartInfo("cmd.exe", delayed)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                msiLaunched = true;
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't launch the MSI uninstall.", ex);
            }
        }

        private static void ParseCommand(string command, out string exe, out string args)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                exe = command.Substring(1, end - 1);
                args = command.Substring(end + 1).Trim();
            }
            else
            {
                int space = command.IndexOf(' ');
                exe = space < 0 ? command : command.Substring(0, space);
                args = space < 0 ? "" : command.Substring(space + 1).Trim();
            }
        }
    }
}
