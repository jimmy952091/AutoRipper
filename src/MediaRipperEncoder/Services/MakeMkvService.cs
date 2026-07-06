using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>Result of a rip attempt.</summary>
    public class RipOutcome
    {
        /// <summary>True when at least one title was ripped successfully.</summary>
        public bool Success { get; set; }
        public string Error { get; set; }

        /// <summary>Files produced by THIS rip attempt (not pre-existing files in the folder).</summary>
        public List<string> OutputFiles { get; set; }

        public int TitlesSucceeded { get; set; }
        public int TitlesFailed { get; set; }

        /// <summary>True when the user stopped the rip (vs. a disc/read failure). The disc is
        /// deliberately left in the drive so the stopped title can be cleaned and retried.</summary>
        public bool Cancelled { get; set; }

        public RipOutcome()
        {
            Error = "";
            OutputFiles = new List<string>();
        }
    }

    /// <summary>
    /// Drives makemkvcon: enumerate drives, scan a disc's titles, and rip. All calls go
    /// through ProcessRunner so output is captured/logged; nothing is assumed to have
    /// succeeded without checking the exit code AND that files were actually produced.
    /// </summary>
    public class MakeMkvService
    {
        private readonly string _makeMkvPath;

        public MakeMkvService(string makeMkvPath)
        {
            _makeMkvPath = makeMkvPath;
        }

        /// <summary>The MakeMKV source spec for a job: an explicit override (network/mapped source)
        /// if set, otherwise the local drive by disc index.</summary>
        private static string SourceFor(RipJob job)
        {
            return string.IsNullOrEmpty(job.SourceSpec)
                ? "disc:" + job.DiscIndex.ToString(CultureInfo.InvariantCulture)
                : job.SourceSpec;
        }

        /// <summary>
        /// Turns a user-configured network path into a MakeMKV source spec. If it already carries a
        /// scheme (disc:/file:/iso:) it's used as-is; a path ending in .iso becomes iso:"path";
        /// anything else (a mapped drive root or mounted disc folder) becomes file:"path".
        /// </summary>
        public static string BuildFileSourceSpec(string configuredPath)
        {
            string p = (configuredPath ?? "").Trim();
            if (p.Length == 0) { return ""; }

            if (p.StartsWith("disc:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("iso:", StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }

            // Strip trailing slashes BEFORE quoting. A drive root like "Y:\" would otherwise
            // become file:"Y:\" — and Windows command-line parsing treats the backslash-then-quote
            // (\") as an escaped literal quote, not a closing quote. That mangles the argument (the
            // title number and output path get swallowed into it), so MakeMKV fails with a bogus
            // "is a disc inserted?" error. "Y:\" -> "Y:" gives file:"Y:", which MakeMKV opens as
            // file://Y: and reads correctly. Verified with an argv echo test.
            p = p.TrimEnd('\\', '/');
            if (p.Length == 0) { return ""; }

            string scheme = p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? "iso:" : "file:";
            return scheme + "\"" + p + "\"";
        }

        /// <summary>
        /// Resolves the actual disc-structure folder under a configured root. MakeMKV's file source
        /// needs the folder that CONTAINS a "BDMV" (Blu-ray) or "VIDEO_TS" (DVD) directory — which
        /// changes per disc type. So when the user swaps a Blu-ray for a DVD on a shared drive, the
        /// correct target moves, and pointing at the drive root alone fails.
        ///
        /// When <paramref name="searchSubfolders"/> is on, this searches a few levels down for the
        /// disc structure and returns its parent, so the user can leave the setting on the drive
        /// root and never re-point it. Returns the original path unchanged if nothing is found
        /// (or the path isn't a local/mapped folder), so behaviour is never worse than before.
        /// </summary>
        public static string ResolveDiscFolder(string configuredPath, bool searchSubfolders)
        {
            string p = (configuredPath ?? "").Trim();
            if (p.Length == 0) { return p; }

            // Leave explicit schemes and ISO images alone — they're not folders to search.
            if (p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("disc:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("iso:", StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }

            try
            {
                if (!Directory.Exists(p)) { return p; }

                // If the path already IS or directly contains the disc structure, use it as-is.
                if (HasDiscStructure(p)) { return p; }

                if (!searchSubfolders) { return p; }

                // Shallow breadth-first search (cap depth so a big/networked tree can't hang the
                // scan). Return the parent of the first BDMV/VIDEO_TS folder we find.
                const int maxDepth = 3;
                var frontier = new List<string> { p };
                for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
                {
                    var next = new List<string>();
                    foreach (string dir in frontier)
                    {
                        string[] subs;
                        try { subs = Directory.GetDirectories(dir); }
                        catch { continue; } // unreadable/permission — skip this branch
                        foreach (string sub in subs)
                        {
                            string leaf = Path.GetFileName(sub);
                            if (leaf.Equals("BDMV", StringComparison.OrdinalIgnoreCase) ||
                                leaf.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
                            {
                                return dir; // MakeMKV wants the PARENT of BDMV/VIDEO_TS
                            }
                            next.Add(sub);
                        }
                    }
                    frontier = next;
                }
            }
            catch (Exception ex)
            {
                Logger.Info("ResolveDiscFolder couldn't search '" + p + "' (" + ex.Message + "); using it as-is.");
            }

            return p;
        }

        /// <summary>True if this folder is, or directly contains, a BDMV/VIDEO_TS disc structure.</summary>
        private static bool HasDiscStructure(string folder)
        {
            string leaf = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (leaf.Equals("BDMV", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return Directory.Exists(Path.Combine(folder, "BDMV")) ||
                   Directory.Exists(Path.Combine(folder, "VIDEO_TS"));
        }

        // --- drive enumeration / mapping ---

        /// <summary>
        /// Lists the optical drives MakeMKV can see, with its own disc indexes. Uses an
        /// invalid disc index (9999) so it just enumerates drives and returns quickly.
        /// </summary>
        public List<MakeMkvDrive> ListDrives()
        {
            ProcessRunResult run = ProcessRunner.Run(_makeMkvPath,
                "-r --cache=1 info disc:9999", 90000);

            if (!run.Started)
            {
                Logger.Error("MakeMKV ListDrives couldn't launch: " +
                    (run.Exception == null ? "?" : run.Exception.Message));
                return new List<MakeMkvDrive>();
            }

            string[] lines = SplitLines(run.CombinedOutput);
            List<MakeMkvDrive> drives = MakeMkvRobotParser.ParseDrives(lines);
            Logger.Info("MakeMKV sees " + drives.Count + " drive(s).");
            return drives;
        }

        /// <summary>
        /// Maps a Windows drive letter (e.g. "G:") to MakeMKV's disc index by matching the
        /// device name MakeMKV reports. Returns -1 if it can't be matched.
        /// </summary>
        public int FindDiscIndexForLetter(string driveLetter)
        {
            string target = NormalizeLetter(driveLetter);
            if (string.IsNullOrEmpty(target)) { return -1; }

            foreach (MakeMkvDrive d in ListDrives())
            {
                if (NormalizeLetter(d.DeviceName) == target)
                {
                    return d.Index;
                }
            }
            Logger.Error("Couldn't map drive " + driveLetter + " to a MakeMKV disc index.");
            return -1;
        }

        // --- disc scan ---

        /// <summary>
        /// Scans a disc for its titles. Streams (no timeout) since a Blu-ray scan can take a
        /// while; cancellable. Runs on a background thread from the UI.
        /// </summary>
        public DiscScanResult ScanDisc(int discIndex, CancellationToken cancel)
        {
            return ScanSource("disc:" + discIndex.ToString(CultureInfo.InvariantCulture), discIndex, cancel);
        }

        /// <summary>
        /// Scans any MakeMKV source spec — a local drive ("disc:N") or a file source
        /// (file:"path" / iso:"path") for the advanced network/mapped-drive path.
        /// </summary>
        public DiscScanResult ScanSource(string sourceSpec, int discIndex, CancellationToken cancel)
        {
            var result = new DiscScanResult { DiscIndex = discIndex };
            var lines = new List<string>();
            var messages = new List<string>();

            int exit = ProcessRunner.RunStreaming(_makeMkvPath,
                "-r --cache=1 info " + sourceSpec,
                line =>
                {
                    lines.Add(line);
                    int code; string text;
                    if (MakeMkvRobotParser.TryParseMessage(line, out code, out text))
                    {
                        messages.Add(text);
                        Logger.Info("MakeMKV MSG " + code + ": " + text);
                    }
                },
                err => Logger.Info("MakeMKV stderr: " + err),
                cancel);

            result.Messages = messages;

            if (cancel.IsCancellationRequested)
            {
                result.Success = false;
                result.Error = "Scan cancelled.";
                return result;
            }

            string discName;
            result.Titles = MakeMkvRobotParser.ParseTitles(lines, out discName);
            result.DiscName = discName;

            if (result.Titles.Count > 0)
            {
                result.Success = true;
                Logger.Info("Scan of disc:" + discIndex + " found " + result.Titles.Count + " title(s).");
            }
            else
            {
                result.Success = false;
                result.Error = exit == 0
                    ? "No rippable titles found on this disc."
                    : "MakeMKV couldn't read a disc in this drive. Is a disc inserted and readable?";
                Logger.Error("Scan of disc:" + discIndex + " found no titles (exit " + exit + ").");
            }

            return result;
        }

        // --- rip ---

        /// <summary>
        /// Rips the job's titles to its output folder, ONE TITLE AT A TIME, and — critically —
        /// keeps going past a title that fails instead of abandoning the rest. Each title's
        /// outcome is recorded in <see cref="RipJob.TitleResults"/> and reported live via
        /// <paramref name="onTitle"/>, so the UI can show which titles succeeded and offer to
        /// retry the failures. A title "succeeds" only on a zero exit code AND a new .mkv
        /// actually appearing. Overall success means at least one title was ripped.
        ///
        /// Files are attributed by a before/after folder snapshot rather than by filename, so a
        /// retry into the same folder never re-hands-off titles that were already ripped.
        /// </summary>
        public RipOutcome Rip(RipJob job, Action<int, string> onProgress,
            Action<RipTitleResult> onTitle, CancellationToken cancel)
        {
            var outcome = new RipOutcome();

            try
            {
                Directory.CreateDirectory(job.OutputDirectory);
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Error = "Couldn't create output folder: " + ex.Message;
                Logger.Error("Rip " + job.ShortId + " output folder failed.", ex);
                return outcome;
            }

            // "All titles" mode can't attribute per-title results — one MakeMKV call does the
            // lot — so it's handled as a single aggregate operation.
            if (job.RipAllTitles)
            {
                return RipAllTitles(job, onProgress, cancel);
            }

            EnsureTitleResults(job);
            int total = job.TitleResults.Count;
            int completed = 0;

            // When the user hits Stop, mark every title that hadn't finished (the one mid-rip,
            // plus any still queued) as Failed so they show in the UI and the "Retry failed"
            // button can re-run them. Already-completed titles keep their success. The disc is
            // NOT ejected (RipQueue checks Cancelled) so it can be cleaned and retried.
            Func<RipOutcome> stopNow = () =>
            {
                foreach (RipTitleResult t in job.TitleResults)
                {
                    if (t.Status == RipStatus.Ripping || t.Status == RipStatus.Queued)
                    {
                        t.Status = RipStatus.Failed;
                        t.ProgressPercent = 0;
                        if (string.IsNullOrEmpty(t.Error))
                        {
                            t.Error = "Stopped by user before this title finished.";
                        }
                        outcome.TitlesFailed++;
                        if (onTitle != null) { onTitle(t); }
                    }
                }
                outcome.Cancelled = true;
                outcome.Success = outcome.TitlesSucceeded > 0;
                outcome.Error = "Rip stopped by user.";
                return outcome;
            };

            foreach (RipTitleResult tr in job.TitleResults)
            {
                if (cancel.IsCancellationRequested)
                {
                    return stopNow();
                }

                // A retry builds a fresh list, but guard against re-ripping an already-done title.
                if (tr.Status == RipStatus.Completed) { completed++; continue; }

                tr.Status = RipStatus.Ripping;
                tr.ProgressPercent = 0;
                tr.Error = "";
                if (onTitle != null) { onTitle(tr); }

                // Snapshot the folder so we can attribute exactly what THIS title produced.
                HashSet<string> before = SnapshotMkv(job.OutputDirectory);

                string selector = tr.TitleIndex.ToString(CultureInfo.InvariantCulture);

                // --progress=-same is REQUIRED: without it makemkvcon doesn't emit its
                // PRGV/PRGC progress lines onto the stream we read, so the rip runs fine but
                // the progress bar sits at 0% the whole time.
                string args = string.Format(CultureInfo.InvariantCulture,
                    "-r --progress=-same --minlength={0} mkv {1} {2} \"{3}\"",
                    job.MinLengthSeconds, SourceFor(job), selector, job.OutputDirectory);

                Logger.Info("Rip " + job.ShortId + " title " + selector + ": makemkvcon " + args);

                int localCompleted = completed;
                int lastPct = 0;
                Action<string> handleLine = line =>
                {
                    int pct; string op;
                    if (MakeMkvRobotParser.TryParseProgress(line, out pct, out op))
                    {
                        if (pct >= 0)
                        {
                            lastPct = pct;
                            tr.ProgressPercent = pct;
                            if (onTitle != null) { onTitle(tr); }
                        }
                        // Blend this title's percent into the whole-job progress bar.
                        if (onProgress != null && (pct >= 0 || !string.IsNullOrEmpty(op)))
                        {
                            onProgress((localCompleted * 100 + lastPct) / total, op);
                        }
                    }
                    int code; string text;
                    if (MakeMkvRobotParser.TryParseMessage(line, out code, out text))
                    {
                        Logger.Info("MakeMKV MSG " + code + ": " + text);
                    }
                };

                int exit = ProcessRunner.RunStreaming(_makeMkvPath, args,
                    handleLine,
                    err => { Logger.Info("MakeMKV stderr: " + err); handleLine(err); },
                    cancel);

                if (cancel.IsCancellationRequested)
                {
                    return stopNow();
                }

                List<string> produced = NewMkvSince(job.OutputDirectory, before);

                if (exit == 0 && produced.Count > 0)
                {
                    tr.Status = RipStatus.Completed;
                    tr.ProgressPercent = 100;
                    tr.OutputFile = produced[0];
                    outcome.OutputFiles.Add(produced[0]);
                    outcome.TitlesSucceeded++;
                    Logger.Info("Rip " + job.ShortId + " title " + selector + " OK -> " + produced[0]);
                }
                else
                {
                    tr.Status = RipStatus.Failed;
                    tr.Error = exit != 0
                        ? "MakeMKV exit code " + exit + " (often a disc read error — clean the disc and retry)."
                        : "MakeMKV exited 0 but produced no file for this title.";
                    outcome.TitlesFailed++;
                    Logger.Error("Rip " + job.ShortId + " title " + selector + " FAILED: " + tr.Error);
                }

                if (onTitle != null) { onTitle(tr); }
                completed++;
                if (onProgress != null) { onProgress(completed * 100 / total, "Title complete"); }
            }

            outcome.Success = outcome.TitlesSucceeded > 0;
            if (outcome.TitlesFailed > 0)
            {
                outcome.Error = outcome.TitlesSucceeded + " of " + total + " titles ripped; " +
                                outcome.TitlesFailed + " failed.";
            }
            return outcome;
        }

        /// <summary>Aggregate "rip everything in one call" path (used only when no specific titles were chosen).</summary>
        private RipOutcome RipAllTitles(RipJob job, Action<int, string> onProgress, CancellationToken cancel)
        {
            var outcome = new RipOutcome();
            HashSet<string> before = SnapshotMkv(job.OutputDirectory);

            string args = string.Format(CultureInfo.InvariantCulture,
                "-r --progress=-same --minlength={0} mkv {1} all \"{2}\"",
                job.MinLengthSeconds, SourceFor(job), job.OutputDirectory);
            Logger.Info("Rip " + job.ShortId + " (all titles): makemkvcon " + args);

            int lastOverall = 0;
            Action<string> handleLine = line =>
            {
                int pct; string op;
                if (MakeMkvRobotParser.TryParseProgress(line, out pct, out op))
                {
                    if (pct >= 0) { lastOverall = pct; }
                    if (onProgress != null && (pct >= 0 || !string.IsNullOrEmpty(op))) { onProgress(lastOverall, op); }
                }
                int code; string text;
                if (MakeMkvRobotParser.TryParseMessage(line, out code, out text)) { Logger.Info("MakeMKV MSG " + code + ": " + text); }
            };

            int exit = ProcessRunner.RunStreaming(_makeMkvPath, args, handleLine,
                err => { Logger.Info("MakeMKV stderr: " + err); handleLine(err); }, cancel);

            if (cancel.IsCancellationRequested)
            {
                outcome.Success = false;
                outcome.Error = "Rip cancelled.";
                return outcome;
            }

            outcome.OutputFiles = NewMkvSince(job.OutputDirectory, before);
            if (exit == 0 && outcome.OutputFiles.Count > 0)
            {
                outcome.Success = true;
                outcome.TitlesSucceeded = outcome.OutputFiles.Count;
            }
            else
            {
                outcome.Success = false;
                outcome.TitlesFailed = 1;
                outcome.Error = exit != 0
                    ? "MakeMKV returned a non-zero exit code (" + exit + ") ripping all titles."
                    : "MakeMKV finished but produced no .mkv files.";
                Logger.Error("Rip " + job.ShortId + " (all titles) failed: " + outcome.Error);
            }
            return outcome;
        }

        private static void EnsureTitleResults(RipJob job)
        {
            if (job.TitleResults != null && job.TitleResults.Count > 0) { return; }
            job.TitleResults = new List<RipTitleResult>();
            foreach (int idx in job.TitleIndices)
            {
                job.TitleResults.Add(new RipTitleResult
                {
                    TitleIndex = idx,
                    Label = "Title " + idx.ToString("00")
                });
            }
        }

        private static HashSet<string> SnapshotMkv(string dir)
        {
            try { return new HashSet<string>(Directory.GetFiles(dir, "*.mkv"), StringComparer.OrdinalIgnoreCase); }
            catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        private static List<string> NewMkvSince(string dir, HashSet<string> before)
        {
            var list = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.mkv"))
                {
                    if (!before.Contains(f)) { list.Add(f); }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't list produced files in " + dir, ex);
            }
            return list;
        }

        // --- helpers ---

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) { return new string[0]; }
            return text.Replace("\r\n", "\n").Split('\n');
        }

        private static string NormalizeLetter(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) { return ""; }
            char c = char.ToUpperInvariant(input.Trim()[0]);
            return (c >= 'A' && c <= 'Z') ? c.ToString() : "";
        }
    }
}
