# DupProbe

DXGI Desktop Duplication delivery-rate probe (see `DupProbe.cpp` header for the
full rationale). Answers where this machine's ~12 fps HDR-state WGC delivery
cap lives:

- **duplication also ~12 fps** → the cap is at the DWM composition level and
  hits every desktop capture path; only in-game D3D Present hooks (OBS
  "game capture") can bypass it.
- **duplication at full rate** → the cap is WGC-specific; duplication is a
  viable full-rate capture backend (OBS's "Display Capture (DXGI)" path).

Measured on this machine (4K, HDR state): **112 fps** with no WGC session
active (DWM composites the 4K at the full 120 Hz panel rate), **30 fps**
while a WGC session is active, vs WGC's **~12 fps** — the cap is WGC-specific
(full write-up in `performance.md`, "Desktop-duplication probe" section).
The buffer format is state-dependent: `R16G16B16A16_FLOAT` (real HDR pixels)
in the full EDR state, `B8G8R8A8` (SDR-toned) in others.

## Build

`pwsh tools/DupProbe/build.ps1` (needs the VS C++ workload, like HdrSignalHost).
Notes for the modernized 10.0.26100 SDK: the creation entry point is
`IDXGIOutput1::DuplicateOutput(pDevice, **dup)` (not the old `CreateDuplication`),
and `D3D11CreateDevice` requires `D3D11_SDK_VERSION` (7) as its SDK-version
argument (0 → E_INVALIDARG).

## Run

Against CHANGING content, or a static desktop reports whatever DWM is
compositing (in the HDR state the EDR flicker keeps every frame dirty, so a
static desktop still reports ~full panel rate):

```
.\tools\DupProbe\dup-probe.exe 10              # default (8-bit) duplication
.\tools\DupProbe\dup-probe.exe 10 hdr          # request 10-bit/16F via DuplicateOutput1
```

The `hdr` mode QIs the output to `IDXGIOutput5` and calls
`DuplicateOutput1` with `R10G10B10A2_UNORM` / `R16G16B16A16_FLOAT` in
`pSupportedFormats`. The buffer format is printed from the first delivered
frame (an 8-bit format = SDR-toned state, 10-bit/16F = HDR-state data).

Outputs auto-pick: the 3840x2160 output (by the duplication **buffer** size
after the first frame, not by `GetDesc` coordinates, which can be
DPI-scaled), else the largest. Run concurrently with a WGC capture to see
the cross-degradation (expect ~30 fps vs ~112 fps alone).
