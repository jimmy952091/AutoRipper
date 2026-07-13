namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// Everything the app remembers between runs. Serialized as JSON to
    /// %AppData%\MediaRipperEncoder\settings.json.
    ///
    /// Property names are kept simple and stable because they become the JSON keys —
    /// renaming one would orphan existing users' saved values.
    /// </summary>
    public class AppSettings
    {
        // --- External command-line tools (user-supplied) ---

        /// <summary>Full path to makemkvcon.exe / makemkvcon64.exe.</summary>
        public string MakeMkvCliPath { get; set; }

        /// <summary>Full path to HandBrakeCLI.exe (the command-line build, not the GUI).</summary>
        public string HandBrakeCliPath { get; set; }

        /// <summary>Full path to the general HandBrake preset .json exported from the HandBrake GUI.</summary>
        public string HandBrakePresetPath { get; set; }

        /// <summary>
        /// Optional path to a second, animation-tuned HandBrake preset .json. The library
        /// mixes live-action and animation, so the per-disc screen can pick this one for
        /// cartoons/anime. Blank if the user keeps a single preset.
        /// </summary>
        public string HandBrakeAnimationPresetPath { get; set; }

        /// <summary>
        /// Optional path to a UHD (4K) HandBrake preset .json — used when the disc type is UHD
        /// Blu-ray. UHD is HEVC 10-bit HDR, which needs a different preset than the x264 standard
        /// one (an 8-bit x264 encode strips HDR). Blank if the user doesn't rip UHD.
        /// </summary>
        public string HandBrakeUhdPresetPath { get; set; }

        /// <summary>Optional UHD animation-tuned preset .json (cartoons/anime on UHD Blu-ray).</summary>
        public string HandBrakeUhdAnimationPresetPath { get; set; }

        /// <summary>
        /// Output format for ripped music: "flac" (default), "mp3", or "wav". CD ripping and
        /// encoding are built in (no external tool) — see Services\Music.
        /// </summary>
        public string MusicFormatId { get; set; }

        // --- Metadata lookup API keys (Phase 5) ---

        /// <summary>OMDb API key (free from omdbapi.com) — used for movie lookups (IMDb proxy).</summary>
        public string OmdbApiKey { get; set; }

        /// <summary>TheTVDB v4 API key (from thetvdb.com) — used for TV series + episode lists.</summary>
        public string TheTvdbApiKey { get; set; }

        /// <summary>Optional TheTVDB subscriber PIN, only for user-supported subscriber keys.</summary>
        public string TheTvdbPin { get; set; }

        /// <summary>
        /// Preferred metadata language as a TheTVDB ISO 639-3 code (e.g. "eng", "spa", "jpn").
        /// Show and episode names are forced to this language so, for example, an anime's title
        /// comes back in English rather than its original language. Defaults to "eng".
        /// </summary>
        public string MetadataLanguage { get; set; }

        // --- Library output roots (media-server library folders) ---

        public string MoviesRoot { get; set; }
        public string TvShowsRoot { get; set; }
        public string MusicRoot { get; set; }

        // --- Working storage ---

        /// <summary>Scratch folder where MakeMKV writes raw rips before HandBrake encodes them.</summary>
        public string TempFolder { get; set; }

        // --- App state / preferences ---

        /// <summary>False once the user ticks "Don't show this again" on the welcome screen.</summary>
        public bool ShowWelcomeOnStartup { get; set; }

        /// <summary>Set true once the first-run setup wizard has been completed successfully.</summary>
        public bool SetupCompleted { get; set; }

        /// <summary>Drive letter of the last optical drive used (Phase 2+). Blank until set.</summary>
        public string LastUsedDrive { get; set; }

        /// <summary>UI theme: follow Windows, or force Light/Dark. Defaults to Light (the app's
        /// original look) so an update never surprises an existing user with a sudden theme change.</summary>
        public ThemePreference Theme { get; set; }

        // --- Advanced: network / mapped-drive rip source ---
        // A backup path for a machine with no local optical drive (e.g. a headless server): point
        // MakeMKV at a disc shared/mapped from another PC over the LAN. Because the drive is remote,
        // we can't auto-eject it, so a "please change the disc on the shared drive" prompt is shown
        // when a rip finishes. Off unless explicitly enabled on the Advanced settings tab.

        /// <summary>When true, ripping reads from <see cref="NetworkRipSource"/> instead of a local drive.</summary>
        public bool NetworkRipEnabled { get; set; }

        /// <summary>
        /// When true, the network source is searched a few levels down for the disc structure
        /// (BDMV / VIDEO_TS) at scan time, so the user can point at the drive root once and not
        /// re-point it each time they swap a Blu-ray for a DVD (the structure folder differs).
        /// </summary>
        public bool NetworkRipSearchSubfolders { get; set; }

        /// <summary>
        /// Path MakeMKV opens as a file source for network ripping: a mapped drive root
        /// (e.g. "Z:\"), a mounted disc folder, or an ISO image. Wrapped into MakeMKV's
        /// file:/iso: source spec automatically. Blank disables it even when the flag is on.
        /// </summary>
        public string NetworkRipSource { get; set; }

        // --- Distributed rip/encode (LAN-only server/client split) ---
        // Standalone by default. A Server node encodes for a Client node that rips. LAN-only and
        // unauthenticated by design (personal media backup across one's own machines).

        /// <summary>This instance's role: Standalone (default), EncoderServer, or RipperClient.</summary>
        public NodeRole NodeRole { get; set; }

        /// <summary>TCP port the encoder server listens on (and the client connects to). LAN-only.</summary>
        public int NodePort { get; set; }

        /// <summary>Host/IP of the encoder server, used by a RipperClient to connect. Blank if Standalone/Server.</summary>
        public string NodeServerHost { get; set; }

        /// <summary>
        /// Shared secret both nodes must hold to talk. Used for an HMAC challenge-response at connect
        /// time so the secret itself never crosses the wire and a random port-scanner is rejected at
        /// the handshake. The server REFUSES TO START with a blank secret (fail safe, never wide open).
        /// Not real transport encryption — a "don't expose this port to the internet" LAN gate.
        /// </summary>
        public string NodeSharedSecret { get; set; }

        public AppSettings()
        {
            // Sensible defaults for a brand-new install.
            MakeMkvCliPath = "";
            HandBrakeCliPath = "";
            HandBrakePresetPath = "";
            HandBrakeAnimationPresetPath = "";
            HandBrakeUhdPresetPath = "";
            HandBrakeUhdAnimationPresetPath = "";
            MusicFormatId = "flac";
            OmdbApiKey = "";
            TheTvdbApiKey = "";
            TheTvdbPin = "";
            MetadataLanguage = "eng";
            MoviesRoot = "";
            TvShowsRoot = "";
            MusicRoot = "";
            TempFolder = "";
            ShowWelcomeOnStartup = true;
            SetupCompleted = false;
            LastUsedDrive = "";
            Theme = ThemePreference.Light;
            NetworkRipEnabled = false;
            NetworkRipSource = "";
            NetworkRipSearchSubfolders = true;
            NodeRole = NodeRole.Standalone;
            NodePort = 47820; // arbitrary high port for the LAN encode session
            NodeServerHost = "";
            NodeSharedSecret = "";
        }
    }
}
