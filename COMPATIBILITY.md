# AutoRipper Compatibility Log

Real-world verified combinations. "Verified" means actually run on hardware, not assumed.

## Windows versions

| Windows | AutoRipper | Notes |
|---|---|---|
| Windows 7 SP1 / 8 / 8.1 | ✅ Verified (Win7 **Home Premium**, eMachines laptop) | Requires .NET Framework 4.8 (offline installer: `ndp48-x86-x64-allos-enu.exe`). Tested on Home Premium; other editions (incl. Home Basic) are expected to work — .NET 4.8 and everything AutoRipper uses ship in all editions. See the HandBrake version note below — this is the important one. |
| Windows 10 / 11 | ✅ Verified (daily driver) | Latest tools throughout. |
| Windows Server 2019 | ✅ Verified (encoder node) | Use .NET Framework **4.8** — 4.8.1 does **not** support Server 2019. Add a TCP firewall rule for the node port (default 47820) for distributed mode. |
| Windows Server 2022 | Expected OK (untested) | Same guidance as Server 2019. |
| Linux via Wine | Soft target (untested) | WinForms chosen specifically for Wine friendliness. Install .NET 4.8 into the prefix via `winetricks dotnet48`. External tools can be the Windows builds under Wine or native Linux builds (paths are configurable in Settings). |

## External tools on Windows 7 / 8

- **HandBrake / HandBrakeCLI: use 1.3.3 or 1.4.x.** 1.4.x is the last release that supports
  Windows 7/8, but its GUI requires the **.NET 5.0 desktop runtime** (a separate Microsoft
  download). **1.3.3 works out of the box with no extra runtime** (it's what Ninite installs
  on Windows 7) and is fine for AutoRipper — both verified.
- **Presets on old HandBrake: modern preset exports do NOT import.** Preset files exported
  from a current HandBrake use a newer format (v72 as of mid-2026) than 1.3.3/1.4 understand
  (1.3.3 = v42). Re-create the preset in the old version's own GUI and export from there, or
  use a preset file written in the old format. Do NOT build UHD/4K-HDR presets on 1.3.3:
  HDR10 passthrough and the 10-bit pipeline arrived in HandBrake 1.4 — a 1.3.3 UHD encode
  strips the HDR (washed-out colors). Keep UHD encoding on a modern-HandBrake machine.
- **"Encode fails instantly" on 1.4 — root cause found (log-verified):** AutoRipper versions
  before 2026-07-09 passed a `--no-metadata` flag that HandBrakeCLI 1.4 doesn't know;
  1.4 treats unknown options as fatal (`unknown option (--no-metadata)` in the log) and exits
  before encoding a frame. Current AutoRipper probes the installed CLI and only passes the
  flag when supported — update AutoRipper if you see this. The earlier suspicion that
  1.11-exported presets were to blame was wrong: a preset re-exported from the 1.4 GUI
  imports cleanly (`-Z` accepted in the same log), and 1.11-exported presets have not been
  proven bad — treat cross-version preset reuse as *untested*, with a same-version export as
  the safe choice. (Key settings if rebuilding by hand: encoder preset *slow*, profile High,
  level Auto, RF 18, framerate Same-as-source/VFR, no subtitle tracks.)
- **MakeMKV: the current version installs and runs on Windows 7 with no issues (verified).**
- **MusicBrainz (audio-CD lookup) on Windows 7: WORKS as of v0.2.1** — verified on a fully
  patched Windows 7 Home Premium laptop. The underlying Windows limitation is real and
  remains: Windows 7's TLS layer (SChannel) lacks the modern cipher suites musicbrainz.org's
  servers require (added in Windows 8), so the OS handshake fails with "Could not create
  SSL/TLS secure channel" no matter how updated the machine is. AutoRipper now detects that
  exact failure and transparently redoes the lookup over its own bundled modern-TLS engine
  (BouncyCastle) — its own cipher suites, Windows crypto not involved. The fallback is
  deliberately narrow: it can only contact MusicBrainz / Cover Art Archive (plus the
  archive.org hosts cover images redirect to), HTTPS only, with full certificate chain and
  hostname validation (including the pinned-root rule below). Modern Windows never uses it —
  the OS TLS stack stays in charge there. Root-certificate note (separate, also handled):
  old installs missing DigiCert Global Root G2 / ISRG Root X1 get them as pinned fallback
  trust anchors; validation is not weakened.

## Distributed (two-machine) mode

- ✅ Verified: **Windows 7 ripper client → Windows Server 2019 encoder server**, mixed Windows
  generations on either end. Rips on the old laptop are slower but acceptable; encoding happens
  on the server, which is exactly the point — pair a weak ripper with a strong encoder.
- ✅ Verified: Windows 10 client → Server 2019 encoder (WiFi and wired).
- Traffic is LAN-only by design. Do not port-forward the node port; use a VPN for remote access.

## Old / low-end hardware expectations

Ripping speed is bound by the optical drive, not the machine — a 2009-era laptop rips a DVD
episode in acceptable time. Local *encoding* on such hardware is very slow (that's physics);
use Client Node mode to ship encodes to a faster machine instead.
