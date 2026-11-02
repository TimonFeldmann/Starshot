using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Display;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Starshot.Features.Screenshot;
using Starshot.Helpers;
using Vanara.PInvoke;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Recording;

/// <summary>
/// 录制目标。
/// </summary>
public enum VideoRecordTarget
{
    /// <summary>整个显示器（前台窗口所在屏）。</summary>
    FullScreen,

    /// <summary>框选区域（静态选区，须落在单一显示器内）。</summary>
    Region,

    /// <summary>前台窗口。</summary>
    Window,
}

/// <summary>
/// 录屏服务：WGC 捕获 + x264 (SDR H.264) / x265 (HDR HEVC) + faac (WASAPI 回环 AAC)
/// + 自研 MP4 复接，输出到截图文件夹。
/// 开关语义：录制中按任意录制热键 = 停止（v1 不支持录制中切换目标）。
/// 帧处理在 WGC 泵线程；本类 Start/Stop 在 UI 线程发起（COM 调用）。
/// </summary>
public sealed class VideoRecordService
{
    private readonly ILogger<VideoRecordService> _logger;

    // 串行化 Start/Stop（停止中不许再启动）
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly object _sessionLock = new();

    private RecordingSession? _session;

    private DateTime _startedUtc;

    public VideoRecordService(ILogger<VideoRecordService> logger)
    {
        _logger = logger;
    }

    public bool IsRecording { get; private set; }

    public VideoRecordTarget? Target { get; private set; }

    /// <summary>
    /// Post-encode mode (VideoRecordPostEncode): capture has stopped but the background
    /// drain (encode + finalize) is still running. A new recording is not started while
    /// finalizing (the drain owns the encoder resources and the CPU).
    /// </summary>
    public bool IsFinalizing { get; private set; }

    private Task? _drainTask;

    /// <summary>
    /// Post-encode mode: the running drain task (null when not finalizing). Headless
    /// test hosts await it before assuming the MP4 is on disk.
    /// </summary>
    public Task? FinalizeTask => _drainTask;

    /// <summary>录制状态变化（托盘刷新菜单用；事件在 UI 线程触发）。</summary>
    public event Action? RecordingChanged;

    public TimeSpan Elapsed => IsRecording ? DateTime.UtcNow - _startedUtc : TimeSpan.Zero;

