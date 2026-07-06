namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Outcome of validating a single configured path (a CLI tool, a preset, a folder).
    /// Carries a user-facing message that, on failure, explains what to do about it —
    /// not just that something is wrong.
    /// </summary>
    public class ValidationResult
    {
        public bool Success { get; private set; }

        /// <summary>Short, user-facing status line (e.g. "OK — HandBrake 1.7.3").</summary>
        public string Message { get; private set; }

        /// <summary>
        /// Optional longer detail (e.g. captured tool output) for the log or a tooltip.
        /// Not required for the UI to make sense.
        /// </summary>
        public string Detail { get; private set; }

        private ValidationResult(bool success, string message, string detail)
        {
            Success = success;
            Message = message;
            Detail = detail;
        }

        public static ValidationResult Ok(string message, string detail = null)
        {
            return new ValidationResult(true, message, detail);
        }

        public static ValidationResult Fail(string message, string detail = null)
        {
            return new ValidationResult(false, message, detail);
        }
    }
}
