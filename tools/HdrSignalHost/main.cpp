// Headless HDR10 signal host (probe tool; see build.ps1).
//
// Usage: hdr-signal.exe [targetWpx targetHpx [periodSeconds [t0File [nits [firstPhaseSeconds
//                          [maxSeconds [pauseFlagFile]]]]]]]
//
// Renders the four flat HDR10 fields red/green/blue/yellow in a borderless D3D11 window that
// covers a target monitor, cycling every periodSeconds (default 6 s; loops forever unless
// maxSeconds > 0, in which case the host exits on its own after maxSeconds of non-paused
// time). Each lit
// channel carries the ST 2084 PQ code for `nits` (default 10000 - the full-scale ground-truth
// value; lower nits gives midtone fields, e.g. 5000 or 240 nits, for pipeline-fidelity checks).
//
// Why C++: a player's video output can bind to a different monitor than its window, and the
// earlier hand-rolled C# D3D11/DXGI interop proved too fragile (IIDs/vtable slots are easy to
// get wrong). Using the real Windows SDK headers lets the compiler generate the correct
// vtables/IIDs. A windowed D3D11 10-bit flip-model swap chain follows the window.
//
// Monitor targeting is by PHYSICAL resolution, not logical coordinates: EnumDisplaySettings
// reports the panel's physical mode (3840x2160 for the 4K), which is not virtualized by DPI
// awareness, and GetMonitorInfo gives the rect in the calling process's own coordinate
// system. Creating the window on that rect lands it on the target panel regardless of the
// process's DPI awareness, so no per-monitor-v2 setup is required (and none is trusted).
//
// The swap chain is R10G10B10A2 with color space RGB_FULL_G2084_NONE_P2020 (HDR10/BT.2020/PQ).
// WGC of the monitor then delivers HDR (or SDR/tone-mapped) values, which the Starshot
// recording pipeline must faithfully convert to P010.
//
// On-screen label: a top strip (inside the panel's top quarter - OUTSIDE the test's central
// crop, so it cannot affect the code verification) shows the row identity (resolution + nits),
// the current color phase, the seconds left in the phase, and the total elapsed time, so a
// human operator can tell at a glance what is being played and which row is on the panel.
//
// Console protocol:
//   [hdr-signal] monitor[i] <device> <physWxH> rect=(l,t)-(r,b)
//   [hdr-signal] target 3840x2160 -> <device>
//   [hdr-signal] window 0x... at (l,t) WxH
//   [hdr-signal] d3d11 featureLevel=0x...
//   [hdr-signal] colorspace hr=0x... (0 = HDR10/PQ set)
//   [hdr-signal] label on (top strip <W>x<H>)
//   [hdr-signal] t+<s>s <COLOR>
//   [hdr-signal] t0 written: <path>
// The t0 file (default %TEMP%\starshot\hdr_signal_t0.txt) carries the unix-ms t0 + period so an
// orchestrator can poll for it and align recordings to the color phases.
//
// Pause flag (last arg, optional): while that file exists, the color phases and the label
// countdown FREEZE on the current color (the window keeps presenting the frozen frame, which
// also keeps a WGC capture session alive), and the maxSeconds self-exit clock holds. The
// HDR test's Space-pause creates/removes this file.

#include <d3d11_1.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <shcore.h>
#include <ShellScalingApi.h>
#include <wrl/client.h>
#include <windows.h>

