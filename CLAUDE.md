# Project: Disc Ripper/Encoder for Plex/Jellyfin

## License (decided 2026-07-06)

This project is licensed **GNU AGPL v3 or later** (see `/LICENSE`, canonical FSF text).
Strong copyleft, chosen deliberately so the project can never be closed-sourced or run as a
private modified network service without releasing source (AGPL §13 closes the SaaS loophole —
important because of the planned LAN client/server "connector"). Copyright holder: Heto
(heto.black@gmail.com). When adding new source files, prepend the short AGPL notice header (see
`Program.cs` for the canonical block). The About box / server node must show an "appropriate
legal notice" (AGPL, copyright, link to source) to satisfy §13 when the network feature ships.
Do NOT relicense, add proprietary/closed components, or introduce dependencies whose licenses
are incompatible with AGPL v3.

## Role & Standards

You are acting as a senior Windows desktop application engineer with deep experience in
.NET Framework, Win32 interop, and building reliable automation tools around third-party
CLI processes. Apply these standards throughout:

- Prioritize correctness and reliability over cleverness. This app manages long-running,
  unattended jobs (rips/encodes can take 30+ minutes each) — a silent failure wastes
  real time and can produce mislabeled files in a user's permanent media library.
- Every external process call (MakeMKV, HandBrake) must have its exit code checked and
  stderr/stdout captured and logged. Never assume a subprocess succeeded just because it
  didn't throw.
- Never let one queue item's failure silently stop the rest of the queue. Log it, mark it
  failed in the UI, move on.
- All file/folder operations that could destroy or overwrite user media must confirm
  before overwriting.
- Explain what you built in plain language after each phase — assume the person directing
  this project cannot read the code themselves and is relying on your summary to sanity
  check direction.
- Prefer explicit, readable code over dense one-liners. Comment non-obvious Win32/interop
  calls with what they do and why.

## Platform & Stack

- **.NET Framework 4.8**, **WinForms** (not WPF — WinForms has meaningfully better
  compatibility under Wine, which is a soft target for this app; WPF's rendering stack is
  much rockier under Wine).
- Must run on **Windows 7 SP1 and later**. Avoid any API introduced after Windows 7 unless
  there's no alternative — check before using anything from `System.Runtime.WindowsRuntime`
  or Windows 10+-only APIs.
- Target x64 primarily; note if anything requires x86.
- **Minimum supported screen resolution: 1366x768** (2010-era laptop). Every window must
  either fit within that (minus taskbar) or provide scrollbars so no button/control is
  ever unreachable. All forms inherit from `BaseForm`, which clamps window size to the
  screen working area and enables auto-scroll — new forms must inherit from it too.
- Installer: **Inno Setup** or **WiX**, fully offline (bundles only this app + required
  .NET Framework redistributable check/installer — never fetches this app's own files from
  the internet during install).
- This app does **not** bundle or redistribute MakeMKV or HandBrake. Those are user-supplied
  external tools (see Setup Wizard below) due to their separate licensing.

## Application Flow

### 1. Welcome/Intro Screen
Shown on first launch (checkbox: "don't show this again", stored in settings).
Static screen explaining: this app automates ripping and encoding physical media (DVD,
Blu-ray, 4K UHD Blu-ray) into files organized for Plex, Jellyfin, or other media servers,
and can also be used for personal media preservation. "Next" → Setup Wizard.

### 2. First-Run Setup Wizard
Collects and validates:
- **MakeMKV CLI path** (`makemkvcon.exe`) — browse button + attempt auto-detect in common
  Program Files locations as a shortcut.
- **HandBrake CLI path** (`HandBrakeCLI.exe`) — same pattern.
- **HandBrake preset file** — a JSON file exported from HandBrake's GUI.
- **Movies output root folder**, **TV Shows output root folder**, **Music output root
  folder** — these are the Plex/Jellyfin library roots.
- **Temporary/scratch folder** — where MakeMKV writes raw rip output before HandBrake
  encodes it. Should warn if this is on a slow/removable drive.
- **Validation step**: run each configured CLI with a harmless flag (e.g. version check)
  to confirm it's a real, working executable before allowing setup to complete. Show a
  clear pass/fail per field, don't just accept any path silently.

