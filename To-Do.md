# To-Do — video recording

Branch: `screenshot-editor-features` (local: 3110e11 + 7c2d947 + 6d259c9 + f5e0541 + 13abde1, not pushed)

## 0. Audio silence — full root-cause chain (resolved 2026-10-30, committed in 13abde1)

Symptom: the AAC track in recordings was 100% silent. Five independent causes, found in order:

1. **Default device is a silent virtual mixer.** The Windows default playback endpoint was a virtual-mixer playback device (8ch/96kHz); its WASAPI loopback is silent — the mixer forwards the real mix to the hardware output device (2ch/48kHz). Probe-confirmed: a tone rendered *directly to the mixer itself* reads back as silence from its own loopback (the virtual device decouples its own mix from loopback capture). **Fix:** new **Recording** settings page selects the loopback capture device (`AppConfig.VideoRecordAudioDeviceId`, empty = system default); device list via new `WasapiDeviceEnumerator` (WASAPI 2.0 aware: HSTRING `GetId`, new `IMMDeviceCollection`/`IPropertyStore` IIDs, raw PROPVARIANT reads) + i18n (en/zh/ja).
2. **WASAPI 2.0 render client IID change (build 26300).** `IAudioRenderClient` is now `F294ACFC-3146-4483-A7BF-ADDCA7C260E2`; the classic `F294AC38…` returns `E_NOINTERFACE`. Affected only the standalone test probe (`wasapiprobe` tone mode), not the app (capture-only; capture IID `C8ADBD64…` unchanged).
3. **Resampler stall in `FeedAac`.** The input cursor `_inPos` indexed per-packet buffers, so the global position reset at each packet and the resampler starved. Fixed with a global integer cursor `_inTotal` + 2-sample downmixed carry `_carryL/_carryR` for interpolation across packet edges (`GetInputSample` with local index / -1 / -2).
4. **faac int16-scale float input (the main silence cause).** faac 2.2's `FAAC_INPUT_FLOAT` is on the int16 scale (±32768), **not** natural [-1,1] floats: the faac CLI frontend multiplies float32 PCM by 32768 (`PCM_16BIT_FLOAT_SCALE`, `frontend/input.c`), and `libfaac/frame.c appendInputFifo` copies FLOAT input unscaled. The app fed raw WASAPI floats → 32768× too small → every frame quantized to zero. Proven with a direct ctypes test of `Starshot.Codecs.dll`: amp 0.1 → 100% silent AAC; amp 0.1×32768 → tone decodes cleanly. **Fix:** `WasapiLoopbackSource.ScaleToFaacDomain` (in-place ×32768.0f) at both faac feed sites (full frame + `StopAndFlush` remainder); `_silence` is all-zero (scaling a no-op) and the faac flush loop passes no input. Level note: faac CBR output runs ~1.55× the s16 level of ffmpeg's AAC for identical input (encoder-specific, not a bug — expected decoded peak for a 0.1-input tone ≈ 5000–5300 s16).
5. **Silence-filler over-generation.** The poll loop's data path never refreshed `_lastSilenceQpc100`, so *every* empty poll (there are many, even with continuous data) injected a silence frame in parallel with the live stream → AAC track ~50% longer than the video (14.78s vs 9.75s on a 10s test). **Fix:** the marker is updated on every data packet; silence only fills real gaps of ≥2 frames. Re-verified: audio 10.005s vs video 9.867s, tone peak 4991 s16 (−16.3 dBFS), near-silent frames 1% (was 32%).

Same commit, related:
- 48 kHz/stereo output stage: downmix (C/LFE 0.5, surround 0.707) + linear-interp resample from the device mix format (e.g. 8ch/96kHz virtual endpoints) before faac — faac cannot resample, and the MP4 track is declared 48k/stereo.
- x265 flush salvage: a failed `Flush` no longer discards the recording — encoded samples are salvaged and the file finalized with a warning.
- clli written as the standard 16-byte `FullBox` (see §1).

**Pending user confirmation:** pick the real speakers/headphones in **Settings → Recording → Audio device** (dev config `build\config.sjson` already points at a real output device for the headless tone test), then record real content and check the audio plays.

## 1. HDR10 content + container correctness (user reported "everything is magenta", 2026-10-29)

