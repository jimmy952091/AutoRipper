using System;
using System.IO;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Minimal append-only file logger. Later phases lean on this heavily: the project
    /// standard is that every external process call (MakeMKV, HandBrake) has its outcome
    /// logged, so failures during unattended overnight jobs can be diagnosed after the
    /// fact instead of vanishing.
    ///
    /// Writes one file per day to %AppData%\MediaRipperEncoder\logs\app-YYYYMMDD.log.
    /// Logging must never crash the app, so all failures here are swallowed.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + Environment.NewLine + ex);
        }

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.LogFolder);
                string file = Path.Combine(AppInfo.LogFolder,
                    "app-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}{3}",
                    DateTime.Now, level, message, Environment.NewLine);

                // Lock so concurrent rip/encode threads (later phases) don't interleave lines.
                lock (_lock)
                {
                    File.AppendAllText(file, line);
                }
            }
            catch
            {
                // A logging failure must never take down a running rip/encode job.
            }
        }
    }
}
