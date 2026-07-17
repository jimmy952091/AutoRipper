using System;
using System.IO;

namespace MediaRipperEncoder.Services
{
    /// <summary>How to resolve a name collision when the target file already exists.</summary>
    public enum ConflictResolution
    {
        /// <summary>Replace the existing file. Only pass this after the user has confirmed it.</summary>
        Overwrite,

        /// <summary>Leave the existing file untouched; don't move the new one.</summary>
        Skip,

        /// <summary>Keep both by giving the new file a " (2)", " (3)"... suffix.</summary>
        KeepBoth
    }

    /// <summary>What actually happened when a placement was committed.</summary>
    public enum PlacementOutcome
    {
        Moved,
        Overwritten,
        Skipped,
        KeptBoth,
        Failed
    }

    /// <summary>
    /// A planned move of one encoded file into the library. Built first (cheap, no I/O beyond
    /// an existence check) so the UI can detect collisions and ask the user BEFORE anything is
    /// written — the project standard forbids silently overwriting media.
    /// </summary>
    public class PlacementPlan
    {
        public string SourcePath { get; set; }
        public LibraryTarget Target { get; set; }

        /// <summary>True if a file already exists at the target path (a collision to confirm).</summary>
        public bool TargetExists { get; set; }
    }

    public class PlacementResult
    {
        public PlacementOutcome Outcome { get; set; }

        /// <summary>The path the file actually ended up at (differs from the plan for KeepBoth).</summary>
        public string FinalPath { get; set; }

        public Exception Error { get; set; }
    }

    /// <summary>
    /// Moves encoded files into their computed library locations, safely. Folder creation is
    /// idempotent, and an existing target is never clobbered unless the caller explicitly asks
    /// for <see cref="ConflictResolution.Overwrite"/>.
    /// </summary>
    public static class LibraryPlacer
    {
        /// <summary>
        /// Builds a plan (and detects a collision) without moving anything. Call this, and if
        /// <see cref="PlacementPlan.TargetExists"/> is true, confirm with the user before committing.
        /// </summary>
        public static PlacementPlan Plan(string sourcePath, LibraryTarget target)
        {
            return new PlacementPlan
            {
                SourcePath = sourcePath,
                Target = target,
                TargetExists = File.Exists(target.FullPath)
            };
        }

        /// <summary>
        /// Commits a plan, moving the source file into place. When the target already exists,
        /// <paramref name="resolution"/> decides what happens. Creating the destination folder
        /// is idempotent (no failure if it already exists from a previous disc).
        /// </summary>
        public static PlacementResult Commit(PlacementPlan plan, ConflictResolution resolution)
        {
            var result = new PlacementResult();
            try
            {
                if (!File.Exists(plan.SourcePath))
                {
                    result.Outcome = PlacementOutcome.Failed;
                    result.Error = new FileNotFoundException("Source file to place was not found.", plan.SourcePath);
                    Logger.Error("Placement failed: missing source " + plan.SourcePath);
                    return result;
                }

                // Idempotent: does nothing if the folder already exists.
                Directory.CreateDirectory(plan.Target.Folder);

                string destination = plan.Target.FullPath;
                bool exists = File.Exists(destination);

                if (exists)
                {
                    switch (resolution)
                    {
                        case ConflictResolution.Skip:
                            result.Outcome = PlacementOutcome.Skipped;
                            result.FinalPath = destination;
                            Logger.Info("Placement skipped (target exists): " + destination);
                            return result;

                        case ConflictResolution.KeepBoth:
                            destination = MakeUniquePath(destination);
                            MoveAcrossVolumes(plan.SourcePath, destination, overwrite: false);
                            result.Outcome = PlacementOutcome.KeptBoth;
                            result.FinalPath = destination;
                            Logger.Info("Placement kept both -> " + destination);
                            return result;

                        case ConflictResolution.Overwrite:
                            MoveAcrossVolumes(plan.SourcePath, destination, overwrite: true);
                            result.Outcome = PlacementOutcome.Overwritten;
                            result.FinalPath = destination;
                            Logger.Info("Placement overwrote existing -> " + destination);
                            return result;
                    }
                }

                MoveAcrossVolumes(plan.SourcePath, destination, overwrite: false);
                result.Outcome = PlacementOutcome.Moved;
                result.FinalPath = destination;
                Logger.Info("Placed file -> " + destination);
                return result;
            }
            catch (Exception ex)
            {
                result.Outcome = PlacementOutcome.Failed;
                result.Error = ex;
                Logger.Error("Placement failed for " + plan.SourcePath, ex);
                return result;
            }
        }

        /// <summary>
        /// Moves a file even when source and destination are on different volumes (the temp
        /// scratch drive is usually not the library drive). File.Move handles cross-volume
        /// moves, but its overwrite overload doesn't exist on .NET Framework, so we delete an
        /// existing destination first when overwrite is requested.
        ///
        /// RETRIES on sharing violations: a freshly encoded multi-gigabyte file (a UHD movie
        /// especially) is prime bait for antivirus scanners and indexers, whose brief exclusive
        /// lock makes the very first Move attempt fail — which stranded a finished UHD encode
        /// in the scratch folder on the server. Three attempts with growing pauses ride out a
        /// scan; a persistent failure (disk full, permissions) still fails fast enough.
        /// </summary>
        private static void MoveAcrossVolumes(string source, string destination, bool overwrite)
        {
            if (overwrite && File.Exists(destination))
            {
                File.Delete(destination);
            }

            int[] pauseMs = { 0, 2000, 8000 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (pauseMs[attempt] > 0) { System.Threading.Thread.Sleep(pauseMs[attempt]); }
                    File.Move(source, destination);
                    return;
                }
                catch (IOException ex) when (attempt < pauseMs.Length - 1)
                {
                    Logger.Info("Placement move attempt " + (attempt + 1) + " failed (" + ex.Message +
                                "); retrying — the file may be briefly locked by an antivirus scan.");
                }
                catch (UnauthorizedAccessException ex) when (attempt < pauseMs.Length - 1)
                {
                    Logger.Info("Placement move attempt " + (attempt + 1) + " failed (" + ex.Message +
                                "); retrying — the file may be briefly locked by an antivirus scan.");
                }
            }
        }

        /// <summary>
        /// Returns a path that doesn't collide with an existing file by inserting " (2)",
        /// " (3)", ... before the extension.
        /// </summary>
        private static string MakeUniquePath(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);

            for (int n = 2; n < 1000; n++)
            {
                string candidate = Path.Combine(dir, stem + " (" + n + ")" + ext);
                if (!File.Exists(candidate)) { return candidate; }
            }
            // Extremely unlikely; fall back to a timestamp so we never loop forever.
            return Path.Combine(dir, stem + " (" + DateTime.Now.Ticks + ")" + ext);
        }
    }
}
