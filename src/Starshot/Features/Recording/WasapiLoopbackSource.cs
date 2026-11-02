using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace Starshot.Features.Recording;

/// <summary>
/// WASAPI 系统声音回环 (loopback) 采集 + faac AAC 编码。
/// COM 全部走 RCW/ComImport 互操作 (vtable 调用经 .NET 原生 COM interop,
/// 不经 unmanaged 函数指针委托——本机 .NET 8/9/10 的函数指针桩实测不稳定,
/// 会 AV; RCW 路线实测 40/40 全通):
///   Modern (26300+, WASAPI 2.0): 6 参 Initialize (hnsBuffer/hnsPeriod 时长参数) +
///       5 参 GetBuffer (QPC 时间戳);
///   Legacy (19041~26100): 4 参 Initialize + 4 参 GetBuffer (hns 时间戳)。
/// 两代 IID 相同 (20H2 起稳定), 按 OS build 选签名。轮询模式取 float32 混音流,
/// 静音间隙由 QPC 驱动的 CPU 补静帧保证音轨时间轴连续。
/// 构造失败 (设备/格式/系统不支持) 由调用方捕获后降级为无音频录制。
/// </summary>
internal sealed class WasapiLoopbackSource : IDisposable
{
    private enum WasapiGen
    {
        Legacy,
        Modern,
    }

    // ===== COM GUID (20H2 起各 build 共用) =====
    private static readonly Guid ClsidMmDeviceEnumerator = new(
        "BCDE0395-E52F-467C-8E3D-C4579291692E"
    );

    private static readonly Guid IidClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    private static readonly Guid IidCap = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private const uint AudclntStreamFlagsLoopback = 0x20000;

    // AUDCLNT_S_BUFFER_EMPTY (GetBuffer 无数据时返回, 不是错误)
    private const int AudclntSBufferEmpty = unchecked((int)0x08890001);

    // ===== COM 接口 (vtable 顺序与 SDK 头文件一致, 方法按槽位声明) =====

    // IMMDeviceEnumerator (A95664D2): 槽 3~7
    [
        ComImport,
        Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IEnum
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int role, [Out] out object ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, [Out] out IMMDevice ppDevice);

        // LPCWSTR 显式按 UTF-16 传 (GetDevice 两代签名相同)
        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [Out] out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(object client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(object client);
    }

    // IMMDevice (D666063F): 槽 3~6 (20H2+ 为 4 参 Activate)
    [
        ComImport,
        Guid("D666063F-1587-4E43-81F1-B948E807363F"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid riid, uint dwClsCtx, IntPtr pActivationParams, [Out] out IntPtr ppv);

        [PreserveSig]
        int OpenPropertyStore(int access, [Out] out object ppProperties);

        [PreserveSig]
        int GetId([Out] string ppstrId);

        [PreserveSig]
        int GetState(out int pwState);
    }

