using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// The Encode Queue: encodes one file at a time with HandBrake, on its own background
    /// thread, INDEPENDENTLY of the rip queue — so encoding a finished rip runs at the same
    /// time as the next disc is ripping.
    ///
    /// Ordering is strictly FIFO: <see cref="Enqueue"/> appends to the back, so files handed
    /// over as rips complete never jump ahead of or reorder work that's already queued or in
    /// progress. <see cref="ReEncode"/> (for re-running a specific bad episode) also appends
    /// to the back rather than cutting in line.
    ///
    /// As with the rip queue, one job failing never stops the queue.
    /// </summary>
    public class EncodeQueue : IDisposable
    {
        private readonly HandBrakeService _handBrake;
        private readonly BlockingCollection<EncodeJob> _queue = new BlockingCollection<EncodeJob>();
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Task _worker;

        private CancellationTokenSource _currentJobCancel;

        /// <summary>Fires on any job status/progress change. Runs on the worker thread —
        /// UI subscribers must marshal to the UI thread.</summary>
        public event Action<EncodeJob> JobUpdated;

        /// <summary>
        /// Fires once after a job encodes successfully — the pipeline hooks this to move the
        /// finished file into the library. Runs on the worker thread.
        /// </summary>
        public event Action<EncodeJob> JobEncodedSuccessfully;

        public EncodeQueue(HandBrakeService handBrake)
        {
            _handBrake = handBrake;
            _worker = Task.Factory.StartNew(ProcessLoop, TaskCreationOptions.LongRunning);
        }

        /// <summary>Appends a job to the back of the queue (FIFO).</summary>
        public void Enqueue(EncodeJob job)
        {
            job.Status = EncodeStatus.Queued;
            job.ProgressPercent = 0;
            job.CurrentOperation = "Queued";
            Raise(job);
            _queue.Add(job);
            Logger.Info("Encode job " + job.ShortId + " queued: " + job.InputFile);
        }

        /// <summary>
        /// Re-queues an already-finished (or failed) job to the BACK of the line — used by
        /// the "Re-encode selected" action so a single bad episode can be redone without
        /// disturbing the current order.
        /// </summary>
        public void ReEncode(EncodeJob job)
        {
            job.Error = "";
            job.ProgressPercent = 0;
            Enqueue(job);
        }

        /// <summary>Cancels the encode currently in progress (if any). Queued jobs still run.</summary>
        public void CancelCurrent()
        {
            CancellationTokenSource cts = _currentJobCancel;
            if (cts != null) { cts.Cancel(); }
        }

        private void ProcessLoop()
        {
            try
            {
                foreach (EncodeJob job in _queue.GetConsumingEnumerable(_shutdown.Token))
                {
                    try
                    {
                        RunJob(job);
                    }
                    catch (Exception ex)
                    {
                        job.Status = EncodeStatus.Failed;
                        job.Error = "Unexpected error: " + ex.Message;
                        Logger.Error("Encode job " + job.ShortId + " threw.", ex);
                        Raise(job);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on shutdown.
            }
        }

        private void RunJob(EncodeJob job)
        {
            job.Status = EncodeStatus.Encoding;
            job.ProgressPercent = 0;
            job.CurrentOperation = "Starting...";
            Raise(job);

            _currentJobCancel = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

            EncodeOutcome outcome = job.Kind == JobKind.Music
                ? EncodeMusic(job, _currentJobCancel.Token)
                : _handBrake.Encode(job,
                    (pct, op) =>
                    {
                        if (pct >= 0) { job.ProgressPercent = pct; }
                        if (!string.IsNullOrEmpty(op)) { job.CurrentOperation = op; }
                        Raise(job);
                    },
                    _currentJobCancel.Token);

            if (outcome.Success)
            {
                job.ProgressPercent = 100;
                job.Status = EncodeStatus.Completed;
                job.CurrentOperation = "Encoded -> " + job.OutputFile;
                Raise(job);

                Action<EncodeJob> handler = JobEncodedSuccessfully;
                if (handler != null) { handler(job); }
            }
            else
            {
                job.Status = EncodeStatus.Failed;
                job.Error = outcome.Error;
                job.CurrentOperation = "Failed.";
                Logger.Error("Encode job " + job.ShortId + " failed: " + outcome.Error);
                Raise(job);
            }
        }

        /// <summary>
        /// Music encode: WAV -> chosen format via the built-in encoders. Wrapped to the same
        /// EncodeOutcome contract as HandBrake so the rest of the queue is engine-agnostic.
        /// </summary>
        private EncodeOutcome EncodeMusic(EncodeJob job, CancellationToken cancel)
        {
            var outcome = new EncodeOutcome();
            try
            {
                Music.MusicEncoder.Encode(job.InputFile, job.OutputFile, job.AudioFormatId,
                    pct =>
                    {
                        if (cancel.IsCancellationRequested) { throw new OperationCanceledException(); }
                        if (pct != job.ProgressPercent)
                        {
                            job.ProgressPercent = pct;
                            job.CurrentOperation = "Encoding";
                            Raise(job);
                        }
                    });

                var fi = new System.IO.FileInfo(job.OutputFile);
                if (!fi.Exists || fi.Length == 0)
                {
                    outcome.Success = false;
                    outcome.Error = "Music encode produced no output file.";
                    return outcome;
                }
                outcome.Success = true;
            }
            catch (OperationCanceledException)
            {
                outcome.Success = false;
                outcome.Error = "Encode cancelled.";
                try { if (System.IO.File.Exists(job.OutputFile)) { System.IO.File.Delete(job.OutputFile); } }
                catch { /* partial cleanup is best-effort */ }
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Error = "Music encode failed: " + ex.Message;
                Logger.Error("Music encode " + job.ShortId + " failed.", ex);
            }
            return outcome;
        }

        private void Raise(EncodeJob job)
        {
            Action<EncodeJob> handler = JobUpdated;
            if (handler != null) { handler(job); }
        }

        public void Dispose()
        {
            try
            {
                _shutdown.Cancel();
                _queue.CompleteAdding();
                _worker.Wait(2000);
            }
            catch
            {
                // Best-effort shutdown.
            }
        }
    }
}
