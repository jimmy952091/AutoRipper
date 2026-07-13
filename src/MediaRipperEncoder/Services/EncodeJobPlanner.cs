using System;
using System.IO;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Turns a confirmed <see cref="MediaMetadata"/> package + one ripped input file into a fully
    /// planned <see cref="EncodeJob"/>: the library target path, the chosen preset, a staging output
    /// path, and the embedded Title tag. Pure planning — no disk or subprocess work — so it's unit
    /// testable and, crucially, SHARED by both the local <see cref="PipelineCoordinator"/> and the
    /// remote encoder server. Sharing it means a file encoded on the server node is named and placed
    /// EXACTLY as it would be locally; the logic can't drift between the two and mislabel media.
    ///
    /// All library roots / preset paths / temp folder are passed in explicitly, so the server can
    /// plan using ITS OWN roots and preset files from the SAME metadata package the client confirmed.
    /// </summary>
    public class EncodeJobPlanner
    {
        private readonly string _moviesRoot;
        private readonly string _tvRoot;
        private readonly string _tempFolder;
        private readonly string _outputExtension;
        private readonly string _generalPresetPath;
        private readonly string _generalPresetName;
        private readonly string _animationPresetPath;
        private readonly string _animationPresetName;

        public EncodeJobPlanner(string moviesRoot, string tvRoot, string tempFolder, string outputExtension,
            string generalPresetPath, string generalPresetName,
            string animationPresetPath, string animationPresetName)
        {
            _moviesRoot = moviesRoot;
            _tvRoot = tvRoot;
            _tempFolder = tempFolder;
            _outputExtension = string.IsNullOrEmpty(outputExtension) ? "mp4" : outputExtension;
            _generalPresetPath = generalPresetPath;
            _generalPresetName = generalPresetName;
            _animationPresetPath = animationPresetPath;
            _animationPresetName = animationPresetName;
        }

        /// <summary>Builds a planner from settings, resolving preset names + output container via PresetInfo.</summary>
        public static EncodeJobPlanner FromSettings(AppSettings s)
        {
            return new EncodeJobPlanner(
                s.MoviesRoot, s.TvShowsRoot, s.TempFolder,
                PresetInfo.GetContainerExtension(s.HandBrakePresetPath),
                s.HandBrakePresetPath, PresetInfo.GetPresetName(s.HandBrakePresetPath),
                s.HandBrakeAnimationPresetPath, PresetInfo.GetPresetName(s.HandBrakeAnimationPresetPath));
        }

        /// <summary>
        /// Plans the encode for one ripped file, or returns null if the title is excluded/unmapped
        /// (an unchecked special feature or a title with no episode mapping) and should be skipped.
        /// </summary>
        public EncodeJob BuildEncodeJob(MediaMetadata meta, string inputFile, int titleIndex)
        {
            LibraryTarget target = BuildTargetFor(meta, titleIndex);
            if (target == null) { return null; }

            bool useAnimation = !string.IsNullOrEmpty(_animationPresetName) &&
                                string.Equals(meta.PresetName, _animationPresetName, StringComparison.Ordinal);

            string presetPath = useAnimation ? _animationPresetPath : _generalPresetPath;
            string presetName = useAnimation ? _animationPresetName : _generalPresetName;

            // Encode to a staging file first, then move into the library with overwrite protection —
            // so a partial encode never appears in the library.
            string staging = Path.Combine(_tempFolder ?? "",
                "enc_" + Guid.NewGuid().ToString("N").Substring(0, 8), target.FileName);

            return new EncodeJob
            {
                InputFile = inputFile,
                OutputFile = staging,
                FinalTargetPath = target.FullPath,
                PresetPath = presetPath,
                PresetName = presetName,
                TitleIndex = titleIndex,
                DisplayName = target.FileName,
                EmbeddedTitle = BuildEmbeddedTitle(meta, titleIndex, target)
            };
        }

        /// <summary>
        /// Whether this disc's chosen preset is the animation one — the flag a RipperClient sends
        /// so the server picks the matching preset. Assumes both nodes name their presets the same
        /// (the user exports the same preset files to each machine, which is how they're set up).
        /// </summary>
        public bool IsAnimationChoice(MediaMetadata meta)
        {
            return !string.IsNullOrEmpty(_animationPresetName) && meta != null &&
                   string.Equals(meta.PresetName, _animationPresetName, StringComparison.Ordinal);
        }

        public LibraryTarget BuildTargetFor(MediaMetadata meta, int titleIndex)
        {
            if (meta.MediaType == MediaType.Movie)
            {
                // Multi-movie (double-feature) disc: each title is a distinct film with its own
                // confirmed identity. Build from THAT title's movie fields.
                TitleMapping movieMapping = FindMapping(meta, titleIndex);
                if (movieMapping != null && movieMapping.Kind == TitleKind.Movie)
                {
                    if (!movieMapping.Include) { return null; }
                    return LibraryPathBuilder.BuildMovie(_moviesRoot,
                        movieMapping.MovieTitle, movieMapping.MovieYear, _outputExtension);
                }

                // Single-movie disc: the top-level metadata is the movie.
                return LibraryPathBuilder.BuildMovie(_moviesRoot, meta, _outputExtension);
            }

            TitleMapping mapping = FindMapping(meta, titleIndex);
            if (mapping == null || !mapping.Include || mapping.Kind == TitleKind.Ignore)
            {
                return null;
            }

            if (mapping.Kind == TitleKind.Episode && mapping.Episodes != null && mapping.Episodes.Count > 0)
            {
                return LibraryPathBuilder.BuildTvEpisode(_tvRoot, meta, mapping, _outputExtension);
            }

            return BuildTvExtraTarget(meta, mapping);
        }

        private LibraryTarget BuildTvExtraTarget(MediaMetadata meta, TitleMapping mapping)
        {
            string show = FilenameSanitizer.Sanitize(meta.ShowName, "Unknown Show");
            string folder = Path.Combine(_tvRoot ?? "", show, "Extras");
            string fileName = "Extra - Title " + mapping.TitleIndex.ToString("00") + "." + _outputExtension;
            return new LibraryTarget
            {
                Folder = folder,
                FileName = fileName,
                FullPath = Path.Combine(folder, fileName)
            };
        }

        /// <summary>
        /// The Title tag to embed: "Show - S01E009 (Episode Name)" for TV, "Title (Year)" for movies,
        /// and the file name (no extension) for kept extras. Original punctuation preserved.
        /// </summary>
        private static string BuildEmbeddedTitle(MediaMetadata meta, int titleIndex, LibraryTarget target)
        {
            if (meta.MediaType == MediaType.Movie)
            {
                TitleMapping movieMapping = FindMapping(meta, titleIndex);
                if (movieMapping != null && movieMapping.Kind == TitleKind.Movie)
                {
                    return LibraryPathBuilder.BuildMovieTitle(movieMapping.MovieTitle, movieMapping.MovieYear);
                }
                return LibraryPathBuilder.BuildMovieTitle(meta);
            }

            TitleMapping mapping = FindMapping(meta, titleIndex);
            if (mapping != null && mapping.Kind == TitleKind.Episode &&
                mapping.Episodes != null && mapping.Episodes.Count > 0)
            {
                return LibraryPathBuilder.BuildTvEpisodeTitle(meta, mapping);
            }

            return MediaTagWriter.TitleFromFileName(target.FullPath);
        }

        public static TitleMapping FindMapping(MediaMetadata meta, int titleIndex)
        {
            if (meta.TitleMappings == null) { return null; }
            foreach (TitleMapping m in meta.TitleMappings)
            {
                if (m.TitleIndex == titleIndex) { return m; }
            }
            return null;
        }
    }
}
