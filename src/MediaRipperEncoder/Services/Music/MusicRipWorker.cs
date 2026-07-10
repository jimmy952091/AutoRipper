using System;
using System.IO;
using System.Threading;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// The Music counterpart of MakeMkvService.Rip: rips each selected CD track to a numbered
    /// WAV in the job's output folder, one track at a time, with the SAME per-title result
    /// semantics as video — so the rip queue's eject / partial-failure / "Retry failed title(s)"
    /// behavior applies to a scratched audio track exactly like a scratched DVD title.
    ///
    /// TitleResults entries map 1:1 to tracks (TitleIndex = track number). Output naming is
    /// "NN.wav"; the encode stage maps NN back to the release's track for tagging/placement.
    /// </summary>
    public static class MusicRipWorker
    {
        public static RipOutcome Rip(RipJob job, Action<int, string> onProgress,
            Action<RipTitleResult> onTitle, CancellationToken cancel)
        {
            var outcome = new RipOutcome();

            if (job.Toc == null || job.Release == null)
            {
                outcome.Success = false;
                outcome.Error = "Music job is missing its TOC/release package.";
                return outcome;
            }

            try
            {
                Directory.CreateDirectory(job.OutputDirectory);
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Error = "Couldn't create output folder: " + ex.Message;
                Logger.Error("Music rip " + job.ShortId + " output folder failed.", ex);
                return outcome;
            }

            int total = job.TitleResults.Count;
            int completed = 0;

            foreach (RipTitleResult tr in job.TitleResults)
            {
                if (cancel.IsCancellationRequested) { return StopNow(job, outcome, onTitle); }
                if (tr.Status == RipStatus.Completed) { completed++; continue; } // retry re-entry

                int trackNumber = tr.TitleIndex;
                int trackIndex = trackNumber - job.Toc.FirstTrack; // offsets list is 0-based
                if (trackIndex < 0 || trackIndex >= job.Toc.TrackCount)
                {
                    tr.Status = RipStatus.Failed;
                    tr.Error = "Track " + trackNumber + " isn't on this disc's TOC.";
                    outcome.TitlesFailed++;
                    if (onTitle != null) { onTitle(tr); }
                    continue;
                }

                tr.Status = RipStatus.Ripping;
                tr.ProgressPercent = 0;
                tr.Error = "";
                if (onTitle != null) { onTitle(tr); }

                string wavPath = Path.Combine(job.OutputDirectory, trackNumber.ToString("00") + ".wav");
                int startFrame = job.Toc.TrackOffsets[trackIndex];
                int endFrame = trackIndex + 1 < job.Toc.TrackCount
                    ? job.Toc.TrackOffsets[trackIndex + 1]
                    : job.Toc.LeadOutOffset;

                try
                {
                    int localCompleted = completed;
                    CdAudioReader.RipTrackToWav(job.DriveLetter, startFrame, endFrame, wavPath,
                        (done, frames) =>
                        {
                            int pct = frames > 0 ? done * 100 / frames : 0;
                            if (pct != tr.ProgressPercent)
                            {
                                tr.ProgressPercent = pct;
                                if (onTitle != null) { onTitle(tr); }
                                if (onProgress != null)
                                {
                                    onProgress((localCompleted * 100 + pct) / total,
                                        "Reading track " + trackNumber);
                                }
                            }
                        },
                        cancel);

                    tr.Status = RipStatus.Completed;
                    tr.ProgressPercent = 100;
                    tr.OutputFile = wavPath;
                    outcome.OutputFiles.Add(wavPath);
                    outcome.TitlesSucceeded++;
                    Logger.Info("Music rip " + job.ShortId + " track " + trackNumber + " OK -> " + wavPath);
                }
                catch (OperationCanceledException)
                {
                    tr.Status = RipStatus.Failed;
                    tr.Error = "Stopped by user before this track finished.";
                    if (onTitle != null) { onTitle(tr); }
                    return StopNow(job, outcome, onTitle);
                }
                catch (Exception ex)
                {
                    tr.Status = RipStatus.Failed;
                    tr.Error = ex.Message;
                    outcome.TitlesFailed++;
                    Logger.Error("Music rip " + job.ShortId + " track " + trackNumber + " FAILED.", ex);
                }

                if (onTitle != null) { onTitle(tr); }
                completed++;
                if (onProgress != null) { onProgress(completed * 100 / total, "Track complete"); }
            }

            outcome.Success = outcome.TitlesSucceeded > 0;
            if (outcome.TitlesFailed > 0)
            {
                outcome.Error = outcome.TitlesSucceeded + " of " + total + " tracks ripped; " +
                                outcome.TitlesFailed + " failed.";
            }
            return outcome;
        }

        /// <summary>User stop: mark everything unfinished Failed so "Retry failed" can re-run it.</summary>
        private static RipOutcome StopNow(RipJob job, RipOutcome outcome, Action<RipTitleResult> onTitle)
        {
            foreach (RipTitleResult t in job.TitleResults)
            {
                if (t.Status == RipStatus.Ripping || t.Status == RipStatus.Queued)
                {
                    t.Status = RipStatus.Failed;
                    t.ProgressPercent = 0;
                    if (string.IsNullOrEmpty(t.Error)) { t.Error = "Stopped by user."; }
                    outcome.TitlesFailed++;
                    if (onTitle != null) { onTitle(t); }
                }
            }
            outcome.Cancelled = true;
            outcome.Success = outcome.TitlesSucceeded > 0;
            outcome.Error = "Rip stopped by user.";
            return outcome;
        }
    }
}
