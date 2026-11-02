<#
.SYNOPSIS
Launches the Starshot HDR flat-field integration test against the physical HDR display.

.DESCRIPTION
Asks how to run: with or without automatic recording, and how long each color phase plays
(a single value for all colors, default 5 s):

  With automatic recording: runs the 4-row suite (HdrFlatFieldRecording). Each row paints
  four N-nit BT.2020 color fields (GREEN/RED/BLUE/YELLOW, -PhaseSeconds each, default 5 s)
  and takes an automated record-test capture (4 x -PhaseSeconds), verifying the color
  codes against the analytic ST 2084 + BT.2020 ground truth (~2.5 min at the default). A
  running Starshot instance is left alone
  (the headless record-test child runs alongside it) - but do not focus your Starshot
  window during a row: the capture follows the foreground window, and a stray focus
  change makes that row record the wrong window and fail.

  Without automatic recording (play-only): runs HdrFlatFieldPlay, which paints the
  4K/10000-nit color fields on the panel for STARSHOT_HDR_PLAY_SECONDS (default 120,
  clamped to 15 s..1 h) and takes no recording, so you capture them with your own
  manual recording. The test does NOT kill a running Starshot instance - launch the
  app while the fields are up and record (the fields stay up for the whole play
  window - start your recording when you are ready). The fields self-clear even if
  the test is interrupted.

Keyboard control during a run (no console focus needed - the keys are global): ESC ends
the suite, Space freezes the field on the current color (the window stays up) and the
next Space resumes from that spot; in play-only mode a pause also extends the play
window by the held time.
This script prints each pause / resume / stop as it happens, so a paused suite is
distinguishable from a hung one.

The test self-skips when a prerequisite is missing (a plain run is always safe):
a Windows HDR display at the panel's resolution, the encoder DLLs in build/codecs
(see tools/codecs/build-codecs.ps1), and ffmpeg/ffprobe (set STARSHOT_TEST_FFMPEG_DIR,
put ffmpeg.exe + ffprobe.exe in tools\ffmpeg\bin, or add them to PATH - the
tools\ffmpeg\bin copy needs no environment variable once in place).

.PARAMETER Mode
Skips the question: 'WithRecording' or 'PlayOnly'.

.PARAMETER PhaseSeconds
Seconds each color phase plays (a single value for all colors; default 5, clamped to
2 s..300 s). Recording rows capture 4 x PhaseSeconds; play-only uses it for all
colors, GREEN included. When omitted, the script
asks for it (an empty answer keeps 5); the effective value is exported to the test as
STARSHOT_HDR_PHASE_SECONDS.

.EXAMPLE
pwsh scripts\run-hdr-test.ps1
Asks which mode to launch in.

.EXAMPLE
pwsh scripts\run-hdr-test.ps1 -Mode PlayOnly
Goes straight to play-only mode.

