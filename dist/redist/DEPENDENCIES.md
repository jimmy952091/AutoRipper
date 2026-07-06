# AutoRipper — Runtime Dependencies & Packaging Manifest

This folder holds the exact set of files that must ship with the app, gathered for the
Phase 8 installer. Built from `-c Release`, net48, x64.

## 1. Files bundled in this folder (ship all of them, side by side)

| File | Version | Purpose | License note |
|------|---------|---------|--------------|
| `MediaRipperEncoder.exe` | 0.1.0 | The app itself (assembly name stays `MediaRipperEncoder`; user-facing name is **AutoRipper**). | Ours |
| `MediaRipperEncoder.exe.config` | — | Binding redirects + runtime config. Ships next to the exe. | Ours |
| `Newtonsoft.Json.dll` | 13.0.3 | JSON: settings store + TheTVDB/OMDb parsing. Single self-contained managed DLL. | MIT |
| `TagLibSharp.dll` | 2.3.0 | Writes the embedded Title tag into finished MP4/MKV (episode name in VLC/Explorer). Managed, no native deps. | LGPL-2.1 |

That is the **complete** third-party runtime set — only two DLLs. Everything else the app
uses is part of the .NET Framework itself (next section).

## 2. Framework dependencies — NOT bundled, come from .NET Framework 4.8

Referenced from the GAC, so they are NOT copied locally and must NOT be shipped:
- `System.Windows.Forms` / `System.Drawing` — the WinForms UI.
- `System.Management` — WMI optical-drive enumeration (`Win32_CDROMDrive`).
- `System.Net.Http` — OMDb / TheTVDB HTTP calls.

**Requirement:** .NET Framework **4.8** (NOT 4.8.1 — see [[phase8-installer-requirements]];
4.8.1 is unsupported on Windows Server 2019). Installer must verify it (registry
`HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full` `Release` >= 528040) and, if missing,
warn + link the OFFLINE redist (`ndp48-x86-x64-allos-enu.exe`).

## 3. External user-supplied tools — NEVER bundled (separate licenses)

The app shells out to these; the user installs them and points Settings at them:
- **MakeMKV** (`makemkvcon.exe` / `makemkvcon64.exe`) — ripping.
- **HandBrakeCLI.exe** — encoding. (The CLI, not the GUI. The GUI needs the .NET *Desktop*
  Runtime; the CLI is native and needs neither. A rip-only or encode-only node only needs the
  relevant tool.)
- **fre:ac `freaccmd`** — audio-CD ripping, OPTIONAL (only for the Music window). Not yet built.

## 4. Linux via Wine — forward-looking notes (soft target)

The app must eventually run well on Debian-like (Ubuntu/Mint) and Red Hat-like (**Fedora is
RPM-based**, dnf/rpm — not Debian) distros through **Wine** as the translation layer. This is
why the stack is .NET Framework 4.8 + **WinForms** (WPF's rendering is far rockier under Wine).

Packaging implications for the installer phase:
- Under Wine, .NET Framework 4.8 is installed into the Wine prefix via **winetricks** (`dotnet48`),
  not the Windows OS installer. Document this path; the Windows Inno/WiX installer itself may not
  run cleanly under Wine, so provide a "copy this folder + `winetricks dotnet48`" fallback.
- The two bundled DLLs are pure managed code — they work identically under Wine's .NET 4.8.
- The external tools have their own Linux-native builds (MakeMKV, HandBrakeCLI, fre:ac all have
  Linux versions). Decide later whether Wine runs the Windows tools or the app calls native Linux
  binaries — the Settings paths already make the tool locations configurable, so either works.
- WMI (`Win32_CDROMDrive`) and the MCI/DeviceIoControl eject interop are the most Wine-fragile
  spots; the network-mapped-source path and manual-eject prompt are a good fallback there.

## 5. Handoff status

- **Music window (fre:ac + MusicBrainz):** designed, not built — deferred with the connector.
- **Installer (Phase 8):** to build next; three modes planned — Standalone / Server Node (encoder) /
  Client Node (ripper), matching [[connector-distributed-encoding]].
