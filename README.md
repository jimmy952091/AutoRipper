# AutoRipper

Automated disc ripping and encoding for Plex, Jellyfin, Emby, Kodi, Universal Media Server,
and any media server that reads the standard Movies / TV Shows folder layout — a Windows
desktop app that turns a stack of DVDs, Blu-rays, and audio CDs into a correctly named,
correctly tagged media library, unattended. Also usable for personal media preservation.

Inspired by the idea behind the Automatic Ripping Machine (ARM) script, but an independent
from-scratch implementation as a Windows program (no ARM code is used).

## What it does

- **One flow for everything**: insert a disc, click *Scan disc & configure*, confirm what it
  is, done. DVDs, Blu-rays, UHD Blu-rays, and audio CDs all ride the same two queues
  (rip + encode) with live progress, per-title failure isolation, and retry for scratched
  titles/tracks.
- **Metadata you confirm, never guesses**: movies and TV via OMDb / TheTVDB (bring your own
  free API keys — TheTVDB keys are per-user by their terms), with aired/DVD/absolute episode
  ordering, multi-episode ("segmented cartoon") files, and per-title include/exclude. Music
  via MusicBrainz (identified from the disc itself — exact Disc ID, fuzzy TOC, or typed
  search) with cover art from the Cover Art Archive.
- **Plex/Jellyfin-compliant placement**: `Movies/Title (Year)/`, `TV Shows/Show/Season NN/
  SxxExxx (Episode).ext`, `Music/Artist/Album (Year)/NN - Track.ext` (disc-aware numbers for
  multi-disc albums), illegal-character sanitization, and never a silent overwrite.
- **Audio CD ripping is built in** — no extra program. Raw digital extraction plus built-in
  encoders for **FLAC, MP3, OGG Vorbis, Opus, M4A/AAC, WMA, AIFF, and WAV**, tags written
  inside every file, album art embedded and saved as `cover.jpg`.
- **Distributed mode (optional, LAN only)**: rip on one PC while a stronger machine encodes.
  A Client Node ships each finished rip to a Server Node with checksummed transfer, live
  synced progress, and automatic reconnect — a weak laptop can feed a big server.
- **Runs on old hardware**: Windows 7 SP1 through Windows 11 / Server 2022, 1366x768 screens,
  single-core CPUs (see [COMPATIBILITY.md](COMPATIBILITY.md) for verified combinations and
  the Windows 7 HandBrake note). Light and Dark themes included.

## Requirements

- Windows 7 SP1 or later, x64. (Linux via Wine is a soft target; WinForms was chosen with
  that in mind.)
- **.NET Framework 4.8** — not 4.8.1, which Windows Server 2019 cannot run. The installer
  checks for it and points at Microsoft's offline installer if it's missing.
- For **video** discs, two user-supplied tools (separate downloads, not bundled, due to their
  own licensing):
  - **MakeMKV** — the normal installer already includes the command-line tool (`makemkvcon.exe`).
  - **HandBrakeCLI** — the *command-line* build (`HandBrakeCLI.exe`), a separate download from
    handbrake.fr; the GUI app alone is not enough. On Windows 7/8, use HandBrake 1.4.x and
    create your preset with that version (see COMPATIBILITY.md).
- For **audio CDs**: nothing — ripping and encoding are built in.

## Install

Grab the MSI and run it, or deploy silently:

```
msiexec /i AutoRipper-0.1.0-x64.msi /qn
```

(`INSTALLDESKTOPSHORTCUT=0` skips the desktop shortcut; uninstall with `msiexec /x ... /qn`.)
First launch walks you through tool paths and library folders, and validates every tool
actually runs before letting you finish. **Help → Uninstall AutoRipper...** offers a full
cleanup that also removes settings, logs, and registry entries if you're leaving for good.

## Build from source

No Visual Studio required. With the .NET SDK (8+) installed:

```
dotnet build MediaRipperEncoder.sln -c Release
```

To build the MSI (WiX v6 — deliberately not v7, whose maintenance-fee EULA doesn't suit an
AGPL project):

```
dotnet tool install --global wix --version 6.0.2
wix extension add -g WixToolset.Netfx.wixext/6.0.2
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

The app icon is generated from code: `tools\IconGenerator.cs`.

## Network mode security (LAN only — do not port-forward)

AutoRipper's distributed rip/encode mode (Server Node / Client Node) is designed for
**your own local network only**. Connections require a shared secret (HMAC challenge-response;
the secret itself never crosses the wire), the encoder refuses to start without one, and
received file names are confined to a staging inbox so remote input cannot write outside it.

However, traffic is **not encrypted** (no TLS). Do **not** forward the node port through your
router or expose it to the internet. If you need to check in remotely, use a proper VPN
(e.g. WireGuard/Tailscale) into your LAN instead — that keeps the session encrypted end to end
without AutoRipper needing to be reachable from the outside.

## A note on ripping legality

AutoRipper is a tool for backing up and organizing **your own** media. Laws on ripping
copy-protected discs vary by country; understanding and complying with yours is your
responsibility.

## License — GNU AGPL v3 (or later)

Copyright (C) 2026 James Spurgeon (heto.black@gmail.com)

AutoRipper is free software licensed under the **GNU Affero General Public License, version 3
or (at your option) any later version**. The full text is in [LICENSE](LICENSE).

In plain terms, this license guarantees the project stays open:

- Anyone may use, study, share, and modify it.
- Anyone who distributes it (free **or** sold) must provide the corresponding source code.
- Any modified version must also be AGPL — it can never be re-licensed as closed source.
- **Network use is covered:** if someone runs a modified version as a network service (for
  example, hosting the encoder/server node for others), they must offer those users the
  modified source too. This deliberately closes the "run it as a private paid service" loophole.

It may be sold, but it can never be made proprietary or locked behind a closed paywall.

Bundled third-party libraries: Newtonsoft.Json (MIT), TagLib# (LGPL-2.1), CUETools codecs
(GPL), NAudio (MIT), NAudio.Lame + libmp3lame (LGPL), OggVorbisEncoder (MIT-ish Xiph port),
Concentus (MIT/Xiph). External tools (MakeMKV, HandBrake) are user-supplied and never bundled.
