using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Starshot.Features.Screenshot;
using Vanara.PInvoke;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Starshot.Features.Recording;

/// <summary>
/// 长时 WGC 捕获源：FreeThreaded 帧池 (深度 4) + 捕获会话，FrameArrived 线程只负责
/// 取帧入 Channel (丢弃最旧、保留最新)，泵线程消费——处理慢时不阻塞 WGC 回调。
/// 泵 await 调用方的 onFrame (readback + convert only; encoding is offloaded to a
/// dedicated thread) and disposes the frame after onFrame completes, so the pool
/// texture recycles as soon as the readback is done.
/// </summary>
internal sealed class WgcCaptureSource : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Channel<Direct3D11CaptureFrame> _channel;
    private readonly Task _pump;
    private readonly ILogger<VideoRecordService> _logger;

    private bool _disposed;

    /// <summary>
    /// fpsCap = 0 不限帧率 (跟显示器原生刷新)；否则按 QPC 时间戳跳过超出上限的帧。
    /// </summary>
    private WgcCaptureSource(
        GraphicsCaptureItem item,
        DirectXPixelFormat format,
        bool cursorCapture,
        int fpsCap,
        int minUpdateIntervalMs,
        ILogger<VideoRecordService> logger,
        Func<Direct3D11CaptureFrame, Task> onFrame,
        CancellationToken stopToken
    )
    {
        _item = item;
        _logger = logger;

        // 池深度 4：泵处理一帧的时间差由池内排队吸收，超出后 FrameArrived 侧丢最旧
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            CanvasDevice.GetSharedDevice(),
            format,
            4,
            item.Size
        );
        _session = _pool.CreateCaptureSession(item);
        if (ScreenCaptureHelper.IsIsBorderRequiredPresent)
        {
            _session.IsBorderRequired = false;
        }
        if (ScreenCaptureHelper.IsIsCursorCaptureEnabledPresent)
        {
            _session.IsCursorCaptureEnabled = cursorCapture;
        }
        // MinUpdateInterval (26H2, IGraphicsCaptureSession5): left at the OS default
        // (0 = unset) the compositor paces WGC frame delivery conservatively; in the
        // HDR/EDR state that collapses delivery to ~12 fps on this machine
        // (performance.md, WGC root-cause section). A small explicit interval overrides
        // the default pacing, the same lever OBS uses (#12759).
        if (minUpdateIntervalMs > 0 && ScreenCaptureHelper.IsMinUpdateIntervalPresent)
        {
            _session.MinUpdateInterval = TimeSpan.FromMilliseconds(minUpdateIntervalMs);
            _logger.LogDebug(
                "WGC MinUpdateInterval set to {ms} ms (overrides the compositor's default pacing)",
                minUpdateIntervalMs
            );
        }

        _channel = Channel.CreateBounded<Direct3D11CaptureFrame>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait }
        );

        _fpsCap = fpsCap;
        _lastProcessedQpc = 0;

        _frameArrived = (s, _) => OnFrameArrived();
        _pool.FrameArrived += _frameArrived;

        _pump = Task.Run(() => PumpAsync(onFrame, stopToken));
    }

    private readonly int _fpsCap;

    private long _lastProcessedQpc;

    private readonly TypedEventHandler<Direct3D11CaptureFramePool, object> _frameArrived;

    // Per-arrival trace (STARSHOT_WGC_ARRIVAL_TRACE=1): classifies the WGC delivery
    // rhythm (uniform timer vs event-gated bursts) from the FrameArrived callback
    // itself, independent of the pump's processing rate. One Debug line per second.
    private static readonly bool TraceArrivals =
        "1" == Environment.GetEnvironmentVariable("STARSHOT_WGC_ARRIVAL_TRACE");

    private long _traceLastQpc;

    private long _traceWinStartQpc;

    private int _traceWinFrames;

    private long _traceMinDeltaTicks;

    private long _traceMaxDeltaTicks;

    private long _traceSumDeltaTicks;

    public int CaptureWidth => _item.Size.Width;

    public int CaptureHeight => _item.Size.Height;

    /// <summary>泵任务（停止录制时等待其退出，避免收尾与帧处理并发碰编码器句柄）。</summary>
    public Task PumpTask => _pump;

    public void Start()
    {
        _session.StartCapture();
    }

    /// <summary>
    /// 取池里排队的帧入 Channel：队列里已有更新的就丢旧的 (保留最新)，
    /// Channel 满 (泵在忙) 时丢最旧再入。
    /// </summary>
    private void OnFrameArrived()
    {
        // Trace before draining: each FrameArrived callback is one WGC delivery, so
        // this measures the delivery rate even when the pool drops duplicates.
        if (TraceArrivals)
            TraceArrival();

        Direct3D11CaptureFrame? frame = null;
        while (true)
        {
            var f = _pool.TryGetNextFrame();
            if (f is null)
                break;
            if (frame is not null)
            {
                frame.Dispose();
            }
            frame = f;
        }
        if (frame is null)
            return;

        if (!_channel.Writer.TryWrite(frame))
        {
            // 泵在忙：丢最旧 (它比新帧更旧), 入新帧; 仍失败就丢新帧 (下帧再来)
            if (_channel.Reader.TryRead(out var old))
            {
                old.Dispose();
            }
            if (!_channel.Writer.TryWrite(frame))
            {
                frame.Dispose();
            }
        }
    }

    private void TraceArrival()
    {
        long qpc = Stopwatch.GetTimestamp();
        if (_traceLastQpc > 0)
        {
            long d = qpc - _traceLastQpc;
            if (_traceMinDeltaTicks == 0 || d < _traceMinDeltaTicks)
                _traceMinDeltaTicks = d;
            if (d > _traceMaxDeltaTicks)
                _traceMaxDeltaTicks = d;
            _traceSumDeltaTicks += d;
            _traceWinFrames++;
        }
        _traceLastQpc = qpc;
        if (_traceWinStartQpc == 0)
            _traceWinStartQpc = qpc;

        double windowMs = (double)(qpc - _traceWinStartQpc) / Stopwatch.Frequency * 1000.0;
        if (windowMs < 1000.0)
            return;

        double minMs = (double)_traceMinDeltaTicks / Stopwatch.Frequency * 1000.0;
        double maxMs = (double)_traceMaxDeltaTicks / Stopwatch.Frequency * 1000.0;
        double avgMs =
            _traceWinFrames > 0
                ? (double)_traceSumDeltaTicks / _traceWinFrames / Stopwatch.Frequency * 1000.0
                : 0.0;
        _logger.LogDebug(
            "WGC arrival trace: ~{fps} fps over {win:0.0} ms window (delta min {min:0.0} / avg {avg:0.0} / max {max:0.0} ms)",
            (double)_traceWinFrames / windowMs * 1000.0,
            windowMs,
            minMs,
            avgMs,
            maxMs
        );
        _traceWinStartQpc = qpc;
        _traceWinFrames = 0;
        _traceMinDeltaTicks = 0;
        _traceMaxDeltaTicks = 0;
        _traceSumDeltaTicks = 0;
    }

    private async Task PumpAsync(
        Func<Direct3D11CaptureFrame, Task> onFrame,
        CancellationToken stopToken
    )
    {
        try
        {
            await foreach (
                var frame in _channel.Reader.ReadAllAsync(stopToken).ConfigureAwait(false)
            )
            {
                try
                {
                    // SystemRelativeTime 投影为 TimeSpan (QPC 100ns 单位)
                    long qpc = frame.SystemRelativeTime.Ticks;
                    if (_fpsCap > 0)
                    {
                        // 帧率上限：距上次处理不足一个目标帧间隔就跳过 (keyint 由编码器自己保证)
                        long interval = 10_000_000L / _fpsCap;
                        if (_lastProcessedQpc > 0 && qpc - _lastProcessedQpc < interval)
                        {
                            continue;
                        }
                        _lastProcessedQpc = qpc;
                    }
                    // await: 回读 + 转换 (编码在独立线程, 泵只阻塞在泵侧那一段)
                    await onFrame(frame);
                }
                catch (Exception ex) when (!stopToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Frame processing failed");
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture pump failed");
        }
    }

    public static WgcCaptureSource Create(
        GraphicsCaptureItem item,
        DirectXPixelFormat format,
        bool cursorCapture,
        int fpsCap,
        int minUpdateIntervalMs,
        ILogger<VideoRecordService> logger,
        Func<Direct3D11CaptureFrame, Task> onFrame,
        CancellationToken stopToken
    )
    {
        return new WgcCaptureSource(
            item,
            format,
            cursorCapture,
            fpsCap,
            minUpdateIntervalMs,
            logger,
            onFrame,
            stopToken
        );
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _pool.FrameArrived -= _frameArrived;
            // StopCapture 不在本 WinAppSDK 投影内, 弃用会话句柄即等效停止
        }
        catch
        {
            // 会话可能已随目标失效
        }
        _channel.Writer.TryComplete();
        _session.Dispose();
        _pool.Dispose();
        // GraphicsCaptureItem 无 Dispose (投影内), 由 GC 回收
        // 泵退出由 TryComplete 驱动; 未读完的帧随 GC 释放 (Direct3D11CaptureFrame 有终结器)
    }
}
