# Releasing AutoRipper

The mechanical checklist for shipping a release. Code work is assumed done and committed.

## 1. Version bump

- `src\MediaRipperEncoder\MediaRipperEncoder.csproj` → `<Version>`
- `installer\AutoRipper.wxs` → `Package Version` (and the MSI filename in
  `installer\build-installer.ps1`)
- `Services\Music\MusicBrainzClient.cs` → `UserAgent` version string
- **Never change the UpgradeCode** in the .wxs — it's what makes upgrades replace instead of
  installing side-by-side.

## 2. Build + verify

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

- Confirm the MSI file count looks right (admin extract:
  `msiexec /a dist\AutoRipper-<ver>-x64.msi /qn TARGETDIR=<temp>`).
- Install it for real on the dev machine; spot-check: app launches, About shows the right
  version + license, a disc scans.
- Silent-install test on the server: `msiexec /i ... /qn`.

## 3. Defender / SmartScreen (reputation)

An unsigned, brand-new binary WILL get "unrecognized app" prompts and risks ML false
positives (we've seen Trojan:Win32/Bearfoos.A!ml on freshly built exes first-hand).

- **Best:** sign the exe and MSI with an Authenticode certificate (signtool; EV certs get
  instant SmartScreen trust, standard certs build it over weeks).
- **Free and worthwhile either way:** submit the release binaries to Microsoft's
  false-positive portal: https://www.microsoft.com/wdsi/filesubmission
- Reputation accrues with downloads; the first weeks are the rough patch.

## 4. GitHub

- Push the repo (main branch) to the public remote.
- Tag: `git tag v<ver> && git push --tags`
- Create a GitHub Release for the tag; attach `dist\AutoRipper-<ver>-x64.msi`.
- Release notes: what changed, plus the two standing pointers (COMPATIBILITY.md for the
  Windows 7 HandBrake note; README's LAN-only warning for the connector).

## 5. AGPL §13 reminder

The About box already shows the license + copyright. When the source is public, add the
repository URL to the About box ("source code available at ...") so network users of the
server node can find the source — that completes the §13 "appropriate legal notice."

## 6. Package managers (all free; do after a Release exists)

A desktop app belongs in APP package managers — NOT NuGet (that's for .NET *libraries*;
an MSI there would help no one). The right registries, all free:

- **winget** (Microsoft's, built into Win10/11 — the big one): PR a manifest to
  `microsoft/winget-pkgs` pointing at the GitHub Release MSI URL + its SHA-256 (GitHub
  shows the digest on the release asset). Easiest via the `wingetcreate` tool. Users then:
  `winget install AutoRipper`.
- **Chocolatey** (community repo): free account, push a package whose install script pulls
  the MSI. `choco install autoripper`.
- **Scoop**: JSON manifest in a bucket (or petition the `extras` bucket). `scoop install autoripper`.
- Update manifests each release (URL + hash change).

**Linux reality check:** apt/dnf/pacman package *native Linux* software — they won't carry a
Windows app. The honest Linux story is "runs under Wine, install the MSI" (see COMPATIBILITY),
not a deb/rpm. If demand appears someday, a Flatpak that bundles Wine is the theoretical route,
but it's heavy and not worth it before there's an audience.

## 7. After release

- Watch for Defender FP reports from users; resubmit new builds to the portal as needed.
- TheTVDB/OMDb keys are per-user by design — never ship keys in a release.
