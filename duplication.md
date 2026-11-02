# Desktop Duplication as a Capture Backend (design record, implementation deferred)

Status: feasibility + measurement record. **Not implemented** — Starshot records via
WGC (`WgcCaptureSource`). Everything below is measured or grounded in the current
codebase as of 2026-11-01. If this gets implemented later, start from the
checklist in §6.

## 1. Why this exists: the WGC cap on this machine

The 4K panel runs 120 Hz **in the HDR state** (OSD + Windows settings), but every
WGC measurement on this machine lands at ~11-13 fps, content-independent:

- 9+ runs: 11.07 / 10.93 / 12.80 (earlier), 11.0 / 11.1 (window + monitor probes),
  11.5 (2026-11-01 concurrent test), and the user's 80 fps game clip at **10.15 fps**.
- SDR state: the same pipeline delivers 57-59 fps on the 4K (60 Hz SDR panel mode).

A DXGI desktop-duplication probe (`tools/DupProbe`) against the same 4K in the HDR
state showed the throttle is **not** at DWM's composition level:

| Condition (4K, HDR state) | Delivered |
| --- | --- |
| Duplication, no WGC session, static desktop | **112 fps** (108-117 f/s, rock-stable) |
| Duplication + active WGC session (concurrent `--record-test fullscreen 8`) | 30.2 fps (242 f / 8 s) |
| WGC, same concurrent test (63 fps counter content) | 11.5 fps (92 f / 7.98 s) |

Reading: DWM composites the 4K at the full 120 Hz panel rate in the HDR state (the
EDR-state flicker documented in `performance.md` L1 keeps every delivered frame
dirty), and desktop duplication receives all of it (~112 fps). WGC receives only
~12 of those 120 frames/s. **The ~12 fps cap is specific to the DWM→WGC handoff in
the EDR-flicker state** — not a panel-link limit, not a DWM limit, not a
Starshot-pipeline limit. A concurrent WGC session also degrades duplication's
delivery to ~30 fps (mechanism unknown, empirical) — a duplication backend must
*replace* the WGC session, never coexist with it on the same output.

Cross-reference: `performance.md` → "HDR-state cap, all capture modes" and
"Desktop-duplication probe: the cap is WGC-specific" (same date).

## 2. Duplication buffer format is state-dependent

Measured from the first delivered frame (`tools/DupProbe` prints it):

| EDR state (observed) | Buffer format | HDR pixel data? |
| --- | --- | --- |
| Full EDR state (4K settled in HDR) | `R16G16B16A16_FLOAT` (16F) | **yes** |
| State observed right after the 4K re-appeared | `B8G8R8A8_UNORM` (8-bit, SDR-toned) | no |

The 8-bit state appeared in the two runs immediately after the 4K reconnected
(topology was unstable during the session — see §7); the 16F buffer followed in
subsequent runs. The state machine that switches the buffer format is not fully
understood. Consequences for a backend: detect the format at session start, and
if the user asked for HDR but the buffer is 8-bit, warn (SDR-toned data) or fall
back to WGC. `DuplicateOutput1` (IDXGIOutput5/6, `pSupportedFormats`) can request
`R10G10B10A2_UNORM` / `R16G16B16A16_FLOAT` explicitly — the probe's `hdr` mode
does this; the runtime still delivered whatever the EDR state dictated.

## 3. Modernized 10.0.26100 SDK deltas (all hit in practice building DupProbe)

- `IDXGIOutput1::CreateDuplication(Flags, **dup)` **no longer declared anywhere**.
  Replaced by `DuplicateOutput(IUnknown *pDevice, IDXGIOutputDuplication **)` —
  the D3D11 device backs the frame buffers. (Same method on IDXGIOutput2-6.)
- `IDXGIOutputDuplicationFrame` (and `AcquireResizeBuffer`) removed. Frame access
  is via QI of the `AcquireNextFrame` resource to `ID3D11Texture2D`, or the new
  `MapDesktopSurface`/`UnMapDesktopSurface` shared-memory pair on
  `IDXGIOutputDuplication`.
