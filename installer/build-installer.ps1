# Builds the AutoRipper MSI from the Release output.
#
# Prereqs (one-time):
#   dotnet tool install --global wix --version 6.0.2
#   wix extension add -g WixToolset.Netfx.wixext/6.0.2
#
# Usage:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "src\MediaRipperEncoder\bin\Release\net48"
$out = Join-Path $root "dist"
$wix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"

# Fresh Release build so the MSI never packages stale binaries.
& "C:\Program Files\dotnet\dotnet.exe" build (Join-Path $root "MediaRipperEncoder.sln") -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build failed." }

New-Item -ItemType Directory -Force -Path $out | Out-Null

# Stage the ship set: everything the build produced except debug symbols. The .wxs harvests
# this folder with a wildcard, so new dependencies are always packaged automatically.
$staging = Join-Path $out "staging"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item (Join-Path $publish "*") $staging -Recurse -Exclude "*.pdb"
$publish = $staging

& $wix build (Join-Path $PSScriptRoot "AutoRipper.wxs") `
    -ext WixToolset.Netfx.wixext `
    -arch x64 `
    -d "PublishDir=$publish" `
    -d "SrcDir=$(Join-Path $root 'src\MediaRipperEncoder')" `
    -o (Join-Path $out "AutoRipper-0.2.2-x64.msi")
if ($LASTEXITCODE -ne 0) { throw "MSI build failed." }

Write-Host "MSI written to $out\AutoRipper-0.2.2-x64.msi"
Write-Host "Silent install:   msiexec /i AutoRipper-0.2.2-x64.msi /qn"
Write-Host "Silent uninstall: msiexec /x AutoRipper-0.2.2-x64.msi /qn"
