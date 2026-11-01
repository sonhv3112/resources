<#
  Builds the installable GpmLite for Windows x64 — a self-contained folder (and .zip) that bundles:
    - the .NET app (self-contained, no .NET install needed)
    - Camoufox (Firefox antidetect) under camoufox\
    - the frozen Python sidecar under sidecar\  (no Python needed)
  The user just unzips and runs GpmLite.exe (or installs via the optional Inno Setup script).

  Prereqs on this Windows machine:
    1. .NET 10 SDK.
    2. A Python venv with camoufox+pyinstaller, and the frozen sidecar built:
         py -m venv .camoufox-venv ; .camoufox-venv\Scripts\pip install "camoufox[geoip]" pyinstaller
         py -m camoufox fetch                       # downloads the Camoufox Windows binary
         scripts\build-sidecar.ps1
    3. The Camoufox Windows binary folder (contains camoufox.exe). Point -CamoufoxDir at it, or let the
       script auto-detect the pip cache (%LOCALAPPDATA%\camoufox\camoufox or a 'camoufox fetch' path).

  Usage:
    scripts\package-windows.ps1 -CamoufoxDir C:\path\to\camoufox-win-extracted
#>
param(
  [string]$CamoufoxDir = "",
  [string]$SidecarDist = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $root
$dist = Join-Path $root "dist"
$app  = Join-Path $dist "GpmLite-win-x64"
$rid  = "win-x64"

if (-not $SidecarDist) { $SidecarDist = Join-Path $repo "dist-sidecar\gpmlite-camoufox-sidecar" }

# Auto-detect Camoufox dir if not given.
if (-not $CamoufoxDir) {
  $cands = @(
    (Join-Path $env:LOCALAPPDATA "camoufox\camoufox"),
    (Join-Path $repo "camoufox-win\extracted")
  )
  $CamoufoxDir = $cands | Where-Object { Test-Path (Join-Path $_ "camoufox.exe") } | Select-Object -First 1
}
if (-not $CamoufoxDir -or -not (Test-Path (Join-Path $CamoufoxDir "camoufox.exe"))) {
  throw "Camoufox not found. Pass -CamoufoxDir <folder containing camoufox.exe> (run 'py -m camoufox fetch' or unzip the Windows build)."
}
if (-not (Test-Path (Join-Path $SidecarDist "gpmlite-camoufox-sidecar.exe"))) {
  throw "Frozen sidecar not found at $SidecarDist. Run scripts\build-sidecar.ps1 first."
}

Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $app | Out-Null

Write-Host "Publishing .NET app ($rid, self-contained) ..."
dotnet publish (Join-Path $root "src\GpmLite.App\GpmLite.App.csproj") -c Release -r $rid --self-contained true `
  -p:PublishTrimmed=false -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Bundling Camoufox ..."
Copy-Item -Recurse -Force $CamoufoxDir (Join-Path $app "camoufox")
# Camoufox's pkgman needs version.json in the install dir or it fails on fresh machines
# ("Version information not found ... run camoufox fetch"). Ensure it got bundled.
if (-not (Test-Path (Join-Path $app "camoufox\version.json"))) {
  $vj = Join-Path (Split-Path -Parent $CamoufoxDir) "version.json"
  if (Test-Path $vj) { Copy-Item -Force $vj (Join-Path $app "camoufox\version.json") }
}
if (-not (Test-Path (Join-Path $app "camoufox\version.json"))) {
  throw "version.json not found in bundled camoufox dir. The app would fail on fresh machines. CamoufoxDir=$CamoufoxDir"
}

Write-Host "Bundling frozen sidecar ..."
New-Item -ItemType Directory -Force -Path (Join-Path $app "sidecar") | Out-Null
Copy-Item -Recurse -Force $SidecarDist (Join-Path $app "sidecar\gpmlite-camoufox-sidecar")

$zip = Join-Path $dist "GpmLite-win-x64.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip
$size = "{0:N0} MB" -f ((Get-ChildItem $app -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Built folder: $app  ($size)"
Write-Host "Built zip:    $zip"
Write-Host "To ship an installer .exe, build scripts\installer.iss with Inno Setup (optional)."
