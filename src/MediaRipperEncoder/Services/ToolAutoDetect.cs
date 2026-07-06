using System;
using System.IO;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Best-effort guesses at where the user's CLI tools are installed, used to pre-fill
    /// the setup wizard. This is only a convenience shortcut — whatever it finds still gets
    /// run through <see cref="ToolValidator"/> before it's trusted. Returns null when
    /// nothing plausible is found, which is a normal outcome (especially for HandBrakeCLI,
    /// which is often just unzipped to an arbitrary folder).
    /// </summary>
    public static class ToolAutoDetect
    {
        public static string FindMakeMkv()
        {
            string[] programFiles =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            // MakeMKV ships makemkvcon64.exe on 64-bit installs and makemkvcon.exe otherwise.
            string[] exeNames = { "makemkvcon64.exe", "makemkvcon.exe" };

            foreach (string root in programFiles)
            {
                if (string.IsNullOrEmpty(root)) { continue; }
                foreach (string exe in exeNames)
                {
                    string candidate = Path.Combine(Path.Combine(root, "MakeMKV"), exe);
                    if (SafeFileExists(candidate)) { return candidate; }
                }
            }

            return FindOnPath("makemkvcon64.exe") ?? FindOnPath("makemkvcon.exe");
        }

        public static string FindHandBrakeCli()
        {
            string[] programFiles =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (string root in programFiles)
            {
                if (string.IsNullOrEmpty(root)) { continue; }
                string candidate = Path.Combine(Path.Combine(root, "HandBrake"), "HandBrakeCLI.exe");
                if (SafeFileExists(candidate)) { return candidate; }
            }

            // The CLI is frequently just extracted somewhere and added to PATH.
            return FindOnPath("HandBrakeCLI.exe");
        }

        /// <summary>Searches each folder on the PATH environment variable for an executable.</summary>
        private static string FindOnPath(string exeName)
        {
            try
            {
                string pathVar = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathVar)) { return null; }

                foreach (string dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) { continue; }
                    string candidate = Path.Combine(dir.Trim(), exeName);
                    if (SafeFileExists(candidate)) { return candidate; }
                }
            }
            catch
            {
                // A malformed PATH entry shouldn't break auto-detect; just give up quietly.
            }
            return null;
        }

        private static bool SafeFileExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }
    }
}