.EXAMPLE
pwsh scripts\run-hdr-test.ps1 -PhaseSeconds 10
Runs with each color phase lasting 10 s (recording rows then capture 40 s).
#>
[CmdletBinding()]
param(
    [ValidateSet('WithRecording', 'PlayOnly')]
    [string]$Mode,
    <#
    Seconds each color phase plays - a single value for all colors, GREEN included
    (recording rows and the play-only cycle alike). Default 5; the test clamps
    to 2 s..300 s. When omitted, the script asks for it; the effective value is exported to
    the test as STARSHOT_HDR_PHASE_SECONDS.
    #>
    [ValidateRange(2, 300)]
    [int]$PhaseSeconds = 5
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Phase length (seconds per color, all colors): the effective value is exported to the test
# as STARSHOT_HDR_PHASE_SECONDS below - it is taken from -PhaseSeconds when given, or
# answered in the prompt when not (the prompt comes after the mode question, further down).

# Prerequisite check: ffmpeg/ffprobe (the test self-skips without them, so warn only).
# Same order the test resolves them in: STARSHOT_TEST_FFMPEG_DIR, the repo-local
# tools\ffmpeg\bin copy, or PATH.
$haveFfmpeg =
    ($env:STARSHOT_TEST_FFMPEG_DIR -and (Test-Path (Join-Path $env:STARSHOT_TEST_FFMPEG_DIR 'ffmpeg.exe'))) -or
    (Test-Path (Join-Path $repoRoot 'tools\ffmpeg\bin\ffmpeg.exe')) -or
    [bool](Get-Command ffmpeg -ErrorAction SilentlyContinue)
if (-not $haveFfmpeg) {
    Write-Warning 'ffmpeg/ffprobe not found: set STARSHOT_TEST_FFMPEG_DIR, put ffmpeg.exe + ffprobe.exe in tools\ffmpeg\bin, or add them to PATH - or the test will self-skip.'
}

# Runs `dotnet test` as a child and watches the suite's live state file (written by the
# test's key watcher - HdrKeyControl), printing each pause / resume / stop so a paused
# suite never looks like a hung one. All printing goes through Write-Host so the
# function's only pipeline output is the exit code.
function Invoke-HdrTest {
    param(
        [Parameter(Mandatory = $true)][string]$Filter,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $stateFile = Join-Path $env:TEMP 'starshot_hdr_suite_state.txt'
    Remove-Item $stateFile -ErrorAction SilentlyContinue # stale from a killed run

    Write-Host $Label
    Write-Host 'Keyboard (global, no focus needed): ESC ends the suite - Space freezes the field on the current color (the window stays up); press Space again to resume from that spot.'

    $proc = Start-Process -FilePath 'dotnet' -ArgumentList @('test', 'src/Starshot.Tests/Starshot.Tests.csproj', '--filter', $Filter) -NoNewWindow -PassThru
    $script:HdrFinalState = $null
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 500
        $state = $null
        if (Test-Path $stateFile) {
            $state = (Get-Content $stateFile -Raw).Trim()
        }
        if ($state -and $state -ne $script:HdrFinalState) {
            switch ($state) {
                'paused' {
                    Write-Host ''
                    Write-Host '[HDR] PAUSED - the field is frozen on the current color; press Space to resume or ESC to end the suite'
                }
                'stopped' {
                    Write-Host ''
                    Write-Host '[HDR] STOPPED by ESC - ending the suite (remaining rows skip)'
                }
                'running' {
                    if ($script:HdrFinalState -eq 'paused') {
                        Write-Host ''
                        Write-Host '[HDR] RESUMED - the color cycle continues from where it stopped'
                    }
                }
            }
            $script:HdrFinalState = $state
        }
    }
    $proc.WaitForExit()
    return $proc.ExitCode
}

# The question (skipped when -Mode was given).
if (-not $Mode) {
    Write-Host ''
    Write-Host 'HDR flat-field test - how to launch:'
    Write-Host '  [1] With automatic recording    - 4-row suite: four equal color phases per row, automated record-test capture + code check.'
    Write-Host '                                   Leaves a running Starshot instance alone; do not focus it during a row.'
    Write-Host '  [2] Without automatic recording - play-only: color fields painted for STARSHOT_HDR_PLAY_SECONDS'
    Write-Host '                                   (default 120 s) so you can record manually. Starshot is not killed.'
    $answer = Read-Host 'Enter 1 or 2 (default: 1)'
    if ($answer -match '^\s*2\s*$') {
        $Mode = 'PlayOnly'
    }
    else {
        $Mode = 'WithRecording'
    }
}

# Phase length (only asked when -PhaseSeconds was not given): seconds each color phase
# plays - a single value for all colors (2..300 s, default 5; the test clamps). An empty
# answer keeps the default.
if (-not $PSBoundParameters.ContainsKey('PhaseSeconds')) {
    Write-Host ''
    Write-Host 'Seconds each color phase plays (a single value for all colors; 2..300 s, default 5):'
    $phaseAnswer = [string](Read-Host 'Enter the seconds per color (default: 5)')
    if ($phaseAnswer -match '^\s*(\d+)\s*$') {
        if ([int]$Matches[1] -ge 2 -and [int]$Matches[1] -le 300) {
            $PhaseSeconds = [int]$Matches[1]
        }
        else {
            Write-Warning "Phase length '$phaseAnswer' is outside 2..300 s - using the default 5 s."
        }
    }
    elseif ($phaseAnswer.Trim() -ne '') {
        Write-Warning "Could not read '$phaseAnswer' as a number of seconds - using the default 5 s."
    }
}

# Export the effective phase length to the test (it clamps to 2..300 and defaults to 5).
$env:STARSHOT_HDR_PHASE_SECONDS = "$PhaseSeconds"

switch ($Mode) {
    'WithRecording' {
        Write-Host ''
        Write-Host 'Running the recording suite (rows: 4K/10k, 1440p/10k, 4K/5k, 4K/240 nits) ...'
        if (Get-Process -Name Starshot -ErrorAction SilentlyContinue) {
            Write-Host 'Starshot is running - it will be left alone and the existing build\app is used (close the app to test freshly changed source).'
            Write-Host "Keep your Starshot window unfocused during each row (the $(4 * $PhaseSeconds) s capture must keep the signal host in front)."
        }
        $exitCode =
            Invoke-HdrTest -Filter 'FullyQualifiedName~HdrFlatFieldRecording' -Label "4-row recording suite: each row paints four $PhaseSeconds s color fields (4 x $PhaseSeconds s), takes one automated record-test capture, and checks the color codes against the analytic ground truth."
    }
    'PlayOnly' {
        $playSeconds = if ($env:STARSHOT_HDR_PLAY_SECONDS) { $env:STARSHOT_HDR_PLAY_SECONDS } else { '120' }
        Write-Host ''
        Write-Host "Running play-only mode: color fields up for $playSeconds s (override with STARSHOT_HDR_PLAY_SECONDS, 15 s..1 h) ..."
        if ($PhaseSeconds -ne 5) {
            Write-Host "All colors (GREEN included) play $PhaseSeconds s (the play window length is unchanged)."
        }
        Write-Host 'When the color fields appear on the panel: launch Starshot and start your manual recording.'
        Write-Host 'The test takes no recording and does not kill a running Starshot instance.'
        $exitCode =
            Invoke-HdrTest -Filter 'FullyQualifiedName~HdrFlatFieldPlay' -Label "Play-only mode: the fields stay up for $playSeconds s so you can record manually (ESC ends the play window early; Space freezes the fields on the current color and extends the window by the held time)."
    }
}

if ($Mode -eq 'WithRecording' -and $exitCode -eq 0) {
    Write-Host ''
    if ($script:HdrFinalState -eq 'stopped') {
        Write-Host 'Suite ended by ESC (the remaining rows were skipped).'
    }
    else {
        Write-Host 'Suite passed.'
    }
}
exit $exitCode
