using System;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// The captured outcome of running an external process: exit code plus everything it
    /// printed. Used by validation now, and by the rip/encode pipeline later — every
    /// subprocess call in this app funnels through a runner that produces one of these so
    /// nothing is ever assumed to have "just worked."
    /// </summary>
    public class ProcessRunResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }

        /// <summary>True if the process had to be killed for exceeding its timeout.</summary>
        public bool TimedOut { get; set; }

        /// <summary>Set if the process could not be started at all (e.g. file not found).</summary>
        public Exception Exception { get; set; }

        public ProcessRunResult()
        {
            StandardOutput = "";
            StandardError = "";
        }

        /// <summary>Combined stdout + stderr, convenient for scanning for a version banner.</summary>
        public string CombinedOutput
        {
            get { return (StandardOutput + Environment.NewLine + StandardError); }
        }

        public bool Started
        {
            get { return Exception == null; }
        }
    }
}
