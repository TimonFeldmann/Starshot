# Build the hdr-signal.exe probe tool (x64, MSVC + Windows SDK).
# Output: <this dir>\hdr-signal.exe
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# Prefer an explicit VS_PATH (path to vcvarsall.bat); otherwise try known install locations.
$vc = $null
if ($env:VS_PATH -and (Test-Path $env:VS_PATH)) { $vc = $env:VS_PATH }
if (-not $vc) {
    $candidates = @(
        "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat",
        "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvarsall.bat",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
    )
    $vc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $vc) { throw "vcvarsall.bat not found (install VS Build Tools with the C++ workload, or set VS_PATH)" }

Push-Location $here
try {
    $cmd = "call `"$vc`" x64 >nul && cl /nologo /EHsc /O2 /W3 /DUNICODE /D_UNICODE /DWINVER=0x0A00 /D_WIN32_WINNT=0x0A00 /D_CRT_SECURE_NO_WARNINGS main.cpp /Fe:hdr-signal.exe d3d11.lib d3dcompiler.lib dxgi.lib shcore.lib user32.lib gdi32.lib /link /SUBSYSTEM:CONSOLE"
    cmd /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "cl failed (exit $LASTEXITCODE)" }
    Remove-Item "$here\*.obj" -ErrorAction SilentlyContinue
}
finally {
    Pop-Location
}
Write-Output "built: $here\hdr-signal.exe"