    /// <summary>开关：未录制启动目标，录制中停止当前录制。</summary>
    public async Task ToggleAsync(VideoRecordTarget target)
    {
        if (IsRecording)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync(target);
        }
    }

    // ==================== 启动 ====================

    public async Task StartAsync(VideoRecordTarget target)
    {
        if (IsRecording)
            return;
        if (IsFinalizing)
            return; // a background drain owns the encoder/CPU; a new start would starve it
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5)))
            return; // 停止流程进行中
        try
        {
            await StartCoreAsync(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video record start failed");
            InAppToast.MainWindow?.Error(Lang.Starshot_RecordingFailed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync(VideoRecordTarget target)
    {
        // 编码器 DLL（x64 才随包发布；缺了直接提示，不崩）
        string? codecErr = CodecsNative.Init();
        if (codecErr is not null)
        {
            _logger.LogWarning("Codec init failed: {err}", codecErr);
            InAppToast.MainWindow?.Error(Lang.Starshot_RecordingCodecUnavailable, codecErr);
            return;
        }

        // 1) 目标解析：捕获 item + 裁切窗口
        (
            GraphicsCaptureItem Item,
            int CropX,
            int CropY,
            int CropW,
            int CropH,
            Microsoft.UI.DisplayId DisplayId
        )? resolved = await ResolveTargetAsync(target);
        if (resolved is null)
            return; // 用户取消选区 / 无前台窗口（已提示）
        var (item, cropX, cropY, cropW, cropH, displayId) = resolved.Value;

        // 2) HDR 判定：显示器 HDR 模式 + 自动开关
        using var displayInfo = DisplayInformation.CreateForDisplayId(displayId);
        bool displayHdr =
            displayInfo.GetAdvancedColorInfo().CurrentAdvancedColorKind
            is DisplayAdvancedColorKind.HighDynamicRange;
        bool hdr = displayHdr && AppConfig.VideoRecordHdrMode == 0;

        int quality = Math.Clamp(AppConfig.VideoRecordQuality, 0, 2);
        double crf = hdr
            ? new[] { 18.0, 22.0, 27.0 }[quality]
            : new[] { 18.0, 23.0, 28.0 }[quality];
        // HDR (x265) encoder preset (x265 speed ladder; SDR is fixed to veryfast)
        string x265Preset = new[]
        {
            "ultrafast",
            "superfast",
            "veryfast",
            "faster",
            "fast",
            "medium",
        }[Math.Clamp(AppConfig.VideoRecordEncoderPreset, 0, 5)];
        // Target FPS cap (Recording settings "Target FPS"; 0 = native display refresh).
        // Dropped frames are skipped in the capture pump BEFORE readback/convert/encode,
        // so a lower cap cuts CPU usage + file size. The legacy hidden VideoRecordFpsMode
        // ladder (60/30) is honored only while TargetFps is unset.
        int fpsCap = AppConfig.VideoRecordTargetFps;
        if (fpsCap == 0)
        {
            fpsCap = AppConfig.VideoRecordFpsMode switch
            {
                1 => 60,
                2 => 30,
                _ => 0,
            };
        }
        // Hidden debug override (record-test A/B; see performance.md, WGC root-cause
        // section): the compositor's default WGC pacing is conservative; a small
        // explicit MinUpdateInterval (26H2) may unlock full-rate delivery in the
        // HDR/EDR state. 0 = leave the OS default.
        int minUpdateIntervalMs = 0;
        if (
            int.TryParse(
                Environment.GetEnvironmentVariable("STARSHOT_WGC_MIN_UPDATE_INTERVAL_MS") ?? "",
                out int minMi
            )
        )
        {
            minUpdateIntervalMs = Math.Max(0, minMi);
        }
        int keyint = Math.Max(30, fpsCap > 0 ? fpsCap * 2 : 120);
        int threads = Math.Max(2, Environment.ProcessorCount / 2);

        // Fast-convert mode (Recording settings toggle, default off = bit-exact): a
        // smaller 80 KB hybrid PQ LUT + reciprocal chroma math (luma +-1 / chroma
        // +-2..3 codes, inside the HDR suite bands). STARSHOT_WGC_FAST_CONVERT=1/0
        // overrides the saved setting for A/B testing (record-test, see performance.md).
        bool fastConvert = AppConfig.VideoRecordFastConvert;
        if (
            bool.TryParse(
                Environment.GetEnvironmentVariable("STARSHOT_WGC_FAST_CONVERT") ?? "",
                out bool fastOv
            )
        )
        {
            fastConvert = fastOv;
        }

        // SIMD kernel + row-pool worker count (Recording settings; 0 = Auto for both).
        // The STARSHOT_WGC_SIMD / STARSHOT_WGC_WORKERS env probes override these inside
        // FrameProcessor for A/B testing (record-test, see performance.md).
        int simdKernelIndex = AppConfig.VideoRecordSimdKernel;
        int workerCount = AppConfig.VideoRecordWorkerCount;
        // Target video bitrate (Recording settings "Bitrate"; 0 = off -> CRF quality mode).
        // ABR mode sizes the file ~= bitrate x duration instead of the quality-driven CRF.
        int bitrateKbps = AppConfig.VideoRecordBitrateKbps;

        var session = new RecordingSession
        {
            Item = item,
            Hdr = hdr,
            CropX = cropX,
            CropY = cropY,
            CropW = cropW,
            CropH = cropH,
            OutWidth = cropW & ~1,
            OutHeight = cropH & ~1,
            // 回读缓冲按整帧（捕获面）尺寸，裁切在转换阶段做
            CaptureWidth = item.Size.Width,
            CaptureHeight = item.Size.Height,
        };
        try
        {
            // Post-encode mode: validate the raw buffer location FIRST (a bad drive or a
            // nearly full one must not leave a half-started session behind - nothing of
            // the session is open yet at this point).
            bool postMode = AppConfig.VideoRecordPostEncode;
            string rawDir = string.Empty;
            if (postMode)
            {
                string? rawErr = ValidateRawBufferDir(out rawDir);
                if (rawErr is not null)
                {
                    _logger.LogWarning("Raw buffer validation failed: {err}", rawErr);
                    InAppToast.MainWindow?.Error(Lang.Starshot_RecordingFailed, rawErr);
                    session.Dispose(); // only the field-initialized semaphores are open yet
                    return;
                }
            }
            session.PostMode = postMode;

            // 3) 输出文件（截图文件夹 + 唯一化）
            session.FilePath = ResolveOutputPath(session.OutWidth, session.OutHeight);
            session.Muxer = Mp4Muxer.Open(session.FilePath);
            session.Encoder = new VideoEncoder(
                hdr,
                session.OutWidth,
                session.OutHeight,
                60,
                1,
                crf,
                bitrateKbps,
                keyint,
                0, // bframes=0：输出序=显示序，muxer 免 ctts
                threads,
                x265Preset
            );
            session.Processor = new FrameProcessor(
                hdr,
                cropX,
                cropY,
                session.OutWidth,
                session.OutHeight,
                fastConvert,
                simdKernelIndex,
                workerCount
            );
            // 解耦管线：泵做回读 + 转换 (双缓冲 ping-pong)，编码线程消费 job
            // 每组处理平面带独立信号量，编码线程读完释放，泵绝不覆盖还在被读的平面
            session.ProcessorB = new FrameProcessor(
                hdr,
                cropX,
                cropY,
                session.OutWidth,
                session.OutHeight,
                fastConvert,
                simdKernelIndex,
                workerCount
            );
            session.Device = CanvasDevice.GetSharedDevice();
            // 会话独占的回读缓冲（整帧尺寸：SDR RGBA8 / HDR R16G16B16A16Float），
            // 每帧不再做 66MB 级 LOH 分配
            session.PixelBuf = new byte[
                session.CaptureWidth * session.CaptureHeight * (hdr ? 8 : 4)
            ];
            // 有界 job 通道 = 背压（满 → 泵等待），不丢帧
            session.EncodeJobs = Channel.CreateBounded<EncodeJob>(
                new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.Wait }
            );
            // Encoder output buffer: one frame's NALs per in-loop call (a 4K veryfast
            // I-frame can approach 4 MB; 8 MB keeps headroom) and the initial flush
            // buffer (the flush loop grows it on overflow).
            session.EncodeBuf = new byte[8 << 20];

            // L5: pin the encoder inputs once per session (set A/B planes + EncodeBuf);
            // the encode thread then uses the native-pointer encode overloads
            byte[] ay,
                au,
                av;
            byte[] by,
                bu,
                bv;
            if (hdr)
            {
                var hpa = session.Processor.HdrPlanes();
                var hpb = session.ProcessorB.HdrPlanes();
                (ay, au, av) = (hpa.Y, hpa.U, hpa.V);
                (by, bu, bv) = (hpb.Y, hpb.U, hpb.V);
            }
            else
            {
                // SDR: two planes (Y + interleaved UV); the UV array fills the V slot
                var spa = session.Processor.SdrPlanes();
                var spb = session.ProcessorB.SdrPlanes();
                (ay, au, av) = (spa.Y, spa.UV, spa.UV);
                (by, bu, bv) = (spb.Y, spb.UV, spb.UV);
            }
            session.PlanePinsA = new PinnedEncodeInputs(ay, au, av, session.EncodeBuf);
            session.PlanePinsB = new PinnedEncodeInputs(by, bu, bv, session.EncodeBuf);

            if (postMode)
            {
                // The raw file: the final MP4's stem, ~-prefixed in the buffer directory
                // (looks like a temp file; a stale one from a crashed session is swept by
                // the next start, like the muxer's stale-temp sweep). A failing open
                // (drive/permission) throws -> the catch below disposes the session and
                // the outer catch toasts the error.
                session.RawPath = Path.Combine(
                    rawDir,
                    $"~{BuildOutputName(session.OutWidth, session.OutHeight)}.raw"
                );
                session.RawWriter = RawCaptureFile.Writer.Open(
                    session.RawPath,
                    session.OutWidth,
                    session.OutHeight,
                    hdr,
                    60,
                    1
                );
            }

            // 4) 音频（WASAPI 回环；faac 初始化失败（高采样率等）降级为无音频）
            if (AppConfig.VideoRecordAudioEnabled)
            {
                try
                {
                    int bitRate = quality switch
                    {
                        0 => 160_000,
                        2 => 64_000,
                        _ => 128_000,
                    };
                    session.Audio = new WasapiLoopbackSource(
                        bitRate,
                        2,
                        _logger,
                        AppConfig.VideoRecordAudioDeviceId
                    );
                    session.Audio.AacFrame += OnAacFrame;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WASAPI loopback unavailable, recording without audio");
                    session.Audio?.Dispose();
                    session.Audio = null;
                }
            }

            // 5) 捕获源 + 启动（音频先起，保证首包不晚于视频）
            var pixelFormat = hdr
                ? DirectXPixelFormat.R16G16B16A16Float
                : DirectXPixelFormat.R8G8B8A8UIntNormalized;
            session.Capture = WgcCaptureSource.Create(
                item,
                pixelFormat,
                cursorCapture: true,
                fpsCap,
                minUpdateIntervalMs,
                _logger,
                OnFrame,
                CancellationToken.None
            );
            // L5: dedicated encode thread at AboveNormalPriority (scheduling latency under
            // load). A dedicated Thread, not Task.Run: re-prioritizing a thread-pool thread
            // would affect other work scheduled onto it later.
            // Post-encode mode: the consumer writes the converted frames to the raw buffer
            // (disk instead of CPU); the encoder itself runs later in the drain.
            var encodeTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var encodeThread = new Thread(() =>
            {
                try
                {
                    Task loop = postMode ? RawWriteLoopAsync(session) : EncodeLoopAsync(session);
                    loop.GetAwaiter().GetResult();
                    encodeTcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    encodeTcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = postMode ? "VideoRawWrite" : "VideoEncode",
            };
            encodeThread.Start();
            session.EncodeTask = encodeTcs.Task;
            session.Audio?.Start();
            session.Capture.Start();

            lock (_sessionLock)
            {
                _session = session;
            }
            IsRecording = true;
            Target = target;
            _startedUtc = DateTime.UtcNow;
            _logger.LogInformation(
                "Recording started: {target} {w}x{h} {fmt} crf={crf} br={br} preset={preset} fpsCap={fps} minUpd={minUpd} audio={audio} mode={mode} -> {path}",
                target,
                session.OutWidth,
                session.OutHeight,
                hdr ? "HEVC-HDR10" : "H264-SDR",
                crf,
                bitrateKbps,
                hdr ? x265Preset : "veryfast",
                fpsCap,
                minUpdateIntervalMs,
                session.Audio is not null,
                postMode ? "post-encode" : "live",
                session.FilePath
            );
            if (postMode)
            {
                _logger.LogInformation("Raw capture buffer: {raw}", session.RawPath);
            }
            RecordingChanged?.Invoke();
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 解析录制目标。region 走区域选区覆盖层；full/window 取前台窗口（full 用其所在屏）。
    /// </summary>
    private async Task<(
        GraphicsCaptureItem Item,
        int CropX,
        int CropY,
        int CropW,
        int CropH,
        Microsoft.UI.DisplayId DisplayId
    )?> ResolveTargetAsync(VideoRecordTarget target)
    {
        if (target is VideoRecordTarget.FullScreen or VideoRecordTarget.Window)
        {
            nint hwnd = (nint)User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                InAppToast.MainWindow?.Warning(Lang.Starshot_RecordingNoForeground);
                return null;
            }
            if (target == VideoRecordTarget.Window)
            {
                if (User32.IsIconic(hwnd))
                {
                    InAppToast.MainWindow?.Warning(Lang.Starshot_RecordingWindowMinimized);
                    return null;
                }
                var item = ScreenCaptureHelper.CreateGraphicsCaptureItemForWindow(hwnd);
                HMONITOR mon = User32.MonitorFromWindow(
                    hwnd,
                    User32.MonitorFlags.MONITOR_DEFAULTTONEAREST
                );
                return (
                    item,
                    0,
                    0,
                    item.Size.Width,
                    item.Size.Height,
                    new Microsoft.UI.DisplayId((ulong)mon.DangerousGetHandle())
                );
            }
            HMONITOR mon2 = User32.MonitorFromWindow(
                hwnd,
                User32.MonitorFlags.MONITOR_DEFAULTTONEAREST
            );
            var item2 = ScreenCaptureHelper.CreateGraphicsCaptureItemForMonitor(
                mon2.DangerousGetHandle()
            );
            return (
                item2,
                0,
                0,
                item2.Size.Width,
                item2.Size.Height,
                new Microsoft.UI.DisplayId((ulong)mon2.DangerousGetHandle())
            );
        }

        // region：静态选区（虚拟屏幕物理像素，原点=虚拟屏幕左上角）
        Windows.Foundation.Rect? sel = await ScreenCaptureService.SelectRecordingRegionAsync();
        if (sel is null)
            return null; // 用户取消，静默
        int vx = User32.GetSystemMetrics((User32.SystemMetric)76);
        int vy = User32.GetSystemMetrics((User32.SystemMetric)77);
        int selW = (int)sel.Value.Width;
        int selH = (int)sel.Value.Height;
        int ax = vx + (int)sel.Value.X;
        int ay = vy + (int)sel.Value.Y;
        int bx = ax + selW - 1;
        int by = ay + selH - 1;

        // 选区须整体落在单一显示器内（OuterBounds 为虚拟屏幕绝对坐标）
        // (变量名用 display, 避免与上方窗口分支的 HMONITOR mon 冲突)
        // for 循环按下标取，不用 foreach（CsWinRT 投影的 IReadOnlyList 枚举器有 bug，
        // 与区域截图同样的绕法）
        var displays = DisplayArea.FindAll();
        DisplayArea? display = null;
        for (int i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            var b = d.OuterBounds;
            if (
                b.X <= ax
                && ax < b.X + b.Width
                && b.Y <= ay
                && ay < b.Y + b.Height
                && b.X <= bx
                && bx < b.X + b.Width
                && b.Y <= by
                && by < b.Y + b.Height
            )
            {
                display = d;
                break;
            }
        }
        if (display is null)
        {
            InAppToast.MainWindow?.Warning(Lang.Starshot_RecordingRegionSpansDisplays);
            return null;
        }
        var item3 = ScreenCaptureHelper.CreateGraphicsCaptureItemForMonitor(
            (nint)display.DisplayId.Value
        );
        int cropX = ax - (int)display.OuterBounds.X;
        int cropY = ay - (int)display.OuterBounds.Y;
        // 保险夹到捕获面尺寸内
        int w = Math.Min(selW, item3.Size.Width - cropX);
        int h = Math.Min(selH, item3.Size.Height - cropY);
        if (w < 2 || h < 2)
        {
            InAppToast.MainWindow?.Warning(Lang.Starshot_RecordingRegionSpansDisplays);
            return null;
        }
        return (item3, cropX, cropY, w, h, display.DisplayId);
    }

    /// <summary>截图文件夹（与截图同源，不用子文件夹）+ Recording_{time}.mp4 唯一化。</summary>
    private static string ResolveOutputPath(int width, int height)
    {
        string folder = AppConfig.ScreenshotFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = Path.Join(AppConfig.UserDataFolder, "Screenshots");
        }
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        // 清上次崩溃/退出留下的残留临时文件（muxer 只清自己名字的，其余会永久留下；
        // 同时只允许一条录制，不会误删进行中的文件）
        try
        {
            foreach (var stale in Directory.EnumerateFiles(folder, "~*.v.tmp"))
                File.Delete(stale);
            foreach (var stale in Directory.EnumerateFiles(folder, "~*.a.tmp"))
                File.Delete(stale);
        }
        catch
        {
            // 清理失败不阻塞录制
        }
        string name = BuildOutputName(width, height);
        string path = Path.Combine(folder, name + ".mp4");
        if (!File.Exists(path))
            return path;
        string dir = folder;
        string stem = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}_{i}.mp4");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    /// <summary>Recording_{time}_{w}x{h} 文件名（不含扩展名；输出路径与 raw 缓冲共用同一 stem）。</summary>
    private static string BuildOutputName(int width, int height)
    {
        return ScreenCaptureService.BuildFileName(
            string.Empty,
            string.Empty,
            string.Empty,
            DateTimeOffset.Now,
            (uint)width,
            (uint)height,
            "Recording_{time}"
        );
    }

    /// <summary>
    /// Post-encode start guard: resolve the raw buffer directory (the VideoRecordRawBufferDir
    /// setting, or the recordings directory when unset), create it if needed, sweep a stale
    /// ~*.raw from a crashed session, and require a minimum of free space (8 GB - a sanity
    /// floor; 4K HDR 60 fps writes ~1.4 GB/s, so an HDD-backed buffer will drop frames).
    /// Returns null on success, an error message on failure.
    /// </summary>
    private string? ValidateRawBufferDir(out string dir)
    {
        dir = AppConfig.VideoRecordRawBufferDir;
        // Hidden debug override (record-test A/B; see performance.md, WGC root-cause
        // section): STARSHOT_RAW_BUFFER_DIR points the raw capture buffer at another
        // drive (e.g. the local NVMe) to measure the capture ceiling per drive.
        var rawDirEnv = Environment.GetEnvironmentVariable("STARSHOT_RAW_BUFFER_DIR");
        if (!string.IsNullOrWhiteSpace(rawDirEnv))
            dir = rawDirEnv;
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = AppConfig.ScreenshotFolder;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Join(AppConfig.UserDataFolder, "Screenshots");
            }
        }
        dir = Path.GetFullPath(dir);
        try
        {
            Directory.CreateDirectory(dir);
            // A crashed post-encode session leaves its raw file (the drain never ran);
            // only one recording at a time, so this cannot hit a live file.
            foreach (var stale in Directory.EnumerateFiles(dir, "~*.raw"))
                File.Delete(stale);
        }
        catch (Exception ex)
        {
            return $"The raw buffer folder is unavailable ({ex.Message}).";
        }
        const long MinFreeBytes = 8L << 30;
        try
        {
            var info = new DriveInfo(dir);
            if (!info.IsReady)
                return "The raw buffer drive is not ready.";
            if (info.AvailableFreeSpace < MinFreeBytes)
            {
                return $"Not enough free space for the raw buffer: {info.AvailableFreeSpace / (1L << 30)} GB free, at least 8 GB needed.";
            }
        }
        catch (Exception ex)
        {
            return $"Could not check the raw buffer drive ({ex.Message}).";
        }
        return null;
    }

    // ==================== 帧处理（WGC 泵线程：回读 + 转换；编码线程：编码 + 复接） ====================

    // Per-stage pipeline timing (STARSHOT_WGC_STAGE_TRACE=1): one Debug line per frame,
    // classifying the capture budget (readback / set-wait / convert / channel / raw write).
    private static readonly bool StageTrace =
        "1" == Environment.GetEnvironmentVariable("STARSHOT_WGC_STAGE_TRACE");

    private async Task OnFrame(Direct3D11CaptureFrame frame)
    {
        RecordingSession s;
        lock (_sessionLock)
        {
            s = _session;
        }
        if (s is null)
            return;

        var muxer = s.Muxer;
        var encoder = s.Encoder;
        var processorA = s.Processor;
        var processorB = s.ProcessorB;
        var device = s.Device;
        var jobs = s.EncodeJobs;
        if (
            muxer is null
            || encoder is null
            || processorA is null
            || processorB is null
            || device is null
            || jobs is null
        )
            return; // 会话未完成初始化（泵刚退出的边缘时序）

        long qpc = frame.SystemRelativeTime.Ticks; // QPC 100ns (投影为 TimeSpan)
        long t0 = 0,
            t1 = 0,
            t2 = 0,
            t3 = 0,
            t4 = 0;
        if (StageTrace)
            t0 = Stopwatch.GetTimestamp();
        if (!s.VideoBegun)
        {
            // 首帧定视频时间轴起点，声明 muxer 视频轨（头部 NAL 此时已生成）
            s.VideoBegun = true;
            s.FirstVideoQpc = qpc;
            muxer.BeginVideo(s.OutWidth, s.OutHeight, s.Hdr, encoder.GetHeaderBytes(), 0, 60, 1);
        }
        long pts = qpc - s.FirstVideoQpc;

        // 回读进会话独占缓冲 (GetPixelBytes 的写入型重载只接受 IBuffer: 按位图原生格式写紧排行:
        // SDR = RGBA8, HDR = R16G16B16A16Float; 无每帧 66MB 级分配, AsBuffer 只是包装会话缓冲)
        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface, 96);
        bitmap.GetPixelBytes(s.PixelBuf.AsBuffer(), 0, 0, s.CaptureWidth, s.CaptureHeight);
        if (StageTrace)
            t1 = Stopwatch.GetTimestamp();

        // 取空闲处理平面组 (ping-pong)：泵是单线程——先等编码线程释放这组的
        // 信号量再往里转换，然后切到另一组 (转换完即入队，不等编码)
        bool useA = s.NextSet == 0;
        var processor = useA ? processorA : processorB;
        var setSem = useA ? s.SetASem : s.SetBSem;
        s.NextSet ^= 1;
        await setSem.WaitAsync().ConfigureAwait(false);
        if (StageTrace)
            t2 = Stopwatch.GetTimestamp();

        if (s.Hdr)
        {
            // 转换过程顺带算出 clli Y 统计 (不再对 Y 平面做第二遍完整扫描, 见 FrameProcessor.ClliY)
            processor.ConvertHdr(s.PixelBuf, s.CaptureWidth, s.CaptureHeight);
            var clli = processor.ClliY;
            s.MinY10 = Math.Min(s.MinY10, clli.Min);
            s.MaxY10 = Math.Max(s.MaxY10, clli.Max);
            // maxFALL needs the MAX over frames of the per-frame average light level, not a
            // global average: a global PQ-domain mean folds dim frames into bright ones and
            // understates the brightest frame (for the flat-field test it read ~2405 nits
            // while the YELLOW frame alone averages ~9400). The per-frame mean Y code is exact
            // for a uniform frame (the PQ code IS the frame's light level) and is the standard
            // maxFALL approximation for general content.
            int px = s.OutWidth * s.OutHeight;
            if (px > 0)
                s.MaxFrameAvgY10 = Math.Max(s.MaxFrameAvgY10, clli.Mean);
        }
        else
        {
            processor.ConvertSdr(s.PixelBuf, s.CaptureWidth, s.CaptureHeight);
        }
        if (StageTrace)
            t3 = Stopwatch.GetTimestamp();

        // 交给编码线程 (有界通道 = 背压：满则泵等待)。编码线程读完平面后释放
        // SetSem (出队 ≠ 读完成，守卫平面的是信号量而不是通道)。
        var job = new EncodeJob
        {
            Processor = processor,
            Pts = pts,
            SetSem = setSem,
            Pins = useA ? s.PlanePinsA : s.PlanePinsB,
        };
        try
        {
            await jobs.Writer.WriteAsync(job).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // 停止流程已把通道写完 (录制收尾)，释放该组供会话 Dispose
            setSem.Release();
        }
        if (StageTrace)
        {
            t4 = Stopwatch.GetTimestamp();
            double f = 1000.0 / Stopwatch.Frequency;
            _logger.LogDebug(
                "stage trace: readback {rb:0.0} ms, setWait {sw:0.0} ms, convert {cv:0.0} ms, chan {ch:0.0} ms",
                (t1 - t0) * f,
                (t2 - t1) * f,
                (t3 - t2) * f,
                (t4 - t3) * f
            );
        }
    }

    /// <summary>
    /// 编码线程：消费泵的转换 job，做 x264/x265 编码 + 复接 + pts 记账，
    /// 读完平面后释放该组信号量。视频状态 (PendingVideoPts / EncodeBuf / muxer 视频轨)
    /// 只被本线程与停止流程的冲刷 (在本线程退出后运行) 触碰，
    /// 维持 muxer 视频/音频分线程的约定。
    /// </summary>
    private async Task EncodeLoopAsync(RecordingSession s)
    {
        var muxer = s.Muxer;
        var encoder = s.Encoder;
        var jobs = s.EncodeJobs;
        if (muxer is null || encoder is null || jobs is null)
            return; // 正常不会：编码任务在这些字段就绪后才启动
        try
        {
            await foreach (var job in jobs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                long pts = job.Pts;
                try
                {
                    // 编码（bframes=0：每帧至多一个输出）。L5: pre-pinned session pointers
                    // (CodecsNative.X26*EncodeNative - no per-frame GCHandle alloc/free)
                    int rc;
                    bool isSync;
                    int outBytes = 0;
                    if (s.Hdr)
                    {
                        var p = job.Processor.HdrPlanes();
                        rc = encoder.EncodeHdrPinned(
                            job.Pins.Y,
                            p.StrideY,
                            job.Pins.U,
                            p.StrideU,
                            job.Pins.V,
                            p.StrideV,
                            pts,
                            job.Pins.Out,
                            s.EncodeBuf.Length,
                            s.EncodeBuf,
                            out outBytes,
                            out isSync
                        );
                    }
                    else
                    {
                        var p = job.Processor.SdrPlanes();
                        rc = encoder.EncodeSdrPinned(
                            job.Pins.Y,
                            p.StrideY,
                            job.Pins.U,
                            p.StrideUV,
                            pts,
                            job.Pins.Out,
                            s.EncodeBuf.Length,
                            s.EncodeBuf,
                            out outBytes,
                            out isSync
                        );
                    }
                    // 提交一帧输入, 记下它的 pts (编码器内部 frame-threads 缓冲使输出滞后输入)
                    s.PendingVideoPts.Enqueue(pts);

                    if (rc > 0)
                    {
                        // 本输出帧 = 最早尚未出码的输入帧 (bframes=0: 输出序=输入序, 一一对应)
                        long outPts =
                            s.PendingVideoPts.Count > 0 ? s.PendingVideoPts.Dequeue() : pts;
                        muxer.AddVideoSample(outPts, s.EncodeBuf, outBytes, isSync);
                        s.VideoSampleCount++;
                        s.LastVideoPts = outPts;
                    }
                }
                finally
                {
                    // 平面不再被读——泵可以复用这组
                    job.SetSem.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Encode loop failed");
        }
    }

    /// <summary>
    /// Post-encode consumer thread: consumes the pump's converted jobs and appends each
    /// frame's planes verbatim to the raw buffer file (disk instead of CPU), then releases
    /// the plane set's semaphore. The encoder itself never runs during capture - the
    /// post-stop drain re-reads the raw file and encodes. A failed write (full disk etc.)
    /// drops the frame and keeps going (the drain encodes the frames that made it to
    /// disk); the first failure is logged.
    /// </summary>
    private async Task RawWriteLoopAsync(RecordingSession s)
    {
        var writer = s.RawWriter;
        var jobs = s.EncodeJobs;
        if (writer is null || jobs is null)
            return; // 正常不会：raw 任务在这些字段就绪后才启动
        _logger.LogInformation("Raw write loop started: {raw}", s.RawPath);
        bool firstLogged = false;
        bool writeFailed = false;
        long w0 = 0,
            w1 = 0;
        try
        {
            await foreach (var job in jobs.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (StageTrace)
                        w0 = Stopwatch.GetTimestamp();
                    if (s.Hdr)
                    {
                        var p = job.Processor.HdrPlanes();
                        writer.WriteFrame(job.Pts, p.Y, p.U, p.V);
                    }
                    else
                    {
                        var p = job.Processor.SdrPlanes();
                        writer.WriteFrame(job.Pts, p.Y, p.UV, null);
                    }
                    if (StageTrace)
                    {
                        w1 = Stopwatch.GetTimestamp();
                        _logger.LogDebug(
                            "raw write: {ms:0.0} ms ({fmt})",
                            (w1 - w0) * 1000.0 / Stopwatch.Frequency,
                            s.Hdr ? "P010" : "NV12"
                        );
                    }
                    if (!firstLogged)
                    {
                        firstLogged = true;
                        _logger.LogDebug(
                            "Raw capture: first frame written ({w}x{h} {fmt})",
                            s.OutWidth,
                            s.OutHeight,
                            s.Hdr ? "P010" : "NV12"
                        );
                    }
                }
                catch (Exception ex)
                {
                    if (!writeFailed)
                    {
                        _logger.LogError(ex, "Raw write failed; later frames are dropped");
                        writeFailed = true;
                    }
                }
                finally
                {
                    // Plane guard, same as the live encode thread: the pump waits on this
                    // set's semaphore before it may overwrite the planes
                    job.SetSem.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Raw write loop failed");
        }
        _logger.LogInformation(
            "Raw write loop done: {count} frames in {raw}{dropped}",
            writer.FrameCount,
            s.RawPath,
            writeFailed ? " (writes failed - some frames dropped)" : ""
        );
    }

    // ==================== 音频（WASAPI 轮询线程） ====================

    private void OnAacFrame(byte[] buf, int len, long qpc)
    {
        RecordingSession s;
        lock (_sessionLock)
        {
            s = _session;
        }
        if (s is null || s.Audio is null)
            return;
        var muxer = s.Muxer;
        if (muxer is null)
            return;
        if (!s.AudioBegun)
        {
            // 音频轨声明：首包相对视频时间轴的偏移（早于视频首帧则钳 0）
            s.AudioBegun = true;
            muxer.BeginAudio(
                s.Audio.SampleRate,
                s.Audio.Channels,
                s.Audio.Asc,
                Math.Max(0, qpc - s.FirstVideoQpc)
            );
        }
        muxer.AddAudioSample(buf, len);
    }

    // ==================== 停止 ====================

    public async Task StopAsync()
    {
        if (!IsRecording)
            return;
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(10)))
            return;
        try
        {
            RecordingSession s;
            lock (_sessionLock)
            {
                s = _session;
                _session = null;
            }
            IsRecording = false;
            Target = null;
            RecordingChanged?.Invoke();

            if (s is not null)
            {
                // 1) 停捕获（泵退出；等它读完队列，避免与编码器收尾并发）
                s.Capture?.Dispose();
                if (s.Capture is not null)
                {
                    await Task.WhenAny(s.Capture.PumpTask, Task.Delay(3000));
                }
                // 1b) 排空 job 队列 (有界) 并等消费者线程退出：之后视频状态
                //     (PendingVideoPts / muxer 视频轨) 只有停止流程会碰
                bool encodeDrained = true;
                if (s.EncodeJobs is not null && s.EncodeTask is not null)
                {
                    s.EncodeJobs.Writer.TryComplete();
                    await Task.WhenAny(s.EncodeTask, Task.Delay(5000));
                    encodeDrained = s.EncodeTask.IsCompleted;
                    if (!encodeDrained)
                    {
                        _logger.LogWarning(
                            "Encode thread did not drain within 5s; discarding the recording"
                        );
                    }
                }
                // 2) 停音频 + faac 冲刷
                s.Audio?.StopAndFlush();

                if (s.PostMode)
                {
                    // 3) Post-encode: every converted frame is in the raw buffer (the encoder
                    //    never ran during capture). The actual encode + finalize runs in a
                    //    background drain (~5x realtime at 4K), so StopAsync returns fast.
                    //    The drain owns the session (disposes it in its finally).
                    IsFinalizing = true;
                    _drainTask = PostEncodeDrainAsync(s);
                    _logger.LogInformation(
                        "Recording captured to raw buffer {raw}; encoding in the background -> {mp4}",
                        s.RawPath,
                        s.FilePath
                    );
                    RecordingChanged?.Invoke();
                    return; // the gate is released in the finally below
                }

                string? filePath = null;
                try
                {
                    if (encodeDrained)
                    {
                        filePath = FlushAndFinalize(s);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Recording finalize failed");
                }
                finally
                {
                    s.Dispose();
                }

                if (filePath is not null)
                {
                    _logger.LogInformation("Recording saved: {path}", filePath);
                    InAppToast.MainWindow?.Success(Lang.Starshot_RecordingSaved, filePath);
                    if (AppConfig.VideoRecordOpenFolderAfterStop)
                    {
                        try
                        {
                            Process.Start(
                                new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                                {
                                    UseShellExecute = true,
                                }
                            );
                        }
                        catch { }
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Encoder flush + FinalizeFile, shared by the live stop flow and the post-encode drain.
    /// Returns the saved file path, or null when nothing was saved (no samples / flush
    /// failed) - the caller still disposes the session, which cleans the muxer temps.
    /// </summary>
    private string? FlushAndFinalize(RecordingSession s)
    {
        var muxer = s.Muxer;
        var encoder = s.Encoder;
        if (muxer is null || encoder is null)
            return null;

        // The encoder's internal frame threads (x264/x265) buffer a backlog of frames;
        // a flush round emits their NALs all at once, which a fixed-size session buffer
        // may not hold (observed: a fast 4K SDR drain left a 17-frame backlog against
        // the old 4 MB buffer, and the failed flush dropped the tail). On overflow the
        // native layer reports the recoverable partial (complete NALs only): mux it,
        // grow the buffer, and retry - the remaining frames are still inside the
        // encoder. Emitted NALs must be split into one sample per access unit (a
        // sample holding several frames makes FFmpeg's HEVC decoder report "Two
        // slices"); each frame's pts comes in order from the input pts queue
        // (bframes=0: output=input).
        byte[] flushBuf = s.EncodeBuf;
        while (true)
        {
            int rc = encoder.Flush(flushBuf, out int partial);
            if (rc == 0)
                break; // drained
            if (rc > 0)
            {
                MuxFlushChunk(s, flushBuf, rc);
                continue; // the encoder may still hold queued frames
            }
            // rc < 0: buffer overflow (or a hard encoder error, in which case the
            // retries below just bounce until the cap). Recover the partial first.
            if (partial > 0)
                MuxFlushChunk(s, flushBuf, partial);
            if (flushBuf.Length >= 64 << 20)
            {
                _logger.LogError(
                    "Video encoder flush buffer hit the 64 MB cap with frames still pending; finalizing with encoded samples so far"
                );
                break;
            }
            _logger.LogWarning(
                "Video encoder flush overflowed the {old} B buffer ({partial} B partial recovered); growing to {new} B",
                flushBuf.Length,
                partial,
                flushBuf.Length * 2
            );
            flushBuf = new byte[flushBuf.Length * 2];
        }

        if (s.VideoSampleCount > 0)
        {
            (ushort MaxCLL, ushort MaxFALL)? clli = null;
            if (s.Hdr)
            {
                ushort cll = (ushort)
                    Math.Min(ushort.MaxValue, (int)Math.Round(PqCodeToNits(s.MaxY10)));
                // maxFALL = the brightest frame's average light level (nits), from the
                // max of the per-frame mean Y codes (see MaxFrameAvgY10).
                ushort fall = (ushort)
                    Math.Min(ushort.MaxValue, (int)Math.Round(PqCodeToNits(s.MaxFrameAvgY10)));
                clli = (cll, fall);
            }
            muxer.FinalizeFile(clli);
            return s.FilePath;
        }

        _logger.LogWarning(
            "Recording stopped without any video sample; discarding {path}",
            s.FilePath
        );
        return null;
    }

    /// <summary>One flushed NAL chunk -> one MP4 sample per access unit (see <see cref="FlushAndFinalize"/>).</summary>
    private static void MuxFlushChunk(RecordingSession s, byte[] buf, int len)
    {
        var muxer = s.Muxer;
        if (muxer is null)
            return;
        foreach (var (uOff, uLen, uSync) in SplitAccessUnits(buf, len, s.Hdr))
        {
            long uPts;
            if (s.PendingVideoPts.Count > 0)
            {
                uPts = s.PendingVideoPts.Dequeue();
            }
            else
            {
                uPts = s.LastVideoPts += 10_000_000L / 60;
            }
            muxer.AddVideoSample(uPts, buf, uOff, uLen, uSync);
            s.VideoSampleCount++;
            s.LastVideoPts = uPts;
        }
    }

    /// <summary>
    /// Post-encode drain (background task, spawned by StopAsync): re-read the raw frames
    /// and run the real encoder, ping-ponging the two processor plane sets exactly like
    /// the live encode thread; then flush + FinalizeFile, delete the raw file, and toast.
    /// Owns the session (disposes it in the finally, which also cleans the muxer temps
    /// on failure). The raw file is deleted on every exit - the MP4 is the only truth.
    /// Timing: ~5x realtime at 4K (12 fps encoder), ~2.1x at 1440p.
    /// </summary>
    private async Task PostEncodeDrainAsync(RecordingSession s)
    {
        try
        {
            var muxer = s.Muxer;
            var encoder = s.Encoder;
            var raw = s.RawWriter;
            if (
                muxer is null
                || encoder is null
                || raw is null
                || s.Processor is null
                || s.ProcessorB is null
            )
            {
                _logger.LogError("Post-encode drain: session incomplete, cannot finalize");
                return;
            }
            // The frames are read back through the capture's own stream (the file is
            // never re-opened - a separate open can fail on machines where a file-system
            // filter locks newly written files; see RawCaptureFile).
            raw.SeekToFrames();
            _logger.LogInformation("Post-encode drain: {raw} -> {mp4}", s.RawPath, s.FilePath);
            var sw = Stopwatch.StartNew();
            int frames = 0;
            bool useA = true;
            while (true)
            {
                // Ping-pong: the encoder's frame threads may still read the previous
                // frame's planes, so the next frame is converted into the other set
                // (the same pattern the live pump uses).
                var proc = useA ? s.Processor : s.ProcessorB;
                var pins = useA ? s.PlanePinsA : s.PlanePinsB;
                useA = !useA;

                int rc;
                bool isSync;
                int outBytes = 0;
                long pts;
                if (s.Hdr)
                {
                    var p = proc.HdrPlanes();
                    if (!raw.TryReadNext(out pts, p.Y, p.U, p.V))
                        break; // clean EOF / torn tail: the complete frames only
                    rc = encoder.EncodeHdrPinned(
                        pins.Y,
                        p.StrideY,
                        pins.U,
                        p.StrideU,
                        pins.V,
                        p.StrideV,
                        pts,
                        pins.Out,
                        s.EncodeBuf.Length,
                        s.EncodeBuf,
                        out outBytes,
                        out isSync
                    );
                }
                else
                {
                    var p = proc.SdrPlanes();
                    if (!raw.TryReadNext(out pts, p.Y, p.UV, null))
                        break;
                    rc = encoder.EncodeSdrPinned(
                        pins.Y,
                        p.StrideY,
                        pins.U,
                        p.StrideUV,
                        pts,
                        pins.Out,
                        s.EncodeBuf.Length,
                        s.EncodeBuf,
                        out outBytes,
                        out isSync
                    );
                }

                // Same accounting as the live encode thread (bframes=0: output=input)
                s.PendingVideoPts.Enqueue(pts);
                if (rc > 0)
                {
                    long outPts = s.PendingVideoPts.Count > 0 ? s.PendingVideoPts.Dequeue() : pts;
                    muxer.AddVideoSample(outPts, s.EncodeBuf, outBytes, isSync);
                    s.VideoSampleCount++;
                    s.LastVideoPts = outPts;
                }
                frames++;
                if (frames % 300 == 0)
                {
                    _logger.LogInformation(
                        "Post-encode drain: {frames} frames in {secs:0}s",
                        frames,
                        sw.Elapsed.TotalSeconds
                    );
                }
            }

            string? saved = FlushAndFinalize(s);
            if (saved is not null)
            {
                _logger.LogInformation(
                    "Post-encode drain done: {frames} frames in {secs:0}s -> {path}",
                    frames,
                    sw.Elapsed.TotalSeconds,
                    saved
                );
                InAppToast.MainWindow?.Success(Lang.Starshot_RecordingSaved, saved);
                if (AppConfig.VideoRecordOpenFolderAfterStop)
                {
                    try
                    {
                        Process.Start(
                            new ProcessStartInfo("explorer.exe", $"/select,\"{saved}\"")
                            {
                                UseShellExecute = true,
                            }
                        );
                    }
                    catch { }
                }
            }
            else
            {
                _logger.LogWarning(
                    "Post-encode drain produced no samples (raw had {frames} frames); raw buffer deleted",
                    frames
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-encode drain failed");
            InAppToast.MainWindow?.Error(Lang.Starshot_RecordingFailed, ex.Message);
        }
        finally
        {
            // The drain owns the session: encoder/muxer (its Dispose cleans the temp
            // files on failure)/processors/pins/semaphores - and the raw writer's
            // stream; the raw file can only be deleted once that stream is closed.
            s.Dispose();
            // The raw buffer is deleted on every exit - the MP4 is the only truth.
            TryDeleteFile(s.RawPath);
            IsFinalizing = false;
            _drainTask = null;
            RecordingChanged?.Invoke();
        }
    }

    /// <summary>Best-effort delete (a failure never blocks the recording flow; a stale raw is swept at the next start).</summary>
    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 把 [len4][NAL] 流按 access unit 拆分 (冲刷缓冲一次含多帧时用):
    /// 每个 VCL NAL 是一个独立帧 (bframes=0, 输出序=输入序), 前置非 VCL NAL (SEI 等)
    /// 归并到后续帧, 后置的归并到当前帧。
    /// VCL 类型: HEVC = 1 (非 IDR) / 19/20/21 (IRAP, x265 4.3 编号); H.264 = 1 (非 IDR) / 5 (IDR)。
    /// </summary>
    private static List<(int Offset, int Len, bool Sync)> SplitAccessUnits(
        byte[] buf,
        int len,
        bool hdr
    )
    {
        var units = new List<(int Offset, int Len, bool Sync)>();
        int unitStart = 0;
        bool sawVcl = false;
        bool unitOpen = false; // 正在积攒前置非 VCL NAL
        bool sync = false;
        int off = 0;
        while (off + 4 <= len)
        {
            int nlen =
                (buf[off] << 24 | buf[off + 1] << 16 | buf[off + 2] << 8 | buf[off + 3])
                & 0x7FFFFFFF;
            if (nlen < 1 || off + 4 + nlen > len)
                break;
            int first = buf[off + 4];
            int type = hdr ? (first >> 1) & 0x3F : first & 0x1F;
            bool isVcl = hdr ? type is 1 or 19 or 20 or 21 : type is 1 or 5;
            if (isVcl)
            {
                if (sawVcl)
                    units.Add((unitStart, off - unitStart, sync));
                if (!unitOpen)
                    unitStart = off;
                sawVcl = true;
                unitOpen = false;
                sync = hdr ? type is 19 or 20 or 21 : type == 5;
            }
            else if (!sawVcl)
            {
                // 前置非 VCL NAL: 归并到后续帧
                if (!unitOpen)
                    unitStart = off;
                unitOpen = true;
            }
            off += 4 + nlen;
        }
        if (sawVcl)
            units.Add((unitStart, off - unitStart, sync));
        return units;
    }

    /// <summary>
    /// PQ code value (10-bit, 10000-nit master) to nits: exact inverse of FrameProcessor's PQ LUT
    /// (ST 2084 EOTF, normalized domain, same constants as BuildPqLut).
    /// </summary>
    private static double PqCodeToNits(int code10)
    {
        // OETF (BuildPqLut): pq = ((C1 + C2*y^M1) / (1 + C3*y^M1))^M2 with y = nits/10000.
        // Inversion: p = pq^(1/M2); t = (p - C1) / (C2 - C3*p) = y^M1; nits = 10000 * t^(1/M1).
        // (The previous version mixed nits-domain constants with the normalized-domain formula
        // and mapped every nonzero code to ~9988 nits; sanity: code 629 -> ~277 nits, 430 -> ~40.)
        const double M1 = 0.25 * 2610.0 / 4096.0; // 0.1593017578125
        const double M2 = 128.0 * 2523.0 / 4096.0; // 78.84375
        const double C1 = 3424.0 / 4096.0; // 0.8359375
        const double C2 = 32.0 * 2413.0 / 4096.0; // 18.8515625
        const double C3 = 32.0 * 2392.0 / 4096.0; // 18.6875

        double pq = Math.Clamp(code10, 0, 1023) / 1023.0;
        double p = Math.Pow(pq, 1.0 / M2);
        double t = (p - C1) / (C2 - C3 * p);
        if (t <= 0)
            return 0;
        return 10000.0 * Math.Pow(t, 1.0 / M1);
    }

    // ==================== 会话 ====================

    private sealed class RecordingSession : IDisposable
    {
        public GraphicsCaptureItem? Item;

        public WgcCaptureSource? Capture;

        public FrameProcessor? Processor;

        public VideoEncoder? Encoder;

        public WasapiLoopbackSource? Audio;

        public Mp4Muxer? Muxer;

        public bool Hdr;

        public int CropX;

        public int CropY;

        public int CropW;

        public int CropH;

        public int OutWidth;

        public int OutHeight;

        public int CaptureWidth;

        public int CaptureHeight;

        public string FilePath = string.Empty;

        /// <summary>Post-encode mode: the encoder does not run during capture; a background drain after stop encodes the raw buffer.</summary>
        public bool PostMode;

        /// <summary>Post-encode mode: the raw capture buffer file (~Recording_*.raw in the buffer directory).</summary>
        public string RawPath = string.Empty;

        /// <summary>Post-encode mode: the raw capture writer (opened at start, closed via this session's Dispose).</summary>
        public RawCaptureFile.Writer? RawWriter;

        public byte[] EncodeBuf = Array.Empty<byte>();

        /// <summary>会话独占回读缓冲（整帧尺寸：SDR RGBA8 / HDR R16G16B16A16Float）。</summary>
        public byte[] PixelBuf = Array.Empty<byte>();

        public CanvasDevice? Device;

        /// <summary>第二组处理平面（与 <see cref="Processor"/> ping-pong）。</summary>
        public FrameProcessor? ProcessorB;

        /// <summary>ping-pong 组选择 (0=A, 1=B)；仅泵线程读写。</summary>
        public int NextSet;

        public SemaphoreSlim SetASem { get; } = new(1, 1);

        public SemaphoreSlim SetBSem { get; } = new(1, 1);

        /// <summary>泵 → 编码线程的 job 队列（有界 = 背压）。</summary>
        public Channel<EncodeJob>? EncodeJobs;

        public Task? EncodeTask;

        public bool VideoBegun;

        public bool AudioBegun;

        public long FirstVideoQpc;

        public long LastVideoPts;

        public int VideoSampleCount;

        /// <summary>
        /// 已提交未出码的输入帧 pts (bframes=0: 输出序=输入序, 一一对应)。
        /// 编码器内部 frame-threads 会缓冲若干帧, 输出帧必须取对应输入帧的 pts,
        /// 不能用"当前帧"的 pts (否则整轨时间轴偏移、冲刷帧时间被压扁)。
        /// </summary>
        public readonly Queue<long> PendingVideoPts = new();

        public int MinY10 = 1023;

        public int MaxY10;

        public int MaxFrameAvgY10;

        // ==================== L5 (per-session pinned encoder inputs) ====================

        public PinnedEncodeInputs PlanePinsA = null!;

        public PinnedEncodeInputs PlanePinsB = null!;

        public void Dispose()
        {
            Audio?.Dispose();
            Encoder?.Dispose();
            Muxer?.Dispose();
            RawWriter?.Dispose();
            Processor?.Dispose();
            ProcessorB?.Dispose();
            PlanePinsA?.Dispose();
            PlanePinsB?.Dispose();
            // 让还阻塞在通道上的编码线程能退出（启动失败路径）
            try
            {
                EncodeJobs?.Writer.TryComplete();
            }
            catch { }
            try
            {
                SetASem.Dispose();
                SetBSem.Dispose();
            }
            catch { }
            // Capture 可能已在停止流程里释放过（Dispose 幂等）
            try
            {
                Capture?.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// L5: per-session pre-pinned encoder input pointers (the set A/B plane arrays + the
    /// shared EncodeBuf). The encode thread calls the native-pointer overloads
    /// (VideoEncoder.EncodeHdrPinned / EncodeSdrPinned over CodecsNative.X26*EncodeNative)
    /// instead of the 4 GCHandle alloc/free pairs the byte[] overloads make per frame.
    /// SDR uses the V slot for the interleaved UV array.
    /// </summary>
    private sealed class PinnedEncodeInputs : IDisposable
    {
        private readonly GCHandle _y;
        private readonly GCHandle _u;
        private readonly GCHandle _v;
        private readonly GCHandle _o;

        public nint Y { get; }

        public nint U { get; }

        public nint V { get; }

        public nint Out { get; }

        public PinnedEncodeInputs(byte[] y, byte[] u, byte[] v, byte[] outBuf)
        {
            _y = GCHandle.Alloc(y, GCHandleType.Pinned);
            _u = GCHandle.Alloc(u, GCHandleType.Pinned);
            _v = GCHandle.Alloc(v, GCHandleType.Pinned);
            _o = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
            Y = _y.AddrOfPinnedObject();
            U = _u.AddrOfPinnedObject();
            V = _v.AddrOfPinnedObject();
            Out = _o.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            _y.Free();
            _u.Free();
            _v.Free();
            _o.Free();
        }
    }

    /// <summary>
    /// Pump → encode-thread handoff: a converted plane set (the encode thread releases
    /// SetSem after reading the planes).
    /// </summary>
    private sealed class EncodeJob
    {
        public FrameProcessor Processor = null!;

        public long Pts;

        public SemaphoreSlim SetSem = null!;

        /// <summary>L5: the plane set's pre-pinned pointers.</summary>
        public PinnedEncodeInputs Pins = null!;
    }
}
