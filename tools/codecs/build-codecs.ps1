# build-codecs.ps1 - stage the recording encoder DLLs into build\codecs and build the C shim.
#
# The three encoder DLLs (x264, x265, faac) ship pre-built in this repo at
# tools\codecs\dlls (see tools\codecs\dlls\README.md + the LICENSE.* files).
# This script copies them into build\codecs, then compiles the C shim
# (Starshot.Codecs.dll) from src\Starshot.Codecs\starshot-codecs.c with MSVC.
#
# Outputs (build\codecs\):
#   Starshot.Codecs.dll  this shim (cl build)
#   libx264-164.dll      x264 0.164 (H.264/AVC)   - LGPL 2.1 (LICENSE.x264)
#   libx265.dll          x265 4.3   (H.265/HEVC)  - GPL 2.0  (LICENSE.x265)
#   faac-2.dll           faac 2.2.0 (AAC)         - LGPL 2.1 (LICENSE.faac)
#
# The three encoder DLLs are pre-built (NOT compiled here), so no OBS install,
# agent temp dirs, CMake, or nasm are needed - the repo is self-contained.
# Only the shim is built from source, which needs an MSVC C++ toolchain (any
# VS or Build Tools; set VS_PATH to point at a specific vcvars64.bat).
#
# Usage: pwsh tools/codecs/build-codecs.ps1 [-SkipFetch]
#   -SkipFetch  only compile the shim; assume the three encoder DLLs are already in build\codecs
#
# Env overrides:
#   STARSHOT_CODEC_DLLS  directory holding the three encoder DLLs (default tools\codecs\dlls)
#   VS_PATH              path to vcvars64.bat (skips the candidate search)

param([switch]$SkipFetch)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # .../Starshot
$dlldir = if ($env:STARSHOT_CODEC_DLLS) { $env:STARSHOT_CODEC_DLLS } else { Join-Path $repo 'tools\codecs\dlls' }
$outDir = Join-Path $repo 'build\codecs'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- 1. Copy the pre-built encoder DLLs from the repo ---
$encoders = @('libx264-164.dll', 'libx265.dll', 'faac-2.dll')
if ($SkipFetch) {
    Write-Host '-SkipFetch: leaving the existing build\codecs encoder DLLs in place'
} else {
    foreach ($dll in $encoders) {
        $src = Join-Path $dlldir $dll
        if (Test-Path $src) {
            Copy-Item $src (Join-Path $outDir $dll) -Force
            Write-Host ('  {0} <- {1}' -f $dll, $src)
        } else {
            Write-Warning ('missing: {0} (restore tools\codecs\dlls or set STARSHOT_CODEC_DLLS)' -f $src)
        }
    }
}

# --- 2. Locate a vcvars64.bat (MSVC + C++ workload) ---
function Find-Vcvars {
    if ($env:VS_PATH -and (Test-Path $env:VS_PATH)) { return $env:VS_PATH }
    $candidates = @(
        'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { throw 'vcvars64.bat not found (install VS Build Tools with the C++ workload, or set VS_PATH)' }
    return $found
}
$vcvars = Find-Vcvars
Write-Host ('  vcvars: ' + $vcvars)

# --- 3. Compile the C shim (Starshot.Codecs.dll) ---
$shimSrc = Join-Path $repo 'src\Starshot.Codecs\starshot-codecs.c'
$incDir = Join-Path $PSScriptRoot 'headers'
$batContent = @"
@echo off
call "$vcvars" >nul 2>&1
if errorlevel 1 (echo VCVARS FAILED & exit /b 10)
cl /nologo /O2 /W3 /LD /I "$incDir" /Fe:"$outDir\Starshot.Codecs.dll" "$shimSrc" /Fo:"$outDir\\" /link user32.lib
exit /b %ERRORLEVEL%
"@
$tmpBat = Join-Path $env:TEMP 'ss-compile-codecs.bat'
Set-Content $tmpBat $batContent -Encoding ascii
cmd /c "`"$tmpBat`""
$rc = $LASTEXITCODE
Remove-Item $tmpBat -ErrorAction SilentlyContinue
if ($rc -ne 0) {
    Remove-Item (Join-Path $outDir 'starshot-codecs.obj'), (Join-Path $outDir 'Starshot.Codecs.lib'), (Join-Path $outDir 'Starshot.Codecs.exp') -ErrorAction SilentlyContinue
    throw ('Starshot.Codecs.dll build failed (exit {0})' -f $rc)
}
Remove-Item (Join-Path $outDir 'starshot-codecs.obj'), (Join-Path $outDir 'Starshot.Codecs.lib'), (Join-Path $outDir 'Starshot.Codecs.exp') -ErrorAction SilentlyContinue

Write-Host '=== build\codecs ==='
Get-ChildItem $outDir | Select-Object Name, @{N='KB';E={[int]($_.Length/1KB)}} | Format-Table -AutoSize
