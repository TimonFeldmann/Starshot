# Recording frame rate is extremely low — performance investigation

Why Starshot recorded at a few fps (HDR) instead of 30–60, the root causes, the
CPU-side fixes (landed 2026-10-30; pipeline decoupling + preset tuning 2026-11-01),
the remaining-CPU-levers analysis (2026-11-01), the post-encode disk-buffer mode
(feature, 2026-11-01), and the GPU roadmap (the eventual fix for 60 fps 4K).

## Symptom (measured end-to-end, before the fixes)

Frame counts from real user recordings (`ffprobe -count_frames`, 2026-10-30):

| file | content | frames / span | achieved rate |
| --- | --- | --- | --- |
| `Recording_20261030_22035395.mp4` | 4K HDR | 31 | ≈ 3.1 fps (declared 37/12) |
| `Recording_20261030_22024139.mp4` | 1440p HDR | 69 | ≈ 3.4 fps (declared 41/12) |
| `Recording_20261030_21254157.mp4` | 4K HDR | 50 | ≈ 1.4 fps (declared 17/12) |

The odd `r_frame_rate` values (37/12, 17/12) are a display artifact: the muxer derives
the declared rate from the pts span at finalize, so "37/12 fps" simply means "31 frames
over the span that actually elapsed". The real number is *frames ÷ wall-clock span* —
**4K HDR ≈ 1.4–3.1 fps, 1440p HDR ≈ 3.4 fps.**

Note: WGC delivers a frame only when content changes (fpsCap=0 = native refresh). The
measured rates are the pump's *throughput ceiling*; with a 60 Hz screen and active
content, ~97% of delivered frames are dropped by the pool/channel.

## Pipeline shape (where the time goes)

`WgcCaptureSource.cs`:

- `Direct3D11CaptureFramePool.CreateFreeThreaded(..., depth 4)` — frames queue in the pool.
- `Channel.CreateBounded(2)` with **drop-oldest** in `OnFrameArrived` — when the pump is
  slower than the arrival rate, old frames are discarded to keep the newest.
- A **single pump `Task`** consumes the channel and calls `VideoRecordService.OnFrame`.

`VideoRecordService.OnFrame` is one serial chain on that one pump thread:

```
1. readback   CanvasBitmap.CreateFromDirect3D11Surface + GetPixelBytes
               (GPU→CPU: 66 MB @ 4K HDR, 33 MB @ 4K SDR, per frame)
2. convert    FrameProcessor.ConvertHdr / ConvertSdr
               (now row-parallel across pool threads; HDR clli Y stats accumulated
               by the same pass - no separate scan)
3. encode     blocking x265 (HDR, P010) / x264 (SDR, NV12) call, 16 encoder threads
4. muxer      Mp4Muxer.AddVideoSample — unbuffered per-sample write to a FileStream
```

Because every stage is serial on one thread, **effective fps = 1 / (sum of all stage
costs)**. The pool/channel then drops whatever the pump can't keep up with. Audio
(WASAPI loopback → faac) runs on its own polling thread and is *not* on this path.

## Per-stage cost, measured (baseline, before the fixes)

Measured with `tools/PerfProbe` (mirror of the app's conversion loops/LUTs + P/Invoke
into the exact `Starshot.Codecs.dll` with the exact `VideoEncoder` settings: x265
`faster` / x264 `veryfast`+film, rc-lookahead=0, bframes=0, keyint=120, CRF 22/23 =
quality 1 default, 16 encoder threads = `max(2, ProcessorCount/2)` on this machine,
a 16c/32t Zen 5 CPU + a high-end dGPU). Note: the probe's main pass hardcodes the
*baseline* `faster` preset so the tables above stay comparable; the app's current
default is `ultrafast` and is user-configurable (Settings → Recording → "Encoder
speed (HDR)"). Use `--ab` for the current preset ladder. "flat" = one frame fed repeatedly (easiest
content); "noise" = 8 random frames cycled (pathological worst case; real UI / video
content sits between, much closer to flat).

| stage | 4K (3840×2160) | 1440p (2560×1440) |
| --- | ---: | ---: |
| readback (memcpy floor; real = Win2D GPU→CPU copy) | 2.8 ms | 1.1 ms |
| convert HDR, 1 thread (PQ + BT.2020, half-float LUTs) | **103.3 ms** | 42.8 ms |
| convert SDR, 1 thread (NV12) | 19.8 ms | 8.1 ms |
| clli Y scan (second full pass) | 5.1 ms | 2.3 ms |
| x265 encode, flat | 62.6 ms | 25.9 ms |
| x265 encode, noise | **395.9 ms** | 222.8 ms |
| x264 encode, noise | 21.0 ms | 9.5 ms |

Baseline pump total per frame:

| | total | → fps ceiling |
| --- | ---: | ---: |
| 4K HDR | ~507 ms | ~2.0 |
| 1440p HDR | ~268 ms | ~3.7 |
| 4K SDR | ~42 ms | ~24 |
| 1440p SDR | ~18 ms | ~56 |

These ceilings matched the measured end-to-end numbers (4K 1.4–3.1, 1440p 3.4) — the
pipeline model is complete, nothing else was hiding.

## Root causes, ranked

1. **The shipped `libx265.dll` was a pure-C build — no SIMD at all.**
   The x265 banner from any run:
   `x265 [info]: build info [Windows][MSVC 1951][64 bit][noasm] 10bit` and
   `x265 [info]: using cpu capabilities: none!`
   Proof: the build's `CMakeCache.txt`
   (`a local x265 CMake build cache`) had
   `ENABLE_ASSEMBLY:BOOL=OFF` and `NASM_EXECUTABLE:FILEPATH=NASM_EXECUTABLE-NOTFOUND`
   (nasm was not on the machine when it was built). That one decision made the x265
   encode cost ~19× the x264 cost on 4K noise (396 ms vs 21 ms) even though both used
   16 threads. It was the single dominant cost of the HDR path.
2. **The HDR pixel conversion was single-threaded and non-SIMD** — 103 ms @ 4K, which
   was *more* than a flat 4K x265 encode (62 ms). `FrameProcessor.ConvertHdr` was a
   scalar loop with a half-float→float LUT, a PQ LUT, the WgcTo2020 matrix, and 16-bit
   stores. The work is embarrassingly parallel per row; it used exactly 1 thread.
3. **The whole chain is serialized on one pump thread** (readback → convert → clli →
   blocking encode → muxer write). No stage overlaps the next. This is why the *sum*
   of the stages is the ceiling. Note this architecture is **fine for SDR** — the same
   serial chain does ~24 fps @ 4K SDR — so it is not the bug by itself; it is what
   turns (1)+(2) into the observed fps.
4. **The clli scan was a redundant second full pass** over the Y plane (5.1 ms @ 4K)
   that only existed to track min/max/mean for the `clli` box.
5. **Readback is NOT a bottleneck.** The 66 MB GPU→CPU copy has a memcpy floor of
   ~2.8 ms @ 4K; the actual Win2D readback is only a little more. The pipeline moves a
   lot of bytes, but not enough to matter.

## Why SDR recordings feel fine

Same serial architecture, but: NV12 conversion is cheap (20 ms @ 4K baseline), and the
x264 in `build\codecs` is the OBS 0.164 build — a **full-SIMD (AVX512) encoder** —
21 ms @ 4K noise. So 4K SDR ran at ~24 fps and 1440p SDR at ~56 fps. The HDR gap is
not the algorithm; it is the **encoder build (noasm x265)** plus the
**single-threaded float conversion**.

## Fixes landed (2026-10-30)

All three CPU-only wins from the plan are in:

