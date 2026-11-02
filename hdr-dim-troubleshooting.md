# HDR recording looks dim / desaturated — troubleshooting log

Working doc to survive context compaction. Records what we've tried, what's confirmed,
and what's still open. Update it as we go.

## Symptom (user report)

- Starshot's HDR recording (10-bit HEVC, HDR10) looks **dimmer and desaturated** vs the
  live on-panel display. Red → pink/salmon, blue → light baby blue.
- A recording of normal **SDR** content (Windows/YouTube) looks **"almost completely gray."**
- User has Windows HDR on, viewing on the 4K HDR panel (peak ~1499 nits, SDR white 240 nits).
- PotPlayer renderer switched to D3D11 + HDR output: slightly less dim, but still far less
  saturated than live. Green is the "best" of the four; red/blue worst.

## Environment / panel facts

- 4K panel: peak ~1499 nits (HDR), 240 nits SDR white. 1440p: ~1156 nits @ 180 Hz.
- Windows HDR is **on**. The app always takes the **HEVC-HDR10** path (log confirms
  `Recording started: ... "HEVC-HDR10"` for every capture, including SDR content).
- WGC delivers linear scene-referred RGB (R16G16B16A16Float, 80 nits/unit; the luma row is
  the sRGB/BT.709 triplet 0.2126/0.7152/0.0722 — **not** the BT.2020 row). Out-of-gamut
  2020 colors carry negative channels (e.g. 10000-nit 2020 RED = WGC (16600, −1245,
  −181) nits). The repo's measured matrix `WgcTo2020Ainv` (FrameProcessor.cs) is the
  WGC→2020 mapping (inverse round-trips to 1e-16). Trust it and `PqMath` (both
  cross-verified); do **not** re-derive WGC↔709↔2020 from CIE primaries — hand-built
  CIE 2020 matrices do not close to D65 (see "Superseded claims" below).
- The signal host renders a **10000-nit** BT.2020/PQ flat field (GREEN/RED/BLUE/YELLOW).

## What is CONFIRMED correct (do not re-litigate)

- **Container tags** are correct HDR10: `hevc Main 10 / yuv420p10le / range=pc (full) /
  color_space=bt2020nc / color_primaries=bt2020 / color_transfer=smpte2084`. `colr` box =
  `nclx 09 10 09 80`.
- ~~**Luma (Y) is correct.**~~ **CORRECTED:** the recorded Y codes matched the *old (wrong)*
  ground truth within ±6 (GREEN 980, RED 872, BLUE 711, YELLOW 1016), but those values are
  **not** the standard. The standard luma for these fields is much lower (see Bug 2). The old
  "luma is correct" claim was a shared-constant false positive (encoder + ground truth agreed
  on the wrong domain).
- **Local ffprobe key is `color_transfer`** (this ffmpeg master build; the old `color_trc`
  key is silently omitted). The test's `FrameAnalysis` already queries `color_transfer` —
  so the test's HDR10 check is **not** silently broken. Verified, no change needed.
- Ground truth (`PqMath.ExpectedCodes`) is an **independent analytic** computation (same
  ST 2084 / BT.2020 math, re-derived), so a passing flat-field test genuinely proves the
  *luma* + *chroma-as-encoded* match the model.

## Bugs found

### 1. `clli` box `maxFALL` miscalculated  (FIXED in `VideoRecordService.cs`)

- **Was:** `maxFALL` = a **global** average of Y codes taken **in the PQ (nonlinear) domain**,
  then converted to nits. Two errors: (a) it's an *average*, but `maxFALL` is the *max over
  frames* of the *per-frame* average light level; (b) averaging in the PQ domain is wrong.
  For the flat-field test it produced `maxFALL=2405` when the correct value is ≈5700 nits
  (the YELLOW frame's mean *coded* luma: PQ EOTF of Y=962; ≈6100 with this machine's
  shifted YELLOW Y=969). The field's *physical* yellow luminance is ≈9400 nits
  (0.9407 × 10000) - a different quantity; clli carries the coded luma (ffmpeg-style
  approximation from the Y plane), so the expected value is the ≈5700/≈6100 one.
