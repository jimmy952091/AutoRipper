# AutoRipper

Automated disc ripping and encoding for Plex, Jellyfin, Emby, Kodi, Universal Media Server,
and any media server that reads the standard Movies / TV Shows folder layout — a Windows
desktop app that wraps MakeMKV (ripping) and HandBrake (encoding) into an unattended,
metadata-aware pipeline. Also usable for personal media preservation.

Inspired by the idea behind the Automatic Ripping Machine (ARM) script, but an independent
from-scratch implementation as a Windows program (no ARM code is used).

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

## Requirements

- Windows 7 SP1 or later (x64). Linux via Wine is a soft target — see
  `dist/redist/DEPENDENCIES.md`.
- **.NET Framework 4.8** (not 4.8.1 — 4.8.1 is unsupported on Windows Server 2019).
- User-supplied external tools (separate downloads, not bundled): **MakeMKV** (`makemkvcon`),
  **HandBrake CLI** (`HandBrakeCLI.exe` — the command-line build, not the GUI), and optionally
  **fre:ac** (`freaccmd`) for audio-CD ripping.

## Build

No Visual Studio required. With the .NET SDK installed:

```
dotnet build MediaRipperEncoder.sln -c Release
```

Output and the redistributable set are described in `dist/redist/DEPENDENCIES.md`.

## Status

Core pipeline (rip → encode → Plex/Jellyfin placement) is complete and working. Remaining:
the standalone Music-ripping window, the distributed rip/encode "connector" (LAN client/server
nodes), and the offline installer.
