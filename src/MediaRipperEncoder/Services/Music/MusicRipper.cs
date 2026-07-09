using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>Status of one track through the rip -> tag -> place pipeline.</summary>
    public class MusicTrackResult
    {
        public AudioTrack Track { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public string FinalPath { get; set; }

        public MusicTrackResult() { Error = ""; FinalPath = ""; }
    }

    /// <summary>
    /// Rips the checked tracks of a confirmed release: drives freaccmd one track at a time
    /// (so one bad track never loses the album — same per-title rule as the video rip queue),
    /// tags each file itself (see <see cref="MusicTagger"/>), and places it into the Music
    /// library with the same overwrite protection as video.
    /// </summary>
    public class MusicRipper
    {
        private readonly string _freacPath;
        private readonly string _tempFolder;
        private readonly string _musicRoot;

        /// <summary>Fires as each track starts/finishes (background thread — marshal in the UI).</summary>
        public event Action<int, int, MusicTrackResult> TrackUpdated; // done, total, result (null while starting)

        /// <summary>UI hook for the never-silently-overwrite rule; null = KeepBoth.</summary>
        public Func<PlacementPlan, ConflictResolution> ResolveConflict;

        public MusicRipper(string freacPath, string tempFolder, string musicRoot)
        {
            _freacPath = freacPath;
            _tempFolder = tempFolder;
            _musicRoot = musicRoot;
        }

        /// <summary>
        /// Rips all selected tracks. <paramref name="freacDriveIndex"/> is fre:ac's 0-based CD
        /// drive number (0 on single-drive machines, which is nearly everyone).
        /// </summary>
        public List<MusicTrackResult> RipSelected(MusicRelease release, int freacDriveIndex,
            MusicFormat format, byte[] coverArt, CancellationToken cancel)
        {
            var results = new List<MusicTrackResult>();
            var selected = new List<AudioTrack>();
            foreach (AudioTrack t in release.Tracks) { if (t.Selected) { selected.Add(t); } }

            string scratch = Path.Combine(string.IsNullOrEmpty(_tempFolder) ? Path.GetTempPath() : _tempFolder,
                "music_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(scratch);

            int done = 0;
            try
            {
                foreach (AudioTrack track in selected)
                {
                    if (cancel.IsCancellationRequested) { break; }

                    var result = new MusicTrackResult { Track = track };
                    Raise(done, selected.Count, null);

                    try
                    {
                        RipOneTrack(release, track, freacDriveIndex, format, coverArt, scratch, result, cancel);
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.Error = ex.Message;
                        Logger.Error("Music track " + track.Number + " failed.", ex);
                    }

                    results.Add(result);
                    done++;
                    Raise(done, selected.Count, result);
                }
            }
            finally
            {
                TryDeleteDir(scratch);
            }
            return results;
        }

        private void RipOneTrack(MusicRelease release, AudioTrack track, int driveIndex,
            MusicFormat format, byte[] coverArt, string scratch, MusicTrackResult result,
            CancellationToken cancel)
        {
            // Rip to a bare numbered name in scratch; the real name/tags are applied by US so the
            // output always matches the confirmed on-screen list (never fre:ac's own lookup).
            string args = string.Format(CultureInfo.InvariantCulture,
                "-cd {0} -t {1} -e {2} -d \"{3}\" -p \"<track(2)>\"",
                driveIndex, track.Number, format.EncoderId, scratch);

            Logger.Info("Music rip track " + track.Number + ": freaccmd " + args);

            int exit = ProcessRunner.RunStreaming(_freacPath, args,
                line => Logger.Info("freaccmd: " + line),
                line => Logger.Info("freaccmd err: " + line),
                cancel);

            if (cancel.IsCancellationRequested)
            {
                result.Success = false;
                result.Error = "Stopped by user.";
                return;
            }

            // freaccmd wrote "<NN>.<ext>" — find it (tolerate variations like "01 - .flac").
            string ripped = FindRippedFile(scratch, track.Number, format.Extension);
            if (exit != 0 || ripped == null)
            {
                result.Success = false;
                result.Error = exit != 0
                    ? "freaccmd exit code " + exit + " (dirty disc? wrong drive number?)"
                    : "freaccmd finished but produced no ." + format.Extension + " file for this track.";
                return;
            }

            // Tag in place, then move into the library with overwrite protection.
            MusicTagger.Tag(ripped, release, track, coverArt);

            LibraryTarget target = MusicPathBuilder.BuildTrack(_musicRoot, release, track, format.Extension);
            PlacementPlan plan = LibraryPlacer.Plan(ripped, target);
            ConflictResolution resolution = ConflictResolution.KeepBoth;
            if (plan.TargetExists && ResolveConflict != null) { resolution = ResolveConflict(plan); }

            PlacementResult placed = LibraryPlacer.Commit(plan, resolution);
            if (placed.Outcome == PlacementOutcome.Failed)
            {
                result.Success = false;
                result.Error = "Ripped and tagged, but placing into the library failed (see log).";
                return;
            }

            result.Success = true;
            result.FinalPath = placed.FinalPath ?? target.FullPath;
        }

        /// <summary>Finds the file freaccmd produced for a track number in the scratch folder.</summary>
        public static string FindRippedFile(string folder, int trackNumber, string extension)
        {
            try
            {
                string prefix = trackNumber.ToString("00");
                foreach (string file in Directory.GetFiles(folder, "*." + extension))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) { return file; }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't scan " + folder + " for track " + trackNumber, ex);
            }
            return null;
        }

        private void Raise(int done, int total, MusicTrackResult result)
        {
            var handler = TrackUpdated;
            if (handler != null) { handler(done, total, result); }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } }
            catch { /* scratch cleanup is best-effort */ }
        }
    }
}