- `DuplicateOutput1(pDevice, Flags, SupportedFormatsCount, pSupportedFormats,
  **dup)` on IDXGIOutput5/6: supported-format negotiation. `DXGI_OUTDUPL_FLAG`
  currently defines only `DXGI_OUTDUPL_COMPOSITED_UI_CAPTURE_ONLY = 1`.
- `D3D11CreateDevice` rejects SDK-version **0** (E_INVALIDARG at runtime) — pass
  `D3D11_SDK_VERSION` (7).
- `D3D11_FEATURE_LEVEL` is gone; the type is `D3D_FEATURE_LEVEL`, a scoped enum
  (`= 0` does not compile — value-initialize).
- `CreateDXGIFactory2(UINT, const IID&, void**)` — the out param is `void**`
  (attach via `factory.Attach((IDXGIFactory2*)raw)`).
- `DXGI_OUTDUPL_FRAME_INFO` no longer has `SourceFrameId` (fields:
  `LastPresentTime`, `LastMouseUpdateTime`, `AccumulatedFrames`,
  `RectsCoalesced`, `ProtectedContentMaskedOut`, `PointerPosition`,
  `TotalMetadataBufferSize`, `PointerShapeBufferSize`).
- `DXGI_OUTPUT_DESC` has no `IsPrimary`; and `DesktopCoordinates` can be
  **DPI-scaled logical**: a 4K at 150 % DPI enumerated as 2560×1440 while its
  duplication buffer was the physical 3840×2160. **Target outputs by the
  duplication buffer size (first frame's texture desc), not by GetDesc
  coordinates.**
- Do **not** define `WINAPI_FAMILY` (desktop is the default; this SDK uses
  `WINAPI_FAMILY_DESKTOP_APP = 100`, and `/DWINAPI_FAMILY=1` is fatal C1189).

Working reference implementation: `tools/DupProbe/` (probe + `build.ps1` + its
README; the `hdr` mode exercises `DuplicateOutput1`).

## 4. What OBS does (the original question, answered)

- **Game Capture** (borderless *and* exclusive fullscreen) is **not WGC**: OBS
  injects a hook DLL into the game process and hooks the game's D3D11/D3D12
  `Present`, grabbing the back buffer at the game's own present rate with native
  HDR10 pixels — it bypasses DWM/WGC/duplication entirely. This is why OBS
  records an 80 fps HDR game at 80 fps while every desktop-level API on this
  machine is throttled. (Nuance from OBS issue #13719: with NVIDIA Frame
  Generation, game capture and duplication see only the *original* frames; only
  the WGC method captures the frame-generated output.)
- **Display Capture** (modern OBS) offers three methods: **WGC** (the newer
  backend; "Automatic" prefers it on recent Windows), **DXGI Desktop
  Duplication** (the long-standing default), and Automatic. On this machine the
  WGC method would hit the same ~12 fps cap Starshot hits; the duplication method
  gets 30-112 fps (8-bit in some EDR states, 16F in the full EDR state).

## 5. Starshot backend design (feasible; deferred)

### 5.1 The seam is clean

The recording pipeline is already backend-agnostic from `OnFrame` onward:

- `WgcCaptureSource` (`src/Starshot/Features/Recording/WgcCaptureSource.cs`) does
  one job: frames (texture + timestamp) → bounded `Channel` (drop-oldest) → pump
  task. `VideoRecordService.OnFrame` reads the texture into the session's
  `PixelBuf` (`CanvasBitmap.CreateFromDirect3D11Surface(CanvasDevice.GetSharedDevice(),
  frame.Surface, 96)` + `GetPixelBytes`), runs `FrameProcessor.ConvertHdr/ConvertSdr`,
  and hands ping-pong plane sets to the encode/raw threads.
- The WGC HDR path **already consumes `R16G16B16A16Float`**
  (`VideoRecordService` ~L334) — the *same* format the duplication buffer delivers
  in the full EDR state. `ConvertHdr` (16F → P010, PQ LUT, CLLI stats) carries
  over as-is, **pending the colorimetry validation in §6**.
- The 8-bit EDR state (`B8G8R8A8`) maps onto the existing SDR path (a
  BGRA→RGBA channel swap; WGC SDR is `R8G8B8A8`).
- Create the duplication's D3D11 device **from `CanvasDevice.GetSharedDevice()`**
  (the same device the frame pool uses) and the buffers land on the device the
  readback already uses — no extra interop.

### 5.2 Work items

1. **`DuplicationCaptureSource`** (sibling of `WgcCaptureSource`, same
   pump/channel contract): P/Invoke DXGI — factory → output for the selected
   monitor → QI `IDXGIOutput1`/`5` → `D3D11CreateDevice` (shared CanvasDevice) →
   `DuplicateOutput` → `AcquireNextFrame`/`ReleaseFrame` loop.
   `E_ACCESS_LOST` → re-create; `E_ACCESSDENIED` → user-facing toast (another
   capture tool holds the output). All modernized-SDK quirks per §3; port the
   logic from DupProbe. ~400-600 lines of C# interop (keep it fully managed —
   the codebase already uses Vanara.PInvoke).