Settings persist to `%AppData%\<AppName>\settings.json` (or XML — your call, be
consistent). Must be re-editable later via a Settings menu item, not just on first run —
paths, drives, and presets will change over time.

### 3. Main Working Screen (per-disc entry)

When a disc is inserted (or manually triggered):

- **Drive selector** — enumerate optical drives via WMI (`Win32_CDROMDrive`) at startup,
  list by drive letter + model string. Auto-select if only one drive found. Store "last
  used drive" as default. Must support re-scanning if a drive is added/removed while the
  app is running.
- **Disc type selector**: DVD / Blu-ray / UHD Blu-ray. Auto-fill based on MakeMKV's disc
  scan where possible, but always overridable by the user before processing — auto-detection
  can be wrong, especially with modified/reflashed drives (this user has a WH16NS60 reflashed
  for 4K UHD ripping, so UHD disc handling and MakeMKV's beta-tier UHD support path must be
  accounted for explicitly, not treated identically to standard Blu-ray).
- **Media type selector**: Movie / TV Show / Music — determines which fields appear next.
- **If Movie**: Title, Year (year helps disambiguate lookups — many titles are reused
  across remakes/eras).
- **If TV Show**: Show name, Season number, Disc number within season.
- **"Look up" button**: queries TheTVDB and/or OMDb (as an IMDB proxy — IMDB has no public
  API) using entered info, shows candidate matches, requires explicit user confirmation of
  the correct match before locking in metadata. Never silently auto-match — wrong metadata
  means wrong placement in a user's permanent library.
- **Episode mapping (TV only)**: once show + season are confirmed, pull that season's full
  episode list (number + title) from TheTVDB. MakeMKV exposes disc contents as generic
  "Title 1, Title 2..." — not episode-aware. Suggest a starting episode number based on
  season/disc number entered, but always show a per-track confirm/edit screen before
  processing so mis-ordered or bonus-content tracks don't get mislabeled as episodes.
- **"Process" button**: locks in all confirmed metadata (disc type, media type, title/year
  or show/season/episode mapping, confirmed TheTVDB/OMDb ID) as a single package attached
  to this disc's queue job. This metadata package must travel with the job through ripping,
  encoding, and file placement — never re-derive it from a filename later.

### 4. Rip → Encode Pipeline (concurrent)

Two independent queues, not one sequential pipeline:
- **Rip Queue**: one disc ripped at a time (physical drive constraint), using MakeMKV CLI.
- **Encode Queue**: runs independently on its own thread/task, pulling from a shared
  producer/consumer collection (`System.Collections.Concurrent.BlockingCollection<T>` or
  `System.Threading.Channels` if available) as rip jobs complete.
- The moment a rip finishes, its output is handed to the Encode Queue and the Rip Queue
  immediately starts the next disc — ripping and encoding happen in parallel, not
  sequentially. This is a core functional requirement, not an optimization detail.
- UI must show live status of both queues (current disc ripping, current file encoding,
  what's pending in each queue).
- **Encode queue is strictly FIFO on hand-off**: when a rip finishes, its output files are
  appended to the END of the encode queue. Newly ripped files must NOT jump ahead of or
  reorder whatever is already encoding or already queued — the existing order is preserved.
- **Re-encode selection**: the encode queue UI retains finished items (completed and failed)
  in a list with per-item checkboxes, plus Select-All / Deselect-All and a "Re-encode
  selected" action. This lets the user re-run only specific episodes/titles later (e.g. one
  bad episode) without redoing the whole disc. Re-encoded items go to the END of the queue.
