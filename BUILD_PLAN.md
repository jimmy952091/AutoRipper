# Build Order

Work through these phases roughly in order. Each phase should end with something you can
actually run and see working — don't let Claude Code move to the next phase until you've
had it explain what it built and you've tried it yourself.

## Phase 0: Project Skeleton
- Create the .NET Framework 4.8 WinForms solution.
- Empty main window, empty settings screen, empty welcome screen (just navigation between
  them, no real logic yet).
- Confirms the toolchain and target framework are correctly set up before anything else.

## Phase 1: Settings & Setup Wizard
- Welcome screen (static content, "don't show again" checkbox).
- Setup wizard: path pickers for MakeMKV CLI, HandBrake CLI, preset file, output folders,
  temp folder.
- Validation logic (run each CLI with a version-check flag, confirm success/failure).
- Persist to `%AppData%\<AppName>\settings.json`.
- Settings re-editable later via menu.
- **Ask Claude Code to explain**: where settings are stored, how validation works, what
  happens if a path is wrong.

## Phase 2: Drive Detection & Manual Eject
- WMI enumeration of optical drives.
- Drive selector UI (auto-select if one drive, list if multiple).
- Eject function: `mciSendString` primary, `DeviceIoControl` fallback.
- Test this in isolation with a simple "eject" button before wiring it into the pipeline —
  this touches low-level Win32 interop and is worth confirming works on your actual
  WH16NS60 before building anything on top of it.

## Phase 3: MakeMKV Integration (Rip Queue)
- Shell out to `makemkvcon`, parse output to detect disc type/titles.
- Build the Rip Queue (single active rip, queued disc jobs).
- Wire in auto-eject after a successful rip.
- Test with a standard DVD and a standard Blu-ray before touching UHD — UHD/MakeMKV beta
  behavior is a separate variable, easier to debug once standard ripping is solid.
- Then test UHD specifically with your reflashed WH16NS60.

## Phase 4: HandBrake Integration (Encode Queue)
- Shell out to `HandBrakeCLI` using your exported preset.
- Build the Encode Queue as an independent producer/consumer pipeline, fed by completed
  rip jobs.
- Confirm rip and encode genuinely run concurrently — start a rip, and while it's still
  running, manually drop a test file into the encode queue and confirm it processes at the
  same time, not after the rip finishes.

## Phase 5: Metadata Entry & Lookup
- Per-disc metadata form: disc type, media type, movie/show fields.
- TheTVDB / OMDb API integration + "Look up" confirm-match UI.
- Episode list pull + per-track mapping/confirmation screen for TV.
- This phase doesn't need real ripping to test — you can build and test this against fake
  disc data before it's wired into the real pipeline.

## Phase 6: File Placement
- Sanitization logic.
- Folder structure builder for Movies/TV (Music once you've confirmed the convention you
  want).
- Overwrite confirmation logic.
- Wire the confirmed metadata package from Phase 5 through to this stage.

## Phase 7: Full Pipeline Integration
- Connect all pieces: disc inserted → metadata entered/confirmed → rip queue → auto-eject
  → encode queue → file placement in correct Plex folder.
- This is where you actually run a real disc start-to-finish and see the final file land
  correctly in your Plex library.

## Phase 8: Music Ripping Window (standalone)
- Add a "Rip Music" button on the main window that opens its own dedicated window.
- Add the audio-CD ripper as a fourth, OPTIONAL tool path in Settings (lead candidate:
  fre:ac `freaccmd`). Video-only users never need it.
- MusicBrainz client: identify the CD by Disc ID (from the CD's TOC), fuzzy TOC / typed
  artist+album as fallback; confirm-match on multiple candidates. Pull album art from the
  Cover Art Archive.
- Track list with checkboxes (all checked by default) + Select All / Deselect All; rip only
  the checked tracks to the user-selected format (FLAC/MP3/etc. dropdown).
- Write tags inside each file; place into `Music/<Artist>/<Album> (<Year>)/` with disc-aware
  track numbers for multi-disc albums. Reuses the eject code, sanitizer, ProcessRunner,
  Logger, and settings store from earlier phases.
- Test with a single-disc CD and a multi-disc album (e.g. AC/DC *Live* — also verifies the
  "AC/DC" → legal folder name sanitization).

## Phase 9: Installer
- Package with Inno Setup or WiX.
- Bundle app + .NET Framework 4.8 redistributable (offline installer, no internet fetch
  during install).
- Test on a clean Windows 7 VM if possible, not just your dev machine.

---

**When starting with Claude Code:** put `CLAUDE.md` in your project root, then just say
"start Phase 0" and work through them one at a time. Have it explain each phase in plain
language before you move to the next.