#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
    // Color order = test order: GREEN first, then RED, BLUE, YELLOW. The first phase is longer
    // (firstPhaseSec) because a recording's WGC session init eats the first seconds of a
    // capture, so the first color gets the margin. r/g/b are per-channel "lit" flags (linear
    // light, yellow = red + green, so a field's channels add in light).
    struct Color
    {
        const char* name;
        float r, g, b, a;
    };
    const Color kColors[] = {
        { "GREEN", 0.0f, 1.0f, 0.0f, 1.0f },
        { "RED", 1.0f, 0.0f, 0.0f, 1.0f },
        { "BLUE", 0.0f, 0.0f, 1.0f, 1.0f },
        { "YELLOW", 1.0f, 1.0f, 0.0f, 1.0f },
    };
    const int kColorCount = sizeof(kColors) / sizeof(kColors[0]);

    // ST 2084 PQ OETF (same constants as FrameProcessor.BuildPqLut): linear-light fraction
    // (1.0 = 10000 nits) -> PQ code fraction 0..1. A G2084/P2020 swap chain stores PQ codes,
    // so clearing a lit channel with OETF(nits/10000) makes the compositor show `nits`.
    float PqCode(float lin01)
    {
        constexpr double C1 = 3424.0 / 4096.0;
        constexpr double C2 = 32.0 * 2413.0 / 4096.0;
        constexpr double C3 = 32.0 * 2392.0 / 4096.0;
        constexpr double M1 = 0.25 * 2610.0 / 4096.0;
        constexpr double M2 = 128.0 * 2523.0 / 4096.0;
        double y = lin01;
        if (y <= 0.0)
            return 0.0f;
        if (y > 1.0)
            y = 1.0;
        double t = pow(y, M1);
        double pq = pow((C1 + C2 * t) / (1.0 + C3 * t), M2);
        if (pq < 0.0)
            pq = 0.0;
        if (pq > 1.0)
            pq = 1.0;
        return (float)pq;
    }

    // Pass-through WndProc. IMPORTANT: call DefWindowProcW directly - do NOT go through a global
    // function pointer (a WndProc that invokes a cached global WNDPROC makes CreateWindowEx fail
    // with error 2 in this environment; calling DefWindowProcW directly succeeds). The process is
    // stopped externally (the orchestrator kills it), so no stop flag is needed here.
    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // Register the window class once (before any other Win32 work).
    bool RegisterWindowClass(HINSTANCE hInst)
    {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = WndProc;
        wc.hInstance = hInst;
        wc.lpszClassName = L"StarshotHdrSignalClass";
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        ATOM atom = RegisterClassExW(&wc);
        if (!atom)
        {
            fprintf(stderr, "[hdr-signal] RegisterClassEx failed: %lu\n", GetLastError());
            return false;
        }
        return true;
    }

    // Create a borderless popup window at (x,y) with client size (w,h) (client == window).
    // Tries the full size first; on failure falls back to smaller sizes (diagnostic) so the
    // signal still lands on the target monitor. Returns the created window or nullptr.
    HWND CreatePopupWindow(HINSTANCE hInst, const wchar_t* title, int x, int y, int w, int h)
    {
        int tries[3][2] = { { w, h }, { w / 2, h / 2 }, { 320, 200 } };
        for (auto& t : tries)
        {
            HWND hwnd = CreateWindowExW(0, L"StarshotHdrSignalClass", title, WS_POPUP, x, y, t[0],
                                        t[1], nullptr, nullptr, hInst, nullptr);
            if (hwnd)
            {
                if (t[0] != w || t[1] != h)
                    fprintf(stderr, "[hdr-signal] WARNING: full %dx%d window failed; using %dx%d\n",
                            w, h, t[0], t[1]);
                ShowWindow(hwnd, SW_SHOW);
                return hwnd;
            }
            fprintf(stderr, "[hdr-signal] CreateWindowEx %dx%d failed: %lu\n", t[0], t[1],
                    GetLastError());
        }
        return nullptr;
    }

    // Make the window topmost (so it covers the whole target panel and the compositor shows it)
    // and bring it to the foreground (so the recording's FullScreen target resolves to its
    // monitor). A simulated ALT press primes the input system so SetForegroundWindow bypasses
    // the foreground lockout. Safe to call repeatedly.
    void ForegroundAndTopmost(HWND hwnd, int x, int y, int w, int h)
    {
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_NOZORDER);
        INPUT inputs[2] = {};
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_MENU;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_MENU;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, sizeof(INPUT));
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
    }

    void PumpMessages()
    {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    struct MonitorInfo
    {
        std::wstring device;
        HMONITOR hmon = nullptr;
        int physW = 0, physH = 0; // physical (EnumDisplaySettings) resolution
        int dpi = 0;              // effective DPI (GetDpiForMonitor, not virtualized)
        RECT rect{};              // rect in the calling process's coordinate system
        bool isTarget = false;
    };

    struct EnumCtx
    {
        int targetW, targetH;
        std::vector<MonitorInfo>* out;
    };
    static EnumCtx* g_enumCtx = nullptr;

    BOOL CALLBACK MonitorEnumCallback(HMONITOR hMon, HDC, LPRECT, LPARAM)
    {
        MONITORINFOEXW mi{};
        mi.cbSize = sizeof(mi);
        if (!GetMonitorInfoW(hMon, &mi))
            return TRUE;
        MonitorInfo m;
        m.device = mi.szDevice;
        m.hmon = hMon;
        m.rect = mi.rcMonitor;
        UINT dpiX = 0, dpiY = 0;
        if (SUCCEEDED(GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, &dpiX, &dpiY)))
            m.dpi = (int)dpiX;
        DEVMODEW dm{};
        dm.dmSize = sizeof(dm);
        if (EnumDisplaySettingsW(mi.szDevice, ENUM_CURRENT_SETTINGS, &dm))
        {
            m.physW = (int)dm.dmPelsWidth;
            m.physH = (int)dm.dmPelsHeight;
        }
        m.isTarget = (m.physW == g_enumCtx->targetW && m.physH == g_enumCtx->targetH);
        g_enumCtx->out->push_back(m);
        return TRUE;
    }

    // Enumerate every monitor: physical resolution (EnumDisplaySettings, not virtualized by DPI
    // awareness) + rect (GetMonitorInfo, in this process's coordinate system). Mark the one whose
    // physical resolution matches (targetW x targetH).
    std::vector<MonitorInfo> EnumerateMonitors(int targetW, int targetH)
    {
        std::vector<MonitorInfo> out;
        EnumCtx ctx{ targetW, targetH, &out };
        g_enumCtx = &ctx;
        EnumDisplayMonitors(nullptr, nullptr, MonitorEnumCallback, 0);
        g_enumCtx = nullptr;
        return out;
    }

    // Two-line label text: "<W>x<H> @ <nits> nits" (the row identity) and
    // "<COLOR> | <n>s left | t+<total>s". Changes at most once per second, so the label
    // texture is re-rendered at most once per second.
    std::string MakeLabel(int w, int h, int nits, const char* color, double elapsedS,
                          double firstPhaseSec, double period)
    {
        double left = elapsedS < firstPhaseSec
                          ? firstPhaseSec - elapsedS
                          : period - fmod(elapsedS - firstPhaseSec, period);
        char buf[160];
        snprintf(buf, sizeof(buf), "%dx%d @ %d nits\n%s | %ds left | t+%ds", w, h, nits, color,
                 (int)std::ceil(left), (int)elapsedS);
        return buf;
    }

    // On-screen label: the top strip (inside the panel's top quarter - OUTSIDE the test's
    // central crop, so it cannot affect the code verification) is drawn with a small 5x7
    // bitmap font: each glyph's set pixels are scaled into a DYNAMIC D3D11 texture (white
    // fill + dark outline, ~78% opaque), which the label quad's pixel shader alpha-blends
    // over the field (out = tex.rgb + field*(1-tex.a)).
    //
    // Why a bitmap font: GDI text is a silent no-op in this environment (TextOut returns
    // TRUE but never touches a DIB), and this SDK's D2D1/DWrite headers lack the interop a
    // TrueType path would need (no CreateSharedResource / D2D1 software device / DWrite CPU
    // rasterizer), so the font is generated in pure C++ and uploaded on each label change.
    //
    // 5x7 bitmap font. Each glyph is 7 rows of 5 columns; column c (0 = leftmost) of a row is
    // bit (4-c) of the row byte. kFontData lists the glyphs in the order used by Glyph7.
    constexpr uint8_t kFontData[] = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // ' '
        0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E, // '0'
        0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E, // '1'
        0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F, // '2'
        0x0E, 0x11, 0x01, 0x06, 0x01, 0x11, 0x0E, // '3'
        0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02, // '4'
        0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E, // '5'
        0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E, // '6'
        0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08, // '7'
        0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E, // '8'
        0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C, // '9'
        0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, // 'A'
        0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E, // 'B'
        0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E, // 'C'
        0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C, // 'D'
        0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F, // 'E'
        0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10, // 'F'
        0x0E, 0x11, 0x10, 0x1B, 0x11, 0x11, 0x0E, // 'G'
        0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11, // 'H'
        0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E, // 'I'
        0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C, // 'J'
        0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11, // 'K'
        0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F, // 'L'
        0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11, // 'M'
        0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11, // 'N'
        0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E, // 'O'
        0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10, // 'P'
        0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D, // 'Q'
        0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11, // 'R'
        0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E, // 'S'
        0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, // 'T'
        0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E, // 'U'
        0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04, // 'V'
        0x11, 0x11, 0x11, 0x15, 0x15, 0x0A, 0x0A, // 'W'
        0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11, // 'X'
        0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04, // 'Y'
        0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F, // 'Z'
        0x0E, 0x11, 0x1F, 0x17, 0x1F, 0x10, 0x0E, // '@'
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, // '|'
        0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00, // '+'
        0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x04, // '.'
        0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00, // '-'
    };
    // Map a (case-insensitive) character to its 7-byte glyph in kFontData.
    const uint8_t* Glyph7(char c)
    {
        if (c >= 'a' && c <= 'z')
            c = char(c - 'a' + 'A');
        int idx;
        if (c == ' ')
            idx = 0;
        else if (c >= '0' && c <= '9')
            idx = 1 + (c - '0');
        else if (c >= 'A' && c <= 'Z')
            idx = 11 + (c - 'A');
        else if (c == '@')
            idx = 37;
        else if (c == '|')
            idx = 38;
        else if (c == '+')
            idx = 39;
        else if (c == '.')
            idx = 40;
        else if (c == '-')
            idx = 41;
        else
            idx = 0; // unknown -> blank
        return kFontData + idx * 7;
    }

    struct Label
    {
        int w = 0, h = 0; // strip size: (window width x strip height)
        ComPtr<ID3D11Device> dev;
        ComPtr<ID3D11Texture2D> tex;
        ComPtr<ID3D11ShaderResourceView> srv;
        std::vector<DWORD> pix; // the strip's R8G8B8A8 pixels (w*h), stored as 0xAARRGGBB
        std::string last;

        bool Init(const ComPtr<ID3D11Device>& device, int winW, int winH)
        {
            dev = device;
            w = winW;
            h = (winH + 13) / 14; // ~154 px at 2160p, ~103 px at 1440p (well inside top quarter)
            pix.assign((size_t)w * h, 0);
            return true; // the texture is (re)created on each Blit, not here
        }

        // `text` carries a '\n' between the two lines (row identity / phase + countdown).
        // Renders the bitmap font into the DYNAMIC texture (white fill + dark outline); the
        // label quad samples it on the following frame.
        void Blit(const std::string& text, ID3D11DeviceContext* context)
        {
            last = text;
            size_t nl = text.find('\n');
            std::string l1 = text.substr(0, nl);
            std::string l2 = (nl != std::string::npos) ? text.substr(nl + 1) : "";

            // Two 7-tall lines, 3 font rows apart; each char advances 8 font columns.
            const int kGlyphW = 5, kGlyphH = 7, kGap = 3, kCharW = 8;
            int rows = kGlyphH * 2 + kGap;
            int longest = (int)(l1.size() > l2.size() ? l1.size() : l2.size());
            int cols = longest * kCharW;
            if (cols < 8)
                cols = 8;
            std::vector<uint8_t> ink((size_t)rows * cols, 0);
            auto mark = [&](const std::string& line, int rowOff) {
                for (int i = 0; i < (int)line.size() && i * kCharW + kGlyphW <= cols; i++)
                {
                    const uint8_t* g = Glyph7(line[i]);
                    for (int r = 0; r < kGlyphH; r++)
                        for (int c = 0; c < kGlyphW; c++)
                            if (g[r] & (1u << (kGlyphW - 1 - c)))
                                ink[(size_t)(rowOff + r) * cols + (i * kCharW + c)] = 1;
                }
            };
            mark(l1, 0);
            mark(l2, kGlyphH + kGap);
            // Outline = the 8-neighborhood of the ink, minus the ink itself.
            std::vector<uint8_t> dilated = ink;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    if (ink[(size_t)r * cols + c])
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int rr = r + dr, cc = c + dc;
                                if (rr >= 0 && rr < rows && cc >= 0 && cc < cols)
                                    dilated[(size_t)rr * cols + cc] = 1;
                            }

            // Scale the font grid to the strip height; center vertically, left margin.
            int scale = (h - 4) / rows;
            if (scale < 1)
                scale = 1;
            int originY = (h - rows * scale) / 2;
            int originX = 6 * scale;

            // ~78% opaque white (fill) and black (outline) so the label reads on any field
            // without pushing the strip to a brighter HDR level.
            const uint32_t kWhite = 0xC8FFFFFFu;
            const uint32_t kBlack = 0xC8000000u;
            for (size_t i = 0; i < pix.size(); i++)
                pix[i] = 0;
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    bool inky = ink[(size_t)r * cols + c] != 0;
                    bool outline = dilated[(size_t)r * cols + c] != 0 && !inky;
                    if (!inky && !outline)
                        continue;
                    uint32_t val = inky ? kWhite : kBlack;
                    int x0 = originX + c * scale;
                    int y0 = originY + r * scale;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        int Y = y0 + dy;
                        if (Y < 0 || Y >= h)
                            continue;
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int X = x0 + dx;
                            if (X >= 0 && X < w)
                                pix[(size_t)Y * w + X] = val;
                        }
                    }
                }
            // Upload: a fresh DEFAULT texture with the pixels as initial data, and a new SRV
            // bound in place of the old one (at most one per second; the previous texture
            // stays alive until the GPU finishes sampling it).
            //
            // Why not a DYNAMIC texture: in this environment Map(D3D11_MAP_WRITE) on a
            // DYNAMIC texture returns E_INVALIDARG and UpdateSubresource is a silent no-op
            // (readback shows zero pixels), so the texture is recreated on each change.
            D3D11_TEXTURE2D_DESC td{};
            td.Width = (UINT)w;
            td.Height = (UINT)h;
            td.MipLevels = 1;
            td.ArraySize = 1;
            td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            td.SampleDesc.Count = 1;
            td.Usage = D3D11_USAGE_DEFAULT;
            td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA sd{};
            sd.pSysMem = pix.data();
            sd.SysMemPitch = (UINT)(w * 4);
            ComPtr<ID3D11Texture2D> newTex;
            ComPtr<ID3D11ShaderResourceView> newSrv;
            HRESULT hrT = dev->CreateTexture2D(&td, &sd, newTex.ReleaseAndGetAddressOf());
            HRESULT hrS = SUCCEEDED(hrT)
                              ? dev->CreateShaderResourceView(newTex.Get(), nullptr,
                                                              newSrv.ReleaseAndGetAddressOf())
                              : hrT;
            if (SUCCEEDED(hrS))
            {
                tex = newTex;
                srv = newSrv;
                context->PSSetShaderResources(0, 1, srv.GetAddressOf());
            }
            else
                fprintf(stderr, "[hdr-signal] label: texture recreate failed (tex=0x%08lX "
                                "srv=0x%08lX)\n",
                        (unsigned long)hrT, (unsigned long)hrS);
        }
    };

    // Shaders for the label quad (compiled at startup; d3dcompiler.lib). The vertex buffer
    // carries uv 0..1 and the VS scales it to pixels; the PS samples the label texture.
    // (HLSL is C-like: the cbuffer/struct declarations need trailing semicolons.)
    constexpr char kVsHlsl[] =
        "struct V { float2 P : POSITION; float2 UV : TEXCOORD0; };\n"
        "struct O { float4 Pos : SV_Position; float2 UV : TEXCOORD0; };\n"
        "O main(V v) {\n"
        "  O o;\n"
        "  o.Pos = float4(v.P, 0.0, 1.0); // vertices are pre-computed in NDC (no cbuffer)\n"
        "  o.UV = v.UV;\n"
        "  return o;\n"
        "}\n";
    constexpr char kPsHlsl[] =
        "Texture2D T : register(t0);\n"
        "SamplerState Smp : register(s0);\n"
        "float4 main(float4 p : SV_Position, float2 uv : TEXCOORD0) : SV_Target\n"
        "{ return T.Sample(Smp, uv); }\n";

    void WriteT0(const std::string& path, double period)
    {
        auto slash = path.find_last_of("/\\");
        if (slash != std::string::npos && slash > 0)
            CreateDirectoryA(path.substr(0, slash).c_str(), nullptr);
        auto now = std::chrono::duration_cast<std::chrono::milliseconds>(
                       std::chrono::system_clock::now().time_since_epoch())
                       .count();
        // Atomic publish: write a temp file, then rename over the target (MoveFile is atomic
        // on NTFS). A plain create-then-write leaves a window where the file exists but is
        // empty/partial, which the orchestrator's polling loop could read.
        std::string tmp = path + ".tmp";
        FILE* f = fopen(tmp.c_str(), "w");
        if (!f)
            return;
        fprintf(f, "%lld %g\n", static_cast<long long>(now), period);
        fclose(f);
        MoveFileA(tmp.c_str(), path.c_str());
    }

    // Pause-flag check (one cheap query; the render loop polls it at ~10 Hz).
    bool FileExists(const std::string& path)
    {
        DWORD attr = GetFileAttributesA(path.c_str());
        return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
    }
} // namespace

