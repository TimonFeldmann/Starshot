using System;
using System.Collections.Generic;
using System.Text;

namespace Starshot.Features.Recording;

/// <summary>
/// 视频编码器会话：SDR 走 x264（NV12 8-bit -> H.264），HDR 走 x265（planar P010 10-bit -> HEVC）。
/// 输出为 MP4 sample 布局（[4字节长度][NAL] 拼接）；头部 NAL 供 muxer 生成 avcC / hvcC。
/// </summary>
internal sealed class VideoEncoder : IDisposable
{
    private readonly bool _hdr;

    private IntPtr _param;
    private IntPtr _enc;

    private readonly int _width;
    private readonly int _height;
    private readonly int _fpsNum;
    private readonly int _fpsDen;

    /// <summary>头部 NAL 缓冲（x264: [len4][SPS][len4][PPS]；x265: [len4][VPS/SPS/PPS/SEI...]）。</summary>
    private readonly byte[] _headerBuf = new byte[8192];

    public int HeaderByteCount { get; private set; }

    public int HeaderNalCount { get; private set; }

    public bool IsHdr { get; private set; }

    /// <summary>
    /// x264 固定 8 位 / x265 10 位（16-bit 容器）的帧缓冲由 FrameProcessor 提供。
    /// bitrateKbps &gt; 0 switches the rate control to ABR (file ~= bitrate x duration);
    /// 0 = CRF quality mode (size varies with content).
    /// </summary>
    public VideoEncoder(
        bool hdr,
        int width,
        int height,
        int fpsNum,
        int fpsDen,
        double crf,
        int bitrateKbps,
        int keyint,
        int bframes,
        int threads,
        string x265Preset
    )
    {
        _hdr = hdr;
        _width = width;
        _height = height;
        _fpsNum = fpsNum;
        _fpsDen = fpsDen;
        IsHdr = hdr;

        if (_hdr)
        {
            _param = CodecsNative.X265ParamCreate();
            // Preset comes from the user's setting (AppConfig.VideoRecordEncoderPreset,
            // the x265 speed ladder); smaller index = faster encoding / bigger files.
            // A/B data in performance.md ("x265 preset A/B").
            CheckOk(CodecsNative.X265Preset(_param, x265Preset, null), "x265 preset");
            // MP4 需要长度前缀 NAL（4.3 默认 Annex B 起始码，必须关掉）
            CheckOk(CodecsNative.X265Parse(_param, "annexb", "0"), "x265 annexb=0");
            // HDR 输出走全范围（与 colr nclx full range 对应）
            CheckOk(CodecsNative.X265Parse(_param, "range", "full"), "x265 range=full");
            // 录屏低延迟：关 rc-lookahead（否则编码器缓冲约 20+ 帧，视频轨开头缺失一大段）
            CheckOk(CodecsNative.X265Parse(_param, "rc-lookahead", "0"), "x265 rc-lookahead=0");
            CodecsNative.X265SetGeometry(_param, _width, _height, fpsNum, fpsDen);
            // 10-bit planar I420 + BT.2020 / PQ(smpte2084) / BT.2020nc
            CodecsNative.X265SetHdr(_param, 10, 9, 16, 9);
            CodecsNative.X265SetCrf(_param, crf, keyint, bframes, threads);
            // ABR mode (Recording settings "Bitrate"): target average bitrate instead of
            // CRF quality; keyint/bframes/threads from SetCrf stay in effect.
            if (bitrateKbps > 0)
                CodecsNative.X265SetBitrate(_param, bitrateKbps);
            _enc = CodecsNative.X265Open(_param);
            int rc = CodecsNative.X265Headers(_enc, _headerBuf, out int bytes, out int nals);
            if (rc < 0)
                throw new InvalidOperationException("x265 header NALs failed");
            HeaderByteCount = bytes;
            HeaderNalCount = nals;
        }
        else
        {
            _param = CodecsNative.X264ParamCreate();
            CheckOk(CodecsNative.X264Preset(_param, "veryfast", "film"), "x264 preset");
            CheckOk(CodecsNative.X264Parse(_param, "annexb", "0"), "x264 annexb=0");
            // 录屏低延迟：关 rc-lookahead（x264 默认缓冲 40 帧，视频轨开头缺失）
            CheckOk(CodecsNative.X264Parse(_param, "rc-lookahead", "0"), "x264 rc-lookahead=0");
            CodecsNative.X264SetGeometry(_param, _width, _height, fpsNum, fpsDen);
            // SDR 固定 BT.709 有限范围（与 NV12 转换系数 + colr nclx 对应）
            CodecsNative.X264SetColor(_param, 1, 1, 1, 0);
            CodecsNative.X264SetCrf(_param, crf, keyint, bframes, threads);
            // ABR mode (Recording settings "Bitrate"): target average bitrate instead of
            // CRF quality; keyint/bframes/threads from SetCrf stay in effect.
            if (bitrateKbps > 0)
                CodecsNative.X264SetBitrate(_param, bitrateKbps);
            _enc = CodecsNative.X264Open(_param);
            int rc4 = CodecsNative.X264Headers(_enc, _headerBuf, out int bytes4);
            if (rc4 < 0)
                throw new InvalidOperationException("x264 header NALs failed");
            HeaderByteCount = bytes4;
            HeaderNalCount = 2;
        }
    }

