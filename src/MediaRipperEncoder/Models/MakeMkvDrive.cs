namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// One optical drive as MakeMKV sees it (from a robot-mode "DRV:" line). MakeMKV
    /// addresses discs by its own drive index (disc:N), which is NOT the Windows drive
    /// letter — so we parse these to map a user-selected Windows drive to the right index.
    ///
    /// On Windows, MakeMKV helpfully reports the drive letter as the device name, e.g.
    /// DRV:0,0,999,0,"BD-RE HL-DT-ST ... WH16NS60 ...","","G:"
    /// </summary>
    public class MakeMkvDrive
    {
        /// <summary>MakeMKV's drive index — the N in "disc:N".</summary>
        public int Index { get; set; }

        /// <summary>Drive model/firmware string reported by MakeMKV.</summary>
        public string Name { get; set; }

        /// <summary>Volume label of the inserted disc, or empty if none/unknown.</summary>
        public string DiscName { get; set; }

        /// <summary>Device name — on Windows this is the drive letter, e.g. "G:".</summary>
        public string DeviceName { get; set; }

        /// <summary>True if this DRV slot actually holds a drive (has a model name).</summary>
        public bool IsPresent
        {
            get { return !string.IsNullOrEmpty(Name); }
        }

        public MakeMkvDrive()
        {
            Name = "";
            DiscName = "";
            DeviceName = "";
        }
    }
}
