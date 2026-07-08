namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// How this instance of AutoRipper participates in a LAN rip/encode session. Chosen at
    /// install time (installer offers Standalone / Server Node / Client Node) and changeable
    /// later on the Advanced settings tab.
    ///
    /// The split is deliberately LAN-only with no accounts/passwords: it's meant for one person
    /// backing up their own media across their own machines, so we avoid building an auth layer.
    /// </summary>
    public enum NodeRole
    {
        /// <summary>Everything on one PC: rip and encode locally. The default; unchanged behaviour.</summary>
        Standalone,

        /// <summary>
        /// Server node = the ENCODER. Listens for a ripper client, receives ripped files + their
        /// confirmed metadata, encodes them (usually the beefier / always-on machine), and places
        /// the results. Owns the authoritative encode queue and persists it, so it survives a
        /// reboot and lets a client rejoin an in-progress session.
        /// </summary>
        EncoderServer,

        /// <summary>
        /// Client node = the RIPPER. Has the optical drive (or the network-mapped source), rips
        /// discs, and hands each finished file to the server node to encode. Shows the remote
        /// encode progress locally. Can reconnect and rejoin if either side restarts.
        /// </summary>
        RipperClient
    }
}
