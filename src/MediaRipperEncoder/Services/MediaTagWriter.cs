using System;
using System.IO;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Writes the embedded "Title" tag inside a finished media file so players (VLC) and the
    /// Windows Explorer Details pane show the episode/movie name — not the disc-level title that
    /// MakeMKV stamps in (e.g. "Solo Leveling S1 D2", which is identical for every episode on a
    /// disc). Uses TagLib#.
    ///
    /// Never throws: a tagging failure must not fail the encode or lose the file. It's logged and
    /// the file is left exactly as HandBrake produced it.
    /// </summary>
    public static class MediaTagWriter
    {
        /// <summary>
        /// The title to embed: the file's own name without its extension, so the on-screen title
        /// matches the file name exactly (what the user asked for).
        /// </summary>
        public static string TitleFromFileName(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) { return ""; }
            return Path.GetFileNameWithoutExtension(filePath);
        }

        public static void SetTitle(string filePath, string title)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) { return; }

            try
            {
                using (TagLib.File file = TagLib.File.Create(filePath))
                {
                    file.Tag.Title = title;
                    file.Save();
                }
                Logger.Info("Set title tag '" + title + "' on " + filePath);
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't set the title tag on " + filePath + " (leaving file as-is).", ex);
            }
        }
    }
}
