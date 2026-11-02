using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Starshot.Features.Recording;

/// <summary>
/// Starshot.Codecs.dll（编码器垫片）的 P/Invoke 层。
/// 垫片通过 LoadLibrary 加载三个 LGPL 编码器 DLL（libx264-164 / libx265 / faac-2），
/// 结构体布局全部封闭在 C 层，C# 只传平面指针 + 参数名/值，拿到的是 MP4 sample 布局
/// （[4字节长度][NAL] 拼接）的字节流。
/// 帧平面一律用固定句柄（GCHandle Pin）传指针，避免 byte[] 每次调用整块拷贝。
/// </summary>
internal static class CodecsNative
{
    private const string Lib = "Starshot.Codecs.dll";

    private static readonly object _initLock = new();

    private static bool _initialized;

    private static string? _initError;

    /// <summary>编码器 DLL 目录（随包发布到 exe 同级的 codecs\ 子目录）。</summary>
    public static string CodecsDir => Path.Combine(AppContext.BaseDirectory, "codecs");

    /// <summary>
    /// 初始化（幂等）。成功返回 null；失败返回错误描述（供 UI 展示）。
    /// </summary>
    public static string? Init()
    {
        lock (_initLock)
        {
            if (_initialized)
                return null;
            if (_initError is not null)
                return _initError;
            try
            {
                string dir = CodecsDir;
                string x264 = Path.Combine(dir, "libx264-164.dll");
                string x265 = Path.Combine(dir, "libx265.dll");
                string faac = Path.Combine(dir, "faac-2.dll");
                foreach (var f in new[] { x264, x265, faac, Path.Combine(dir, Lib) })
                {
                    if (!File.Exists(f))
                    {
                        _initError = $"Codec DLL not found: {f}";
                        return _initError;
                    }
                }
                // 预加载垫片（让后续 P/Invoke 生效），编码器 DLL 由 ss_init 内部加载
                NativeLibrary.Load(Path.Combine(dir, Lib));
                int rc = ss_init(x264, x265, faac);
                if (rc != 0)
                {
                    _initError = rc switch
                    {
                        -1 => "Failed to load x264 encoder",
                        -2 => "Failed to load x265 encoder",
                        -3 => "Failed to load faac encoder",
                        _ => $"Unknown codec init error ({rc})",
                    };
                    return _initError;
                }
                _initialized = true;
                return null;
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
                return _initError;
            }
        }
    }

    // ==================== x264（SDR NV12 -> H.264） ====================

    public static IntPtr X264ParamCreate()
    {
        IntPtr p = ss_x264_param_create();
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("x264 param create failed");
        return p;
    }

    public static void X264ParamFree(IntPtr h)
    {
        if (h != IntPtr.Zero)
            ss_x264_param_free(h);
    }

    public static int X264Preset(IntPtr h, string preset, string? tune)
    {
        return ss_x264_preset(h, preset, tune);
    }

    public static int X264Parse(IntPtr h, string name, string value)
    {
        return ss_x264_parse(h, name, value);
    }

    public static void X264SetGeometry(IntPtr h, int width, int height, int fpsNum, int fpsDen)
    {
        ss_x264_set_geometry(h, width, height, fpsNum, fpsDen);
    }

    public static void X264SetColor(
        IntPtr h,
        int colorprim,
        int transfer,
        int matrix,
        int fullrange
    )
    {
        ss_x264_set_color(h, colorprim, transfer, matrix, fullrange);
    }

    public static void X264SetCrf(IntPtr h, double crf, int keyint, int bframes, int threads)
    {
        ss_x264_set_crf(h, crf, keyint, bframes, threads);
    }

    /// <summary>ABR target average bitrate (kb/s); kbps &lt;= 0 is a no-op (keeps CRF mode). Call after SetCrf.</summary>
    public static void X264SetBitrate(IntPtr h, int kbps)
    {
        ss_x264_set_bitrate(h, kbps);
    }

    public static IntPtr X264Open(IntPtr param)
    {
        IntPtr e = ss_x264_open(param);
        if (e == IntPtr.Zero)
            throw new InvalidOperationException("x264 open failed");
        return e;
    }

