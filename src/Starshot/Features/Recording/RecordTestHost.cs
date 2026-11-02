#if DEBUG
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Features.Recording;

/// <summary>
/// 无头录制测试宿主（仅 DEBUG 构建；入口 Program.Main 的 --record-test 分支）。
/// 用法: Starshot.exe --record-test [fullscreen|window] [秒数] [pauseFlagFile (optional: while
/// the file exists the duration budget is held - the HDR test's Space-pause, see
/// src/Starshot.Tests/HdrRecordingTests.cs)]
/// 流程: 最小初始化配置/日志 → 创建已知内容测试窗口（四色象限 + 秒数计数器 + 扫描条）并置前
/// → 用真实的 VideoRecordService 录制 N 秒 → 打印输出文件与日志路径。
/// 全程不起 UI/托盘/热键；帧内容与时长可程序化校验（计数器逐秒 +1，扫描条逐帧位置不同）。
/// </summary>
internal static class RecordTestHost
{
    public static int Run(string[] args)
    {
        string targetName = args.Length > 1 ? args[1] : "window";
        int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 15;
        // Optional pause-flag file (the HDR flat-field test's Space-pause): while it exists,
        // the duration budget does NOT advance - the capture keeps recording the (frozen)
        // content in real time, so the finished video stays aligned with the stimulus' t0
        // and still contains every color phase.
        string? pauseFlag = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3]) ? args[3] : null;
        // "raw" = capture the current foreground monitor without the synthetic counter
        // window (used to sample real on-screen HDR content, e.g. a playing video).
        // "windowfg" = capture the current foreground WINDOW (no counter window),
        // mirroring the user's real window-recording path against a live window.
        bool noCounter = targetName is "raw" or "windowfg";
        VideoRecordTarget target = targetName switch
        {
            "fullscreen" or "full" or "raw" => VideoRecordTarget.FullScreen,
            _ => VideoRecordTarget.Window,
        };

        // WinRT 互操作（WGC 捕获需要）
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        AppConfig.PrepareHeadless();

        var service = AppConfig.GetService<VideoRecordService>();

        TestCounterWindow? win = null;
        bool haveCounter = false;
        if (noCounter)
        {
            Console.WriteLine(
                targetName is "raw"
                    ? "[record-test] raw mode: no counter window; capturing current foreground monitor (have your HDR content on screen first)"
                    : "[record-test] windowfg mode: no counter window; capturing the current foreground window"
            );
        }
        else
        {
            try
            {
                win = new TestCounterWindow();
                haveCounter = win.Foreground();
                Console.WriteLine(
                    $"[record-test] counter window shown (fg={haveCounter}), target={target} seconds={seconds} folder={AppConfig.ScreenshotFolder}"
                );
            }
            catch (Exception ex)
            {
                // 窗口创建失败不致命：退回录制当前前台窗口（管线同样跑通，仅失去地面真值内容）
                Console.Error.WriteLine(
                    $"[record-test] counter window unavailable ({ex.Message}); will record existing foreground"
                );
                win?.Dispose();
                win = null;
            }
        }

        // Probe the effective Sleep(1) granularity before the capture starts: the OS can
        // override the timeBeginPeriod request (power policy), in which case Sleep(1)
        // quantizes to the 15.6 ms system timer and a 12 ms cadence degrades to ~40 fps.
        int timerBeginRc = TestCounterWindow.Win32.timeBeginPeriod(1);
        var probeSw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++)
            Thread.Sleep(1);
        double probeAvg = probeSw.Elapsed.TotalMilliseconds / 50;
        Console.WriteLine(
            $"[record-test] timeBeginPeriod(1) rc={timerBeginRc}; Sleep(1) x50 = {probeSw.Elapsed.TotalMilliseconds:0} ms (~{probeAvg:0.0} ms/sleep pre-capture; 1.0 = 1 ms timer, 15.6 = system timer)"
        );

        service.StartAsync(target).GetAwaiter().GetResult();
        if (!service.IsRecording)
        {
            Console.Error.WriteLine($"[record-test] start failed, log: {AppConfig.LogFile}");
            return 3;
        }
        Console.WriteLine($"[record-test] recording {seconds}s ...");
        var sw = Stopwatch.StartNew();
        TimeSpan pausedTotal = TimeSpan.Zero;
        DateTime? sincePaused = null;
        var cadence = TimeSpan.FromMilliseconds(12); // ~83 fps content: distinguishes a 60 Hz panel cap from 120 Hz
        var nextPaint = TimeSpan.Zero;
        int iterations = 0;
        var tPump = Stopwatch.StartNew();
        var tWait = Stopwatch.StartNew();
        long pumpMaxMs = 0;
        // SpinOnce() (no count) is a hardware pause with no yield - Thread.SpinWait(32)
        // instead yields to the scheduler after 8 iterations, and a single yield can cost
        // a full 15.6 ms time quantum, which is exactly what made the "spin" wait above
        // average 24 ms.
        var spin = new SpinWait();
        try
        {
            while (service.IsRecording)
            {
                // Space-pause (the flag file, written by the HDR test's key control): hold the
                // duration budget while the stimulus host is frozen; the capture itself keeps
                // running (frozen content), so video and audio stay in sync and every color
                // phase still lands in the recording. A stale flag (the orchestrator died
                // mid-pause) must not hang this host forever.
                bool paused = pauseFlag is not null && File.Exists(pauseFlag);
                if (paused)
                {
                    if (sincePaused is null)
                    {
                        sincePaused = DateTime.UtcNow;
                        Console.WriteLine("[record-test] paused (duration budget held)");
                    }
                    else if (DateTime.UtcNow - sincePaused.Value > TimeSpan.FromHours(1))
                    {
                        Console.WriteLine(
                            "[record-test] pause flag held > 1 h (stale?) - finishing"
                        );
                        break;
                    }
                }
                else if (sincePaused is not null)
                {
                    pausedTotal += DateTime.UtcNow - sincePaused.Value;
                    sincePaused = null;
                    Console.WriteLine(
                        $"[record-test] resumed ({(int)pausedTotal.TotalSeconds}s held)"
                    );
                }
                if (sw.Elapsed - pausedTotal >= TimeSpan.FromSeconds(seconds))
                    break;
                tPump.Start();
                win?.PumpMessages();
                tPump.Stop();
                long pumpMs = tPump.ElapsedMilliseconds;
                if (pumpMs > pumpMaxMs)
                    pumpMaxMs = pumpMs;
                iterations++;
                // Fast paint cadence: the counter window is the ground-truth content for
                // the capture, and WGC only delivers on change - a 10 fps paint (the old
                // 100 ms sleep) made "test content" runs cap at 10 fps and masked the
                // panel's actual delivery rate. Spin the whole wait to hit the deadline
                // precisely: the effective Sleep() granularity is unreliable here (a
                // pre-capture probe measured 1.4 ms/sleep, yet in-loop sleeps degraded to
                // the 15.6 ms system timer once the WGC pipeline started, collapsing a
                // 12 ms cadence to ~40 fps), so the spin makes the cadence independent of
                // the timer resolution (it burns ~1 core for the test duration; acceptable
                // for a headless debug host). The deadline is cumulative, so an occasional
                // slow paint drops one paint instead of shifting the whole cadence.
                nextPaint += cadence;
                tWait.Start();
                while (sw.Elapsed < nextPaint)
                    spin.SpinOnce();
                tWait.Stop();
                // Resync only when more than one full cadence is behind (a slow pump
                // dropped paints). The spin exits 0-1 ms past the deadline, so a plain
                // "nextPaint < elapsed" test fires on every on-schedule iteration and
                // shifts the cadence out by a full 12 ms (24 ms cycle -> ~40 fps).
                if (sw.Elapsed - nextPaint > cadence)
                    nextPaint = sw.Elapsed + cadence;
            }
        }
        finally
        {
            TestCounterWindow.Win32.timeEndPeriod(1);
        }
        double paintedSeconds = (sw.Elapsed - pausedTotal).TotalSeconds;
        if (paintedSeconds > 0 && iterations > 0)
        {
            Console.WriteLine(
                $"[record-test] paint iterations={iterations} over {paintedSeconds:0.0}s -> content ~{iterations / paintedSeconds:0} fps; avg pump+paint={tPump.Elapsed.TotalMilliseconds / iterations:0.0}ms (max {pumpMaxMs}ms) avg deadline-wait={tWait.Elapsed.TotalMilliseconds / iterations:0.0}ms"
            );
        }
        service.StopAsync().GetAwaiter().GetResult();
        // Post-encode mode (VideoRecordPostEncode): StopAsync returns as soon as the raw
        // buffer is drained to disk; the MP4 is written by the background drain (a 4K
        // drain runs ~5x the clip length). Await it, or the newest-Recording_*.mp4 probe
        // below would report a file that does not exist yet.
        Task? drain = service.FinalizeTask;
        if (drain is not null && !drain.IsCompleted)
        {
            Console.WriteLine(
                "[record-test] post-encode mode: waiting for the background drain ..."
            );
            Task finished = Task.WhenAny(drain, Task.Delay(TimeSpan.FromMinutes(60)));
            finished.GetAwaiter().GetResult();
            if (!drain.IsCompleted)
            {
                Console.Error.WriteLine(
                    "[record-test] the post-encode drain did not finish within 60 min"
                );
                return 4;
            }
            Console.WriteLine("[record-test] drain done");
        }
        win?.Dispose();

        // 输出文件 = 截图文件夹下最新的 Recording_*.mp4
        string folder = AppConfig.ScreenshotFolder;
        string? newest = Directory
            .GetFiles(folder, "Recording_*.mp4")
            .OrderBy(p => new FileInfo(p).LastWriteTimeUtc)
            .LastOrDefault();
        Console.WriteLine($"[record-test] OUTPUT {newest}");
        Console.WriteLine($"[record-test] LOG {AppConfig.LogFile}");
        return 0;
    }
}

