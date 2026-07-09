using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// Reads an audio CD's table of contents straight from the drive via
    /// IOCTL_CDROM_READ_TOC — the same raw-handle route the eject fallback uses, so it works
    /// on any drive Windows can see, with no third-party tool involved.
    ///
    /// The parse itself is a pure function over the returned buffer, so it's unit-testable
    /// with a crafted byte array (no physical disc needed).
    /// </summary>
    public static class CdTocReader
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_CDROM_READ_TOC = 0x00024000;

        // CDROM_TOC: 2-byte length, first track, last track, then 100 8-byte TRACK_DATA entries.
        private const int TocBufferSize = 4 + 100 * 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share,
            IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
            IntPtr inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);

        /// <summary>
        /// Reads the TOC of the disc in the given drive ("G:" / "G"). Throws IOException with a
        /// user-meaningful message when there's no readable disc.
        /// </summary>
        public static AudioCdToc Read(string driveLetter)
        {
            string letter = (driveLetter ?? "").Trim().TrimEnd(':', '\\');
            if (letter.Length != 1)
            {
                throw new ArgumentException("A drive letter like \"G:\" is required.", "driveLetter");
            }

            using (SafeFileHandle handle = CreateFile(@"\\.\" + letter + ":",
                GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new IOException("Couldn't open drive " + letter + ": (error " +
                        Marshal.GetLastWin32Error() + "). Is a disc inserted?");
                }

                var buffer = new byte[TocBufferSize];
                int returned;
                if (!DeviceIoControl(handle, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0,
                        buffer, buffer.Length, out returned, IntPtr.Zero))
                {
                    throw new IOException("Couldn't read the disc's table of contents (error " +
                        Marshal.GetLastWin32Error() + "). Is an AUDIO CD inserted?");
                }

                return ParseToc(buffer);
            }
        }

        /// <summary>
        /// Parses a CDROM_TOC buffer. Track addresses come back as MSF (minute/second/frame);
        /// offset-in-frames = M*4500 + S*75 + F, which conveniently already includes the
        /// standard 150-frame lead-in MusicBrainz expects. Data tracks (control bit 0x04, e.g.
        /// the data session of an "enhanced" CD) are excluded from the audio track list.
        /// </summary>
        public static AudioCdToc ParseToc(byte[] buffer)
        {
            var toc = new AudioCdToc
            {
                FirstTrack = buffer[2],
                LastTrack = buffer[3]
            };

            int entryCount = (buffer[3] - buffer[2] + 2); // tracks + lead-out entry
            for (int i = 0; i < entryCount && i < 100; i++)
            {
                int b = 4 + i * 8;
                int control = buffer[b + 1] & 0x0F;
                int trackNumber = buffer[b + 2];
                int frames = buffer[b + 5] * 4500 + buffer[b + 6] * 75 + buffer[b + 7];

                if (trackNumber == 0xAA)
                {
                    toc.LeadOutOffset = frames;
                }
                else if ((control & 0x04) == 0) // audio track (bit 2 set = data)
                {
                    toc.TrackOffsets.Add(frames);
                }
            }

            // If data tracks were skipped, the numeric range must match the audio list.
            toc.LastTrack = toc.FirstTrack + toc.TrackOffsets.Count - 1;
            return toc;
        }
    }
}
