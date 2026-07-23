using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// The Rip Queue: rips one disc at a time (a physical drive can only do one at once),
    /// with additional jobs waiting behind it. Runs on its own long-lived background thread
    /// via a BlockingCollection producer/consumer, so the UI thread never blocks on a rip.
    ///
    /// After each SUCCESSFUL rip it auto-ejects the disc. By the time MakeMKV's Rip() call
    /// returns, the makemkvcon child process has already exited, so its handle on the disc is
    /// released — which is why the eject won't be blocked by our own process (the lock risk
    /// we flagged during Phase 2 testing).
    ///
    /// Crucially, one job failing NEVER stops the queue: it's logged, marked Failed, and the
    /// queue moves on to the next job.
    /// </summary>
    public class RipQueue : IDisposable
    {
        private readonly MakeMkvService _makeMkv;
        private readonly BlockingCollection<RipJob> _queue = new BlockingCollection<RipJob>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Task _worker;

        private CancellationTokenSource _currentJobCancel;

        /// <summary>
        /// Raised whenever a job's status or progress changes. Fires on the background
        /// worker thread — subscribers that touch the UI must marshal to the UI thread.
        /// </summary>
        public event Action<RipJob> JobUpdated;

        /// <summary>Raised when a rip completes successfully, handing off its output files
        /// (Phase 4 will connect the encode queue here).</summary>
        public event Action<RipJob> JobRippedSuccessfully;

        /// <summary>Raised whenever a single title's status/progress changes within a job.
        /// Fires on the background worker thread — marshal to the UI thread to display.</summary>
        public event Action<RipJob, RipTitleResult> TitleUpdated;

        /// <summary>Raised after a successful rip from a remote/mapped source that can't be ejected
        /// here, so the UI can prompt the user to change the disc on the shared drive.</summary>
        public event Action<RipJob> ManualDiscChangeRequested;

        public RipQueue(MakeMkvService makeMkv)
        {
            _makeMkv = makeMkv;
            _worker = Task.Factory.StartNew(ProcessLoop, TaskCreationOptions.LongRunning);
        }

        public void Enqueue(RipJob job)
        {
            job.Status = RipStatus.Queued;
            Raise(job);
            _queue.Add(job);
            Logger.Info("Rip job " + job.ShortId + " queued (disc:" + job.DiscIndex + ").");
        }

        /// <summary>Cancels the rip currently in progress (if any). Queued jobs still run.</summary>
        public void CancelCurrent()
        {
            CancellationTokenSource cts = _currentJobCancel;
            if (cts != null)
            {
                cts.Cancel();
            }
        }

        private void ProcessLoop()
        {
            try
            {
                foreach (RipJob job in _queue.GetConsumingEnumerable(_shutdown.Token))
                {
                    try
                    {
                        RunJob(job);
                    }
                    catch (Exception ex)
                    {
                        // A single job blowing up must not kill the queue thread.
                        job.Status = RipStatus.Failed;
                        job.Error = "Unexpected error: " + ex.Message;
                        Logger.Error("Rip job " + job.ShortId + " threw.", ex);
                        Raise(job);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on shutdown.
            }
        }

        private void RunJob(RipJob job)
        {
            job.Status = RipStatus.Ripping;
            job.ProgressPercent = 0;
            job.CurrentOperation = "Starting...";
            Raise(job);

            _currentJobCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

            Action<int, string> onProgress = (pct, op) =>
            {
                job.ProgressPercent = pct;
                if (!string.IsNullOrEmpty(op)) { job.CurrentOperation = op; }
                Raise(job);
            };

            // Same queue, two engines: video discs go through MakeMKV; audio CDs through the
            // built-in raw reader. Everything downstream (eject, partial-failure handling,
            // "Retry failed title(s)") is engine-agnostic and works for both.
            RipOutcome outcome = job.Kind == JobKind.Music
                ? Music.MusicRipWorker.Rip(job, onProgress, tr => RaiseTitle(job, tr), _currentJobCancel.Token)
                : _makeMkv.Rip(job, onProgress, tr => RaiseTitle(job, tr), _currentJobCancel.Token);

            job.OutputFiles = outcome.OutputFiles;

            // Hand the titles that DID rip to the encode queue, even if some failed — one bad
            // title must never hold back the good ones (a core project guarantee).
            if (outcome.TitlesSucceeded > 0)
            {
                Action<RipJob> handler = JobRippedSuccessfully;
                if (handler != null) { handler(job); }
            }

            if (outcome.Cancelled)
            {
                // User pressed Stop. Never eject — the disc stays in so the stopped title can be
                // cleaned and retried. Any titles that DID finish were already handed to encode.
                job.Status = outcome.TitlesSucceeded > 0 ? RipStatus.Completed : RipStatus.Failed;
                job.Error = outcome.Error;
                job.CurrentOperation = outcome.TitlesSucceeded > 0
                    ? "Stopped by user after " + outcome.TitlesSucceeded + " title(s) — disc left in; use 'Retry failed'."
                    : "Stopped by user — disc left in; clean it and use 'Retry failed'.";
                Logger.Info("Rip job " + job.ShortId + " stopped by user.");
                Raise(job);
            }
            else if (outcome.TitlesFailed == 0 && outcome.TitlesSucceeded > 0)
            {
                // Clean rip: everything succeeded.
                job.ProgressPercent = 100;
                job.Status = RipStatus.Completed;
                job.CurrentOperation = "Ripped " + outcome.TitlesSucceeded + " title(s).";
                Raise(job);

                if (job.ManualDiscChange)
                {
                    // Remote/mapped source — we can't eject it from here. Ask the user to swap the
                    // disc on the machine that owns the drive.
                    job.CurrentOperation = "Ripped " + outcome.TitlesSucceeded +
                        " title(s). Change the disc on the shared drive.";
                    Logger.Info("Rip job " + job.ShortId + " done (network source); prompting for disc change.");
                    Raise(job);
                    Action<RipJob> h = ManualDiscChangeRequested;
                    if (h != null) { h(job); }
                }
                else
                {
                    AutoEject(job);
                }
            }
            else if (outcome.TitlesSucceeded > 0)
            {
                // Partial success: DO NOT eject — leave the disc in so the user can retry the
                // failed titles (often just a smudge/scratch on one title).
                job.Status = RipStatus.Completed;
                job.Error = outcome.Error;
                job.CurrentOperation = "Ripped " + outcome.TitlesSucceeded + ", " +
                    outcome.TitlesFailed + " FAILED — disc left in; use 'Retry failed'.";
                Logger.Error("Rip job " + job.ShortId + " partial: " + outcome.Error);
                Raise(job);
            }
            else
            {
                // Nothing ripped. Leave the disc in for a retry too.
                job.Status = RipStatus.Failed;
                job.Error = outcome.Error;
                job.CurrentOperation = "Failed — disc left in; clean it and use 'Retry failed'.";
                Logger.Error("Rip job " + job.ShortId + " failed: " + outcome.Error);
                Raise(job);
            }
        }

        private void AutoEject(RipJob job)
        {
            if (string.IsNullOrEmpty(job.DriveLetter)) { return; }

            EjectResult result = EjectService.Eject(job.DriveLetter);
            if (result.Success)
            {
                job.CurrentOperation = "Ripped; disc ejected (" + result.Method + ").";
            }
            else
            {
                // Don't fail the rip over a failed eject — the files are safe — but make the
                // user aware they need to remove the disc by hand. On Windows 7 the usual fix is
                // running AutoRipper as administrator, so nudge toward that when it applies.
                job.CurrentOperation = EjectService.Windows7AdminEjectHint().Length > 0
                    ? "Ripped OK, but auto-eject FAILED — on Windows 7, try running AutoRipper as administrator (or remove the disc manually)."
                    : "Ripped OK, but auto-eject FAILED — remove the disc manually.";
                Logger.Error("Auto-eject failed for job " + job.ShortId + ": " + result.Message);
            }
            Raise(job);
        }

        private void Raise(RipJob job)
        {
            Action<RipJob> handler = JobUpdated;
            if (handler != null) { handler(job); }
        }

        private void RaiseTitle(RipJob job, RipTitleResult title)
        {
            Action<RipJob, RipTitleResult> handler = TitleUpdated;
            if (handler != null) { handler(job, title); }
        }

        public void Dispose()
        {
            try
            {
                _shutdown.Cancel();
                _queue.CompleteAdding();
                // Give the worker a moment to unwind; don't hang shutdown on it.
                _worker.Wait(2000);
            }
            catch
            {
                // Best-effort shutdown.
            }
        }
    }
}
