using System;
using System.IO;
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using NAudio.Lame;
using NAudio.Wave;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// Encodes a ripped WAV to the user's chosen music format, entirely in-process — the
    /// managed Flake encoder for FLAC (the same codec family CUERipper uses) and managed LAME
    /// for MP3. No external tool, nothing for Defender to distrust. All formats verified
    /// against a real CD rip (Creed — Greatest Hits, 2026-07-09).
    /// </summary>
    public static class MusicEncoder
    {
        /// <summary>
        /// Encodes <paramref name="wavPath"/> to <paramref name="outputPath"/> in the given
        /// format ("flac" / "mp3" / "wav"). Progress is (percent). Throws on failure — the
        /// encode queue turns that into a per-job failure without stopping the queue.
        /// </summary>
        public static void Encode(string wavPath, string outputPath, string formatId,
            Action<int> onProgress)
        {
            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }

            switch ((formatId ?? "flac").ToLowerInvariant())
            {
                case "flac":
                    EncodeFlac(wavPath, outputPath, onProgress);
                    break;
                case "mp3":
                    EncodeMp3(wavPath, outputPath, onProgress);
                    break;
                case "wav":
                    File.Copy(wavPath, outputPath, overwrite: true);
                    if (onProgress != null) { onProgress(100); }
                    break;
                default:
                    throw new NotSupportedException("Unknown music format '" + formatId + "'.");
            }
        }

        private static void EncodeFlac(string wavPath, string flacPath, Action<int> onProgress)
        {
            var reader = new WAVReader(wavPath, null);
            try
            {
                var buffer = new AudioBuffer(reader, 0x10000);
                var writer = new FlakeWriter(flacPath, reader.PCM);
                try
                {
                    writer.CompressionLevel = 8; // max standard FLAC compression
                    long total = reader.Length;  // total samples
                    long done = 0;
                    while (reader.Read(buffer, -1) > 0)
                    {
                        writer.Write(buffer);
                        done += buffer.Length;
                        if (onProgress != null && total > 0)
                        {
                            onProgress((int)(done * 100 / total));
                        }
                    }
                }
                finally
                {
                    writer.Close();
                }
            }
            finally
            {
                reader.Close();
            }
        }

        private static void EncodeMp3(string wavPath, string mp3Path, Action<int> onProgress)
        {
            using (var reader = new WaveFileReader(wavPath))
            using (var writer = new LameMP3FileWriter(mp3Path, reader.WaveFormat, LAMEPreset.STANDARD))
            {
                var chunk = new byte[64 * 1024];
                long total = reader.Length;
                long done = 0;
                int read;
                while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
                {
                    writer.Write(chunk, 0, read);
                    done += read;
                    if (onProgress != null && total > 0)
                    {
                        onProgress((int)(done * 100 / total));
                    }
                }
            }
        }
    }
}