/// <summary>
/// 已知内容测试窗口（Win32 + GDI，无 WinForms/WinUI 依赖）：
/// 四色象限（红/绿/蓝/黄）+ 中央黑底白字「REC-TEST n s」秒数计数器 + 底部洋红扫描条（~1s 扫一遍宽）。
/// 帧间差异确定可校验：计数器每秒 +1，扫描条位置逐帧变化。
/// 窗口在创建线程（主线程）上泵消息；Run 的等待循环用同步 Sleep 保证同线程。
/// 同一时刻只会有一个实例（宿主一次性建窗），WndProc 走静态当前实例指针。
/// </summary>
internal sealed class TestCounterWindow : IDisposable
{
    private static TestCounterWindow? _current;

    private readonly Stopwatch _clock = new();

    private readonly IntPtr _hInstance = Win32.GetModuleHandle(null);

    private readonly IntPtr _font;

    private readonly IntPtr[] _brushes; // 红/绿/蓝/黄/黑/洋红

    private readonly IntPtr _hwnd;

    private int _w;

    private int _h;

    private bool _staticPainted;

    public TestCounterWindow()
    {
        _hwnd = Win32.CreateWindow("Starshot RecordTest", out _w, out _h);
        _font = Win32.CreateFont(
            -(int)Math.Clamp(_h / 6, 24, 160),
            0, // nWidth
            0, // nEscapement
            0, // nOrientation
            700, // nWeight (bold)
            0, // bItalic
            0, // bUnderline
            0, // cStrikeOut
            1, // lfCharSet (DEFAULT_CHARSET)
            0, // lfOutPrecision
            0, // lfClipPrecision
            4, // lfQuality (ANTIALIASED_QUALITY)
            0, // lfPitchAndFamily (DEFAULT_PITCH | DEFAULT_FAMILY)
            "Arial"
        );
        uint[] colors =
        {
            Win32.CLR_RED,
            Win32.CLR_GREEN,
            Win32.CLR_BLUE,
            Win32.CLR_YELLOW,
            Win32.CLR_BLACK,
            Win32.CLR_MAGENTA,
        };
        _brushes = new IntPtr[colors.Length];
        for (int i = 0; i < _brushes.Length; i++)
            _brushes[i] = Win32.CreateSolidBrush(colors[i]);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
        Win32.UpdateWindow(_hwnd); // 消费初始 WM_PAINT（ValidateRect），画面由托管 PaintFrame 绘制
        _clock.Start();
        _current = this;
        PaintFrame(); // 首帧立即就绪（还没开始录）
    }

