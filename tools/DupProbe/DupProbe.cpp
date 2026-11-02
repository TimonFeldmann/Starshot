// dup-probe - DXGI Desktop Duplication delivery-rate probe (x64, MSVC + Windows SDK)
//
// Measures the frame delivery rate of IDXGIOutputDuplication on the 4K output
// over a fixed window. Purpose: settle where this machine's ~12 fps HDR-state
// WGC delivery cap lives.
//   - duplication also ~12 fps  -> the cap is at the DWM composition level and
//     hits every desktop capture path (only in-game D3D Present hooks, the
//     OBS "game capture" approach, can bypass it)
//   - duplication at full rate  -> the cap is WGC-specific; duplication is a
//     viable full-rate capture backend (OBS's default display-capture path)
//
// Usage: dup-probe [seconds]   (default 10)
// Run it against CHANGING content (e.g. Starshot.exe --record-test fullscreen N)
// or it reports 0 fps (a static desktop delivers no frames).
//
// Prints per-second frame counts, the duplication buffer format (a 10-bit/16F
// format means the output is in its HDR state), and the total fps.
//
// Note: the modernized 10.0.26100 SDK renamed the creation entry point:
// IDXGIOutput1::CreateDuplication(Flags, **dup) is now
// DuplicateOutput(pDevice, **dup) - the device backs the frame buffers (the
// same headers add MapDesktopSurface for shared-memory frame access and drop
// IDXGIOutputDuplicationFrame entirely).

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <dxgi1_5.h>
#include <wrl/client.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

using Microsoft::WRL::ComPtr;

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")

// GDI monitor count, to cross-reference against the DXGI output count.
static int gMonCount = 0;
static BOOL CALLBACK MonEnumProc(HMONITOR, HDC, RECT*, LPARAM)
{
    gMonCount++;
    return TRUE;
}

static const char* FmtName(DXGI_FORMAT f)
{
    switch (f)
    {
        case DXGI_FORMAT_R8G8B8A8_UNORM:
            return "R8G8B8A8_UNORM (8-bit, SDR)";
        case DXGI_FORMAT_B8G8R8A8_UNORM:
            return "B8G8R8A8_UNORM (8-bit, SDR)";
        case DXGI_FORMAT_R10G10B10A2_UNORM:
            return "R10G10B10A2_UNORM (10-bit, HDR state)";
        case DXGI_FORMAT_R16G16B16A16_FLOAT:
            return "R16G16B16A16_FLOAT (16F, HDR state)";
        default:
            return "other format";
    }
}

struct OutRef
{
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutput1> out;
    int w = 0;
    int h = 0;
};

