using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Runs an external console executable and captures its output with a hard timeout.
    ///
    /// Reads stdout and stderr on background callbacks (BeginOutputReadLine /
    /// BeginErrorReadLine) instead of reading the streams inline. This avoids the classic
    /// deadlock where a child process fills one pipe's buffer and blocks while the parent
    /// is blocked reading the other pipe.
    /// </summary>
    public static class ProcessRunner
    {
        public static ProcessRunResult Run(string exePath, string arguments, int timeoutMs)
        {
            var result = new ProcessRunResult();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = exePath;
                    process.StartInfo.Arguments = arguments ?? string.Empty;
                    process.StartInfo.UseShellExecute = false;      // required to redirect streams
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;         // no flashing console window

                    // Run in the exe's own folder so tools that look for sibling files
                    // (DLLs, data files) behave the same as when launched normally.
                    string workingDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        process.StartInfo.WorkingDirectory = workingDir;
                    }

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null) { stdout.AppendLine(e.Data); }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null) { stderr.AppendLine(e.Data); }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (process.WaitForExit(timeoutMs))
                    {
                        // Second, parameterless WaitForExit ensures the async output
                        // callbacks have fully flushed before we read the buffers.
                        process.WaitForExit();
                        result.ExitCode = process.ExitCode;
                    }
                    else
                    {
                        result.TimedOut = true;
                        try { process.Kill(); }
                        catch { /* process may have exited between the wait and the kill */ }
                    }
                }
            }
            catch (Exception ex)
            {
                // Most commonly a Win32Exception when the file isn't a runnable executable.
                result.Exception = ex;
            }

            result.StandardOutput = stdout.ToString();
            result.StandardError = stderr.ToString();
            return result;
        }

        /// <summary>
        /// Runs a long-running process (like a MakeMKV rip) and delivers stdout/stderr to
        /// callbacks line-by-line as they arrive, so the UI can show live progress instead
        /// of waiting for the whole thing to finish. There is deliberately NO timeout — a
        /// Blu-ray rip can legitimately run well over half an hour — but a CancellationToken
        /// can kill the process if the user cancels.
        ///
        /// Returns the process exit code, or -1 if it couldn't be started / was cancelled.
        /// </summary>
        public static int RunStreaming(string exePath, string arguments,
            Action<string> onStdoutLine, Action<string> onStderrLine, CancellationToken cancel)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = exePath;
                    process.StartInfo.Arguments = arguments ?? string.Empty;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;

                    string workingDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(workingDir))
                    {
                        process.StartInfo.WorkingDirectory = workingDir;
                    }

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null && onStdoutLine != null) { onStdoutLine(e.Data); }
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null && onStderrLine != null) { onStderrLine(e.Data); }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Poll for exit so we can honour cancellation without blocking forever.
                    while (!process.WaitForExit(250))
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            try { process.Kill(); }
                            catch { /* already exiting */ }
                            return -1;
                        }
                    }

                    // Flush any remaining async output.
                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("RunStreaming failed to launch " + exePath, ex);
                return -1;
            }
        }
    }
}
