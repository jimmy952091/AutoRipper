using System.Collections.Generic;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>One entry of the output-format dropdown: display name + freaccmd encoder id + extension.</summary>
    public class MusicFormat
    {
        public string DisplayName { get; private set; }
        public string EncoderId { get; private set; }
        public string Extension { get; private set; }

        private MusicFormat(string display, string encoderId, string extension)
        {
            DisplayName = display;
            EncoderId = encoderId;
            Extension = extension;
        }

        public override string ToString() { return DisplayName; }

        /// <summary>The formats offered in the dropdown. FLAC first — lossless is the archival default.</summary>
        public static List<MusicFormat> All()
        {
            return new List<MusicFormat>
            {
                new MusicFormat("FLAC (lossless)", "flac", "flac"),
                new MusicFormat("MP3 (LAME)", "lame", "mp3"),
                new MusicFormat("OGG Vorbis", "vorbis", "ogg"),
                new MusicFormat("WAV (uncompressed)", "wave", "wav")
            };
        }
    }
}