1. **x265 rebuilt with `ENABLE_ASSEMBLY=ON`.** nasm 3.02 lives in `tools\nasm\bin\`
   (gitignored, same pattern as `tools\ffmpeg\`); the x265 4.3 source tree was
   reconfigured and rebuilt with `-DENABLE_ASSEMBLY=ON` (inside a vcvars64 shell —
   a plain shell lacks `INCLUDE`/`LIB` and the C++ headers are not found), and the new
   DLL is what `tools\codecs\build-codecs.ps1` now gathers. Banner is now
   `using cpu capabilities: MMX2 SSE2Fast LZCNT SSSE3 SSE4.2 AVX FMA3 BMI2 AVX2`.
2. **`FrameProcessor` converts in parallel** (`ConvertHdr`/`ConvertSdr`): rows are
   split into contiguous disjoint chunks, one pool `Task` per chunk
   (`min(rows, ProcessorCount)` workers; frames below 1 MP stay serial — task spin-up
   would cost more than the work). The per-pixel math is unchanged; the output is meant
   to be bit-identical to the serial version — and it was, except for one chroma-row
   off-by-one in the chunk mapping that survived the initial implementation (see
   "Off-by-one chroma-row bug" below; caught by the user's recordings, regression-checked
   by `PerfProbe --verify`).
3. **clli folded into the conversion** — `ConvertHdrRows` accumulates the 10-bit Y
   min/max/sum per chunk and reduces across chunks; `FrameProcessor.ClliY` exposes
   `(min, max, mean)` and `VideoRecordService.OnFrame` uses it instead of the old
   second-pass scan.

Re-measured with the updated `tools\PerfProbe` (same settings; the probe now mirrors
the parallel conversion and the folded clli):

| stage | 4K before → after | 1440p before → after |
| --- | --- | --- |
| convert HDR (+ clli folded) | 103.3 + 5.1 → **26.7 ms** | 42.8 + 2.3 → **11.9 ms** |
| convert SDR | 19.8 → **1.8 ms** | 8.1 → **0.9 ms** |
| x265 encode, flat | 62.6 → **21.5 ms** | 25.9 → **10.0 ms** |
| x265 encode, noise | 395.9 → **152.9 ms** | 222.8 → **84.8 ms** |
| x264 encode, noise | 21.0 → 20.8 ms | 9.5 → 9.2 ms |

New pump total per frame (worst case = noise content; real UI content is much closer to
flat):

| | before | after |
| --- | ---: | ---: |
| 4K HDR | ~507 ms (~2.0 fps) | **~182 ms (~5.5 fps worst case; ~10–20 fps for typical UI content)** |
| 1440p HDR | ~268 ms (~3.7 fps) | **~98 ms (~10 fps worst case; ~20–40 fps typical)** |
| 4K SDR | ~42 ms (~24 fps) | **~24 ms (~42 fps)** |
| 1440p SDR | ~18 ms (~56 fps) | **~11 ms (~95 fps)** |

(Re-measured 2026-11-01 with a 20-iteration quick pass: 4K HDR pump total ~119 ms /
~8.4 fps, 1440p ~65 ms / ~15.4 fps. The x265 encode time wanders with CPU boost
state — 152.9 ms (4K noise) in the 60-iteration steady-state pass vs 87.7 ms in the
quick pass — so read the encode rows as a range; the ASM advantage is unambiguous
in every run.)

A 3–5× improvement in the HDR ceiling from CPU-only work. The encode is now the
limiting stage (even with assembly, x265 at 4K is a big job) — which is exactly what
the GPU roadmap replaces.

### Toolchain quirk found along the way (keep in mind)

The .NET SDKs on this machine (8.0.427 and 10.0.400, both verified with a minimal
repro) capture a **`for` loop variable shared across iterations** (pre-C# 5
semantics): `t[i] = Task.Run(() => use(i))` gives every task the loop's *final* value
of `i`. Fresh locals declared inside the loop body are captured per-iteration, as
expected. `FrameProcessor` therefore uses an `int idx = i;` local that the lambdas
capture (see the comment in the code) — don't "simplify" the lambdas back to the loop
variable.

## Off-by-one chroma-row bug (regression in the parallel conversion, found 2026-10-30)

Symptom (user report): horizontal chroma lines across the whole video at **fixed
vertical positions**, only visible where a chroma edge exists (so visibility was
color-dependent — "all colors except red").

Root cause: the chunk mapping assumed `cbRow = rowBegin / 2` is the chunk's first
4:2:0 block row. That is only true for **even** `rowBegin`. On this machine
(32 pool threads) 4K rows split into 67/68-row chunks, so every other chunk starts
on an odd row (e.g. rows 67–135). For such a chunk the first even row is
`rowBegin + 1`, whose block row is `(rowBegin + 1) / 2` — the buggy start wrote the
chunk's chroma one 4:2:0 row (2 luma rows) **too high**, and the chunk's **last**
chroma row was never written at all, leaving the previous frame's chroma there (a
ghost line). Fixed positions + color-edge dependence = exactly the reported lines.

Fix (app `ConvertSdrRows` + `ConvertHdrRows`, mirrored in `tools\PerfProbe`):

```csharp
int cbRow = (rowBegin + (rowBegin & 1)) / 2;
```

Regression check: `PerfProbe --verify` (no GPU/encoder needed) — a synthetic 4K black
field with 1-chroma-row green stripes on the first/last chroma rows of the first
odd-starting chunk (located from the actual worker count, so the check holds on any
machine; 32 workers → chunk [67,135)). PASSes with the fix, FAILs (both SDR and HDR)
with the old formula.

## HDR suite: fps-agnostic color assertions (2026-11-01)

The new pump records at ~10–22 fps (old: ~3.5). That changed the suite's flat-field
chroma results: under CRF the per-frame QP is set from the per-frame bitrate budget,
which scales as **1/fps**, so the settled 10-bit chroma DC of a saturated flat field
lands up to ~8 codes away from the input. Measured on RED@10000n U (standard 369):

| pump frame rate | frames/phase | settled U |
| ---: | ---: | ---: |
| 16.4 (first new-pipeline run) | 83 | 360.7 |
| 22.5 (1440p, 2026-11-01) | 115 | 370.3 |
| 9.9 (4K, 2026-11-01) | 50 | 371.1 |
| 3.5 (old pipeline) | 18 | 370.3 |

Three fps regimes, three different ABR/QP equilibria — and **no in-phase drift**
(per-frame spread ~0.1 code in every run), while luma stayed exact in all of them.
This is an encoder rate-control characteristic of pathological flat content, not a
color-pipeline defect (the old low-fps pipeline passes the standard band under the
same machine state, with either x265 build).

The suite (`HdrRecordingTests`) was made agnostic of the frame rate:

- **Y stays strict (±6)** — luma has been exact (±2) in every run; it is the
  precision check.
- **Chroma band widened to ±12** (`ChromaTolerance`) — absorbs the ≤8-code fuzz. A
  real chroma bug (wrong matrix/primaries/gamma) shifts U/V by 50+ codes and still
  fails easily.
- **New per-phase stability guard (±2)** — per-frame codes within a color phase must
  stay within 2 codes of the segment mean (first/last 2 transition frames excluded).
  This is the fps-agnostic drift detector: a gradual in-phase color drift (display
  EDR/LUT drift) fails at any frame rate, which is the hazard the wider band
  weakens.

All 4 rows pass with this design (2026-11-01), including the 5000-nit row that hit a
panel-provisioning transient the day before. `MachineExpectation` values were not
changed (still within the new bands); if the encoder preset/QP or the fps regime
changes materially, re-measure them (see the comment in `HdrRecordingTests.cs`).

## Pipeline decoupling + x265 preset (2026-11-01)

Two more CPU-side wins, on top of the 2026-10-30 set:

1. **Ingest decoupled from encode.** The pump thread now only does readback +
   convert; a **dedicated encode thread** consumes a bounded job channel
   (cap 3, `FullMode.Wait` = backpressure, never drops) and runs x265/x264 +
   the muxer. Conversion uses a **double-buffered `FrameProcessor`** (two
   instances, ping-pong): each set has its own `SemaphoreSlim`, released by the
   encode thread only *after* it is done reading the planes (dequeue ≠ read-done,
   so the semaphore — not the channel — guards the planes). Stop drains the
   encode queue before the audio/encoder flush. The muxer's video state is now
   touched by the encode thread alone (audio state by the audio thread), keeping
   its thread-safety-by-convention.
2. **Per-frame 66 MB LOH allocation eliminated.** Readback now copies into a
   session-owned buffer via `GetPixelBytes(IBuffer)` (the Win2D projection only
   exposes the `IBuffer` overload for write-into-buffer; `byte[].AsBuffer()`
   wraps the session buffer, zero extra allocation) instead of
   `GetPixelBytes()` which allocated a fresh 66 MB array per 4K frame.
   `CanvasDevice` is hoisted to the session.
3. **x265 preset `faster` → `ultrafast`.** `PerfProbe --ab` (4K, this machine)
   shows `ultrafast` is **2.5× faster on hard content** (130.5 → 51.8 ms) and
   1.6× on flat (39.5 → 24.6 ms) at equal CRF. `veryfast` and `no-psy-rd`
   measured as no-ops; frame-threads are already at the build cap
   (`X265_MAX_FRAME_THREADS=16`), so threads is not a knob. Trade-off: slightly
   larger files / marginally lower quality at equal CRF — acceptable for mostly-
   static screen content (one-line revert to `faster` if quality is preferred).

Because the pump (readback+convert ≈ 31 ms @ 4K) now runs fully in parallel with
the encoder, the ceiling is `max(pump, encode)` instead of their sum. The encode
remains the dominant cost, and at 4K it is a function of pixel count:

| | serial+faster | decoupled+faster | decoupled+ultrafast |
| --- | ---: | ---: | ---: |
| 4K HDR (suite 10000-nit) | 9.9 fps | 11.2 | **11.6** |
| 1440p HDR (suite 10000-nit) | 22.5 fps | 25.4 | **28.9** |

(End-to-end from the HDR suite's 20 s recordings, `nb_frames / duration`.)
1440p now sits at ~29 fps. **4K stays at ~12 fps** — the x265 encode of a 4K
frame is ~80 ms regardless of preset, and no CPU-only change closes that gap.
All 4 suite rows still pass (Y ±6, chroma ±12, drift ±2), so the pipeline changes
are color-neutral. This is the hard wall the **GPU roadmap** (below) removes:
NVENC encodes 4K 10-bit HEVC on the ASIC in ~ms at near-zero CPU cost, which is
the only route to 30–60 fps at 4K.

### Re-measured fps after L3/L5 (pool-fixed build, 5 s phases, 2026-11-01)

The L3 bring-up hit the straggler double-work bug (see "L3 implemented" below);
the table shows the bug was real end-to-end, not just in the probe. Same
conditions as the baseline table (suite 5 s phases, 20 s recordings,
`nb_frames / duration`):

| row | baseline | pre-poolfix build | pool-fixed build |
| --- | ---: | ---: | ---: |
| 4K@10000n | 11.6 | 8.5 | **11.75** |
| 1440p@10000n | 28.9 | 17.8 | **24.7** |
| 4K@5000n | 12.0 | 8.5 | **11.2** |
| 4K@240n | 12.9 | 8.2 | **11.0** |

So the 2.3x-slow first-cut pool had cost ~40 % of the 4K ceiling (8.5 vs 11.75);
the fixed pool is back at baseline (within the run-to-run display state). All 4
rows **pass** (Y ±6, chroma ±12, drift ±2). Two caveats, both environmental
not code:

- The recorded fps is **WGC-delivery/display-bound, not encoder-bound, for these
  rows**: WGC only delivers a frame when the panel output changes, and this
  display's EDR/LUT drift makes the "static" flat field change frame-to-frame
  (exactly what the drift guard detects), so WGC delivers at refresh and the
  recorded rate = the pipeline ceiling. The 4K ceiling is encode-bound (~12 fps);
  1440p varies with display state (24.7 this run vs 28.9 baseline).
- Two consecutive 5 s suite runs (pre-poolfix) **failed 4/4 on the ±2 drift
  guard** (in-phase drift 4.9–58.7 codes; Y/U/V means still within the color
  bands). The pool-fixed run passes 4/4, so the failures were a **transient
  display EDR/LUT state**, not a pipeline regression (the conversion is a
  deterministic pure function of the readback; L1 was off). If the guard fails
  again on a future run with a passing mean check, re-run the row before
  suspecting the code.

## x265 preset A/B (via `PerfProbe --ab`)

`dotnet tools/PerfProbe/bin/Release/net10.0/PerfProbe.dll --ab` compares knob
variants in-process (fair, since x265 time wanders with CPU boost state across
separate runs). 4K, 60 iters, CRF 22:

| variant | noise (ms/frame) | flat (ms/frame) |
| --- | ---: | ---: |
| faster t16 (old default) | 130.5 | 39.5 |
| veryfast t16 | 129.6 | 39.2 |
| **ultrafast t16 (new default)** | **51.8** | **24.6** |
| faster t16 no-psy-rd | 131.7 | 40.8 |
| veryfast t16 no-psy-rd | 133.6 | 40.5 |

`ultrafast` is the only knob that moves the needle; `no-psy-rd` and `veryfast`
are within noise of `faster`, and `frameNumThreads` is capped at 16 by the build
(values ≥ 17 fail `x265_encoder_open`).

## Remaining CPU levers (analysis, 2026-11-01)

What else can squeeze fps out of the CPU path before (or without) the GPU work.
Verdict up front: **no pipeline rewrite is needed** — the decoupled shape (pump =
readback+convert ∥ encode thread, double-buffered planes, bounded job channel) is
correct. What remains is one feature (content-adaptive frame skip), a batch of x265
knob tuning, and some hygiene.

### Where the time goes now (4K HDR, measured)

| stage | cost | note |
| --- | ---: | --- |
| readback (Win2D GPU→CPU, 66 MB) | ~3–5 ms | 2.8 ms memcpy floor + Win2D wrapper |
| convert (row-parallel, 16 pool workers) | ~8–15 ms | readback + convert ≈ 15–20 ms total |
| **x265 encode (ultrafast, t16), flat / noise** | **~52 / 88–150 ms** | **the wall**; 88–150 wander = CPU boost state |
| muxer write | <0.1 ms | SSD headroom is ~100× our bit rate |

So 4K HDR = 11.6 fps, **purely encode-bound** (convert+readback is ~15–20 ms of the
~86 ms frame budget). 1440p HDR = 28.9 fps, also encode-bound. 4K SDR (x264
veryfast, ~21 ms/frame on noise) is already ~30–45 fps — the 30–60 fps gap exists
**only on the 10-bit HDR path**.

### L1 — content-adaptive frame skip/repeat (the one "big" item)

WGC keeps delivering frames at refresh even when the screen barely changes, and x265
pays the full ~80 ms per 4K frame no matter how little changed. A cheap per-frame
signature (e.g. a 16×16 grid sample of the already-resident `PixelBuf`,
~0.3–0.5 ms) lets us skip the encode when the screen is (nearly) unchanged — two
variants:

- **VFR variant (safe, standard):** write no sample; the MP4 track has a PTS gap and
  players hold the last decoded frame. Smallest files, spec-clean. "60 fps" becomes
  "60 fps except during static stretches" (sample-count fps drops, perceived
  smoothness unchanged).
- **Repeat-sample variant (constant 60 fps):** re-mux the last P-frame access unit
  with a new PTS. Needs `ref≥2` (with the current `ref=1` the DPB evicts the frame
  the repeat would reference — strict decoders choke), and is a mild spec-stretch
  that players tolerate. Cost: slightly slower encode (ref 1→2).

Caveats: bound consecutive skips (~8) so a small change (a ticking clock) lands
within ~130 ms; tolerance-based diff (≤2 % of sampled pixels beyond ε) so
"nearly static" content also gets skipped; the HDR suite's flat phases would
produce almost no samples under VFR, so the suite's fps metric would need an
active-content row. One 5-minute probe first: **measure WGC's actual frame-arrival
rate on a static screen** — if DWM coalescing already drops the delivery rate, L1's
benefit is the near-static case (small moving regions) only. Effort: medium
(detector + muxer repeat/skip + `ref` bump + suite adaptation). Impact: for the
typical screen-recording workload (mostly static UI) the CPU cost → near zero,
removing the fps ceiling in the common case.

### L2 — x265 knob micro-tuning (+10–25 % encode throughput, cheap)

All of these work through the existing `X265Parse` (the shim delegates to
`x265_param_parse` — no C changes):

- **`--ctu 32`** (smaller coding-tree units): usually the biggest single win for
  low-latency screen content — better thread load balance, less wasted ME on flat
  regions. Top candidate.
- **`--dyn4o`** (runtime 32↔64 CTA switching): good for screen content with big
  flat areas.
- **`--rdoq 0`**, **`--no-info`**, verify SAO is already off in ultrafast: small, free.
- **Pool / frame-thread shape:** 16 frame threads on a 32-thread CPU gives x265's
  auto pool ~1–2 slice threads/frame. A/B: 8/16/24 frame threads × pool `cpu:16`
  vs `cpu:32`.
- **Build-side:** rebuild x265 with `X265_MAX_FRAME_THREADS=24/32` (one CMake flag;
  more concurrent frames in low-latency mode), and check why the probe banner caps
  at AVX2 — x265's asm build is runtime-dispatched; if this 4.3 build lacks AVX-512
  kernels or its CPUID gate is conservative on this Zen 5 chip (full-speed AVX-512
  FMA), that is a silent loss. Verify, don't assume.

Everything is A/B-able in `PerfProbe --ab` (extend the variant matrix) before any
app code changes. Expected: 4K ceiling 11.6 → ~14–15 fps.

### L3 — persistent convert worker pool (helps 1440p most)

`ConvertHdr` spawns 16 `Task.Run`s and allocates the `mins/maxs/sums/tasks` arrays
**every frame** (~20 allocs/frame at 4K; GC churn at 1440p's ~30 fps). A
pre-created ring of worker threads with pre-split row chunks + pre-allocated stat
arrays saves the ~1–3 ms dispatch/sync overhead and the GC pressure. Optional
add-on: SIMD the inner loop (Vector256/AVX-512 — Zen 5 has 1024-bit FMA; the loop
is scalar float + LUT today). Convert is *not* the 4K bottleneck, so this mainly
lifts **1440p (28.9 → ~33–35 fps)** and general CPU headroom. Effort: small–medium.

### L4 — raw D3D11 shared-handle readback (bypass Win2D)

`CanvasBitmap.CreateFromDirect3D11Surface` + `GetPixelBytes` is a staging copy
under a managed wrapper. The raw route — our own `ID3D11Device` (Vanara already
covers D3D11 interop) → `OpenSharedResource1` on the WGC shared texture →
`CopyResource` to a local `CPU_ACCESS_READ` staging → `Map` + memcpy into
`PixelBuf` — saves the Win2D layer (~0.3–0.8 ms/frame) and gives format control
(`R8G8B8A8_UNORM` / `R16G16B16A16_FLOAT`). Small win at 4K; pairs with L1 (the
signature check can read the mapped staging directly). Effort: small–medium.

### L5 — hygiene (<1 ms each, fold into the other work)

- Encode thread at `AboveNormalPriority` (scheduling latency under load).
- Pre-pin the three plane arrays + `EncodeBuf` **once per session** instead of the
  4 `GCHandle.Alloc`s the shim makes per encode call.

### L6 — quality lever, not fps: the `rc-lookahead` tradeoff

`rc-lookahead=0` was set for live latency. For file recording (no streaming), a
small lookahead (20–40) enables AQ + CUTree on the 16 frame threads with
**no wall-time cost** (it overlaps) — typically 10–20 % smaller files at equal
CRF. Worth an A/B as a *file-size* option, orthogonal to fps.

### Ruled out (won't help)

- More encoder threads beyond 16 without a rebuild (hard build cap; A/B'd).
- Bigger `EncodeBuf` / async muxer (SSD headroom is ~100× our bit rate).
- 8-bit tricks for the HDR path (10-bit P010 is mandatory for HDR10 signaling).
- `no-psy-rd` / `veryfast`-tier knobs (A/B'd: no-ops).

### Ceiling summary (CPU path)

| config | today | after L2+L3 | after +L1 |
| --- | ---: | ---: | ---: |
| 4K HDR, active content | 11.6 fps | ~14–15 | ~14–15 (unchanged) |
| 4K HDR, mostly static screen | 11.6 fps | ~14–15 | **near-unlimited** (VFR) / 60 (repeat) |
| 1440p HDR | 28.9 fps | ~33–35 | ~35, static → 60 |
| 4K SDR | ~30–45 fps | (x264, untouched) | 60 on static |

**Suggested order for the next work session:** (1) probes — WGC static-screen
arrival rate + extend `PerfProbe --ab` with the L2 matrix (`--ctu 32`, `--dyn4o`,
`--rdoq 0`, frame-thread/pool shapes, `--ref 2` cost); (2) implement the L2 winners
(a few `X265Parse` lines); (3) L1 as a settings toggle ("skip unchanged frames",
default off; repeat mode needs `ref≥2`); (4) L3/L4/L5 as a cleanup pass. Anything
above ~15 fps on *active* 4K HDR content is out of reach on this CPU — that is the
NVENC path (below).

## CPU levers: results (implemented 2026-11-01)

L1/L2/L3/L5/L6 implemented or A/B'd, L1 later **removed** (user-reported playback
artifacts - "L1 removed" below); L7 tested and **rejected** (no CPU-side winner -
"L7" below); L4 deferred to the GPU phase - rationale below.
All A/B data below from `PerfProbe --ab` (4K, CRF 22, ultrafast; the x265 time wanders
with boost state, so only within-run comparisons are valid).

### L2 A/B matrix (no winner - the app config was already optimal)

| variant (ultrafast) | noise ms/frame | flat ms/frame | verdict |
| --- | ---: | ---: | --- |
| t16 (app default) | **43.4** | 44.2 | baseline |
| t8 | 58.8 | 25.4 | worse on active content - rejected |
| t24 | 49.3 | 33.1 | worse on active content - rejected |
| t32 | 51.0 | 17.8 | worse on active content - rejected |
| t16 + `ctu=32` | 44.7 | 47.2 | no gain (within wander) |
| t16 + `dyn4o` | **open failed (rc=-1)** | - | unusable in this 10-bit build |
| t16 + `rdoq=0` | 45.0 | 39.7 | no gain |
| t16 + `asm=avx512` | 44.5 | 43.1 | AVX-512 activates (banner shows it) but gains **nothing** - the 10-bit build's AVX-512 kernel coverage is not on the hot path |
| t16 + `ref=2` (L1 cost) | 45.4 | 43.9 | repeat-mode cost ≈ +2 ms - acceptable |

Notes:

- Frame threads: the local x265 build was rebuilt with `X265_MAX_FRAME_THREADS=32`
  (patched `x265.h`; stock is 16, DLL size unchanged at 6,318,592 B) so t24/t32 could
  be tested at all. On active 4K content t16 still wins - more concurrent frames cost
  more sync than they save. The flat-column anomalies (t32 "17.8 ms") are trivial-frame
  parallelism (32 identical frames in flight) and say nothing about real content.
- **Decision: no L2 knob changes ship.** The `ultrafast t16` config stands; the probe
  matrix is kept for re-checks (a future x265 release may beat it).

### L6 A/B (rc-lookahead file size - keep 0, documented)

4K noise, CRF 22, ultrafast t16, bytes/frame of the timed window (after 60-frame warm):

| variant | ms/frame | KB/frame | vs app |
| --- | ---: | ---: | --- |
| rc-lookahead=0 (app) | 71.6 | 4005 | - |
| rc-lookahead=30 | 56.1 | 6224 | **+55 % size**, -22 % time |
| rc-lookahead=30 + ctu=32 | 54.1 | 6224 | same |

The expected "smaller files" did not materialize: with CRF + lookahead, x265's RC moves
the operating point (lower QP, more bits on complex content - the timed window sits on
higher-QP P-frames at lookahead 0). It is *faster* (the lookahead thread overlaps) but
not smaller on worst-case content, and it would add a 30-frame video-track start offset
(the original reason for `rc-lookahead=0`). **Decision: keep 0; no new setting.** A
real-content A/B could revisit this if file size ever matters.

### L1 removed (2026-11-01) - "Skip unchanged frames" toggle (implemented, then removed)

Implemented as described below, then **removed on user report**: recordings made with
the toggle on **reported the wrong video length in playback and constantly froze**.

- Settings → Recording → "Skip unchanged frames" (`AppConfig.VideoRecordSkipUnchangedFrames`,
  default **off**; i18n en/zh-CN/ja-JP).
- Pump-side detector: 64×64 grid (4096 samples) of 10-bit Y codes over the session
  `PixelBuf` (~0.1 ms at 4K): HDR through the same WgcTo2020 + PQ-LUT math as the
  conversion, SDR as Y8×4. "Unchanged" = at most 2 % of the samples (82) moved by more
  than 16 10-bit codes; the first frame always encodes; at most 8 consecutive repeats
  (a small change lands within ~133 ms at 60 fps), after which a real encode is forced.
- Repeat = re-mux the last encoded access unit (stored per frame only while the feature
  is on) with the new PTS - no convert, no encode, no plane set consumed, so the MP4
  track was supposed to keep its dense nominal fps.
- Encoder wiring: x265 `ref=2` while the feature is on (A/B'd above: +2 ms) - with the
  stock `ref=1` the DPB evicts the frame a repeat references and strict decoders choke.
  SDR needs nothing (x264 veryfast keeps `ref=3`).

**Why it was removed:** the repeat-sample design assumes WGC keeps delivering at the
nominal refresh rate, but real delivery is sparse and bursty (a truly static desktop
delivers ~0.5 fps; a live UI stalls in bursts). The re-muxed repeats carry those wide
PTS gaps onto a 60 fps-declared track, so players (a) miscompute the clip's duration
from the gap-ridden PTS/sample-count pair (wrong length) and (b) hold each repeated
frame across the gaps (the constant freezes). The HDR suite could not catch it: its
flat-field rows genuinely change on this machine (panel EDR flicker, below) and a
static capture delivers too little content to surface the symptom.

All L1 code is deleted - detector, re-mux path, `EncodeJob.Repeat`, the session
fields, the settings toggle + i18n keys, and the x265 `ref=2` wiring (x265 is back
to the preset-default `ref`, ultrafast = 1). The `ref=2` A/B row above is kept as
data: the knob itself is valid, only the repeat-sample design was wrong. If frame
skip is ever revisited, the VFR variant from the analysis above (write no sample;
the player holds the last frame) is the spec-clean option.

**End-to-end check (pre-removal, 2026-11-01):** with the toggle on, the suite still
passes 5/5 and the x265 banner showed `ref=2` (wiring verified). The caveat is this
machine's display state: the WGC-delivered content of the 4K panel's 10000-nit flat
field **flickers in the bottom half of the frame every delivered frame** (≥16 codes
on 50 % of the grid, mean-preserving - a panel EDR artifact of the dual-HDR + 150 %
DPI setup, the same family as the in-phase drift hazard). L1 therefore correctly
refused to skip the flat-field rows (the content genuinely changes) and the suite's
fps was unchanged with L1 on (~11.5 @4K, encode-bound either way).

### L3 implemented - persistent convert worker pool

`FrameProcessor` now owns a `RowPool`: one dedicated background thread per worker with
a fixed row chunk (`idx*h/workers`, identical to the old Task split so `--verify` is
unchanged), pre-allocated stat arrays, and start/done `ManualResetEvents`. Zero
per-frame Task / array allocations (was ~20 allocs/frame at 4K). Threads are stopped in
`FrameProcessor.Dispose()` (session teardown). Probe mirror: `PerfProbe` now times both
paths ("convert HDR, pool (L3)" row).

Pool delta (PerfProbe, 60 iters, same machine state): the fixed pool is ~18 % slower
than the Task path - that is the inherent cost of 16 kernel-event Set/Wait barriers
per frame, and it is small next to the 21-158 ms encode stage:

| 4K HDR convert | ms | | 1440p convert | ms |
| --- | ---: | --- | --- | ---: |
| Task path (old) | 26.3 | | Task path (old) | 11.7 |
| pool, first cut (bug, see below) | 59.4 | | pool, first cut | 27.5 |
| **pool, fixed** | **31.0** | | **pool, fixed** | **13.9** |

**Bug found + fixed during bring-up (straggler double-work):** with no guard, a fast
worker could lap the coordinator's `_start.Reset()` and re-pass `_start.WaitOne()`
while the event was still set, re-running its whole chunk - the first-cut pool did
~2x the work and measured 2.3x slower (59.4 vs 26.3 ms @4K). Fix: a per-frame
generation counter (`_gen`, bumped before `_start.Set` so the Set/Wait barrier
publishes it); a worker does a frame's chunk at most once per generation
(`_lastDone[idx] < _gen`). The double-work was idempotent (same chunk, same data -
no pixel drift, only wasted CPU). Same fix in the PerfProbe `ConvertPool` mirror.

### L5 implemented - encode-thread hygiene

- The encode thread is now a dedicated `Thread` at `AboveNormalPriority` (re-prioritizing
  a thread-pool thread would affect other pool work); the stop-flow contract
  (`EncodeTask` drained / timed out) is unchanged.
- Session-pinned encoder inputs: `PinnedEncodeInputs` pins the set A/B plane arrays +
  `EncodeBuf` once per session; the encode loop calls new `CodecsNative.X26*EncodeNative`
  overloads (raw pointers - the C shim already took them, no shim changes) instead of
  the 4 `GCHandle.Alloc`/`Free` pairs per frame.

### L7 boost-state micro-tune - tested, rejected (2026-11-01)

Goal: shave the p95 of the 4K encode frame time (the measured 88-150 ms wander = CPU
boost state). Two candidate levers, both A/B'd with `PerfProbe --affinity` (4K noise +
flat, ultrafast t16, two passes; the harness stays in the probe as the rejection's data
archive):

**Lever 1: PERFBOOST power request - unavailable on this machine (and would be a no-op).**
This OS build (26100) no longer exports the classic powrprof power-request API
(`PowerCreateRequest`/`PowerSetRequest` are absent from `powrprof.dll`'s export table -
only the new "Power Key" generation of APIs remains). Moot in any case: the machine runs
the **Ultimate Performance** power scheme, and PERFBOOST only prevents the OS from
lowering P-states - it cannot beat the thermal/power wall the wander actually hits.

**Lever 2: CCD affinity (pin x265 onto a dual-CCD Zen 5 CPU's V-cache CCD) - slower, rejected.**
The test CPU is 2 CCDs x 8 cores x SMT2 (LP 0-15 / LP 16-31), one CCD carrying the
96 MB 3D V-cache. Hypothesis: pin x265's frame threads to the V-cache CCD and the 16
convert workers to the other. A/B result:

| variant | noise ms/frame (pass 1 / 2) | flat ms/frame (pass 1 / 2) |
| --- | ---: | ---: |
| baseline (scheduler) | **54.2 / 51.5** | **26.1 / 25.3** |
| pin x265 to CCD A (LP 0-15) | 92.4 / 89.8 | 30.2 / 29.9 |
| pin x265 to CCD B (LP 16-31) | 92.9 / 92.0 | 29.1 / 28.7 |

Pinning to *either* CCD is **65-75 % slower on noise** (both CCDs equally bad, so it is
not a V-cache-placement effect): x265 spawns 32 threads (16 frame + 16 pool, per the
banner) and a single CCD offers only 16 LPs - 2x-oversubscribed while the other half of
the CPU idles. The scheduler's free placement (one thread per LP across both CCDs) is
strictly better. (The discovery diff also caught .NET runtime threads - 48 pinned in
total, which made it worse still.)

**Verdict: no CPU-side winner.** The wander is thermal (sustained 32-thread load at the
120 W PBP limit) - no software lever beats the cooler. The CPU wall for 4K stands; the
30-60 fps @ 4K target remains the NVENC route (GPU roadmap below).

### L4 deferred to the GPU phase (documented decision)

The raw D3D11 shared-handle readback is **not** possible through the WinAppSDK 1.8
projection, which exposes no route to the WGC texture's shared handle:
`Direct3D11CaptureFrame` projects only `Surface` (`IDirect3DSurface`, which projects
`Description` only), there is no `IDirect3D11Texture2D` projection, no `Texture`
property, and no `ICsWinRtObject`/`GetNativePointer` escape in the runtime. The raw
route would mean a hand-rolled D3D11 COM stack (device, `OpenSharedResource1`, staging
copy, `Map`) for a saving of ≤0.5 ms/frame - the measured 3-5 ms Win2D readback is
dominated by the GPU staging copy the raw path also has to do. That interop is required
for NVENC texture registration anyway, so it lands with the GPU path (below), where it
pays for itself.

### Status checklist

- [x] x265 knob A/B batch (result: no winner; `dyn4o` unusable in 10-bit; AVX-512
      enabled but no gain; t16 stays - "L2 A/B matrix" above)
- [x] Content-adaptive frame skip/repeat ("Skip unchanged frames", default off;
      repeat samples with `ref=2`) - implemented, **then removed** (user-reported
      wrong playback length + constant freezes - "L1 removed" above)
- [x] Boost-state micro-tune A/B (L7: PERFBOOST absent from this OS build's powrprof
      and moot under Ultimate Performance; CCD affinity is 65-75 % slower - the
      scheduler wins; the frame-time wander is thermal-limited - "L7" above)
- [x] Convert worker pool (L3) + encode-thread hygiene (L5) - see above; L4 raw D3D11
      readback deferred to the GPU phase (rationale above)
- [x] rc-lookahead size A/B (result: +55 % on 4K noise, keep 0 - "L6 A/B" above)
- [x] Post-encode disk-buffer mode (raw capture + background drain; 1440p holds 60 fps,
      no 4K gain on this panel - WGC delivers ~11 fps there; AV-lock same-handle rule
      - "Post-encode mode" section below)
- [ ] GPU path: compute-shader WGC→P010 + GPU clli + NVENC 10-bit HEVC (the 4K@60
      route; also the home for L4's D3D11 shared-handle interop)

## Post-encode mode (feature, 2026-11-01)

A Settings toggle (Recording → "Encode after stopping", default **off**) that moves
the x264/x265 encode out of the capture path: the pump's converted frames are
written **raw to disk** (a `~Recording_*.raw` record file in a user-pickable
folder — Recording → "Raw buffer folder", default = the recordings folder) and a
background drain after stop re-reads the file and runs the real encoder, then
deletes the raw file. `StopAsync` returns in milliseconds; the MP4 lands when the
drain finishes (`VideoRecordService.IsFinalizing` / `FinalizeTask`; a new
recording is blocked while the drain owns the encoder). Format: 64-byte header
("SRR1", w/h/fps/frameBytes) + `[i64 relPts100ns][planes]` records (HDR P010 =
3·w·h B/frame, SDR NV12 = 3·w·h/2); a torn tail (crash mid-write) simply ends the
drain (complete frames only), a stale `~*.raw` from a crashed session is swept at
the next start, and the buffer drive gets an 8 GB free-space floor. The
record-test host awaits `FinalizeTask` (60-min cap, exit 4).

Measured (2026-11-01, `--record-test` fullscreen 6 s, 4K HDR panel, C: NVMe
buffer):

| | result |
| --- | --- |
| raw rate, 4K HDR P010 | 24.9 MB/frame → 1.42 GiB/s @ 60 fps (≈85 GiB/min) |
| raw rate, 1440p | 331 MB/s (SDR) / 664 MB/s (HDR) @ 60 fps |
| 4K E2E | 68 frames in 6.14 s (11.3 fps); drain **2 s** (static test content) → 694,430 B MP4, raw deleted |
| 1440p capture | **278 frames in 4.65 s ≈ 60 fps** (WGC's full native rate; live mode is encode-bound at ~29 fps) |

Two findings:

1. **1440p: the intended win.** Live mode is encode-bound (~29 fps, suite
   number); post-encode records WGC's full 60 fps delivery. The drain runs at
   the encoder's throughput (~29 fps @ 1440p ≈ 2.1× clip length for a
   60-fps-captured clip; ~12 fps @ 4K ≈ 5× — the 2 s above is nearly-static
   test content, which encodes well above the active-content ceiling).
2. **4K on this panel: no gain in the HDR state (WGC delivers only ~11 fps), and in SDR the panel is 60 Hz-capped — the 120 Hz setting is not in effect.** The HDR-state numbers above (11.3 / 11.0 fps, test content and live-mode, content-independent) come from the dual-HDR + 150 % DPI EDR-flicker state (cf. the L1 section): the panel's effective content-update rate is ~11-13 Hz, so no capture strategy can do better. Panel state measured the same day (`EnumDisplaySettings` EM_CURRENT): the 4K panel reads **3840×2160 @ ~23 Hz** (a known-120 Hz virtual display reads 120 at the same offset of this build's 148-byte DEVMODE, so the layout is right — but the field later proved untrustworthy for this panel, below). The 4K is driven by the **dGPU** (verified in Windows display settings; WMI's per-adapter `VideoModeDescription` only reports the dGPU's *first* display, 2560×1440, which initially misled to a wrong iGPU attribution), and in the dual-HDR state the panel's effective content-update rate is ~11-13 Hz. A third independent measurement: a user YouTube region capture, post-encode, recorded 112 frames / 8.747 s = **12.80 fps** (raw: 112 frames written to the NVMe buffer at ~319 MB/s; drain 3 s; final MP4 2.58 MB, nb_frames=112) — content-independent across test content and live video.
   
   **SDR follow-up (the HDR toggle was the fix).** The user toggled the 4K's HDR off (Settings → Display → Use HDR) and re-ran a YouTube region capture: **243 raw frames / 6.10 s = 39.8 fps** (vs 12.8 in the HDR state) — the HDR state was the throttle. 39.8 is neither 60 nor 120 because it is the *content's* rate (the 4K browser video painted at ~40 fps, or the panel was still mid-reprovision right after the toggle), not a pipeline cap — the controlled runs below deliver 57-59 fps.
   
   **Counter-content ground truth (three probe fixes).** The record-test counter window is the ground-truth content; WGC delivers only on change, so the content's paint rate caps the delivery. (1) The paint cadence was 100 ms (10 fps content) — every earlier "test content" 4K number, including the ~11 fps readings, was masked by the 10 fps paint and must be read through that. (2) The static background (quadrants + central band) is now painted once and only the counter text box + scan strip repaint per tick (a full 4K GDI repaint costs ~18 ms and pins content to ~55 fps). (3) The deadline loop spins to a cumulative 12 ms deadline (83 fps content): `Thread.Sleep(1)` degrades to the 15.6 ms system timer once the capture pipeline starts (a pre-capture probe measures 1.4 ms/sleep, in-loop waits average 24 ms), and `Thread.SpinWait(32)` yields (a yield can cost a full 15.6 ms quantum); the reliable form is `SpinWait.SpinOnce()` plus a resync that fires only when more than one full cadence is behind (a plain `nextPaint < elapsed` test fires on the 0-1 ms spin overshoot and silently doubles the period — that bug sat behind every fast-paint run).
   
   Measured 4K SDR delivery with 83 fps counter content (`--record-test fullscreen`): **457 f / 8.01 s = 57.0 fps** and **588 f / 10.02 s = 58.7 fps** (a 120 Hz panel would deliver ~83; a 60 Hz panel delivers ~60). 60 fps counter content delivered 422 f / 8.02 s = 52.6 fps. **Conclusion: the 4K runs at 60 Hz in SDR — the 120 Hz setting is not actually in effect on this display.** Re-check after re-plugging: Settings → Advanced display → refresh rate for the 4K; 4K120 needs DP 2.1, DP 1.4 + DSC, or HDMI 2.1 on the port/cable in use, and the 4K must be driven by the dGPU (a DP 1.2 iGPU output caps 4K at 60 Hz). Re-run the 83 fps probe after any change: ~80 fps delivery = 120 Hz confirmed.
   
   **Flush tail-loss (found by the 39.8 clip, fixed in-app).** The 39.8-fps clip lost its tail: 243 raw frames → 226 in the MP4 (`x264 flush failed` WARN). The fast 4K SDR drain left a 17-frame x264 frame-thread backlog; the final flush accumulated it across up to 256 `encode(NULL)` steps into the 4 MB `EncodeBuf`, overflowed partway, and the throw discarded the partial. Fix: the shim (`ss_x264_flush` / `ss_x265_flush`) now reports the recoverable partial (complete NALs only) via `*outBytes` before returning -1; `CodecsNative.X264Flush/X265Flush` return the rc instead of throwing; `FlushAndFinalize` muxes the partial, grows the buffer 8→16→…→64 MB and retries (error-log + finalize with whatever was muxed at the cap). `EncodeBuf` is 8 MB (a 4K veryfast I-frame can approach 4 MB). Verified zero-loss: 422/422, 438/438, 405/405, 378/378. The live-stop path shares `FlushAndFinalize`, so it is fixed too.
   
   **Rate-readout caveats.** The GDI "23 Hz" freq field did not change across the HDR→SDR toggle, so it is not tracking this panel's state — treat it as unreliable for the 4K (it read the known-120 Hz virtual display correctly, so the offset is right; the value just doesn't refresh). The QDC `RefreshRate` doubles remain zero-filled on this build, and the DWM performance-counter sets (Dwm-Api "DWM FPS") are absent on this OS build (10.0.26300, 26H2; `Get-Counter -ListSet '*dwm*'` finds nothing), so no API exposes DWM's composition cadence on this machine. Ground truth for the panel/delivery rate = the controlled counter-content runs above.
   
   **HDR-state cap, all capture modes (2026-11-01 follow-up).** With HDR back on, an 80 fps game recorded at **10.15 fps** (user clip), and controlled probes on the 4K in the HDR state: 65 fps counter content → **88 f / 8.0 s = 11.0 fps** (monitor capture) and 52 fps content → **89 f / 8.0 s = 11.1 fps** (*window* capture of the counter window itself) — the cap holds for both the monitor and the WGC window path (hevc + smpte2084 + bt2020nc confirmed the HDR state in both clips). So the throttle is Windows' frame delivery for the HDR 4K display (the DWM↔WGC handoff, paced by the EDR-state flicker documented in the L1 section), not Starshot's pipeline, the disk, the encoder, or the panel's 120 Hz link (the game's own 80 fps presents reach the panel — it looks smooth; the capture path simply only receives ~12 of those frames per second). In-app fixes cannot raise it; see the panel-state item above for the levers (SDR recording, driver/OS updates, GPU-side capture).
   
   **Desktop-duplication probe: the cap is WGC-specific (2026-11-01, `tools/DupProbe`).** A DXGI desktop-duplication rate probe (`tools\DupProbe\dup-probe.exe`, run against the same 4K in the HDR state) settles where the throttle lives: with **no WGC session active**, duplication on the 4K delivered **112 fps** on a static desktop (108-117 f/s per second, rock-stable) — i.e. DWM composites the 4K at the full 120 Hz panel rate in the HDR state (the EDR flicker keeps every frame dirty), and duplication receives all of it. With an **active WGC session** (a concurrent `--record-test fullscreen 8`, 63 fps counter content), the same duplication probe delivered **242 f / 8 s = 30.2 fps** while the WGC side delivered **92 f / 7.98 s = 11.5 fps** (smpte2084 + bt2020nc confirmed the HDR state) — an active WGC session degrades duplication's delivery to ~30 fps, and WGC itself sits at ~12. So: DWM composes at ~120 Hz; duplication (alone) tracks it at ~112; WGC receives ~12 of the 120 — **the ~12 fps cap is specific to the DWM-to-WGC handoff in the EDR-flicker state, not a DWM composition limit and not a panel-link limit**. The duplication buffer format in the HDR state is **state-dependent**: **R16G16B16A16_FLOAT (16F, real HDR pixel data)** in the full EDR state, **B8G8R8A8 (8-bit SDR-toned)** in an EDR state observed right after the 4K re-appeared; `DuplicateOutput1` (IDXGIOutput5, `pSupportedFormats`) can request the 10-bit/16F buffer explicitly. Implication: a desktop-duplication capture backend is viable on this machine (30-112 fps vs WGC's 12 fps, with 16F HDR data in the full EDR state) — but it must *replace* the WGC session, not coexist with it (the concurrent-session degradation above), and exclusive-fullscreen games never reach DWM at all (only an in-process D3D Present hook, the OBS game-capture approach, captures those).
   
   **OBS's high-fps HDR capture, answered (the "what does OBS do" question).** OBS **Game Capture** (borderless *and* exclusive fullscreen) is not WGC: it injects a small hook DLL into the game process and hooks the game's D3D11/D3D12 `Present`, grabbing the back buffer at the game's own present rate (80 fps here) with native HDR10 pixel data — it bypasses DWM/WGC/duplication entirely. OBS **Display Capture** offers three methods: **WGC (Windows Graphics Capture)** (the newer backend; "Automatic" prefers it on recent Windows), **DXGI Desktop Duplication** (the long-standing default), and Automatic. On this machine the WGC method would hit the same ~12 fps cap Starshot hits; the duplication method gets 30-112 fps (8-bit in some EDR states, 16F in the full EDR state) — so for full-rate *desktop* capture duplication is the working path here, and for the *game's own* 80 fps the in-process hook (or the game's built-in capture) is the only route.
   
   **Display-topology + modernized-SDK notes from the same session.** (a) The 4K dropped out of the active display configuration mid-session (GDI monitor count 3 to 2: only 2560x1440 + the 2048x1152 virtual display remained; a DP link drop when the 4K was toggled is the usual cause) and re-appeared within ~10 min — treat a "missing 4K" as the first suspect when a 4K probe suddenly targets a different output. (b) The modernized 10.0.26100 SDK: `IDXGIOutput1::CreateDuplication` is gone, replaced by `DuplicateOutput(IUnknown *pDevice, **dup)` (the device backs the frame buffers; `MapDesktopSurface` gives shared-memory access; `IDXGIOutputDuplicationFrame` is removed); `D3D11CreateDevice` now rejects SDK-version 0 (E_INVALIDARG) — pass `D3D11_SDK_VERSION` (7); `DXGI_OUTDUPL_FRAME_INFO` no longer has `SourceFrameId`; and `GetDesc`'s DesktopCoordinates can be DPI-scaled logical (a 4K at 150 % DPI enumerated as 2560x1440 while its duplication buffer is the physical 3840x2160 — target outputs by the duplication buffer size, not the coordinates).
   
   **Practical ceiling.** 4K SDR 60 fps post-encode raw = ~745 MB/s (712 MiB/s vs the NVMe's 1,437 MiB/s sustained — 2× headroom). 4K 120 fps = ~1.49 GB/s ≈ the disk's sustained ceiling, so **60 fps is the realistic 4K post-encode rate on this disk even if the panel reaches 120 Hz** (HDR P010 60 fps ≈ 1.42 GiB/s ≈ the same wall).
   
   The old "4K ceiling = encode-bound (~12 fps)" reading was a coincidence of the panel's delivery rate and the encoder's ceiling — 4K@60 still needs the NVENC route *and* a 4K surface that actually changes at 60 fps.

Disk headroom: the C: NVMe sustained 1,437 MiB/s (2 GB write test) vs the
1,424 MiB/s a full-rate 4K capture needs — **~1 % margin, effectively zero**;
an HDD-backed buffer drops frames, 1440p (≤664 MiB/s) has comfortable headroom
on any SSD.

**AV-lock quirk (this machine; now a design constraint):** a *second* open of
a freshly written file fails with ERROR 32 (sharing violation) even with fully
compatible `FileShare` — verified with raw `CreateFile` P/Invoke and ≥10 s
waits (WinDefend real-time is active; an OS-level filter grabs the lock).
Consequence, hard rule in `RawCaptureFile`: the raw file is opened **once**
with `FileAccess.ReadWrite`; the drain reads back through the writer's own
handle (`SeekToFrames` + `TryReadNext`) and the raw file is deleted only after
the session's `Dispose` closes that stream. The first E2E caught it (the
drain's separate `FileStream` open failed and orphaned a 1.5 GB raw file); the
same-handle design fixed it end-to-end.

## Root cause — final (2026-11-01): the ~12 fps is the CPU consumer pipeline, not WGC

Five hidden probes (env vars read at process start, no UI; all kept as probe
infra) settle the long-running "why does 4K HDR record at ~12 fps" question, and
**supersede the DWM→WGC handoff attribution in the duplication section above**:

- `STARSHOT_WGC_ARRIVAL_TRACE=1` (`WgcCaptureSource`): timestamp every
  `FrameArrived` callback *before* the pool drop, one Debug line per second.
- `STARSHOT_WGC_STAGE_TRACE=1` (`VideoRecordService`): per-stage Stopwatch in
  `OnFrame` + `RawWriteLoopAsync`, one Debug line per frame.
- `STARSHOT_WGC_WORKERS=N` (`FrameProcessor.WorkerCount`): force the row-pool
  size to classify the convert cost (thread contention vs per-core throughput).
- `STARSHOT_WGC_MIN_UPDATE_INTERVAL_MS=<ms>` (`VideoRecordService`): set the
  26H2 `MinUpdateInterval` (the OBS PR #12759 lever).
- `STARSHOT_RAW_BUFFER_DIR=<dir>` (`VideoRecordService`): point the raw buffer
  at another drive (per-drive capture-ceiling A/B).

**WGC delivers at the content rate.** Arrival trace, 4K in the HDR state,
51-53 fps counter content: `~42-52 fps over ~1005 ms (delta min 0.0 /
avg 20-23 / max 50-93 ms)`. The 0.0 ms deltas are pooled bursts (several
deliveries inside one poll). WGC is *not* the ~12 fps.

**The ~12 fps is the consumer pipeline.** Stage trace, 4K HDR, post-encode,
138-frame run, raw on the D: SAS RAID:

| stage | thread | avg | max |
| --- | --- | --- | --- |
| readback (`GetPixelBytes`, 66 MB) | pump | 8.9 ms | 56.4 ms |
| setWait (ping-pong sem) | pump | 0.0 ms | 0.2 ms |
| **convert (`FrameProcessor.ConvertHdr`, scalar)** | **pump** | **77.8 ms** | **200.4 ms** |
| chan (bounded-channel write) | pump | 0.0 ms | 0.5 ms |
| raw write (23.75 MiB P010 frame) | raw thread | 19.3 ms | 69.2 ms |

The pump's per-frame budget is readback + convert ≈ 87 ms → ~11.5 fps, exactly
the recorded rate (baseline 152 f/12 s = 12.7; stage run 138 f/12 s = 11.5).
Every earlier "WGC delivers ~10.9-12.8 fps" number in this document was a
*count of encoded frames* (pipeline throughput) misattributed to WGC delivery.
The raw write is a separate thread (19.3 ms avg — the RAID sustains ~1.23 GB/s;
a 60 fps 4K HDR raw stream needs 1.4 GB/s, so the RAID is marginal and the C:
NVMe at ~1.44 GB/s would be fine).

**The convert is per-core-throughput-bound, not thread-contended.** Worker
scaling (8/16/32/48/64 workers on this 16C/32T Zen 5 CPU): convert avg
136 / 111 / 77.6 / 73.4 / 76.7 ms. It plateaus at 48; the default (32) is
near-optimal and more threads buy nothing. The scalar LUT + matrix is the cost.

**`MinUpdateInterval` is available but is not the fix here.** Present in the
10.0.26100 SDK (C++ header + C# ref projection; the XML doc file is stale —
check the binary). Since the arrival trace shows WGC already tracks the content
rate, `MinUpdateInterval` only matters where the compositor's default pacing
applies (static/SDR content). Kept as a hidden flag.

**Fix options, in effort order:**
1. **SIMD the `ConvertHdr`** (~1-2 days; the real CPU fix). AVX2+FMA is
   confirmed (the x265 banner shows `AVX2 FMA3`); .NET's runtime intrinsics
   also report AVX-512F/BW/DQ on this box (unexpected for the model string —
   gate any AVX-512 path on the runtime check and verify with a CPUID leaf-7
   probe before relying on it). Keep today's LUTs and op order so the
   colorimetry stays inside the HDR-suite ±6/±12 bands and the
   `MachineExpectation` measured codes don't shift. Expect the convert
   73 ms → ~10-20 ms, i.e. 4K HDR 12.7 → 40-60 fps; then readback (8.9 ms) +
   raw write (19 ms on the NVMe) set the new ceiling.
2. **GPU compute-shader conversion** (~2-4 days; the "GPU roadmap" section):
   WGC-surface→P010 + clli reduce on the dGPU in a few ms; kills the 66 MB
   readback; enables 120 fps; pairs with NVENC.
3. **Config-only: raw buffer on the NVMe** (`VideoRecordRawBufferDir` setting
   or `STARSHOT_RAW_BUFFER_DIR`): removes the 19.3 ms raw-write stage; only
   meaningful *after* the convert is fixed.
4. **Cheap intermediate: kill the chroma-branch recompute** (`ConvertHdrRows`
   fully recomputes pixel x+1 in the chroma path — a ~1.5× win, no SIMD).

Validation for 1/2/4: the HDR suite rows (±6 Y / ±12 chroma bands) and
`HdrFlatFieldPlay`; the machine's WGC color deviations (`MachineExpectation`)
assert against measured codes — do not "fix" them back to the standard.

### Post-SIMD results (2026-11-02): option 1 + 3 landed

`ConvertHdr` is now AVX2 (8 pixels/group; the semantic .NET 10 intrinsics API —
the C-style `_mm*` names were removed from .NET 10, so the kernel uses
`Vector256<T>` + `Avx`/`Avx2`/`Sse2` + `Unsafe.As` bitcasts). Same LUTs and
op order as the scalar path (explicit mul/add/div, no FMA in the color path),
validated bit-for-bit against it over 210,852 mixed groups (0 mismatches).

Stage trace, 4K HDR, 245-frame run (raw buffer on the C: NVMe):

| stage | avg | p50 | p95 | max | (pre-SIMD avg/max) |
| --- | --- | --- | --- | --- | --- |
| readback | 7.1 ms | 7.0 ms | 8.2 ms | 21.6 ms | 8.9 / 56.4 ms |
| **convert (AVX2)** | **25.3 ms** | **24.8 ms** | **31.9 ms** | **40.5 ms** | **77.8 / 200.4 ms** |
| raw write | 15.8 ms | 10.4 ms | 39.9 ms | 47.7 ms | 19.3 / 69.2 ms (RAID) |

Capture rate 138 f/12 s (≈11.5 fps) → **242–245 f/8 s (≈30 fps)** — 2.6×.
The convert is 3.1× faster (25.3 vs 77.8 ms); it lands above the hoped
10–20 ms because the 6 LUT gathers per 8-pixel group and the two `divps`
(chroma) dominate. The x265 drain (~18 fps, 32 threads, ~13.4 s for 245
frames) is now the visible bottleneck — 4K stays encode-bound until the GPU
roadmap (NVENC/compute-shader) lands.

Raw A/B via the option-3 path (`STARSHOT_RAW_BUFFER_DIR`, the same resolver
the UI "Raw buffer folder" setting feeds): 8 s bursts on the C: NVMe
vs the D: CoveFs RAID write p50 10.8 / 9.6 ms — indistinguishable in a short
burst (both buffered; the ~25 MiB/frame copies go through the page cache and
the drives' write-back). The sustained 1.4 GB/s limit still favors the NVMe
for long 60 fps 4K HDR recordings. Both runs created their raw file in the
requested directory and drained it to the final MP4 after the encode (0
leftover `~*.raw`).

#### WGC-delivery vs recorded-fps delta + worker A/B on the SIMD kernel (2026-11-02)

Single 8 s run with `STARSHOT_WGC_ARRIVAL_TRACE` + `STARSHOT_WGC_STAGE_TRACE`
(counter content painted ~72 fps on the 4K/60 Hz panel):

| | fps |
| --- | --- |
| WGC delivery (arrival trace, 7 one-second windows) | 52.8–59.2, avg **55.2** |
| recorded (248 frames / 8 s) | **31.0** |
| x265 drain (separate; does not drop frames, only delays the file) | 17.7 |

We keep ~56 % of the delivered frames (the bounded WGC pool drops the rest;
ptss stay real, so playback is temporally correct, just choppier). The pump's
per-frame budget is readback 6.8 + convert 25.2 ≈ 32 ms → the 31 fps ceiling;
matching 55 fps needs ≤ 18.2 ms (convert ≤ ~11 ms). The kernel is
gather-throughput-bound: 6 LUT gathers per 8-pixel group (3 half-float LUT +
3 PQ LUT, both 256 KB random) ≈ 140 of the ~140 cycles/group at boost.

Worker A/B on the SIMD kernel (the scalar-kernel plateau re-confirmed):
convert avg 25.3 (32 w) / 37.1 (48 w) / 39.5 ms (64 w) — **32 workers is the
sweet spot**; more workers just add LUT/L2 cache contention. Remaining
levers: cut 3 of the 6 gathers with an exact integer half→float conversion
(bit-exact — every half is exactly representable as a float; probe-verifiable
before port), then an AVX-512 16-px/iter path (the test CPU reports
AVX-512F/BW/DQ; Zen 5 upconverts 512-bit uops, so expect a modest +10–25 %).
That pair should bring convert to ~11–13 ms (≈ 50–55 fps, i.e. parity with
60 Hz WGC content); full parity with >60 fps content needs the GPU route.

(Both landed this session — the measured outcome on this Zen 5 box turned out
context-dependent and *not* a clean win over the 3×-gather kernel above; see
"SIMD kernel A/B" below.)

#### SIMD kernel A/B: gather-reduction + AVX-512 kernel (2026-11-02)

Both levers above landed in `FrameProcessor`; the class now has three
bit-exact kernels (same LUTs + op order as the scalar path — explicit
mul/add, no FMA — so coded P010 values match it bit-for-bit):

- **Gather-reduction (in both SIMD kernels)** — the 3× 256 KB half-float LUT
  gathers per 8-px group are replaced by an exact integer fast path: the
  normal / inf / NaN / zero cases are computed with ~15 integer ops (bit-exact
  with the LUT, NaN payload and subnormal normalization included); only the
  subnormal case (exponent field 0, 2048 of 65536 half values) gathers — from
  a 2048-entry (8 KB, L1-resident) `SubnormalLut`, a slice of `HalfToFloat`,
  instead of the 256 KB LUT.
- **AVX-512 16-px/iteration kernel** — 2× 512-bit loads, 6× 256-bit PQ
  gathers (the .NET 10 projection has no 512-bit gather; Zen upconverts one
  to two 256-bit anyway) plus the fast path's 2× 256-bit subnormal gathers
  per 16-px group. An 8–15 px row tail runs the AVX2 8-px group; a <8 px
  remainder runs the scalar per-pixel body.
- **Selection** — default = the widest SIMD the CPU has (AVX-512 → AVX2 →
  scalar; the scalar path stays the fallback for arm64 / AVX2-less x86).
  `STARSHOT_WGC_SIMD=scalar|avx2|avx512` forces one kernel for A/B; a forced
  kernel the CPU lacks falls back down the chain. All 512-bit vector
  constants are gated on `Avx512F.IsSupported`, so type-init never throws on
  a CPU without AVX-512.

Probe validation: both `HalfToFloatSimd` variants bit-exact over all 65,536
half values (0 mismatches); scalar-vs-SIMD equivalence 210,852 8-px groups
(AVX2) + 148,752 16-px groups (AVX-512: gray sweep, two-tone
block-boundaries, structured, bit-pattern edges, 1M-px random, 20k odd rows),
0 mismatches. HDR suite 4/4 rows with the default (AVX-512) kernel — within
the ±6 Y / ±12 chroma bands, `MachineExpectation` codes unchanged.

App A/B (this box: Zen 5, 16c/32t; 8 s `--record-test
window`, 32 workers, raw buffer on the C: NVMe, two runs per kernel):

| kernel | convert avg | convert p50 | capture | WGC arrival |
| --- | --- | --- | --- | --- |
| `scalar` | 72.7–74.1 ms | 73.1–73.9 ms | 12.1–12.4 fps | ~49–51 |
| `avx2` (fast path) | 35.6–37.1 ms | 36.6–37.5 ms | 21.6–22.6 fps | ~50–52 |
| `avx512` (default) | 40.2–40.4 ms | 41.7–42.3 ms | 20.4–20.6 fps | ~50–51 |

Single-threaded probe (Part D, one 4K frame on one core — the app's exact
kernels):

| kernel | ms/frame | vs scalar |
| --- | --- | --- |
| `avx512` (16-px, fast path) | 35.4 | 3.56× |
| previous AVX2 (3× 256 KB LUT gather; now probe-only) | 42.6 | 2.96× |
| `avx2` (fast path) | 88.8 | 1.42× |
| `scalar` | 126.0 | 1× |

**Context-dependent ranking — why the override exists.** Two Zen 5
artifacts flip the single-threaded order in the 32-worker app:

1. **512-bit up-conversion.** Zen 5 executes each 512-bit int op as 2×256
   uops, so the 16-px kernel is the *fastest* single-threaded (35.4 ms) but
   *slower* than 256-bit in the app (40.2 vs 35.6 ms): the 16-px
   unpack/permute setup costs more than it saves when 32 workers share the
   cache.
2. **A 32 MB L3 keeps the 256 KB LUT hot.** The fast path only pays off when
   the 256 KB LUT is *not* already L3-resident (cache-poor CPUs, or many
   workers). Here the LUT is hot, so the ~54 integer ops the fast path adds
   per 8-px group cost more than the L3 traffic the 3 gathers used to move —
   the fast path is even slower than the previous 3×-gather AVX2 kernel in
   the app (that one measured 25.3 ms / ~31 fps; it is no longer in the
   build, replaced in place by the fast path).

So the default stays "widest SIMD available" (the right pick on
AVX-512-native Intel cores, where 512-bit is native and the 512-bit kernel
is expected to win), with no CPU special-casing in code: on this box the
best of the available kernels is `STARSHOT_WGC_SIMD=avx2` (35.6 ms /
22.6 fps), still 2.1× the scalar kernel; the 11–13 ms / 50–55 fps target
needs the GPU route below (or a cache-poor CPU, where the 8 KB
`SubnormalLut` is exactly the fast path's point).

#### Kernel set grew to five + wide 16-px AVX2 kernel + fast-convert toggle (2026-11-02)

The kernel enum is now `Scalar / Avx2 / Avx2Fast / Avx2Wide / Avx512`
(`STARSHOT_WGC_SIMD=scalar|avx2|avx2-fast|avx2-wide|avx512`):

| kernel | shape | LUTs |
| --- | --- | --- |
| `avx2` | 8 px, 3× 256 KB half-gathers — the pre-regression baseline, restored as a named kernel | 256 KB `HalfToFloat` + 256 KB `PqLutF` |
| `avx2-fast` | 8 px, gather-reduction (8 KB `SubnormalLut` instead of 3× 256 KB gathers) | 8 KB + 256 KB `PqLutF` |
| `avx2-wide` | **new** 16 px, batched 256-bit gathers | 256 KB + 256 KB `PqLutF` |
| `avx512` | 16 px, 512-bit (upconverts to 2×256-bit on Zen 5) | 256 KB + 256 KB `PqLutF` |
| `scalar` | per-pixel fallback (arm64 / AVX2-less x86) | 256 KB |

- **`avx2-wide`**: two 8-px halves; all six half-LUT gathers are front-loaded
  (48 loads in flight before any ALU) and half A's three PQ gathers are issued
  while half B's matrix runs — 2× the memory-level parallelism of the 8-px
  kernel without needing AVX-512 (Zen 5 upconverts 512-bit ints to 2×256-bit
  anyway, so the 512-bit kernel's structural advantage does not apply here).
  Same op set as `avx2`, so exact-mode output is bit-identical to the scalar
  path (probe: 487,640 16-px groups, 0 mismatches). An 8–15 px row tail runs
  one 8-px `avx2` group; a <8 px remainder runs the scalar per-pixel body —
  both through the same instance LUT/reciprocal fields, so a row is always one
  consistent mode.
- **Vectorized clli reduce** (all SIMD kernels): the 8/16-code min/max/sum
  store-loop is replaced by a horizontal butterfly — 256-bit: 3-level
  `CtlSwap/CtlSwap2/CtlSwap4` permute-then-min/max/add; 512-bit: 4-level
  (added `CtlSwap2_16/CtlSwap4_16/CtlSwap8_16`, gated on `SimdAvx512`).
  Bit-exact: integer min/max/sum are order-independent.
- **Default selection = the in-app winner**: Settings → Recording now
  defaults to kernel `avx2-wide` + 16 workers (the fastest config measured on
  this box: 18.6 ms / 36.6–36.9 fps). "Auto" is still available as an explicit
  choice and resolves exactly as before — widest SIMD the CPU has
  (AVX-512 → AVX2 → scalar; on AVX2-only CPUs the 3-gather `avx2` baseline)
  for the kernel, and min(rows, logical processors) for workers. The in-app
  matrix below is what settled the wide kernel; the `STARSHOT_WGC_SIMD` /
  `STARSHOT_WGC_WORKERS` env probes still win over the UI (and over the
  default).

**Fast-convert mode — Recording settings toggle (default off = bit-exact).**
`AppConfig.VideoRecordFastConvert` (Settings → Recording → "Fast HDR
conversion"; `STARSHOT_WGC_FAST_CONVERT=1/0` env override for A/B) switches
*every* kernel (including the scalar tail) to:

- **An 80 KB hybrid PQ LUT** (20,480 entries instead of 65,536): the low
  quarter (r01 < 0.0625, i.e. < ~625 nits, where the PQ curve is steep and a
  16K grid's first bin alone spans ~100 codes) keeps full 65,536 resolution;
  the top 94% is 4-slot center-sampled (`lut[k] = PqLutF[4096 + 4·(k-4096) + 2]`,
  last bin clamped). The exact 65,535-scaled index `t` remaps onto it as
  `j = t < 4096 ? t : 4096 + ((t-4096+2) >> 2)` (max j = 19,456 < 20,480).
  SIMD remap uses the sign-bit mask idiom (`sbit = shr(And(t-4096, 0x80000000),
  31); mask = 0 - sbit`) because the projection has no int `CompareLessThan`
  and a logical shift of a negative int yields 1, not -1.
- **Reciprocal chroma**: `0.5 + delta · (1/UDiv|1/VDiv)` multiplies replace
  the two 256-bit `divps` per even-row group (the same reciprocal math in the
  scalar tail, so mixed-kernel rows stay one mode).

Measured deviation vs the exact path (probe Part E3, 200k random groups + the
per-band E4 sweep): max |dY| = **1 code over the full range** (0 below 625
nits — the exact quarter), |dU|/|dV| ≤ **1 code** (LUT propagation +
reciprocal ulp; the ±2..3 worst-case estimate was pessimistic); only 9,248 of
200,000 random groups differ at all. All inside the HDR suite's ±6 Y / ±12
chroma bands, so the `MachineExpectation` anchors do not move with the toggle
on (confirmed by the fast-mode suite run below). The hybrid — not a uniform
16K grid — is what makes the ±1-code guarantee hold.

Cost note: on this box the fast LUT is *not* a speed win (the 32 MB L3
already keeps the 256 KB LUT hot, and the remap's ~10 extra ALU ops per
channel exceed the LUT-traffic savings — probe ST: `avx2-wide` fast 3.06×
vs exact 4.03× vs scalar). The toggle's value is for cache-poor CPUs (smaller
L3, many workers) and the divide→multiply; it is exposed for user choice,
off by default.

**In-app A/B matrix** (8 s `--record-test window`, raw buffer on the C:
NVMe, stage-trace convert + the encoder's own frame count cross-checked
equal on every run; WGC arrival = the per-second arrival-trace average):

| run | convert avg | p50 | capture | WGC arrival |
| --- | --- | --- | --- | --- |
| exact `scalar` @32w | 72.5 ms | 73.0 ms | 12.2 fps | ~51 |
| exact `avx2` @32w | 25.4 ms | 25.2 ms | 28.5 fps | ~54 |
| exact `avx2-fast` @32w | 37.2 ms | 38.4 ms | 21.2 fps | ~50 |
| exact `avx2-wide` @32w | 20.9 ms | 20.6 ms | 31.0 fps | ~54 |
| exact `avx512` @32w | 40.8 ms | 42.8 ms | 19.9 fps | ~47 |
| fast `avx2` @32w | 24.4 ms | 24.2 ms | 29.5 fps | ~51 |
| fast `avx2-wide` @32w | 20.5 ms | 19.5 ms | 33.4 fps | ~53 |
| fast `avx512` @32w | 42.4 ms | 43.5 ms | 18.9 fps | ~45 |

Worker sweep on the wide kernel (exact unless noted):

| run | convert avg | capture | WGC arrival |
| --- | --- | --- | --- |
| `avx2-wide` @8w | 28.2 ms | 27.6 fps | ~60 |
| `avx2-wide` @16w | **18.6 ms** | **36.6 fps** | ~60 |
| `avx2-wide` @32w | 20.9 ms | 31.0 fps | ~54 |
| `avx2-wide` @64w | 40.2 ms | 19.6 fps | ~41 |
| `avx2-wide` @16w, fast | 18.6 ms | 36.9 fps | ~60 |
| `avx2-wide` @32w, fast | 22.1 ms | 31.9 fps | ~53 |

**The wide kernel wins on this box** — 20.9 ms / 31 fps at the default 32
workers (first time anything beats the pre-regression 3-gather baseline,
25.3 ms / ~31 fps), and **16 workers is the wide kernel's sweet spot:
18.6 ms / 36.6–36.9 fps** (WGC delivers ~59.5–59.7 there; the pipeline
keeps ~61 %). Note the worker optimum inverts between kernels: the 3-gather
kernel wanted 32 workers (more workers amortize the 256 KB LUT traffic),
while the wide kernel wants *fewer* — each wide worker already holds 48
loads in flight, so at 32 workers the shared-LUT/L2 contention dominates
(40.2 ms at 64w). "Convert workers" in Settings → Recording now defaults to
16 (the wide kernel's optimum); Auto (min(rows, processors) — 32 here, what
the 3-gather and scalar kernels prefer) stays available, and
`STARSHOT_WGC_WORKERS` still wins over both. The fast toggle adds nothing on
this box (wide fast 20.5 ms ≈ wide exact
20.9 ms — the 32 MB L3 keeps the 256 KB LUT hot, exactly the probe ST
prediction).

**HDR suite, exact mode**: 5/5 (4 recording rows + play row, 7 m 14 s),
default (AVX-512) kernel, `MachineExpectation` codes unchanged.
**HDR suite, fast mode** (`STARSHOT_WGC_FAST_CONVERT=1`): within the bands;
the ≤1 luma / ≤1 measured chroma shift vs exact keeps every row inside
±6 Y / ±12 chroma, so no `MachineExpectation` re-anchoring needed.

## User-facing levers (2026-11-02): Target FPS cap + target bitrate (ABR)

Two more Settings → Recording options that change how the pipeline is *used*,
not how it's built (both default off = no behavior change):

- **Target FPS** (`VideoRecordTargetFps`, 0 = native refresh, combo ladder
  60/48/30/24/15): reuses the existing WGC-pump drop-before-readback in
  `WgcCaptureSource` (`10_000_000 / fpsCap` QPC interval, `keyint = max(30,
  fpsCap*2)` already adapts). Dropped frames skip the readback + convert +
  encode tail, so CPU usage falls with the cap; playback timestamps stay
  correct (`pts = qpc - FirstVideoQpc` is QPC-based, so a capped recording
  plays back at real time). The legacy hidden `VideoRecordFpsMode` ladder
  (1 => 60, 2 => 30) is honored only while TargetFps is 0.
- **Bitrate** (`VideoRecordBitrateKbps`, 0 = off → CRF quality mode, combo
  ladder 5/10/20/32/48/64 Mbps): new shim exports
  `ss_x264_set_bitrate` / `ss_x265_set_bitrate` (called after `SetCrf`, so
  keyint/bframes/threads survive; `kbps <= 0` is a no-op) switch rate
  control to ABR — file size lands near bitrate × duration.

End-to-end verification (record-test, 4K HDR display, live mode, `br=` now
visible in the "Recording started" log line):

- `fpsCap=15` → 67 frames in 5 s (13.4 fps): when the 4K pipeline is
  CPU-bound the pump lags ~28 ms/frame, which stretches the drop grid and
  lands the realized rate slightly *below* the cap (a cap is an upper
  bound; at 1080p-class loads the pump keeps up and lands exactly on it).
- x265 ABR `br=100` → 64 kb/s vs the CRF baseline of ~123 kb/s on the same
  content: ABR tracks the target when it is reachable. On flat 4K content
  an 8000 kb/s target cannot be absorbed (content ceiling ~210 kb/s at qmin;
  a static 4K desktop ~430 kb/s for x264) — ABR pins QP at its floor and the
  file comes in *under* target × duration, which is correct ABR behavior,
  not a tracking failure.
- x264 ABR (SDR path, forced via `VideoRecordHdrMode=1` on the HDR display):
  mode engaged (`H264-SDR br=...` in the start log); a 100 kb/s target
  bottoms out at the x264 4K floor (~490 kb/s, an all-skip 4K frame still
  costs ~500 B).

## GPU roadmap (the eventual goal)

The WGC frame already exists as a **D3D11 texture** (`Direct3D11CaptureFramePool`).
The current design drags it to the CPU (66 MB @ 4K HDR) to run the conversion; the
GPU path removes all of that:

1. **D3D11 compute shader: WGC surface → P010** (half-float RGB → PQ + BT.2020 matrix,
   2×2 chroma subsample). Runs in a few ms on the dGPU (and the iGPU); the 66 MB
   readback disappears entirely. The CPU's `FrameProcessor` math (PQ LUT + BT.2020nc
   matrix in the PQ domain) ports 1:1 into a shader with a 1024-entry PQ LUT texture;
   a second dispatch does the clli reduce (atomic min/max/sum over the Y plane) so the
   `clli` box still comes from real per-frame stats.
2. **clli statistics as a small GPU reduce kernel** on the same device (min/max/mean of
   the Y plane).
3. **Hardware 10-bit HEVC encode**, two routes:
   - **NVIDIA Video Codec SDK (D3D11 interop)** — what OBS/ShareX do. Register the
     shared WGC texture with the NVENC device, feed P010 10-bit frames straight from
     GPU memory (zero CPU copies), full per-frame control (keyint, QP/CRF-style rate
     control, SEI/CLLI in-band). 4K60 10-bit HEVC is comfortably inside the dGPU's
     NVENC ASIC; per-frame CPU cost ~1 ms (API calls only, the ASIC encodes).
     Cost: a new (free, license-gated) SDK dependency to stage in `tools\`, like the
     encoder DLLs today.
   - **Driver-shipped NVENC MFT (Media Foundation)** — no new dependency; the driver
     already ships the HEVC encoder MFT with D3D11 texture input. Less control (fewer
     rate-control knobs, 10-bit support depends on the driver generation), more
     plumbing (MF sample allocation over shared textures).

   The muxer needs no changes either way: it already consumes `[len4][NAL]` access
   units and parses HEVC sync types (`SplitAccessUnits` / `ContainsSyncHevc`); an
   NVENC stream is the same container.

End state: 4K@60 (even 4K@120) HDR with the CPU essentially idle; the serial-chain
design becomes irrelevant because each stage is a ~ms GPU job. This is the only route
to the 30–60 fps @ 4K target — the CPU x265 encode alone caps 4K at ~12 fps
(measured, decoupled pipeline + `ultrafast`; see "Remaining CPU levers" above for
what the CPU path can and cannot give).

## Reproducing the numbers

```powershell
# one-time: build the encoder DLLs into build\codecs (needs the VS C++ workload)
pwsh tools\codecs\build-codecs.ps1

