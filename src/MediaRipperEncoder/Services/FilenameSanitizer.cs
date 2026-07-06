using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Turns a human-readable title into a string that is legal as a single Windows file or
    /// folder name. The on-screen metadata keeps its real punctuation ("AC/DC", "Batman: The
    /// Movie"); only the value written into a PATH is sanitized here.
    ///
    /// This sanitizes ONE path segment (a single folder or file name) — it treats path
    /// separators as illegal characters to be replaced, not as separators to preserve. Build a
    /// path by sanitizing each segment and then combining with Path.Combine.
    /// </summary>
    public static class FilenameSanitizer
    {
        // Per-character replacements. Separator-like characters become a dash so names stay
        // readable (the project's known test: "AC/DC" -> "AC-DC"); a colon gets spaces around
        // its dash because it usually separates a title from a subtitle ("Batman: The Movie" ->
        // "Batman - The Movie"). The remaining illegal characters are simply dropped.
        private static readonly Dictionary<char, string> Replacements = new Dictionary<char, string>
        {
            { '/',  "-" },
            { '\\', "-" },
            { '|',  "-" },
            { ':',  " - " },
            { '*',  "" },
            { '?',  "" },
            { '"',  "'" },
            { '<',  "" },
            { '>',  "" },
        };

        // Windows reserved device names — illegal as a whole file/folder name, with or without
        // an extension. We prefix a matched name with '_' to make it legal.
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(
            new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            });

        /// <summary>
        /// Sanitizes a single name segment. Never returns an empty string — an input that
        /// sanitizes away to nothing falls back to <paramref name="fallback"/>.
        /// </summary>
        public static string Sanitize(string name, string fallback = "Untitled")
        {
            if (string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                string replacement;
                if (Replacements.TryGetValue(c, out replacement))
                {
                    sb.Append(replacement);
                }
                else if (c < 32)
                {
                    // Drop control characters (0-31) entirely — they're illegal in paths.
                }
                else
                {
                    sb.Append(c);
                }
            }

            // Collapse any runs of whitespace introduced by replacements, then trim.
            string result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();

            // Windows forbids trailing dots or spaces on a name segment.
            result = result.TrimEnd('.', ' ');

            if (result.Length == 0)
            {
                return fallback;
            }

            // Guard against reserved device names (compare on the part before any extension).
            int dot = result.IndexOf('.');
            string stem = dot >= 0 ? result.Substring(0, dot) : result;
            if (ReservedNames.Contains(stem.ToUpper(CultureInfo.InvariantCulture)))
            {
                result = "_" + result;
            }

            return result;
        }
    }
}