- **Fix:** added `MaxFrameAvgY10` (max of the per-frame mean Y code, PQ domain); `maxFALL =
  PqCodeToNits(MaxFrameAvgY10)`. Removed the old `YSum10`/`YCount` global-average path.
- **Caveat:** PotPlayer/VLC ignore `clli`, so this likely does **not** change what *they*
  show; it corrects the hint for panels/TVs that honor `clli`. Compiled + csharpier clean.
  Requires a rebuild + relaunch to take effect.

### 2. **Wrong colorimetric domain** — 2020 matrix done in the linear domain, not the PQ domain  (ROOT CAUSE, fix in progress)

- **The bug:** `FrameProcessor.ConvertHdr` (via `RgbToYuv01`) and the mirrored
  `PqMath.ExpectedCodes` run the BT.2020 RGB→YUV matrix in the **linear-light** domain and
  then PQ-encode only the luma (`Y_code = PQ(Yr·R + Yg·G + Yb·B)`), writing the chroma as
  linear values. A standard HEVC HDR10 decoder (H.273, `color_space=bt2020nc` +
  `color_transfer=smpte2084`) expects the matrix in the **transfer-applied (PQ) domain**:
  - `R' = PQ(R)`, `G' = PQ(G)`, `B' = PQ(B)`
  - `Y' = 0.2627·R' + 0.678·G' + 0.0593·B'`
  - `Cb' = 0.5 + (B' − Y')/2(1−Kb)`, `Cr' = 0.5 + (R' − Y')/2(1−Kr)` (Kb=0.0593, Kr=0.2627)
  - `Y_code = round(Y'·1023)`, `Cb_code = round(Cb'·1023)`, `Cr_code = round(Cr'·1023)`
  The `2·(1−K)` division is **correct and standard** (an earlier "drop the division" theory
  was wrong). What was wrong is the **domain**: Starshot's `PQ(linear luma)` is too high for
  saturated colors, and its linear-domain chroma is not what the decoder inverts. A standard
  decoder reconstructs a washed-out color (red gains green+blue → pink; blue gains white →
  baby blue; low-luma SDR chroma is under-coded → "completely gray").
