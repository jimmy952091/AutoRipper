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

- **HandBrake / HandBrakeCLI: use version 1.4.x** — it's the last release that supports
  Windows 7/8. The 1.4 GUI additionally requires the **.NET 5.0 desktop runtime** (a separate
  Microsoft download; only needed for the GUI — AutoRipper itself only drives the CLI).
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
- **MusicBrainz (audio-CD lookup) on Windows 7: fixed in-app.** Old Windows installs that don't
  receive automatic root-certificate updates lack roots issued after ~2009 — and MusicBrainz's
  chain terminates in **DigiCert Global Root G2 (issued 2013, newer than Windows 7 itself)**,
  so the connection failed while other sites with older chains worked. AutoRipper now embeds
  the genuine DigiCert Global Root G2 and ISRG Root X1 (Let's Encrypt) roots as pinned fallback
  trust anchors: chains rejected *only* for an unknown root are re-verified against these exact
  roots (all other TLS errors still fail — validation is not weakened). No user action needed;
  the log notes when the fallback engages. If a lookup still fails, the log
  (`%AppData%\AutoRipper\logs`) prints the full error chain naming the cause.

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
