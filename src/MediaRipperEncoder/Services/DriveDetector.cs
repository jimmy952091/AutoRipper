using System;
using System.Collections.Generic;
using System.Management;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Enumerates the machine's optical drives via WMI (Win32_CDROMDrive). This is how the
    /// UI populates its drive selector, and it can be re-run at any time so drives added or
    /// removed while the app is open are picked up (the spec requires re-scanning support).
    ///
    /// WMI can occasionally be slow or throw on odd systems, so this never lets an exception
    /// escape — a failed scan returns an empty list and logs the reason rather than crashing
    /// the UI.
    /// </summary>
    public static class DriveDetector
    {
        public static List<OpticalDrive> GetOpticalDrives()
        {
            var drives = new List<OpticalDrive>();

            try
            {
                // Win32_CDROMDrive covers CD/DVD/Blu-ray/UHD optical drives. "Drive" is the
                // assigned letter (e.g. "E:"), "Name"/"Caption" the model string.
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Drive, Name, Caption, DeviceID FROM Win32_CDROMDrive"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        var drive = new OpticalDrive
                        {
                            DriveLetter = GetString(mo, "Drive"),
                            Model = FirstNonEmpty(GetString(mo, "Name"), GetString(mo, "Caption")),
                            DeviceId = GetString(mo, "DeviceID")
                        };
                        drives.Add(drive);
                        // Dispose each management object to release COM handles promptly.
                        mo.Dispose();
                    }
                }

                Logger.Info("Drive scan found " + drives.Count + " optical drive(s).");
            }
            catch (Exception ex)
            {
                Logger.Error("Optical drive scan failed.", ex);
            }

            return drives;
        }

        private static string GetString(ManagementObject mo, string property)
        {
            try
            {
                object value = mo[property];
                return value == null ? "" : value.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }

        private static string FirstNonEmpty(string a, string b)
        {
            if (!string.IsNullOrEmpty(a)) { return a; }
            return b ?? "";
        }
    }
}
