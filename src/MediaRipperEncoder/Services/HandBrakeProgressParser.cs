using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services
{
    /// <summary>One decoded HandBrake --json status update.</summary>
    public class HandBrakeProgress
    {
        public string State { get; set; }          // SCANNING, WORKING, MUXING, WORKDONE...
        public int Percent { get; set; }           // 0..100 for the encode pass, else -1
        public int PassId { get; set; }            // -1 = subtitle scan, >=0 = encode pass
        public bool IsWorkDone { get; set; }
        public int WorkError { get; set; }         // WORKDONE error code (0 = success)
    }

    /// <summary>
    /// Reads HandBrakeCLI's <c>--json</c> output. That output is a stream of pretty-printed
    /// JSON objects each introduced by a "Progress: {" line, e.g.:
    ///
    ///   Progress: {
    ///       "State": "WORKING",
    ///       "Working": { "PassID": 0, "Progress": 0.53, ... }
    ///   }
    ///
    /// We're fed one line at a time (as the process emits them), so this accumulates lines
    /// from the opening brace until the braces balance, then parses the whole object. This
    /// is more robust than HandBrake's plain text progress, which updates in place with
    /// carriage returns that don't split into lines cleanly.
    /// </summary>
    public class HandBrakeProgressParser
    {
        private readonly StringBuilder _buffer = new StringBuilder();
        private bool _capturing;
        private int _depth;

        /// <summary>
        /// Feed one output line. Returns a HandBrakeProgress when a complete object has been
        /// assembled and parsed, otherwise null.
        /// </summary>
        public HandBrakeProgress Feed(string line)
        {
            if (line == null) { return null; }

            if (!_capturing)
            {
                int marker = line.IndexOf("Progress:", StringComparison.Ordinal);
                if (marker < 0) { return null; }
                int brace = line.IndexOf('{', marker);
                if (brace < 0) { return null; }

                _capturing = true;
                _buffer.Clear();
                _depth = 0;
                return Consume(line.Substring(brace));
            }

            return Consume(line);
        }

        private HandBrakeProgress Consume(string fragment)
        {
            _buffer.Append(fragment).Append('\n');

            // The JSON here has no braces inside string values, so a raw brace count is a
            // safe way to detect the end of the object.
            foreach (char c in fragment)
            {
                if (c == '{') { _depth++; }
                else if (c == '}') { _depth--; }
            }

            if (_depth > 0) { return null; }

            // Balanced — parse and reset.
            string json = _buffer.ToString();
            _capturing = false;
            _buffer.Clear();

            return Parse(json);
        }

        private static HandBrakeProgress Parse(string json)
        {
            try
            {
                JObject root = JObject.Parse(json);
                var result = new HandBrakeProgress
                {
                    State = (string)root["State"] ?? "",
                    Percent = -1,
                    PassId = int.MinValue
                };

                if (result.State == "WORKDONE")
                {
                    result.IsWorkDone = true;
                    JToken wd = root["WorkDone"];
                    if (wd != null && wd["Error"] != null)
                    {
                        result.WorkError = (int)wd["Error"];
                    }
                    return result;
                }

                JToken working = root["Working"];
                if (working != null)
                {
                    if (working["PassID"] != null) { result.PassId = (int)working["PassID"]; }

                    // Only surface a percent for the actual encode pass (PassID >= 0); the
                    // subtitle-scan pass (PassID -1) runs to 100% first and would make the
                    // bar jump to full and then reset.
                    if (result.PassId >= 0 && working["Progress"] != null)
                    {
                        double frac = (double)working["Progress"];
                        int pct = (int)Math.Round(frac * 100.0);
                        if (pct < 0) { pct = 0; }
                        if (pct > 100) { pct = 100; }
                        result.Percent = pct;
                    }
                }

                return result;
            }
            catch
            {
                // A malformed block shouldn't kill progress reporting.
                return null;
            }
        }
    }
}
