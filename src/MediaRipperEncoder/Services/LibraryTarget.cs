namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// The computed destination for one output file: the folder it belongs in, its final file
    /// name, and the two joined. Purely describes a target — nothing is created or moved here.
    /// </summary>
    public class LibraryTarget
    {
        /// <summary>Absolute folder the file belongs in (may not exist yet).</summary>
        public string Folder { get; set; }

        /// <summary>Sanitized file name including extension, e.g. "S01E01 (Pilot).mp4".</summary>
        public string FileName { get; set; }

        /// <summary>Folder + FileName.</summary>
        public string FullPath { get; set; }

        public LibraryTarget()
        {
            Folder = "";
            FileName = "";
            FullPath = "";
        }
    }
}
