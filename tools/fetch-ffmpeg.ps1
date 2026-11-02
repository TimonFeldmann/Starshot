# fetch-ffmpeg.ps1 - download a pinned ffmpeg/ffprobe into the gitignored tools\ffmpeg\bin.
#
# The HDR integration suite (src\Starshot.Tests) and the manual analysis scripts
# shell out to ffmpeg/ffprobe. They resolve them in this order:
#   1. $env:STARSHOT_TEST_FFMPEG_DIR
#   2. <repo>\tools\ffmpeg\bin     (gitignored; THIS SCRIPT populates it)
#   3. PATH
#
# The ffmpeg/ffprobe binaries themselves are NOT committed (they are large).
# This script fetches a Windows build and extracts ffmpeg.exe + ffprobe.exe into
# tools\ffmpeg\bin, so the test toolchain is one command away. Re-run with -Force
# to refetch over an existing copy.
#
# Usage:
#   pwsh tools/fetch-ffmpeg.ps1                 # fetch the default build
#   pwsh tools/fetch-ffmpeg.ps1 -Force          # refetch over the existing copy
#   pwsh tools/fetch-ffmpeg.ps1 -Url <url>      # pin a specific build (see below)
#
# Default: BtbN's "master-latest" GPL build (matches what the suite was developed
# against, e.g. an N-...-g<hash> master build). For a strictly reproducible
# toolchain, pin a versioned asset, e.g.:
#   -Url https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-9.0.zip

param(
    [string]$Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot   # .../Starshot (this script is <repo>\tools\fetch-ffmpeg.ps1)
$binDir = Join-Path $repo 'tools\ffmpeg\bin'
$dstFfmpeg = Join-Path $binDir 'ffmpeg.exe'
$dstFfprobe = Join-Path $binDir 'ffprobe.exe'

if ((Test-Path $dstFfmpeg) -and (Test-Path $dstFfprobe) -and -not $Force) {
    Write-Host ('ffmpeg/ffprobe already present in {0} (use -Force to refetch)' -f $binDir)
    return
}

$zip = Join-Path $env:TEMP 'starshot-ffmpeg-fetch.zip'
$extract = Join-Path $env:TEMP 'starshot-ffmpeg-fetch-x'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null

Write-Host ('downloading: {0}' -f $Url)
Invoke-WebRequest $Url -OutFile $zip -UseBasicParsing

Write-Host 'extracting ...'
if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
Expand-Archive $zip -DestinationPath $extract -Force
$ff = Get-ChildItem $extract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
$fp = Get-ChildItem $extract -Recurse -Filter 'ffprobe.exe' | Select-Object -First 1
if (-not $ff -or -not $fp) { throw 'ffmpeg.exe / ffprobe.exe not found in the downloaded archive' }
Copy-Item $ff.FullName $dstFfmpeg -Force
Copy-Item $fp.FullName $dstFfprobe -Force
Remove-Item $zip, $extract -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ('installed to ' + $binDir)
& $dstFfmpeg -version 2>$null | Select-Object -First 1
& $dstFfprobe -version 2>$null | Select-Object -First 1
