using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Confirms that configured paths point at real, working tools — the Phase 1
    /// requirement to never silently accept a path. The CLI checks actually launch the
    /// executable and look for its version banner, which catches the common mistakes:
    /// pointing at the wrong .exe (e.g. the HandBrake GUI instead of the CLI), a path
    /// that no longer exists, or a file that isn't runnable at all.
    /// </summary>
    public static class ToolValidator
    {
        // Version checks are quick; if a tool hasn't answered in 20 seconds something is
        // wrong (bad exe, hung process) and we'd rather report failure than hang the wizard.
        private const int VersionCheckTimeoutMs = 20000;

        /// <summary>
        /// Validates makemkvcon. Run with no arguments it prints a "MakeMKV v.." banner and
        /// usage text, so we launch it and confirm that banner appears. (makemkvcon has no
        /// clean "--version" flag, and its exit code with no args isn't reliably zero, so we
        /// match on the banner text rather than the exit code.)
        /// </summary>
        public static ValidationResult ValidateMakeMkv(string path)
        {
            ValidationResult basic = CheckExecutableFile(path, "MakeMKV CLI");
            if (basic != null) { return basic; }

            ProcessRunResult run = ProcessRunner.Run(path, string.Empty, VersionCheckTimeoutMs);

            ValidationResult launchProblem = CheckLaunchProblem(run, "MakeMKV CLI");
            if (launchProblem != null) { return launchProblem; }

            string output = run.CombinedOutput;
            if (output.IndexOf("makemkv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string version = ExtractFirstMatch(output, @"MakeMKV\s+v?[\d\.]+");
                string label = string.IsNullOrEmpty(version) ? "MakeMKV detected" : version.Trim();
                Logger.Info("MakeMKV validation passed: " + label);
                return ValidationResult.Ok("OK — " + label, output);
            }

            Logger.Error("MakeMKV validation failed; unexpected output from " + path);
            return ValidationResult.Fail(
                "This ran, but doesn't look like makemkvcon. Point this at makemkvcon.exe " +
                "(or makemkvcon64.exe) inside your MakeMKV install folder.",
                output);
        }

        /// <summary>
        /// Validates HandBrakeCLI via "--version", which prints "HandBrake x.y.z" and exits 0.
        /// On failure the message reminds the user that the CLI is a SEPARATE download from
        /// the HandBrake GUI — the single most common setup mistake.
        /// </summary>
        public static ValidationResult ValidateHandBrake(string path)
        {
            ValidationResult basic = CheckExecutableFile(path, "HandBrake CLI");
            if (basic != null) { return basic; }

            ProcessRunResult run = ProcessRunner.Run(path, "--version", VersionCheckTimeoutMs);

            ValidationResult launchProblem = CheckLaunchProblem(run, "HandBrake CLI");
            if (launchProblem != null) { return launchProblem; }

            string output = run.CombinedOutput;
            if (output.IndexOf("handbrake", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string version = ExtractFirstMatch(output, @"HandBrake\s+[\d\.]+");
                string label = string.IsNullOrEmpty(version) ? "HandBrake CLI detected" : version.Trim();
                Logger.Info("HandBrake validation passed: " + label);
                return ValidationResult.Ok("OK — " + label, output);
            }

            Logger.Error("HandBrake validation failed; unexpected output from " + path);
            return ValidationResult.Fail(
                "This ran, but doesn't look like HandBrakeCLI. Note the command-line version " +
                "is a SEPARATE download from the HandBrake app — get \"HandBrakeCLI\" from " +
                "handbrake.fr (Downloads → Command Line Version).",
                output);
        }

        /// <summary>
        /// Validates that the preset file exists, is valid JSON, and looks like a HandBrake
        /// preset export (has a "PresetList"). We don't run HandBrake here — just sanity
        /// check the file so a wrong file is caught before it fails mid-encode later.
        /// </summary>
        public static ValidationResult ValidatePresetFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ValidationResult.Fail("No HandBrake preset file selected.");
            }
            if (!File.Exists(path))
            {
                return ValidationResult.Fail("Preset file not found at: " + path);
            }

            try
            {
                string json = File.ReadAllText(path);
                JObject root = JObject.Parse(json);

                if (root["PresetList"] != null)
                {
                    // Try to surface the preset's own name for a friendlier confirmation.
                    string presetName = null;
                    JToken list = root["PresetList"];
                    if (list.Type == JTokenType.Array && list.HasValues)
                    {
                        JToken first = list.First;
                        if (first != null && first["PresetName"] != null)
                        {
                            presetName = first["PresetName"].ToString();
                        }
                    }

                    string label = string.IsNullOrEmpty(presetName)
                        ? "OK — valid HandBrake preset file"
                        : "OK — preset \"" + presetName + "\"";
                    return ValidationResult.Ok(label);
                }

                return ValidationResult.Fail(
                    "This is valid JSON but has no \"PresetList\" — it doesn't look like a " +
                    "HandBrake preset. In HandBrake, use Presets → Export to a file.");
            }
            catch (Exception ex)
            {
                Logger.Error("Preset validation failed for " + path, ex);
                return ValidationResult.Fail(
                    "Couldn't read this as a HandBrake preset. Make sure it's a .json file " +
                    "exported from HandBrake (Presets → Export).");
            }
        }

        // --- shared helpers ---

        /// <summary>
        /// Returns a failure result if the path is blank or the file is missing; returns
        /// null if the file exists (meaning "keep going and actually run it").
        /// </summary>
        private static ValidationResult CheckExecutableFile(string path, string toolLabel)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ValidationResult.Fail("No " + toolLabel + " path set yet.");
            }
            if (!File.Exists(path))
            {
                return ValidationResult.Fail(toolLabel + " not found at: " + path);
            }
            return null;
        }

        /// <summary>
        /// Turns "couldn't start" and "timed out" process outcomes into user-facing
        /// failures. Returns null if the process ran to completion normally.
        /// </summary>
        private static ValidationResult CheckLaunchProblem(ProcessRunResult run, string toolLabel)
        {
            if (!run.Started)
            {
                return ValidationResult.Fail(
                    "Couldn't launch " + toolLabel + ". The file may not be a valid program " +
                    "or you may not have permission to run it.",
                    run.Exception == null ? null : run.Exception.Message);
            }
            if (run.TimedOut)
            {
                return ValidationResult.Fail(
                    toolLabel + " didn't respond to a version check within 20 seconds. " +
                    "The path may point at the wrong program.");
            }
            return null;
        }

        private static string ExtractFirstMatch(string input, string pattern)
        {
            Match m = Regex.Match(input, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }
    }
}
