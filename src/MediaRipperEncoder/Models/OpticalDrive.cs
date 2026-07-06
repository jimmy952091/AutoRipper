namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// One optical drive as discovered on the system. Kept deliberately small — just what
    /// the UI and the rip/eject code need to identify and address a specific drive.
    /// </summary>
    public class OpticalDrive
    {
        /// <summary>Drive letter with colon, e.g. "E:". May be empty if Windows hasn't
        /// assigned one (rare, but possible for a drive with no media and odd drivers).</summary>
        public string DriveLetter { get; set; }

        /// <summary>Manufacturer/model string from WMI, e.g. "HL-DT-ST BD-RE WH16NS60".</summary>
        public string Model { get; set; }

        /// <summary>
        /// The raw device ID from WMI (e.g. \\.\CDROM0-style / PNPDeviceID). Not shown to
        /// the user, but kept because the DeviceIoControl eject fallback needs a raw device
        /// path and this helps us build/verify it.
        /// </summary>
        public string DeviceId { get; set; }

        public OpticalDrive()
        {
            DriveLetter = "";
            Model = "";
            DeviceId = "";
        }

        /// <summary>Friendly one-line label for the drive dropdown, e.g. "E: — HL-DT-ST WH16NS60".</summary>
        public override string ToString()
        {
            string letter = string.IsNullOrEmpty(DriveLetter) ? "(no letter)" : DriveLetter;
            string model = string.IsNullOrEmpty(Model) ? "Optical drive" : Model;
            return letter + " — " + model;
        }
    }
}
