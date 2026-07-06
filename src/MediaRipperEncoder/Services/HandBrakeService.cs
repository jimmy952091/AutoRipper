using System;
using System.Globalization;
using System.IO;
using System.Threading;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>Result of an encode attempt.</summary>
    public class EncodeOutcome
    {
        public bool Success { get; set; }
        public string Error { get; set; }

        public EncodeOutcome()
        {
            Error = "";
        }
    }

    /// <summary>
    /// Drives HandBrakeCLI to encode one file using the user's preset. Uses --json so
    /// progress is machine-readable (see HandBrakeProgressParser). Success requires a zero
    /// exit code AND an output file that actually exists with real content — an encode that
    /// silently produced nothing is treated as a failure, not a success.
    /// </summary>
    public class HandBrakeService
    {
        private readonly string _handBrakePath;
        private readonly string _presetPath;
        private readonly string _presetName;

        public HandBrakeService(string handBrakePath, string presetPath)
        {
            _handBrakePath = handBrakePath;
            _presetPath = presetPath;
            // HandBrakeCLI needs the preset's NAME (-Z) to select the imported preset file.
            _presetName = PresetInfo.GetPresetName(presetPath);
        }

        public bool HasValidPresetName
        {
            get { return !string.IsNullOrEmpty(_presetName); }
        }

        public EncodeOutcome Encode(EncodeJob job, Action<int, string> onProgress, CancellationToken cancel)
        {
            var outcome = new EncodeOutcome();

            // A job can override the queue's default preset (used to pick the animation preset
            // for cartoons vs. the general one for live-action). Fall back to the constructor
            // defaults when the job doesn't specify its own.
            string presetPath = !string.IsNullOrEmpty(job.PresetPath) ? job.PresetPath : _presetPath;
            string presetName = !string.IsNullOrEmpty(job.PresetName)
                ? job.PresetName
                : (!string.IsNullOrEmpty(job.PresetPath) ? PresetInfo.GetPresetName(job.PresetPath) : _presetName);

            // Check the tool + preset actually exist on THIS machine before trying to encode, so
            // a mis-configured box (e.g. a rip-only server without HandBrake, or preset paths that
            // point at another PC's disk) fails with a clear, actionable message instead of an
            // opaque "exit -1". This is the common cause of an encode that "fails without trying".
            if (string.IsNullOrEmpty(_handBrakePath) || !File.Exists(_handBrakePath))
            {
                outcome.Success = false;
                outcome.Error = "HandBrakeCLI.exe was not found at '" + _handBrakePath +
                    "'. Is the HandBrake command-line tool installed on THIS machine, and is its " +
                    "path set correctly in Settings? (A rip-only server won't have it.)";
                Logger.Error("Encode " + job.ShortId + " aborted: " + outcome.Error);
                return outcome;
            }
            if (!string.IsNullOrEmpty(presetPath) && !File.Exists(presetPath))
            {
                outcome.Success = false;
                outcome.Error = "The HandBrake preset file was not found at '" + presetPath +
                    "'. On this machine that path doesn't exist — re-point it in Settings.";
                Logger.Error("Encode " + job.ShortId + " aborted: " + outcome.Error);
                return outcome;
            }
            if (string.IsNullOrEmpty(presetName))
            {
                outcome.Success = false;
                outcome.Error = "Couldn't read the preset name from the preset file: " + presetPath;
                return outcome;
            }
            if (!File.Exists(job.InputFile))
            {
                outcome.Success = false;
                outcome.Error = "Input file not found: " + job.InputFile;
                return outcome;
            }

            try
            {
                string outDir = Path.GetDirectoryName(job.OutputFile);
                if (!string.IsNullOrEmpty(outDir)) { Directory.CreateDirectory(outDir); }
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Error = "Couldn't create output folder: " + ex.Message;
                Logger.Error("Encode " + job.ShortId + " output folder failed.", ex);
                return outcome;
            }

            // -i input -o output --preset-import-file <file> -Z "<name>" --no-metadata --json
            // --no-metadata stops HandBrake copying the source's title tag through — MakeMKV
            // stamps the disc label (e.g. "Solo Leveling S1 D2") into every title, which would
            // otherwise show up as the player title for every episode. The correct per-episode
            // title is written afterwards from the file name (see MediaTagWriter).
            string args = string.Format(CultureInfo.InvariantCulture,
                "-i \"{0}\" -o \"{1}\" --preset-import-file \"{2}\" -Z \"{3}\" --no-metadata --json",
                job.InputFile, job.OutputFile, presetPath, presetName);

            Logger.Info("Encode " + job.ShortId + " running: HandBrakeCLI " + args);

            var parser = new HandBrakeProgressParser();
            int workError = 0;
            bool sawWorkDone = false;

            Action<string> handleLine = line =>
            {
                HandBrakeProgress p = parser.Feed(line);
                if (p == null) { return; }

                if (p.IsWorkDone)
                {
                    sawWorkDone = true;
                    workError = p.WorkError;
                    return;
                }

                if (onProgress != null)
                {
                    if (p.Percent >= 0)
                    {
                        onProgress(p.Percent, "Encoding");
                    }
                    else if (p.PassId < 0 && p.State == "WORKING")
                    {
                        // Subtitle-scan pass — keep the user informed without moving the bar.
                        onProgress(0, "Scanning subtitles");
                    }
                    else if (!string.IsNullOrEmpty(p.State))
                    {
                        onProgress(-1 == p.Percent ? 0 : p.Percent, p.State);
                    }
                }
            };

            // Capture HandBrake's own diagnostic output (its [HH:MM:SS] log lines, "No title
            // found", "Invalid preset", scan results, etc.) into a small ring buffer. HandBrake
            // writes these to stderr while --json progress goes to stdout. We dump the tail to
            // the log ONLY on failure, so a broken encode is diagnosable instead of a blind
            // "produced no output" — without spamming the log on every successful encode.
            var diag = new System.Collections.Generic.List<string>();
            const int maxDiag = 60;
            Action<string> stderrLine = line =>
            {
                if (!string.IsNullOrEmpty(line))
                {
                    diag.Add(line);
                    if (diag.Count > maxDiag) { diag.RemoveAt(0); }
                }
                handleLine(line); // still parse, in case a build emits WORKDONE on stderr
            };

            Action dumpDiag = () =>
            {
                if (diag.Count == 0) { return; }
                int take = Math.Min(diag.Count, 25);
                var tail = diag.GetRange(diag.Count - take, take);
                Logger.Error("Encode " + job.ShortId + " — HandBrake output (last " + take + " lines):" +
                    Environment.NewLine + string.Join(Environment.NewLine, tail));
            };

            int exit = ProcessRunner.RunStreaming(_handBrakePath, args, handleLine, stderrLine, cancel);

            if (cancel.IsCancellationRequested)
            {
                outcome.Success = false;
                outcome.Error = "Encode cancelled.";
                // Remove a partial output file so it can't be mistaken for a finished encode.
                TryDeletePartial(job.OutputFile);
                return outcome;
            }

            if (exit != 0 || (sawWorkDone && workError != 0))
            {
                outcome.Success = false;
                outcome.Error = "HandBrake reported an error (exit " + exit +
                                (sawWorkDone ? ", work error " + workError : "") + ").";
                Logger.Error("Encode " + job.ShortId + " failed: " + outcome.Error);
                dumpDiag();
                TryDeletePartial(job.OutputFile);
                return outcome;
            }

            // Confirm a real output file exists.
            try
            {
                var fi = new FileInfo(job.OutputFile);
                if (!fi.Exists || fi.Length == 0)
                {
                    outcome.Success = false;
                    outcome.Error = "HandBrake finished but produced no output file.";
                    Logger.Error("Encode " + job.ShortId + " produced no output.");
                    dumpDiag();
                    return outcome;
                }
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Error = "Couldn't verify the output file: " + ex.Message;
                Logger.Error("Encode " + job.ShortId + " output check failed.", ex);
                return outcome;
            }

            outcome.Success = true;
            Logger.Info("Encode " + job.ShortId + " completed -> " + job.OutputFile);
            return outcome;
        }

        private void TryDeletePartial(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) { File.Delete(path); }
            }
            catch
            {
                // Non-fatal; leaving a partial file is better than throwing here.
            }
        }
    }
}