int main(int argc, char** argv)
{
    int seconds = (argc > 1) ? atoi(argv[1]) : 10;
    if (seconds < 2)
        seconds = 2;
    // "hdr" = request a 10-bit/16F duplication buffer via DuplicateOutput1
    // (IDXGIOutput5, the modern SDK's supported-format negotiation).
    bool wantHdr = (argc > 2 && strcmp(argv[2], "hdr") == 0);

    ComPtr<IDXGIFactory2> factory;
    void* factoryRaw = nullptr;
    HRESULT hr = CreateDXGIFactory2(0, __uuidof(IDXGIFactory2), &factoryRaw);
    if (FAILED(hr))
    {
        printf("[dup-probe] CreateDXGIFactory2 failed (0x%08X)\n", (unsigned)hr);
        return 1;
    }
    factory.Attach((IDXGIFactory2*)factoryRaw);

    // Enumerate every (adapter, output) pair; target = the 3840x2160 output,
    // else the largest.
    std::vector<OutRef> outs;
    for (UINT a = 0;; a++)
    {
        ComPtr<IDXGIAdapter1> ad;
        HRESULT hrAd = factory->EnumAdapters1(a, &ad);
        if (hrAd != S_OK)
            break;
        DXGI_ADAPTER_DESC adDesc;
        if (ad->GetDesc(&adDesc) == S_OK)
            wprintf(L"[dup-probe] adapter %u: %ls\n", a, adDesc.Description);
        else
            printf("[dup-probe] adapter %u (desc unavailable)\n", a);
        for (UINT o = 0;; o++)
        {
            ComPtr<IDXGIOutput> out;
            HRESULT hrOut = ad->EnumOutputs(o, &out);
            if (hrOut != S_OK)
            {
                printf("[dup-probe]   EnumOutputs(%u) = 0x%08X\n", o, (unsigned)hrOut);
                break;
            }
            ComPtr<IDXGIOutput1> out1;
            if (out.As(&out1) != S_OK)
                continue;
            DXGI_OUTPUT_DESC d;
            out1->GetDesc(&d);
            OutRef ref;
            ref.adapter = ad;
            ref.out = out1;
            ref.w = d.DesktopCoordinates.right - d.DesktopCoordinates.left;
            ref.h = d.DesktopCoordinates.bottom - d.DesktopCoordinates.top;
            printf("[dup-probe]   output %u: %dx%d\n", o, ref.w, ref.h);
            outs.push_back(ref);
        }
    }
    EnumDisplayMonitors(nullptr, nullptr, MonEnumProc, 0);
    printf(
        "[dup-probe] DXGI outputs: %zu | GDI monitors: %d\n",
        outs.size(),
        gMonCount
    );
    if (outs.empty())
    {
        printf("[dup-probe] no outputs found\n");
        return 1;
    }
    size_t target = 0;
    for (size_t i = 0; i < outs.size(); i++)
    {
        if (outs[i].w == 3840 && outs[i].h == 2160)
        {
            target = i;
            break;
        }
        if (outs[i].w * outs[i].h > outs[target].w * outs[target].h)
            target = i;
    }
    printf("[dup-probe] target: %dx%d\n", outs[target].w, outs[target].h);

    // A D3D11 device on the target output's adapter is required to create the
    // duplication (DuplicateOutput takes the device) and is used to QI the
    // frame buffers to ID3D11Texture2D for the format readout.
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    D3D_FEATURE_LEVEL featLevel{};
    hr = D3D11CreateDevice(
        outs[target].adapter.Get(),
        D3D_DRIVER_TYPE_UNKNOWN,
        0,
        0,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        &dev,
        &featLevel,
        &ctx
    );
    if (FAILED(hr))
    {
        printf(
            "[dup-probe] D3D11CreateDevice(adapter) failed (0x%08X); retrying "
            "with the default hardware adapter\n",
            (unsigned)hr
        );
        dev.Reset();
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            0,
            0,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &dev,
            &featLevel,
            &ctx
        );
        if (FAILED(hr))
        {
            printf(
                "[dup-probe] D3D11CreateDevice(default) failed (0x%08X)\n",
                (unsigned)hr
            );
            return 1;
        }
    }

    ComPtr<IDXGIOutputDuplication> dup;
    auto createDup = [&]() -> HRESULT
    {
        ComPtr<IDXGIOutputDuplication> d;
        if (wantHdr)
        {
            ComPtr<IDXGIOutput5> out5;
            if (outs[target].out.As(&out5) == S_OK)
            {
                const DXGI_FORMAT hdrFormats[] = {
                    DXGI_FORMAT_R10G10B10A2_UNORM,
                    DXGI_FORMAT_R16G16B16A16_FLOAT,
                };
                HRESULT h = out5->DuplicateOutput1(dev.Get(), 0, 2, hdrFormats, &d);
                if (SUCCEEDED(h))
                {
                    dup = d;
                    return h;
                }
                printf(
                    "[dup-probe] DuplicateOutput1 failed (0x%08X); trying default\n",
                    (unsigned)h
                );
            }
        }
        HRESULT h = outs[target].out->DuplicateOutput(dev.Get(), &d);
        if (SUCCEEDED(h))
            dup = d;
        return h;
    };

    HRESULT hrDup = createDup();
    if (hrDup == E_ACCESSDENIED)
    {
        printf(
            "[dup-probe] E_ACCESSDENIED: another desktop duplication is already "
            "active on this output (close other capture tools and retry)"
        );
        return 2;
    }
    if (FAILED(hrDup))
    {
        printf("[dup-probe] duplication creation failed (0x%08X)\n", (unsigned)hrDup);
        return 1;
    }
    printf(
        "[dup-probe] duplicating %dx%d for %d s (%s) ...\n",
        outs[target].w,
        outs[target].h,
        seconds,
        wantHdr ? "10-bit/16F preferred" : "default format"
    );

    unsigned long long t0ms = GetTickCount64();

    long long totalFrames = 0;
    int curSecond = -1;
    int secFrames = 0;
    char fmtStr[64] = "?";
    int bufW = 0, bufH = 0;
    int fmtKnown = 0;

    for (;;)
    {
        double elapsed = (double)(GetTickCount64() - t0ms) / 1000.0;
        if (elapsed >= seconds)
            break;

        DXGI_OUTDUPL_FRAME_INFO info;
        ComPtr<IDXGIResource> res;
        HRESULT r = dup->AcquireNextFrame(16, &info, &res);
        if (r == S_OK)
        {
            totalFrames++;
            secFrames++;
            if (res.Get() != nullptr && !fmtKnown)
            {
                ComPtr<ID3D11Texture2D> tex;
                if (res.As(&tex) == S_OK)
                {
                    D3D11_TEXTURE2D_DESC td;
                    tex->GetDesc(&td);
                    bufW = (int)td.Width;
                    bufH = (int)td.Height;
                    sprintf_s(fmtStr, "%s", FmtName(td.Format));
                }
                else
                {
                    sprintf_s(fmtStr, "shared-memory buffer (no D3D texture)");
                }
                fmtKnown = 1;
            }
            res.Reset();
            dup->ReleaseFrame();
        }
        else if (r == DXGI_ERROR_ACCESS_LOST)
        {
            printf(
                "[dup-probe] access lost at %.1f s; re-creating duplication\n",
                elapsed
            );
            dup.Reset();
            hr = createDup();
            if (FAILED(hr))
            {
                printf(
                    "[dup-probe] re-create failed (0x%08X); stopping\n",
                    (unsigned)hr
                );
                break;
            }
            continue;
        }
        // E_TIMEOUT and the rest: nothing delivered.

        int sec = (int)elapsed;
        if (sec != curSecond)
        {
            if (curSecond >= 0)
            {
                printf(
                    "[dup-probe] %2d s: %4d frames (cum %lld) | buf %dx%d %s\n",
                    curSecond,
                    secFrames,
                    totalFrames - secFrames,
                    bufW,
                    bufH,
                    fmtStr
                );
            }
            curSecond = sec;
            secFrames = 0;
        }
    }
    printf(
        "[dup-probe] %2d s: %4d frames | TOTAL %lld frames / %d s = %.1f fps\n",
        curSecond,
        secFrames,
        totalFrames,
        seconds,
        (double)totalFrames / seconds
    );
    return 0;
}