    // IAudioClient (1CB9AD4C) — Legacy (20H2~26100): 4 参 Initialize
    [
        ComImport,
        Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IAudioClientLegacy
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, IntPtr pFormat, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr pFormat, IntPtr ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr ppDeviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long hnsDefault, out long hnsMin);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [Out] out IntPtr ppv);
    }

    // IAudioClient (1CB9AD4C) — Modern (26300+): 6 参 Initialize (时长参数)
    [
        ComImport,
        Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface IAudioClientModern
    {
        [PreserveSig]
        int Initialize(
            uint shareMode,
            uint streamFlags,
            long hnsBuffer,
            long hnsPeriod,
            IntPtr pFormat,
            IntPtr sessionGuid
        );

        [PreserveSig]
        int GetBufferSize(out uint bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint padding);

        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr pFormat, IntPtr ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr ppDeviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long hnsDefault, out long hnsMin);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [Out] out IntPtr ppv);
    }

    // IAudioCaptureClient (C8ADBD64) — Legacy: 4 参 GetBuffer (hns 时间戳)
    [
        ComImport,
        Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface ICaptureLegacy
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr ppData,
            ref uint pNumFramesToRead,
            ref uint pdwFlags,
            ref long pHnsTimestamp
        );

        [PreserveSig]
        int ReleaseBuffer(uint numFramesToRelease);

        [PreserveSig]
        int GetNextPacketSize(out ulong packetSize);
    }

    // IAudioCaptureClient (C8ADBD64) — Modern: 5 参 GetBuffer (QPC 时间戳)
    [
        ComImport,
        Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
        InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
    ]
    private interface ICaptureModern
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr ppData,
            ref uint pNumFramesToRead,
            ref uint pdwFlags,
            ref ulong pu64DevicePosition,
            ref ulong pu64QPCPosition
        );

        [PreserveSig]
        int ReleaseBuffer(uint numFramesToRelease);

        [PreserveSig]
        int GetNextPacketSize(out ulong packetSize);
    }

    // 枚举器工厂 (ComImport 类: new 时 CoCreateInstance, 转型时 QI 到 IEnum)
    [
        ComImport,
        Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
        ClassInterface(ClassInterfaceType.None)
    ]
    private class MmDevEnumClass { }

    /// <summary>AAC 帧回调: (缓冲, 长度, 首样本 QPC 100ns 时间戳)。</summary>
    public event Action<byte[], int, long>? AacFrame;

    // 输出恒为 48kHz 立体声（faac 会话 + MP4 音轨按此声明）。
    // 设备混音格式（如 8 声道 96kHz）只用于 WASAPI 捕获会话，
    // 喂给 faac 前先降混 + 重采样，保证普通播放器可解。
    public const int OutSampleRate = 48_000;

    public const int OutChannels = 2;

    // 公共属性返回“输出”格式（muxer BeginAudio 用），捕获混音格式存私有字段。
    public int SampleRate => OutSampleRate;

    public int Channels => OutChannels;

    public byte[] Asc { get; private set; } = Array.Empty<byte>();

    public IntPtr FaacHandle { get; private set; }

    private readonly WasapiGen _gen;
    private readonly uint _maxFrames;

    // 捕获混音格式（WASAPI 会话格式；仅用于捕获会话，输出前会转换）
    private int _capRate;
    private int _capCh;

    // 混音格式 -> 48k 立体声 转换状态（线性插值重采样 + 降混，跨包连续）
    private double _inPos; // 全局输入样本位置（混音格式样本单位，含小数；每个 48k 输出样本消耗 ratio 个输入样本）
    private long _inTotal; // 已收到的输入样本总数（混音速率整数；当前包从 _inTotal 开始）
    private float[] _carryL; // 衔接：最后两个输入样本的降混立体声 (idx0=较旧 / idx1=最新)，跨包边界插值用
    private float[] _carryR;
    private float[] _convBuf; // 累积待编码的 48k 立体声样本
    private int _convLen; // 已累积的立体声帧数（每帧 2 样本）
    private float[] _pktBuf; // 捕获包拷贝缓冲（避免逐样本 P/Invoke）

    private readonly ILogger _logger;

    // COM RCW (各持 1 份引用, Dispose 时 ReleaseComObject 归还)
    private readonly object? _enumRcw;
    private readonly object? _deviceRcw;
    private readonly object? _clientRcw;
    private readonly object? _captureRcw;
    private readonly ICaptureLegacy? _capLegacy;
    private readonly ICaptureModern? _capModern;
    private readonly IAudioClientModern? _clientModern;
    private readonly IAudioClientLegacy? _clientLegacy;

    private readonly byte[] _aacOutBuf;
    private readonly float[] _silence;
    private readonly uint _aacFrameSamples; // AAC 帧样本数 (每声道, AAC-LC = 1024)
    private readonly uint _aacFrameTotal; // 每声道 * 声道数 (faac inSamples 为全部声道合计)

    private readonly Thread _thread;
    private volatile bool _running;
    private long _lastSilenceQpc100; // 最近一次数据/静默活动时刻 (QPC 100ns); 静默间隙判定 + 静默帧时间戳

    // QPC 频率
    private readonly long _qpcFreq;

    /// <summary>
    /// 采样率/声道数取自设备混音格式（faac 不支持重采样，必须按实际混音格式建会话；
    /// 高采样率/多声道设备 faac 初始化失败时由调用方捕获后降级为无音频录制）。
    /// deviceId 为 null/空 = Windows 默认播放设备；否则按 MMDevice ID 取指定设备
    /// (GetDevice 失败——设备已拔出等——回退默认设备并记日志)。
    /// </summary>
    public WasapiLoopbackSource(
        int bitRate,
        int quantQuality,
        ILogger logger,
        string? deviceId = null
    )
    {
        _logger = logger;
        // 按 OS build 选签名代: 26300+ 用 WASAPI 2.0 时长参数接口, 20H2~26100 用经典参数
        int build = Environment.OSVersion.Version.Build;
        if (build < 19041)
        {
            throw new InvalidOperationException(
                $"WASAPI 音频回环需要 Win10 20H2+ (当前 build {build})"
            );
        }
        _gen = build >= 26300 ? WasapiGen.Modern : WasapiGen.Legacy;

        // 设备枚举 + 端点激活 (WASAPI COM 对象自由线程, 构造在 UI 线程, 轮询线程直接调用)
        IEnum? en = null;
        IMMDevice? dev = null;
        object? client = null;
        object? capture = null;
        try
        {
            en = (IEnum)new MmDevEnumClass();
            int hr;
            if (!string.IsNullOrEmpty(deviceId))
            {
                // 指定设备 (GetDevice 两代 build 槽位相同); 失败回退默认设备
                hr = en.GetDevice(deviceId, out dev!);
                if (hr != 0)
                {
                    _logger.LogWarning(
                        "wasapi: GetDevice '{id}' failed (0x{hr:X8}), falling back to system default",
                        deviceId,
                        hr
                    );
                    hr = en.GetDefaultAudioEndpoint(
                        0 /* eRender */
                        ,
                        0 /* eConsole */
                        ,
                        out dev!
                    );
                }
            }
            else
            {
                hr = en.GetDefaultAudioEndpoint(
                    0 /* eRender */
                    ,
                    0 /* eConsole */
                    ,
                    out dev!
                );
            }
            if (hr != 0)
                throw new InvalidOperationException($"Audio endpoint resolve failed (0x{hr:X8})");
            Guid riidC = IidClient;
            hr = dev.Activate(
                ref riidC,
                1 /* CLSCTX_INPROC_SERVER */
                ,
                IntPtr.Zero,
                out IntPtr clientPtr
            );
            if (hr != 0 || clientPtr == IntPtr.Zero)
                throw new InvalidOperationException($"IMMDevice::Activate failed (0x{hr:X8})");
            client = Marshal.GetObjectForIUnknown(clientPtr);
            _clientModern = (IAudioClientModern)client;
            _clientLegacy = (IAudioClientLegacy)client;

            // 混音格式 (回环共享模式为 float32; 会话按设备混音格式建, 输出前再转 48k 立体声)
            int mixHr = _clientLegacy.GetMixFormat(out IntPtr wfPtr);
            if (mixHr != 0)
                throw new InvalidOperationException($"GetMixFormat failed (0x{mixHr:X8})");

            // WAVEFORMATEX: wFormatTag(2) nChannels(2) nSamplesPerSec(4)
            _capCh = (int)Marshal.ReadInt16(wfPtr, 2);
            _capRate = Marshal.ReadInt32(wfPtr, 4);
            if (_capCh <= 0 || _capRate <= 0)
                throw new InvalidOperationException("Invalid WASAPI mix format");

            // 混音格式非 float32 时抛错, 由调用方降级为无音频录制
            ushort formatTag = (ushort)Marshal.ReadInt16(wfPtr, 0);
            if (formatTag == 0xFFFE)
            {
                // WAVEFORMATEXTENSIBLE 布局:
                //   WAVEFORMATEX(18) + dwChannelMask(4)@18 + wValidBitsPerSample(2)@22
                //   + SubFormat GUID(16)@24, 其首 DWORD = KSDATAFORMAT_SUBTYPE 主字段
                // (IEEE_FLOAT 小端首 DWORD = 3; PCM = 1)
                if (Marshal.ReadInt32(wfPtr, 24) != 3)
                    throw new InvalidOperationException(
                        "WASAPI mix format is not float32 (extensible sub)"
                    );
            }
            else if (
                formatTag != 0x0003 /* WAVE_FORMAT_IEEE_FLOAT */
            )
            {
                throw new InvalidOperationException(
                    $"WASAPI mix format tag 0x{formatTag:X4} unsupported (仅 float32)"
                );
            }

            // Initialize (轮询模式, 不传事件句柄)
            if (_gen == WasapiGen.Modern)
            {
                // 时长参数: 周期取设备默认值 (拿不到则 10ms 兜底), 缓冲 200ms
                _clientModern.GetDevicePeriod(out long hnsDef, out _); // 失败不致命
                long hnsPeriod = hnsDef > 0 ? hnsDef : 10_000_000;
                hr = _clientModern.Initialize(
                    0 /* SHARED */
                    ,
                    AudclntStreamFlagsLoopback,
                    200_000_000,
                    hnsPeriod,
                    wfPtr,
                    IntPtr.Zero
                );
            }
            else
            {
                hr = _clientLegacy.Initialize(
                    0 /* SHARED */
                    ,
                    AudclntStreamFlagsLoopback,
                    wfPtr,
                    IntPtr.Zero
                );
            }
            if (hr != 0)
                throw new InvalidOperationException($"IAudioClient::Initialize failed (0x{hr:X8})");

            // GetBufferSize — 轮询 GetBuffer 的最大帧数
            int bsHr = _clientLegacy.GetBufferSize(out uint maxFrames);
            if (bsHr != 0)
                throw new InvalidOperationException($"GetBufferSize failed (0x{bsHr:X8})");
            _maxFrames = maxFrames > 0 ? maxFrames : (uint)(_capRate / 2);

            // GetService → IAudioCaptureClient
            Guid riidCap = IidCap;
            int srvHr = _clientLegacy.GetService(ref riidCap, out IntPtr capPtr);
            if (srvHr != 0)
                throw new InvalidOperationException(
                    $"GetService capture client failed (0x{srvHr:X8})"
                );
            capture = Marshal.GetObjectForIUnknown(capPtr);
            _capLegacy = (ICaptureLegacy)capture;
            _capModern = (ICaptureModern)capture;

            _enumRcw = en;
            _deviceRcw = dev;
            _clientRcw = client;
            _captureRcw = capture;
            Marshal.FreeCoTaskMem(wfPtr);
        }
        catch
        {
            // 中途失败: 归还已创建的 COM 引用
            if (capture != null)
                Marshal.ReleaseComObject(capture);
            if (client != null)
                Marshal.ReleaseComObject(client);
            if (dev != null)
                Marshal.ReleaseComObject(dev);
            if (en != null)
                Marshal.ReleaseComObject(en);
            throw;
        }

        // faac 会话按固定输出格式 (48k 立体声) 建；混音格式只用于捕获会话
        FaacHandle = CodecsNative.FaacOpen(
            OutSampleRate,
            OutChannels,
            bitRate,
            quantQuality,
            out uint faacFrame,
            out uint maxOut
        );
        _aacFrameSamples = faacFrame > 0 ? faacFrame : 1024u;
        _aacFrameTotal = _aacFrameSamples * (uint)OutChannels;
        _aacOutBuf = new byte[Math.Max(maxOut, 8192)];
        _silence = new float[_aacFrameTotal];
        _convBuf = new float[_aacFrameSamples * OutChannels * 4];
        _pktBuf = new float[_maxFrames * (uint)Math.Max(_capCh, 2)];
        _carryL = new float[2];
        _carryR = new float[2];
        var ascBuf = new byte[64];
        CodecsNative.FaacAsc(FaacHandle, ascBuf, out int ascLen);
        Asc = ascBuf.AsSpan(0, ascLen).ToArray();

        Kernel32.QueryPerformanceFrequency(out _qpcFreq);
        _lastSilenceQpc100 = NowQpc100();

        _thread = new Thread(PollLoop) { IsBackground = true, Name = "WasapiLoopback" };
    }

    /// <summary>启动回环采集 (轮询线程)。</summary>
    public void Start()
    {
        int hr = _clientLegacy!.Start();
        if (hr != 0)
            throw new InvalidOperationException($"IAudioClient::Start failed (0x{hr:X8})");
        _running = true;
        _thread.Start();
    }

    private void PollLoop()
    {
        while (_running)
        {
            IntPtr data = IntPtr.Zero;
            uint n = _maxFrames;
            uint flags = 0;
            int hr;
            long tsQpc100 = 0;
            if (_gen == WasapiGen.Modern)
            {
                // Modern: GetBuffer 5 参, 无数据时返回 AUDCLNT_S_BUFFER_EMPTY
                ulong devPos = 0;
                ulong qpcPos = 0;
                hr = _capModern!.GetBuffer(out data, ref n, ref flags, ref devPos, ref qpcPos);
                if (hr == 0 || hr == AudclntSBufferEmpty)
                    tsQpc100 = (long)qpcPos;
            }
            else
            {
                // Legacy: GetBuffer 4 参, 无数据时 S_OK + 0 帧
                long hns = 0;
                hr = _capLegacy!.GetBuffer(out data, ref n, ref flags, ref hns);
                if (hr == 0 || hr == AudclntSBufferEmpty)
                    tsQpc100 = hns;
            }
            if (hr != 0 && hr != AudclntSBufferEmpty)
            {
                _logger.LogWarning("wasapi poll loop ended early: hr=0x{hr:X8}", hr);
                break; // 设备失效等, 由录制停止兜底
            }

            if (hr == 0 && n > 0)
            {
                // 转 48k 立体声后按整 AAC 帧喂 faac
                FeedAac(data, n, tsQpc100);
                if (_gen == WasapiGen.Modern)
                    _capModern!.ReleaseBuffer(n);
                else
                    _capLegacy!.ReleaseBuffer(n);
                // 刷新静默标记: 只有真实间隙 (无数据超过两帧) 才补静帧,
                // 避免数据连续时静默与数据流并行生成导致音轨长于视频
                _lastSilenceQpc100 = NowQpc100();
                continue;
            }

            // 无数据: 静音间隙超过两帧就补一帧静默 (保音轨时间轴连续)
            long now = NowQpc100();
            long interval100 = 10_000_000L * (long)_aacFrameSamples / OutSampleRate;
            if (now - _lastSilenceQpc100 >= interval100 * 2)
            {
                _lastSilenceQpc100 = now;
                FeedSilence();
            }
            else
            {
                Thread.Sleep(3);
            }
        }
    }

    /// <summary>
    /// 把混音格式 (多声道/高采样率) 降混 + 重采样为 48kHz 立体声, 再按整 AAC 帧喂 faac
    /// (inSamples = 全部声道合计样本数)。转换跨包连续: 全局输入位置 _inPos 与
    /// 不足一 AAC 帧的余量 _convBuf 在包间保留; 插值需要的前一/前二输入样本
    /// 跨包边界时由 _carryL/_carryR (最后两个输入样本的降混) 衔接。
    /// 产出的 AAC 帧走 AacFrame 回调。
    /// </summary>
    private void FeedAac(IntPtr data, uint frames, long tsQpc100)
    {
        int total = (int)frames * _capCh;
        if (_pktBuf.Length < total)
        {
            _pktBuf = new float[total];
        }
        Marshal.Copy(data, _pktBuf, 0, total);

        // 全局输入位置: 本包覆盖 [_inTotal, _inTotal + frames);
        // 每个 48k 输出样本对应 _capRate/48000 个混音输入样本 (线性插值, 需 i0 与 i0+1)
        long baseG = _inTotal;
        _inTotal += frames;
        double ratio = _capRate / (double)OutSampleRate;
        long lastAvail = _inTotal - 1; // 本包最后一个输入样本的全局序号
        while (_inPos + 1.0 <= lastAvail)
        {
            int i0 = (int)_inPos; // 全局输入序号 (上一包收尾时 i0 最多回看 2 个样本)
            double frac = _inPos - i0;
            float l0,
                r0,
                l1,
                r1;
            GetInputSample((int)(i0 - baseG), out l0, out r0);
            GetInputSample((int)(i0 + 1 - baseG), out l1, out r1);
            int o = _convLen * OutChannels;
            _convBuf[o] = (float)(l0 * (1.0 - frac) + l1 * frac);
            _convBuf[o + 1] = (float)(r0 * (1.0 - frac) + r1 * frac);
            _convLen++;
            _inPos += ratio;
            if (_convLen >= _aacFrameSamples)
            {
                EmitConvFrame(tsQpc100);
            }
        }

        // 更新衔接样本: 本包最后两个输入样本 (较旧 → 较新)
        if (frames >= 2)
        {
            DownmixStereo(_pktBuf, (int)frames - 2, out float cl0, out float cr0);
            DownmixStereo(_pktBuf, (int)frames - 1, out float cl1, out float cr1);
            _carryL[0] = cl0;
            _carryR[0] = cr0;
            _carryL[1] = cl1;
            _carryR[1] = cr1;
        }
        else
        {
            _carryL[0] = _carryL[1];
            _carryR[0] = _carryR[1];
            DownmixStereo(_pktBuf, 0, out float cnl, out float cnr);
            _carryL[1] = cnl;
            _carryR[1] = cnr;
        }
    }

    /// <summary>
    /// 取全局输入样本 (降混立体声): localIdx 为相对当前包 (0..frames-1) 的下标,
    /// -1/-2 用包间衔接样本 _carryL/_carryR。
    /// </summary>
    private void GetInputSample(int localIdx, out float l, out float r)
    {
        if (localIdx >= 0)
        {
            DownmixStereo(_pktBuf, localIdx, out l, out r);
        }
        else if (localIdx == -1)
        {
            l = _carryL[1];
            r = _carryR[1];
        }
        else
        {
            l = _carryL[0];
            r = _carryR[0];
        }
    }

    /// <summary>
    /// Scale the first n samples of buf in place by 32768. faac 2.2's FAAC_INPUT_FLOAT
    /// is on the int16 scale (±32768), not natural [-1,1] floats (the faac CLI frontend
    /// multiplies float32 by 32768 before encoding, PCM_16BIT_FLOAT_SCALE). Without this
    /// gain the signal is 32768x too small and faac quantizes everything to zero.
    /// </summary>
    private static void ScaleToFaacDomain(float[] buf, int n)
    {
        for (int i = 0; i < n; i++)
        {
            buf[i] *= 32768.0f;
        }
    }

    /// <summary>把已累积满一 AAC 帧 (48k 立体声) 的样本喂 faac。</summary>
    private void EmitConvFrame(long tsQpc100)
    {
        ScaleToFaacDomain(_convBuf, (int)_aacFrameSamples * OutChannels);
        if (
            CodecsNative.FaacEncode(
                FaacHandle,
                _convBuf,
                (uint)(_aacFrameSamples * OutChannels),
                _aacOutBuf,
                out uint written
            ) > 0
        )
        {
            AacFrame?.Invoke(_aacOutBuf, (int)written, tsQpc100);
        }
        _convLen = 0;
    }

    /// <summary>
    /// 混音格式一帧 (声道序 L R C LFE BL BR SL SR) 降混为立体声。
    /// 系数与常见播放器下混一致: 中置/LFE 0.5, 环绕 0.707。
    /// </summary>
    private void DownmixStereo(float[] buf, int frame, out float l, out float r)
    {
        const double Wc = 0.5;
        const double Wlfe = 0.5;
        const double Wsur = 0.70710678118654752;
        int baseIdx = frame * _capCh;
        float c0 = 0,
            c1 = 0,
            c2 = 0,
            c3 = 0,
            c4 = 0,
            c5 = 0,
            c6 = 0,
            c7 = 0;
        if (_capCh > 0)
            c0 = buf[baseIdx + 0];
        if (_capCh > 1)
            c1 = buf[baseIdx + 1];
        if (_capCh > 2)
            c2 = buf[baseIdx + 2];
        if (_capCh > 3)
            c3 = buf[baseIdx + 3];
        if (_capCh > 4)
            c4 = buf[baseIdx + 4];
        if (_capCh > 5)
            c5 = buf[baseIdx + 5];
        if (_capCh > 6)
            c6 = buf[baseIdx + 6];
        if (_capCh > 7)
            c7 = buf[baseIdx + 7];
        l = (float)(c0 + Wc * c2 + Wlfe * c3 + Wsur * c4 + Wsur * c6);
        r = (float)(c1 + Wc * c2 + Wlfe * c3 + Wsur * c5 + Wsur * c7);
    }

    private void FeedSilence()
    {
        if (
            CodecsNative.FaacEncode(
                FaacHandle,
                _silence,
                (uint)_silence.Length,
                _aacOutBuf,
                out uint written
            ) > 0
        )
        {
            AacFrame?.Invoke(_aacOutBuf, (int)written, _lastSilenceQpc100);
        }
    }

    private long NowQpc100()
    {
        long c;
        Kernel32.QueryPerformanceCounter(out c);
        return c * 10_000_000L / _qpcFreq;
    }

    /// <summary>停止采集并冲刷 faac (剩余 AAC 帧走 AacFrame 回调)。</summary>
    public void StopAndFlush()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(3));
        try
        {
            _clientLegacy?.Stop();
        }
        catch
        {
            // 客户端可能已失效
        }
        // 冲刷转换器余量 (不足一 AAC 帧的 48k 立体声样本, 直接喂入由 faac 内部衔接)
        if (_convLen > 0)
        {
            ScaleToFaacDomain(_convBuf, _convLen * OutChannels);
            if (
                CodecsNative.FaacEncode(
                    FaacHandle,
                    _convBuf,
                    (uint)(_convLen * OutChannels),
                    _aacOutBuf,
                    out uint written
                ) > 0
                && written > 0
            )
            {
                AacFrame?.Invoke(_aacOutBuf, (int)written, _lastSilenceQpc100);
            }
            _convLen = 0;
        }
        // faac 冲刷: inSamples=0 直到无输出
        int guard = 0;
        while (guard++ < 16)
        {
            if (
                CodecsNative.FaacEncodeNative(
                    FaacHandle,
                    IntPtr.Zero,
                    0,
                    _aacOutBuf,
                    out uint written
                ) <= 0
                || written == 0
            )
            {
                break;
            }
            AacFrame?.Invoke(_aacOutBuf, (int)written, _lastSilenceQpc100);
        }
    }

    public void Dispose()
    {
        if (FaacHandle != IntPtr.Zero)
        {
            CodecsNative.FaacClose(FaacHandle);
            FaacHandle = IntPtr.Zero;
        }
        // 归还 COM 引用 (顺序: 后创建的先释放)
        if (_captureRcw is not null)
            Marshal.ReleaseComObject(_captureRcw);
        if (_clientRcw is not null)
            Marshal.ReleaseComObject(_clientRcw);
        if (_deviceRcw is not null)
            Marshal.ReleaseComObject(_deviceRcw);
        if (_enumRcw is not null)
            Marshal.ReleaseComObject(_enumRcw);
    }
}