2. **Generalize the frame contract**: `OnFrame(Direct3D11CaptureFrame)` → a small
   common frame type (texture reference + pixel format + QPC timestamp).
   Duplication timestamps from `LastPresentTime` (100 ns QPC units) or a session
   clock fallback. One-line change on the WGC side.
3. **GPU crop for the region target** (§5.4): `CopySubresourceRegion` from the
   duplication buffer into a crop-sized texture on the same device; read back the
   small texture. Everything downstream (readback, conversion, raw-file size in
   post-encode mode, encoder cost) then scales with the crop, not the full 4K.
4. **Settings row** in `RecordingSetting.xaml`: "Capture engine: Windows
   Graphics Capture (default) / Desktop duplication", following the existing
   ComboBox-row pattern + `AppConfig` property + i18n (en/zh-CN/ja-JP,
   `Lang.Designer.cs` by hand).
5. **Colorimetry validation** (de-risking step, no app code — see §6.1).
6. **Tests**: `--record-test` gains a backend flag for A/B rate checks; the HDR
   suite grows duplication-backend rows with a fresh expectation table (the
   WGC-path `MachineExpectation` values do not transfer).

Rough effort: **2-4 days** focused work.

### 5.3 Target support

Duplication is strictly **per-output**: it delivers the whole monitor's desktop
surface. There is no region- or window-subscription in the API (the dirty
rectangles are change-notification metadata, not a capture-area selector; the
shared-memory `MapDesktopSurface` path also covers the whole output).

