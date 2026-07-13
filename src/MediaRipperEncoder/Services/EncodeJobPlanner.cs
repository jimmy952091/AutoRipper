using System;
using System.Collections.Generic;
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

        // The chosen preset is resolved by NAME (meta.PresetName, set by the per-disc screen), so
        // one map covers general/animation/UHD/UHD-animation. The remote server rebuilds its own
        // planner from ITS settings and resolves the same name to its own path — names match
        // because the user exports the same presets to each machine.
        private readonly Dictionary<string, string> _presetPathByName;
        private readonly HashSet<string> _animationPresetNames;

        /// <summary>Standard-definition constructor (general + animation). Kept for existing callers/tests.</summary>
        public EncodeJobPlanner(string moviesRoot, string tvRoot, string tempFolder, string outputExtension,
            string generalPresetPath, string generalPresetName,
            string animationPresetPath, string animationPresetName)
            : this(moviesRoot, tvRoot, tempFolder, outputExtension,
                   generalPresetPath, generalPresetName, animationPresetPath, animationPresetName,
                   "", "", "", "")
        {
        }

        /// <summary>Full constructor including the UHD (4K) preset variants.</summary>
        public EncodeJobPlanner(string moviesRoot, string tvRoot, string tempFolder, string outputExtension,
            string generalPresetPath, string generalPresetName,
            string animationPresetPath, string animationPresetName,
            string uhdPresetPath, string uhdPresetName,
            string uhdAnimationPresetPath, string uhdAnimationPresetName)
        {
            _moviesRoot = moviesRoot;
            _tvRoot = tvRoot;
            _tempFolder = tempFolder;
            _outputExtension = string.IsNullOrEmpty(outputExtension) ? "mp4" : outputExtension;
            _generalPresetPath = generalPresetPath;
            _generalPresetName = generalPresetName;

            _presetPathByName = new Dictionary<string, string>(StringComparer.Ordinal);
            Register(generalPresetName, generalPresetPath);
            Register(animationPresetName, animationPresetPath);
            Register(uhdPresetName, uhdPresetPath);
            Register(uhdAnimationPresetName, uhdAnimationPresetPath);

            _animationPresetNames = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(animationPresetName)) { _animationPresetNames.Add(animationPresetName); }
            if (!string.IsNullOrEmpty(uhdAnimationPresetName)) { _animationPresetNames.Add(uhdAnimationPresetName); }
        }

        private void Register(string name, string path)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
            {
                _presetPathByName[name] = path;
            }
        }

        /// <summary>Builds a planner from settings, resolving preset names + output container via PresetInfo.</summary>
        public static EncodeJobPlanner FromSettings(AppSettings s)
        {
            return new EncodeJobPlanner(
                s.MoviesRoot, s.TvShowsRoot, s.TempFolder,
                PresetInfo.GetContainerExtension(s.HandBrakePresetPath),
                s.HandBrakePresetPath, PresetInfo.GetPresetName(s.HandBrakePresetPath),
                s.HandBrakeAnimationPresetPath, PresetInfo.GetPresetName(s.HandBrakeAnimationPresetPath),
                s.HandBrakeUhdPresetPath, PresetInfo.GetPresetName(s.HandBrakeUhdPresetPath),
                s.HandBrakeUhdAnimationPresetPath, PresetInfo.GetPresetName(s.HandBrakeUhdAnimationPresetPath));
        }

        /// <summary>Resolves the chosen preset name to its file path; falls back to the general preset.</summary>
        private string ResolvePresetPath(string presetName)
        {
            string path;
            if (!string.IsNullOrEmpty(presetName) && _presetPathByName != null &&
                _presetPathByName.TryGetValue(presetName, out path))
            {
                return path;
            }
            return _generalPresetPath;
        }

        /// <summary>The output container extension of the chosen preset (e.g. mp4 vs mkv), general as fallback.</summary>
        private string ResolveExtension(string presetName)
        {
            string path = ResolvePresetPath(presetName);
            if (string.Equals(path, _generalPresetPath, StringComparison.Ordinal)) { return _outputExtension; }
            string ext = PresetInfo.GetContainerExtension(path);
            return string.IsNullOrEmpty(ext) ? _outputExtension : ext;
        }

        /// <summary>
        /// Plans the encode for one ripped file, or returns null if the title is excluded/unmapped
        /// (an unchecked special feature or a title with no episode mapping) and should be skipped.
        /// </summary>
        public EncodeJob BuildEncodeJob(MediaMetadata meta, string inputFile, int titleIndex)
        {
            LibraryTarget target = BuildTargetFor(meta, titleIndex);
            if (target == null) { return null; }

            // Resolve the chosen preset by name (general/animation/UHD/UHD-animation). If the disc
            // didn't name a preset, or it isn't configured on this machine, fall back to general.
            string presetName = !string.IsNullOrEmpty(meta.PresetName) &&
                                 _presetPathByName != null && _presetPathByName.ContainsKey(meta.PresetName)
                ? meta.PresetName : _generalPresetName;
            string presetPath = ResolvePresetPath(presetName);

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
            return meta != null && _animationPresetNames != null &&
                   !string.IsNullOrEmpty(meta.PresetName) && _animationPresetNames.Contains(meta.PresetName);
        }

        public LibraryTarget BuildTargetFor(MediaMetadata meta, int titleIndex)
        {
            // Use the CHOSEN preset's container (mp4 vs mkv) — a UHD preset may output a
            // different container than the standard one.
            string ext = ResolveExtension(meta.PresetName);

            if (meta.MediaType == MediaType.Movie)
            {
                // Multi-movie (double-feature) disc: each title is a distinct film with its own
                // confirmed identity. Build from THAT title's movie fields.
                TitleMapping movieMapping = FindMapping(meta, titleIndex);
                if (movieMapping != null && movieMapping.Kind == TitleKind.Movie)
                {
                    if (!movieMapping.Include) { return null; }
                    return LibraryPathBuilder.BuildMovie(_moviesRoot,
                        movieMapping.MovieTitle, movieMapping.MovieYear, ext);
                }

                // Single-movie disc: the top-level metadata is the movie.
                return LibraryPathBuilder.BuildMovie(_moviesRoot, meta, ext);
            }

            TitleMapping mapping = FindMapping(meta, titleIndex);
            if (mapping == null || !mapping.Include || mapping.Kind == TitleKind.Ignore)
            {
                return null;
            }

            if (mapping.Kind == TitleKind.Episode && mapping.Episodes != null && mapping.Episodes.Count > 0)
            {
                return LibraryPathBuilder.BuildTvEpisode(_tvRoot, meta, mapping, ext);
            }

            return BuildTvExtraTarget(meta, mapping, ext);
        }

        private LibraryTarget BuildTvExtraTarget(MediaMetadata meta, TitleMapping mapping, string ext)
        {
            string show = FilenameSanitizer.Sanitize(meta.ShowName, "Unknown Show");
            string folder = Path.Combine(_tvRoot ?? "", show, "Extras");
            string fileName = "Extra - Title " + mapping.TitleIndex.ToString("00") + "." + ext;
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
