using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// Rips audio CD tracks with NO external tool: raw digital audio extraction through the same
    /// raw-drive-handle route as <see cref="CdTocReader"/>, via IOCTL_CDROM_RAW_READ. Each CD
    /// sector is 2352 bytes of plain 44.1kHz 16-bit stereo PCM, written out as a standard WAV.
    ///
    /// This replaced the fre:ac dependency after Defender flagged its installer — an unsigned
    /// third-party download was exactly the trust problem this app tries not to have. Built-in
    /// ripping means the Music feature needs nothing the user doesn't already have.
    /// </summary>
    public static class CdAudioReader
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint IOCTL_CDROM_RAW_READ = 0x0002403E;

        private const int RawSectorBytes = 2352;   // one CDDA sector = 1/75th second of PCM
        private const int CookedSectorBytes = 2048; // DiskOffset is expressed in cooked sectors
        private const int SectorsPerRead = 20;      // ~46 KB per DeviceIoControl round-trip

        private enum TrackModeType { YellowMode2 = 0, XAForm2 = 1, CDDA = 2 }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAW_READ_INFO
        {
            public long DiskOffset;     // BYTE offset = LBA * 2048
            public uint SectorCount;
            public TrackModeType TrackMode;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share,
            IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
            ref RAW_READ_INFO inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize,
            out int bytesReturned, IntPtr overlapped);

        /// <summary>
        /// Rips one track (frame offsets from the TOC, which include the 150-frame lead-in) to a
        /// WAV file. Reports progress as (framesDone, framesTotal). Throws IOException on read
        /// failure — callers treat a failed track like a failed rip title (log, mark, continue).
        /// </summary>
        public static void RipTrackToWav(string driveLetter, int startFrame, int endFrame,
            string outputWavPath, Action<int, int> onProgress, System.Threading.CancellationToken cancel)
        {
            string letter = (driveLetter ?? "").Trim().TrimEnd(':', '\\');
            if (letter.Length != 1) { throw new ArgumentException("Drive letter required.", "driveLetter"); }

            int totalFrames = endFrame - startFrame;
            if (totalFrames <= 0) { throw new ArgumentException("Track has no length."); }

            using (SafeFileHandle handle = CreateFile(@"\\.\" + letter + ":",
                GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new IOException("Couldn't open drive " + letter + ": (error " +
                        Marshal.GetLastWin32Error() + ").");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputWavPath));
                using (var fs = new FileStream(outputWavPath, FileMode.Create, FileAccess.Write))
                {
                    WriteWavHeader(fs, totalFrames * RawSectorBytes);

                    var buffer = new byte[SectorsPerRead * RawSectorBytes];
                    int framesDone = 0;

                    while (framesDone < totalFrames)
                    {
                        cancel.ThrowIfCancellationRequested();

                        int framesThisRead = Math.Min(SectorsPerRead, totalFrames - framesDone);
                        // TOC offsets include the 150-frame lead-in; the device address space
                        // doesn't — subtract it to get the LBA, then scale to a byte offset in
                        // COOKED (2048-byte) sectors as IOCTL_CDROM_RAW_READ expects.
                        long lba = (startFrame - 150) + framesDone;
                        var info = new RAW_READ_INFO
                        {
                            DiskOffset = lba * CookedSectorBytes,
                            SectorCount = (uint)framesThisRead,
                            TrackMode = TrackModeType.CDDA
                        };

                        int returned;
                        if (!DeviceIoControl(handle, IOCTL_CDROM_RAW_READ, ref info, Marshal.SizeOf(info),
                                buffer, framesThisRead * RawSectorBytes, out returned, IntPtr.Zero))
                        {
                            throw new IOException("Raw audio read failed at frame " + (startFrame + framesDone) +
                                " (error " + Marshal.GetLastWin32Error() + "). Dirty/scratched disc?");
                        }

                        fs.Write(buffer, 0, returned);
                        framesDone += framesThisRead;
                        if (onProgress != null) { onProgress(framesDone, totalFrames); }
                    }
                }
            }
        }

        /// <summary>Standard 44-byte RIFF/WAVE header for CD audio (44100 Hz, 16-bit, stereo).</summary>
        public static void WriteWavHeader(Stream stream, int pcmDataBytes)
        {
            using (var w = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + pcmDataBytes);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);                 // fmt chunk size
                w.Write((short)1);           // PCM
                w.Write((short)2);           // stereo
                w.Write(44100);              // sample rate
                w.Write(44100 * 2 * 2);      // byte rate
                w.Write((short)4);           // block align
                w.Write((short)16);          // bits per sample
                w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                w.Write(pcmDataBytes);
            }
        }
    }
}
