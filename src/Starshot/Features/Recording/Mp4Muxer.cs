using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Starshot.Features.Recording;

/// <summary>
/// 极简 MP4 (ISO BMFF) 复接器。
/// 布局: ftyp | mdat(视频) | mdat(音频, 可选) | moov (尾部追加, 播放器可 seek)。
/// 视频/音频 sample 先落各自的临时文件, FinalizeFile 时顺序装配到目标文件
/// (单 mdat 单 chunk / stco, 最常规的多播兼容布局); 每个 track 独立 timescale:
/// 视频 10_000_000 (100ns, stts 用真实帧间隔 RLE), 音频 = 采样率 (1024 样本/条)。
/// 32-bit sample 偏移, 单文件上限 4GB。
/// </summary>
internal sealed class Mp4Muxer : IDisposable
{
    private const int VideoTimescale = 10_000_000;

    private readonly string _targetPath;
    private readonly FileStream _vData;
    private readonly FileStream? _aData;

    private readonly List<long> _vPts = new(); // 100ns, 相对首帧 (bframes=0, 单调)
    private readonly List<int> _vSizes = new();
    private readonly List<int> _vSync = new(); // 1-based sample 号 (stss)
    private int _vSampleCount;

    private int _aSampleCount; // AAC 帧数 (每帧固定 1024 样本)
    private readonly List<int> _aSizes = new();

    private bool _audioBegun;
    private int _audioRate;
    private int _audioChannels;
    private byte[] _audioAsc = Array.Empty<byte>();
    private long _firstAudioPts;

    private int _width;
    private int _height;
    private bool _hdr;
    private byte[] _videoHeaders = Array.Empty<byte>(); // [len4][NAL] 头部流 (avcC / hvcC 用)
    private byte[] _firstSamplePrefix = Array.Empty<byte>(); // 首个 sample 前置的带内头部 NAL
    private long _firstVideoPts;
    private int _fpsNum = 60;
    private int _fpsDen = 1;

    private bool _disposed;

    private Mp4Muxer(string targetPath, FileStream vData, FileStream? aData)
    {
        _targetPath = targetPath;
        _vData = vData;
        _aData = aData;
    }

    /// <summary>
    /// 打开录制文件 (目标路径 + 同目录两个临时数据文件)。
    /// </summary>
    public static Mp4Muxer Open(string targetPath)
    {
        string dir = Path.GetDirectoryName(targetPath)!;
        string name = Path.GetFileNameWithoutExtension(targetPath);
        string vTemp = Path.Combine(dir, $"~{name}.v.tmp");
        string aTemp = Path.Combine(dir, $"~{name}.a.tmp");
        // ReadWrite: FinalizeFile 要从临时文件读回 sample 数据组装最终 MP4
        var vData = new FileStream(
            vTemp,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            1 << 20
        );
        var aData = new FileStream(
            aTemp,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read,
            1 << 20
        );
        return new Mp4Muxer(targetPath, vData, aData);
    }

    // ==================== 视频 ====================

    /// <summary>
    /// 声明视频轨道。videoHeaders = 编码器的 [len4][NAL] 头部流
    /// (x264: SPS+PPS; x265: VPS/SPS/PPS/SEI), 用于生成 avcC / hvcC。
    /// </summary>
    public void BeginVideo(
        int width,
        int height,
        bool hdr,
        byte[] videoHeaders,
        long firstVideoPts100ns,
        int fpsNum,
        int fpsDen
    )
    {
        _width = width;
        _height = height;
        _hdr = hdr;
        _videoHeaders = videoHeaders;
        _firstSamplePrefix = BuildFirstSamplePrefix(videoHeaders, hdr);
        _firstVideoPts = firstVideoPts100ns;
        _fpsNum = fpsNum;
        _fpsDen = fpsDen;
    }

    /// <summary>追加一个视频 sample (pts 为 100ns 相对时间)。</summary>
    public void AddVideoSample(long pts100ns, byte[] data, int len, bool isSync)
    {
        AddVideoSample(pts100ns, data, 0, len, isSync);
    }

    /// <summary>
    /// 追加一个视频 sample, 数据取 [offset, offset+len) 子区
    /// (冲刷缓冲一次含多个 access unit 时按帧逐条入轨用)。
    /// </summary>
    public void AddVideoSample(long pts100ns, byte[] data, int offset, int len, bool isSync)
    {
        int size = len;
        if (_vSampleCount == 0 && _firstSamplePrefix.Length > 0)
        {
            // 首个 sample 前置带内头部 NAL (见 BuildFirstSamplePrefix)
            _vData.Write(_firstSamplePrefix, 0, _firstSamplePrefix.Length);
            size += _firstSamplePrefix.Length;
        }
        _vData.Write(data, offset, len);
        _vPts.Add(pts100ns);
        _vSizes.Add(size);
        _vSampleCount++;
        if (isSync)
        {
            _vSync.Add(_vSampleCount);
        }
    }