**Root cause** (found 2026-10-30, forensics + FFmpeg standard reference):
- `FrameProcessor.ConvertHdr` applied the PQ OETF to **all three planes** (Y/U/V). Standard HEVC HDR10 applies the ST 2084 transfer curve to luma only; U/V stay linear 10-bit (full-range neutral = 512 — verified against FFmpeg's own libx265 HDR10 output: gray frame decodes to U=V=512). Our neutral chroma was 948 (the PQ code of 0.5), i.e. a +436 offset (~85% of the chroma range). Every decoder — SDR or HDR — reads that as a huge R+B offset: black renders as pure magenta, so **every** color comes out "a shade of magenta".
- It is a content-side bug, not a player fallback: the U/V data itself is non-standard, so no player can render it correctly. Explains the user's A/B: the SDR 8-bit path (ConvertSdr) was correct ("colors fine, not HDR accurate"); the bug entered together with the HDR pipeline.
- Why the frame-level Δ=0 check missed it: the expected codes were derived from the pipeline's own (buggy) output — the comparison was self-referential, so the non-standard 948 neutral was baked into the "expected" values and every check passed. The local zscale SDR render had actually shown the magenta symptom, but it was dismissed as a zscale build quirk instead of being chased into the content.

**Fixes:**
- [x] `FrameProcessor.ConvertHdr`: U/V now go through `Lin10` (linear 10-bit full range, neutral 512) instead of the PQ LUT. Verified (`Recording_20261030_00025460.mp4`): Y regions Δ=0 (unchanged), neutral U/V = 512, Python SDR proof render shows correct hues (red R=190/G=0/B=0, blue B=243, yellow R+G/B=0, black ~0), hvcC/colr intact, clean full decode.
- [x] `Mp4Muxer` clli box — **revised 2026-10-30**: the earlier "drop the U32 → 12-byte box" change was a misdiagnosis. A standard `clli` is a 16-byte `FullBox` (`[size]['clli'][version(8)=0][flags(24)=0][maxCLL(16)][maxFALL(16)]`); the 4 `00` bytes *are* the required version+fields, not a stray U32. The 12-byte box made VLC/PotPlayer/FFmpeg misread it (version=0x01, maxCLL out-of-range→0), losing the tone-mapping hint. Restored the `U32(0)` (version+flags) → 16-byte box. Verified in `Recording_20261030_00451294.mp4`: `clli size=16`, `00 00 00 00 01 19 00 0e` (maxCLL=281, maxFALL=14), clean FFmpeg decode.
- [x] `VideoRecordService.PqCodeToNits`: rewritten as the ST 2084 EOTF inverse of `BuildPqLut` (the old one mixed nits-domain and normalized-domain constants and mapped every nonzero code to ~9988 nits; the user's file carried 9988/9983 at the shifted offset). clli now 281/14 on the 4K test content — sane.
- [x] User retest of the magenta: confirmed fixed — the test-quadrant source window looks correct on the user's monitor, and the new files decode spec-correct (see below).

### 1a. Follow-up: "now everything is washed out / almost gray" (user report, 2026-10-30)

**Players involved (user): VLC and PotPlayer.** Symptom: *disable* HDR in a player → even grayer; *enable* HDR → less gray but **still** gray. So the players *are* doing some HDR-aware processing, yet still desaturate.

**Two independent findings:**

1. **Content is spec-correct.** All 10-bit Y/U/V match theory (red 489/508/526, green 431/509/509, blue 349/526/510, yellow 622/497/513, black 0/512/512, white-text max 629 ≈ 280.84 nits = this display's SDR white). A *proper* HDR→SDR tone-map (PQ EOTF → nits → 280-nit SDR white, sRGB OETF) renders the quadrants **vivid**: red (255,0,~50), green (~35,126,0), blue (0,~17,255), yellow (255,254,0). So the *pixels* are fine.

2. **The gray is a tone-mapping behavior, and the `clli` box was broken.** Verified against the FFmpeg reference build:
   - FFmpeg's *naive* HDR→SDR auto-convert (10→8-bit scaling, no perceptual tone-map) yields **the same gray the user sees**: red (128,121,120) / green (107,110,106) / blue (87,87,95) / yellow (156,157,149) — within ±2 of the user's screenshot (126,122,120 / 106,108,105 / 86,85,92 / 155,158,148).
   - A *transfer-only* zscale (PQ→709, no luma compression) is pale/washed (red 249,209,210).
   - A *proper* tone-map (Reinhard) recovers the color.
   So a player that does not perform a real perceptual tone-map (or feeds a broken `clli` maxCLL hint) shows gray/pale. The `clli` box was 12 bytes (missing version+flags) → players misread maxCLL → wrong tone-map. **Fixed to the standard 16-byte `FullBox` (see Fixes above).**

**Pending / open:**
- [ ] User retest of `Recording_20261030_00451294.mp4` (clli fixed) in VLC/PotPlayer — does the color come back?
- [ ] If still gray: the players are not doing a real perceptual tone-map. Options: (a) retest in mpv / MPC-BE / Windows HDR; (b) **product option** — for SDR-class content (maxCLL ≈ 281 nits, no genuine HDR) auto-select / offer an **SDR 8-bit output** (the `ConvertSdr`/x264 path exists) so plain SDR players show correct colors without needing an HDR-aware player. Awaiting user decision.

**Forensics on the user's own recording (2026-10-30, "washed out" report):** the user's file measures clli maxCLL=850, per-frame peaks 620–750 nits, 98.3% colored pixels — i.e. genuinely HDR content, not SDR-washed. Gray playback in VLC/PotPlayer is a player tone-map limitation (naive 10→8-bit scaling), not a content bug; the clli box was fixed to the standard 16-byte FullBox in 13abde1. Recommendation to user: play it with a real tone mapper (mpv / MPC-BE / Windows HDR playback).

### 1b. Controlled 10000-nit quad test (2026-10-30, content side re-exonerated)

Fully automated, player-free test: a C++ D3D11 signal host (`tools\HdrSignalHost\hdr-signal.exe`, throwaway probe tool) renders the four 10000-nit BT.2020/PQ flat fields (RED→GREEN→BLUE→YELLOW, 6 s each, same PQ codes as a local ground-truth clip `Starshot_HdrQuad_10k.mp4`) in a borderless window covering the 4K panel; the orchestrator (PowerShell) runs Starshot's real capture pipeline on both WGC paths (`raw` monitor + `windowfg` window) and classifies the recorded 10-bit Y/U/V per frame.

**Result: PASS.** All four primaries on the `windowfg` path (the user's real recording path) match the ground-truth codes within 0.1/1023 — RED 877/369/1022.9, GREEN 981/143/41.1, BLUE 711/1022.9/470, YELLOW 1016/0.1/553. The content side is correct end-to-end on HDR content; the gray playback stays attributed to the player tone-map (see §1a).

**WGC HDR float space, measured (raw WGC half-floats from the 10000-nit source):**

| Color | R | G | B (half-float, 1.0 = 80 nits) |
|---|---|---|---|
| RED | 207.5 | −15.56 | −2.27 |
| GREEN | −73.44 | 141.5 | −12.57 |
| BLUE | −9.10 | −1.04 | 139.75 |
| YELLOW | 134 | 126 | −14.84 |

- Out-of-gamut (BT.2020) colors carry **negative channels** — the space is a narrower linear RGB gamut.
- Luma fit: `luma_nits = 80 × (0.2126 r + 0.7152 g + 0.0722 b)` (sRGB/BT.709 luma row) fits all four colors at 80.00–80.08 nits/unit (<0.1% spread). BT.2020 and P3 luma rows are inconsistent (59.9–114.3 / 71.8–84.7 nits/unit). **The 80 nits/unit scale in `FrameProcessor` is correct.**
- Empirical transform: WGC RGB = A × BT.2020 RGB with A columns (80-nit units) col1 (1.660, −0.1245, −0.018141), col2 (−0.5875, 1.1320, −0.100562), col3 (−0.072813, −0.008344, 1.1180); white→white, neutral→neutral.

**Midtone fidelity bug — FIXED.** The old `WgcToLinear` clamped negative channels to 0 and `ConvertHdr` then applied BT.2020 luma coefficients to that clamped, 709-luma space. At full scale (10000-nit primaries) the result was *exactly* right by coincidence (dominant channel clamps to 1.0 and the BT.2020 Yr/Yg/Yb equal the true normalized luma of the 10000-nit primaries) — which is why the 10000-nit test passed while midtones were wrong: a 5000-nit RED field measured y01 0.218 vs true 0.131 (×1.66); GREEN ×1.13, BLUE ×1.12, YELLOW ×1.03; neutral exact. Chroma was off too (5000-nit RED U/V 393/938 vs true 440/768). The fix (`FrameProcessor`): the measured A⁻¹ matrix (`WgcTo2020Ainv`, rows in the source) maps the unclamped WGC floats to true BT.2020 linear RGB (`WgcTo2020Rgb01`), which is clamped to [0,1] after the matrix (negative values = numerical noise, >1 = EDR cap); luma/chroma then use the standard 2020 formulas. Verified: primaries reproduce exactly, YELLOW within 0.05%, `w709ᵀA ≈ w2020` (0.05% noise), and the pipeline's predicted 10k codes equal the ground-truth clip codes. The signal host now takes a nits argument (arg 5, default 10000; each lit channel cleared with the ST 2084 PQ code `OETF(nits/10000)`, 5000 nits → code 0.92655 verified).

**Observations / gotchas:**
- WGC is change-driven: a capture started on static content receives a black session-init frame. Start each capture ~1–1.5 s *before* a content change so the change event lands inside the window.
- The 4K panel itself flickered RED↔GREEN at ~30 Hz during one monitor probe (172 alternating frames in 3 s) — matches the user's "yellow flicker" report; the window-capture path stayed clean (the flicker is on the DWM/EDR-hooked HDR monitor path, cause unknown, outside Starshot).
- Test-tooling pitfalls (orchestrator script, not product code): a single-line file makes PowerShell `Get-Content` return a scalar string, so `[0]` reads the first *character*; GUI-subsystem console output is not captured by `&` (use `Start-Process -RedirectStandardOutput`); `Write-Output` inside functions pollutes the return value (use `Write-Host`).

**Automated as an XUnit integration test (this repo).** The PowerShell orchestrator was replaced by `src/Starshot.Tests` (category `HdrRecording`; opt-in, self-skipping when a prerequisite is missing): one C++ host session per row, ONE `windowfg` recording of 28 s from t0 (the user's real path; the flicker-prone monitor path is dropped), source-rate YUV extract piped to a C# port of the segment classifier (no 1-fps resampling - WGC's jittery ~2-6 fps delivery aliases whole color phases away at 1 fps), per-color longest-segment Y/U/V vs the analytic ST 2084 + BT.2020 codes (±6), plus pix_fmt/trc checks. The host opens on a 10-s GREEN phase (a WGC capture's session init eats the first ~2-3 s, so the long opening phase keeps GREEN visible; RED/BLUE/YELLOW follow at 6 s each). Rows: 4K/10000, 1440p/10000 (control), 4K/5000, 4K/240 nits. See the "Integration tests" section of the README.

**Artifacts:** the old orchestrator `hdr_signal_test.ps1`, the consolidated findings JSON, and the original four recordings (`Recording_20261030_14060456/14061137/14062909/14064102.mp4`) live in a local temp folder and a local recordings folder. The temporary `[DBG]` dump code in `VideoRecordService.cs` was removed with the fix.

## 2. GPU readback/crop acceleration (last item of the original 3-part request)

- [ ] D3D11 staging copy + Map readback to replace the managed `CanvasBitmap.GetPixelBytes` path (4K: ~400 ms/frame -> ~2.3 fps today; target a large fps gain).
- [ ] (Optional) GPU compute shader for the YUV conversion (x264/x265 still consume CPU planes).
- [ ] Accept via `--record-test` (fps + content re-check delta = 0); separate `perf(recording)` commit.

## 3. CI is missing the codecs build step

- [ ] Neither `pr-check.yml` nor `release.yml` (nor `deploy-release.ps1`) runs the codecs build -> CI artifacts (zip/installer) ship without `codecs\` -> recording degrades to the CodecUnavailable toast (the csproj `Exists` guard silently skips the copy).
- [ ] Decide: add a CI step (first check `tools\codecs\build-codecs.ps1` CI dependencies) or document the v1 degradation.

## 4. Push / PR

- [ ] Push `screenshot-editor-features` (3110e11 + 7c2d947 + 6d259c9 + f5e0541 + 13abde1 + later perf work); open a PR if requested — waiting on user.

## 5. Documented, no action

- FFmaster null-muxer VFR "non-monotonic dts" complaints: container stts/dts/pts are strictly monotonic; FFmpeg's own output remuxes identically; not a container defect.
- 4K x high-refresh files are large; CRF is the only knob (no bitrate cap in v1).
