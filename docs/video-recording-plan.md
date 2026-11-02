# Video Recording — Architecture

Status: implemented (v1), pixel content verified headlessly via `--record-test` (see [Verification](#headless-verification--record-test-debug-only)). Records full screen / region / window to MP4 in the screenshot folder; HDR displays produce HDR10 (HEVC 10-bit) like the screenshot pipeline, SDR otherwise.

## Locked spec

| Decision | Choice |
|---|---|
| Encoder | Bundled LGPL dynamic DLLs: x264 (SDR 8-bit NV12 → H.264) + x265 (HDR P010 → HEVC `hvc1`) + faac (WASAPI float32 → AAC-LC), loaded through one C shim. No GPL, no Media Foundation |
| Audio | WASAPI system loopback (shared mode, polling) → AAC-LC; silence keep-alive keeps the audio timeline continuous |
| Targets | Full screen + region + window (3 toggle hotkeys, default Alt+R / Alt+T / Alt+Y). While recording, **any** record hotkey stops (v1: no target switching mid-recording) |
| Control | Toggle hotkey + tray Stop button (1 s elapsed ticker); no on-screen countdown, no global Esc |
| Rate | Native display refresh by default, optional 60/30 fps cap; quality = true CRF (3 levels) |
| Output | MP4 into the existing screenshot folder, `Recording_{time}.mp4` |
| HDR | Auto: HDR10 (BT.2020 full-range + PQ, 10-bit, `colr`/`clli` atoms) when the display is in HDR mode; SDR-only toggle forces 8-bit H.264 |
| Indicator | Tray icon + Stop button + duration tooltip only (WGC would composite any on-screen UI into the video; a toast on stop is fine) |

## Pipeline

```
WGC frame pool (FreeThreaded, depth 4)
  → Channel (drop-oldest, keep-newest)
  → pump thread: CanvasBitmap readback (GetPixelBytes, native format)
  → FrameProcessor (CPU): RGBA8 → NV12 (BT.709 limited)  |  float16 → 线性域 BT.2020 YUV → P010 (BT.2020 full + PQ OETF)
  → VideoEncoder (x264 SDR / x265 HDR, bframes=0, CRF)
  → Mp4Muxer (per-track temp mdat, moov appended at end)
WASAPI loopback poll thread → 1024-sample blocks → faac AAC-LC → same muxer
```

A/V sync: one QPC epoch — video pts from `frame.SystemRelativeTime` (QPC 100 ns), audio from GetBuffer's `hnsTimestamp`; the audio track's `mdhd` start_time is the first-audio minus first-video offset clamped to ≥ 0. Shared video timescale 10 000 000 (100 ns, real per-frame deltas RLE-encoded in `stts`); audio timescale = actual mix sample rate.

## New code

### `src/Starshot/Features/Recording/`

| Class | Responsibility |
|---|---|
| `VideoRecordService` | DI singleton orchestrator. Target resolution (full = foreground window's monitor, window = foreground HWND, region = static selection that must fit one monitor), per-recording HDR decision, start/stop state machine, clli tracking (min/mean of 10-bit Y across frames, inverse-PQ → nits; MaxFALL ≈ content mean) |
| `WgcCaptureSource` | Continuous WGC: long-lived `Direct3D11CaptureFramePool` (depth 4, pool format chosen explicitly — `R8G8B8A8UIntNormalized` SDR / `R16G16B16A16Float` HDR) + `GraphicsCaptureSession`, cursor capture **on**. `FrameArrived` thread only moves frames into a bounded `Channel` (drop oldest); a pump thread consumes, so slow frame processing never blocks the WGC callback |
| `FrameProcessor` | CPU pixel conversion of the readback buffer: SDR `RGBA8 → NV12` (BT.709 limited range), HDR `R16G16B16A16Float → planar P010`. HDR path: scale WGC float (1.0 = 80 nits scene-referenced) into the linear 0..1 domain (1.0 = 10000 nits), form BT.2020 **linear** Y/U/V (U/V centered on 0.5), then apply the SMPTE ST 2084 **PQ OETF LUT per plane** (Y, U, V each). Order matters — PQ is non-linear, so per-channel-PQ-then-luma-combine is wrong (underestimates luma ~35×); must be luma-combine-in-linear-first-then-PQ. Region recording crops here (even-aligned) |
| `VideoEncoder` | x264/x265 sessions with true CRF (quality 0/1/2 → 18/23/28 SDR, 18/22/27 HDR), `bframes=0` (output order = display order → no `ctts`), keyint = max(30, 2×fps or 120), threads = ½ cores. Emits MP4-ready `[len4][NAL]` byte streams; header NALs feed `avcC`/`hvcC` |
| `Mp4Muxer` | Minimal ISO-BMFF writer: `ftyp | mdat(video) | mdat(audio) | moov` with the moov appended after recording (players can still seek). Video/audio samples stream to per-track temp files while recording; `FinalizeFile` assembles the final MP4 (single-chunk `stco`, 32-bit offsets → 4 GB cap). Writes `avcC` (0xE3) / `hvcC` (0x1B), `colr` (nclx), `clli` (HDR), `esds` AAC-ASC |
| `WasapiLoopbackSource` | WASAPI loopback capture with **hand-rolled COM vtables** (classic `IAudioClient` 1F954BFC / `IAudioCaptureClient` 82D3E3FB, loopback flag 0x20000, polling mode). 1024-sample blocks → faac. Idle endpoints deliver no packets, so a QPC-driven CPU silence keep-alive injects silent frames (audio timeline stays continuous). Built with the device's **actual** mix rate/channels (faac cannot resample); very high-rate devices (192 k+) make faac init fail → recording degrades to no audio |
| `CodecsNative` | P/Invoke into the codec shim (raw-pointer exports, array pinning) |

### `src/Starshot.Codecs/` (new)

- `starshot-codecs.c` — one C shim that `LoadLibrary`/`GetProcAddress`s the three LGPL DLLs (x264 0.164, x265 4.3, faac 2.2.0) and exposes a small raw-pointer API (`ss_x264_*`, `ss_x265_*`, `ss_faac_*`). Keeps all encoder API differences in C; C# only pins buffers.
- `tools/codecs/build-codecs.ps1` — builds the three DLLs + the shim into `build/codecs/` (x64).
- `tools/CodecsSmoke` — headless smoke test of all three encoder paths (verified: H.264 NV12, HEVC P010, AAC float32 all produce output).
- csproj: the 4 DLLs are `Content`-copied to `codecs/` next to the exe (**x64 only**); `CodecsNative.Init()` fails gracefully on other platforms → `Starshot_RecordingCodecUnavailable` toast.

### Wiring (all done)

- `ScreenCaptureService.SelectRecordingRegionAsync()` — region target: static selection overlay (reuses the screenshot region flow), containing single display, monitor item + crop. Region must fit one display (virtual-screen coords mapped via system metrics).
- `HotkeyManager` ids 44449–44451 (Alt+R / Alt+T / Alt+Y defaults) + 3 `HotkeySetting` cards; `SystemTrayWindow` record ×3 buttons + Stop item with 1 s `DispatcherQueue` timer elapsed display.
- `AppConfig` 8 keys (`VideoRecordFullHotkey` `1+82`, `VideoRecordRegionHotkey` `1+84`, `VideoRecordWindowHotkey` `1+89`, `VideoRecordFpsMode` 0, `VideoRecordQuality` 1, `VideoRecordHdrMode` 0, `VideoRecordAudioEnabled` true, `VideoRecordOpenFolderAfterStop` false).
- i18n: 16 new keys × 3 resx + manual `Lang.Designer.cs` update (designer is checked in, not regenerated).
- Gallery: `*.mp4` added to `ScreenshotHelper` watcher filters (thumbnails already handled).
- DI: `sc.AddSingleton<VideoRecordService>()`.

## Deviations from the original plan (MF design)

The original plan used Media Foundation encoder MFTs + GPU `ScRGBToHDR10Effect` preprocessing. Final implementation:

1. **Bundled LGPL encoders instead of MF** — true CRF, deterministic 10-bit HDR10 without the optional HEVC Video Extensions, and a much smaller API surface. Trade-off: CPU encode cost (acceptable: ½-core threads, native-fps cap optional).
2. **CPU pixel conversion** — `CanvasBitmap.GetPixelBytes()` readback + `FrameProcessor` instead of GPU D3D11 texture chains (the encoder DLLs consume raw planes, not textures). Simpler; measured cost is fine at 1080p-class.
3. **CPU silence keep-alive** for the WASAPI loopback idle gap instead of rendering a silent stream to the device (no extra audio-graph footprint).
4. **Static region selection** instead of a live `RegionRecordWindow` overlay — reuses the existing screenshot region flow; live preview is future work.
5. **Temp-file mdat assembly** — samples stream to temp files, final MP4 assembled on stop; 32-bit `stco` → 4 GB per-file cap (fine for v1).
6. **bframes=0** — no `ctts`, pts strictly monotonic, simpler muxer.

## WinAppSDK 1.8 projection quirks (hit in this feature)

- `Direct3D11CaptureFramePool` only projects `TryGetNextFrame()` (no `TryGetNextUpdate`); `GraphicsCaptureSession.StopCapture` and `GraphicsCaptureItem.Dispose` don't exist in the projection — dropping the session/item handles is the stop path.
- `frame.SystemRelativeTime` projects as `TimeSpan` (use `.Ticks` for 100 ns QPC units).
- `DisplayArea`/`DisplayArea.FindAll()` live in `Microsoft.UI.Windowing` (verify with the projection winmd, e.g. `tools/WinmdDump`).
- This toolchain rejects `fixed` on value-type locals (CS0213, even `int`/`Guid`); raw-pointer COM args go through `byte[]` buffers instead.
- Vanara `Ole32.CoCreateInstance` is generic over a reference-type out param — for raw pointers, P/Invoke `ole32!CoCreateInstance` directly.

## Headless verification (`--record-test`, DEBUG only)

`Starshot.exe --record-test [fullscreen|window] [seconds]` (`RecordTestHost`, compiled into Debug builds only): minimal config/log init → creates a known-content test window (four color quadrants red/green/blue/yellow + centered "REC-TEST n s" counter that ticks each second + a 24 px magenta scan bar sweeping ~1 s across the bottom) and foregrounds it → records N seconds through the real `VideoRecordService` → prints the output MP4 + log path. No UI/tray/hotkeys are started. The window is a borderless `WS_POPUP` (client == work area; a titled `WS_OVERLAPPEDWINDOW` would steal ~48 px for its caption and clip the bottom scan bar), and its GDI `COLORREF` constants use `0x00BBGGRR` byte order (a classic mix-up made the "green"/"yellow" quadrants render navy/cyan).

Content correctness was verified end-to-end against measured inputs (3840×2160 HDR box):

1. Dump two raw WGC frames (R16G16B16A16Float) and read the actual float values per region (e.g. GDI white 255 → 3.5 float on this display ⇒ SDR white ≈ 280 nits, `1.0 = 80 nits` scene-referred scale confirmed).
2. Replicate the C# pipeline (float32 math + the PQ LUT, ST 2084 constants identical to FFmpeg `libavutil/csp.c`) in Python to compute the expected 10-bit Y/U/V codes for every region.
3. Full-decode the MP4 (clean, no slice errors), extract a mid-recording frame as `yuv420p10le`, and compare region averages to the expected codes: **all flat regions match exactly (Δ = 0)**; the counter text peak and the 24 px scan bar are within high-contrast edge-ringing tolerance, and the scan bar's x position moves frame-to-frame (2796 → 2925), confirming per-frame motion made it through encode → container → decode.
4. `ffprobe`/box forensics: 47 HEVC frames + AAC, `hvc1` + `colr`/`clli`, in-band VPS/SPS/PPS prefix; the only `ffmpeg -f null` complaints are the known FFmaster null-muxer VFR "non-monotonic dts" harness artifact (reproduces identically when remuxing FFmpeg's own output), not a container defect — stts/dts/pts are strictly monotonic in the file.

## Caveats / known limits

- **Window recording**: WGC window items don't composite overlapping windows (same limitation as screenshots); mid-recording window resize → content is padded/letterboxed to the initial size (v1).
- **Exclusive-fullscreen games** with `WDA_EXCLUDEFROMCAPTURE` are not capturable (documented screenshot limitation).
- **Region recording** is single-display; a selection spanning monitors is rejected with a toast.
- **File size** at 144/240 Hz × HDR10 10-bit is large; the CRF levels are the only knob (no bitrate cap in v1).
- **Throughput is readback-bound at 4K**: the `CanvasBitmap.GetPixelBytes()` CPU readback takes ~400 ms per 3840×2160 frame, so the pipeline sustains only ~2.3–2.6 fps at 4K (1080p-class displays are fine). GPU crop + readback/convert acceleration is planned work (the encoder DLLs consume raw CPU planes, so at minimum the readback copy needs to move off the CPU, ideally with the YUV conversion on a GPU shader).
- **Audio**: 192 k/384 k mix rates are unsupported by faac → those devices record without audio (log + degrade).
- 4 GB per-file cap (32-bit sample offsets).

## CI note

The 4 LGPL DLLs (`faac-2.dll`, `libx264-164.dll`, `libx265.dll`, `Starshot.Codecs.dll`) ship in `codecs/` (x64). The pr-check slim step (removes AI/codec binaries) must keep them, or recording silently degrades via the `Starshot_RecordingCodecUnavailable` toast.
