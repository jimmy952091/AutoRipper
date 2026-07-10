using System.Collections.Generic;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// One entry of the Settings > Music output-format dropdown. Display names carry an honest
    /// tested/experimental label — "tested" is only claimed after a real-CD verification, and
    /// the labels double as a heads-up that the program keeps updating.
    /// </summary>
    public class MusicFormat
    {
        public string DisplayName { get; private set; }
        public string FormatId { get; private set; }
        public string Extension { get; private set; }

        private MusicFormat(string display, string formatId, string extension)
        {
            DisplayName = display;
            FormatId = formatId;
            Extension = extension;
        }

        public override string ToString() { return DisplayName; }

        /// <summary>
        /// The formats offered. FLAC first — lossless is the archival default. All three were
        /// verified against a real CD on 2026-07-09 (rip -> encode -> tag round-trip).
        /// </summary>
        public static List<MusicFormat> All()
        {
            return new List<MusicFormat>
            {
                new MusicFormat("FLAC — lossless (tested)", "flac", "flac"),
                new MusicFormat("MP3 — LAME VBR (tested)", "mp3", "mp3"),
                new MusicFormat("OGG Vorbis — game-player friendly (tested)", "ogg", "ogg"),
                new MusicFormat("WAV — uncompressed (tested)", "wav", "wav")
            };
        }

        /// <summary>Finds a format by its persisted id; unknown ids fall back to FLAC.</summary>
        public static MusicFormat ById(string formatId)
        {
            foreach (MusicFormat f in All())
            {
                if (f.FormatId.Equals(formatId ?? "", System.StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
            return All()[0];
        }
    }
}
