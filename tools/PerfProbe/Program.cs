// PerfProbe: measures each stage of the Starshot recording pipeline in isolation,
// to explain why the achieved recording frame rate is very low (see performance.md).
//
// Usage:  dotnet run --project tools/PerfProbe [--quick]
//
// Measured stages (mirroring VideoRecordService.OnFrame exactly):
//   1. readback  - the Win2D GetPixelBytes GPU->CPU copy of the whole capture surface;
//                  cannot be isolated here, so a same-size memcpy is reported as a floor
//   2. convert   - FrameProcessor.ConvertHdr / ConvertSdr (row-chunk parallel, mirror of
//                  the app's loops + LUTs; the HDR clli Y stats are accumulated in the same
//                  pass, so there is no separate clli stage anymore)
//   3. encode    - x265 (HDR P010) / x264 (SDR NV12) through Starshot.Codecs.dll with
//                  the exact VideoEncoder settings (preset/params/threads)
//
// Also: --verify runs a deterministic chroma-row alignment check of the parallel
// conversion (synthetic stripe pattern; no codecs needed) - see VerifyConversions.
// Also: --ab compares x265 knob variants (the L2 batch: frame-thread shapes, ctu/dyn4o/
// rdoq, AVX-512 dispatch, ref=2) on 4K noise + flat, plus the L6 rc-lookahead file-size
// A/B, to size the remaining encode-side headroom.
//
// Content: "flat" (easiest content - like the HDR suite's flat fields, the same frame
// fed repeatedly) and "noise" (8 pre-generated random frames cycled, the worst case
// for the encoder; real UI/video content sits between the two).

using System.Diagnostics;
using System.Runtime.InteropServices;

static class Program
{
    // ==================== P/Invoke (Starshot.Codecs.dll shim, same layout as CodecsNative) ====================

    [DllImport(
        "Starshot.Codecs.dll",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi
    )]
    static extern int ss_init(string x264Path, string x265Path, string faacPath);

    // x265
    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr ss_x265_param_create();

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x265_param_free(IntPtr h);

    [DllImport(
        "Starshot.Codecs.dll",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi
    )]
    static extern int ss_x265_preset(IntPtr h, string? preset, string? tune);

    [DllImport(
        "Starshot.Codecs.dll",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi
    )]
    static extern int ss_x265_parse(IntPtr h, string name, string value);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x265_set_geometry(IntPtr h, int w, int hgt, int fpsNum, int fpsDen);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x265_set_hdr(IntPtr h, int depth, int cp, int tf, int mx);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x265_set_crf(IntPtr h, double crf, int keyint, int bframes, int threads);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr ss_x265_open(IntPtr param);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ss_x265_encode(
        IntPtr h,
        IntPtr y,
        int strideY,
        IntPtr u,
        int strideU,
        IntPtr v,
        int strideV,
        long pts,
        IntPtr buf,
        int cap,
        out int bytes
    );

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x265_close(IntPtr h);

    // x264
    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr ss_x264_param_create();

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x264_param_free(IntPtr h);

    [DllImport(
        "Starshot.Codecs.dll",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi
    )]
    static extern int ss_x264_preset(IntPtr h, string? preset, string? tune);

    [DllImport(
        "Starshot.Codecs.dll",
        CallingConvention = CallingConvention.Cdecl,
        CharSet = CharSet.Ansi
    )]
    static extern int ss_x264_parse(IntPtr h, string name, string value);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x264_set_geometry(IntPtr h, int w, int hgt, int fpsNum, int fpsDen);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x264_set_color(IntPtr h, int cp, int tf, int mx, int fr);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x264_set_crf(IntPtr h, double crf, int keyint, int bframes, int threads);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr ss_x264_open(IntPtr param);

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int ss_x264_encode(
        IntPtr h,
        IntPtr y,
        int strideY,
        IntPtr uv,
        int strideUV,
        long pts,
        IntPtr buf,
        int cap,
        out int bytes
    );

