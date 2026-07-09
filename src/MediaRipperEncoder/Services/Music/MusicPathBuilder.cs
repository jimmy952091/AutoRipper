using System.IO;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// Builds the library destination for one ripped audio track, per the project convention:
    ///
    ///   Music/&lt;Artist&gt;/&lt;Album&gt; (&lt;Year&gt;)/&lt;NN&gt; - &lt;Track&gt;.&lt;ext&gt;          (single-disc)
    ///   Music/&lt;Artist&gt;/&lt;Album&gt; (&lt;Year&gt;)/&lt;D&gt;-&lt;NN&gt; - &lt;Track&gt;.&lt;ext&gt;      (multi-disc)
    ///
    /// Year is required in the album folder (distinguishes reissues with different track lists);
    /// multi-disc releases use disc-aware numbers (1-01, 2-05) so two discs' tracks don't collide.
    /// Names go through the same <see cref="FilenameSanitizer"/> as video — the canonical test
    /// being AC/DC -> "AC-DC" as a folder while the tags inside the file keep "AC/DC".
    /// </summary>
    public static class MusicPathBuilder
    {
        private const int MaxFullPathLength = 250;

        public static LibraryTarget BuildTrack(string musicRoot, MusicRelease release,
            AudioTrack track, string extension)
        {
            string artist = FilenameSanitizer.Sanitize(release.Artist, "Unknown Artist");
            string album = FilenameSanitizer.Sanitize(release.Album, "Unknown Album");
            string albumFolder = string.IsNullOrWhiteSpace(release.Year)
                ? album
                : album + " (" + release.Year + ")";

            string folder = Path.Combine(musicRoot ?? "", artist, albumFolder);

            string number = release.DiscCount > 1
                ? release.DiscNumber + "-" + track.Number.ToString("00")
                : track.Number.ToString("00");

            string title = FilenameSanitizer.Sanitize(track.Title, "Track " + track.Number.ToString("00"));
            string ext = (extension ?? "flac").TrimStart('.').ToLowerInvariant();

            string fileName = number + " - " + title + "." + ext;

            // Same MAX_PATH guard as video: trim the descriptive title, never the number.
            int projected = Path.Combine(folder, fileName).Length;
            if (projected > MaxFullPathLength)
            {
                int overBy = projected - MaxFullPathLength;
                int keep = title.Length - overBy - 1;
                title = keep >= 1 ? title.Substring(0, keep).TrimEnd() + "…" : "";
                fileName = string.IsNullOrEmpty(title)
                    ? number + "." + ext
                    : number + " - " + title + "." + ext;
            }

            return new LibraryTarget
            {
                Folder = folder,
                FileName = fileName,
                FullPath = Path.Combine(folder, fileName)
            };
        }
    }
}