    /// <summary>
    /// 置前。从非交互 shell 启动时进程无前台权限，SetForegroundWindow 常被拒绝，
    /// 用 Alt 键技巧 + AttachThreadInput 附到前台线程 + TOPMOST 逐级解锁。成功返回 true。
    /// </summary>
    public bool Foreground()
    {
        IntPtr fg0 = Win32.GetForegroundWindow();
        Console.WriteLine(
            $"[record-test] fg-before=0x{fg0:X} my=0x{_hwnd:X} (fgTitle=[{Win32.GetWindowTitle(fg0)}])"
        );
        if (fg0 == _hwnd)
            return true;

        // 技巧 1: 模拟按一次 Alt，允许本进程调用 SetForegroundWindow
        Win32.keybd_event(0x12, 0, 0, IntPtr.Zero);
        Win32.keybd_event(0x12, 0, Win32.KEYEVENTF_KEYUP, IntPtr.Zero);
        bool r1 = Win32.SetForegroundWindow(_hwnd);
        Thread.Sleep(100);
        Console.WriteLine(
            $"[record-test] step1 SetForegroundWindow ret={r1} fg=0x{Win32.GetForegroundWindow():X}"
        );
        if (Win32.GetForegroundWindow() == _hwnd)
            return true;

        // 技巧 2: TOPMOST + 附到当前前台窗口所属线程，继承其前台权限
        Win32.SetWindowPos(
            _hwnd,
            Win32.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE
        );
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
            uint myThread = Win32.GetCurrentThreadId();
            if (fgThread != 0 && myThread != 0 && fgThread != myThread)
            {
                Win32.AttachThreadInput(myThread, fgThread, true);
                Win32.BringWindowToTop(_hwnd);
                bool r2 = Win32.SetForegroundWindow(_hwnd);
                Win32.AttachThreadInput(myThread, fgThread, false);
                Thread.Sleep(100);
                Console.WriteLine(
                    $"[record-test] step2 attach ret={r2} fg=0x{Win32.GetForegroundWindow():X}"
                );
            }
            else
            {
                Console.WriteLine("[record-test] step2 skipped (same thread or no fg thread)");
            }
        }
        if (Win32.GetForegroundWindow() == _hwnd)
            return true;

