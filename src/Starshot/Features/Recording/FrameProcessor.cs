using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Microsoft.Graphics.Canvas;

namespace Starshot.Features.Recording;

/// <summary>
/// 帧像素转换：WGC 帧 (CanvasBitmap 回读的紧排行) -> 编码器平面。
/// SDR: R8G8B8A8 8-bit → NV12 (BT.709 limited, 与 avc1 + colr nclx 对应)。
/// HDR: R16G16B16A16Float (WGC 场景参考, 1.0 = 80 nits) → planar P010
///      (BT.2020 全范围 + PQ OETF, 10-bit 值存 16-bit 容器, 与 hvc1 + colr/clli 对应)。
///      Order (H.273 / HEVC bt2020nc): PQ-OETF each R/G/B channel to R'/G'/B', then apply
///      the BT.2020 matrix in the PQ domain (Y'=0.2627R'+0.678G'+0.0593B', Cb'/Cr' likewise);
///      Y'/Cb'/Cr' are the coded values (chroma neutral 512).
/// 区域录制时只转换裁切窗口 (cropX/Y + 偶数对齐)。
/// </summary>
internal sealed class FrameProcessor
{
    private readonly bool _hdr;
    private readonly int _cropX;
    private readonly int _cropY;

    // Fast-convert mode (Recording settings toggle, default off = bit-exact): a smaller
    // 80 KB hybrid PQ LUT + reciprocal chroma multiplies instead of divides. NOT
    // bit-exact: luma +-1 code, chroma +-2..3 vs the exact path (inside the HDR suite
    // bands). The kernels pick the LUT / remap / chroma math from these fields.
    private readonly bool _fastConvert;

    private readonly float[] _pqLutF;

    private readonly float _uInv;

    private readonly float _vInv;

    // HDR conversion kernel (Recording settings selection; the STARSHOT_WGC_SIMD env
    // probe wins - see ResolveKernel) and the row-pool worker count (0 = Auto; the
    // STARSHOT_WGC_WORKERS env probe wins).
    private readonly SimdKernel _kernel;

    private readonly int _forcedWorkers;

    // 输出平面 (紧凑无 pitch, x264/x265 stride 直接 = 宽 * 通道字节)
    private byte[] _y;
    private byte[] _uv; // NV12
    private byte[] _y16;
    private byte[] _u16;
    private byte[] _v16;

    // 转换行并行的最小像素数 (低于此值任务启动开销大于收益, 保持串行)
    private const int ParallelMinPixels = 1 << 20;

    // AVX2 (256-bit) SIMD for the HDR conversion: 8 pixels per iteration, with the
    // same operation order as the scalar path (explicit mul/add/div, no FMA) so the
    // coded P010 values match the scalar path bit-for-bit (±1 LUT-index code at rare
    // .5 boundaries, inside the HDR suite bands). Gated on the runtime CPU feature;
    // the scalar path stays as the fallback (arm64 or an AVX2-less x86).
    private static readonly bool SimdAvx2 = Avx2.IsSupported;

    // AVX-512 (512-bit) kernel: 16 pixels per iteration, same bit-exact contract as
    // the AVX2 kernel. IsSupported is pure CPUID (never throws), but the 512-bit
    // intrinsic calls that build the vector constants below WOULD throw at type init
    // on a CPU without AVX-512 - so every 512-bit constant is gated on SimdAvx512.
    private static readonly bool SimdAvx512 = Avx512F.IsSupported && Avx512BW.IsSupported;

    internal enum SimdKernel
    {
        Scalar,
        Avx2,
        Avx2Fast,
        Avx2Wide,
        Avx512,
    }

    /// <summary>
    /// HDR conversion kernel resolution. The STARSHOT_WGC_SIMD=scalar|avx2|avx2-fast|
    /// avx2-wide|avx512 env probe (A/B; record-test children inherit it from the
    /// launching shell) wins over the Recording settings selection (settingIndex:
    /// 0 = Auto = widest SIMD the CPU has, AVX-512 -&gt; AVX2 -&gt; scalar; 1 = AVX-512,
    /// 2 = AVX2 wide, 3 = AVX2 baseline, 4 = AVX2 fast, 5 = scalar). A kernel the CPU
    /// lacks falls back down the chain (avx512 -&gt; the Auto default; avx2-fast /
    /// avx2-wide / avx2 -&gt; scalar on an AVX2-less CPU). avx2 = 8-px, 3x 256 KB LUT
    /// gathers (the baseline); avx2-fast = 8-px with the 8 KB subnormal
    /// gather-reduction half-&gt;float fast path; avx2-wide = 16-px batched half-LUT
    /// gathers (front-loaded memory-level parallelism).
    /// </summary>
    public static SimdKernel ResolveKernel(int settingIndex)
    {
        string ov = (
            Environment.GetEnvironmentVariable("STARSHOT_WGC_SIMD") ?? ""
        ).ToLowerInvariant();
        if (ov.Length > 0)
            return KernelFromName(ov);
        SimdKernel def =
            SimdAvx512 ? SimdKernel.Avx512
            : SimdAvx2 ? SimdKernel.Avx2
            : SimdKernel.Scalar;
        return settingIndex switch
        {
            1 => SimdAvx512 ? SimdKernel.Avx512 : def,
            2 => SimdAvx2 ? SimdKernel.Avx2Wide : SimdKernel.Scalar,
            3 => SimdAvx2 ? SimdKernel.Avx2 : SimdKernel.Scalar,
            4 => SimdAvx2 ? SimdKernel.Avx2Fast : SimdKernel.Scalar,
            5 => SimdKernel.Scalar,
            _ => def,
        };
    }

    private static SimdKernel KernelFromName(string name)
    {
        SimdKernel def =
            SimdAvx512 ? SimdKernel.Avx512
            : SimdAvx2 ? SimdKernel.Avx2
            : SimdKernel.Scalar;
        return name switch
        {
            "scalar" => SimdKernel.Scalar,
            "avx2" => SimdAvx2 ? SimdKernel.Avx2 : SimdKernel.Scalar,
            "avx2-fast" => SimdAvx2 ? SimdKernel.Avx2Fast : SimdKernel.Scalar,
            "avx2-wide" => SimdAvx2 ? SimdKernel.Avx2Wide : SimdKernel.Scalar,
            "avx512" => def,
            _ => def,
        };
    }

    // 最近一次 ConvertHdr 的 clli Y 统计 (10-bit 码值): 由转换过程顺带累计
    // (取代原来对 Y 平面的第二遍完整扫描)
    private int _lastYMin = 1023;
    private int _lastYMax;
    private long _lastYSum;

    // L3: persistent worker pool (large frames only; small frames stay serial). Replaces the
    // per-frame Task.Run x workers + 4 stat arrays (GC churn + ~1-3 ms dispatch/sync); the
    // row-chunk split (idx*h/workers) is identical to the old Task-based path.
    private RowPool? _pool;

    public int OutWidth { get; }

    public int OutHeight { get; }

    /// <summary>
    /// clli Y 统计 (最近一次 ConvertHdr): 10-bit 编码亮度码值的 (min, max, mean)。
    /// mean = 帧平均编码亮度 (maxFALL 的输入; 空帧为 0)。仅对最近一次 ConvertHdr 有效。
    /// </summary>
    public (int Min, int Max, int Mean) ClliY
    {
        get
        {
            int px = OutWidth * OutHeight;
            return (_lastYMin, _lastYMax, px > 0 ? (int)(_lastYSum / px) : 0);
        }
    }

    /// <summary>PQ LUT: 0..1 值 (i/65535, 1.0 = 10000 nits) → HDR10 10-bit (0..1023)。</summary>
    private static readonly ushort[] PqLut = BuildPqLut();

    /// <summary>
    /// Float-coded PQ LUT (PqLut[i] / 1023.0f, precomputed): the AVX2 path gathers 32-bit
    /// floats from this instead of 16-bit shorts, and the value is bit-identical to the
    /// scalar <c>PqLut[idx] / 1023.0f</c> (the same single float division, precomputed).
    /// </summary>
    private static readonly float[] PqLutF = BuildPqLutF();