    /// <summary>
    /// 首个视频 sample 前置的带内头部 NAL (保留长度前缀)。
    /// FFmpeg movenc 把带内头部 NAL 写进首个 sample (HEVC: VPS/SPS/PPS; H.264: SPS/PPS),
    /// 而不是只放 hvcC/avcC; 缺少带内头部 NAL 时 FFmpeg 的 HEVC 解码器会在部分帧
    /// 报 "Two slices reporting being the first in the same frame" (已对照 FF remux 验证)。
    /// HEVC 取类型 32/33/34 (x265 4.3 编号) 的 NAL; H.264 头部流整体即 SPS+PPS。
    /// </summary>
    private static byte[] BuildFirstSamplePrefix(byte[] headers, bool hdr)
    {
        if (headers.Length == 0)
            return Array.Empty<byte>();
        if (!hdr)
            return (byte[])headers.Clone(); // x264: [len4][SPS][len4][PPS]
        var pre = new List<byte>();
        int off = 0;
        while (off + 4 <= headers.Length)
        {
            int nlen = Be32(headers, ref off); // off 推进到 NAL 起点
            if (nlen < 2 || off + nlen > headers.Length)
                break;
            int type = (headers[off] >> 1) & 0x3F;
            if (type is 32 or 33 or 34) // VPS/SPS/PPS
                pre.AddRange(headers.AsSpan(off - 4, nlen + 4)); // 连同原长度前缀
            off += nlen;
        }
        return pre.ToArray();
    }

    // ==================== 音频 ====================

    /// <summary>声明音频轨道 (aac-lc, asc 为 esds 的 decoderSpecificInfo)。</summary>
    public void BeginAudio(int sampleRate, int channels, byte[] asc, long firstAudioPts100ns)
    {
        _audioBegun = true;
        _audioRate = sampleRate;
        _audioChannels = channels;
        _audioAsc = asc;
        _firstAudioPts = firstAudioPts100ns;
    }

    /// <summary>追加一个 AAC 帧 (固定 1024 样本)。</summary>
    public void AddAudioSample(byte[] data, int len)
    {
        _aData!.Write(data, 0, len);
        _aSizes.Add(len);
        _aSampleCount++;
    }

    // ==================== 收尾 ====================

    /// <summary>
    /// 装配最终文件: ftyp + mdatV + mdatA + moov, 删除临时文件。
    /// clli 非空 (HDR) 时写 clli box。
    /// </summary>
    public void FinalizeFile((ushort MaxCLL, ushort MaxFALL)? clli)
    {
        _vData.Flush();
        _aData?.Flush();
        long vLen = _vData.Length;
        long aLen = _aData?.Length ?? 0;

        var ftyp = BuildFtyp();
        long mdatVStart = ftyp.Length;
        long mdatAStart = mdatVStart + 8 + vLen;

        var moov = BuildMoov(mdatVStart + 8, _audioBegun ? mdatAStart + 8 : 0, clli);

        using (
            var f = new FileStream(
                _targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1 << 20
            )
        )
        {
            f.Write(ftyp, 0, ftyp.Length);
            CopyAsMdat(f, _vData, vLen);
            if (_audioBegun)
            {
                CopyAsMdat(f, _aData!, aLen);
            }
            f.Write(moov, 0, moov.Length);
            f.Flush();
        }

        _vData.Dispose();
        _aData?.Dispose();
        _disposed = true;
        TryDeleteTemp(
            Path.GetDirectoryName(_targetPath)!,
            Path.GetFileNameWithoutExtension(_targetPath)
        );
    }

