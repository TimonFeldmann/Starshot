<#
.SYNOPSIS
  Build & (optionally) run the Starshot INSTALLER release (local mirror of the kachina line in
  .github/workflows/release.yml). This is the "installer distribution line" - NOT the portable
  zip line: the app exe gets the STARSHOT-INSTALLER magic stamped into its DOS stub (offset 0x40),
  so the running app identifies as the installer edition (registry identity, Starshot.Uninst.exe,
  Starshot.Update.exe, installer-line update checks).
.DESCRIPTION
  1. dotnet publish (Release: self-contained, trimmed, R2R)  -> build\release\app\
  2. Slim: CI "Remove unuse" files + unsupported locale dirs
  3. Stage a flat content dir, stamp the installer flag into Starshot.exe's DOS stub
  4. kachina-builder: pack updater -> gen (full package, no local baseline) -> pack installer
     Output: build\installer\Starshot-Setup-x64.exe (content embedded, works offline;
     CI's online shell + CDN content source are replaced by the embedded content)
  5. Optional: launch the installer (UAC) - installs to C:\Program Files\Starshot
  The C++ launcher is NOT part of the installer line (it is portable-line only), so MSBuild
  is not needed.
.PARAMETER NoBuild
  Reuse an existing build\release\app (must be built + slimmed); only restage/repack the installer.
.PARAMETER Version
  Version tag stamped into the package (update checks compare against CI releases).
  Default: latest stable release of loliri/Starshot (queried from the GitHub API; falls back to 2.6.5).
.PARAMETER LaunchInstaller
  Launch the built Starshot-Setup-x64.exe after packing (elevated).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\deploy-release.ps1 -LaunchInstaller
#>
param(
    [switch]$NoBuild,
    [string]$Version,
    [switch]$LaunchInstaller
)
$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$work = Join-Path $repo "build\installer"
Set-Location $work

# A running install locks its files
Stop-Process -Name Starshot -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (-not $NoBuild) {
    Write-Host "== Publishing app (dotnet publish Release: self-contained, trimmed, R2R) =="
    dotnet publish "src\Starshot\Starshot.csproj" -c Release -p:Platform=x64 -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    # publish 输出在 build\release\app；CopyLauncher 目标顺手拷了 C++ launcher（安装版线不用，忽略）

    Write-Host "== Slimming (CI 'Remove unuse' + locale trim) =="
    Remove-Item -Force "build/release/app/RestartAgent.exe","build/release/app/DirectML.dll","build/release/app/onnxruntime_providers_shared.dll","build/release/app/Microsoft.ML.OnnxRuntime.dll","build/release/app/VP9DecoderMFT.dll","build/release/app/avifdec.exe","build/release/app/djxl.exe","build/release/app/avifgainmaputil.exe","build/release/app/jxlinfo.exe" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "build/release/app/NpuDetect" -ErrorAction SilentlyContinue
    Remove-Item -Force "build/release/app/workloads*.json" -ErrorAction SilentlyContinue
    # 与 release.yml 一致：只保留 Starshot.Language 的 resx 支持的语言 + en-US
    $supported = Get-ChildItem "src\Starshot.Language\Lang.*.resx" | ForEach-Object {
        if ($_.Name -match '^Lang\.([a-z]{2}(-[A-Za-z0-9]+)?)\.resx$') { $Matches[1] }
    }
    Get-ChildItem "build\release\app" -Directory | Where-Object {
        if ($supported -contains $_.Name -or $_.Name -eq "en-US") { return $false }
        try { [void][System.Globalization.CultureInfo]::GetCultureInfo($_.Name); return $true } catch { return $false }
    } | Remove-Item -Recurse -Force
}

if (-not (Test-Path "build\release\app\Starshot.dll")) { throw "build\release\app missing - run without -NoBuild first" }

# --- kachina-builder (CI 同款，公共 release 二进制)
if (-not (Test-Path "kachina-builder.exe")) {
    Write-Host "== Downloading kachina-builder =="
    foreach ($i in 1..3) {
        try {
            Invoke-WebRequest "https://github.com/loliri/kachina-installer/releases/download/1.0.0/kachina-builder.exe" -OutFile "kachina-builder.exe" -ErrorAction Stop
            break
        }
        catch { Write-Host "download attempt $i failed: $($_.Exception.Message)"; if ($i -lt 3) { Start-Sleep -Seconds 5 } }
    }
    if (-not (Test-Path "kachina-builder.exe")) { throw "kachina-builder download failed" }
}

# --- 版本号：缺省取最新 stable release（更新检查据此判定"已是最新"）
if (-not $Version) {
    try {
        $r = Invoke-RestMethod "https://api.github.com/repos/loliri/Starshot/releases?per_page=10" -Headers @{ "User-Agent" = "starshot" }
        $latest = @($r | Where-Object { -not $_.prerelease -and -not $_.draft } | Select-Object -First 1)
        if ($latest.Count -gt 0) { $Version = $latest[0].tag_name }
    }
    catch { Write-Warning "could not query latest release ($($_.Exception.Message))" }
    if (-not $Version) { $Version = "2.6.5" }
}
Write-Host "== Installer version tag: $Version =="

# --- 装配：平铺内容目录 + 烙安装版标志（release.yml kachina job 同款）
Write-Host "== Staging flat content dir + stamping installer flag =="
Remove-Item "flat","kachina-hashed","kachina-metadata.json" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "build\release\app" "flat" -Recurse
$exe = Join-Path $work "flat\Starshot.exe"
$bytes = [System.IO.File]::ReadAllBytes($exe)
$eLfanew = [BitConverter]::ToInt32($bytes, 0x3C)
$magic = [System.Text.Encoding]::ASCII.GetBytes("STARSHOT-INSTALLER")
if ($eLfanew -lt 0x40 + $magic.Length) { throw "DOS stub too small (e_lfanew=$eLfanew), cannot stamp installer flag" }
[Array]::Copy($magic, 0, $bytes, 0x40, $magic.Length)
[System.IO.File]::WriteAllBytes($exe, $bytes)
Write-Host "Installer flag stamped into flat\Starshot.exe (e_lfanew=$eLfanew)"

# 配置与 release.yml kachina job 一致（更新源 = CDN 固定 URL，壳与版本无关）
$config = @{
    source           = @(
        @{ id = "stable";  name = "正式版"; uri = "https://starshot-release.cialo.site/kachina/Starshot-full-win-x64.exe" },
        @{ id = "preview"; name = "预览版"; uri = "https://starshot-release.cialo.site/kachina/Starshot-preview-win-x64.exe" }
    )
    appName            = "Starshot"
    publisher          = "loliri"
    regName            = "Starshot"
    exeName            = "Starshot.exe"
    uninstallName      = "Starshot.Uninst.exe"
    updaterName        = "Starshot.Update.exe"
    programFilesPath   = "Starshot"
    userDataPath       = @()
    extraUninstallPath = @()
    title              = "Starshot"
    description        = "Windows HDR Screenshot Tool"
    windowTitle        = "Starshot Installer"
    uacStrategy        = "prefer-user"
}
# serde_json 不吃 BOM（与 CI 注释一致）
[System.IO.File]::WriteAllText((Join-Path $work "config.json"), ($config | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
$icon = Join-Path $repo "src\logo.ico"

# --- 打包：更新器壳 -> 内容哈希 -> 全量安装器（本地无差分基线 = 纯全量，CI 首版同路径）
Write-Host "== Packing updater =="
& (Join-Path $work "kachina-builder.exe") pack -c config.json -o "Starshot.Update-x64.exe" --icon $icon
if ($LASTEXITCODE -ne 0) { throw "updater pack failed" }

Write-Host "== Generating hashed content =="
& (Join-Path $work "kachina-builder.exe") gen -j 8 -i flat -m "kachina-metadata.json" -o "kachina-hashed" -r "loliri/Starshot" -t $Version -u "Starshot.Update-x64.exe"
if ($LASTEXITCODE -ne 0) { throw "gen failed" }

Write-Host "== Packing installer =="
& (Join-Path $work "kachina-builder.exe") pack -c config.json -m "kachina-metadata.json" -d "kachina-hashed" -o "Starshot-Setup-x64.exe" --icon $icon
if ($LASTEXITCODE -ne 0) { throw "installer pack failed" }
$setup = Join-Path $work "Starshot-Setup-x64.exe"

# 中间产物清掉（builder/config/setup 保留，便于复跑）
Remove-Item "flat","kachina-hashed" -Recurse -Force

Write-Host "== Built: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB) =="

if ($LaunchInstaller) {
    Write-Host "== Launching installer (UAC prompt) =="
    Start-Process $setup -Verb RunAs
    Write-Host "Installer is in front of you - complete it. Target: C:\Program Files\Starshot"
}