    /// <summary>
    /// 取 SPS/PPS（[len4][SPS][len4][PPS] 布局，avcC 用）。
    /// </summary>
    public static int X264Headers(IntPtr h, byte[] buf, out int bytes)
    {
        GCHandle hnd = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            int rc = ss_x264_headers(h, hnd.AddrOfPinnedObject(), buf.Length, out bytes);
            if (rc < 0)
                throw new InvalidOperationException("x264 headers failed");
            return rc;
        }
        finally
        {
            hnd.Free();
        }
    }

    /// <summary>
    /// 编码一帧 NV12（y/uv 平面指针 + 字节 stride）。
    /// 返回写入字节数；0 = 帧还在编码器内部（正常）；负数 = 错误。
    /// </summary>
    public static int X264Encode(
        IntPtr h,
        byte[] y,
        int strideY,
        byte[] uv,
        int strideUV,
        long pts,
        byte[] outBuf,
        out int outBytes
    )
    {
        GCHandle yh = GCHandle.Alloc(y, GCHandleType.Pinned);
        GCHandle uvh = GCHandle.Alloc(uv, GCHandleType.Pinned);
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            int rc = ss_x264_encode(
                h,
                yh.AddrOfPinnedObject(),
                strideY,
                uvh.AddrOfPinnedObject(),
                strideUV,
                pts,
                oh.AddrOfPinnedObject(),
                outBuf.Length,
                out outBytes
            );
            if (rc < 0)
                throw new InvalidOperationException("x264 encode failed");
            return rc;
        }
        finally
        {
            yh.Free();
            uvh.Free();
            oh.Free();
        }
    }

    /// <summary>
    /// L5: native-pointer version of <see cref="X264Encode"/>: the caller pins the planes /
    /// output buffer once per session and passes the pointers (no per-frame GCHandle churn).
    /// The C shim already takes raw pointers - no shim changes.
    /// </summary>
    public static int X264EncodeNative(
        IntPtr h,
        nint y,
        int strideY,
        nint uv,
        int strideUV,
        long pts,
        nint outBuf,
        int outCap,
        out int outBytes
    )
    {
        outBytes = 0;
        int rc = ss_x264_encode(h, y, strideY, uv, strideUV, pts, outBuf, outCap, out outBytes);
        if (rc < 0)
            throw new InvalidOperationException("x264 encode failed");
        return rc;
    }

    /// <summary>
    /// Flush: drains the encoder's frame-thread backlog (one round can emit the NALs of
    /// several queued frames). Returns the bytes written this round (0 = drained); on a
    /// buffer overflow it returns a negative rc and outBytes holds the recoverable
    /// partial already written into the buffer (complete NALs only) - the caller muxes
    /// that part, grows the buffer, and retries (the remaining frames are still inside
    /// the encoder).
    /// </summary>
    public static int X264Flush(IntPtr h, byte[] outBuf, out int outBytes)
    {
        outBytes = 0;
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            return ss_x264_flush(h, oh.AddrOfPinnedObject(), outBuf.Length, out outBytes);
        }
        finally
        {
            oh.Free();
        }
    }

    public static void X264Close(IntPtr h)
    {
        if (h != IntPtr.Zero)
            ss_x264_close(h);
    }

    // ==================== x265（HDR planar P010 -> HEVC） ====================

    public static IntPtr X265ParamCreate()
    {
        IntPtr p = ss_x265_param_create();
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("x265 param create failed");
        return p;
    }

    public static void X265ParamFree(IntPtr h)
    {
        if (h != IntPtr.Zero)
            ss_x265_param_free(h);
    }

    public static int X265Preset(IntPtr h, string? preset, string? tune)
    {
        return ss_x265_preset(h, preset, tune);
    }

    public static int X265Parse(IntPtr h, string name, string value)
    {
        return ss_x265_parse(h, name, value);
    }

    public static void X265SetGeometry(IntPtr h, int width, int height, int fpsNum, int fpsDen)
    {
        ss_x265_set_geometry(h, width, height, fpsNum, fpsDen);
    }

    /// <summary>HDR10：10-bit planar I420 + BT.2020 / PQ / BT.2020nc（vui 字段）。</summary>
    public static void X265SetHdr(IntPtr h, int bitDepth, int colorprim, int transfer, int matrix)
    {
        ss_x265_set_hdr(h, bitDepth, colorprim, transfer, matrix);
    }

    public static void X265SetCrf(IntPtr h, double crf, int keyint, int bframes, int threads)
    {
        ss_x265_set_crf(h, crf, keyint, bframes, threads);
    }

    /// <summary>ABR target average bitrate (kb/s); kbps &lt;= 0 is a no-op (keeps CRF mode). Call after SetCrf.</summary>
    public static void X265SetBitrate(IntPtr h, int kbps)
    {
        ss_x265_set_bitrate(h, kbps);
    }

    public static IntPtr X265Open(IntPtr param)
    {
        IntPtr e = ss_x265_open(param);
        if (e == IntPtr.Zero)
            throw new InvalidOperationException("x265 open failed");
        return e;
    }

    /// <summary>
    /// 取头部 NAL（VPS/SPS/PPS/SEI，[len4][NAL] 拼接，hvcC 用）。
    /// </summary>
    public static int X265Headers(IntPtr h, byte[] buf, out int bytes, out int numNals)
    {
        GCHandle hnd = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            int rc = ss_x265_headers(
                h,
                hnd.AddrOfPinnedObject(),
                buf.Length,
                out bytes,
                out numNals
            );
            if (rc < 0)
                throw new InvalidOperationException("x265 headers failed");
            return rc;
        }
        finally
        {
            hnd.Free();
        }
    }

    /// <summary>
    /// 编码一帧 planar P010（Y/U/V 各 2 字节/样本，16-bit 容器存 10-bit 值）。
    /// </summary>
    public static int X265Encode(
        IntPtr h,
        byte[] y,
        int strideY,
        byte[] u,
        int strideU,
        byte[] v,
        int strideV,
        long pts,
        byte[] outBuf,
        out int outBytes
    )
    {
        GCHandle yh = GCHandle.Alloc(y, GCHandleType.Pinned);
        GCHandle uh = GCHandle.Alloc(u, GCHandleType.Pinned);
        GCHandle vh = GCHandle.Alloc(v, GCHandleType.Pinned);
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            int rc = ss_x265_encode(
                h,
                yh.AddrOfPinnedObject(),
                strideY,
                uh.AddrOfPinnedObject(),
                strideU,
                vh.AddrOfPinnedObject(),
                strideV,
                pts,
                oh.AddrOfPinnedObject(),
                outBuf.Length,
                out outBytes
            );
            if (rc < 0)
                throw new InvalidOperationException("x265 encode failed");
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

    /// <summary>
    /// L5: native-pointer version of <see cref="X265Encode"/>: the caller pins the planes /
    /// output buffer once per session and passes the pointers (no per-frame GCHandle churn).
    /// The C shim already takes raw pointers - no shim changes.
    /// </summary>
    public static int X265EncodeNative(
        IntPtr h,
        nint y,
        int strideY,
        nint u,
        int strideU,
        nint v,
        int strideV,
        long pts,
        nint outBuf,
        int outCap,
        out int outBytes
    )
    {
        outBytes = 0;
        int rc = ss_x265_encode(
            h,
            y,
            strideY,
            u,
            strideU,
            v,
            strideV,
            pts,
            outBuf,
            outCap,
            out outBytes
        );
        if (rc < 0)
            throw new InvalidOperationException("x265 encode failed");
        return rc;
    }

    /// <summary>Same contract as <see cref="X264Flush"/> (x265/HDR variant).</summary>
    public static int X265Flush(IntPtr h, byte[] outBuf, out int outBytes)
    {
        outBytes = 0;
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            return ss_x265_flush(h, oh.AddrOfPinnedObject(), outBuf.Length, out outBytes);
        }
        finally
        {
            oh.Free();
        }
    }

    public static void X265Close(IntPtr h)
    {
        if (h != IntPtr.Zero)
            ss_x265_close(h);
    }

    // ==================== faac（WASAPI float32 -> AAC-LC） ====================

    public static IntPtr FaacOpen(
        int sampleRate,
        int channels,
        int bitRate,
        int quantQuality,
        out uint frameSamples,
        out uint maxOutBytes
    )
    {
        IntPtr e = ss_faac_open(
            sampleRate,
            channels,
            bitRate,
            quantQuality,
            out frameSamples,
            out maxOutBytes
        );
        if (e == IntPtr.Zero)
            throw new InvalidOperationException("faac open failed");
        return e;
    }

    /// <summary>取 AudioSpecificConfig（esds 的 decoderSpecificInfo）。返回拷贝字节数。</summary>
    public static int FaacAsc(IntPtr h, byte[] buf, out int len)
    {
        GCHandle hnd = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            int rc = ss_faac_asc(h, hnd.AddrOfPinnedObject(), buf.Length, out len);
            if (rc < 0)
                throw new InvalidOperationException("faac asc failed");
            return rc;
        }
        finally
        {
            hnd.Free();
        }
    }

    /// <summary>
    /// 编码 float32 交错 PCM（inSamples 为全部声道合计的样本数）。
    /// 返回写入字节数（0 = 帧还在累积，正常）；负数 = 错误。结束时传 0 冲刷。
    /// </summary>
    public static int FaacEncode(
        IntPtr h,
        float[] inBuf,
        uint inSamples,
        byte[] outBuf,
        out uint written
    )
    {
        GCHandle ih = GCHandle.Alloc(inBuf, GCHandleType.Pinned);
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            int rc = ss_faac_encode(
                h,
                ih.AddrOfPinnedObject(),
                inSamples,
                oh.AddrOfPinnedObject(),
                (uint)outBuf.Length,
                out written
            );
            if (rc < 0)
                throw new InvalidOperationException("faac encode failed");
            return rc;
        }
        finally
        {
            ih.Free();
            oh.Free();
        }
    }

    /// <summary>
    /// 编码 float32 交错 PCM (native 指针版, WASAPI 回读缓冲直接用)。
    /// 结束时传 inSamples=0 冲刷。
    /// </summary>
    public static int FaacEncodeNative(
        IntPtr h,
        IntPtr inPtr,
        uint inSamples,
        byte[] outBuf,
        out uint written
    )
    {
        GCHandle oh = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
        try
        {
            int rc = ss_faac_encode(
                h,
                inPtr,
                inSamples,
                oh.AddrOfPinnedObject(),
                (uint)outBuf.Length,
                out written
            );
            if (rc < 0)
                throw new InvalidOperationException("faac encode failed");
            return rc;
        }
        finally
        {
            oh.Free();
        }
    }

    public static void FaacClose(IntPtr h)
    {
        if (h != IntPtr.Zero)
            ss_faac_close(h);
    }

    // ==================== P/Invoke（平面参数一律 IntPtr，由调用方 Pin） ====================

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int ss_init(string x264Path, string x265Path, string faacPath);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_shutdown();

    // x264
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ss_x264_param_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x264_param_free(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int ss_x264_preset(IntPtr h, string? preset, string? tune);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int ss_x264_parse(IntPtr h, string name, string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x264_set_geometry(
        IntPtr h,
        int w,
        int hgt,
        int fpsNum,
        int fpsDen
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x264_set_color(IntPtr h, int cp, int tf, int mx, int fr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x264_set_crf(
        IntPtr h,
        double crf,
        int keyint,
        int bframes,
        int threads
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x264_set_bitrate(IntPtr h, int kbps);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ss_x264_open(IntPtr param);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_x264_headers(IntPtr h, IntPtr buf, int cap, out int bytes);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_x264_encode(
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

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_x264_flush(IntPtr h, IntPtr buf, int cap, out int bytes);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x264_close(IntPtr h);

    // x265
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ss_x265_param_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x265_param_free(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int ss_x265_preset(IntPtr h, string? preset, string? tune);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int ss_x265_parse(IntPtr h, string name, string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x265_set_geometry(
        IntPtr h,
        int w,
        int hgt,
        int fpsNum,
        int fpsDen
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x265_set_hdr(IntPtr h, int depth, int cp, int tf, int mx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x265_set_crf(
        IntPtr h,
        double crf,
        int keyint,
        int bframes,
        int threads
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x265_set_bitrate(IntPtr h, int kbps);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ss_x265_open(IntPtr param);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_x265_headers(
        IntPtr h,
        IntPtr buf,
        int cap,
        out int bytes,
        out int numNals
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_x265_encode(
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

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_x265_flush(IntPtr h, IntPtr buf, int cap, out int bytes);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_x265_close(IntPtr h);

    // faac
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ss_faac_open(
        int sr,
        int ch,
        int br,
        int q,
        out uint frameSamples,
        out uint maxOut
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_faac_asc(IntPtr h, IntPtr buf, int cap, out int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ss_faac_encode(
        IntPtr h,
        IntPtr inBuf,
        uint inSamples,
        IntPtr outBuf,
        uint cap,
        out uint written
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ss_faac_close(IntPtr h);
}
