<#
  Freezes the Camoufox sidecar into a standalone Windows .exe (no Python needed at runtime).
  Output: <repo>\dist-sidecar\gpmlite-camoufox-sidecar\  (bundled by package-windows.ps1)

  Requires a Python venv with:  pip install "camoufox[geoip]" pyinstaller
  Set -Venv to that venv (default: <repo>\.camoufox-venv).
#>
param([string]$Venv = "")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot      # gpm-lite\
$repo = Split-Path -Parent $root               # repo root
if (-not $Venv) { $Venv = Join-Path $repo ".camoufox-venv" }
$py = Join-Path $Venv "Scripts\python.exe"
if (-not (Test-Path $py)) { throw "No venv python at $py. Create it + pip install 'camoufox[geoip]' pyinstaller." }

& $py -m PyInstaller --noconfirm --clean `
  --name gpmlite-camoufox-sidecar `
  --distpath (Join-Path $repo "dist-sidecar") --workpath (Join-Path $repo "build\sidecar-build") --specpath (Join-Path $repo "build\sidecar-build") `
  --collect-all camoufox --collect-all browserforge --collect-all apify_fingerprint_datapoints `
  --collect-all language_tags --collect-submodules playwright `
  (Join-Path $root "sidecar\camoufox_sidecar.py")

Write-Host "Frozen sidecar: $(Join-Path $repo 'dist-sidecar\gpmlite-camoufox-sidecar')"