    private static float[] BuildPqLutF()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
            lut[i] = PqLut[i] / 1023.0f;
        return lut;
    }

    /// <summary>
    /// Fast-mode PQ LUT (the Recording settings toggle): an 80 KB (L2-resident) hybrid.
    /// A uniform 16K grid is NOT +-1 code: the PQ curve is steep at low r01 (0 -> 0.001
    /// spans ~300 codes), so its first bin alone carries up to ~100 codes. The hybrid
    /// keeps FULL 65536-entry resolution for the low quarter (r01 &lt; 0.0625, below
    /// ~625 nits, where 4-slot bins would drift up to ~4 codes and the sub-nit black
    /// range up to ~100) and 4-slot center-samples the top 94%, where the measured
    /// error is &lt;= 1 code (probe Part E4). Worst case vs exact: luma +-1 code over
    /// the full range, chroma +-2..3 (LUT propagation + recip-ulp), all inside the
    /// HDR suite bands (+-6 Y / +-12 chroma).
    /// </summary>
    private static readonly float[] PqLutFastF = BuildPqLutFastF();

    private static float[] BuildPqLutFastF()
    {
        var lut = new float[4096 + 16384];
        for (int k = 0; k < 4096; k++)
            lut[k] = PqLutF[k]; // low quarter: exact resolution
        for (int k = 4096; k < lut.Length; k++)
        {
            int s = 4096 + 4 * (k - 4096) + 2; // 4-slot bin center
            lut[k] = PqLutF[Math.Min(s, 65535)];
        }
        return lut;
    }

    // WGC 场景参考值 (1.0 = 80 nits) → 0..1 (1.0 = 10000 nits) 的换算 (80/10000 = 1/125)
    private const float UnitPerNits = 80.0f / 10000.0f;

    // Measured WGC → BT.2020 matrix (2026-10-30, 10000-nit BT.2020 flat fields on this WGC
    // capture path). WGC HDR floats are a linear scene-referred RGB space at 80 nits/unit whose
    // luma row is the sRGB/BT.709 triplet (0.2126/0.7152/0.0722) - NOT BT.2020 - so
    // out-of-gamut BT.2020 primaries carry NEGATIVE channels (10000-nit RED = (207.5,
    // -15.5625, -2.2676)). Fitting WGC = A x C2020 (80-nit units) to the measured RED/GREEN/
    // BLUE fields, C2020 = Ainv x WGC (rows below) recovers the true BT.2020 linear RGB:
    // primaries reproduce exactly, YELLOW within 0.05%, and w709 . A = w2020 to 0.05% (the
    // space's luma row and the 2020 luma agree). Clamping the raw WGC channels to [0,1] first
    // (the old WgcToLinear) was exact at full scale only by coincidence and made saturated
    // midtones up to 1.66x too bright (5000-nit RED luma 0.218 vs true 0.131).
    private static readonly float[] WgcTo2020Ainv =
    {
        0.627600968f,
        0.329570085f,
        0.043333914f, // R2020
        0.069145925f,
        0.920288682f,
        0.011371733f, // G2020
        0.016403098f,
        0.088126212f,
        0.896180391f, // B2020
    };

    // BT.2020 线性 RGB→YUV (0..1 域, 1.0 = 10000 nits; U/V 以 0.5 为中性)
    private const float Yr = 0.2627f;

    private const float Yg = 0.678f;

    private const float Yb = 0.0593f;

    private const float UDiv = 2.0f * (1.0f - Yb);

    private const float VDiv = 2.0f * (1.0f - Yr);

    public FrameProcessor(
        bool hdr,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight,
        bool fastConvert = false,
        int simdKernelIndex = 0,
        int forcedWorkers = 0
    )
    {
        _hdr = hdr;
        _cropX = cropX;
        _cropY = cropY;
        _fastConvert = fastConvert;
        _pqLutF = fastConvert ? PqLutFastF : PqLutF;
        _uInv = 1.0f / UDiv;
        _vInv = 1.0f / VDiv;
        // 0 = Auto; the STARSHOT_WGC_SIMD env probe still wins (record-test A/B).
        _kernel = ResolveKernel(simdKernelIndex);
        _forcedWorkers = forcedWorkers; // 0 = Auto
        // x264/x265 要求偶数宽高
        OutWidth = cropWidth & ~1;
        OutHeight = cropHeight & ~1;

        if (_hdr)
        {
            int halfW = OutWidth / 2;
            _y16 = new byte[OutWidth * OutHeight * 2];
            _u16 = new byte[halfW * (OutHeight / 2) * 2];
            _v16 = new byte[halfW * (OutHeight / 2) * 2];
        }
        else
        {
            _y = new byte[OutWidth * OutHeight];
            _uv = new byte[OutWidth * (OutHeight / 2)];
        }

        // L3: persistent worker pool (only when the old path would have parallelized). The
        // per-worker work delegate is built last (it needs the pool's own stat/chunk arrays,
        // which the pool's constructor allocates - declare first, assign second).
        int workers = WorkerCount(OutHeight, OutWidth * OutHeight);
        if (workers > 1)
        {
            RowPool? pool = null;
            if (_hdr)
            {
                pool = new RowPool(
                    OutHeight,
                    workers,
                    (i, src, sw) =>
                    {
                        var p = pool!; // only invoked after the constructor returns
                        ConvertHdrRows(
                            src,
                            sw,
                            p.R0[i],
                            p.R1[i],
                            ref p.Mins[i],
                            ref p.Maxs[i],
                            ref p.Sums[i]
                        );
                    }
                );
            }
            else
            {
                pool = new RowPool(
                    OutHeight,
                    workers,
                    (i, src, sw) =>
                    {
                        var p = pool!;
                        ConvertSdrRows(src, sw, p.R0[i], p.R1[i]);
                    }
                );
            }
            _pool = pool;
        }
    }

    public void Dispose()
    {
        _pool?.Dispose();
        _pool = null;
    }

    /// <summary>
    /// L3: persistent worker pool. One dedicated background thread per worker with a FIXED
    /// row chunk (idx*h/workers - the session's OutWidth/OutHeight never change), pre-allocated
    /// stat arrays, and start/done ManualResetEvents: zero per-frame Task / array allocations
    /// (the old path spawned `workers` Task.Run + 4 arrays every frame - GC churn at 1440p's
    /// ~30 fps). The Set/Wait pair is a full memory barrier, so the workers see the frame
    /// buffers the coordinator pointed at.
    /// </summary>
    private sealed class RowPool : IDisposable
    {
        private readonly Thread[] _threads;
        private readonly ManualResetEvent[] _done;
        private readonly ManualResetEvent _start = new(false);
        private readonly int[] _mins;
        private readonly int[] _maxs;
        private readonly long[] _sums;
        private readonly int[] _r0;
        private readonly int[] _r1;
        private readonly Action[] _works;
        private readonly int _workers;
        private volatile bool _stop;
        private byte[]? _src;
        private int _srcWidth;

        // Generation guard: a fast worker can lap the coordinator's _start.Reset() and
        // re-pass _start.WaitOne() while the event is still set, re-doing its chunk (the
        // pool measured 2.3x slower than the Task path because of this double-work). The
        // coordinator bumps _gen each frame (before _start.Set, so the Set/Wait barrier
        // publishes it); a worker does a frame's chunk at most once per generation.
        private int _gen;

        private readonly int[] _lastDone;

        public int[] Mins => _mins;

        public int[] Maxs => _maxs;

        public long[] Sums => _sums;

        public int[] R0 => _r0;

        public int[] R1 => _r1;

        // workChunk(workerIdx, src, srcWidth) converts that worker's fixed chunk
        // [R0[idx], R1[idx]) and, on the HDR path, that worker's stat slot.
        public RowPool(int h, int workers, Action<int, byte[], int> workChunk)
        {
            _workers = workers;
            _mins = new int[workers];
            _maxs = new int[workers];
            _sums = new long[workers];
            _r0 = new int[workers];
            _r1 = new int[workers];
            _done = new ManualResetEvent[workers];
            _works = new Action[workers];
            _threads = new Thread[workers];
            _lastDone = new int[workers];
            for (int i = 0; i < workers; i++)
            {
                // for-loop variable i must not be captured by the lambda (the local .NET
                // SDKs capture it shared across iterations - pre-C#5 semantics); a fresh
                // local is captured per iteration.
                int idx = i;
                _r0[idx] = idx * h / workers;
                _r1[idx] = (idx + 1) * h / workers;
                _mins[idx] = 1023;
                _done[idx] = new ManualResetEvent(false);
                _works[idx] = () => workChunk(idx, _src!, _srcWidth);
                _threads[idx] = new Thread(() => Loop(idx))
                {
                    IsBackground = true,
                    Name = $"FrameConvert-{idx}",
                };
            }
            foreach (var t in _threads)
                t.Start();
        }

        private void Loop(int idx)
        {
            while (true)
            {
                _start.WaitOne();
                if (_stop)
                    break;
                // Do this frame's chunk at most once: a re-entry that laps the
                // coordinator's _start.Reset() sees _lastDone[idx] == _gen and skips.
                if (_lastDone[idx] < _gen)
                {
                    _works[idx]();
                    _lastDone[idx] = _gen;
                }
                _done[idx].Set();
            }
        }

        /// <summary>Convert one frame across the pool (blocks until every chunk is done).</summary>
        public void Run(byte[] src, int srcWidth)
        {
            for (int i = 0; i < _workers; i++)
            {
                _mins[i] = 1023;
                _maxs[i] = 0;
                _sums[i] = 0;
                _done[i].Reset();
            }
            _src = src;
            _srcWidth = srcWidth;
            _gen++; // published to the workers by the _start.Set/WaitOne barrier below
            _start.Set();
            for (int i = 0; i < _workers; i++)
                _done[i].WaitOne();
            _start.Reset();
        }

        public void Dispose()
        {
            _stop = true;
            _start.Set();
            foreach (var t in _threads)
                t.Join();
            foreach (var d in _done)
                d.Dispose();
            _start.Dispose();
        }
    }

    /// <summary>
    /// SDR 转换。src = RGBA8 紧排行 (srcWidth 为帧像素宽)。
    /// 行并行: 连续不相交的行块分给线程池 (小帧保持串行, 任务开销大于收益)。
    /// </summary>
    public void ConvertSdr(byte[] src, int srcWidth, int srcHeight)
    {
        var pool = _pool;
        if (pool is null)
        {
            ConvertSdrRows(src, srcWidth, 0, OutHeight);
            return;
        }
        pool.Run(src, srcWidth);
    }

    /// <summary>
    /// 并行工作线程数: 像素数低于阈值 (任务启动开销大于收益) 时为 1 (串行),
    /// 否则 min(行数, 逻辑处理器数)。
    /// </summary>
    private int WorkerCount(int h, int pixels)
    {
        if (pixels < ParallelMinPixels)
            return 1;
        int n = Math.Min(h, Environment.ProcessorCount);
        // Hidden probe override (performance.md, WGC root-cause section): the env var
        // wins over the Recording settings worker count; forcing the row-pool worker
        // count classifies the HDR convert cost (thread contention vs per-core
        // throughput). Read at process start; record-test children inherit it from
        // the launching shell.
        if (
            int.TryParse(
                Environment.GetEnvironmentVariable("STARSHOT_WGC_WORKERS") ?? "",
                out int w
            )
            && w > 0
        )
        {
            n = Math.Min(h, w);
        }
        else if (_forcedWorkers > 0)
        {
            n = Math.Min(h, _forcedWorkers);
        }
        return n;
    }

    /// <summary>连续行块 [rowBegin, rowEnd) 的 SDR 转换 (块间平面写入范围不相交, 可并行)。</summary>
    private void ConvertSdrRows(byte[] src, int srcWidth, int rowBegin, int rowEnd)
    {
        int w = OutWidth;
        var y = _y!;
        var uv = _uv!;

        // Cb/Cr block row of even row r is r/2. When rowBegin is odd the first even row of
        // the chunk is rowBegin+1, so the chunk's first block row is (rowBegin+1)/2 - the
        // chroma rows must line up with the serial whole-frame numbering exactly.
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = (_cropY + row) * srcWidth + _cropX;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * w : 0;

            for (int x = 0; x < w; x++)
            {
                int si = (srcRow + x) * 4;
                int r = src[si];
                int g = src[si + 1];
                int b = src[si + 2];

                int yy = (46 * r + 205 * g + 11 * b + 128) >> 8;
                if (yy < 16)
                    yy = 16;
                else if (yy > 235)
                    yy = 235;
                y[yOff + x] = (byte)yy;

                if (evenRow && (x & 1) == 0)
                {
                    // 水平 2x2 平均: x 与 x+1 两个像素的 Cb/Cr 取均值
                    int cb = ((-38 * r - 74 * g + 112 * b + 512) >> 8) + 128;
                    int cr = ((112 * r - 94 * g - 18 * b + 512) >> 8) + 128;
                    int r2 = src[(srcRow + x + 1) * 4];
                    int g2 = src[(srcRow + x + 1) * 4 + 1];
                    int b2 = src[(srcRow + x + 1) * 4 + 2];
                    int cb2 = ((-38 * r2 - 74 * g2 + 112 * b2 + 512) >> 8) + 128;
                    int cr2 = ((112 * r2 - 94 * g2 - 18 * b2 + 512) >> 8) + 128;
                    cb = ClampCbCr8((cb + cb2 + 1) >> 1);
                    cr = ClampCbCr8((cr + cr2 + 1) >> 1);
                    uv[cbBase + x] = (byte)cb;
                    uv[cbBase + x + 1] = (byte)cr;
                }
            }
            if (evenRow)
            {
                cbRow++;
            }
        }
    }

    /// <summary>
    /// HDR conversion. src = R16G16B16A16Float packed rows (2 bytes/channel half float, 1.0 = 80 nits).
    /// WGC's HDR space is a 709-luma linear RGB (80 nits/unit; out-of-gamut 2020 colors carry
    /// negative channels) - WgcTo2020Rgb01 maps it to true BT.2020 linear RGB (0..1, 1.0 = 10000 nits).
    /// Per the H.273 / HEVC bt2020nc convention, each RGB channel is PQ-OETF'd (R'/G'/B'), then
    /// the BT.2020 matrix is applied in the PQ domain: Y' = 0.2627R'+0.678G'+0.0593B',
    /// Cb' = 0.5+(B'-Y')/2(1-Kb), Cr' = 0.5+(R'-Y')/2(1-Kr). Y'/Cb'/Cr' are the coded values
    /// (chroma neutral = 512). Output planar P010: Y/U/V 2 bytes/sample (10-bit values, full range).
    /// Rows are parallelized across pool threads (disjoint contiguous chunks); the clli Y stats
    /// (10-bit min/max/sum) are accumulated by the same pass and exposed via <see cref="ClliY"/>,
    /// replacing the separate second-pass Y scan.
    /// </summary>
    public void ConvertHdr(byte[] src, int srcWidth, int srcHeight)
    {
        var pool = _pool;
        if (pool is null)
        {
            int sMin = 1023,
                sMax = 0;
            long sSum = 0;
            ConvertHdrRows(src, srcWidth, 0, OutHeight, ref sMin, ref sMax, ref sSum);
            _lastYMin = sMin;
            _lastYMax = sMax;
            _lastYSum = sSum;
            return;
        }

        pool.Run(src, srcWidth);
        var mins = pool.Mins;
        var maxs = pool.Maxs;
        var sums = pool.Sums;
        int min = 1023,
            max = 0;
        long sum = 0;
        for (int i = 0; i < mins.Length; i++)
        {
            if (mins[i] < min)
                min = mins[i];
            if (maxs[i] > max)
                max = maxs[i];
            sum += sums[i];
        }
        _lastYMin = min;
        _lastYMax = max;
        _lastYSum = sum;
    }

    /// <summary>
    /// 连续行块 [rowBegin, rowEnd) 的 HDR 转换 (块间平面写入范围不相交, 可并行)。
    /// 同时累计该块输出 Y 平面 (10-bit 码值) 的 min/max/sum —— clli 统计随转换一次完成。
    /// Dispatches to the instance kernel (<see cref="_kernel"/>: the STARSHOT_WGC_SIMD
    /// env probe, then the Recording settings selection, then Auto = the widest SIMD
    /// the CPU has; scalar fallback); all share the same LUTs, op order and
    /// write ranges (fast mode swaps in the hybrid LUT + reciprocal chroma, see
    /// <see cref="PqLutFastF"/>).
    /// </summary>
    private void ConvertHdrRows(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        switch (_kernel)
        {
            case SimdKernel.Avx512:
                ConvertHdrRowsAvx512(src, srcWidth, rowBegin, rowEnd, ref min, ref max, ref sum);
                break;
            case SimdKernel.Avx2Fast:
                ConvertHdrRowsAvx2Fast(src, srcWidth, rowBegin, rowEnd, ref min, ref max, ref sum);
                break;
            case SimdKernel.Avx2Wide:
                ConvertHdrRowsAvx2Wide(src, srcWidth, rowBegin, rowEnd, ref min, ref max, ref sum);
                break;
            case SimdKernel.Avx2:
                ConvertHdrRowsAvx2(src, srcWidth, rowBegin, rowEnd, ref min, ref max, ref sum);
                break;
            default:
                ConvertHdrRowsScalar(src, srcWidth, rowBegin, rowEnd, ref min, ref max, ref sum);
                break;
        }
    }

    /// <summary>
    /// Scalar HDR row conversion (the fallback for non-AVX2 CPUs; its per-pixel body is
    /// shared with the AVX2 kernel's &lt;8-pixel tail via <see cref="ConvertHdrPixel"/>).
    /// </summary>
    private void ConvertHdrRowsScalar(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        int w = OutWidth;
        int halfW = w / 2;

        // Cb/Cr block row of even row r is r/2 (same as the serial whole-frame version).
        // When rowBegin is odd the chunk's first even row is rowBegin+1, whose block row is
        // (rowBegin+1)/2 - starting from rowBegin/2 would shift every chroma row of the
        // chunk up by one (2 luma rows) and leave the chunk's last chroma row unwritten.
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = (_cropY + row) * srcWidth + _cropX;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * halfW : 0;

            for (int x = 0; x < w; x++)
            {
                ConvertHdrPixel(src, srcRow, x, yOff, evenRow, cbBase, ref min, ref max, ref sum);
            }
            if (evenRow)
            {
                cbRow++;
            }
        }
    }

    /// <summary>
    /// One source pixel of the HDR conversion (shared by the scalar path and the AVX2
    /// kernel's tail): luma Y + clli stats, plus the 2x2 horizontal chroma average when
    /// this is an even row and an even x. <c>srcRow</c> is in source-pixel units; the
    /// pixel's bytes are at <c>(srcRow + x) * 8</c>.
    /// </summary>
    private void ConvertHdrPixel(
        byte[] src,
        int srcRow,
        int x,
        int yOff,
        bool evenRow,
        int cbBase,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        var y = _y16!;
        var u = _u16!;
        var v = _v16!;
        int si = (srcRow + x) * 8;
        WgcTo2020Rgb01(
            ReadHalf(src, si),
            ReadHalf(src, si + 2),
            ReadHalf(src, si + 4),
            out float r01,
            out float g01,
            out float b01
        );

        // H.273 / HEVC bt2020nc: PQ-OETF each channel, then the BT.2020 matrix in the
        // PQ (transfer-applied) domain. Y' is the coded luma (NOT re-PQ'd); a standard
        // decoder inverts exactly this. (Doing the matrix in the linear domain and then
        // PQ-ing the luma desaturates colors; see hdr-dim-troubleshooting.md.)
        float rp = Pq01(r01);
        float gp = Pq01(g01);
        float bp = Pq01(b01);
        float yp = Yr * rp + Yg * gp + Yb * bp;
        int yv = Lin10(yp);
        WriteU16(y, (yOff + x) * 2, yv);

        // clli: same 10-bit codes the old second-pass scan read back from Y.
        // (Two independent ifs: with min=1023/max=0 the first value can be both a new
        // min and a new max.)
        if (yv < min)
            min = yv;
        if (yv > max)
            max = yv;
        sum += yv;

        if (evenRow && (x & 1) == 0)
        {
            // 2x2 horizontal chroma average, in the PQ domain (neutral 512). Fast mode
            // multiplies by the precomputed reciprocals (the SIMD kernels do the same,
            // so a fast-mode row is one consistent mode even with a scalar tail).
            float u01 = _fastConvert
                ? Clamp01(0.5f + (bp - yp) * _uInv)
                : Clamp01(0.5f + (bp - yp) / UDiv);
            float v01 = _fastConvert
                ? Clamp01(0.5f + (rp - yp) * _vInv)
                : Clamp01(0.5f + (rp - yp) / VDiv);
            int si2 = (srcRow + x + 1) * 8;
            WgcTo2020Rgb01(
                ReadHalf(src, si2),
                ReadHalf(src, si2 + 2),
                ReadHalf(src, si2 + 4),
                out float r2,
                out float g2,
                out float b2
            );
            float rp2 = Pq01(r2);
            float gp2 = Pq01(g2);
            float bp2 = Pq01(b2);
            float yp2 = Yr * rp2 + Yg * gp2 + Yb * bp2;
            float u2 = _fastConvert
                ? Clamp01(0.5f + (bp2 - yp2) * _uInv)
                : Clamp01(0.5f + (bp2 - yp2) / UDiv);
            float v2 = _fastConvert
                ? Clamp01(0.5f + (rp2 - yp2) * _vInv)
                : Clamp01(0.5f + (rp2 - yp2) / VDiv);
            WriteU16(u, (cbBase + x / 2) * 2, Lin10((u01 + u2) * 0.5f));
            WriteU16(v, (cbBase + x / 2) * 2, Lin10((v01 + v2) * 0.5f));
        }
    }

    /// <summary>
    /// AVX2 HDR row conversion: 8 source pixels per iteration; the &lt;8-pixel tail falls back
    /// to <see cref="ConvertHdrPixel"/> (scalar). Same LUTs and op order as the scalar path,
    /// so the coded P010 values match (±1 LUT-index code at rare .5 boundaries). Chroma is
    /// computed from the in-group pixel pairs (x, x+1), so the scalar path's redundant
    /// recompute of pixel x+1 disappears for the bulk of the row.
    /// </summary>
    private void ConvertHdrRowsAvx2(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        int w = OutWidth;
        int halfW = w / 2;
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = (_cropY + row) * srcWidth + _cropX;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * halfW : 0;

            int bulk = w & ~7;
            for (int x0 = 0; x0 < bulk; x0 += 8)
            {
                ConvertHdrPixelGroupAvx2(
                    src,
                    srcRow,
                    x0,
                    yOff,
                    evenRow,
                    cbBase,
                    ref min,
                    ref max,
                    ref sum
                );
            }
            for (int x = bulk; x < w; x++)
            {
                ConvertHdrPixel(src, srcRow, x, yOff, evenRow, cbBase, ref min, ref max, ref sum);
            }
            if (evenRow)
            {
                cbRow++;
            }
        }
    }

    /// <summary>
    /// AVX2 gather-reduction row conversion (the avx2-fast A/B kernel): same structure as
    /// <see cref="ConvertHdrRowsAvx2"/>, one <see cref="ConvertHdrPixelGroupAvx2Fast"/>
    /// per 8-px group.
    /// </summary>
    private void ConvertHdrRowsAvx2Fast(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        int w = OutWidth;
        int halfW = w / 2;
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = (_cropY + row) * srcWidth + _cropX;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * halfW : 0;

            int bulk = w & ~7;
            for (int x0 = 0; x0 < bulk; x0 += 8)
            {
                ConvertHdrPixelGroupAvx2Fast(
                    src,
                    srcRow,
                    x0,
                    yOff,
                    evenRow,
                    cbBase,
                    ref min,
                    ref max,
                    ref sum
                );
            }
            for (int x = bulk; x < w; x++)
            {
                ConvertHdrPixel(src, srcRow, x, yOff, evenRow, cbBase, ref min, ref max, ref sum);
            }
            if (evenRow)
            {
                cbRow++;
            }
        }
    }

    /// <summary>
    /// 16-px wide row conversion (the avx2-wide kernel): one
    /// <see cref="ConvertHdrPixelGroupAvx2Wide"/> per 16-px group; an 8-15 pixel tail runs
    /// one <see cref="ConvertHdrPixelGroupAvx2"/> (the 8-px bulk is a multiple of 16, so
    /// the (x, x+1) chroma pairs stay aligned), and the &lt;8 remainder falls back to
    /// <see cref="ConvertHdrPixel"/> (scalar). Fast mode is honored by both kernels via
    /// the instance LUT / reciprocal fields, so a row is always one consistent mode.
    /// </summary>
    private void ConvertHdrRowsAvx2Wide(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        int w = OutWidth;
        int halfW = w / 2;
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = (_cropY + row) * srcWidth + _cropX;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * halfW : 0;

            int bulk = w & ~15;
            for (int x0 = 0; x0 < bulk; x0 += 16)
            {
                ConvertHdrPixelGroupAvx2Wide(
                    src,
                    srcRow,
                    x0,
                    yOff,
                    evenRow,
                    cbBase,
                    ref min,
                    ref max,
                    ref sum
                );
            }
            if (w - bulk >= 8)
            {
                ConvertHdrPixelGroupAvx2(
                    src,
                    srcRow,
                    bulk,
                    yOff,
                    evenRow,
                    cbBase,
                    ref min,
                    ref max,
                    ref sum
                );
            }
            for (int x = bulk + (w - bulk >= 8 ? 8 : 0); x < w; x++)
            {
                ConvertHdrPixel(src, srcRow, x, yOff, evenRow, cbBase, ref min, ref max, ref sum);
            }
            if (evenRow)
            {
                cbRow++;
            }
        }
    }

    // ------------------------------------------------------------------
    // AVX2 kernel constants + helpers (semantic .NET 10 intrinsics API: the
    // C-style _mm* names and __m256i/__m128i types do not exist in .NET 10).
    // Vector constants are built once: int set1 = movd + broadcast, float set1 =
    // 32-bit load + broadcast, vpermps controls = plain 256-bit load, zero = default.
    // ------------------------------------------------------------------

    private static readonly Vector256<int> ZeroI = default;

    private static readonly Vector256<float> ZeroF = default;

    private static readonly Vector256<int> Lo16I = Set1I(0x0000FFFF);

    private static readonly Vector256<int> CtlEven = Ctl(0, 2, 4, 6, 0, 0, 0, 0);

    private static readonly Vector256<int> CtlOdd = Ctl(1, 3, 5, 7, 0, 0, 0, 0);

    private static readonly Vector256<int> CtlSwap = Ctl(1, 0, 3, 2, 5, 4, 7, 6);

    private static readonly Vector256<float> OneF = Set1F(1.0f);

    private static readonly Vector256<float> UpnF = Set1F(UnitPerNits);

    private static readonly Vector256<float> Ainv0F = Set1F(WgcTo2020Ainv[0]);

    private static readonly Vector256<float> Ainv1F = Set1F(WgcTo2020Ainv[1]);

    private static readonly Vector256<float> Ainv2F = Set1F(WgcTo2020Ainv[2]);

    private static readonly Vector256<float> Ainv3F = Set1F(WgcTo2020Ainv[3]);

    private static readonly Vector256<float> Ainv4F = Set1F(WgcTo2020Ainv[4]);

    private static readonly Vector256<float> Ainv5F = Set1F(WgcTo2020Ainv[5]);

    private static readonly Vector256<float> Ainv6F = Set1F(WgcTo2020Ainv[6]);

    private static readonly Vector256<float> Ainv7F = Set1F(WgcTo2020Ainv[7]);

    private static readonly Vector256<float> Ainv8F = Set1F(WgcTo2020Ainv[8]);

    private static readonly Vector256<float> YrF = Set1F(Yr);

    private static readonly Vector256<float> YgF = Set1F(Yg);

    private static readonly Vector256<float> YbF = Set1F(Yb);

    private static readonly Vector256<float> UDivF = Set1F(UDiv);

    private static readonly Vector256<float> VDivF = Set1F(VDiv);

    private static readonly Vector256<float> SixtF = Set1F(65535.0f);

    private static readonly Vector256<float> Half01F = Set1F(0.5f);

    private static readonly Vector256<float> YScaleF = Set1F(1023.0f);

    private static readonly Vector256<int> MaxPqIdxI = Set1I(65535);

    private static readonly Vector256<int> MaxY10I = Set1I(1023);

    // Fast-mode PQ LUT remap (PqLutFastF): the exact 65535-scaled index t maps to
    // j = t < 4096 ? t : 4096 + ((t - 4096 + 2) >> 2) (exact resolution for the low
    // quarter, 4-slot center-sampled bins above; see FastLutIdx).
    private static readonly Vector256<int> FourK96I = Set1I(4096);

    private static readonly Vector256<int> TwoI = Set1I(2);

    private static readonly Vector256<int> SignBitI = Set1I(-2147483648); // 0x80000000

    // 256-bit 8-lane horizontal reduce butterflies (3 levels: pair -> quad -> all 8;
    // after level k each 2^k-lane group holds its result in ALL of its lanes).
    private static readonly Vector256<int> CtlSwap2 = Ctl(2, 2, 0, 0, 6, 6, 4, 4);

    private static readonly Vector256<int> CtlSwap4 = Ctl(4, 5, 6, 7, 0, 1, 2, 3);

    // Fast-mode chroma: multiply by the precomputed reciprocals instead of dividing.
    private static readonly Vector256<float> UInvF = Set1F(1.0f / UDiv);

    private static readonly Vector256<float> VInvF = Set1F(1.0f / VDiv);

    private static unsafe Vector256<float> Set1F(float v)
    {
        var one = stackalloc float[1] { v };
        return Avx2.BroadcastScalarToVector256(one);
    }

    private static Vector256<int> Set1I(int v)
    {
        return Avx2.BroadcastScalarToVector256(Sse2.ConvertScalarToVector128Int32(v));
    }

    private static unsafe Vector256<int> Ctl(
        int l0,
        int l1,
        int l2,
        int l3,
        int l4,
        int l5,
        int l6,
        int l7
    )
    {
        var a = stackalloc int[8] { l0, l1, l2, l3, l4, l5, l6, l7 };
        return Avx.LoadVector256(a);
    }

    /// <summary>Float to int bitcast (reinterprets the lane bits, no conversion).</summary>
    private static Vector256<int> ToI(Vector256<float> v)
    {
        return Unsafe.As<Vector256<float>, Vector256<int>>(ref v);
    }

    /// <summary>Int to float bitcast (reinterprets the lane bits, no conversion).</summary>
    private static Vector256<float> ToF(Vector256<int> v)
    {
        return Unsafe.As<Vector256<int>, Vector256<float>>(ref v);
    }

    /// <summary>
    /// Adjacent pair sums per 128-bit lane: [v0+v1, v0+v1, v2+v3, v2+v3 | v4+v5, v4+v5, v6+v7, v6+v7]
    /// (IEEE float add). .NET 10's 256-bit HorizontalAdd isolates 128-bit lanes (duplicating the
    /// low-half result into the high half), so the lane swap goes through a vpermps bitcast and
    /// the sum through a 256-bit float add.
    /// </summary>
    private static Vector256<float> PairSums(Vector256<float> v)
    {
        ref Vector256<int> vI = ref Unsafe.As<Vector256<float>, Vector256<int>>(ref v);
        Vector256<int> swapI = Avx2.PermuteVar8x32(vI, CtlSwap);
        return Avx.Add(v, ToF(swapI));
    }

    /// <summary>
    /// Fast-LUT index remap (PqLutFastF); see that comment for the bin layout. Bit-exact.
    /// t is in [0, 65535]. j = t for t &lt; 4096 (exact-resolution low quarter), else the
    /// 4-slot center-sampled bin index 4096 + ((t - 4096 + 2) &gt;&gt; 2) in [4096, 19456].
    /// The "up" operand is built from a clamped (t &gt;= 4096) so it is an in-range LUT
    /// index in EVERY lane; the mux then picks t for the low lanes. The "t &lt; 4096" mask
    /// must be ALL-ONES, but the projection has no int CompareLessThan or 256-bit
    /// arithmetic shift, and a plain logical ShiftRightLogical(d, 31) on a negative d
    /// yields 1 (bit 0 only), NOT -1. So: isolate the sign bit, logical-shift it to bit 0
    /// (1 iff d &lt; 0), then negate (0 - sbit): 1 -&gt; -1 (all-ones), 0 -&gt; 0.
    /// </summary>
    private static Vector256<int> FastLutIdx(Vector256<int> t)
    {
        Vector256<int> hi = Avx2.Max(t, FourK96I); // >= 4096 in every lane
        Vector256<int> up = Avx2.Add(
            FourK96I,
            Avx2.ShiftRightLogical(Avx2.Add(Avx2.Subtract(hi, FourK96I), TwoI), 2)
        );
        Vector256<int> d = Avx2.Subtract(t, FourK96I);
        Vector256<int> sbit = Avx2.ShiftRightLogical(Avx2.And(d, SignBitI), 31); // 1 iff d < 0
        Vector256<int> mask = Avx2.Subtract(ZeroI, sbit); // all-ones where t < 4096, else 0
        // .NET AndNot(a, b) = ~a & b (first arg inverted); pinned in the probe.
        return Avx2.Or(Avx2.And(t, mask), Avx2.AndNot(mask, up));
    }

    /// <summary>
    /// 256-bit 8-lane horizontal min/max/sum (bit-exact: integer min/max/sum are
    /// order-independent). 3-level butterfly tree (pair -&gt; quad -&gt; all 8): after level k
    /// each 2^k-lane group holds that group's result in ALL of its lanes, so the final
    /// vector carries the full 8-lane result in every lane.
    /// </summary>
    private static Vector256<int> HMin8(Vector256<int> v)
    {
        v = Avx2.Min(v, Avx2.PermuteVar8x32(v, CtlSwap)); // [m01 x2, m23 x2, m45 x2, m67 x2]
        v = Avx2.Min(v, Avx2.PermuteVar8x32(v, CtlSwap2)); // [m0..3 x4, m4..7 x4]
        return Avx2.Min(v, Avx2.PermuteVar8x32(v, CtlSwap4)); // all lanes = min over the 8
    }

    private static Vector256<int> HMax8(Vector256<int> v)
    {
        v = Avx2.Max(v, Avx2.PermuteVar8x32(v, CtlSwap));
        v = Avx2.Max(v, Avx2.PermuteVar8x32(v, CtlSwap2));
        return Avx2.Max(v, Avx2.PermuteVar8x32(v, CtlSwap4)); // all lanes = max over the 8
    }

    private static Vector256<int> HSum8(Vector256<int> v)
    {
        v = Avx2.Add(v, Avx2.PermuteVar8x32(v, CtlSwap)); // [s01 x2, s23 x2, s45 x2, s67 x2]
        v = Avx2.Add(v, Avx2.PermuteVar8x32(v, CtlSwap2)); // [s0..3 x4, s4..7 x4]
        return Avx2.Add(v, Avx2.PermuteVar8x32(v, CtlSwap4)); // all lanes = total
    }

    /// <summary>
    /// Chroma 0..1 from a PQ-domain channel delta (delta = bp-yp or rp-yp): divide by
    /// UDiv/VDiv (exact mode, bit-match with the scalar path) or multiply by the
    /// precomputed reciprocal (fast mode).
    /// </summary>
    private static Vector256<float> Chroma01(
        Vector256<float> delta,
        Vector256<float> divOrMulF,
        bool fast
    )
    {
        Vector256<float> f = fast
            ? Avx.Add(Half01F, Avx.Multiply(delta, divOrMulF))
            : Avx.Add(Half01F, Avx.Divide(delta, divOrMulF));
        return Avx.Min(Avx.Max(f, ZeroF), OneF);
    }

    /// <summary>
    /// 8-pixel AVX2 group (baseline kernel): luma Y + clli stats, and (even rows only)
    /// the 2x2 horizontal chroma for the group's four (x, x+1) pairs. Writes Y (8 x
    /// 16-bit) and, when <c>evenRow</c>, U/V (4 x 16-bit each) at the group's chroma
    /// position (<c>cbBase + x0/2</c>). Same LUTs and op order as the scalar path
    /// (explicit mul/add/div, no FMA); validated bit-for-bit against it over 210,852
    /// mixed groups (0 mismatches) before porting. Fast mode swaps in the 80 KB hybrid
    /// PQ LUT (via <see cref="FastLutIdx"/>) and reciprocal chroma multiplies.
    /// </summary>
    private unsafe void ConvertHdrPixelGroupAvx2(
        byte[] src,
        int srcRow,
        int x0,
        int yOff,
        bool evenRow,
        int cbBase,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        byte* sp = (byte*)Unsafe.AsPointer(ref src[0]) + ((srcRow + x0) * 8);
        Vector256<int> v0 = Avx.LoadVector256((int*)sp);
        Vector256<int> v1 = Avx.LoadVector256((int*)(sp + 32));

        // ---- Extract the 8 R/G/B half-float bit patterns as 32-bit LUT indices. ----
        // A 32-byte block is 4 pixels, each R G B A (2 bytes/channel). 32-bit words:
        // [R|G][B|A]..., so R = low16 of words 0,2,4,6; G = high16 of words 0,2,4,6
        // (psrld 16); B = low16 of words 1,3,5,7. Cross-4-pixel combine via InsertVector128
        // (.NET 10's Permute2x128 zeroes the high half, so it is not usable here).
        Vector256<int> v0m = Avx2.And(v0, Lo16I);
        Vector256<int> v1m = Avx2.And(v1, Lo16I);
        Vector256<int> R8 = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(v0m, CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(v1m, CtlEven), 0),
            1
        );
        Vector256<int> G8 = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(Avx2.ShiftRightLogical(v0, 16), CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(Avx2.ShiftRightLogical(v1, 16), CtlEven), 0),
            1
        );
        Vector256<int> B8 = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(v0m, CtlOdd),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(v1m, CtlOdd), 0),
            1
        );

        // ---- half -> float (gather from the same 256 KB LUT the scalar path uses) ----
        float* halfLut = (float*)Unsafe.AsPointer(ref HalfToFloat[0]);
        Vector256<float> fr = Avx2.GatherVector256(halfLut, R8, 4);
        Vector256<float> fg = Avx2.GatherVector256(halfLut, G8, 4);
        Vector256<float> fb = Avx2.GatherVector256(halfLut, B8, 4);

        // ---- WGC scene reference (80 nits/unit) -> BT.2020 linear 0..1 (same op order) ----
        // The min/max clamp chain doubles as the scalar NaN guard + Math.Clamp: a NaN in any
        // channel makes every matrix row NaN (all Ainv coefficients > 0) -> 0, and +/-inf
        // clamps to 1/0 (SSE min/max take the non-NaN operand).
        Vector256<float> m = Avx.Add(
            Avx.Add(Avx.Multiply(fr, Ainv0F), Avx.Multiply(fg, Ainv1F)),
            Avx.Multiply(fb, Ainv2F)
        );
        Vector256<float> r01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(fr, Ainv3F), Avx.Multiply(fg, Ainv4F)),
            Avx.Multiply(fb, Ainv5F)
        );
        Vector256<float> g01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(fr, Ainv6F), Avx.Multiply(fg, Ainv7F)),
            Avx.Multiply(fb, Ainv8F)
        );
        Vector256<float> b01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);

        // ---- PQ OETF (float gather; same LUT + index as the scalar path, except fast
        // mode remaps the 65535-scaled index onto the 80 KB hybrid LUT) ----
        // Index is the SIMD mirror of PqIndex: (int)(v*65535+0.5) clamped to [0,65535]
        // (explicit mul/add, no FMA, so it matches the scalar rounding).
        float* pqLut = (float*)Unsafe.AsPointer(ref _pqLutF[0]);
        Vector256<float> f = Avx.Add(Avx.Multiply(r01, SixtF), Half01F);
        Vector256<int> t = Avx2.Min(
            Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI),
            MaxPqIdxI
        );
        Vector256<int> idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> rp = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(g01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> gp = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(b01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> bp = Avx2.GatherVector256(pqLut, idx, 4);

        // ---- BT.2020 luma in the PQ domain -> 10-bit code ----
        Vector256<float> yp = Avx.Add(
            Avx.Add(Avx.Multiply(rp, YrF), Avx.Multiply(gp, YgF)),
            Avx.Multiply(bp, YbF)
        );
        Vector256<float> yvF = Avx.Add(Avx.Multiply(yp, YScaleF), Half01F);
        Vector256<int> yv = Avx2.Min(
            Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(yvF), ZeroI),
            MaxY10I
        );

        // ---- clli stats (vectorized 3-level butterfly; bit-exact, int min/max/sum are
        // order-free). Lane extraction: store the low 128-bit half and read lane 0 (the
        // projection's Sse2.Extract only has the 16-bit overload).
        Vector256<int> mn = HMin8(yv);
        Vector256<int> mx = HMax8(yv);
        Vector256<int> su = HSum8(yv);
        int* st = stackalloc int[4];
        Sse2.Store(st, Avx.ExtractVector128(mn, 0));
        int cMin = st[0];
        Sse2.Store(st, Avx.ExtractVector128(mx, 0));
        int cMax = st[0];
        Sse2.Store(st, Avx.ExtractVector128(su, 0));
        long cSum = st[0];
        if (cMin < min)
            min = cMin;
        if (cMax > max)
            max = cMax;
        sum += cSum;

        // ---- write Y (8 x 16-bit = 16 bytes) ----
        // 128-bit signed pack of the two 128-bit halves (codes are 0..1023, so the int16
        // bits == u16 bits), bitcast to the ushort view, 16-byte store.
        ushort* yd = (ushort*)Unsafe.AsPointer(ref _y16![0]) + (yOff + x0);
        Vector128<short> yPacked = Sse2.PackSignedSaturate(
            Avx.ExtractVector128(yv, 0),
            Avx.ExtractVector128(yv, 1)
        );
        Sse2.Store(yd, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref yPacked));

        if (evenRow)
        {
            // ---- 2x2 horizontal chroma (PQ domain, neutral 512) for the four (x, x+1) pairs ----
            Vector256<float> u01 = Chroma01(
                Avx.Subtract(bp, yp),
                _fastConvert ? UInvF : UDivF,
                _fastConvert
            );
            Vector256<float> v01 = Chroma01(
                Avx.Subtract(rp, yp),
                _fastConvert ? VInvF : VDivF,
                _fastConvert
            );

            // Pair averages: .NET 10's 256-bit HorizontalAdd isolates 128-bit lanes (its
            // lanes 2,3 would duplicate (u0+u1, u2+u3) instead of (u4+u5, u6+u7)), so the
            // pair sums go through the lane-swap permute and the four averages land in
            // lanes 0-3. 10-bit codes: SIMD mirror of Lin10 (v*1023+0.5, clamped [0,1023]).
            Vector256<float> avgU = Avx.Multiply(
                ToF(Avx2.PermuteVar8x32(ToI(PairSums(u01)), CtlEven)),
                Half01F
            );
            Vector256<float> f10 = Avx.Add(Avx.Multiply(avgU, YScaleF), Half01F);
            Vector256<int> uC = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );
            Vector256<float> avgV = Avx.Multiply(
                ToF(Avx2.PermuteVar8x32(ToI(PairSums(v01)), CtlEven)),
                Half01F
            );
            f10 = Avx.Add(Avx.Multiply(avgV, YScaleF), Half01F);
            Vector256<int> vC = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );

            // Pack the four 10-bit codes to 4 x 16-bit and write the low 8 bytes (codes are
            // 0..1023, so the signed 128-bit pack is bit-identical to the unsigned one).
            ushort* ud = (ushort*)Unsafe.AsPointer(ref _u16![0]) + (cbBase + x0 / 2);
            ushort* vd = (ushort*)Unsafe.AsPointer(ref _v16![0]) + (cbBase + x0 / 2);
            Vector128<short> uPs = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(uC, 0),
                Avx.ExtractVector128(uC, 0)
            );
            Vector128<short> vPs = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(vC, 0),
                Avx.ExtractVector128(vC, 0)
            );
            Vector128<ushort> uP = Unsafe.As<Vector128<short>, Vector128<ushort>>(ref uPs);
            Vector128<ushort> vP = Unsafe.As<Vector128<short>, Vector128<ushort>>(ref vPs);
            ud[0] = Sse2.Extract(uP, 0);
            ud[1] = Sse2.Extract(uP, 1);
            ud[2] = Sse2.Extract(uP, 2);
            ud[3] = Sse2.Extract(uP, 3);
            vd[0] = Sse2.Extract(vP, 0);
            vd[1] = Sse2.Extract(vP, 1);
            vd[2] = Sse2.Extract(vP, 2);
            vd[3] = Sse2.Extract(vP, 3);
        }
    }

    /// <summary>
    /// 8-pixel AVX2 group, gather-reduction variant: the half-&gt;float step replaces 3 x
    /// 256 KB LUT gathers with ~15 integer ops + 1 x 8 KB L1-resident subnormal gather
    /// (<see cref="HalfToFloatSimd256"/>), measured SLOWER than the 3-gather baseline on
    /// this CPU (the LUT stays L2/L3-hot there) - kept as the avx2-fast A/B kernel. Same
    /// LUTs/op order and fast-mode hooks as <see cref="ConvertHdrPixelGroupAvx2"/>.
    /// </summary>
    private unsafe void ConvertHdrPixelGroupAvx2Fast(
        byte[] src,
        int srcRow,
        int x0,
        int yOff,
        bool evenRow,
        int cbBase,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        byte* sp = (byte*)Unsafe.AsPointer(ref src[0]) + ((srcRow + x0) * 8);
        Vector256<int> v0 = Avx.LoadVector256((int*)sp);
        Vector256<int> v1 = Avx.LoadVector256((int*)(sp + 32));

        // ---- Extract the 8 R/G/B half-float bit patterns as 32-bit LUT indices. ----
        // (See ConvertHdrPixelGroupAvx2 for the word layout.)
        Vector256<int> v0m = Avx2.And(v0, Lo16I);
        Vector256<int> v1m = Avx2.And(v1, Lo16I);
        Vector256<int> R8 = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(v0m, CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(v1m, CtlEven), 0),
            1
        );
        Vector256<int> G8 = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(Avx2.ShiftRightLogical(v0, 16), CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(Avx2.ShiftRightLogical(v1, 16), CtlEven), 0),
            1
        );
        Vector256<int> B8 = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(v0m, CtlOdd),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(v1m, CtlOdd), 0),
            1
        );

        // ---- half -> float (gather-reduction fast path, bit-exact with HalfToFloat) ----
        // The normal / inf / NaN / zero cases are computed with ~15 integer ops; only the
        // subnormal case (exponent field 0, 2048 of 65536 values) gathers, from the 8 KB
        // L1-resident SubnormalLut instead of the 256 KB HalfToFloat (x3 per group).
        Vector256<float> fr = HalfToFloatSimd256(R8);
        Vector256<float> fg = HalfToFloatSimd256(G8);
        Vector256<float> fb = HalfToFloatSimd256(B8);

        // ---- WGC scene reference (80 nits/unit) -> BT.2020 linear 0..1 (same op order) ----
        // The min/max clamp chain doubles as the scalar NaN guard + Math.Clamp (as in
        // ConvertHdrPixelGroupAvx2).
        Vector256<float> m = Avx.Add(
            Avx.Add(Avx.Multiply(fr, Ainv0F), Avx.Multiply(fg, Ainv1F)),
            Avx.Multiply(fb, Ainv2F)
        );
        Vector256<float> r01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(fr, Ainv3F), Avx.Multiply(fg, Ainv4F)),
            Avx.Multiply(fb, Ainv5F)
        );
        Vector256<float> g01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(fr, Ainv6F), Avx.Multiply(fg, Ainv7F)),
            Avx.Multiply(fb, Ainv8F)
        );
        Vector256<float> b01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);

        // ---- PQ OETF (float gather; fast mode remaps the index onto the hybrid LUT) ----
        float* pqLut = (float*)Unsafe.AsPointer(ref _pqLutF[0]);
        Vector256<float> f = Avx.Add(Avx.Multiply(r01, SixtF), Half01F);
        Vector256<int> t = Avx2.Min(
            Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI),
            MaxPqIdxI
        );
        Vector256<int> idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> rp = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(g01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> gp = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(b01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> bp = Avx2.GatherVector256(pqLut, idx, 4);

        // ---- BT.2020 luma in the PQ domain -> 10-bit code ----
        Vector256<float> yp = Avx.Add(
            Avx.Add(Avx.Multiply(rp, YrF), Avx.Multiply(gp, YgF)),
            Avx.Multiply(bp, YbF)
        );
        Vector256<float> yvF = Avx.Add(Avx.Multiply(yp, YScaleF), Half01F);
        Vector256<int> yv = Avx2.Min(
            Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(yvF), ZeroI),
            MaxY10I
        );

        // ---- clli stats (vectorized 3-level butterfly; bit-exact) ----
        Vector256<int> mn = HMin8(yv);
        Vector256<int> mx = HMax8(yv);
        Vector256<int> su = HSum8(yv);
        int* st = stackalloc int[4];
        Sse2.Store(st, Avx.ExtractVector128(mn, 0));
        int cMin = st[0];
        Sse2.Store(st, Avx.ExtractVector128(mx, 0));
        int cMax = st[0];
        Sse2.Store(st, Avx.ExtractVector128(su, 0));
        long cSum = st[0];
        if (cMin < min)
            min = cMin;
        if (cMax > max)
            max = cMax;
        sum += cSum;

        // ---- write Y (8 x 16-bit = 16 bytes) ----
        ushort* yd = (ushort*)Unsafe.AsPointer(ref _y16![0]) + (yOff + x0);
        Vector128<short> yPacked = Sse2.PackSignedSaturate(
            Avx.ExtractVector128(yv, 0),
            Avx.ExtractVector128(yv, 1)
        );
        Sse2.Store(yd, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref yPacked));

        if (evenRow)
        {
            // ---- 2x2 horizontal chroma (PQ domain, neutral 512) for the four (x, x+1) pairs ----
            Vector256<float> u01 = Chroma01(
                Avx.Subtract(bp, yp),
                _fastConvert ? UInvF : UDivF,
                _fastConvert
            );
            Vector256<float> v01 = Chroma01(
                Avx.Subtract(rp, yp),
                _fastConvert ? VInvF : VDivF,
                _fastConvert
            );

            Vector256<float> avgU = Avx.Multiply(
                ToF(Avx2.PermuteVar8x32(ToI(PairSums(u01)), CtlEven)),
                Half01F
            );
            Vector256<float> f10 = Avx.Add(Avx.Multiply(avgU, YScaleF), Half01F);
            Vector256<int> uC = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );
            Vector256<float> avgV = Avx.Multiply(
                ToF(Avx2.PermuteVar8x32(ToI(PairSums(v01)), CtlEven)),
                Half01F
            );
            f10 = Avx.Add(Avx.Multiply(avgV, YScaleF), Half01F);
            Vector256<int> vC = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );

            // Pack the four 10-bit codes to 4 x 16-bit and write the low 8 bytes.
            ushort* ud = (ushort*)Unsafe.AsPointer(ref _u16![0]) + (cbBase + x0 / 2);
            ushort* vd = (ushort*)Unsafe.AsPointer(ref _v16![0]) + (cbBase + x0 / 2);
            Vector128<short> uPs = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(uC, 0),
                Avx.ExtractVector128(uC, 0)
            );
            Vector128<short> vPs = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(vC, 0),
                Avx.ExtractVector128(vC, 0)
            );
            Vector128<ushort> uP = Unsafe.As<Vector128<short>, Vector128<ushort>>(ref uPs);
            Vector128<ushort> vP = Unsafe.As<Vector128<short>, Vector128<ushort>>(ref vPs);
            ud[0] = Sse2.Extract(uP, 0);
            ud[1] = Sse2.Extract(uP, 1);
            ud[2] = Sse2.Extract(uP, 2);
            ud[3] = Sse2.Extract(uP, 3);
            vd[0] = Sse2.Extract(vP, 0);
            vd[1] = Sse2.Extract(vP, 1);
            vd[2] = Sse2.Extract(vP, 2);
            vd[3] = Sse2.Extract(vP, 3);
        }
    }

    /// <summary>
    /// 16-pixel wide AVX2 group (two 8-px 256-bit halves, batched gathers): all six
    /// half-LUT gathers are front-loaded (48 loads in flight before any ALU) and half A's
    /// three PQ gathers are issued while half B's matrix runs - doubling the memory-level
    /// parallelism of the 8-px kernel without AVX-512 (Zen 5 upconverts 512-bit ints to
    /// 2x 256-bit anyway). Same op set as <see cref="ConvertHdrPixelGroupAvx2"/>, so
    /// exact-mode results are bit-identical to the scalar path (487,640 groups, 0
    /// mismatches in the probe); fast mode uses the 80 KB hybrid PQ LUT + reciprocal
    /// chroma (±1 luma / ±2..3 chroma codes).
    /// </summary>
    private unsafe void ConvertHdrPixelGroupAvx2Wide(
        byte[] src,
        int srcRow,
        int x0,
        int yOff,
        bool evenRow,
        int cbBase,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        byte* sp = (byte*)Unsafe.AsPointer(ref src[0]) + ((srcRow + x0) * 8);
        Vector256<int> a0 = Avx.LoadVector256((int*)sp);
        Vector256<int> a1 = Avx.LoadVector256((int*)(sp + 32));
        Vector256<int> b0 = Avx.LoadVector256((int*)(sp + 64));
        Vector256<int> b1 = Avx.LoadVector256((int*)(sp + 96));

        // R = low16 of even words; G = high16 of even words (shift 16); B = low16 of odd
        // words - per 8-px half (see ConvertHdrPixelGroupAvx2 for the word layout).
        Vector256<int> a0m = Avx2.And(a0, Lo16I);
        Vector256<int> a1m = Avx2.And(a1, Lo16I);
        Vector256<int> b0m = Avx2.And(b0, Lo16I);
        Vector256<int> b1m = Avx2.And(b1, Lo16I);
        Vector256<int> a0s = Avx2.ShiftRightLogical(a0, 16);
        Vector256<int> a1s = Avx2.ShiftRightLogical(a1, 16);
        Vector256<int> b0s = Avx2.ShiftRightLogical(b0, 16);
        Vector256<int> b1s = Avx2.ShiftRightLogical(b1, 16);
        Vector256<int> rA = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(a0m, CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(a1m, CtlEven), 0),
            1
        );
        Vector256<int> gA = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(a0s, CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(a1s, CtlEven), 0),
            1
        );
        Vector256<int> bA = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(a0m, CtlOdd),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(a1m, CtlOdd), 0),
            1
        );
        Vector256<int> rB = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(b0m, CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(b1m, CtlEven), 0),
            1
        );
        Vector256<int> gB = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(b0s, CtlEven),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(b1s, CtlEven), 0),
            1
        );
        Vector256<int> bB = Avx2.InsertVector128(
            Avx2.PermuteVar8x32(b0m, CtlOdd),
            Avx.ExtractVector128(Avx2.PermuteVar8x32(b1m, CtlOdd), 0),
            1
        );

        // Batch: all six half-LUT gathers up front (48 loads in flight).
        float* halfLut = (float*)Unsafe.AsPointer(ref HalfToFloat[0]);
        Vector256<float> frA = Avx2.GatherVector256(halfLut, rA, 4);
        Vector256<float> fgA = Avx2.GatherVector256(halfLut, gA, 4);
        Vector256<float> fbA = Avx2.GatherVector256(halfLut, bA, 4);
        Vector256<float> frB = Avx2.GatherVector256(halfLut, rB, 4);
        Vector256<float> fgB = Avx2.GatherVector256(halfLut, gB, 4);
        Vector256<float> fbB = Avx2.GatherVector256(halfLut, bB, 4);

        // WGC -> BT.2020 linear 0..1 (same op order as the scalar path; the min/max clamp
        // chain doubles as the NaN guard).
        Vector256<float> m = Avx.Add(
            Avx.Add(Avx.Multiply(frA, Ainv0F), Avx.Multiply(fgA, Ainv1F)),
            Avx.Multiply(fbA, Ainv2F)
        );
        Vector256<float> rA01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(frA, Ainv3F), Avx.Multiply(fgA, Ainv4F)),
            Avx.Multiply(fbA, Ainv5F)
        );
        Vector256<float> gA01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(frA, Ainv6F), Avx.Multiply(fgA, Ainv7F)),
            Avx.Multiply(fbA, Ainv8F)
        );
        Vector256<float> bA01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);

        // PQ A: issue the three gathers; their latency overlaps half B's matrix below.
        // Fast mode: remap the exact 65535-scaled index t onto the 80 KB hybrid LUT.
        float* pqLut = (float*)Unsafe.AsPointer(ref _pqLutF[0]);
        Vector256<float> f = Avx.Add(Avx.Multiply(rA01, SixtF), Half01F);
        Vector256<int> t = Avx2.Min(
            Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI),
            MaxPqIdxI
        );
        Vector256<int> idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> rpA = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(gA01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> gpA = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(bA01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> bpA = Avx2.GatherVector256(pqLut, idx, 4);

        m = Avx.Add(
            Avx.Add(Avx.Multiply(frB, Ainv0F), Avx.Multiply(fgB, Ainv1F)),
            Avx.Multiply(fbB, Ainv2F)
        );
        Vector256<float> rB01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(frB, Ainv3F), Avx.Multiply(fgB, Ainv4F)),
            Avx.Multiply(fbB, Ainv5F)
        );
        Vector256<float> gB01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);
        m = Avx.Add(
            Avx.Add(Avx.Multiply(frB, Ainv6F), Avx.Multiply(fgB, Ainv7F)),
            Avx.Multiply(fbB, Ainv8F)
        );
        Vector256<float> bB01 = Avx.Min(Avx.Max(Avx.Multiply(m, UpnF), ZeroF), OneF);

        f = Avx.Add(Avx.Multiply(rB01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> rpB = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(gB01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> gpB = Avx2.GatherVector256(pqLut, idx, 4);
        f = Avx.Add(Avx.Multiply(bB01, SixtF), Half01F);
        t = Avx2.Min(Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f), ZeroI), MaxPqIdxI);
        idx = _fastConvert ? FastLutIdx(t) : t;
        Vector256<float> bpB = Avx2.GatherVector256(pqLut, idx, 4);

        // BT.2020 luma in the PQ domain -> 10-bit codes
        Vector256<float> ypA = Avx.Add(
            Avx.Add(Avx.Multiply(rpA, YrF), Avx.Multiply(gpA, YgF)),
            Avx.Multiply(bpA, YbF)
        );
        Vector256<int> yvA = Avx2.Min(
            Avx2.Max(
                Avx.ConvertToVector256Int32WithTruncation(
                    Avx.Add(Avx.Multiply(ypA, YScaleF), Half01F)
                ),
                ZeroI
            ),
            MaxY10I
        );
        Vector256<float> ypB = Avx.Add(
            Avx.Add(Avx.Multiply(rpB, YrF), Avx.Multiply(gpB, YgF)),
            Avx.Multiply(bpB, YbF)
        );
        Vector256<int> yvB = Avx2.Min(
            Avx2.Max(
                Avx.ConvertToVector256Int32WithTruncation(
                    Avx.Add(Avx.Multiply(ypB, YScaleF), Half01F)
                ),
                ZeroI
            ),
            MaxY10I
        );

        // clli stats: vectorized 16-code reduce (bit-exact: int min/max/sum are order-free)
        Vector256<int> mn = Avx2.Min(HMin8(yvA), HMin8(yvB));
        Vector256<int> mx = Avx2.Max(HMax8(yvA), HMax8(yvB));
        Vector256<int> su = Avx2.Add(HSum8(yvA), HSum8(yvB));
        // Lane extraction: store the low 128-bit halves and read lane 0 (the projection's
        // Sse2.Extract only has the 16-bit overload).
        int* st = stackalloc int[4];
        Sse2.Store(st, Avx.ExtractVector128(mn, 0));
        int cMin = st[0];
        Sse2.Store(st, Avx.ExtractVector128(mx, 0));
        int cMax = st[0];
        Sse2.Store(st, Avx.ExtractVector128(su, 0));
        long cSum = st[0];
        if (cMin < min)
            min = cMin;
        if (cMax > max)
            max = cMax;
        sum += cSum;

        // Y: 16 x 16-bit, two 16-byte stores (256-bit pack idiom; codes 0..1023, so the
        // signed-pack bits == u16 bits).
        ushort* yd = (ushort*)Unsafe.AsPointer(ref _y16![0]) + (yOff + x0);
        Vector128<short> yAp = Sse2.PackSignedSaturate(
            Avx.ExtractVector128(yvA, 0),
            Avx.ExtractVector128(yvA, 1)
        );
        Vector128<short> yBp = Sse2.PackSignedSaturate(
            Avx.ExtractVector128(yvB, 0),
            Avx.ExtractVector128(yvB, 1)
        );
        Sse2.Store(yd, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref yAp));
        Sse2.Store(yd + 8, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref yBp));

        if (evenRow)
        {
            // 2x2 horizontal chroma: u01 = 0.5 + (bp - yp)/UDiv (exact) or * (1/UDiv) (fast)
            Vector256<float> u01A = Chroma01(
                Avx.Subtract(bpA, ypA),
                _fastConvert ? UInvF : UDivF,
                _fastConvert
            );
            Vector256<float> v01A = Chroma01(
                Avx.Subtract(rpA, ypA),
                _fastConvert ? VInvF : VDivF,
                _fastConvert
            );
            Vector256<float> u01B = Chroma01(
                Avx.Subtract(bpB, ypB),
                _fastConvert ? UInvF : UDivF,
                _fastConvert
            );
            Vector256<float> v01B = Chroma01(
                Avx.Subtract(rpB, ypB),
                _fastConvert ? VInvF : VDivF,
                _fastConvert
            );

            // 16 px -> 8 chroma codes: four (x, x+1) pairs per half, pair-sum x 0.5, x 1023
            Vector256<float> avgU = Avx.Multiply(
                ToF(Avx2.PermuteVar8x32(ToI(PairSums(u01A)), CtlEven)),
                Half01F
            );
            Vector256<float> f10 = Avx.Add(Avx.Multiply(avgU, YScaleF), Half01F);
            Vector256<int> uCA = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );
            avgU = Avx.Multiply(ToF(Avx2.PermuteVar8x32(ToI(PairSums(u01B)), CtlEven)), Half01F);
            f10 = Avx.Add(Avx.Multiply(avgU, YScaleF), Half01F);
            Vector256<int> uCB = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );
            Vector256<float> avgV = Avx.Multiply(
                ToF(Avx2.PermuteVar8x32(ToI(PairSums(v01A)), CtlEven)),
                Half01F
            );
            f10 = Avx.Add(Avx.Multiply(avgV, YScaleF), Half01F);
            Vector256<int> vCA = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );
            avgV = Avx.Multiply(ToF(Avx2.PermuteVar8x32(ToI(PairSums(v01B)), CtlEven)), Half01F);
            f10 = Avx.Add(Avx.Multiply(avgV, YScaleF), Half01F);
            Vector256<int> vCB = Avx2.Min(
                Avx2.Max(Avx.ConvertToVector256Int32WithTruncation(f10), ZeroI),
                MaxY10I
            );

            // 8 x 16-bit chroma codes, one 16-byte store each: half A's 4 codes (lanes 0-3
            // of uCA) + half B's 4 codes (lanes 0-3 of uCB), packed as one 128-bit
            // (codes are 0..1023, so the signed-pack bits == u16 bits).
            ushort* ud = (ushort*)Unsafe.AsPointer(ref _u16![0]) + (cbBase + x0 / 2);
            ushort* vd = (ushort*)Unsafe.AsPointer(ref _v16![0]) + (cbBase + x0 / 2);
            Vector128<short> uP = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(uCA, 0),
                Avx.ExtractVector128(uCB, 0)
            );
            Vector128<short> vP = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(vCA, 0),
                Avx.ExtractVector128(vCB, 0)
            );
            Sse2.Store(ud, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref uP));
            Sse2.Store(vd, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref vP));
        }
    }

    // ------------------------------------------------------------------
    // AVX-512 (512-bit) kernel: 16 source pixels per iteration. Used only when
    // SimdAvx512 (dispatched via the instance _kernel field); the 256-bit kernel
    // above stays the universal fallback. Every 512-bit vector constant below is
    // gated on SimdAvx512 (IsSupported is pure CPUID and never throws, but the
    // intrinsic calls that build the constants would throw at type init on other
    // CPUs).
    // ------------------------------------------------------------------

    private static readonly Vector512<int> ZeroI512 = default;

    private static readonly Vector512<float> ZeroF512 = default;

    private static readonly Vector512<int> Lo16I512 = SimdAvx512 ? Set1I512(0x0000FFFF) : default;

    private static readonly Vector512<int> MaxPqIdxI512 = SimdAvx512 ? Set1I512(65535) : default;

    private static readonly Vector512<int> MaxY10I512 = SimdAvx512 ? Set1I512(1023) : default;

    private static unsafe Vector512<int> Ctl512(int[] lanes)
    {
        var a = stackalloc int[16];
        for (int i = 0; i < 16; i++)
            a[i] = lanes[i];
        return Avx512F.LoadVector512(a);
    }

    // PermuteVar16x32 permutes across the full 512-bit vector (absolute 0-15 lane
    // indices; pinned in the probe): the even/odd-gather controls collect a block's
    // 8 channel values into lanes 0-7; the swap/even-gather pair drives the 16-px
    // chroma pipeline.
    private static readonly Vector512<int> CtlEven16 = SimdAvx512
        ? Ctl512(new[] { 0, 2, 4, 6, 8, 10, 12, 14, 0, 0, 0, 0, 0, 0, 0, 0 })
        : default;

    private static readonly Vector512<int> CtlOdd16 = SimdAvx512
        ? Ctl512(new[] { 1, 3, 5, 7, 9, 11, 13, 15, 0, 0, 0, 0, 0, 0, 0, 0 })
        : default;

    private static readonly Vector512<int> CtlSwap16 = SimdAvx512
        ? Ctl512(new[] { 1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14 })
        : default;

    private static readonly Vector512<int> CtlEvenGather16 = SimdAvx512
        ? Ctl512(new[] { 0, 2, 4, 6, 8, 10, 12, 14, 0, 0, 0, 0, 0, 0, 0, 0 })
        : default;

    private static unsafe Vector512<float> Set1F512(float v)
    {
        var arr = stackalloc float[4] { v, v, v, v };
        return Avx512F.BroadcastScalarToVector512(Avx.LoadVector128(arr));
    }

    private static unsafe Vector512<int> Set1I512(int v)
    {
        var arr = stackalloc int[4] { v, v, v, v };
        return Avx512F.BroadcastScalarToVector512(Avx.LoadVector128(arr));
    }

    /// <summary>Float to int bitcast for 512-bit vectors (reinterprets the lane bits).</summary>
    private static Vector512<int> ToI512(Vector512<float> v)
    {
        return Unsafe.As<Vector512<float>, Vector512<int>>(ref v);
    }

    /// <summary>Int to float bitcast for 512-bit vectors (reinterprets the lane bits).</summary>
    private static Vector512<float> ToF512(Vector512<int> v)
    {
        return Unsafe.As<Vector512<int>, Vector512<float>>(ref v);
    }

    private static readonly Vector512<float> OneF512 = SimdAvx512 ? Set1F512(1.0f) : default;

    private static readonly Vector512<float> UpnF512 = SimdAvx512 ? Set1F512(UnitPerNits) : default;

    private static readonly Vector512<float> Ainv0F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[0])
        : default;

    private static readonly Vector512<float> Ainv1F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[1])
        : default;

    private static readonly Vector512<float> Ainv2F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[2])
        : default;

    private static readonly Vector512<float> Ainv3F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[3])
        : default;

    private static readonly Vector512<float> Ainv4F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[4])
        : default;

    private static readonly Vector512<float> Ainv5F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[5])
        : default;

    private static readonly Vector512<float> Ainv6F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[6])
        : default;

    private static readonly Vector512<float> Ainv7F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[7])
        : default;

    private static readonly Vector512<float> Ainv8F512 = SimdAvx512
        ? Set1F512(WgcTo2020Ainv[8])
        : default;

    private static readonly Vector512<float> YrF512 = SimdAvx512 ? Set1F512(Yr) : default;

    private static readonly Vector512<float> YgF512 = SimdAvx512 ? Set1F512(Yg) : default;

    private static readonly Vector512<float> YbF512 = SimdAvx512 ? Set1F512(Yb) : default;

    private static readonly Vector512<float> UDivF512 = SimdAvx512 ? Set1F512(UDiv) : default;

    private static readonly Vector512<float> VDivF512 = SimdAvx512 ? Set1F512(VDiv) : default;

    // Fast-mode (PqLutFastF remap + reciprocal chroma), gated on SimdAvx512 like the
    // other 512-bit constants.
    private static readonly Vector512<float> UInvF512 = SimdAvx512
        ? Set1F512(1.0f / UDiv)
        : default;

    private static readonly Vector512<float> VInvF512 = SimdAvx512
        ? Set1F512(1.0f / VDiv)
        : default;

    private static readonly Vector512<int> FourK96I512 = SimdAvx512 ? Set1I512(4096) : default;

    private static readonly Vector512<int> TwoI512 = SimdAvx512 ? Set1I512(2) : default;

    private static readonly Vector512<int> SignBitI512 = SimdAvx512
        ? Set1I512(-2147483648)
        : default;

    // 16-lane horizontal reduce butterflies (4 levels: pair -> quad -> oct -> all 16).
    private static readonly Vector512<int> CtlSwap2_16 = SimdAvx512
        ? Ctl512(new[] { 2, 2, 0, 0, 6, 6, 4, 4, 10, 10, 8, 8, 14, 14, 12, 12 })
        : default;

    private static readonly Vector512<int> CtlSwap4_16 = SimdAvx512
        ? Ctl512(new[] { 4, 5, 6, 7, 0, 1, 2, 3, 12, 13, 14, 15, 8, 9, 10, 11 })
        : default;

    private static readonly Vector512<int> CtlSwap8_16 = SimdAvx512
        ? Ctl512(new[] { 8, 9, 10, 11, 12, 13, 14, 15, 0, 1, 2, 3, 4, 5, 6, 7 })
        : default;

    private static readonly Vector512<float> SixtF512 = SimdAvx512 ? Set1F512(65535.0f) : default;

    private static readonly Vector512<float> Half01F512 = SimdAvx512 ? Set1F512(0.5f) : default;

    private static readonly Vector512<float> YScaleF512 = SimdAvx512 ? Set1F512(1023.0f) : default;

    /// <summary>
    /// 512-bit adjacent pair sums: lanes [v0+v1, v0+v1, v2+v3, ...]. A pair never crosses
    /// a 128-bit lane boundary, so this is correct even if the permute were lane-isolated
    /// (it is full-512; pinned in the probe).
    /// </summary>
    private static Vector512<float> PairSums16(Vector512<float> v)
    {
        Vector512<int> swap = Avx512F.PermuteVar16x32(ToI512(v), CtlSwap16);
        return Avx512F.Add(v, ToF512(swap));
    }

    /// <summary>
    /// 512-bit fast-LUT index remap (PqLutFastF); same bin layout and mask idiom as
    /// <see cref="FastLutIdx"/> (the projection has no int CompareLessThan, so the
    /// "t &lt; 4096" mask comes from the sign bit of (t - 4096)). Only ever called on
    /// AVX-512 CPUs (dispatched via <see cref="SimdAvx512"/>).
    /// </summary>
    private static Vector512<int> FastLutIdx512(Vector512<int> t)
    {
        Vector512<int> hi = Avx512F.Max(t, FourK96I512); // >= 4096 in every lane
        Vector512<int> up = Avx512F.Add(
            FourK96I512,
            Avx512F.ShiftRightLogical(Avx512F.Add(Avx512F.Subtract(hi, FourK96I512), TwoI512), 2)
        );
        Vector512<int> d = Avx512F.Subtract(t, FourK96I512);
        Vector512<int> sbit = Avx512F.ShiftRightLogical(Avx512F.And(d, SignBitI512), 31);
        Vector512<int> mask = Avx512F.Subtract(ZeroI512, sbit); // all-ones where t < 4096
        return Avx512F.Or(Avx512F.And(t, mask), Avx512F.AndNot(mask, up));
    }

    /// <summary>
    /// 512-bit 16-lane horizontal min/max/sum (bit-exact: integer min/max/sum are
    /// order-independent). 4-level butterfly tree; the final vector carries the full
    /// 16-lane result in every lane.
    /// </summary>
    private static Vector512<int> HMin16(Vector512<int> v)
    {
        v = Avx512F.Min(v, Avx512F.PermuteVar16x32(v, CtlSwap16));
        v = Avx512F.Min(v, Avx512F.PermuteVar16x32(v, CtlSwap2_16));
        v = Avx512F.Min(v, Avx512F.PermuteVar16x32(v, CtlSwap4_16));
        return Avx512F.Min(v, Avx512F.PermuteVar16x32(v, CtlSwap8_16));
    }

    private static Vector512<int> HMax16(Vector512<int> v)
    {
        v = Avx512F.Max(v, Avx512F.PermuteVar16x32(v, CtlSwap16));
        v = Avx512F.Max(v, Avx512F.PermuteVar16x32(v, CtlSwap2_16));
        v = Avx512F.Max(v, Avx512F.PermuteVar16x32(v, CtlSwap4_16));
        return Avx512F.Max(v, Avx512F.PermuteVar16x32(v, CtlSwap8_16));
    }

    private static Vector512<int> HSum16(Vector512<int> v)
    {
        v = Avx512F.Add(v, Avx512F.PermuteVar16x32(v, CtlSwap16));
        v = Avx512F.Add(v, Avx512F.PermuteVar16x32(v, CtlSwap2_16));
        v = Avx512F.Add(v, Avx512F.PermuteVar16x32(v, CtlSwap4_16));
        return Avx512F.Add(v, Avx512F.PermuteVar16x32(v, CtlSwap8_16));
    }

    /// <summary>
    /// 512-bit chroma 0..1 from a PQ-domain channel delta: divide (exact) or reciprocal
    /// multiply (fast mode). Only ever called on AVX-512 CPUs.
    /// </summary>
    private static Vector512<float> Chroma01_512(
        Vector512<float> delta,
        Vector512<float> divOrMulF,
        bool fast
    )
    {
        Vector512<float> f = fast
            ? Avx512F.Add(Half01F512, Avx512F.Multiply(delta, divOrMulF))
            : Avx512F.Add(Half01F512, Avx512F.Divide(delta, divOrMulF));
        return Avx512F.Min(Avx512F.Max(f, ZeroF512), OneF512);
    }

    /// <summary>
    /// AVX-512 HDR row conversion: 16 source pixels per iteration; an 8-15 pixel tail
    /// runs one AVX2 8-px group (AVX-512 implies AVX2, and its (x, x+1) chroma pairs
    /// stay aligned because the 512-bit bulk is a multiple of 16), and the &lt;8 remainder
    /// falls back to <see cref="ConvertHdrPixel"/> (scalar). Same LUTs and op order as
    /// the scalar path, so the coded P010 values match bit-for-bit.
    /// </summary>
    private void ConvertHdrRowsAvx512(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        int w = OutWidth;
        int halfW = w / 2;
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = (_cropY + row) * srcWidth + _cropX;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * halfW : 0;

            int bulk = w & ~15;
            for (int x0 = 0; x0 < bulk; x0 += 16)
            {
                ConvertHdrPixelGroupAvx512(
                    src,
                    srcRow,
                    x0,
                    yOff,
                    evenRow,
                    cbBase,
                    ref min,
                    ref max,
                    ref sum
                );
            }
            if (w - bulk >= 8)
            {
                ConvertHdrPixelGroupAvx2(
                    src,
                    srcRow,
                    bulk,
                    yOff,
                    evenRow,
                    cbBase,
                    ref min,
                    ref max,
                    ref sum
                );
            }
            for (int x = bulk + (w - bulk >= 8 ? 8 : 0); x < w; x++)
            {
                ConvertHdrPixel(src, srcRow, x, yOff, evenRow, cbBase, ref min, ref max, ref sum);
            }
            if (evenRow)
            {
                cbRow++;
            }
        }
    }

    /// <summary>
    /// 16-pixel AVX-512 group: luma Y + clli stats, and (even rows only) the 2x2
    /// horizontal chroma for the group's eight (x, x+1) pairs. Writes Y (16 x 16-bit,
    /// two 16-byte stores) and, when <c>evenRow</c>, U/V (8 x 16-bit each, one 16-byte
    /// store) at the group's chroma position (<c>cbBase + x0/2</c>). Same LUTs and op
    /// order as the scalar path (explicit mul/add, no FMA); validated bit-for-bit
    /// against it over 148,752 mixed 16-px groups (0 mismatches) before porting.
    /// </summary>
    private unsafe void ConvertHdrPixelGroupAvx512(
        byte[] src,
        int srcRow,
        int x0,
        int yOff,
        bool evenRow,
        int cbBase,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        byte* sp = (byte*)Unsafe.AsPointer(ref src[0]) + ((srcRow + x0) * 8);
        Vector512<int> v0 = Avx512F.LoadVector512((int*)sp);
        Vector512<int> v1 = Avx512F.LoadVector512((int*)(sp + 64));

        // ---- Extract the 16 R/G/B half-float bit patterns. ----
        // A 64-byte block is 8 pixels, each R G B A (2 bytes/channel). 32-bit words:
        // [R|G][B|A]..., so R = low16 of the even words; G = high16 of the even words
        // (logical shift right 16: the R half vanishes, G lands in the low 16 bits);
        // B = low16 of the odd words. PermuteVar16x32 is full-512 cross-lane (pinned
        // in the probe), so each block's 8 channel values land in lanes 0-7 of its
        // permute result and the two blocks combine via ExtractVector256(0) +
        // InsertVector256.
        Vector512<int> v0m = Avx512F.And(v0, Lo16I512);
        Vector512<int> v1m = Avx512F.And(v1, Lo16I512);
        Vector256<int> Rlo = Avx512F.ExtractVector256(Avx512F.PermuteVar16x32(v0m, CtlEven16), 0);
        Vector256<int> Rhi = Avx512F.ExtractVector256(Avx512F.PermuteVar16x32(v1m, CtlEven16), 0);
        Vector512<int> R16 = Avx512F.InsertVector256(
            Avx512F.InsertVector256(default, Rlo, 0),
            Rhi,
            1
        );
        Vector512<int> v0s = Avx512F.ShiftRightLogical(v0, 16);
        Vector512<int> v1s = Avx512F.ShiftRightLogical(v1, 16);
        Vector256<int> Glo = Avx512F.ExtractVector256(Avx512F.PermuteVar16x32(v0s, CtlEven16), 0);
        Vector256<int> Ghi = Avx512F.ExtractVector256(Avx512F.PermuteVar16x32(v1s, CtlEven16), 0);
        Vector512<int> G16 = Avx512F.InsertVector256(
            Avx512F.InsertVector256(default, Glo, 0),
            Ghi,
            1
        );
        Vector256<int> Blo = Avx512F.ExtractVector256(Avx512F.PermuteVar16x32(v0m, CtlOdd16), 0);
        Vector256<int> Bhi = Avx512F.ExtractVector256(Avx512F.PermuteVar16x32(v1m, CtlOdd16), 0);
        Vector512<int> B16 = Avx512F.InsertVector256(
            Avx512F.InsertVector256(default, Blo, 0),
            Bhi,
            1
        );

        // ---- half -> float (gather-reduction fast path, bit-exact with HalfToFloat) ----
        Vector512<float> fr = HalfToFloatSimd512(R16);
        Vector512<float> fg = HalfToFloatSimd512(G16);
        Vector512<float> fb = HalfToFloatSimd512(B16);

        // ---- WGC scene reference (80 nits/unit) -> BT.2020 linear 0..1 (same op order) ----
        // The min/max clamp chain doubles as the scalar NaN guard + Math.Clamp (as in
        // the AVX2 kernel).
        Vector512<float> m = Avx512F.Add(
            Avx512F.Add(Avx512F.Multiply(fr, Ainv0F512), Avx512F.Multiply(fg, Ainv1F512)),
            Avx512F.Multiply(fb, Ainv2F512)
        );
        Vector512<float> r01 = Avx512F.Min(
            Avx512F.Max(Avx512F.Multiply(m, UpnF512), ZeroF512),
            OneF512
        );
        m = Avx512F.Add(
            Avx512F.Add(Avx512F.Multiply(fr, Ainv3F512), Avx512F.Multiply(fg, Ainv4F512)),
            Avx512F.Multiply(fb, Ainv5F512)
        );
        Vector512<float> g01 = Avx512F.Min(
            Avx512F.Max(Avx512F.Multiply(m, UpnF512), ZeroF512),
            OneF512
        );
        m = Avx512F.Add(
            Avx512F.Add(Avx512F.Multiply(fr, Ainv6F512), Avx512F.Multiply(fg, Ainv7F512)),
            Avx512F.Multiply(fb, Ainv8F512)
        );
        Vector512<float> b01 = Avx512F.Min(
            Avx512F.Max(Avx512F.Multiply(m, UpnF512), ZeroF512),
            OneF512
        );

        // ---- PQ OETF (2 x 256-bit float gather per channel; fast mode remaps the index
        // onto the 80 KB hybrid LUT) ----
        // The .NET 10 projection has no 512-bit gather; Zen/Intel upconvert a 512-bit
        // gather to two 256-bit ones anyway. Same LUT + index as the scalar path.
        float* pqLut = (float*)Unsafe.AsPointer(ref _pqLutF[0]);
        Vector512<float> f = Avx512F.Add(Avx512F.Multiply(r01, SixtF512), Half01F512);
        Vector512<int> idx = Avx512F.Min(
            Avx512F.Max(Avx512F.ConvertToVector512Int32WithTruncation(f), ZeroI512),
            MaxPqIdxI512
        );
        if (_fastConvert)
            idx = FastLutIdx512(idx);
        Vector512<float> rp = Avx512F.InsertVector256(
            Avx512F.InsertVector256(
                default,
                Avx2.GatherVector256(pqLut, Avx512F.ExtractVector256(idx, 0), 4),
                0
            ),
            Avx2.GatherVector256(pqLut, Avx512F.ExtractVector256(idx, 1), 4),
            1
        );
        f = Avx512F.Add(Avx512F.Multiply(g01, SixtF512), Half01F512);
        idx = Avx512F.Min(
            Avx512F.Max(Avx512F.ConvertToVector512Int32WithTruncation(f), ZeroI512),
            MaxPqIdxI512
        );
        if (_fastConvert)
            idx = FastLutIdx512(idx);
        Vector512<float> gp = Avx512F.InsertVector256(
            Avx512F.InsertVector256(
                default,
                Avx2.GatherVector256(pqLut, Avx512F.ExtractVector256(idx, 0), 4),
                0
            ),
            Avx2.GatherVector256(pqLut, Avx512F.ExtractVector256(idx, 1), 4),
            1
        );
        f = Avx512F.Add(Avx512F.Multiply(b01, SixtF512), Half01F512);
        idx = Avx512F.Min(
            Avx512F.Max(Avx512F.ConvertToVector512Int32WithTruncation(f), ZeroI512),
            MaxPqIdxI512
        );
        if (_fastConvert)
            idx = FastLutIdx512(idx);
        Vector512<float> bp = Avx512F.InsertVector256(
            Avx512F.InsertVector256(
                default,
                Avx2.GatherVector256(pqLut, Avx512F.ExtractVector256(idx, 0), 4),
                0
            ),
            Avx2.GatherVector256(pqLut, Avx512F.ExtractVector256(idx, 1), 4),
            1
        );

        // ---- BT.2020 luma in the PQ domain -> 10-bit code ----
        Vector512<float> yp = Avx512F.Add(
            Avx512F.Add(Avx512F.Multiply(rp, YrF512), Avx512F.Multiply(gp, YgF512)),
            Avx512F.Multiply(bp, YbF512)
        );
        Vector512<float> yvF = Avx512F.Add(Avx512F.Multiply(yp, YScaleF512), Half01F512);
        Vector512<int> yv = Avx512F.Min(
            Avx512F.Max(Avx512F.ConvertToVector512Int32WithTruncation(yvF), ZeroI512),
            MaxY10I512
        );

        // ---- clli stats (vectorized 16-lane butterfly reduce; bit-exact, int min/max/
        // sum are order-free). Lane extraction: store the low 128-bit half and read lane 0.
        Vector512<int> mn = HMin16(yv);
        Vector512<int> mx = HMax16(yv);
        Vector512<int> su = HSum16(yv);
        int* st = stackalloc int[4];
        Sse2.Store(st, Avx.ExtractVector128(Avx512F.ExtractVector256(mn, 0), 0));
        int cMin = st[0];
        Sse2.Store(st, Avx.ExtractVector128(Avx512F.ExtractVector256(mx, 0), 0));
        int cMax = st[0];
        Sse2.Store(st, Avx.ExtractVector128(Avx512F.ExtractVector256(su, 0), 0));
        long cSum = st[0];
        if (cMin < min)
            min = cMin;
        if (cMax > max)
            max = cMax;
        sum += cSum;

        // ---- write Y (16 x 16-bit = two 16-byte stores) ----
        // 256-bit halves via the proven 256-bit pack idiom (codes are 0..1023, so the
        // signed-pack bits == u16 bits).
        ushort* yd = (ushort*)Unsafe.AsPointer(ref _y16![0]) + (yOff + x0);
        Vector256<int> yLo = Avx512F.ExtractVector256(yv, 0);
        Vector256<int> yHi = Avx512F.ExtractVector256(yv, 1);
        Vector128<short> yLoP = Sse2.PackSignedSaturate(
            Avx.ExtractVector128(yLo, 0),
            Avx.ExtractVector128(yLo, 1)
        );
        Vector128<short> yHiP = Sse2.PackSignedSaturate(
            Avx.ExtractVector128(yHi, 0),
            Avx.ExtractVector128(yHi, 1)
        );
        Sse2.Store(yd, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref yLoP));
        Sse2.Store(yd + 8, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref yHiP));

        if (evenRow)
        {
            // ---- 2x2 horizontal chroma (PQ domain, neutral 512), eight (x, x+1) pairs ----
            // divide (exact, bit-match with the scalar) or reciprocal multiply (fast mode)
            Vector512<float> u01 = Chroma01_512(
                Avx512F.Subtract(bp, yp),
                _fastConvert ? UInvF512 : UDivF512,
                _fastConvert
            );
            Vector512<float> v01 = Chroma01_512(
                Avx512F.Subtract(rp, yp),
                _fastConvert ? VInvF512 : VDivF512,
                _fastConvert
            );

            // 16 px -> 8 chroma codes: adjacent pair sums (a pair never crosses a
            // 128-bit lane boundary), the 8 unique sums gathered into lanes 0-7, x0.5.
            // 10-bit codes: SIMD mirror of Lin10 (v*1023+0.5, clamped [0,1023]).
            Vector512<float> avgU = Avx512F.Multiply(
                ToF512(Avx512F.PermuteVar16x32(ToI512(PairSums16(u01)), CtlEvenGather16)),
                Half01F512
            );
            Vector512<float> f10 = Avx512F.Add(Avx512F.Multiply(avgU, YScaleF512), Half01F512);
            Vector512<int> uC = Avx512F.Min(
                Avx512F.Max(Avx512F.ConvertToVector512Int32WithTruncation(f10), ZeroI512),
                MaxY10I512
            );
            Vector512<float> avgV = Avx512F.Multiply(
                ToF512(Avx512F.PermuteVar16x32(ToI512(PairSums16(v01)), CtlEvenGather16)),
                Half01F512
            );
            f10 = Avx512F.Add(Avx512F.Multiply(avgV, YScaleF512), Half01F512);
            Vector512<int> vC = Avx512F.Min(
                Avx512F.Max(Avx512F.ConvertToVector512Int32WithTruncation(f10), ZeroI512),
                MaxY10I512
            );

            // 8 x 16-bit chroma codes, one 16-byte store each (the low 256-bit half of
            // the code vector holds all 8).
            ushort* ud = (ushort*)Unsafe.AsPointer(ref _u16![0]) + (cbBase + x0 / 2);
            ushort* vd = (ushort*)Unsafe.AsPointer(ref _v16![0]) + (cbBase + x0 / 2);
            Vector256<int> uLo = Avx512F.ExtractVector256(uC, 0);
            Vector256<int> vLo = Avx512F.ExtractVector256(vC, 0);
            Vector128<short> uP = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(uLo, 0),
                Avx.ExtractVector128(uLo, 1)
            );
            Vector128<short> vP = Sse2.PackSignedSaturate(
                Avx.ExtractVector128(vLo, 0),
                Avx.ExtractVector128(vLo, 1)
            );
            Sse2.Store(ud, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref uP));
            Sse2.Store(vd, Unsafe.As<Vector128<short>, Vector128<ushort>>(ref vP));
        }
    }

    /// <summary>半浮点位型 → float 查表 (65536 项, 含 NaN/±inf 语义)。</summary>
    private static readonly float[] HalfToFloat = BuildHalfToFloat();

    private static float[] BuildHalfToFloat()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            lut[i] = HalfBitsToFloat((ushort)i);
        }
        return lut;
    }

    /// <summary>
    /// Subnormal/zero slice of <see cref="HalfToFloat"/> for the SIMD fast path: 2048
    /// entries (8 KB, L1-resident) instead of the full 256 KB LUT. Entries 0-1023 are
    /// HalfToFloat[0x0000..0x03FF] (+0 and the positive subnormals), entries 1024-2047
    /// are HalfToFloat[0x8000..0x83FF] (-0 and the negative subnormals), so the SIMD
    /// index (h &amp; 0x3FF) | ((h &amp; 0x8000) &gt;&gt; 5) maps straight onto it. Bit-exact by
    /// construction (same float bits as the scalar LUT).
    /// </summary>
    private static readonly float[] SubnormalLut = BuildSubnormalLut();

    private static float[] BuildSubnormalLut()
    {
        var lut = new float[2048];
        for (int i = 0; i < 1024; i++)
        {
            lut[i] = HalfToFloat[i]; // 0x0000..0x03FF
        }
        for (int i = 0; i < 1024; i++)
        {
            lut[1024 + i] = HalfToFloat[0x8000 + i]; // 0x8000..0x83FF
        }
        return lut;
    }

    /// <summary>
    /// IEEE 754 binary16 → binary32 纯位操作。
    /// (本工具链的 System.Half 没有位级构造器/ToSingle; 纯位操作也可避免
    /// 每通道方法调用, 已用 .NET 自身 float→Half 转换全量比对验证)
    /// </summary>
    private static float HalfBitsToFloat(ushort h)
    {
        int s = (h >> 15) & 1;
        int e = (h >> 10) & 0x1F;
        int m = h & 0x3FF;
        int bits;
        if (e == 0)
        {
            if (m == 0)
            {
                bits = s << 31; // ±0
            }
            else
            {
                // 亚正规数: 左移归一化到正规区
                e = 1;
                while ((m & 0x400) == 0)
                {
                    m <<= 1;
                    e++;
                }
                m &= 0x3FF;
                bits = (s << 31) | ((114 - e) << 23) | (m << 13);
            }
        }
        else if (e == 0x1F)
        {
            bits = (s << 31) | (0xFF << 23) | (m << 13); // ±inf / NaN
        }
        else
        {
            bits = (s << 31) | ((e + 112) << 23) | (m << 13);
        }
        return BitConverter.Int32BitsToSingle(bits);
    }

    /// <summary>R16G16B16A16Float 像素: 2 字节/通道 IEEE 754 半浮点 (小端)。</summary>
    private static float ReadHalf(byte[] src, int offset)
    {
        ushort bits = (ushort)(src[offset] | (src[offset + 1] << 8));
        return HalfToFloat[bits];
    }

    /// <summary>
    /// 256-bit half to float (8 lanes): <c>halves</c> holds the 16-bit half bits in the
    /// low 16 bits of each lane (high 16 zero, from the And(Lo16I) unpack). Bit-exact
    /// with <see cref="HalfToFloat"/> over all 65536 inputs (verified: 0 mismatches):
    /// an integer fast path for the normal / inf / NaN / zero cases (mirroring
    /// <see cref="HalfBitsToFloat"/>, including the NaN payload and subnormal
    /// normalization) plus one gather into the 2048-entry <see cref="SubnormalLut"/>
    /// only when the exponent field is 0. Replaces 3 x 256 KB half-LUT gathers per
    /// 8-px group with 1 x 8 KB L1-resident gather.
    /// </summary>
    private static unsafe Vector256<float> HalfToFloatSimd256(Vector256<int> halves)
    {
        // sbit31 = (h & 0x8000) << 16
        Vector256<int> sbit31 = Avx2.ShiftLeftLogical(Avx2.And(halves, Set1I(0x8000)), 16);
        // expNorm = ((h & 0x7C00) << 13) + 0x38000000 == (e + 112) << 23 for normal e
        // (e + 112 < 256 always, so the single 32-bit add is exact).
        Vector256<int> expNorm = Avx2.Add(
            Avx2.ShiftLeftLogical(Avx2.And(halves, Set1I(0x7C00)), 13),
            Set1I(0x38000000)
        );
        // mPart = (h & 0x3FF) << 13
        Vector256<int> mPart = Avx2.ShiftLeftLogical(Avx2.And(halves, Set1I(0x3FF)), 13);
        // e5 = (h >> 10) & 0x1F (the 5-bit exponent field)
        Vector256<int> e5 = Avx2.And(Avx2.ShiftRightLogical(halves, 10), Set1I(0x1F));
        Vector256<int> isInfNan = Avx2.CompareEqual(e5, Set1I(0x1F));
        Vector256<int> isSub = Avx2.CompareEqual(e5, ZeroI);
        // exp = isInfNan ? 0x7F800000 : expNorm (inf/NaN, payload-preserving).
        // NOTE: .NET AndNot(a, b) = ~a & b (first arg inverted); pinned in the probe.
        Vector256<int> exp = Avx2.Or(
            Avx2.AndNot(isInfNan, expNorm),
            Avx2.And(isInfNan, Set1I(0x7F800000))
        );
        Vector256<int> fastBits = Avx2.Or(Avx2.Or(sbit31, exp), mPart);
        // subIdx = (h & 0x3FF) | ((h & 0x8000) >> 5) -> [0..1023] positive, [1024..2047]
        // negative (the SubnormalLut layout).
        Vector256<int> subIdx = Avx2.Or(
            Avx2.And(halves, Set1I(0x3FF)),
            Avx2.ShiftRightLogical(Avx2.And(halves, Set1I(0x8000)), 5)
        );
        float* subLut = (float*)Unsafe.AsPointer(ref SubnormalLut[0]);
        Vector256<float> subFloat = Avx2.GatherVector256(subLut, subIdx, 4);
        Vector256<int> resBits = Avx2.Or(
            Avx2.AndNot(isSub, fastBits),
            Avx2.And(isSub, ToI(subFloat))
        );
        return ToF(resBits);
    }

    /// <summary>
    /// 512-bit half to float (16 lanes): same bit-exact fast path, with 2 x 256-bit
    /// subnormal gathers (the .NET 10 projection has no 512-bit gather; Zen/Intel
    /// upconvert a 512-bit gather to two 256-bit ones anyway).
    /// </summary>
    private static unsafe Vector512<float> HalfToFloatSimd512(Vector512<int> halves)
    {
        Vector512<int> sbit31 = Avx512F.ShiftLeftLogical(Avx512F.And(halves, Set1I512(0x8000)), 16);
        Vector512<int> expNorm = Avx512F.Add(
            Avx512F.ShiftLeftLogical(Avx512F.And(halves, Set1I512(0x7C00)), 13),
            Set1I512(0x38000000)
        );
        Vector512<int> mPart = Avx512F.ShiftLeftLogical(Avx512F.And(halves, Set1I512(0x3FF)), 13);
        Vector512<int> e5 = Avx512F.And(Avx512F.ShiftRightLogical(halves, 10), Set1I512(0x1F));
        Vector512<int> isInfNan = Avx512F.CompareEqual(e5, Set1I512(0x1F));
        Vector512<int> isSub = Avx512F.CompareEqual(e5, ZeroI512);
        Vector512<int> exp = Avx512F.Or(
            Avx512F.AndNot(isInfNan, expNorm),
            Avx512F.And(isInfNan, Set1I512(0x7F800000))
        );
        Vector512<int> fastBits = Avx512F.Or(Avx512F.Or(sbit31, exp), mPart);
        Vector512<int> subIdx = Avx512F.Or(
            Avx512F.And(halves, Set1I512(0x3FF)),
            Avx512F.ShiftRightLogical(Avx512F.And(halves, Set1I512(0x8000)), 5)
        );
        float* subLut = (float*)Unsafe.AsPointer(ref SubnormalLut[0]);
        Vector256<int> loIdx = Avx512F.ExtractVector256(subIdx, 0);
        Vector256<int> hiIdx = Avx512F.ExtractVector256(subIdx, 1);
        Vector256<float> loF = Avx2.GatherVector256(subLut, loIdx, 4);
        Vector256<float> hiF = Avx2.GatherVector256(subLut, hiIdx, 4);
        Vector512<float> subFloat = Avx512F.InsertVector256(
            Avx512F.InsertVector256(default, loF, 0),
            hiF,
            1
        );
        Vector512<int> resBits = Avx512F.Or(
            Avx512F.AndNot(isSub, fastBits),
            Avx512F.And(isSub, ToI512(subFloat))
        );
        return ToF512(resBits);
    }

    /// <summary>
    /// WGC 场景参考半浮点 (1.0 = 80 nits) → 真实 BT.2020 线性 RGB (0..1, 1.0 = 10000 nits)。
    /// WGC 的 HDR 空间是 709 亮度行的线性 RGB (80 nits/unit, 2020 色域外为负通道),
    /// 先经实测矩阵 WgcTo2020Ainv 换到 2020 空间, 再缩放并钳位 (负值=数值噪声, &gt;1=EDR 封顶)。
    /// NaN → 0, ±inf 由钳位处理。
    /// </summary>
    private static void WgcTo2020Rgb01(
        float fr,
        float fg,
        float fb,
        out float r01,
        out float g01,
        out float b01
    )
    {
        if (float.IsNaN(fr) || float.IsNaN(fg) || float.IsNaN(fb))
        {
            r01 = g01 = b01 = 0f;
            return;
        }
        r01 = Math.Clamp(
            (WgcTo2020Ainv[0] * fr + WgcTo2020Ainv[1] * fg + WgcTo2020Ainv[2] * fb) * UnitPerNits,
            0f,
            1f
        );
        g01 = Math.Clamp(
            (WgcTo2020Ainv[3] * fr + WgcTo2020Ainv[4] * fg + WgcTo2020Ainv[5] * fb) * UnitPerNits,
            0f,
            1f
        );
        b01 = Math.Clamp(
            (WgcTo2020Ainv[6] * fr + WgcTo2020Ainv[7] * fg + WgcTo2020Ainv[8] * fb) * UnitPerNits,
            0f,
            1f
        );
    }

    /// <summary>
    /// 0..1 linear-light value (1.0 = 10000 nits) -> PQ-domain 0..1 (the coded 0..1
    /// value). Exact mode: the 256 KB PqLut (bit-exact with the SIMD kernels). Fast
    /// mode: remap the exact 65535-scaled index onto the 80 KB hybrid LUT (see
    /// <see cref="PqLutFastF"/> / <see cref="FastLutIdx"/> - same remap as the SIMD
    /// kernels, so a fast-mode frame is one consistent mode).
    /// </summary>
    private float Pq01(float v01)
    {
        int i = PqIndex(v01);
        if (_fastConvert)
        {
            i = i < 4096 ? i : 4096 + ((i - 4096 + 2) >> 2);
            return _pqLutF[i];
        }
        return PqLut[i] / 1023.0f;
    }

    /// <summary>Clamp a 0..1 value (coded luma/chroma) to [0,1].</summary>
    private static float Clamp01(float v)
    {
        if (v < 0f)
            return 0f;
        if (v > 1f)
            return 1f;
        return v;
    }

    /// <summary>0..1 线性值 → PQ LUT 下标 (0..65535, 1.0 = 10000 nits), 就近取格。</summary>
    private static int PqIndex(float v)
    {
        int i = (int)(v * 65535.0f + 0.5f);
        if (i < 0)
            return 0;
        if (i > 65535)
            return 65535;
        return i;
    }

    /// <summary>0..1 coded value (luma Y' or chroma Cb'/Cr') to 10-bit full-range code (0..1023; chroma neutral 512).</summary>
    private static ushort Lin10(float v)
    {
        int i = (int)(v * 1023.0f + 0.5f);
        if (i < 0)
            return 0;
        if (i > 1023)
            return 1023;
        return (ushort)i;
    }

    private static int ClampCbCr8(int v)
    {
        return v < 16 ? 16
            : v > 240 ? 240
            : v;
    }

    private static void WriteU16(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)(value & 0xFF);
        dst[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// PQ OETF (SMPTE ST 2084) LUT: 输入 0..1 线性值 (i/65535, 1.0 = 10000 nits),
    /// 输出 10-bit HDR10 值 (0..1023)。
    /// </summary>
    private static ushort[] BuildPqLut()
    {
        // SMPTE ST 2084 PQ OETF 常数 (与 FFmpeg libavutil/csp.c 的 trc_smpte_st2084 一致,
        // 即标准 OETF = 逆 EOTF): OETF(Y) = ((C1 + C2*Y^M1) / (1 + C3*Y^M1))^M2, 其中 Y = nits/10000。
        // (旧版误用未缩放常数 261032.38/2523.40/253.01/12.93 且公式结构错误, 导致 LUT 恒为 1023;
        //  旧版还按 WGC 半浮点码值索引, 现改为 0..1 线性值直接索引, 与 ConvertHdr 配套。)
        const double C1 = 3424.0 / 4096.0; // 0.8359375 (= C3 - C2 + 1)
        const double C2 = 32.0 * 2413.0 / 4096.0; // 18.8515625
        const double C3 = 32.0 * 2392.0 / 4096.0; // 18.6875
        const double M1 = 0.25 * 2610.0 / 4096.0; // 0.1593017578125
        const double M2 = 128.0 * 2523.0 / 4096.0; // 78.84375

        var lut = new ushort[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            // i/65535 = 0..1 线性值 (1.0 = 10000 nits), OETF 得 0..1 码值, ×1023 得 10-bit
            double y = i / 65535.0;
            double pq;
            if (y <= 0)
            {
                pq = 0.0;
            }
            else
            {
                double y1 = Math.Pow(y, M1);
                pq = Math.Pow((C1 + C2 * y1) / (1.0 + C3 * y1), M2);
                if (pq < 0)
                    pq = 0;
                else if (pq > 1)
                    pq = 1;
            }
            lut[i] = (ushort)Math.Round(pq * 1023);
        }
        return lut;
    }

    /// <summary>SDR 平面指针 + 字节 stride (x264)。</summary>
    public (byte[] Y, int StrideY, byte[] UV, int StrideUV) SdrPlanes()
    {
        return (_y!, OutWidth, _uv!, OutWidth);
    }

    /// <summary>HDR 平面指针 + 字节 stride (x265 P010: Y 2B/px, U/V 半宽 2B/px)。</summary>
    public (byte[] Y, int StrideY, byte[] U, int StrideU, byte[] V, int StrideV) HdrPlanes()
    {
        int halfW = OutWidth / 2;
        return (_y16!, OutWidth * 2, _u16!, halfW * 2, _v16!, halfW * 2);
    }
}