    [DllImport("Starshot.Codecs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void ss_x264_close(IntPtr h);

    // ---- L7 (boost-state micro-tune): native-thread CCD pinning.
    //      The PERFBOOST power request was dropped: this OS build (26100) no longer exports
    //      the classic powrprof power-request API, and the machine already runs the
    //      Ultimate Performance scheme, where a boost request would be a no-op anyway.
    //      The 88-150 ms frame-time wander is thermal/power-limit, which no P-state API beats.
    //      RESULT (A/B below): pinning is REJECTED (slower on both CCDs - 32 x265 threads on
    //      16 LPs is 2x-oversubscribed; the scheduler's free placement wins), so the app
    //      ships no L7 lever. The harness stays as the rejection's data archive (like the
    //      historical L1 ref=2 row in the knob A/B).

    // Win32 thread IDs of every thread in the process (snapshot to diff against later -
    // x265 spawns its frame/pool threads natively at encoder open, invisible to managed).
    static HashSet<int> SnapshotThreads()
    {
        using var proc = Process.GetCurrentProcess();
        var set = new HashSet<int>(proc.Threads.Count);
        for (int i = 0; i < proc.Threads.Count; i++)
        {
            int id = proc.Threads[i].Id;
            if (id != 0)
                set.Add(id);
        }
        return set;
    }

    // Pin every thread NOT in `before` (the native encoder threads) to `mask`.
    static int PinNewThreads(HashSet<int> before, nint mask)
    {
        if (mask == 0)
            return -1;
        int pinned = 0;
        using var proc = Process.GetCurrentProcess();
        for (int i = 0; i < proc.Threads.Count; i++)
        {
            var t = proc.Threads[i];
            if (t.Id == 0 || before.Contains(t.Id))
                continue;
            try
            {
                t.ProcessorAffinity = new IntPtr(mask);
                pinned++;
            }
            catch
            {
                // thread exited between enumeration and set - fine
            }
        }
        return pinned;
    }

    // ==================== FrameProcessor replica (verbatim inner loops + LUTs) ====================

    static readonly float[] HalfToFloat = BuildHalfToFloat();

    static float[] BuildHalfToFloat()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
            lut[i] = HalfBitsToFloat((ushort)i);
        return lut;
    }

    static float HalfBitsToFloat(ushort h)
    {
        int s = (h >> 15) & 1;
        int e = (h >> 10) & 0x1F;
        int m = h & 0x3FF;
        int bits;
        if (e == 0)
        {
            if (m == 0)
                bits = s << 31;
            else
            {
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
            bits = (s << 31) | (0xFF << 23) | (m << 13);
        else
            bits = (s << 31) | ((e + 112) << 23) | (m << 13);
        return BitConverter.Int32BitsToSingle(bits);
    }

    static readonly ushort[] PqLut = BuildPqLut();

    static ushort[] BuildPqLut()
    {
        const double C1 = 3424.0 / 4096.0;
        const double C2 = 32.0 * 2413.0 / 4096.0;
        const double C3 = 32.0 * 2392.0 / 4096.0;
        const double M1 = 0.25 * 2610.0 / 4096.0;
        const double M2 = 128.0 * 2523.0 / 4096.0;
        var lut = new ushort[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            double y = i / 65535.0;
            double pq;
            if (y <= 0)
                pq = 0.0;
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

    // The app's WGC -> BT.2020 matrix (FrameProcessor.WgcTo2020Ainv)
    static readonly float[] WgcTo2020Ainv =
    {
        0.627600968f,
        0.329570085f,
        0.043333914f,
        0.069145925f,
        0.920288682f,
        0.011371733f,
        0.016403098f,
        0.088126212f,
        0.896180391f,
    };

    const float UnitPerNits = 80.0f / 10000.0f;
    const float Yr = 0.2627f;
    const float Yg = 0.678f;
    const float Yb = 0.0593f;
    const float UDiv = 2.0f * (1.0f - Yb);
    const float VDiv = 2.0f * (1.0f - Yr);

    static float Pq01(float v01) => PqLut[PqIndex(v01)] / 1023.0f;

    static int PqIndex(float v)
    {
        int i = (int)(v * 65535.0f + 0.5f);
        if (i < 0)
            return 0;
        if (i > 65535)
            return 65535;
        return i;
    }

    static float Clamp01(float v)
    {
        if (v < 0f)
            return 0f;
        if (v > 1f)
            return 1f;
        return v;
    }

    static ushort Lin10(float v)
    {
        int i = (int)(v * 1023.0f + 0.5f);
        if (i < 0)
            return 0;
        if (i > 1023)
            return 1023;
        return (ushort)i;
    }

    static void WgcTo2020Rgb01(
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

    static float ReadHalf(byte[] src, int offset)
    {
        ushort bits = (ushort)(src[offset] | (src[offset + 1] << 8));
        return HalfToFloat[bits];
    }

    static void WriteU16(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)(value & 0xFF);
        dst[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    // Mirror of FrameProcessor's parallelization: rows below this pixel count stay serial.
    const int ParallelMinPixels = 1 << 20;

    static int WorkerCount(int h, int pixels) =>
        pixels < ParallelMinPixels ? 1 : Math.Min(h, Environment.ProcessorCount);

    // Mirror of FrameProcessor.ConvertHdr (row-chunk parallel; clli Y min/max/sum accumulated
    // by the same pass, reduced across chunks - the separate second-pass scan is gone).
    static void ConvertHdr(
        byte[] src,
        int srcWidth,
        int srcHeight,
        byte[] y,
        byte[] u,
        byte[] v,
        int w,
        int h
    )
    {
        int min = 1023;
        int max = 0;
        long sum = 0;
        int workers = WorkerCount(h, w * h);
        if (workers == 1)
        {
            ConvertHdrRows(src, srcWidth, 0, h, w, y, u, v, ref min, ref max, ref sum);
        }
        else
        {
            int[] mins = new int[workers];
            int[] maxs = new int[workers];
            long[] sums = new long[workers];
            Task[] tasks = new Task[workers];
            for (int i = 0; i < workers; i++)
            {
                // loop variable i must not be captured by the lambda (the local .NET SDKs capture
                // it shared across iterations - pre-C#5 semantics); fresh locals are per-iteration
                int idx = i;
                int r0 = idx * h / workers;
                int r1 = (idx + 1) * h / workers;
                mins[idx] = 1023;
                tasks[idx] = Task.Run(() =>
                    ConvertHdrRows(
                        src,
                        srcWidth,
                        r0,
                        r1,
                        w,
                        y,
                        u,
                        v,
                        ref mins[idx],
                        ref maxs[idx],
                        ref sums[idx]
                    )
                );
            }
            Task.WaitAll(tasks);
            for (int i = 0; i < workers; i++)
            {
                min = Math.Min(min, mins[i]);
                max = Math.Max(max, maxs[i]);
                sum += sums[i];
            }
        }
        // sink the stats so the accumulation cannot be optimized away (like the app's clli use)
        _clliMin = min;
        _clliMax = max;
        _clliSum += (int)sum;
    }

    // Mirror of FrameProcessor.ConvertHdrRows (crop at 0,0; the app's crop math adds no
    // per-pixel cost, only the row offset).
    static void ConvertHdrRows(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        int w,
        byte[] y,
        byte[] u,
        byte[] v,
        ref int min,
        ref int max,
        ref long sum
    )
    {
        int halfW = w / 2;
        // Cb/Cr block row of even row r is r/2 (same as the serial whole-frame version);
        // odd rowBegin needs (rowBegin+1)/2 so the chunk's chroma rows line up.
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = row * srcWidth;
            int yOff = row * w;
            bool evenRow = (row & 1) == 0;
            int cbBase = evenRow ? cbRow * halfW : 0;

            for (int x = 0; x < w; x++)
            {
                int si = (srcRow + x) * 8;
                WgcTo2020Rgb01(
                    ReadHalf(src, si),
                    ReadHalf(src, si + 2),
                    ReadHalf(src, si + 4),
                    out float r01,
                    out float g01,
                    out float b01
                );
                float rp = Pq01(r01);
                float gp = Pq01(g01);
                float bp = Pq01(b01);
                float yp = Yr * rp + Yg * gp + Yb * bp;
                int yv = Lin10(yp);
                WriteU16(y, (yOff + x) * 2, yv);
                if (yv < min)
                    min = yv;
                if (yv > max)
                    max = yv;
                sum += yv;

                if (evenRow && (x & 1) == 0)
                {
                    float u01 = Clamp01(0.5f + (bp - yp) / UDiv);
                    float v01 = Clamp01(0.5f + (rp - yp) / VDiv);
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
                    float u2 = Clamp01(0.5f + (bp2 - yp2) / UDiv);
                    float v2 = Clamp01(0.5f + (rp2 - yp2) / VDiv);
                    WriteU16(u, (cbBase + x / 2) * 2, Lin10((u01 + u2) * 0.5f));
                    WriteU16(v, (cbBase + x / 2) * 2, Lin10((v01 + v2) * 0.5f));
                }
            }
            if (evenRow)
                cbRow++;
        }
    }

    // Mirror of FrameProcessor.ConvertSdr (row-chunk parallel).
    static void ConvertSdr(
        byte[] src,
        int srcWidth,
        int srcHeight,
        byte[] y,
        byte[] uv,
        int w,
        int h
    )
    {
        int workers = WorkerCount(h, w * h);
        if (workers == 1)
        {
            ConvertSdrRows(src, srcWidth, 0, h, w, y, uv);
            return;
        }
        Task[] tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
        {
            // loop variable i must not be captured by the lambda (see ConvertHdr)
            int idx = i;
            int r0 = idx * h / workers;
            int r1 = (idx + 1) * h / workers;
            tasks[idx] = Task.Run(() => ConvertSdrRows(src, srcWidth, r0, r1, w, y, uv));
        }
        Task.WaitAll(tasks);
    }

    static void ConvertSdrRows(
        byte[] src,
        int srcWidth,
        int rowBegin,
        int rowEnd,
        int w,
        byte[] y,
        byte[] uv
    )
    {
        // Cb/Cr block row of even row r is r/2 (same as the serial whole-frame version);
        // odd rowBegin needs (rowBegin+1)/2 so the chunk's chroma rows line up.
        int cbRow = (rowBegin + (rowBegin & 1)) / 2;
        for (int row = rowBegin; row < rowEnd; row++)
        {
            int srcRow = row * srcWidth;
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
                cbRow++;
        }
    }

    static int ClampCbCr8(int v) =>
        v < 16 ? 16
        : v > 240 ? 240
        : v;

    // clli Y stats, now accumulated inside ConvertHdr (the separate second-pass scan in
    // VideoRecordService.OnFrame was folded into the conversion). Kept live so the
    // accumulation cannot be optimized away.
    static int _clliMin = 1,
        _clliMax,
        _clliSum;

    // ==================== encoder helpers (exact VideoEncoder settings) ====================

    static IntPtr X265Open(int w, int h, double crf, int threads)
    {
        IntPtr p = ss_x265_param_create();
        Check(ss_x265_preset(p, "faster", null), "x265 preset");
        Check(ss_x265_parse(p, "annexb", "0"), "x265 annexb=0");
        Check(ss_x265_parse(p, "range", "full"), "x265 range=full");
        Check(ss_x265_parse(p, "rc-lookahead", "0"), "x265 rc-lookahead=0");
        ss_x265_set_geometry(p, w, h, 60, 1);
        ss_x265_set_hdr(p, 10, 9, 16, 9);
        ss_x265_set_crf(p, crf, 120, 0, threads);
        IntPtr e = ss_x265_open(p);
        if (e == IntPtr.Zero)
            throw new Exception("x265 open failed");
        return e;
    }

    static IntPtr X264Open(int w, int h, double crf, int threads)
    {
        IntPtr p = ss_x264_param_create();
        Check(ss_x264_preset(p, "veryfast", "film"), "x264 preset");
        Check(ss_x264_parse(p, "annexb", "0"), "x264 annexb=0");
        Check(ss_x264_parse(p, "rc-lookahead", "0"), "x264 rc-lookahead=0");
        ss_x264_set_geometry(p, w, h, 60, 1);
        ss_x264_set_color(p, 1, 1, 1, 0);
        ss_x264_set_crf(p, crf, 120, 0, threads);
        IntPtr e = ss_x264_open(p);
        if (e == IntPtr.Zero)
            throw new Exception("x264 open failed");
        return e;
    }

    // ==================== --ab: x265 knob A/B (4K noise + flat) ====================

    static IntPtr X265OpenVariant(
        int w,
        int h,
        double crf,
        int threads,
        string preset,
        (string Key, string Value)?[]? extra
    )
    {
        IntPtr p = ss_x265_param_create();
        Check(ss_x265_preset(p, preset, null), "x265 preset");
        Check(ss_x265_parse(p, "annexb", "0"), "x265 annexb=0");
        Check(ss_x265_parse(p, "range", "full"), "x265 range=full");
        Check(ss_x265_parse(p, "rc-lookahead", "0"), "x265 rc-lookahead=0");
        if (extra is not null)
        {
            foreach (var (k, v) in extra.Select(e => e!.Value))
                Check(ss_x265_parse(p, k, v), $"x265 {k}={v}");
        }
        ss_x265_set_geometry(p, w, h, 60, 1);
        ss_x265_set_hdr(p, 10, 9, 16, 9);
        ss_x265_set_crf(p, crf, 120, 0, threads);
        IntPtr e = ss_x265_open(p);
        if (e == IntPtr.Zero)
            throw new Exception("x265 open failed");
        return e;
    }

    static void RunX265Ab(int iters, int warmup)
    {
        const int w = 3840,
            h = 2160;
        int px = w * h;
        byte[] y = new byte[px * 2],
            u = new byte[px / 2],
            v = new byte[px / 2];
        FillP010(y, u, v, w, h, null, noise: false);
        var rng = new Random(1);
        (byte[] y, byte[] u, byte[] v)[] noise = new (byte[], byte[], byte[])[4];
        for (int i = 0; i < 4; i++)
        {
            var n = (y: new byte[px * 2], u: new byte[px / 2], v: new byte[px / 2]);
            FillP010(n.y, n.u, n.v, w, h, rng, noise: true);
            noise[i] = n;
        }
        byte[] outBuf = new byte[16 << 20];

        // L2 knob batch (the "Remaining CPU levers" analysis in performance.md): the app's
        // ultrafast baseline, frame-thread shapes (the local x265 build caps at 32 -
        // X265_MAX_FRAME_THREADS patched from the stock 16), the screen-content CTA-size /
        // dyn4o / rdoq knobs, the AVX-512 runtime dispatch (asm=avx512; no rebuild), and
        // the ref=2 cost the repeat-frame (L1) feature would pay.
        var variants = new (string Name, string Preset, int Threads, (string, string)?[]? Extra)[]
        {
            ("ultrafast t16 (app)", "ultrafast", 16, null),
            ("ultrafast t8", "ultrafast", 8, null),
            ("ultrafast t24", "ultrafast", 24, null),
            ("ultrafast t32", "ultrafast", 32, null),
            ("ultrafast t16 +ctu32", "ultrafast", 16, new (string, string)?[] { ("ctu", "32") }),
            ("ultrafast t16 +dyn4o", "ultrafast", 16, new (string, string)?[] { ("dyn4o", "1") }),
            ("ultrafast t16 +rdoq0", "ultrafast", 16, new (string, string)?[] { ("rdoq", "0") }),
            (
                "ultrafast t16 +avx512",
                "ultrafast",
                16,
                new (string, string)?[] { ("asm", "avx512") }
            ),
            ("ultrafast t16 +ref2 (L1)", "ultrafast", 16, new (string, string)?[] { ("ref", "2") }),
        };

        Console.WriteLine($"x265 knob A/B, ms/frame (4K, iters={iters}, crf=22, keyint=120):");
        Console.WriteLine($"  {"".PadRight(22)} {"noise", 10} {"flat", 10}");
        foreach (var (name, preset, thr, extra) in variants)
        {
            IntPtr enc;
            try
            {
                enc = X265OpenVariant(w, h, 22, thr, preset, extra);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name.PadRight(22)} OPEN FAILED: {ex.Message}");
                continue;
            }
            long pts = 0;
            const long FramePts = 10_000_000L / 60;
            double noiseMs = TimeSteady(
                warmup,
                iters,
                () =>
                {
                    pts += FramePts;
                    var f = noise[(int)(pts / FramePts) % 4];
                    EncodeX265(enc, f.y, w * 2, f.u, w, f.v, w / 2, pts, outBuf);
                }
            );
            pts = 0;
            double flatMs = TimeSteady(
                warmup,
                iters,
                () =>
                {
                    pts += FramePts;
                    EncodeX265(enc, y, w * 2, u, w, v, w / 2, pts, outBuf);
                }
            );
            ss_x265_close(enc);
            Console.WriteLine($"  {name.PadRight(22)} {noiseMs, 10:F1} {flatMs, 10:F1}");
        }
        Console.WriteLine();
        Console.WriteLine(
            "(threads param = x265 frame threads; x265 spawns a 2x thread pool. The local x265 build "
                + "caps frame threads at 32 (X265_MAX_FRAME_THREADS patched from the stock 16). "
                + "x265 time wanders with CPU boost state - only within-run comparisons are valid.)"
        );
        Console.WriteLine();
        RunX265SizeAb(iters, w, h, noise, outBuf);
    }

    // L6: rc-lookahead file-size A/B (same CRF, 4K noise cycle). rc-lookahead > 0 enables
    // lookahead AQ/CUTree - usually a size win; the cost is encoder-internal buffering (the
    // first N frames' output is delayed) and possibly wall time (the lookahead thread works
    // in parallel, so steady-state wall cost should be ~0 - measured here).
    static void RunX265SizeAb(
        int iters,
        int w,
        int h,
        (byte[] y, byte[] u, byte[] v)[] noise,
        byte[] outBuf
    )
    {
        const long FramePts = 10_000_000L / 60;
        const int WarmupFrames = 60; // flush the I-frame + lookahead warm-up out of the numbers
        var variants = new (string Name, (string, string)?[]? Extra)[]
        {
            ("rc-lookahead=0 (app)", null),
            ("rc-lookahead=30", new (string, string)?[] { ("rc-lookahead", "30") }),
            (
                "rc-lookahead=30 +ctu32",
                new (string, string)?[] { ("rc-lookahead", "30"), ("ctu", "32") }
            ),
        };
        Console.WriteLine(
            $"x265 file-size A/B (4K noise, crf=22, ultrafast t16, {iters} timed frames after {WarmupFrames} warm):"
        );
        Console.WriteLine(
            $"  {"".PadRight(24)} {"ms/frame", 10} {"KB/frame", 10} {"size vs app", 12}"
        );
        double baseMs = 0,
            baseKb = 0;
        foreach (var (name, extra) in variants)
        {
            IntPtr enc;
            try
            {
                enc = X265OpenVariant(w, h, 22, 16, "ultrafast", extra);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name.PadRight(24)} OPEN FAILED: {ex.Message}");
                continue;
            }
            long pts = 0;
            for (int i = 0; i < WarmupFrames; i++)
            {
                pts += FramePts;
                var f = noise[(int)(pts / FramePts) % 4];
                EncodeX265(enc, f.y, w * 2, f.u, w, f.v, w / 2, pts, outBuf);
            }
            var sw = Stopwatch.StartNew();
            long total = 0;
            for (int i = 0; i < iters; i++)
            {
                pts += FramePts;
                var f = noise[(int)(pts / FramePts) % 4];
                total += EncodeX265(enc, f.y, w * 2, f.u, w, f.v, w / 2, pts, outBuf);
            }
            sw.Stop();
            ss_x265_close(enc);
            double ms = sw.Elapsed.TotalMilliseconds / iters;
            double kb = total / 1024.0 / iters;
            if (baseKb == 0)
            {
                baseMs = ms;
                baseKb = kb;
            }
            string vs =
                baseKb > 0
                    ? $"{(kb - baseKb) / baseKb * 100.0:+0.0;-0.0;0.0}% ({(ms - baseMs) / Math.Max(1, baseMs) * 100.0:+0.0;-0.0;0.0}% time)"
                    : "-";
            Console.WriteLine($"  {name.PadRight(24)} {ms, 10:F1} {kb, 10:F1} {vs, 12}");
        }
        Console.WriteLine();
    }

    // L7: CCD-affinity A/B (--affinity). x265's 32 native threads (16 frame + 16 pool,
    // see the banner) are pinned to one CCD of a dual-CCD Zen 5 CPU (LP 0-15 / LP 16-31; one CCD
    // carries the 96 MB 3D V-Cache) vs left to the scheduler, on 4K noise + flat
    // (ultrafast t16, the app config). Two passes (pass 2 reversed) because x265 time
    // wanders with thermal state (the machine runs Ultimate Performance; the 120 W PBP
    // power limit is what the wander hits, not a P-state governor).
    static void RunAffinityAb(int iters, int warmup)
    {
        const int w = 3840,
            h = 2160;
        int px = w * h;
        byte[] y = new byte[px * 2],
            u = new byte[px / 2],
            v = new byte[px / 2];
        FillP010(y, u, v, w, h, null, noise: false);
        var rng = new Random(1);
        (byte[] y, byte[] u, byte[] v)[] noise = new (byte[], byte[], byte[])[4];
        for (int i = 0; i < 4; i++)
        {
            var n = (y: new byte[px * 2], u: new byte[px / 2], v: new byte[px / 2]);
            FillP010(n.y, n.u, n.v, w, h, rng, noise: true);
            noise[i] = n;
        }
        byte[] outBuf = new byte[16 << 20];

        // CCD split only known/exploitable on a 32-LP dual-CCD Zen 5 box
        if (Environment.ProcessorCount != 32)
        {
            Console.WriteLine(
                "(32-logical-processor box required for the CCD split; nothing to do)"
            );
            return;
        }
        nint ccdA = (nint)0x0000FFFF,
            ccdB = (nint)0xFFFF0000u;
        var variants = new (string Name, nint Pin)[]
        {
            ("baseline (scheduler)", 0),
            ("+pin x265->CCD A (LP 0-15)", ccdA),
            ("+pin x265->CCD B (LP 16-31)", ccdB),
        };

        for (int pass = 1; pass <= 2; pass++)
        {
            var order = pass == 1 ? variants : variants.Reverse().ToArray();
            Console.WriteLine(
                $"L7 CCD-affinity A/B, pass {pass}/2, ms/frame (4K, ultrafast t16, crf=22, iters={iters} after {warmup} warmup):"
            );
            Console.WriteLine($"  {"".PadRight(28)} {"noise", 10} {"flat", 10} {"pinned", 6}");
            foreach (var (name, pin) in order)
            {
                HashSet<int> before = SnapshotThreads();
                IntPtr enc;
                try
                {
                    enc = X265OpenVariant(w, h, 22, 16, "ultrafast", null);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {name.PadRight(28)} OPEN FAILED: {ex.Message}");
                    continue;
                }
                int pinned = PinNewThreads(before, pin); // catch the pool spawned at open
                long pts = 0;
                const long FramePts = 10_000_000L / 60;
                double noiseMs = TimeSteady(
                    warmup,
                    iters,
                    () =>
                    {
                        pts += FramePts;
                        var f = noise[(int)(pts / FramePts) % 4];
                        EncodeX265(enc, f.y, w * 2, f.u, w, f.v, w / 2, pts, outBuf);
                    }
                );
                pinned = Math.Max(pinned, PinNewThreads(before, pin)); // catch lazy spawns
                pts = 0;
                double flatMs = TimeSteady(
                    warmup,
                    iters,
                    () =>
                    {
                        pts += FramePts;
                        EncodeX265(enc, y, w * 2, u, w, v, w / 2, pts, outBuf);
                    }
                );
                ss_x265_close(enc);
                Console.WriteLine(
                    $"  {name.PadRight(28)} {noiseMs, 10:F1} {flatMs, 10:F1} {pinned, 6}"
                );
            }
            Console.WriteLine();
        }
        Console.WriteLine(
            "(pinned = native x265 threads pinned to the CCD; -1 on the baseline row. Pass 2 runs "
                + "the variants in reverse order; x265 time wanders with thermal state, so "
                + "compare within a pass.)"
        );
    }

    static void Check(int rc, string what)
    {
        if (rc < 0)
            throw new Exception($"{what} failed (rc={rc})");
    }

    static int EncodeX265(
        IntPtr enc,
        byte[] y,
        int sy,
        byte[] u,
        int su,
        byte[] v,
        int sv,
        long pts,
        byte[] outBuf
    )
    {
        GCHandle yh = GCHandle.Alloc(y, GCHandleType.Pinned);
        GCHandle uh = GCHandle.Alloc(u, GCHandleType.Pinned);
        GCHandle vh = GCHandle.Alloc(v, GCHandleType.Pinned);
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            int rc = ss_x265_encode(
                enc,
                yh.AddrOfPinnedObject(),
                sy,
                uh.AddrOfPinnedObject(),
                su,
                vh.AddrOfPinnedObject(),
                sv,
                pts,
                oh.AddrOfPinnedObject(),
                outBuf.Length,
                out _
            );
            if (rc < 0)
                throw new Exception($"x265 encode failed (rc={rc})");
            return rc;
        }
        finally
        {
            yh.Free();
            uh.Free();
            vh.Free();
            oh.Free();
        }
    }

    static int EncodeX264(IntPtr enc, byte[] y, int sy, byte[] uv, int suv, long pts, byte[] outBuf)
    {
        GCHandle yh = GCHandle.Alloc(y, GCHandleType.Pinned);
        GCHandle uvh = GCHandle.Alloc(uv, GCHandleType.Pinned);
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            int rc = ss_x264_encode(
                enc,
                yh.AddrOfPinnedObject(),
                sy,
                uvh.AddrOfPinnedObject(),
                suv,
                pts,
                oh.AddrOfPinnedObject(),
                outBuf.Length,
                out _
            );
            if (rc < 0)
                throw new Exception($"x264 encode failed (rc={rc})");
            return rc;
        }
        finally
        {
            yh.Free();
            uvh.Free();
            oh.Free();
        }
    }

    // ==================== --verify: deterministic chroma-row alignment check ====================
    //
    // Runs the parallel conversion on a synthetic flat black field with 2-luma-row (one
    // chroma-row) green stripes on the first and last chroma rows of the first ODD-starting
    // row chunk, plus the neutral chroma rows on both sides. With the fixed cbRow start
    // ((rowBegin + (rowBegin & 1)) / 2) every chroma row lines up; with the old
    // `rowBegin / 2` the odd chunk's chroma is shifted one 4:2:0 row up and its last
    // chroma row is left unwritten (zeroed here, previous-frame chroma in the app - the
    // horizontal lines the user saw). The odd chunk is located from the actual worker
    // count, so the check holds on any machine; no GPU or encoder is needed.
    static int VerifyConversions()
    {
        const int w = 3840,
            h = 2160;
        int px = w * h;
        int failed = 0;

        void Check(string label, bool ok)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
            if (!ok)
                failed++;
        }

        // first chunk that starts on an odd luma row (the one whose chroma mapping the
        // cbRow fix corrects); chunk 0 always starts at 0
        int workers = WorkerCount(h, w * h);
        int chunk = 0;
        for (int i = 1; i < workers; i++)
        {
            if (i * h / workers % 2 == 1)
            {
                chunk = i;
                break;
            }
        }
        int c0 = chunk * h / workers; // odd
        int c1 = (chunk + 1) * h / workers;
        int stripe = c0 + 1; // first even row of the chunk -> first chroma row
        int lastEven = c1 - 1 - ((c1 - 1) & 1); // last even row of the chunk
        int firstChroma = stripe / 2;
        int lastChroma = lastEven / 2;
        bool tailOk = lastEven + 1 < h; // tail stripe stays inside the frame
        Console.WriteLine(
            $"  workers={workers} first odd-start chunk=[{c0},{c1}) chroma rows {firstChroma}..{lastChroma}"
        );
        int[] stripes = [stripe, stripe + 1, lastEven, lastEven + 1];

        // ---------- SDR (NV12): black field + green stripes ----------
        byte[] sdr = new byte[px * 4];
        foreach (int r in stripes)
            if (r < h)
                for (int x = 0; x < w; x++)
                    sdr[(r * w + x) * 4 + 1] = 255; // G channel
        byte[] sY = new byte[px];
        byte[] sUV = new byte[px / 2];
        ConvertSdr(sdr, w, h, sY, sUV, w, h);

        // green: Y=204, Cb=56, Cr=36 (pair-averaged, full-range 16..235); black: Y=16, Cb=Cr=130
        bool sdrOk = true;
        for (int x = 0; x < w; x++)
        {
            sdrOk &= sY[(stripe - 1) * w + x] == 16;
            sdrOk &= sY[stripe * w + x] == 204 && sY[(stripe + 1) * w + x] == 204;
            sdrOk &= sY[lastEven * w + x] == 204;
            if (tailOk)
                sdrOk &= sY[(lastEven + 1) * w + x] == 204;
            for (int k = 0; k < w / 2; k++)
            {
                sdrOk &=
                    sUV[(firstChroma - 1) * w + 2 * k] == 130
                    && sUV[(firstChroma - 1) * w + 2 * k + 1] == 130;
                sdrOk &=
                    sUV[firstChroma * w + 2 * k] == 56 && sUV[firstChroma * w + 2 * k + 1] == 36;
                sdrOk &= sUV[lastChroma * w + 2 * k] == 56 && sUV[lastChroma * w + 2 * k + 1] == 36;
                sdrOk &=
                    sUV[(lastChroma + 1) * w + 2 * k] == 130
                    && sUV[(lastChroma + 1) * w + 2 * k + 1] == 130;
            }
        }
        Check(
            $"SDR NV12: Y rows + chroma rows {firstChroma - 1}/{firstChroma}/{lastChroma}/{lastChroma + 1} aligned ({workers} workers)",
            sdrOk
        );

        // ---------- HDR (P010): black field + 125-unit green stripes (WGC scale) ----------
        byte[] hdr = new byte[px * 8]; // zeros = black
        foreach (int r in stripes)
            if (r < h)
                for (int x = 0; x < w; x++)
                    WriteHalf(hdr, (r * w + x) * 8 + 2, 125.0f); // G channel
        byte[] y16 = new byte[px * 2],
            u16 = new byte[px],
            v16 = new byte[px];
        ConvertHdr(hdr, w, h, y16, u16, v16, w, h);

        // expected codes from the probe's own math (the same LUTs the conversion uses)
        WgcTo2020Rgb01(0f, 125f, 0f, out float gr, out float gg, out float gb);
        float yr = Yr * Pq01(gr) + Yg * Pq01(gg) + Yb * Pq01(gb);
        int yE = Lin10(yr);
        int uE = Lin10(Clamp01(0.5f + (Pq01(gb) - yr) / UDiv));
        int vE = Lin10(Clamp01(0.5f + (Pq01(gr) - yr) / VDiv));
        int uN = Lin10(0.5f),
            vN = Lin10(0.5f); // black: PQ 0 -> neutral chroma

        int halfW = w / 2;
        bool hdrOk = true;
        for (int x = 0; x < w; x += 7) // stride: the plane layout is what's under test
        {
            hdrOk &=
                (y16[(stripe * w + x) * 2] | (y16[(stripe * w + x) * 2 + 1] << 8)) == yE
                && (y16[((stripe - 1) * w + x) * 2] | (y16[((stripe - 1) * w + x) * 2 + 1] << 8))
                    == 0;
            if (tailOk)
                hdrOk &=
                    (
                        y16[((lastEven + 1) * w + x) * 2]
                        | (y16[((lastEven + 1) * w + x) * 2 + 1] << 8)
                    ) == yE;
            for (int k = 0; k < halfW; k += 7)
            {
                int a = (firstChroma - 1) * halfW * 2,
                    b = firstChroma * halfW * 2,
                    c = lastChroma * halfW * 2,
                    d = (lastChroma + 1) * halfW * 2;
                hdrOk &= (u16[a] | (u16[a + 1] << 8)) == uN && (v16[a] | (v16[a + 1] << 8)) == vN;
                hdrOk &= (u16[b] | (u16[b + 1] << 8)) == uE && (v16[b] | (v16[b + 1] << 8)) == vE;
                hdrOk &= (u16[c] | (u16[c + 1] << 8)) == uE && (v16[c] | (v16[c + 1] << 8)) == vE;
                hdrOk &= (u16[d] | (u16[d + 1] << 8)) == uN && (v16[d] | (v16[d + 1] << 8)) == vN;
            }
        }
        Check(
            $"HDR P010: Y rows + chroma rows aligned ({workers} workers; green Y={yE} U={uE} V={vE}, neutral {uN}/{vN})",
            hdrOk
        );

        return failed == 0 ? 0 : 1;
    }

    // ==================== L3 mirror: persistent convert worker pool ====================
    //
    // The app's new path: one dedicated background thread per worker with a FIXED row chunk
    // (idx*h/workers, identical to the old Task-based split so --verify stays valid),
    // pre-allocated stat arrays, and start/done ManualResetEvents. Zero per-frame Task /
    // array allocations. Same design as FrameProcessor.RowPool.
    sealed class ConvertPool : IDisposable
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

        // Generation guard (same as the app's RowPool): a fast worker can lap the
        // coordinator's _start.Reset() and re-pass _start.WaitOne() with the event still
        // set, re-doing its chunk (double-work made the pool 2.3x slower than the Task
        // path). Each frame bumps _gen; a worker does a frame's chunk at most once per gen.
        private int _gen;

        private readonly int[] _lastDone;

        public int[] Mins => _mins;

        public int[] Maxs => _maxs;

        public long[] Sums => _sums;

        public int[] R0 => _r0;

        public int[] R1 => _r1;

        // workChunk(workerIdx) converts that worker's fixed chunk [R0[idx], R1[idx])
        // (the source buffer is captured by the caller - one pool per source)
        public ConvertPool(int h, int workers, Action<int> workChunk)
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
                // loop variable i must not be captured by the lambda (the local .NET SDKs
                // capture it shared across iterations - pre-C#5 semantics); fresh locals
                int idx = i;
                _r0[idx] = idx * h / workers;
                _r1[idx] = (idx + 1) * h / workers;
                _mins[idx] = 1023;
                _done[idx] = new ManualResetEvent(false);
                _works[idx] = () => workChunk(idx);
                _threads[idx] = new Thread(() => Loop(idx)) { IsBackground = true };
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

        public void Run()
        {
            for (int i = 0; i < _workers; i++)
            {
                _mins[i] = 1023;
                _maxs[i] = 0;
                _sums[i] = 0;
                _done[i].Reset();
            }
            _gen++; // published to the workers by the _start.Set/WaitOne barrier below
            _start.Set();
            for (int i = 0; i < _workers; i++)
                _done[i].WaitOne();
            _start.Reset();
            // sink the stats (same role as ConvertHdr's _clliMin/Max/Sum accumulation)
            int min = 1023,
                max = 0;
            long sum = 0;
            for (int i = 0; i < _workers; i++)
            {
                if (_mins[i] < min)
                    min = _mins[i];
                if (_maxs[i] > max)
                    max = _maxs[i];
                sum += _sums[i];
            }
            _clliMin = min;
            _clliMax = max;
            _clliSum += (int)sum;
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

    // steady-state ms/iteration (warmup + N timed iterations)
    static double TimeSteady(int warmup, int iters, Action act)
    {
        for (int i = 0; i < warmup; i++)
            act();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            act();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    // ==================== content generators ====================

    static void WriteHalf(byte[] buf, int off, float v)
    {
        byte[] b = BitConverter.GetBytes((Half)v);
        buf[off] = b[0];
        buf[off + 1] = b[1];
    }

    static void FillHalf(byte[] buf, int px, float r, float g, float b, Random? rng)
    {
        for (int i = 0; i < px; i++)
        {
            // noise: random linear-light values in [0, 125] WGC units (<= 10000 nits)
            float fr = rng != null ? (float)(rng.NextDouble() * 125.0) : r;
            float fg = rng != null ? (float)(rng.NextDouble() * 125.0) : g;
            float fb = rng != null ? (float)(rng.NextDouble() * 125.0) : b;
            WriteHalf(buf, i * 8, fr);
            WriteHalf(buf, i * 8 + 2, fg);
            WriteHalf(buf, i * 8 + 4, fb);
            WriteHalf(buf, i * 8 + 6, 0); // alpha 0 (unused by the pipeline)
        }
    }

    static void FillP010(byte[] y, byte[] u, byte[] v, int w, int h, Random? rng, bool noise)
    {
        int px = w * h;
        int cpx = w * h / 4;
        for (int i = 0; i < px; i++)
        {
            int c = noise ? rng.Next(1024) : 962;
            y[i * 2] = (byte)(c & 0xFF);
            y[i * 2 + 1] = (byte)(c >> 8);
        }
        for (int i = 0; i < cpx; i++)
        {
            int c1 = noise ? rng.Next(1024) : 58;
            int c2 = noise ? rng.Next(1024) : 548;
            u[i * 2] = (byte)(c1 & 0xFF);
            u[i * 2 + 1] = (byte)(c1 >> 8);
            v[i * 2] = (byte)(c2 & 0xFF);
            v[i * 2 + 1] = (byte)(c2 >> 8);
        }
    }

    static void FillNV12(byte[] y, byte[] uv, int w, int h, Random rng, bool noise)
    {
        int ypx = w * h;
        int cpx = ypx / 2; // interleaved UV bytes
        for (int i = 0; i < ypx; i++)
            y[i] = (byte)(noise ? 16 + rng.Next(220) : 118);
        for (int i = 0; i < cpx; i++)
            uv[i] = (byte)(noise ? 16 + rng.Next(225) : 128);
    }

    static void FillRgba8(byte[] buf, int px, Random rng, bool noise)
    {
        for (int i = 0; i < px; i++)
        {
            buf[i * 4] = (byte)(noise ? rng.Next(256) : 120);
            buf[i * 4 + 1] = (byte)(noise ? rng.Next(256) : 120);
            buf[i * 4 + 2] = (byte)(noise ? rng.Next(256) : 120);
            buf[i * 4 + 3] = 255;
        }
    }

    // ==================== main ====================

    static void Main(string[] args)
    {
        if (args.Contains("--verify"))
        {
            // Deterministic check of the parallel conversion's chroma-row mapping (no codecs).
            int rc = VerifyConversions();
            Console.WriteLine(rc == 0 ? "verify: PASS" : "verify: FAIL");
            Environment.Exit(rc);
        }

        bool quick = args.Contains("--quick");
        int iters = quick ? 20 : 60;
        int warmup = 5;

        string repo = FindRepoRoot();
        string codecDir = Path.Combine(repo, "build", "codecs");
        foreach (
            var f in new[] { "Starshot.Codecs.dll", "libx264-164.dll", "libx265.dll", "faac-2.dll" }
        )
        {
            if (!File.Exists(Path.Combine(codecDir, f)))
            {
                Console.Error.WriteLine(
                    $"missing {codecDir}\\{f} - build the codecs first (tools\\codecs\\build-codecs.ps1)"
                );
                return;
            }
        }
        NativeLibrary.Load(Path.Combine(codecDir, "Starshot.Codecs.dll"));
        if (
            ss_init(
                Path.Combine(codecDir, "libx264-164.dll"),
                Path.Combine(codecDir, "libx265.dll"),
                Path.Combine(codecDir, "faac-2.dll")
            ) != 0
        )
        {
            Console.Error.WriteLine("ss_init failed");
            return;
        }

        int threads = Math.Max(2, Environment.ProcessorCount / 2); // same as VideoRecordService
        if (args.Contains("--ab"))
        {
            RunX265Ab(iters, warmup);
            return;
        }
        if (args.Contains("--affinity"))
        {
            RunAffinityAb(iters, warmup);
            return;
        }
        Console.WriteLine(
            $"threads={threads} (app uses max(2, ProcessorCount/2))  iters={iters}  crf=22/23  keyint=120  bframes=0  rc-lookahead=0"
        );
        Console.WriteLine();

        // ---------- 4K buffers (the 3840x2160 row) ----------
        int w4 = 3840,
            h4 = 2160;
        int px4 = w4 * h4;
        byte[] hdr4 = new byte[px4 * 8]; // R16G16B16A16Float (66.4 MB)
        byte[] y4 = new byte[px4 * 2],
            u4 = new byte[px4 / 2],
            v4 = new byte[px4 / 2];
        byte[] n4 = new byte[px4],
            uv4 = new byte[px4 / 2];
        byte[] outBuf = new byte[16 << 20]; // app uses 4 MB; probe needs more for worst-case noise I-frames

        FillHalf(hdr4, px4, 63.75f, 63.75f, 0f, null); // mid-bright yellow-ish flat field
        var rng = new Random(1);
        byte[] hdr4n = new byte[px4 * 8];
        FillHalf(hdr4n, px4, 0, 0, 0, rng);
        byte[] sdr4n = new byte[px4 * 4];
        FillRgba8(sdr4n, px4, rng, true);

        // ---------- 1440p buffers (the 2560x1440 row) ----------
        int w14 = 2560,
            h14 = 1440;
        int px14 = w14 * h14;
        byte[] hdr14 = new byte[px14 * 8];
        byte[] y14 = new byte[px14 * 2],
            u14 = new byte[px14 / 2],
            v14 = new byte[px14 / 2];
        byte[] n14 = new byte[px14],
            uv14 = new byte[px14 / 2];
        byte[] sdr14n = new byte[px14 * 4];
        FillHalf(hdr14, px14, 63.75f, 63.75f, 0f, null);
        FillRgba8(sdr14n, px14, rng, true);

        // ---------- conversion (row-chunk parallel like the pump; clli folded into the pass) ----------
        double conv4k = TimeSteady(
            warmup,
            iters,
            () => ConvertHdr(hdr4, w4, h4, y4, u4, v4, w4, h4)
        );
        double conv1440 = TimeSteady(
            warmup,
            iters,
            () => ConvertHdr(hdr14, w14, h14, y14, u14, v14, w14, h14)
        );
        double conv4kSdr = TimeSteady(
            warmup,
            iters,
            () => ConvertSdr(sdr4n, w4, h4, n4, uv4, w4, h4)
        );
        double conv1440Sdr = TimeSteady(
            warmup,
            iters,
            () => ConvertSdr(sdr14n, w14, h14, n14, uv14, w14, h14)
        );

        // L3: persistent worker pool (the app's new path) vs the old Task-based ConvertHdr.
        // Same chunk split (idx*h/workers), so the conversion results (and --verify) are unchanged.
        int workers4 = WorkerCount(h4, w4 * h4);
        int workers14 = WorkerCount(h14, w14 * h14);
        ConvertPool? pool4k = null;
        pool4k = new ConvertPool(
            h4,
            workers4,
            i =>
            {
                var p = pool4k!; // only invoked after the constructor returns
                ConvertHdrRows(
                    hdr4,
                    w4,
                    p.R0[i],
                    p.R1[i],
                    w4,
                    y4,
                    u4,
                    v4,
                    ref p.Mins[i],
                    ref p.Maxs[i],
                    ref p.Sums[i]
                );
            }
        );
        ConvertPool? pool14 = null;
        pool14 = new ConvertPool(
            h14,
            workers14,
            i =>
            {
                var p = pool14!;
                ConvertHdrRows(
                    hdr14,
                    w14,
                    p.R0[i],
                    p.R1[i],
                    w14,
                    y14,
                    u14,
                    v14,
                    ref p.Mins[i],
                    ref p.Maxs[i],
                    ref p.Sums[i]
                );
            }
        );
        double conv4kPool = TimeSteady(warmup, iters, () => pool4k.Run());
        double conv1440Pool = TimeSteady(warmup, iters, () => pool14.Run());
        pool4k.Dispose();
        pool14.Dispose();

        // ---------- readback lower bound (same-size memcpy; the real stage is the Win2D
        //          GPU->CPU readback of the capture surface - memcpy is the best case) ----------
        byte[] copyDst4 = new byte[px4 * 8];
        byte[] copyDst14 = new byte[px14 * 8];
        double readback4k = TimeSteady(
            5,
            40,
            () => Buffer.BlockCopy(hdr4, 0, copyDst4, 0, hdr4.Length)
        );
        double readback1440 = TimeSteady(
            5,
            40,
            () => Buffer.BlockCopy(hdr14, 0, copyDst14, 0, hdr14.Length)
        );

        // ---------- pre-generated noise frames (8 each; cycled during the timed section) ----------
        (byte[] y, byte[] u, byte[] v)[] p010Noise4k = new (byte[], byte[], byte[])[8];
        for (int i = 0; i < 8; i++)
        {
            p010Noise4k[i] = (new byte[px4 * 2], new byte[px4 / 2], new byte[px4 / 2]);
            FillP010(p010Noise4k[i].y, p010Noise4k[i].u, p010Noise4k[i].v, w4, h4, rng, true);
        }
        (byte[] y, byte[] u, byte[] v)[] p010Noise1440 = new (byte[], byte[], byte[])[8];
        for (int i = 0; i < 8; i++)
        {
            p010Noise1440[i] = (new byte[px14 * 2], new byte[px14 / 2], new byte[px14 / 2]);
            FillP010(
                p010Noise1440[i].y,
                p010Noise1440[i].u,
                p010Noise1440[i].v,
                w14,
                h14,
                rng,
                true
            );
        }
        (byte[] y, byte[] uv)[] nv12Noise4k = new (byte[], byte[])[8];
        for (int i = 0; i < 8; i++)
        {
            nv12Noise4k[i] = (new byte[px4], new byte[px4 / 2]);
            FillNV12(nv12Noise4k[i].y, nv12Noise4k[i].uv, w4, h4, rng, true);
        }
        (byte[] y, byte[] uv)[] nv12Noise1440 = new (byte[], byte[])[8];
        for (int i = 0; i < 8; i++)
        {
            nv12Noise1440[i] = (new byte[px14], new byte[px14 / 2]);
            FillNV12(nv12Noise1440[i].y, nv12Noise1440[i].uv, w14, h14, rng, true);
        }

        // ---------- encoders (flat: one frame fed repeatedly; noise: 8 frames cycled) ----------
        FillP010(y4, u4, v4, w4, h4, null, noise: false);
        FillP010(y14, u14, v14, w14, h14, null, noise: false);

        IntPtr enc4k = X265Open(w4, h4, 22, threads);
        long pts = 0;
        const long FramePts = 10_000_000L / 60;
        double x265_4k_flat = TimeSteady(
            warmup,
            iters,
            () =>
            {
                pts += FramePts;
                EncodeX265(enc4k, y4, w4 * 2, u4, w4, v4, w4 / 2, pts, outBuf);
            }
        );
        pts = 0;
        double x265_4k_noise = TimeSteady(
            warmup,
            iters,
            () =>
            {
                pts += FramePts;
                var f = p010Noise4k[(int)(pts / FramePts) % 8];
                EncodeX265(enc4k, f.y, w4 * 2, f.u, w4, f.v, w4 / 2, pts, outBuf);
            }
        );
        ss_x265_close(enc4k);

        IntPtr enc1440 = X265Open(w14, h14, 22, threads);
        pts = 0;
        double x265_1440_flat = TimeSteady(
            warmup,
            iters,
            () =>
            {
                pts += FramePts;
                EncodeX265(enc1440, y14, w14 * 2, u14, w14, v14, w14 / 2, pts, outBuf);
            }
        );
        pts = 0;
        double x265_1440_noise = TimeSteady(
            warmup,
            iters,
            () =>
            {
                pts += FramePts;
                var f = p010Noise1440[(int)(pts / FramePts) % 8];
                EncodeX265(enc1440, f.y, w14 * 2, f.u, w14, f.v, w14 / 2, pts, outBuf);
            }
        );
        ss_x265_close(enc1440);

        IntPtr encX4 = X264Open(w4, h4, 23, threads);
        pts = 0;
        double x264_4k_noise = TimeSteady(
            warmup,
            iters,
            () =>
            {
                pts += FramePts;
                var f = nv12Noise4k[(int)(pts / FramePts) % 8];
                EncodeX264(encX4, f.y, w4, f.uv, w4, pts, outBuf);
            }
        );
        ss_x264_close(encX4);

        IntPtr encX14 = X264Open(w14, h14, 23, threads);
        pts = 0;
        double x264_1440_noise = TimeSteady(
            warmup,
            iters,
            () =>
            {
                pts += FramePts;
                var f = nv12Noise1440[(int)(pts / FramePts) % 8];
                EncodeX264(encX14, f.y, w14, f.uv, w14, pts, outBuf);
            }
        );
        ss_x264_close(encX14);

        // ---------- report ----------
        void Row(string name, double a, double b) =>
            Console.WriteLine($"  {name.PadRight(28)} {a, 10:F1} {b, 10:F1}");

        Console.WriteLine("per-stage cost, ms/frame (this machine):");
        Console.WriteLine();
        Console.WriteLine($"  {"stage".PadRight(28)} {"4K", 10} {"1440p", 10}");
        Row("readback (memcpy floor)", readback4k, readback1440);
        Row("convert HDR (clli folded)", conv4k, conv1440);
        Row("convert HDR, pool (L3)", conv4kPool, conv1440Pool);
        Row("convert SDR", conv4kSdr, conv1440Sdr);
        Row("x265 encode, flat", x265_4k_flat, x265_1440_flat);
        Row("x265 encode, noise", x265_4k_noise, x265_1440_noise);
        Row("x264 encode, noise", x264_4k_noise, x264_1440_noise);
        Console.WriteLine();

        // pump total uses the pool convert (the shipped path after L3)
        double t4kHdr = readback4k + conv4kPool + x265_4k_noise;
        double t1440Hdr = readback1440 + conv1440Pool + x265_1440_noise;
        double t4kSdr = readback4k / 2 + conv4kSdr + x264_4k_noise; // SDR readback is 33 MB (half of 66)
        double t1440Sdr = readback1440 / 2 + conv1440Sdr + x264_1440_noise;

        Console.WriteLine(
            "pump total per frame (readback floor + parallel convert [clli folded] + encode):"
        );
        Console.WriteLine(
            $"  4K HDR    ~{t4kHdr:F0} ms -> ~{1000.0 / t4kHdr:F1} fps   (real content sits between flat and noise)"
        );
        Console.WriteLine($"  1440p HDR ~{t1440Hdr:F0} ms -> ~{1000.0 / t1440Hdr:F1} fps");
        Console.WriteLine($"  4K SDR    ~{t4kSdr:F0} ms -> ~{1000.0 / t4kSdr:F1} fps");
        Console.WriteLine($"  1440p SDR ~{t1440Sdr:F0} ms -> ~{1000.0 / t1440Sdr:F1} fps");
        Console.WriteLine();
        Console.WriteLine(
            "(measured end-to-end from the user's recordings: 4K ~1.4-3.1 fps, 1440p ~3.4 fps)"
        );
    }

    // repo root = the directory containing Starshot.slnx (walk up from CWD/base dir)
    static string FindRepoRoot()
    {
        foreach (
            string? start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
        )
        {
            for (DirectoryInfo? d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Starshot.slnx")))
                    return d.FullName;
            }
        }
        throw new Exception("Starshot.slnx not found; run from inside the repo");
    }
}
