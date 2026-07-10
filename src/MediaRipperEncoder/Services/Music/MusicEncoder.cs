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
                case "ogg":
                    EncodeOgg(wavPath, outputPath, onProgress);
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

        /// <summary>
        /// OGG Vorbis at VBR quality 0.5 (~160 kbps) — the format game music players expect
        /// (UT2004, Audiosurf). Pure managed encoder; CD audio is always 44.1kHz 16-bit stereo,
        /// so the WAV data can be read directly past the 44-byte header.
        /// </summary>
        private static void EncodeOgg(string wavPath, string oggPath, Action<int> onProgress)
        {
            using (var input = new FileStream(wavPath, FileMode.Open, FileAccess.Read))
            using (var output = new FileStream(oggPath, FileMode.Create, FileAccess.Write))
            {
                input.Seek(44, SeekOrigin.Begin);

                var info = OggVorbisEncoder.VorbisInfo.InitVariableBitRate(2, 44100, 0.5f);
                var oggStream = new OggVorbisEncoder.OggStream(new Random().Next());

                oggStream.PacketIn(OggVorbisEncoder.HeaderPacketBuilder.BuildInfoPacket(info));
                oggStream.PacketIn(OggVorbisEncoder.HeaderPacketBuilder.BuildCommentsPacket(
                    new OggVorbisEncoder.Comments()));
                oggStream.PacketIn(OggVorbisEncoder.HeaderPacketBuilder.BuildBooksPacket(info));
                OggVorbisEncoder.OggPage page;
                while (oggStream.PageOut(out page, true))
                {
                    output.Write(page.Header, 0, page.Header.Length);
                    output.Write(page.Body, 0, page.Body.Length);
                }

                var state = OggVorbisEncoder.ProcessingState.Create(info);
                var pcm = new byte[4096 * 4];
                var buffer = new[] { new float[4096], new float[4096] };
                long total = input.Length - 44;
                long done = 0;
                int read;
                while ((read = input.Read(pcm, 0, pcm.Length)) > 0)
                {
                    int samples = read / 4; // 2 channels x 16-bit
                    for (int i = 0; i < samples; i++)
                    {
                        buffer[0][i] = BitConverter.ToInt16(pcm, i * 4) / 32768f;
                        buffer[1][i] = BitConverter.ToInt16(pcm, i * 4 + 2) / 32768f;
                    }
                    state.WriteData(buffer, samples, 0);
                    FlushOggPackets(state, oggStream, output);

                    done += read;
                    if (onProgress != null && total > 0) { onProgress((int)(done * 100 / total)); }
                }
                state.WriteEndOfStream();
                FlushOggPackets(state, oggStream, output);
            }
        }

        private static void FlushOggPackets(OggVorbisEncoder.ProcessingState state,
            OggVorbisEncoder.OggStream oggStream, Stream output)
        {
            OggVorbisEncoder.OggPacket packet;
            OggVorbisEncoder.OggPage page;
            while (!oggStream.Finished && state.PacketOut(out packet))
            {
                oggStream.PacketIn(packet);
                while (!oggStream.Finished && oggStream.PageOut(out page, false))
                {
                    output.Write(page.Header, 0, page.Header.Length);
                    output.Write(page.Body, 0, page.Body.Length);
                }
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