int main(int argc, char** argv)
{
    int targetW = 3840, targetH = 2160; // default: the 4K panel
    double period = 6.0;
    double firstPhaseSec = 10.0; // GREEN (first color) gets a longer first phase
    int nits = 10000;
    double maxSec = 0.0; // 0 = run forever (the orchestrator kills the process)
    std::string pauseFlagFile; // existence = freeze the phase clock (the test's Space-pause)
    std::string t0File = std::string(getenv("TEMP") ? getenv("TEMP") : "") +
                         "\\starshot\\hdr_signal_t0.txt";
    if (argc > 1) targetW = atoi(argv[1]);
    if (argc > 2) targetH = atoi(argv[2]);
    if (argc > 3) period = atof(argv[3]);
    if (argc > 4) t0File = argv[4];
    if (argc > 5) nits = atoi(argv[5]);
    if (argc > 6) firstPhaseSec = atof(argv[6]);
    if (argc > 7) maxSec = atof(argv[7]);
    if (argc > 8) pauseFlagFile = argv[8];
    if (nits < 1) nits = 1;
    if (nits > 10000) nits = 10000; // PQ reference peak (values above are indistinguishable)
    printf(
        "[hdr-signal] nits=%d (per-lit-channel PQ code %.5f), order GREEN,RED,BLUE,YELLOW "
        "(first phase %.0fs, rest %.0fs)\n",
        nits,
        PqCode(nits / 10000.0f),
        firstPhaseSec,
        period
    );
    if (!pauseFlagFile.empty())
        printf("[hdr-signal] pause flag: %s (while it exists, the phase clock is frozen)\n",
               pauseFlagFile.c_str());

    // Force per-monitor-v2 awareness BEFORE any window/monitor API. In a DPI-unaware process the
    // D3D11/HDR surface is shown at its native (logical) size and is NOT scaled to the panel's
    // physical resolution - so a 4K panel would only show the top-left fraction. PMv2 makes DWM
    // scale the surface to the full physical panel. (The Set return is trusted; the context
    // read-back is unreliable in this environment and is ignored.)
    BOOL pmv2set = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    printf("[hdr-signal] PMv2 set=%s\n", pmv2set ? "ok" : "FAILED");

    HINSTANCE hInst = GetModuleHandleW(nullptr);
    if (!RegisterWindowClass(hInst))
        return 4;

    // Enumerate monitors and find the target by physical resolution.
    auto monitors = EnumerateMonitors(targetW, targetH);
    const MonitorInfo* target = nullptr;
    for (auto& m : monitors)
    {
        char devA[128];
        WideCharToMultiByte(CP_UTF8, 0, m.device.c_str(), -1, devA, (int)sizeof(devA), nullptr,
                            nullptr);
        printf("[hdr-signal] monitor %dx%d dpi=%u rect=(%d,%d)-(%d,%d) %s%s\n", m.physW, m.physH,
               (unsigned)m.dpi, m.rect.left, m.rect.top, m.rect.right, m.rect.bottom, devA,
               m.isTarget ? "  <-- TARGET" : "");
        if (m.isTarget && !target)
            target = &m;
    }
    if (!target)
    {
        fprintf(stderr, "[hdr-signal] FAIL: no monitor with physical resolution %dx%d\n", targetW,
                targetH);
        return 4;
    }
    printf("[hdr-signal] target %dx%d -> rect (%d,%d)-(%d,%d) dpi=%u\n", targetW, targetH,
           target->rect.left, target->rect.top, target->rect.right, target->rect.bottom,
           (unsigned)target->dpi);
    // Cross-check: the PMv2 logical size that maps to the full physical panel.
    if (target->dpi > 0)
    {
        int lw = target->physW * 96 / target->dpi;
        int lh = target->physH * 96 / target->dpi;
        printf("[hdr-signal] dpi-derived logical size %dx%d (rect size %dx%d)\n", lw, lh,
               target->rect.right - target->rect.left, target->rect.bottom - target->rect.top);
    }

    const int x = target->rect.left;
    const int y = target->rect.top;
    const int w = target->rect.right - target->rect.left;
    const int h = target->rect.bottom - target->rect.top;

    // D3D11 device + DXGI factory are created BEFORE the window. d3dtest.cpp showed that
    // calling D3D11CreateDevice before any window is created lets the subsequent CreateWindowEx
    // succeed in this environment, while the opposite order (window first) fails with error 0.
    // The swap chain (which does need the window) is created after it.
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL levels[] = {
        D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0,
        D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
    };
    D3D_FEATURE_LEVEL gotLevel = D3D_FEATURE_LEVEL_9_1;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels,
                                   (UINT)(sizeof(levels) / sizeof(levels[0])), D3D11_SDK_VERSION,
                                   &device, &gotLevel, &context);
    if (FAILED(hr) || !device)
    {
        fprintf(stderr, "[hdr-signal] D3D11CreateDevice failed hr=0x%08lX\n",
                (unsigned long)hr);
        return 4;
    }
    printf("[hdr-signal] d3d11 featureLevel=0x%lX\n", (unsigned long)gotLevel);

    // DXGI factory (before the window; the swap chain, which needs the window, comes after).
    ComPtr<IDXGIFactory2> factory;
    hr = CreateDXGIFactory2(0, __uuidof(IDXGIFactory2), &factory);
    if (FAILED(hr) || !factory)
    {
        fprintf(stderr, "[hdr-signal] CreateDXGIFactory2 failed hr=0x%08lX\n",
                (unsigned long)hr);
        return 4;
    }

    HWND hwnd = CreatePopupWindow(hInst, L"Starshot HDR Signal", x, y, w, h);
    if (!hwnd)
        return 4;
    printf("[hdr-signal] window 0x%llX at (%d,%d) %dx%d\n", (unsigned long long)(intptr_t)hwnd, x,
           y, w, h);

    // Make it topmost + foreground so it covers the target panel and the recording's FullScreen
    // target resolves to this monitor.
    ForegroundAndTopmost(hwnd, x, y, w, h);
    printf("[hdr-signal] foreground+topmost set\n");

    // 10-bit flip-model swap chain bound to our window (follows the window, no monitor bind).
    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = (UINT)w;
    desc.Height = (UINT)h;
    desc.Format = DXGI_FORMAT_R10G10B10A2_UNORM;
    desc.Stereo = 0;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 3; // 3 flip-model buffers: with 2, the per-frame full clear clobbers the
                          // in-flight back buffer before the compositor reads it (drawn content is
                          // lost on screen even though it is in the buffer).
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    ComPtr<IDXGISwapChain1> swapChain;
    hr = factory->CreateSwapChainForHwnd(device.Get(), hwnd, &desc, nullptr, nullptr, &swapChain);
    if (FAILED(hr) || !swapChain)
    {
        fprintf(stderr,
                "[hdr-signal] CreateSwapChainForHwnd failed hr=0x%08lX (10-bit unsupported?)\n",
                (unsigned long)hr);
        return 4;
    }

    // Back buffer -> render target view.
    ComPtr<ID3D11Texture2D> backBuffer;
    hr = swapChain->GetBuffer(
        0, __uuidof(ID3D11Texture2D),
        reinterpret_cast<void**>(backBuffer.ReleaseAndGetAddressOf()));
    if (FAILED(hr) || !backBuffer)
    {
        fprintf(stderr, "[hdr-signal] GetBuffer failed hr=0x%08lX\n", (unsigned long)hr);
        return 4;
    }
    ComPtr<ID3D11RenderTargetView> rtv;
    hr = device->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv);
    if (FAILED(hr) || !rtv)
    {
        fprintf(stderr, "[hdr-signal] CreateRenderTargetView failed hr=0x%08lX\n",
                (unsigned long)hr);
        return 4;
    }

    // HDR color space: RGB_FULL_G2084_NONE_P2020 (HDR10). Failure is not fatal - the
    // recording's hdr-raw dump then shows SDR values, which is itself the diagnostic answer.
    int csHr = 0x80004002; // E_NOINTERFACE sentinel (in case QI to SwapChain3 fails)
    ComPtr<IDXGISwapChain3> swapChain3;
    if (SUCCEEDED(swapChain.As(&swapChain3)))
        csHr = (int)swapChain3->SetColorSpace1(DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020);
    printf("[hdr-signal] colorspace hr=0x%08X (0 = HDR10/PQ set)\n", csHr);

    // Bind the render target + viewport once; then every frame: clear + label + present.
    context->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
    D3D11_VIEWPORT vp{};
    vp.Width = (FLOAT)w;
    vp.Height = (FLOAT)h;
    context->RSSetViewports(1, &vp);

    // On-screen label (top strip). Creation failure is fatal: a silent fallback to no label
    // would leave an operator wondering why the row identity is not visible.
    Label label;
    if (!label.Init(device.Get(), w, h))
    {
        fprintf(stderr, "[hdr-signal] label init failed (D3D11 texture)\n");
        return 4;
    }
    ComPtr<ID3DBlob> vsBlob, psBlob;
    {
        ID3DBlob* err = nullptr;
        HRESULT hrC =
            D3DCompile(kVsHlsl, sizeof(kVsHlsl) - 1, nullptr, nullptr, nullptr, "main", "vs_5_0",
                       0, 0, &vsBlob, &err);
        if (FAILED(hrC))
        {
            const char* msg = err ? (const char*)err->GetBufferPointer() : "(no error blob)";
            fprintf(stderr, "[hdr-signal] VS compile hr=0x%08lX: %s\n", (unsigned long)hrC, msg);
            if (err)
                err->Release();
            return 4;
        }
        hrC = D3DCompile(kPsHlsl, sizeof(kPsHlsl) - 1, nullptr, nullptr, nullptr, "main",
                         "ps_5_0", 0, 0, &psBlob, &err);
        if (FAILED(hrC))
        {
            const char* msg = err ? (const char*)err->GetBufferPointer() : "(no error blob)";
            fprintf(stderr, "[hdr-signal] PS compile hr=0x%08lX: %s\n", (unsigned long)hrC, msg);
            if (err)
                err->Release();
            return 4;
        }
    }
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    if (FAILED(device->CreateVertexShader(vsBlob->GetBufferPointer(),
                                          vsBlob->GetBufferSize(), nullptr,
                                          vs.ReleaseAndGetAddressOf())) ||
        FAILED(device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(),
                                         nullptr, ps.ReleaseAndGetAddressOf())))
        return 4;
    D3D11_INPUT_ELEMENT_DESC ied[2] = {
        { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 },
    };
    ComPtr<ID3D11InputLayout> inputLayout;
    if (FAILED(device->CreateInputLayout(
            ied, 2, vsBlob->GetBufferPointer(), (UINT)vsBlob->GetBufferSize(),
            inputLayout.ReleaseAndGetAddressOf())))
        return 4;
    struct V2 { float x, y, u, v; };
    // The quad's corners are pre-computed in NDC (no cbuffer): full width, and the top strip
    // from the window top (NDC y = 1) down to the strip's bottom.
    const float yB = 1.0f - 2.0f * (float)label.h / (float)h;
    V2 quad[6] = { { -1, 1, 0, 0 }, { 1, 1, 1, 0 }, { 1, yB, 1, 1 },
                   { -1, 1, 0, 0 }, { 1, yB, 1, 1 }, { -1, yB, 0, 1 } };
    D3D11_BUFFER_DESC vbDesc{};
    vbDesc.ByteWidth = (UINT)sizeof(quad);
    vbDesc.Usage = D3D11_USAGE_DEFAULT;
    vbDesc.BindFlags = D3D11_BIND_VERTEX_BUFFER;
    D3D11_SUBRESOURCE_DATA vbInit{};
    vbInit.pSysMem = quad;
    ComPtr<ID3D11Buffer> vb;
    if (FAILED(device->CreateBuffer(&vbDesc, &vbInit, vb.ReleaseAndGetAddressOf())))
        return 4;
    D3D11_SAMPLER_DESC smpDesc{};
    smpDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR; // == the traditional D3D11_FILTER_LINEAR
                                                       // (the alias is absent from these SDK headers)
    smpDesc.AddressU = smpDesc.AddressV = smpDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    smpDesc.MaxLOD = D3D11_FLOAT32_MAX;
    ComPtr<ID3D11SamplerState> sampler;
    if (FAILED(device->CreateSamplerState(&smpDesc, sampler.ReleaseAndGetAddressOf())))
        return 4;
    D3D11_BLEND_DESC blendDesc{};
    blendDesc.RenderTarget[0] = { TRUE, D3D11_BLEND_ONE, D3D11_BLEND_INV_SRC_ALPHA,
                                  D3D11_BLEND_OP_ADD, D3D11_BLEND_ONE,
                                  D3D11_BLEND_INV_SRC_ALPHA, D3D11_BLEND_OP_ADD,
                                  D3D11_COLOR_WRITE_ENABLE_ALL };
    ComPtr<ID3D11BlendState> blend;
    if (FAILED(device->CreateBlendState(&blendDesc, blend.ReleaseAndGetAddressOf())))
        return 4;
    // Draw state for the label quad. Re-applied every frame: in this environment the
    // context's state has been observed to be lost (a one-time bind draws nothing, while
    // per-frame re-binds render reliably), and the calls are cheap.
    auto applyLabelState = [&]()
    {
        ID3D11Buffer* vbPtr = vb.Get();
        UINT stride = (UINT)sizeof(V2), zeroOff = 0;
        context->IASetVertexBuffers(0, 1, &vbPtr, &stride, &zeroOff);
        context->IASetInputLayout(inputLayout.Get());
        context->VSSetShader(vs.Get(), nullptr, 0);
        context->PSSetShader(ps.Get(), nullptr, 0);
        context->PSSetSamplers(0, 1, sampler.GetAddressOf());
        context->PSSetShaderResources(0, 1, label.srv.GetAddressOf());
        context->OMSetBlendState(blend.Get(), nullptr, 0xFFFFFFFF);
        context->RSSetViewports(1, &vp);
        context->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
    };
    applyLabelState();
    printf("[hdr-signal] label on (top strip %dx%d, outside the test's central crop)\n", w,
           label.h);
    fflush(stdout);

    const float litCode = PqCode(nits / 10000.0f); // per-lit-channel PQ code for `nits`
    int lastIdx = -1;
    bool t0Written = false;
    // Phase clock: accumulated NON-paused time drives the color phases + label, so a
    // pause (the pause-flag file exists) FREEZES the field on the current color. The
    // window keeps presenting the frozen frame while paused, which also keeps a WGC
    // capture session alive; the maxSec self-exit counts non-paused time too, so a
    // pause extends a play-only run by the paused duration.
    // Infinite render loop; the orchestrator stops the host by killing the process.
    // NOTE: no periodic foreground re-assert here - it steals focus from the user's other
    // windows (and its ALT press causes a brief flicker). The orchestrator foregrounds the
    // window only at the moment each recording starts.
    double phaseS = 0.0; // color phases + label countdown (frozen while paused)
    double activeS = 0.0; // non-paused runtime (the maxSec self-exit)
    bool paused = false;
    auto lastFrame = std::chrono::steady_clock::now();
    auto lastFlagCheck = lastFrame;
    while (true)
    {
        PumpMessages();
        auto now = std::chrono::steady_clock::now();
        double frameDt = std::chrono::duration<double>(now - lastFrame).count();
        lastFrame = now;
        // Poll the pause flag at ~10 Hz (a 100 ms detection lag is fine - a pause is a
        // deliberate human action).
        if (now - lastFlagCheck >= std::chrono::milliseconds(100))
        {
            lastFlagCheck = now;
            bool p = !pauseFlagFile.empty() && FileExists(pauseFlagFile);
            if (p != paused)
            {
                paused = p;
                printf("[hdr-signal] %s\n",
                       paused ? "paused (frozen on the current color)" : "resumed");
                fflush(stdout);
            }
        }
        if (!paused)
        {
            phaseS += frameDt;
            activeS += frameDt;
        }
        // Optional hard stop (the play-only test): the fields clear on their own even if the
        // orchestrator dies mid-run, so an interrupted run never leaves a bright field up.
        // (A pause extends the window: activeS is non-paused time.)
        if (maxSec > 0.0 && activeS >= maxSec)
        {
            printf("[hdr-signal] max runtime reached (%.0fs), exiting\n", maxSec);
            fflush(stdout);
            break;
        }
        // Phase 0 (GREEN) lasts firstPhaseSec; the remaining colors each last `period`.
        int idx;
        if (phaseS < firstPhaseSec)
            idx = 0;
        else
            idx = 1 + (int)((phaseS - firstPhaseSec) / period) % (kColorCount - 1);
        const Color& c = kColors[idx];
        // r/g/b are per-channel "lit" masks; a G2084 swap chain stores PQ codes, so each lit
        // channel carries OETF(nits/10000) (== 1.0 at the default 10000 nits).
        float col[4] = { c.r * litCode, c.g * litCode, c.b * litCode, c.a };
        if (idx != lastIdx)
        {
            lastIdx = idx;
            printf("[hdr-signal] t+%llds %s\n", (long long)phaseS, c.name);
            fflush(stdout);
        }
        // Rebuild the frame every time: the flat field color + the label quad on top.
        // (Cheap, and it sidesteps flip-model subtleties of clearing only on color
        // change; rebuilding the FROZEN frame while paused also keeps the WGC session
        // alive.)
        context->ClearRenderTargetView(rtv.Get(), col);
        std::string lbl = MakeLabel(w, h, nits, c.name, phaseS, firstPhaseSec, period);
        if (lbl != label.last)
            label.Blit(lbl, context.Get());
        applyLabelState();
        context->Draw(6, 0);
        swapChain->Present(0, 0);
        if (!t0Written)
        {
            t0Written = true;
            WriteT0(t0File, period);
            printf("[hdr-signal] t0 written: %s\n", t0File.c_str());
            fflush(stdout);
        }
        Sleep(8);
    }
    return 0;
}