| Target | Duplication backend | Notes |
| --- | --- | --- |
| Full monitor | direct | the 12 → 60-112 fps win on this machine |
| Fixed region | GPU crop (§5.2 item 3) | constant crop rect; cheaper than full-monitor downstream |
| Window (moving) | **not in the first cut** | track the rect per frame (`GetWindowRect` + `DWMWA_EXTENDED_FRAME_BOUNDS` → physical px via the window's DPI scale), crop per frame; only while the window stays fully inside one output |
| Window (WGC) | stays WGC | WGC per-window capture is semantically correct: it captures the window's own composed surface (even occluded) and works cross-GPU |

**Semantic caveat for any duplication-based window mode**: it records *what is
visible on the panel* — an overlapping notification/taskbar/other window is what
you get (occlusion baked in); a minimized/hidden window records whatever is
underneath; a cross-monitor window cannot be cropped from one output (fall back
to WGC). For most recording use "visible" is what the user wants, but it differs
from WGC window capture.

### 5.4 The GPU-crop path, concretely

```
duplication buffer (3840×2160, 16F = 66.3 MB)
  → ID3D11DeviceContext::CopySubresourceRegion(dst = crop-sized texture, srcRect)
  → read back the small texture into PixelBuf (existing path)
```

A `CopySubresourceRegion` at 4K is sub-millisecond on a high-end dGPU. For a small
region the CPU readback, the raw buffer, and the encoder all shrink with the
crop — better than full-monitor duplication.

### 5.5 Constraints / UX

- **One duplication consumer per output** — `E_ACCESSDENIED` when another tool
  holds it; toast, don't crash.
- **Never run a WGC session and a duplication on the same output at once** —
  measured 112 → 30 fps degradation. The backend replaces, not coexists.
- **8-bit EDR state + HDR requested** → warn (SDR-toned pixels) or auto-fall
  back to WGC.
- **Cross-GPU**: the duplication device must be on the output's adapter. The
  shared CanvasDevice is the display GPU on normal machines; on multi-GPU boxes
  it can fail → toast (WGC is cross-GPU, which is one reason it stays the
  default).
- **Exclusive-fullscreen games** are uncapturable by *both* backends (they never
  reach DWM) — that remains the in-game-hook (OBS Game Capture) story.
- **Default stays WGC**: on a healthy machine both backends run at full rate, so
  the duplication option is primarily the workaround for EDR-flicker machines
  (this one: 12 → 60-112 fps on the 4K desktop in the HDR state) and a fallback
  where WGC misbehaves.

## 6. Open items / pre-implementation checklist

1. **16F colorimetry (the one real unknown).** `ConvertHdr` encodes the WGC 16F
   pool's colorimetry assumptions (the WgcTo2020/PQ-LUT semantics measured in
   `performance.md`). The duplication 16F buffer's transfer function / primaries
   / EDR scale are **not yet verified**. Validate before building: render known-nit
   flat fields with `tools/HdrSignalHost`, capture them through a buffer-dump
   extension of DupProbe, and check against the analytic ST 2084 + BT.2020 ground
   truth (the HDR suite's method, ±6 codes). If they match, the conversion path
   is free; if not, the measured delta defines a small LUT adjustment.
2. **Win2D raw-texture readback.** `OnFrame` uses `CanvasBitmap.CreateFromDirect3D11Surface`
   with a WGC `Direct3D11Surface`; the duplication side has a raw `ID3D11Texture2D`.
   Verify the Win2D overload taking a raw D3D11 texture exists in the 1.8
   projection (one `WinmdDump` run, per the projection rules in AGENTS.md);
   fallback = same-device `CopyResource` into a pool-owned texture.
3. **8-bit vs 16F state transitions**: when exactly does DWM switch the
   duplication buffer format (observed: 8-bit right after the 4K reconnected,
   16F later)? The detection at session start (§5.5) works regardless, but the
   UX copy should match the real behavior.
4. **`DuplicateOutput1` negotiation**: the runtime delivered the EDR-state format
   whether or not formats were requested (the probe's `hdr` mode ran before the
   16F state settled in, so it hasn't been tested as a *force*); if an explicit
   16F request can pin the buffer in the 8-bit state, that's worth a follow-up
   probe.
5. **Concurrent WGC degradation (112 → 30 fps)**: empirically established,
   mechanism unknown. Only matters if both backends ever run at once — by
   design they don't, so no action beyond the guard.

## 7. Session-topology note (2026-11-01)

The 4K's enumeration was unstable during the test session: at ~22:47 the outputs
were 2560×1440 (DPI-scaled coordinates of the 4K) + the 2048×1152 virtual display,
with the 4K's duplication buffer already 3840×2160 (8-bit, 91 fps static); by
~22:58 the outputs were 3840×2160 + 2560×1440 and the virtual display was gone;
GDI monitor count read 2 throughout. The 4K also dropped out of the active
configuration entirely at some point and re-appeared within ~10 minutes (the
usual suspect is a DP link-training drop during the HDR toggle). Read the
early 8-bit / 91 fps data points as "the 4K in a post-reconnection, not-yet-full
EDR state", and target outputs by duplication buffer size (§3) so the probe is
immune to the coordinate/DPI shifts.

## 8. References

- `tools/DupProbe/` — the rate/format probe (build + usage in its README).
- `performance.md` — "HDR-state cap, all capture modes", "Desktop-duplication
  probe: the cap is WGC-specific", "OBS's high-fps HDR capture" (2026-11-01).
- `src/Starshot/Features/Recording/WgcCaptureSource.cs`,
  `VideoRecordService.cs` — the current pipeline and the seam (§5.1).
- `src/Starshot/Features/Setting/RecordingSetting.xaml` — where the settings row
  goes.
- OBS: game-capture hook + display-capture methods (WGC / DXGI duplication /
  Automatic); issue #13719 (NVIDIA Frame Generation nuance).
