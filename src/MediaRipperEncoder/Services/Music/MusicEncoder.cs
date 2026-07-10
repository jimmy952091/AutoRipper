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
                case "opus":
                    EncodeOpus(wavPath, outputPath, onProgress);
                    break;
                case "m4a":
                    EncodeMediaFoundation(wavPath, outputPath, aac: true, onProgress);
                    break;
                case "wma":
                    EncodeMediaFoundation(wavPath, outputPath, aac: false, onProgress);
                    break;
                case "aiff":
                    EncodeAiff(wavPath, outputPath, onProgress);
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

        /// <summary>
        /// Opus at 160 kbps in an ogg container. Opus only accepts 48 kHz, so CD audio is
        /// resampled 44.1k -> 48k with NAudio's managed resampler first.
        /// </summary>
        private static void EncodeOpus(string wavPath, string opusPath, Action<int> onProgress)
        {
            using (var reader = new NAudio.Wave.WaveFileReader(wavPath))
            using (var output = new FileStream(opusPath, FileMode.Create, FileAccess.Write))
            {
                var resampled = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
                    NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader), 48000);
                var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 2,
                    Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
                encoder.Bitrate = 160000;
                var ogg = new Concentus.Oggfile.OpusOggWriteStream(encoder, output);

                long totalSamples = reader.SampleCount * 2; // per-channel count -> interleaved
                long done = 0;
                var floats = new float[9600]; // 100 ms of 48k stereo
                int read;
                while ((read = resampled.Read(floats, 0, floats.Length)) > 0)
                {
                    var shorts = new short[read];
                    for (int i = 0; i < read; i++)
                    {
                        shorts[i] = (short)Math.Max(short.MinValue,
                            Math.Min(short.MaxValue, floats[i] * 32767f));
                    }
                    ogg.WriteSamples(shorts, 0, read);
                    done += read;
                    if (onProgress != null && totalSamples > 0)
                    {
                        // done is in 48k samples vs total in 44.1k — scale close enough for a bar.
                        onProgress((int)Math.Min(100, done * 100 * 441 / 480 / totalSamples));
                    }
                }
                ogg.Finish();
                if (onProgress != null) { onProgress(100); }
            }
        }

        /// <summary>
        /// M4A/AAC or WMA via Windows' own Media Foundation encoders — nothing shipped by us.
        /// Present on stock Windows 7+; NOT under Wine (Media Foundation is largely unimplemented
        /// there), so the failure message points at the formats that always work.
        /// </summary>
        private static void EncodeMediaFoundation(string wavPath, string outputPath, bool aac,
            Action<int> onProgress)
        {
            try
            {
                NAudio.MediaFoundation.MediaFoundationApi.Startup();
                using (var reader = new NAudio.Wave.WaveFileReader(wavPath))
                {
                    if (aac)
                    {
                        NAudio.Wave.MediaFoundationEncoder.EncodeToAac(reader, outputPath, 192000);
                    }
                    else
                    {
                        NAudio.Wave.MediaFoundationEncoder.EncodeToWma(reader, outputPath, 192000);
                    }
                }
                if (onProgress != null) { onProgress(100); }
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    "This Windows couldn't provide its " + (aac ? "AAC" : "WMA") + " encoder (" +
                    ex.Message + "). FLAC, MP3, OGG, and Opus are built into AutoRipper and always work.", ex);
            }
        }

        /// <summary>
        /// AIFF: Apple's uncompressed container — same PCM as WAV but big-endian, so this is a
        /// header rewrite + byte swap. AIFF stores its sample rate as an 80-bit extended float.
        /// </summary>
        private static void EncodeAiff(string wavPath, string aiffPath, Action<int> onProgress)
        {
            using (var input = new FileStream(wavPath, FileMode.Open, FileAccess.Read))
            using (var w = new BinaryWriter(new FileStream(aiffPath, FileMode.Create, FileAccess.Write)))
            {
                input.Seek(44, SeekOrigin.Begin);
                long dataBytes = input.Length - 44;
                long frames = dataBytes / 4;

                w.Write("FORM".ToCharArray());
                WriteBE32(w, (int)(4 + 8 + 18 + 8 + 8 + dataBytes));
                w.Write("AIFF".ToCharArray());
                w.Write("COMM".ToCharArray());
                WriteBE32(w, 18);
                WriteBE16(w, 2);             // channels
                WriteBE32(w, (int)frames);   // sample frames
                WriteBE16(w, 16);            // bits per sample
                WriteExtended80(w, 44100);   // sample rate
                w.Write("SSND".ToCharArray());
                WriteBE32(w, (int)(dataBytes + 8));
                WriteBE32(w, 0);
                WriteBE32(w, 0);

                var buf = new byte[65536];
                long done = 0;
                int read;
                while ((read = input.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i + 1 < read; i += 2) // little-endian -> big-endian
                    {
                        byte t = buf[i]; buf[i] = buf[i + 1]; buf[i + 1] = t;
                    }
                    w.Write(buf, 0, read);
                    done += read;
                    if (onProgress != null && dataBytes > 0) { onProgress((int)(done * 100 / dataBytes)); }
                }
            }
        }

        private static void WriteBE16(BinaryWriter w, short v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }

        private static void WriteBE32(BinaryWriter w, int v)
        { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }

        /// <summary>IEEE 754 80-bit extended float — how AIFF stores the sample rate. 44100 = exponent 16398.</summary>
        private static void WriteExtended80(BinaryWriter w, int sampleRate)
        {
            WriteBE16(w, 16398);
            long mantissa = (long)sampleRate << (63 - 15);
            for (int i = 7; i >= 0; i--) { w.Write((byte)(mantissa >> (i * 8))); }
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