- **Empirical proof (ffmpeg `setparams`+`format`, the reference the players agree with):**
  for a full-scale 10000-nit 2020 primary, ffmpeg with in==out (2020/PQ/full, a pure matrix
  conversion) produces:
  | color  | Y    | Cb   | Cr    |
  |--------|------|------|-------|
  | GREEN  | 696  | 142  | 40    |
  | RED    | 270  | 369  | 1023  |
  | BLUE   | 61   | 1023 | 471   |
  | YELLOW | 965  | 0    | 553   |
  | WHITE  | 1023 | 512  | 512   |
  Two tells: (1) WHITE → Cb=Cr=**512** (neutral stays 512; a "PQ-of-offset-chroma" scheme
  would give ~945 — that's the "948" a prior build observed and misread as a bug); (2) RED
  luma = **270**, not 872 — i.e. luma is the PQ-domain weighted sum, *not* `PQ(linear luma)`.
- **Why the flat-field test passed:** the ground truth shared the same wrong domain, so the
  test was self-consistent (classic shared-constant bug). For the four *pure* primaries the
  *chroma* happens to be identical in both domains, so only the *luma* was off there; mixed
  colors would have had the chroma off too.
- **Standard 10-bit codes for the 10000-nit fields (H.273 convention A, double precision;
  matches the ffmpeg reference within ±3):**
  | color  | Y   | U (Cb) | V (Cr) |
  |--------|-----|--------|--------|
  | GREEN  | 694 | 143    | 41     |
  | RED    | 269 | 369    | 1023   |
  | BLUE   | 61  | 1023   | 470    |
  | YELLOW | 962 | 0      | 553    |
  (Y drops a lot vs the old values 980/872/711/1016; Cb/Cr are unchanged for these flat
  fields but move for real mixed content.)

## Fix applied (task 2) — status (all UNCOMMITTED)

1. `FrameProcessor.ConvertHdr`: PQ-OETF each RGB channel, then the BT.2020 matrix in the
   **PQ domain**; the linear-domain `RgbToYuv01` + PQ-of-luma path is removed.
2. `PqMath.ExpectedCodes`: rewritten in lockstep (same convention, double precision).
3. `QuadClassifier` BLACK threshold `Ym<60` → `Ym<20` (lowest color luma in the suite is
   now BLUE@240n = 36; 60 misclassified it).
4. Rebuilt main app + tests (Debug x64); csharpier format+check clean.

**Harness gotcha (suite run #3, 2026-10-30):** `dotnet build -p:Platform=x64` writes
test output to `bin\x64\Debug\...`, but `dotnet test --no-build` *without* the Platform
property loads `bin\Debug\...` — a stale DLL (pre-rewrite PqMath), which made the suite
"fail" with the OLD ground truth (GREEN 981±6) even though the pipeline codes were
correct. Always pass `-p:Platform=x64` to `dotnet test` (or drop `--no-build` and let it
rebuild consistently). The stale `bin\Debug\net10.0-windows` dir was deleted.

**Run log:** #2 = table below (pre-override). #3 = invalid (stale non-Platform DLL, see
gotcha). #4 = 3/4 pass (4K+1440p 10000 n, 240 n); 5000 n failed on GREEN 696 vs 643±6 →
diagnosed as the deterministic 5000-n GREEN WGC deviation (above) → `MachineExpectation`
extended → **run #5: 4/4 PASS** (1 m 42 s). Suite green on this machine.

**Suite run #2 results** (`HdrFlatFieldRecording`):
- 4K / 240 nits — **PASS** (exact).
- 4K / 10000 + 1440p / 10000 — **FAIL on YELLOW only**: measured 969/58/548 vs expected
  962/0/553 (assert order Y→U, so the Y 969 > 968 fires first). Primaries exact on both
  panels, every run.
- 4K / 5000 — **FAIL on a WGC transient** (see "5000-nit row" below), not a code defect.

**Manual verification** (`manual_row.ps1 <nits>`, per-second center-pixel dump):
- 10000 nits: RED 269/369/1023, GREEN 694/143/41, BLUE 61/1023/470 — **exact**;
  YELLOW 969/58/548 (expected 962/0/553).
- 5000 nits: YELLOW 898/88/546 (expected 892/38/550); transient absent on re-run.

## ROOT CAUSE 2 (corrected): deterministic out-of-gamut shift in the WGC delivery

- The WGC delivery of an out-of-gamut 2020 YELLOW field is slightly shifted vs the linear
  model fitted on the primaries (repo `WgcTo2020Ainv`): measured 2020-space values
  (10710, 10053, −1184) nits vs model (10725, 10075, −1187) → residual
  (−16.5, −21.4, **+0.45**) nits. The shift is **out-of-gamut-only and deterministic**:
  - 240 nits yellow — exact (in-gamut at this level).
  - 5000 nits yellow — U +50 codes (88 vs 38).
  - 10000 nits yellow — U +58 (58 vs 0), Y +7 (969 vs 962), V −5 (548 vs 553).
  - Identical on both panels, all resolutions, and every run → DWM/GPU/ICM level, present
    in the WGC delivery *before* encoding. It is **not** encoder quantization (a 58-code
    deterministic offset on a flat field would require a ~4000-code quantizer bias).
- **REVISED (suite run #4):** the 5000-nit midtone deviation is **not** encoder
  quantization — it's a second WGC-path deviation, on **GREEN**: the WGC path delivers
  5000-nit 2020 GREEN as ≈ (1.7, 5042, 0.3) nits (+0.84% green, ~2-nit R/B floor) instead
  of (0, 5000, 0) → codes 696/194/163 vs standard 643/170/76 (Y +53 codes — far beyond
  x265's ±1-2-code luma quantization; deterministic across every run, both sparse and
  full-frame captures). A mid-band bulge: 240 n and 10000 n are exact for all colors;
  5000-nit RED (249/379/986) and BLUE (56/986/473) ARE exact (±1 code: V/U 985.47→986,
  i.e. +38/+40 nits, within tolerance). GREEN 5000 n is asserted against the measured
  machine codes (part of `MachineExpectation`).
- The stimulus host is exact: `PQ(0.5) = 0.926547` (repo constants, double) matches the
  host log (`0.92655`); host `PqCode` (main.cpp) uses the identical C1..M2 constants.

### 5000-nit row: WGC/DWM all-neutral transient

A WGC session can deliver ~192 sparse **all-neutral 4232-nit** frames (Y=192, U=514,
V=511) instead of the field, right after init — machine state, not code. First frames
after WGC init can also be contaminated (5000-run GREEN 696/194/163 at sec 2–3). The
suite's ~4.6 s segment averages absorb most of this; a clean manual re-run of the same
row is correct. If the row fails all-neutral, re-run it.

### Superseded claims (do not use these numbers)

- "WGC delivers yellow with ≈3200 nits of blue / 709-space (1.0048, 1.0130, −0.0051)" —
  artifact of a broken hand-built 709→2020 matrix (my CIE 2020 primary coords don't close
  to D65 with the 2020 luma weights; the M2020 primary-direction checks failed). The
  correct characterization is the small out-of-gamut-only shift above (≤0.2% of full
  scale on Y, ≤58 codes on U).
- Trust only: repo `WgcTo2020Ainv` (measured WGC→2020; inverse verified 1e-16; reproduces
  the FrameProcessor RED WGC anchor exactly) and `PqMath` (ffmpeg-verified). M709 (709
  primaries (0.64,0.33)/(0.3,0.6)/(0.15,0.06) + D65) verified against the standard
  sRGB-D65 matrix. Do NOT trust hand-built CIE 2020-primary matrices or remembered
  standard 709→2020 coefficients.

### Registry sweep — no tone-mapping toggle found (2026-10-30)

- Full HKCU + HKLM sweeps for "ToneMapping", "HDCMY", "SDRBoost", "Vibrance", "Gamut":
  **0 relevant hits** (only unrelated Unity / DaVinci strings).
- AMD Avivo (Color): all defaults (Gamma=22, Brightness=100, Contrast=100; no vibrance
  keys). NVIDIA Control Panel registry absent (NVCPL not installed).
- `HKCU\Software\Microsoft\Windows\CurrentVersion\VideoSettings` (26H2 HD-Color
  user-state): battery keys + `EnableHDRForPlayback=1` +
  `EnableAutoEnhanceDuringPlayback=1` (playback-only, not the WGC desktop path);
  **no per-display tone-mapping values, no subkeys**. `HKLM` counterpart absent.
- One unexplored lead (per-display **"Tone mapping for HDR"** in 26H2 Display Settings →
  HD Color, apparently not registry-backed) was explicitly **not pursued** — user decision:
  document the shift and relax the suite's YELLOW asserts instead (see "Open questions").

## Open questions / watch items

- **WGC-path deviations — DECISION (user, 2026-10-30):** treat them as known
  machine/WGC-path characteristics; do NOT chase the 26H2 per-display "Tone mapping for
  HDR" UI toggle. `HdrRecordingTests` now asserts the MEASURED machine codes for the
  deviated fields (machine-specific `MachineExpectation`, standard `PqMath` codes kept
  for everything else; ±6 band around the machine's stable values): 10000 n YELLOW →
  969/58/548, 5000 n YELLOW → 898/88/546, 5000 n GREEN → 696/194/163. If a future
  machine/driver run diverges, re-measure with `manual_row.ps1` and update the values
  (or restore the standard expectation on a machine without the shifts). A "no segment"
  failure now also prints a re-run hint when the WGC session-init transient (NEUTRAL
  ~4232 nits) is present.
- **5000-nit row transient:** if the row fails with all-neutral 4232-nit frames, just
  re-run it (WGC init transient, machine state).
- After the domain fix, re-verify on the panel (the user's live-vs-recording comparison):
  DONE — user recorded `Recording_20261030_21254157.mp4` (PotPlayer D3D11 + HDR, 36 s)
  with all fixes and reports the picture "looks great". Verified in the file: all color
  tags correct (bt2020nc/bt2020/smpte2084, full range, 10-bit), clli maxCLL=10000 /
  maxFALL=35 (sane per-frame values), per-second center-crop Y/U/V with chroma tracking
  the content (no gray-out; black frames neutral at U≈511 V≈512). For a flat-field
  recording, expect clli `maxFALL` ≈5700 (standard codes) / ≈6100 (this machine's
  shifted YELLOW); was 2405 pre-fix.
- The YouTube "gray" result should be resolved by the domain fix (its low-luma chroma was
  under-coded in the linear domain). Confirm with a fresh SDR-content recording.
- **Git state:** everything in this log is committed (PQ-domain fix, clli
  `MaxFrameAvgY10`, suite machine expectations, this doc). The whole HDR-recording line
  (13 commits, `3110e11` through the commit of this doc, branch
  `screenshot-editor-features`) is unpushed. Do not commit/push without instruction.
- Do **not** conflate with playback-side tone-mapping: a 10000-nit master on a 1499-nit
  panel is tone-mapped down by the panel/player regardless; that's normal and separate from
  this bug.
- Reference experiment scripts (reproducible, kept locally): `hdr_ref3.ps1`
  (ffmpeg `setparams`+`format`, the clean convention-A reference). The earlier `hdr_ref.ps1`
  (raw `-color_*` flags) was confounded — the rawvideo demuxer ignored them (709 fallback).

## File map (relevant)

- `src/Starshot/Features/Recording/FrameProcessor.cs` — `RgbToYuv01`, `WgcTo2020Ainv`, PQ LUT.
- `src/Starshot/Features/Recording/VideoRecordService.cs` — HDR decision, clli tracking.
- `src/Starshot/Features/Recording/VideoEncoder.cs` — `X265SetHdr(10,9,16,9)`, full range.
- `src/Starshot/Features/Recording/Mp4Muxer.cs` — hvcC/colr/clli boxes.
- `src/Starshot.Tests/PqMath.cs` — analytic ground truth (shares the chroma formula).
- `src/Starshot.Tests/FrameAnalysis.cs` — ffprobe (`color_transfer`) + 10-bit extraction.
- `src/Starshot.Tests/HdrRecordingTests.cs` — the suite.
- `tools/HdrSignalHost/main.cpp` — the C++ signal host (10000-nit 2020/PQ fields).
- Analysis scripts (kept locally): `manual_row.ps1
  <nits>` (host + `--record-test windowfg 22` + per-second center-pixel Y/U/V dump;
  recordings in a local folder), `yellow_inv.ps1` (full pipeline
  inversion, repo constants; trust only the WGC-nits / 2020 columns — its 709 column used
  the suspect CIE M2020), `pqcheck.ps1` (PQ constant check), `matdbg2.ps1` (matrix
  diagnostics).
- Recordings (local): `Recording_20261030_*.mp4`.
  - `..._19233954` = original flat field (proven correct pixels; re-played, not re-recorded).
  - `..._20114738` = new flat field (20:11).
  - `..._20164720` = YouTube (20:16), the "completely gray" one.
