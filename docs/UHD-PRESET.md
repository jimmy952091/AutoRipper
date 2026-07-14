# Building a UHD (4K HDR) HandBrake preset

UHD Blu-rays are HEVC (H.265) 10-bit with HDR10. **Do not encode them with a standard x264
preset** — an 8-bit x264 encode strips the HDR and produces the classic washed-out, grey
"why does my 4K rip look worse than the Blu-ray" result. Build a dedicated preset in
HandBrake's GUI, export it, and set it under **Settings → Advanced → UHD preset** in
AutoRipper; discs marked UHD Blu-ray use it automatically.

## Recommended settings

**Container (Summary tab): MKV** — MP4 cannot carry TrueHD/Atmos audio, and MKV is the
safest container for HDR metadata. AutoRipper follows each preset's container, so UHD output
lands as `.mkv` while other presets can stay `.mp4`.

**Video tab:**

| Setting | Value | Why |
|---|---|---|
| Encoder | H.265 10-bit (x265) | UHD is HEVC 10-bit HDR; 8-bit strips HDR and bands gradients |
| Encoder preset | medium | See time trade-off below — the quality-per-hour math differs from SD/HD |
| Tune | None | (Optional: `grain` for very grainy films — costs bitrate) |
| Profile / Level | Auto | main10 is selected automatically with the 10-bit encoder |
| Framerate | Same as source, Variable | Movies are 23.976; don't touch |
| Quality | RF 20 | x265's RF scale ≠ x264's; RF 20 at 4K ≈ the tier RF 18 hits at SD/HD. Expect ~10-20 GB per film from a 60-80 GB disc. RF 18 if space is no object |

**Picture tab:** resolution limit 2160p, upscaling off, auto-crop on, ALL filters off —
especially denoise (4K film grain is detail, not noise) and deinterlace (UHD is progressive).

**Color/HDR:** color range "Same as source". HandBrake passes HDR10 through automatically
with the 10-bit encoder as long as no tonemap filter is enabled; recent versions also pass
dynamic HDR metadata (HDR10+ / supported Dolby Vision profiles) by default. Configure
nothing; just don't enable anything called "tonemap".

**Audio tab:** **Auto Passthru with AAC fallback** (unlike SD presets that convert to AAC).
UHD discs carry TrueHD/Atmos and DTS-HD tracks worth keeping; MKV holds them and media
servers transcode down for clients that can't play them.

**Subtitles:** none embedded (media servers handle subtitles separately).

## Trade-offs to decide with open eyes

- **Encode time (software x265):** at 4K, x265 `medium` runs roughly 4-8 fps on a modern
  6-core CPU — 6-12 hours per movie. `slow` roughly doubles that for ~5-10% smaller files;
  medium is the sane default at 4K even if you use slow for DVDs/Blu-rays.
- **NVENC alternative:** NVIDIA GPUs can hardware-encode HEVC 10-bit in 1-2 hours per movie,
  at the cost of ~25-40% larger files for equal visual quality. A second "UHD Fast" preset
  using `nvenc_h265_10bit` is a legitimate choice if encode time matters more than disk.
- **Dolby Vision:** discs with DV profiles HandBrake can't carry keep their HDR10 base layer
  — the result is standard HDR10, which still looks correct (just not DV).
- **Distributed mode:** don't ship UHD encodes to an old-CPU encoder node — pre-AVX2 servers
  run x265 slower than a modern desktop despite more cores, and each film is a 60-80 GB
  transfer first. Switch to Standalone for UHD sessions.

## First-disc sanity check

Encode ONE film (or test with a short title) and check it in your media server before batch
processing: correct colors (not washed out/grey), HDR badge shows on an HDR TV, audio track
listed as expected. Then trust the preset.