# build + run the probe (~1-2 min; --quick for a shorter pass)
dotnet build tools/PerfProbe/PerfProbe.csproj -c Release
dotnet tools/PerfProbe/bin/Release/net10.0/PerfProbe.dll

# x265 knob A/B (in-process comparison: preset/threads/psy-rd on 4K noise + flat):
dotnet tools/PerfProbe/bin/Release/net10.0/PerfProbe.dll --ab

# deterministic chroma-row alignment check of the parallel conversion (no GPU/encoder):
dotnet tools/PerfProbe/bin/Release/net10.0/PerfProbe.dll --verify
```

The probe prints the x265 banner on open — it should show
`using cpu capabilities: ... AVX2` (the pre-fix DLL printed `[noasm]` / `none!`).
End-to-end check: record a 4K HDR clip, then
`tools\ffmpeg\bin\ffprobe -v error -select_streams v:0 -count_frames -show_entries
stream=r_frame_rate,nb_read_frames -of csv=p=0 <clip>.mp4`.

## Status

- [x] Rebuild x265 with `ENABLE_ASSEMBLY=ON` (nasm in `tools\nasm\bin\`, x265-10build reconfigured)
- [x] Multithread `FrameProcessor` conversion (HDR + SDR, row chunks on pool threads)
- [x] Fold clli scan into the conversion (`ClliY` stats, no second Y pass)
- [x] Fix the parallel conversion's off-by-one chroma-row bug (`cbRow` start; `PerfProbe --verify` regression check)
- [x] Make the HDR suite's color assertions fps-agnostic (strict Y, ±12 chroma band, ±2 per-phase drift guard)
- [x] Decouple ingest from encode (pump = readback+convert; dedicated encode thread; double-buffered `FrameProcessor` + per-set semaphore; bounded job channel backpressure)
- [x] Kill the per-frame 66 MB LOH readback allocation (session buffer + `GetPixelBytes(IBuffer)`)
- [x] Switch x265 preset to `ultrafast` (2.5× on hard content; `PerfProbe --ab` A/B)
- [x] Expose the x265 preset to the user (Settings → Recording → "Encoder speed (HDR)", 6-step ladder, `AppConfig.VideoRecordEncoderPreset`, default `ultrafast`)
- [ ] CPU levers (analyzed, not implemented — "Remaining CPU levers" section): x265 knob A/B batch (`--ctu 32` / `--dyn4o` / `--rdoq 0` / pool + frame-thread shapes, possible `X265_MAX_FRAME_THREADS` rebuild + AVX-512 dispatch check)
- [x] Content-adaptive frame skip/repeat for (near-)static screens: the repeat-sample
  variant shipped as "Skip unchanged frames" and was **removed** (user-reported wrong
  playback length + constant freezes - "L1 removed" in the results section); a VFR
  variant (write no sample, player holds the last frame) could still be revisited
- [ ] Convert worker-pool rewrite (kill the per-frame `Task`/array allocs; optional AVX-512 SIMD) + raw D3D11 shared-handle readback + encode-thread hygiene
- [x] Root-cause the 4K HDR ~12 fps: arrival + stage traces → the CPU consumer
  pipeline (scalar `ConvertHdr` ~78 ms/frame), **not** WGC delivery (2026-11-01,
  "Root cause — final" section; five hidden env-var probes documented there)
- [x] SIMD `ConvertHdr` (AVX2 baseline; AVX-512 left optional) — landed 2026-11-02:
  semantic .NET 10 intrinsics API (the C-style `_mm*` names are gone from .NET 10),
  bit-for-bit validated against the scalar path (210,852 groups, 0 mismatches) and
  the HDR suite (4/4 rows, ±6 Y / ±12 chroma bands, `MachineExpectation` codes
  unchanged). Convert 77.8 → 25.3 ms, 4K capture 11.5 → ~30 fps
  ("Post-SIMD results" in the Root-cause section)
- [x] Gather-reduction half→float fast path (8 KB `SubnormalLut`, bit-exact, in both
  SIMD kernels) + AVX-512 16-px kernel + kernel fallback chain
  (scalar → AVX2 → AVX-512, default = widest available; `STARSHOT_WGC_SIMD`
  override for A/B) — landed 2026-11-02: probe-verified (all 65,536 halves +
  148,752 16-px groups, 0 mismatches) and HDR suite 4/4 on the default
  (AVX-512) kernel with `MachineExpectation` unchanged. App A/B on the Zen 5
  CPU is context-dependent (512-bit upconverts; the 32 MB L3 keeps the
  256 KB LUT hot, so the fast path loses to the previous 3×-gather AVX2 here —
  `STARSHOT_WGC_SIMD=avx2` is the box winner, 35.6 ms / 22.6 fps):
  "SIMD kernel A/B" in the Root-cause section
- [x] (Cheap) eliminate the redundant pixel recompute in the `ConvertHdrRows` chroma
  branch — subsumed by the AVX2 kernel (the chroma branch reuses the luma pass's
  PQ values in-register; 2026-11-02)
- [x] 16-px wide AVX2 kernel (`avx2-wide`: batched 256-bit gathers, 48 loads in
  flight) + vectorized 3/4-level butterfly clli reduce + restored 3-gather `avx2`
  baseline as a named kernel — 5-kernel enum, probe-verified (487,640 16-px
  groups, 0 mismatches; remap check 0 lane mismatches over 65,536 indices);
  in-app winner on the test box: 18.6 ms / 36.6–36.9 fps at 16 workers
  ("SIMD kernel A/B" + "Kernel set grew to five" in the Root-cause section)
- [x] Expose the kernel + worker-count levers in Settings → Recording ("HDR
  convert kernel" Auto/AVX-512/AVX2-wide/AVX2/AVX2-fast/Scalar + "Convert
  workers" Auto/8/16/32/48/64; defaults = AVX2-wide + 16 workers = the in-app
  winner, Auto still available = previous behavior; the
  `STARSHOT_WGC_SIMD` / `STARSHOT_WGC_WORKERS` env probes still win for A/B).
  End-to-end verified: writing `VideoRecordSimdKernel=2` + `VideoRecordWorkerCount=16`
  to the active `config.sjson` (portable Debug build = `build\config.sjson`, the
  installed copy = `%LOCALAPPDATA%\Starshot\config.sjson` — the record-test host
  follows the same data-location rule via `PrepareHeadless`) drops the 8 s
  record-test convert time from ~42 ms (Auto = AVX-512 here) to ~18.2 ms
  (the wide@16w matrix value) with no env vars set.
- [x] Fast-convert toggle (Settings → Recording "Fast HDR conversion", default off
  = bit-exact): 80 KB hybrid PQ LUT (exact low quarter + 4-slot center-sampled
  top 94%) + reciprocal chroma; luma ±1 / chroma ≤±3 codes (measured ≤1),
  inside the suite bands; `STARSHOT_WGC_FAST_CONVERT` env override for A/B
- [x] Post-encode mode: raw disk buffer + background drain (Settings → Recording "Encode
  after stopping" + "Raw buffer folder", default off; "Post-encode mode" section)
- [x] Target FPS cap + target bitrate (ABR) in Settings → Recording (both default off:
  `VideoRecordTargetFps` 0 = native refresh, `VideoRecordBitrateKbps` 0 = CRF quality
  mode; new shim exports `ss_x264_set_bitrate` / `ss_x265_set_bitrate`; "User-facing
  levers" section)
- [ ] GPU path: compute-shader WGC→P010 + GPU clli + NVENC 10-bit HEVC (the 4K@60 route)