    /// <summary>未 FinalizeFile（录制失败/取消）时清临时文件；幂等。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _vData.Dispose();
        _aData?.Dispose();
        TryDeleteTemp(
            Path.GetDirectoryName(_targetPath)!,
            Path.GetFileNameWithoutExtension(_targetPath)
        );
    }

    private static void TryDeleteTemp(string dir, string name)
    {
        foreach (var ext in new[] { ".v.tmp", ".a.tmp" })
        {
            string p = Path.Combine(dir, $"~{name}{ext}");
            try
            {
                File.Delete(p);
            }
            catch
            {
                // 清理失败不阻塞主流程 (下次启动会覆盖)
            }
        }
    }

    private static byte[] BuildFtyp()
    {
        var w = new BeWriter(new MemoryStream());
        WriteBox(
            w,
            "ftyp",
            b =>
            {
                b.String4("isom");
                b.U32(0x200);
                b.String4("isom");
                b.String4("iso2");
                b.String4("avc1");
                b.String4("mp41");
                b.String4("hvc1");
            }
        );
        return w.FlushToArray();
    }

    /// <summary>把数据文件整体写成一个 mdat box (size 已知, 直接写对)。</summary>
    private static void CopyAsMdat(FileStream f, FileStream data, long len)
    {
        var w = new BeWriter(f);
        if (len + 8 > 0xFFFFFFFFL)
        {
            // 超大文件: size=1 + 8 字节 largesize (stco 仍是 32 位, 文件整体仍受 4GB 限制)
            w.U32(1);
            w.String4("mdat");
            w.U64((ulong)(len + 8 + 8)); // largesize
        }
        else
        {
            w.U32((int)(len + 8));
            w.String4("mdat");
        }
        data.Seek(0, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        long left = len;
        while (left > 0)
        {
            int n = (int)Math.Min(buf.Length, left);
            data.ReadExactly(buf, 0, n);
            f.Write(buf, 0, n);
            left -= n;
        }
    }

    // ==================== moov ====================

    private byte[] BuildMoov(long vDataOffset, long aDataOffset, (ushort, ushort)? clli)
    {
        // 时长
        long vDur100ns = 0;
        if (_vSampleCount > 0)
        {
            for (int i = 1; i < _vSampleCount; i++)
            {
                vDur100ns += _vPts[i] - _vPts[i - 1];
            }
            vDur100ns += NominalVideoDur100ns; // 末帧补一个标称时长
        }
        long aDurUs = _audioBegun ? (long)_aSampleCount * 1024 * 1_000_000L / _audioRate : 0;
        // 音频起始偏移 (WASAPI 首包延迟) 也计入 movie 时长: 音频轨经 elst 后结束于 aStart + aDur
        long aStartUs = _audioBegun ? Math.Max(0, _firstAudioPts - _firstVideoPts) / 10 : 0;
        long mvDurUs = Math.Max(vDur100ns / 10, aDurUs + aStartUs);

        var w = new BeWriter(new MemoryStream());
        WriteBox(
            w,
            "moov",
            bw =>
            {
                WriteBox(
                    bw,
                    "mvhd",
                    x =>
                    {
                        // mvhd v0 布局严格对齐 FFmpeg mov_read_mvhd (QuickTime/Apple 风格, 100 字节 body / 108 字节 box):
                        // version(1)+flags(3) | ctime(4) | mtime(4) | timescale(4) | duration(4) | rate(4) |
                        // volume(2) | reserved(10) | matrix(36) | 7xU32(28): preview_time/preview_duration/
                        // poster_time/selection_time/selection_duration/current_time/next_track_ID。
                        // 注意: reserved 必须是 10 字节 (FFmpeg 用 avio_skip(pb,10)); 若只写 2 字节会使 box 变 96/100,
                        // FFmpeg 按 100 字节读会 overread 8 字节, 导致后续 trak/stbl/hvcC 全部错位、SPS/PPS 解析失败。
                        x.U32(0); // version 0 + flags 0
                        x.U32(0); // ctime
                        x.U32(0); // mtime
                        x.U32(1_000_000); // timescale
                        x.U32((int)mvDurUs); // duration
                        x.U32(0x00010000); // rate
                        x.U16(0x0100); // volume
                        x.Bytes(new byte[10]); // reserved (FFmpeg avio_skip 10)
                        x.Bytes(Matrix()); // 单位矩阵 36B
                        x.Bytes(new byte[24]); // preview_time..current_time = 6 个 0
                        x.U32(3); // next_track_ID (video=1, audio=2, 下一个可用=3)
                    }
                );

                // 视频 trak
                WriteBox(
                    bw,
                    "trak",
                    x =>
                    {
                        WriteBox(
                            x,
                            "tkhd",
                            t =>
                            {
                                t.U32(0x000007); // version 0, flags: enabled | inMovie | inPreview
                                t.U32(0); // ctime
                                t.U32(0); // mtime
                                t.U32(1); // track_ID
                                t.U32(0);
                                t.U32((int)(vDur100ns / 10));
                                t.Bytes(new byte[8]);
                                t.U16(0); // layer
                                t.U16(0); // alternate_group
                                t.U16(0); // volume (video)
                                t.Bytes(new byte[2]);
                                t.Bytes(Matrix());
                                // tkhd v0 宽高为 16.16 定点 (各 4 字节, 不能写 U16)
                                t.U32(_width << 16);
                                t.U32(_height << 16);
                            }
                        );

                        WriteBox(
                            x,
                            "mdia",
                            m =>
                            {
                                WriteBox(
                                    m,
                                    "mdhd",
                                    d =>
                                    {
                                        // mdhd v0 (32 字节 box, 与 FFmpeg mov_write_mdhd_tag 一致):
                                        // ver/flags(4) ctime(4) mtime(4) timescale(4) duration(4) language(2) quality(2)
                                        d.U32(0); // version 0 + flags 0
                                        d.U32(0); // ctime
                                        d.U32(0); // mtime
                                        d.U32(VideoTimescale);
                                        d.U32((int)vDur100ns);
                                        d.U16(0x55C4); // language 'und'
                                        d.U16(0); // quality (reserved)
                                    }
                                );

                                WriteBox(
                                    m,
                                    "hdlr",
                                    h =>
                                    {
                                        h.U32(0);
                                        h.U32(0); // pre_defined
                                        h.String4("vide");
                                        h.Bytes(new byte[12]);
                                    }
                                );

                                WriteBox(
                                    m,
                                    "minf",
                                    mi =>
                                    {
                                        WriteBox(
                                            mi,
                                            "vmhd",
                                            vm =>
                                            {
                                                vm.U32(1); // version 0, flags 1
                                                vm.U16(0); // graphicsmode
                                                vm.Bytes(new byte[4]); // opcolor
                                            }
                                        );

                                        WriteDref(mi);

                                        WriteBox(
                                            mi,
                                            "stbl",
                                            st =>
                                            {
                                                WriteBox(
                                                    st,
                                                    "stsd",
                                                    sd =>
                                                    {
                                                        sd.U32(0);
                                                        sd.U32(1); // entry_count
                                                        if (_hdr)
                                                        {
                                                            WriteHevcSampleEntry(sd, clli);
                                                        }
                                                        else
                                                        {
                                                            WriteAvcSampleEntry(sd);
                                                        }
                                                    }
                                                );

                                                WriteStts(st);

                                                bool allSync = _vSync.Count == _vSampleCount;
                                                if (!allSync && _vSync.Count > 0)
                                                {
                                                    WriteBox(
                                                        st,
                                                        "stss",
                                                        ss =>
                                                        {
                                                            ss.U32(0);
                                                            ss.U32(_vSync.Count);
                                                            foreach (int n in _vSync)
                                                            {
                                                                ss.U32(n);
                                                            }
                                                        }
                                                    );
                                                }

                                                WriteBox(
                                                    st,
                                                    "stsc",
                                                    sc =>
                                                    {
                                                        sc.U32(0);
                                                        sc.U32(1); // 1 chunk
                                                        sc.U32(1); // first_chunk
                                                        sc.U32(_vSampleCount); // samples_per_chunk
                                                        sc.U32(1);
                                                    }
                                                );

                                                WriteStsz(st, _vSizes, _vSampleCount);
                                                WriteStco(st, vDataOffset);
                                            }
                                        );
                                    }
                                );
                            }
                        );
                    }
                );

                // 音频 trak
                if (_audioBegun)
                {
                    // 音频相对视频的起始偏移 (WASAPI 首包延迟), 保持 A/V 对齐:
                    // aStartTime 为音频时基 (采样数) 单位, 写进 edts/elst 的 media_time。
                    long aStart100 = Math.Max(0, _firstAudioPts - _firstVideoPts);
                    long aStartTime = aStart100 * (long)_audioRate / 10_000_000;
                    long aStartUs = aStart100 / 10; // 100ns -> us (movie 时基)

                    WriteBox(
                        bw,
                        "trak",
                        x =>
                        {
                            WriteBox(
                                x,
                                "tkhd",
                                t =>
                                {
                                    t.U32(0x000007);
                                    t.U32(0);
                                    t.U32(0);
                                    t.U32(2);
                                    t.U32(0);
                                    t.U32((int)(aDurUs + aStartUs)); // 含起始偏移的轨道时长
                                    t.Bytes(new byte[8]);
                                    t.U16(0);
                                    t.U16(0);
                                    t.U16(0x0100); // volume
                                    t.Bytes(new byte[2]);
                                    t.Bytes(Matrix());
                                    t.U32(0); // width (16.16, 音频为 0)
                                    t.U32(0); // height (16.16)
                                }
                            );

                            // edts/elst (v1): 把整段媒体从 t=media_time 开始播放,
                            // 即音频轨在 movie 时间轴上延迟 aStartTime, 与视频对齐。
                            // (FFmpeg 读取 edts 生成起始 DTS 偏移; 缺失时 A/V 会差一个首包延迟)
                            if (aStartTime > 0)
                            {
                                WriteBox(
                                    x,
                                    "edts",
                                    ed =>
                                    {
                                        WriteBox(
                                            ed,
                                            "elst",
                                            el =>
                                            {
                                                el.U32(0x01000000); // version 1 + flags 0
                                                el.U32(1); // entry_count
                                                el.U64((ulong)aDurUs); // segment_duration (movie 时基 = us)
                                                el.U64((ulong)aStartTime); // media_time (媒体时基 = 采样数)
                                                el.U16(0x0100); // media_rate 1.0 (16.16)
                                                el.U16(0); // reserved (v1)
                                            }
                                        );
                                    }
                                );
                            }

                            WriteBox(
                                x,
                                "mdia",
                                m =>
                                {
                                    WriteBox(
                                        m,
                                        "mdhd",
                                        d =>
                                        {
                                            // mdhd v0 (32 字节 box, 与 FFmpeg mov_write_mdhd_tag 一致):
                                            // ver/flags(4) ctime(4) mtime(4) timescale(4) duration(4) language(2) quality(2)
                                            d.U32(0); // version 0 + flags 0
                                            d.U32(0); // ctime
                                            d.U32(0); // mtime
                                            d.U32(_audioRate);
                                            d.U32(_aSampleCount * 1024);
                                            d.U16(0x55C4); // language 'und'
                                            d.U16(0); // quality (reserved)
                                        }
                                    );

                                    WriteBox(
                                        m,
                                        "hdlr",
                                        h =>
                                        {
                                            h.U32(0);
                                            h.U32(0);
                                            h.String4("soun");
                                            h.Bytes(new byte[12]);
                                        }
                                    );

                                    WriteBox(
                                        m,
                                        "minf",
                                        mi =>
                                        {
                                            WriteBox(
                                                mi,
                                                "smhd",
                                                sm =>
                                                {
                                                    sm.U32(0);
                                                    sm.U16(0); // balance
                                                    sm.U16(0); // reserved
                                                }
                                            );

                                            WriteDref(mi);

                                            WriteBox(
                                                mi,
                                                "stbl",
                                                st =>
                                                {
                                                    WriteBox(
                                                        st,
                                                        "stsd",
                                                        sd =>
                                                        {
                                                            sd.U32(0);
                                                            sd.U32(1);
                                                            WriteAacSampleEntry(sd);
                                                        }
                                                    );

                                                    // stts: 每帧 1024 样本 (faac 固定帧长)
                                                    WriteBox(
                                                        st,
                                                        "stts",
                                                        tt =>
                                                        {
                                                            tt.U32(0);
                                                            tt.U32(1);
                                                            tt.U32(_aSampleCount);
                                                            tt.U32(1024);
                                                        }
                                                    );

                                                    WriteBox(
                                                        st,
                                                        "stsc",
                                                        sc =>
                                                        {
                                                            sc.U32(0);
                                                            sc.U32(1);
                                                            sc.U32(1);
                                                            sc.U32(_aSampleCount);
                                                            sc.U32(1);
                                                        }
                                                    );

                                                    WriteStsz(st, _aSizes, _aSampleCount);
                                                    WriteStco(st, aDataOffset);
                                                }
                                            );
                                        }
                                    );
                                }
                            );
                        }
                    );
                }
            }
        );

        return w.FlushToArray();
    }

    /// <summary>末帧标称时长 (100ns), 由编码器帧率推算。</summary>
    private long NominalVideoDur100ns => 10_000_000L * _fpsDen / _fpsNum;

    private void WriteStts(BeWriter st)
    {
        // 帧间隔 RLE (bframes=0, pts 单调; 末帧用标称间隔)
        var runs = new List<(int Count, int Delta)>();
        for (int i = 0; i < _vSampleCount; i++)
        {
            long dur = i < _vSampleCount - 1 ? _vPts[i + 1] - _vPts[i] : NominalVideoDur100ns;
            if (dur < 0)
                dur = 0;
            int d = (int)Math.Min(int.MaxValue, dur);
            if (runs.Count > 0 && runs[^1].Delta == d)
            {
                runs[^1] = (runs[^1].Count + 1, d);
            }
            else
            {
                runs.Add((1, d));
            }
        }
        WriteBox(
            st,
            "stts",
            tt =>
            {
                tt.U32(0);
                tt.U32(runs.Count);
                foreach (var (c, d) in runs)
                {
                    tt.U32(c);
                    tt.U32(d);
                }
            }
        );
    }

    private static void WriteStsz(BeWriter st, List<int> sizes, int count)
    {
        WriteBox(
            st,
            "stsz",
            sz =>
            {
                sz.U32(0);
                sz.U32(0); // sample_size = 0 (变长)
                sz.U32(count);
                foreach (int s in sizes)
                {
                    sz.U32(s);
                }
            }
        );
    }

    private static void WriteStco(BeWriter st, long offset)
    {
        WriteBox(
            st,
            "stco",
            co =>
            {
                co.U32(0);
                co.U32(1); // 1 chunk
                co.U32((int)offset);
            }
        );
    }

    private static void WriteDref(BeWriter minf)
    {
        WriteBox(
            minf,
            "dref",
            d =>
            {
                d.U32(0);
                d.U32(1);
                WriteBox(
                    d,
                    "url ", // fourCC 必须 4 字符 (ISO BMFF 规范写作 'url ', 尾随空格)
                    u =>
                    {
                        u.U32(1); // version 0, flags: self-contained
                    }
                );
            }
        );
    }

    /// <summary>avc1 sample entry: 78 字节 VisualSampleEntry 基体 + avcC + colr。</summary>
    private void WriteAvcSampleEntry(BeWriter stsd)
    {
        var (sps, pps) = SplitH264Headers();
        WriteBox(
            stsd,
            "avc1",
            e =>
            {
                WriteSampleEntryBase(e);
                // avcC
                WriteBox(
                    e,
                    "avcC",
                    c =>
                    {
                        c.U8(1); // configurationVersion
                        c.U8(sps[1]); // profile
                        c.U8(sps[2]); // compatibility
                        c.U8(sps[3]); // level
                        // reserved(2)=11b + lengthSizeMinus1(4)=3 (4 字节 NAL 长度前缀)
                        c.U8(0xE3);
                        c.U8(0xE0 | 1); // reserved(3)=111b + numSequenceParameterSets=1
                        c.U16((ushort)sps.Length);
                        c.Bytes(sps);
                        c.U8(1);
                        c.U16((ushort)pps.Length);
                        c.Bytes(pps);
                    }
                );
                WriteNclx(e, 1, 1, 1, false); // SDR: BT.709 limited range
            }
        );
    }

    /// <summary>从 [len4][SPS][len4][PPS] 头部流拆出 SPS / PPS。</summary>
    private (byte[], byte[]) SplitH264Headers()
    {
        int off = 0;
        int nlen = Be32(_videoHeaders, ref off);
        var sps = _videoHeaders.AsSpan(off, nlen).ToArray();
        off += nlen;
        int nlen2 = Be32(_videoHeaders, ref off);
        var pps = _videoHeaders.AsSpan(off, nlen2).ToArray();
        return (sps, pps);
    }

    /// <summary>
    /// hvc1 sample entry: 基体 + hvcC (CTA-5004-A 布局, VPS/SPS/PPS 三个 array) + colr + clli。
    /// </summary>
    private void WriteHevcSampleEntry(BeWriter stsd, (ushort, ushort)? clli)
    {
        var vps = Array.Empty<byte>();
        var sps = Array.Empty<byte>();
        var pps = Array.Empty<byte>();
        int off = 0;
        while (off + 4 <= _videoHeaders.Length)
        {
            int nlen = Be32(_videoHeaders, ref off);
            if (nlen < 2 || off + nlen > _videoHeaders.Length)
                break;
            int type = (_videoHeaders[off] >> 1) & 0x3F;
            var nal = _videoHeaders.AsSpan(off, nlen).ToArray();
            off += nlen;
            switch (type)
            {
                case 32:
                    vps = nal;
                    break;
                case 33:
                    sps = nal;
                    break;
                case 34:
                    pps = nal;
                    break;
            }
        }

        // CEA-5004-A hvcC 通用 PTL (96 bit = 12 字节): [profile8][compat32][constraint48][level8]
        // 与 SPS 内 PTL 布局不同 (SPS 是 profile8|constraint32|level12), 须从 SPS 字段重建。
        var ptl = BuildHevcPtlCeA(sps);
        int bitDepthMinus8 = _hdr ? 2 : 0; // HDR 10-bit / SDR 8-bit
        const int chromaFormat = 1; // 4:2:0

        WriteBox(
            stsd,
            "hvc1",
            e =>
            {
                WriteSampleEntryBase(e);
                WriteBox(
                    e,
                    "hvcC",
                    c =>
                    {
                        c.U8(1); // configurationVersion = 1
                        c.Bytes(ptl); // general PTL (96 bit): profile|compat32|constraint48|level
                        c.U8(0xF0); // reserved4=0xF | min_spatial_segmentation_idc 高 4 位 (0)
                        c.U8(0x00); // min_spatial_segmentation_idc 低 8 位 (0)
                        c.U8(0xFC); // reserved6=0xFC | parallelism_present_flag (2)=0
                        c.U8((byte)(0xFC | chromaFormat)); // reserved6=0xFC | chroma_format_idc (2)=1
                        c.U8((byte)(0xF8 | bitDepthMinus8)); // reserved5=0xF8 | bit_depth_luma_minus8 (3)
                        c.U8((byte)(0xF8 | bitDepthMinus8)); // reserved5=0xF8 | bit_depth_chroma_minus8 (3)
                        c.U16(0); // avg_frame_rate (unspecified)
                        // constant_frame_rate(2)=0 | num_temporal_layers(3)=1
                        // | temporal_id_zero(1)=1 | length_minus1(2)=3
                        c.U8(0x0F);
                        c.U8(3); // num_of_arrays (VPS/SPS/PPS)
                        WriteHvcCArray(c, 32, vps);
                        WriteHvcCArray(c, 33, sps);
                        WriteHvcCArray(c, 34, pps);
                    }
                );
                WriteNclx(e, 9, 16, 9, true); // HDR: BT.2020 / PQ / 2020nc, 10-bit 全量程 (JPEG)
                if (clli is { } c)
                {
                    // clli is a FullBox (ISO 14496-12): [size]['clli'][version(8)][flags(24)][maxCLL(16)][maxFALL(16)]
                    // = 16 bytes total. version+flags must be present; without them standard parsers
                    // (VLC/PotPlayer/FFmpeg) misread the box (version=0x01, maxCLL out-of-range -> 0),
                    // so the tone-mapping hint is lost and the player falls back to a wrong/default
                    // maxCLL -> washed-out (gray) output.
                    WriteBox(
                        e,
                        "clli",
                        x =>
                        {
                            x.U32(0); // version(8)=0 + flags(24)=0
                            x.U16(c.Item1);
                            x.U16(c.Item2);
                        }
                    );
                }
            }
        );
    }

    /// <summary>
    /// hvcC NAL array (与 ffmpeg ff_isom_write_hvcc 布局一致, 14496-15):
    /// [array_completeness(1)=1 | reserved(1)=0 | NAL_unit_type(6)][numNalus(16)][每个 NAL: len16 + NAL]。
    /// 注意: 没有独立的 reserved 字节, 每个 NAL 前各带一个 U16 长度 (不含 4 字节流前缀)。
    /// </summary>
    private static void WriteHvcCArray(BeWriter c, int nalType, byte[] nal)
    {
        c.U8((byte)(0x80 | (nalType & 0x3F))); // completeness(1)=1 | reserved(1)=0 | NAL_unit_type(6)
        c.U16(1); // numNalus
        c.U16((ushort)nal.Length); // 该 NAL 长度 (不含长度前缀)
        c.Bytes(nal);
    }

    /// <summary>
    /// NAL → 干净 RBSP: 剥 2 字节 NAL 头, 剔除防码字插入字节 (00 00 03 中的 03)。
    /// SPS 时 rb[0]=vps_id(4)|max_sub_layers(3)|nesting(1), rb[1..]=PTL 起。
    /// </summary>
    private static List<byte> HevcRbsp(byte[] nal)
    {
        var rb = new List<byte>();
        int zeros = 0;
        for (int i = 2; i < nal.Length; i++)
        {
            byte b = nal[i];
            if (b <= 0x03 && zeros >= 2)
            {
                zeros = 0;
                continue; // 防码字插入字节, 剔除
            }
            rb.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return rb;
    }

    /// <summary>
    /// 从 SPS 取 CEA-5004-A hvcC 通用 PTL (12 字节)。
    /// SPS 内 PTL (general 部分, 96 bit) 与 CEA hvcC 通用 PTL 布局完全一致:
    /// [profile_space2|tier1|profile_idc5][compat32][constraint48][level8]。
    /// 因此直接整段拷贝 SPS RBSP 第 1..12 字节 (前面是 vps_id4+max_sub3+nesting1 共 1 字节)。
    /// level 原样保留 (与 FFmpeg ff_isom_write_hvcc 一致, 实测 0x99 = L15.3 在 HEVC 合法
    /// level_idc 范围内, 解码器只看 SPS 里的 level, hvcC 此处仅描述性字段)。
    /// </summary>
    private static byte[] BuildHevcPtlCeA(byte[] spsNal)
    {
        var rb = HevcRbsp(spsNal);
        var ptl = new byte[12];
        for (int i = 0; i < 12; i++)
            ptl[i] = rb.Count > 1 + i ? rb[1 + i] : (byte)0;
        return ptl;
    }

    private static string Hex(ReadOnlySpan<byte> b)
    {
        var s = new System.Text.StringBuilder(b.Length * 3);
        foreach (byte x in b)
            s.Append(x.ToString("X2")).Append(' ');
        return s.ToString().TrimEnd();
    }

    private static void WriteNclx(
        BeWriter parent,
        int colorPrimaries,
        int transfer,
        int matrix,
        bool fullRange
    )
    {
        // colr (19 字节, 与 FFmpeg movenc mov_write_colr_tag 逐字节一致):
        // "colr" + colour_type "nclx" + primaries/transfer/matrix (各 16 位) + reserved(1)。
        // 最后 1 字节 = (full_range ? 0x80 : 0x00) (FFmpeg: avio_w8(full_range << 7));
        // FFmpeg 的 mov_read_colr 对 nclx 会读这 1 字节, 缺失会 "overread end of atom 'colr'"。
        WriteBox(
            parent,
            "colr",
            c =>
            {
                c.String4("nclx");
                c.U16((ushort)colorPrimaries);
                c.U16((ushort)transfer);
                c.U16((ushort)matrix);
                c.U8(fullRange ? (byte)0x80 : (byte)0x00); // reserved 高位 = 全量程 (JPEG range)
            }
        );
    }

    private void WriteAacSampleEntry(BeWriter stsd)
    {
        WriteBox(
            stsd,
            "mp4a",
            e =>
            {
                // AudioSampleEntry 固定基体 (28 字节), 布局逐一对齐 FFmpeg movenc
                // (mov_write_audio_tag, mp4 version=0): FFmpeg 的 mov_parse_stsd_audio
                // 按固定偏移读 channels (E+24) / samplerate (E+32 高 16 位),
                // esds 必须紧跟在 28 字节基体之后 (E+36), 偏移错了 esds 解析直接错位。
                e.U32(0); // reserved
                e.U16(0); // reserved
                e.U16(1); // data_reference_index
                e.U16(0); // version
                e.U16(0); // revision level
                e.U32(0); // reserved
                e.U16((ushort)_audioChannels); // channelcount
                e.U16(16); // samplesize
                e.U16(0); // reserved (compression ID)
                e.U16(0); // packet size
                e.U16((ushort)_audioRate); // samplerate (16.16 定点高 16 位, FFmpeg 写 U16)
                e.U16(0); // reserved
                // esds (与 FFmpeg mov_write_esds_tag 布局一致)
                WriteBox(
                    e,
                    "esds",
                    x =>
                    {
                        x.U32(0); // version + flags (FFmpeg 读 esds 的第一个 U32, 缺失则整体错位)
                        int ascLen = _audioAsc.Length;
                        // DecoderConfig: objType(1)+streamType(1)+buffer(3)+maxBitrate(4)+avgBitrate(4)
                        // 内嵌 DecSpecificInfo (0x05: tag+len+ASC), 与 FFmpeg 写法一致
                        int decConfigLen = 13 + 2 + ascLen;
                        // ES descriptor: ES_ID(2)+flags(1) + 0x04 desc(2+decConfigLen) + SL desc(2+1)
                        int esLen = 3 + (2 + decConfigLen) + 3;
                        x.U8(0x03); // ES_Descriptor
                        x.U8((byte)esLen);
                        x.U16(0); // ES_ID
                        x.U8(0x00); // flags
                        x.U8(0x04); // DecoderConfig descriptor
                        x.U8((byte)decConfigLen);
                        x.U8(0x15); // object type: AAC ADTS (ff_mp4_obj_type: AAC = 0x15)
                        x.U8(0x15); // stream type: audio
                        x.Bytes(new byte[3]); // maxBufferSizeDB
                        x.U32(0); // maxBitrate
                        x.U32(0); // avgBitrate
                        x.U8(0x05); // DecSpecificInfo descriptor (AAC ASC)
                        x.U8((byte)ascLen);
                        x.Bytes(_audioAsc);
                        x.U8(0x06); // SL descriptor
                        x.U8(1);
                        x.U8(0x02);
                    }
                );
            }
        );
    }

    /// <summary>
    /// VisualSampleEntry 固定基体 (avc1 / hvc1 用, ISO 14496-12 8.5.2.2), 共 78 字节:
    /// reserved(4) reserved(2) data_reference_index(2) version(2) revision(2) pre_defined[3](12)
    /// + 宽高/分辨率/frame_count/compressorname(1+31)/depth(2)/pre_defined(2)。
    /// 字段顺序与字节布局逐一对齐 FFmpeg movenc (mov_write_video_tag), 因为 FFmpeg 的
    /// mov_parse_stsd_video 是按固定偏移读取的: 基体差 1 字节 width/height 就会错位
    /// (读成 72x0 -> "Picture size 0x0"), 子盒 (hvcC/colr) 也会从错误偏移开始遍历。
    /// </summary>
    private void WriteSampleEntryBase(BeWriter e)
    {
        e.U32(0); // reserved
        e.U16(0); // reserved (FFmpeg 读作 "reserved", dref 在其后)
        e.U16(1); // data_reference_index
        e.U16(0); // codec stream version
        e.U16(0); // revision level
        e.Bytes(new byte[12]); // pre_defined[3]
        e.U16(_width); // 像素计数 (16-bit 整数; 16.16 定点只在 tkhd 用)
        e.U16(_height);
        e.U32(0x00480000); // 72 dpi
        e.U32(0x00480000);
        e.U32(0); // data size (always 0)
        e.U16(1); // frame_count
        e.U8(0); // compressorname_size
        e.Bytes(new byte[31]); // compressorname
        e.U16(0x0018); // depth
        e.U16(0xFFFF); // pre_defined
    }

    private static byte[] Matrix()
    {
        // 单位矩阵 (16.16 fixed point, 大端)
        var m = new byte[36];
        WriteBe32(m, 0, 0x00010000);
        WriteBe32(m, 16, 0x00010000);
        WriteBe32(m, 32, 0x40000000);
        return m;
    }

    private static void WriteBe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    // ==================== 工具 ====================

    /// <summary>
    /// 写一个 box: [size4][type4][body]。size 先占位, 写完回补。
    /// </summary>
    private static void WriteBox(BeWriter w, string type, Action<BeWriter> body)
    {
        long sizePos = w.Position;
        w.U32(0);
        w.String4(type);
        body(w);
        long end = w.Position;
        int size = (int)(end - sizePos);
        w.Seek(sizePos);
        w.U32(size);
        w.Seek(end);
    }

    private static int Be32(byte[] data, ref int offset)
    {
        int v =
            (data[offset] << 24)
            | (data[offset + 1] << 16)
            | (data[offset + 2] << 8)
            | data[offset + 3];
        offset += 4;
        return v;
    }

    /// <summary>ISO BMFF 大端 writer (BinaryWriter 是小端, 不能直接用)。</summary>
    private sealed class BeWriter
    {
        private readonly Stream _s;
        private readonly MemoryStream? _ms;

        public BeWriter(Stream s)
        {
            _s = s;
            _ms = s as MemoryStream;
        }

        public long Position => _s.Position;

        public void Seek(long pos) => _s.Seek(pos, SeekOrigin.Begin);

        public void U8(byte v) => _s.WriteByte(v);

        // 参数放宽为 int (所有调用点都是 int / 常量字面量), 写入前截断
        public void U16(int v)
        {
            _s.WriteByte((byte)((v >> 8) & 0xFF));
            _s.WriteByte((byte)(v & 0xFF));
        }

        public void U24(int v)
        {
            _s.WriteByte((byte)((v >> 16) & 0xFF));
            _s.WriteByte((byte)((v >> 8) & 0xFF));
            _s.WriteByte((byte)(v & 0xFF));
        }

        public void U32(int v)
        {
            _s.WriteByte((byte)((v >> 24) & 0xFF));
            _s.WriteByte((byte)((v >> 16) & 0xFF));
            _s.WriteByte((byte)((v >> 8) & 0xFF));
            _s.WriteByte((byte)(v & 0xFF));
        }

        public void U64(ulong v)
        {
            for (int i = 56; i >= 0; i -= 8)
            {
                _s.WriteByte((byte)(v >> i));
            }
        }

        public void Bytes(byte[] data) => _s.Write(data, 0, data.Length);

        public void String4(string s) =>
            Bytes(new byte[] { (byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3] });

        public byte[] FlushToArray()
        {
            _s.Flush();
            return _ms?.ToArray() ?? throw new InvalidOperationException("not a memory stream");
        }
    }
}
