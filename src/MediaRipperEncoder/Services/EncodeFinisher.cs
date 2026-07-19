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
            return FinishAndPlace(job, resolveConflict, null);
        }

        /// <summary>
        /// As above, plus the scratch folder root so the emptied per-job staging folder can be
        /// swept once the encoded file has been moved into the library. Pass null to skip that.
        /// </summary>
        public static PlacementResult FinishAndPlace(EncodeJob job,
            Func<PlacementPlan, ConflictResolution> resolveConflict, string scratchRoot)
        {
            // Tag the staged file before it's moved. Music files get the full embedded tag set
            // (artist/album/track/cover — media servers read music metadata from tags, not
            // filenames); video files get the Title tag so players show the episode name
            // instead of MakeMKV's disc label.
            if (job.Kind == JobKind.Music && job.Release != null && job.Track != null)
            {
                Music.MusicTagger.Tag(job.OutputFile, job.Release, job.Track, job.CoverArt);
            }
            else
            {
                string embeddedTitle = string.IsNullOrEmpty(job.EmbeddedTitle)
                    ? MediaTagWriter.TitleFromFileName(job.FinalTargetPath)
                    : job.EmbeddedTitle;
                MediaTagWriter.SetTitle(job.OutputFile, embeddedTitle);
            }

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
                // Say WHY it failed and WHERE the finished file is sitting — an encode that
                // took hours must never quietly strand its output in the scratch folder.
                string reason = result.Error != null ? result.Error.Message : "see the log";
                job.CurrentOperation = "Encoded OK, but placing into the library FAILED (" + reason +
                    "). The finished file is safe at: " + job.OutputFile;
            }
            else
            {
                job.CurrentOperation = "Placed -> " + result.FinalPath;

                // The encoded file has been moved into the library, so its per-job staging folder
                // (Scratch\enc_<id>) is now empty. Sweep it, or these pile up forever — they cost
                // no disk space but they're exactly the clutter the scratch folder shouldn't
                // accumulate. Only ever removes an EMPTY folder, and never the scratch root.
                ScratchCleaner.RemoveEmptyStagingFolder(job.OutputFile, scratchRoot);

                // First placed track of an album also drops cover.jpg beside it — some media
                // servers/pickers prefer a folder image over the embedded one.
                if (job.Kind == JobKind.Music && job.CoverArt != null && job.CoverArt.Length > 0)
                {
                    try
                    {
                        string coverPath = Path.Combine(Path.GetDirectoryName(result.FinalPath), "cover.jpg");
                        if (!File.Exists(coverPath)) { File.WriteAllBytes(coverPath, job.CoverArt); }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Couldn't write album cover.jpg (tracks are unaffected).", ex);
                    }
                }
            }

            return result;
        }
    }
}
