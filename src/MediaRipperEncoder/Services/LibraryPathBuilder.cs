using System.Collections.Generic;
using System.IO;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Turns a confirmed <see cref="MediaMetadata"/> + one <see cref="TitleMapping"/> into the
    /// exact Plex/Jellyfin-compliant folder and file name the output should land at.
    ///
    /// Layouts (from the project spec):
    ///   Movies:  &lt;MoviesRoot&gt;/&lt;Title&gt; (&lt;Year&gt;)/&lt;Title&gt; (&lt;Year&gt;).&lt;ext&gt;
    ///   TV:      &lt;TvRoot&gt;/&lt;Show&gt;/Season &lt;NN&gt;/S&lt;NN&gt;E&lt;NN&gt; (&lt;Episode&gt;).&lt;ext&gt;
    ///   TV multi: ...Season &lt;NN&gt;/S&lt;NN&gt;E&lt;NN&gt;-E&lt;NN&gt; (&lt;Name A&gt; &amp; &lt;Name B&gt;).&lt;ext&gt;
    ///
    /// Every name segment is run through <see cref="FilenameSanitizer"/>, so illegal characters
    /// never reach the filesystem while the on-screen metadata keeps its real punctuation.
    /// </summary>
    public static class LibraryPathBuilder
    {
        // Keep full paths comfortably under the classic Windows MAX_PATH (260) so we never
        // fail to create a file because a multi-segment episode name ran long. If a computed
        // path would exceed this, the descriptive name portion is trimmed (never the S/E code).
        private const int MaxFullPathLength = 250;

        /// <summary>Builds the Movie destination. Year is optional but strongly recommended.</summary>
        public static LibraryTarget BuildMovie(string moviesRoot, MediaMetadata meta, string ext)
        {
            return BuildMovie(moviesRoot, meta.MovieTitle, meta.Year, ext);
        }

        /// <summary>
        /// Builds a Movie destination from an explicit title/year — used for one film on a
        /// multi-movie (double-feature) disc, where each mapped title is its own movie.
        /// </summary>
        public static LibraryTarget BuildMovie(string moviesRoot, string movieTitle, string year, string ext)
        {
            if (string.IsNullOrWhiteSpace(movieTitle))
            {
                // Upstream guards (metadata form + server-side job validation) should make this
                // unreachable — if it fires anyway, say so LOUDLY in the log so the incident can
                // be traced instead of quietly shipping an "Untitled Movie" folder again.
                Logger.Error("BuildMovie called with a BLANK movie title — placing as 'Untitled Movie'. " +
                             "This should have been rejected upstream; please report how this happened.");
            }
            string title = FilenameSanitizer.Sanitize(movieTitle, "Untitled Movie");
            string folderName = string.IsNullOrWhiteSpace(year)
                ? title
                : title + " (" + year + ")";

            string folder = Path.Combine(moviesRoot ?? "", folderName);
            string fileName = folderName + "." + NormalizeExt(ext);

            return Finish(folder, fileName);
        }

        /// <summary>
        /// Builds a TV episode destination for one title mapping. Handles both single-episode
        /// files and multi-episode (segmented) files. Assumes the mapping has at least one
        /// episode; callers should skip mappings that are excluded or have no episodes.
        /// </summary>
        public static LibraryTarget BuildTvEpisode(string tvRoot, MediaMetadata meta, TitleMapping mapping, string ext)
        {
            string show = FilenameSanitizer.Sanitize(meta.ShowName, "Unknown Show");
            int season = mapping.SeasonNumber > 0 ? mapping.SeasonNumber : meta.SeasonNumber;

            string seasonFolder = "Season " + PadSeason(season);
            string folder = Path.Combine(tvRoot ?? "", show, seasonFolder);

            string episodeCode = BuildEpisodeCode(season, mapping);
            string namePart = BuildEpisodeNamePart(mapping);
            string ex = NormalizeExt(ext);

            // Compose "CODE (Name).ext"; if the whole path is too long, trim only the name.
            string fileName = ComposeWithLengthGuard(folder, episodeCode, namePart, ex);
            return Finish(folder, fileName);
        }

        /// <summary>
        /// "S01E001" for a single episode, "S01E001-E002" for a multi-episode file. Episode
        /// numbers are padded to THREE digits so seasons with 100+ episodes (common for
        /// long-running anime) still sort correctly; the season stays two digits. Plex and
        /// Jellyfin both parse the wider episode field fine.
        /// </summary>
        private static string BuildEpisodeCode(int season, TitleMapping mapping)
        {
            string code = "S" + PadSeason(season) + "E" + PadEpisode(mapping.FirstEpisodeNumber);
            if (mapping.IsMultiEpisode)
            {
                code += "-E" + PadEpisode(mapping.LastEpisodeNumber);
            }
            return code;
        }

        // Title tags have no MAX_PATH limit, but guard against a runaway length by dropping the
        // show-name prefix if the combined embedded title would be unreasonably long.
        private const int MaxTitleTagLength = 255;

        /// <summary>
        /// The human title embedded in a TV file's Title tag (what VLC / Explorer show). Unlike the
        /// file NAME this keeps original punctuation (no filesystem sanitization) and leads with the
        /// show name: "Solo Leveling - S01E009 (Episode Name)". Drops the show prefix only if the
        /// result would exceed a sane tag length.
        /// </summary>
        public static string BuildTvEpisodeTitle(MediaMetadata meta, TitleMapping mapping)
        {
            int season = mapping.SeasonNumber > 0 ? mapping.SeasonNumber : meta.SeasonNumber;
            string code = BuildEpisodeCode(season, mapping);
            string names = JoinEpisodeNamesRaw(mapping);
            string baseTitle = string.IsNullOrEmpty(names) ? code : code + " (" + names + ")";

            string show = (meta.ShowName ?? "").Trim();
            if (string.IsNullOrEmpty(show)) { return baseTitle; }

            string withShow = show + " - " + baseTitle;
            return withShow.Length <= MaxTitleTagLength ? withShow : baseTitle;
        }

        /// <summary>The embedded Title tag for a movie: "Title (Year)" (original punctuation).</summary>
        public static string BuildMovieTitle(MediaMetadata meta)
        {
            return BuildMovieTitle(meta.MovieTitle, meta.Year);
        }

        /// <summary>Embedded Title tag "Title (Year)" from an explicit title/year (multi-movie disc).</summary>
        public static string BuildMovieTitle(string movieTitle, string year)
        {
            string t = string.IsNullOrWhiteSpace(movieTitle) ? "Untitled" : movieTitle.Trim();
            return string.IsNullOrWhiteSpace(year) ? t : t + " (" + year + ")";
        }

        /// <summary>Joins episode names with " &amp; " keeping their original punctuation (for tags).</summary>
        private static string JoinEpisodeNamesRaw(TitleMapping mapping)
        {
            var names = new List<string>();
            foreach (EpisodeInfo ep in mapping.Episodes)
            {
                if (!string.IsNullOrWhiteSpace(ep.Name)) { names.Add(ep.Name.Trim()); }
            }
            return string.Join(" & ", names);
        }

        /// <summary>Joins episode names with " &amp; " for multi-episode files; sanitized.</summary>
        private static string BuildEpisodeNamePart(TitleMapping mapping)
        {
            var names = new List<string>();
            foreach (EpisodeInfo ep in mapping.Episodes)
            {
                if (!string.IsNullOrWhiteSpace(ep.Name)) { names.Add(ep.Name.Trim()); }
            }
            if (names.Count == 0) { return ""; }
            return FilenameSanitizer.Sanitize(string.Join(" & ", names), "");
        }

        private static string ComposeWithLengthGuard(string folder, string code, string namePart, string ext)
        {
            string full = string.IsNullOrEmpty(namePart)
                ? code + "." + ext
                : code + " (" + namePart + ")." + ext;

            int projected = Path.Combine(folder, full).Length;
            if (projected <= MaxFullPathLength || string.IsNullOrEmpty(namePart))
            {
                return full;
            }

            // Trim the descriptive name to fit, keeping the episode code intact.
            int overBy = projected - MaxFullPathLength;
            int keep = namePart.Length - overBy - 1; // -1 for a trailing ellipsis char
            if (keep < 1)
            {
                // Not enough room for any name — fall back to just the code.
                return code + "." + ext;
            }
            string trimmed = namePart.Substring(0, keep).TrimEnd() + "…"; // …
            return code + " (" + trimmed + ")." + ext;
        }

        private static LibraryTarget Finish(string folder, string fileName)
        {
            return new LibraryTarget
            {
                Folder = folder,
                FileName = fileName,
                FullPath = Path.Combine(folder, fileName)
            };
        }

        /// <summary>Zero-pads a season number to at least two digits (more digits pass through).</summary>
        private static string PadSeason(int n)
        {
            return n.ToString("00");
        }

        /// <summary>Zero-pads an episode number to at least three digits, for 100+ episode seasons.</summary>
        private static string PadEpisode(int n)
        {
            return n.ToString("000");
        }

        private static string NormalizeExt(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) { return "mp4"; }
            return ext.TrimStart('.').ToLowerInvariant();
        }
    }
}
