using System;
using System.IO;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// The shared "an encode just finished" step: stamp the embedded Title tag on the staged
    /// file, then move it into the library with overwrite protection. Used by BOTH the local
    /// pipeline and the remote encoder server so a file encoded on another machine goes through
    /// exactly the same tagging + conflict-safe placement as a local one.
    /// </summary>
    public static class EncodeFinisher
    {
        /// <summary>
        /// Tags and places a successfully encoded job. <paramref name="resolveConflict"/> decides
        /// what to do if the target already exists; pass null to default to KeepBoth — the
        /// never-silently-overwrite guarantee holds even with no user present (e.g. a headless
        /// server node).
        /// </summary>
        public static PlacementResult FinishAndPlace(EncodeJob job,
            Func<PlacementPlan, ConflictResolution> resolveConflict)
        {
            // Stamp the embedded Title tag (e.g. "Solo Leveling - S01E009 (Episode Name)") so VLC
            // and Explorer show the show + episode instead of MakeMKV's disc label. Done on the
            // staged file before it's moved.
            string embeddedTitle = string.IsNullOrEmpty(job.EmbeddedTitle)
                ? MediaTagWriter.TitleFromFileName(job.FinalTargetPath)
                : job.EmbeddedTitle;
            MediaTagWriter.SetTitle(job.OutputFile, embeddedTitle);

            var target = new LibraryTarget
            {
                Folder = Path.GetDirectoryName(job.FinalTargetPath),
                FileName = Path.GetFileName(job.FinalTargetPath),
                FullPath = job.FinalTargetPath
            };

            PlacementPlan plan = LibraryPlacer.Plan(job.OutputFile, target);

            ConflictResolution resolution = ConflictResolution.KeepBoth;
            if (plan.TargetExists && resolveConflict != null)
            {
                resolution = resolveConflict(plan);
            }

            PlacementResult result = LibraryPlacer.Commit(plan, resolution);

            // Describe the outcome on the job for whichever UI (local or remote) shows it. The
            // job's OutputFile deliberately keeps pointing at the staging path so a later
            // "Re-encode" goes back through this overwrite-protected placement.
            if (result.Outcome == PlacementOutcome.Skipped)
            {
                job.CurrentOperation = "Kept existing library file (skipped).";
            }
            else if (result.Outcome == PlacementOutcome.Failed)
            {
                job.CurrentOperation = "Encoded OK, but placing into the library FAILED.";
            }
            else
            {
                job.CurrentOperation = "Placed -> " + result.FinalPath;
            }

            return result;
        }
    }
}
