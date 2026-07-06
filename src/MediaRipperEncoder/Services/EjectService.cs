using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaRipperEncoder.Services
{
    public enum EjectMethod
    {
        None,
        Mci,
        DeviceIoControl
    }

    /// <summary>Outcome of an eject attempt: whether it worked and which method got there.</summary>
    public class EjectResult
    {
        public bool Success { get; set; }
        public EjectMethod Method { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Ejects a specific optical drive. Two methods, tried in order per the project spec:
    ///
    ///  1. MCI (winmm.dll mciSendString) — the friendly, well-supported path. We open the
    ///     drive by its letter with a private alias so we target THAT drive, not just the
    ///     system's default optical drive (matters on machines with more than one drive).
    ///
    ///  2. DeviceIoControl(IOCTL_STORAGE_EJECT_MEDIA) on a raw handle — the low-level
    ///     fallback for drives that don't respond correctly to MCI (some reflashed/modified
    ///     drives, like the user's WH16NS60, can be in this camp).
    ///
    /// IMPORTANT: "success" here means the OS accepted the eject request. On slim
    /// manual-load laptop trays the drive only releases the latch (the tray doesn't motor
    /// out), so we must NOT judge success by whether media physically left the drive.
    /// </summary>
    public static class EjectService
    {
        public static EjectResult Eject(string driveLetter)
        {
            string letter = NormalizeLetter(driveLetter); // e.g. "E"
            if (string.IsNullOrEmpty(letter))
            {
                return new EjectResult
                {
                    Success = false,
                    Method = EjectMethod.None,
                    Message = "No drive letter to eject."
                };
            }

            // --- Method 1: MCI ---
            string mciError;
            if (TryEjectWithMci(letter, out mciError))
            {
                Logger.Info("Ejected " + letter + ": via MCI.");
                return new EjectResult
                {
                    Success = true,
                    Method = EjectMethod.Mci,
                    Message = "Ejected via MCI (winmm)."
                };
            }
            Logger.Info("MCI eject of " + letter + ": failed (" + mciError + "); trying DeviceIoControl.");

            // --- Method 2: DeviceIoControl fallback ---
            string ioError;
            if (TryEjectWithDeviceIoControl(letter, out ioError))
            {
                Logger.Info("Ejected " + letter + ": via DeviceIoControl.");
                return new EjectResult
                {
                    Success = true,
                    Method = EjectMethod.DeviceIoControl,
                    Message = "Ejected via DeviceIoControl (fallback)."
                };
            }

            // Both failed — surface it; the user needs to know to eject manually.
            string combined = "MCI: " + mciError + " | DeviceIoControl: " + ioError;
            Logger.Error("Both eject methods failed for " + letter + ": " + combined);
            return new EjectResult
            {
                Success = false,
                Method = EjectMethod.None,
                Message = "Couldn't eject automatically — please eject the disc manually. (" + combined + ")"
            };
        }

        // --- MCI (winmm) ---

        private static bool TryEjectWithMci(string letter, out string error)
        {
            error = "";
            // A unique alias so we don't clash with any other MCI device that might be open.
            string alias = "mre_eject_" + letter;
            string device = letter + ":";

            // Open the specific drive as a CDAudio device. "wait" makes each call synchronous.
            int rc = mciSendString("open " + device + " type CDAudio alias " + alias + " wait",
                null, 0, IntPtr.Zero);
            if (rc != 0)
            {
                error = "open failed (" + DescribeMciError(rc) + ")";
                return false;
            }

            try
            {
                rc = mciSendString("set " + alias + " door open wait", null, 0, IntPtr.Zero);
                if (rc != 0)
                {
                    error = "door open failed (" + DescribeMciError(rc) + ")";
                    return false;
                }
                return true;
            }
            finally
            {
                // Always release the MCI device, even if the door command failed.
                mciSendString("close " + alias + " wait", null, 0, IntPtr.Zero);
            }
        }

        private static string DescribeMciError(int code)
        {
            var sb = new StringBuilder(256);
            // Translates an MCI error code into a readable message when possible.
            if (mciGetErrorString(code, sb, sb.Capacity))
            {
                return sb.ToString();
            }
            return "MCI code " + code;
        }

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int mciSendString(string command, StringBuilder returnValue,
            int returnLength, IntPtr callback);

        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern bool mciGetErrorString(int errorCode, StringBuilder message, int length);

        // --- DeviceIoControl fallback ---

        private static bool TryEjectWithDeviceIoControl(string letter, out string error)
        {
            error = "";

            // Raw device path for the volume, e.g. \\.\E:  (the leading \\.\ names the device
            // rather than a file path). GENERIC_READ + share read/write is enough to issue
            // the eject control code.
            string rawPath = @"\\.\" + letter + ":";

            SafeFileHandle handle = CreateFile(rawPath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                error = "couldn't open device (Win32 error " + Marshal.GetLastWin32Error() + ")";
                return false;
            }

            try
            {
                uint bytesReturned;
                bool ok = DeviceIoControl(handle, IOCTL_STORAGE_EJECT_MEDIA,
                    IntPtr.Zero, 0, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero);

                if (!ok)
                {
                    error = "eject IOCTL failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                    return false;
                }
                return true;
            }
            finally
            {
                handle.Dispose();
            }
        }

        // Win32 constants
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        // IOCTL_STORAGE_EJECT_MEDIA — the storage control code that ejects removable media.
        private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
            IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        // --- helpers ---

        /// <summary>Pulls the bare drive letter out of "E", "E:", "E:\", etc. Empty if none.</summary>
        private static string NormalizeLetter(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) { return ""; }
            char c = char.ToUpperInvariant(input.Trim()[0]);
            return (c >= 'A' && c <= 'Z') ? c.ToString() : "";
        }
    }
}