- HandBrake is driven with `--json` for machine-readable progress (its plain progress uses
  carriage-return updates that don't parse reliably line-by-line). The preset is applied via
  `--preset-import-file <file>` + `-Z "<PresetName>"`. Completion is the JSON `WORKDONE`
  state with `Error: 0`; progress is taken from the encode pass (PassID >= 0), ignoring the
  subtitle-scan pass (PassID -1) so the bar doesn't falsely hit 100% early.
- After a successful rip, **eject the disc automatically**:
  - Primary method: `mciSendString` ("set cdaudio door open") via Win32 interop.
  - Fallback: if the primary call fails or the drive is known to need it (some
    reflashed/modified drives don't respond correctly to MCI), fall back to
    `DeviceIoControl` with `IOCTL_STORAGE_EJECT_MEDIA` against a raw handle to the drive.
  - Log which method succeeded; surface to the user if both fail (don't just fail silently
    — the user needs to know to eject manually).

### 4b. Music Ripping Window (standalone, audio CDs)

Music ripping is a separate, self-contained feature — NOT part of the video rip/encode
pipeline above (MakeMKV and HandBrake don't handle audio CDs). It is reached from a
dedicated **"Rip Music" button on the main window** that opens its own window.

- Uses a third user-supplied CLI tool for audio-CD ripping/encoding (lead candidate:
  fre:ac's `freaccmd`). This is a FOURTH tool path in Settings, but **optional** — video-only
  users are never forced to install it. If the user clicks "Rip Music" without it
  configured, prompt them to set it up rather than failing obscurely.
- **We drive the metadata ourselves via MusicBrainz** (free, no API key; identify by Disc ID
  computed from the CD's TOC, fuzzy TOC / typed artist+album as fallback) and pass explicit
  track names + tags to the ripper. We deliberately do NOT use the ripper's own built-in
  lookup/tagging, so the on-screen track list and the actual output always match.
- After the disc is read and matched, list the tracks in a checkbox box: track #, title,
  duration. **All tracks checked by default.** Provide **Select All** and **Deselect All**
  buttons. Only checked tracks are ripped.
- Never silently auto-match a wrong release — if MusicBrainz returns multiple candidates,
  the user confirms which one (same rule as the video metadata lookup).
- Output format is a **user-selectable dropdown** (FLAC lossless / MP3 / etc.).
- Tags are written INSIDE each file (ID3/Vorbis) — media servers read embedded tags for
  music, unlike video where Plex reads the folder/filename. Fetch album art from the Cover
  Art Archive (linked to the MusicBrainz release) and save/embed it.
- Files land in the Music convention defined in File Placement below. Reuses the shared
  eject code, filename sanitizer, ProcessRunner, Logger, and settings store.

### 5. File Placement (Plex/Jellyfin-compliant)

**TV Shows:**
```
TV Shows/
  <Show Name>/
    Season <NN>/
      S<NN>E<NN> (<Episode Name>).mp4
```

**Movies:**
```
Movies/
  <Title> (<Year>)/
    <Title> (<Year>).mp4
```

**Music:** IN SCOPE as a self-contained module in its own window (see "Music Ripping
Window" below). Confirmed convention (2026-07):

```
Music/
  <Artist>/
    <Album> (<Year>)/
      <NN> - <Track Name>.<ext>          (single-disc albums)
      <D>-<NN> - <Track Name>.<ext>      (multi-disc albums: disc-track, e.g. 1-01, 2-05)
```

- Year in the album folder is REQUIRED — distinguishes reissues with different track lists.
- Multi-disc albums use disc-aware track numbers (`1-01`, `2-01`) so a 2-disc release's
  tracks don't collide; single-disc albums use plain `NN - Track`.
- `<ext>` follows the user's chosen output format (flac/mp3/etc.).
- Sanitization applies to artist/album/track exactly like video (illegal chars stripped for
  the path, original punctuation preserved in the on-screen display). Known test case:
  **"AC/DC"** must become a legal folder name (e.g. `AC-DC`) while still displaying "AC/DC".

Requirements:
- Filenames must be sanitized for illegal Windows characters (`: / \ ? * " < > |`) before
  being used in a path, while original punctuation can still be shown in the on-screen
  metadata/preview.
- Folder creation should be idempotent — don't fail if the show/season folder already
  exists from a previous disc.
- Never silently overwrite an existing file with the same target name — confirm with the
  user first.

## Explicitly Out of Scope (for now)
- Bundling or redistributing MakeMKV/HandBrake binaries.
- Automatic online download of the app's own installer contents (installer must be fully
  offline once downloaded).
- (Music ripping is now IN scope — see "Music Ripping Window" in Application Flow. Built as
  its own phase after the video pipeline is complete.)