        // 技巧 3: 直接 SetWindowPos 激活（SWP_NOACTIVATE 之外）
        Win32.SetWindowPos(
            _hwnd,
            Win32.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE
        );
        Thread.Sleep(100);
        Console.WriteLine($"[record-test] step3 activate fg=0x{Win32.GetForegroundWindow():X}");
        return Win32.GetForegroundWindow() == _hwnd;
    }

    /// <summary>同步泵消息并绘制一帧（须在与创建窗口同一线程调用）。</summary>
    public void PumpMessages()
    {
        while (Win32.PeekMessage(out Win32.MSG msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
        PaintFrame();
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
        }
        if (_font != IntPtr.Zero)
            Win32.DeleteObject(_font);
        for (int i = 0; i < _brushes.Length; i++)
            Win32.DeleteObject(_brushes[i]);
        _current = null;
    }

    // 静态 WndProc（类注册一次，经 _current 路由到实例）。
    // GDI 绘制不在原生回调里做（异常/AV 无法跨原生边界收回），改由托管泵循环 PaintFrame 完成；
    // 这里只消费 WM_PAINT（ValidateRect，不擦除），绘制全走托管线程。
    internal static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_current is not { } w)
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        switch (msg)
        {
            case Win32.WM_PAINT:
                Win32.ValidateRect(hwnd, IntPtr.Zero);
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>托管线程直接绘制一帧（GetDC/ReleaseDC，不依赖 WM_PAINT 上下文）。</summary>
    private void PaintFrame()
    {
        if (_brushes is null || _brushes[0] == IntPtr.Zero)
            return;
        IntPtr hdc = Win32.GetDC(_hwnd);
        if (hdc == IntPtr.Zero)
            return;
        try
        {
            int w = _w;
            int h = _h;

            if (!_staticPainted)
            {
                // Static background (the four color quadrants + the central black band)
                // is painted exactly once. Re-painting the whole 4K client every tick
                // cost ~18 ms and pinned the content rate to ~55 fps, which masked the
                // panel's true delivery ceiling - only the dynamic regions below are
                // redrawn per tick.
                var r1 = new Win32.RECT(0, 0, w / 2, h / 2);
                var r2 = new Win32.RECT(w / 2, 0, w, h / 2);
                var r3 = new Win32.RECT(0, h / 2, w / 2, h);
                var r4 = new Win32.RECT(w / 2, h / 2, w, h);
                Win32.FillRect(hdc, ref r1, _brushes[0]);
                Win32.FillRect(hdc, ref r2, _brushes[1]);
                Win32.FillRect(hdc, ref r3, _brushes[2]);
                Win32.FillRect(hdc, ref r4, _brushes[3]);
                var rMid = new Win32.RECT(0, h / 3, w, h * 2 / 3);
                Win32.FillRect(hdc, ref rMid, _brushes[4]);
                _staticPainted = true;
            }

            // Per-tick dynamic content only: the seconds counter in a small centered
            // box (it must stay inside the static central band) and the scan bar.
            int fontH = (int)Math.Clamp(_h / 6, 24, 160);
            int boxW = fontH * 9;
            int boxH = fontH * 2;
            var rText = new Win32.RECT(
                (w - boxW) / 2,
                (h - boxH) / 2,
                (w + boxW) / 2,
                (h + boxH) / 2
            );
            Win32.FillRect(hdc, ref rText, _brushes[4]);
            IntPtr oldFont = Win32.SelectObject(hdc, _font);
            Win32.SetBkMode(hdc, Win32.TRANSPARENT);
            Win32.SetTextColor(hdc, Win32.CLR_WHITE);
            string label = $"REC-TEST {(int)_clock.Elapsed.TotalSeconds} s";
            Win32.DrawText(
                hdc,
                label,
                -1,
                ref rText,
                Win32.DT_CENTER | Win32.DT_VCENTER | Win32.DT_SINGLELINE
            );
            Win32.SelectObject(hdc, oldFont);

            // Bottom scan bar (a 24 px magenta strip; its position changes every tick)
            int x = (int)((Environment.TickCount64 / 4) % (uint)Math.Max(1, w - 24));
            var rBar = new Win32.RECT(x, h - 48, x + 24, h - 24);
            Win32.FillRect(hdc, ref rBar, _brushes[5]);
        }
        finally
        {
            Win32.ReleaseDC(_hwnd, hdc);
        }
    }

    // ==================== Win32/GDI ====================

    internal static class Win32
    {
        public const uint WM_PAINT = 0x000F;
        public const uint WM_TIMER = 0x0113;
        public const uint WM_DESTROY = 0x0002;
        public const uint SPI_GETWORKAREA = 0x0030;
        public const uint WS_POPUP = 0x80000000;
        public const int SW_SHOW = 5;
        public const uint PM_REMOVE = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint DT_CENTER = 0x01;
        public const uint DT_VCENTER = 0x04;
        public const uint DT_SINGLELINE = 0x20;
        public const int TRANSPARENT = 1;

        // COLORREF = 0x00BBGGRR (注意字节序: 0x00800000 是藏青 (0,0,128), 不是绿)
        public const uint CLR_RED = 0x000000FF; // RGB(255,0,0)
        public const uint CLR_GREEN = 0x00008000; // RGB(0,128,0)
        public const uint CLR_BLUE = 0x00FF0000; // RGB(0,0,255)
        public const uint CLR_YELLOW = 0x0000FFFF; // RGB(255,255,0)
        public const uint CLR_BLACK = 0x00000000;
        public const uint CLR_WHITE = 0x00FFFFFF;
        public const uint CLR_MAGENTA = 0x00FF00FF;

        private static readonly WndProcDelegate _wndProc = WndProcTrampoline;

        private static bool _classRegistered;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public RECT(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public long X;
            public long Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public RECT rcPaint;
            public int fRestore;
            public int fValidate;
            public int fAlternate;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszMenuName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszClassName;
            public IntPtr hIconSm;
        }

        private delegate IntPtr WndProcDelegate(
            IntPtr hwnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam
        );

        /// <summary>建窗（主显示器工作区大小），返回句柄；同时输出客户区尺寸。委托表引用须静态存活。</summary>
        public static IntPtr CreateWindow(string title, out int clientW, out int clientH)
        {
            if (!_classRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0x0001, // CS_HREDRAW
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = GetModuleHandle(null),
                    lpszMenuName = null,
                    lpszClassName = "StarshotRecordTestClass",
                };
                if (RegisterClassEx(ref wc) == 0)
                    throw new System.ComponentModel.Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "RegisterClassEx failed"
                    );
                _classRegistered = true;
            }
            RECT wa;
            SystemParametersInfo(SPI_GETWORKAREA, 0, out wa, 0);
            IntPtr h = CreateWindowEx(
                0,
                "StarshotRecordTestClass",
                title,
                WS_POPUP, // 无边框: 客户区 == 窗口区, 绘制坐标与屏幕坐标对齐 (标题栏会挤占客户区, 导致底部扫描条被裁掉)
                wa.Left,
                wa.Top,
                wa.Right - wa.Left,
                wa.Bottom - wa.Top,
                IntPtr.Zero,
                IntPtr.Zero,
                GetModuleHandle(null),
                IntPtr.Zero
            );
            if (h == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "CreateWindowEx failed"
                );
            GetClientRect(h, out RECT cr);
            clientW = cr.Right - cr.Left;
            clientH = cr.Bottom - cr.Top;
            return h;
        }

        // 类注册的窗口过程 → 宿主静态 WndProc（委托表引用须静态存活）
        private static IntPtr WndProcTrampoline(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return TestCounterWindow.Dispatch(hwnd, msg, wParam, lParam);
        }

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
        private static extern uint RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport(
            "user32.dll",
            EntryPoint = "CreateWindowExW",
            CharSet = CharSet.Unicode,
            SetLastError = true
        )]
        public static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam
        );

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out IntPtr lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        public static readonly IntPtr HWND_TOPMOST = new(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags
        );

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(
            IntPtr hWnd,
            System.Text.StringBuilder lpString,
            int nMaxCount
        );

        public static string GetWindowTitle(IntPtr hWnd)
        {
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("user32.dll")]
        public static extern void keybd_event(
            byte bVk,
            byte bScan,
            uint dwFlags,
            IntPtr dwExtraInfo
        );

        // 1 ms timer resolution for the 60 fps paint cadence (Sleep(1) otherwise
        // quantizes to the 15.6 ms system timer and the cadence drifts to 30 fps).
        [DllImport("winmm.dll")]
        public static extern int timeBeginPeriod(int ms);

        [DllImport("winmm.dll")]
        public static extern int timeEndPeriod(int ms);

        [DllImport("user32.dll")]
        public static extern bool PeekMessage(
            out MSG lpMsg,
            IntPtr hWnd,
            uint wMsgFilterMin,
            uint wMsgFilterMax,
            uint wRemoveMsg
        );

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr DefWindowProc(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam
        );

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

        [DllImport("user32.dll")]
        public static extern int DrawText(
            IntPtr hdc,
            string lpString,
            int cchText,
            ref RECT lprc,
            uint uFormat
        );

        [DllImport("user32.dll")]
        public static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hBrush);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(uint rgbColor);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hObject, IntPtr hObjectNew);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFont(
            int nHeight,
            int nWidth,
            int nEscapement,
            int nOrientation,
            int nWeight,
            int bItalic,
            int bUnderline,
            int cStrikeOut,
            uint lfCharSet,
            uint lfOutPrecision,
            uint lfClipPrecision,
            uint lfQuality,
            uint lfPitchAndFamily,
            string lfFaceName
        );

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int iBkMode);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr hdc, uint rgbColor);

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(
            uint uiAction,
            uint uiParam,
            out RECT pvParam,
            uint fWinIni
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}

#endif