    private static void CheckOk(int rc, string what)
    {
        if (rc < 0)
            throw new InvalidOperationException($"{what} failed (rc={rc})");
    }

    public byte[] GetHeaderBytes()
    {
        return _headerBuf.AsSpan(0, HeaderByteCount).ToArray();
    }

    /// <summary>
    /// 编码一帧 SDR NV12。返回写入字节数（0 = B 帧延迟，正常）。
    /// isSync = 该输出含 I 帧（H.264 slice 类型 1/5），stss 用。
    /// </summary>
    public int EncodeSdr(
        byte[] y,
        int strideY,
        byte[] uv,
        int strideUV,
        long pts,
        byte[] outBuf,
        out int outBytes,
        out bool isSync
    )
    {
        isSync = false;
        int rc = CodecsNative.X264Encode(_enc, y, strideY, uv, strideUV, pts, outBuf, out outBytes);
        if (rc > 0)
        {
            isSync = ContainsSyncH264(outBuf, rc);
        }
        return rc;
    }

    /// <summary>
    /// 编码一帧 HDR planar P010（Y/U/V 各 2 字节/样本，16-bit 容器存 10-bit 值）。
    /// </summary>
    public int EncodeHdr(
        byte[] y,
        int strideY,
        byte[] u,
        int strideU,
        byte[] v,
        int strideV,
        long pts,
        byte[] outBuf,
        out int outBytes,
        out bool isSync
    )
    {
        isSync = false;
        int rc = CodecsNative.X265Encode(
            _enc,
            y,
            strideY,
            u,
            strideU,
            v,
            strideV,
            pts,
            outBuf,
            out outBytes
        );
        if (rc > 0)
        {
            isSync = ContainsSyncHevc(outBuf, rc);
        }
        return rc;
    }

    /// <summary>
    /// L5: encode with pre-pinned pointers (the caller pins the session's planes + output
    /// buffer once - see VideoRecordService.PinnedEncodeInputs); no per-call GCHandle churn.
    /// The managed outBuf aliases outPtr (the native call writes through it) and is used
    /// for the sync-point scan.
    /// </summary>
    public int EncodeHdrPinned(
        nint y,
        int strideY,
        nint u,
        int strideU,
        nint v,
        int strideV,
        long pts,
        nint outPtr,
        int outCap,
        byte[] outBuf,
        out int outBytes,
        out bool isSync
    )
    {
        isSync = false;
        outBytes = 0;
        int rc = CodecsNative.X265EncodeNative(
            _enc,
            y,
            strideY,
            u,
            strideU,
            v,
            strideV,
            pts,
            outPtr,
            outCap,
            out outBytes
        );
        if (rc > 0)
        {
            isSync = ContainsSyncHevc(outBuf, rc);
        }
        return rc;
    }

    /// <summary>L5: SDR variant of <see cref="EncodeHdrPinned"/> (single interleaved UV plane).</summary>
    public int EncodeSdrPinned(
        nint y,
        int strideY,
        nint uv,
        int strideUV,
        long pts,
        nint outPtr,
        int outCap,
        byte[] outBuf,
        out int outBytes,
        out bool isSync
    )
    {
        isSync = false;
        outBytes = 0;
        int rc = CodecsNative.X264EncodeNative(
            _enc,
            y,
            strideY,
            uv,
            strideUV,
            pts,
            outPtr,
            outCap,
            out outBytes
        );
        if (rc > 0)
        {
            isSync = ContainsSyncH264(outBuf, rc);
        }
        return rc;
    }

