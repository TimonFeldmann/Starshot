# HdrSignalHost

Throwaway probe tool (not shipped, not part of the app). A headless D3D11 host that renders the
four flat HDR10 fields (green/red/blue/yellow, GREEN first) in a borderless window covering a
target monitor. The GREEN phase lasts `firstPhaseSec`, the other three `periodSeconds` each.
It is the controllable stimulus for the HDR flat-field recording tests: it shows a known nit
level on a real HDR panel so the Starshot capture pipeline can be verified end-to-end without a
video player.

## Usage

```
hdr-signal.exe [targetWpx targetHpx [periodSeconds [t0File [nits [firstPhaseSeconds [maxSeconds [pauseFlagFile]]]]]]]
```

- `targetWpx`/`targetHpx` (default 3840x2160): select the target monitor by its PHYSICAL
  resolution (the process is DPI-unaware, so panel resolution is not virtualized by scaling).
- `periodSeconds` (default 6): cycle length of RED/BLUE/YELLOW.
- `t0File` (default `%TEMP%\starshot\hdr_signal_t0.txt`): written atomically (tmp + rename) with
  `<unix-ms> <period>` so an orchestrator can poll for it and align recordings to color phases.
  `t0` = the moment the first GREEN phase starts (the host's first presented frame).
- `nits` (default 10000): the luminance of each lit field. The swap chain is R10G10B10A2 with
  color space `RGB_FULL_G2084_NONE_P2020` (HDR10/BT.2020/PQ), so each lit channel is cleared with
  the ST 2084 PQ code for `nits` (10000 nits = code 1023 = full-scale ground truth).
- `firstPhaseSeconds` (default 10): length of the opening GREEN phase. A recording's WGC session
  init eats the first ~1.5–2.5 s of a capture, so the test's single recording starts at t0 and
  relies on this extra margin to keep GREEN visible.
- `maxSeconds` (default 0 = run forever): optional hard stop - the host exits on its own after
  `maxSeconds` of NON-PAUSED time (a pause extends the run). The recording rows leave it 0 (the
  orchestrator kills the process); the play-only test sets it so an interrupted run never leaves
  a bright field on the panel.
- `pauseFlagFile` (default: none = pausing disabled): while that file EXISTS, the color phases
  and the label countdown FREEZE on the current color - the window stays up and keeps presenting
  the frozen frame (which also keeps a WGC capture session alive) - and the `maxSeconds` clock
  holds. The HDR test's Space-pause creates/removes this file (`<row workdir>\paused.flag`);
  the record-test child holds its own duration budget on the same flag, so a paused row resumes
  from where it stopped instead of restarting.

## Build

```
pwsh tools\HdrSignalHost\build.ps1
```

Needs the MSVC C++ workload (vcvarsall x64). Output: `hdr-signal.exe` in this directory.

## On-screen label

The host draws a two-line label in the panel's top strip (top ~1/14 of the window height, well
inside the panel's top quarter - OUTSIDE the test's central crop, so it cannot affect the
flat-field code verification; the classifier samples the central crop only):

- line 1: `<width>x<height> @ <nits> nits` - the row identity (which panel/row this is),
- line 2: `<COLOR> | <n>s left | t+<total>s` - the current phase and countdown.

The text changes at most once per second, so it adds at most one extra WGC frame per second to a
recording (harmless: it lives outside the central crop). It lets an operator watching a live
panel - or a recording - tell which row and which color phase is on screen at a glance.

The label is a 5x7 bitmap font (pure C++ glyph data, no font engine) rendered into a texture the
label quad alpha-blends over the field. Environment quirks that shaped the implementation (all
verified on this machine):

- GDI text drawing is a silent no-op here (TextOut returns TRUE but never touches a DIB), and the
  installed SDK's D2D1/DWrite headers lack the text-to-pixels interop (no shared resources,
  software device, or CPU rasterizer), so the font is generated procedurally.
- DYNAMIC texture updates do not work: `Map(D3D11_MAP_WRITE)` returns E_INVALIDARG and
  `UpdateSubresource` is a silent no-op (readback stays all-zero). The label therefore
  RECREATES a `USAGE_DEFAULT` texture with `pInitialData` on each change (at most one per
  second) and swaps the SRV.
- The context's pipeline state is observed to be lost after a one-time bind; the label state is
  re-applied every frame (cheap, and it is what makes the quad actually render).
- The swap chain uses 3 flip-model buffers: with 2, the per-frame full clear clobbers the
  in-flight back buffer before the compositor reads it, so drawn content (the label) is lost on
  screen even though it is present in the buffer.

## Why it exists / environment notes

- A video player's output can bind to a different monitor than its window, so a player is not a
  reliable flat-field source. This host owns both its window and its swap chain, so the content
  lands exactly on the target panel.
- The window's `WndProc` must call `DefWindowProcW` directly (no global function pointer): under
  the installed EDR/Windhawk hooks a WndProc that invokes a cached global `WNDPROC` makes
  `CreateWindowEx` fail with error 2.
- The D3D11 device is created BEFORE the window: in this environment that order is what lets
  `CreateWindowEx` succeed.
- The host foregrounds + sets TOPMOST once at startup only; an orchestrator must re-foreground it
  right before each recording (a periodic re-assert steals focus and its ALT press causes a
  visible flicker).

Driven by the XUnit integration tests in `src/Starshot.Tests` (category `HdrRecording`): the
recording rows take the automated `--record-test` capture, and `HdrFlatFieldPlay` plays the
fields without recording so a manual recording can capture them (it never kills the user's
Starshot instance). See the "Integration tests" section of the top-level README.
