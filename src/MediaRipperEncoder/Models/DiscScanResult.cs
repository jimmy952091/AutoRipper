using System.Collections.Generic;

namespace MediaRipperEncoder.Models
{
    /// <summary>Result of scanning a disc with MakeMKV: the titles found, plus success/error.</summary>
    public class DiscScanResult
    {
        public int DiscIndex { get; set; }

        /// <summary>Disc volume label if MakeMKV reported one.</summary>
        public string DiscName { get; set; }

        public List<DiscTitle> Titles { get; set; }

        /// <summary>Notable MakeMKV messages captured during the scan (for logs/diagnostics).</summary>
        public List<string> Messages { get; set; }

        public bool Success { get; set; }

        /// <summary>User-facing explanation when Success is false.</summary>
        public string Error { get; set; }

        public DiscScanResult()
        {
            DiscName = "";
            Titles = new List<DiscTitle>();
            Messages = new List<string>();
            Error = "";
        }
    }
}