    /// <summary>
    /// Flush (called at recording stop). Returns the bytes written this round (0 =
    /// drained); on a buffer overflow it returns a negative rc and outPartial holds the
    /// recoverable partial already written into outBuf (mux it, grow the buffer, retry).
    /// </summary>
    public int Flush(byte[] outBuf, out int outPartial)
    {
        if (_hdr)
            return CodecsNative.X265Flush(_enc, outBuf, out outPartial);
        return CodecsNative.X264Flush(_enc, outBuf, out outPartial);
    }

    /// <summary>
    /// H.264：NAL 类型 = 首字节低 5 位。类型 1（非 IDR I）/ 5（IDR）视为同步点
    /// （SPS/PPS 已在 avcC 中，I 帧即可解码）。
    /// </summary>
    private static bool ContainsSyncH264(byte[] buf, int len)
    {
        int off = 0;
        while (off + 4 < len)
        {
            int nlen =
                (buf[off] << 24 | buf[off + 1] << 16 | buf[off + 2] << 8 | buf[off + 3])
                & 0x7FFFFFFF;
            if (nlen < 1 || off + 4 + nlen > len)
                break;
            int type = buf[off + 4] & 0x1F;
            if (type is 1 or 5)
                return true;
            off += 4 + nlen;
        }
        return false;
    }

    /// <summary>
    /// HEVC：NAL 类型 = (首字节 >> 1) & 0x3F。仅 19/20/21 (IDR_W_RADL/IDR_N_LP/CRA) 是同步点;
    /// 类型 1 (TRAIL_R) 是普通 P 帧 (bframes=0 时 x265 的非 IDR 帧), 不可当作同步点。
    /// </summary>
    private static bool ContainsSyncHevc(byte[] buf, int len)
    {
        int off = 0;
        while (off + 4 < len)
        {
            int nlen =
                (buf[off] << 24 | buf[off + 1] << 16 | buf[off + 2] << 8 | buf[off + 3])
                & 0x7FFFFFFF;
            if (nlen < 2 || off + 4 + nlen > len)
                break;
            int type = (buf[off + 4] >> 1) & 0x3F;
            if (type is 19 or 20 or 21)
                return true;
            off += 4 + nlen;
        }
        return false;
    }

    public void Dispose()
    {
        if (_enc != IntPtr.Zero)
        {
            if (_hdr)
                CodecsNative.X265Close(_enc);
            else
                CodecsNative.X264Close(_enc);
            _enc = IntPtr.Zero;
        }
        if (_param != IntPtr.Zero)
        {
            if (_hdr)
                CodecsNative.X265ParamFree(_param);
            else
                CodecsNative.X264ParamFree(_param);
            _param = IntPtr.Zero;
        }
    }
}

/// <summary>
/// 把 [len4][NAL] 头部流按 NAL 拆出来（hvcC 需要 VPS/SPS/PPS 各自成组）。
/// </summary>
internal static class HeaderNals
{
    public static byte[][] Split(byte[] data, int headerBytes)
    {
        var result = new List<byte[]>();
        int off = 0;
        while (off + 4 <= headerBytes)
        {
            int nlen =
                (data[off] << 24 | data[off + 1] << 16 | data[off + 2] << 8 | data[off + 3])
                & 0x7FFFFFFF;
            if (nlen < 1 || off + 4 + nlen > headerBytes)
                break;
            result.Add(data.AsSpan(off + 4, nlen).ToArray());
            off += 4 + nlen;
        }
        return result.ToArray();
    }

    /// <summary>
    /// HEVC NAL 类型（首字节 (b>>1)&0x3F）：32=VPS 33=SPS 34=PPS 39=SEI。
    /// </summary>
    public static int HevcNalType(byte[] nal)
    {
        return nal.Length >= 1 ? (nal[0] >> 1) & 0x3F : -1;
    }

    /// <summary>H.264 NAL 类型（首字节低 5 位）：7=SPS 8=PPS。</summary>
    public static int H264NalType(byte[] nal)
    {
        return nal.Length >= 1 ? nal[0] & 0x1F : -1;
    }
}
