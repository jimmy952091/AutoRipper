namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// Physical disc format. UHD is kept distinct from Blu-ray on purpose: MakeMKV's UHD
    /// support is a separate (beta-tier) path, and the user's reflashed drive handles UHD
    /// differently, so the app must never treat UHD as "just another Blu-ray".
    /// </summary>
    public enum DiscType
    {
        Dvd,
        BluRay,
        UhdBluRay,
        // Audio CD — ripped in the dedicated Rip Music window (not the MakeMKV/HandBrake video
        // pipeline). Kept last so the existing video values keep their combo-index mapping.
        Cd
    }

    /// <summary>What kind of content this disc holds — decides which metadata fields apply.</summary>
    public enum MediaType
    {
        Movie,
        TvShow,
        Music
    }

    /// <summary>
    /// UI theme choice. System follows the Windows "app mode" setting (light on Windows 7/8,
    /// which has no such setting). Values are stable — they're persisted in settings.json and
    /// map to the Appearance dropdown by index.
    /// </summary>
    public enum ThemePreference
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    /// <summary>
    /// Which episode-ordering scheme to pull from TheTVDB. Physical box sets are frequently in
    /// DVD order, which differs from broadcast (aired) order — matching the disc's order to the
    /// printed episode booklet is what keeps episodes correctly named.
    /// </summary>
    public enum EpisodeOrder
    {
        Aired,
        Dvd,
        Absolute
    }

    /// <summary>
    /// What a single disc title actually is, once the user has reviewed it. MakeMKV only sees
    /// generic "Title 0/1/2..."; this is the human judgement layer that keeps special features
    /// from being mislabeled as episodes.
    /// </summary>
    public enum TitleKind
    {
        /// <summary>A numbered TV episode (has season + episode number + name).</summary>
        Episode,

        /// <summary>The main movie feature.</summary>
        Movie,

        /// <summary>A bonus/extra (behind the scenes, trailer, etc.) — kept but not an episode.</summary>
        Extra,

        /// <summary>Excluded entirely — do not encode or place this title.</summary>
        Ignore
    }
}
