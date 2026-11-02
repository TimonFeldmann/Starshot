// CodecsSmoke — 编码器链路冒烟测试 (Starshot.Codecs 垫片 + x264/x265/faac)
//
// 用法: dotnet run --project tools/CodecsSmoke [codecsDir]
//   codecsDir 默认 = <repo>\build\codecs
//
// 验证内容:
//   1. ss_init 能加载三个 LGPL DLL
//   2. x264: 合成 NV12 帧 -> H.264 NAL (SPS/PPS 头 + 帧 + flush)
//   3. x265 10-bit: 合成 P010 帧 -> HEVC NAL (HDR10 参数)
//   4. faac: 合成 float32 正弦波 -> AAC 帧 + ASC
// 输出写入 <codecsDir>\smoke\*.bin 供人工/ffprobe 检查

using System.Runtime.InteropServices;

string codecsDir =
    args.Length > 0
        ? Path.GetFullPath(args[0])
        : Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "codecs")
        );
Console.WriteLine($"[smoke] codecs dir: {codecsDir}");

try
{
    // ---------- 0. 初始化 ----------
    Sh.Load(codecsDir);
    int rc = Sh.ss_init(
        Path.Combine(codecsDir, "libx264-164.dll"),
        Path.Combine(codecsDir, "libx265.dll"),
        Path.Combine(codecsDir, "faac-2.dll")
    );
    Check.Ok(rc, "ss_init");
    Console.WriteLine("[smoke] ss_init OK (x264/x265/faac loaded)");

    var smokeDir = Path.Combine(codecsDir, "smoke");
    Directory.CreateDirectory(smokeDir);

    // ---------- 1. x264 SDR: NV12 8-bit -> H.264 ----------
    Console.WriteLine();
    Console.WriteLine("=== x264 (NV12 8-bit -> H.264, 1280x720@30, CRF 23) ===");
    const int W = 1280,
        H = 720;
    IntPtr p4 = Check.Ptr(Sh.ss_x264_param_create(), "x264 param create");
    Check.Ok(Sh.ss_x264_preset(p4, "veryfast", "film"), "x264 preset");
    Check.Ok(Sh.ss_x264_parse(p4, "annexb", "0"), "x264 annexb=0 (MP4 length-prefixed NALs)");
    Sh.ss_x264_set_geometry(p4, W, H, 30, 1);
    Sh.ss_x264_set_color(p4, 1, 1, 1, 0); // BT.709 limited
    Sh.ss_x264_set_crf(p4, 23, 30, 3, 4);

    IntPtr e4 = Check.Ptr(Sh.ss_x264_open(p4), "x264 open");
    byte[] hdr4 = new byte[8192];
    Check.Ok(Sh.ss_x264_headers(e4, hdr4, hdr4.Length, out int hdr4Len), "x264 headers");
    // 布局应为 [len4][SPS][len4][PPS] (len4 为大端)
    Console.WriteLine(
        $"[x264] SPS/PPS raw: {hdr4Len} bytes, first 16: {BitConverter.ToString(hdr4.AsSpan(0, Math.Min(16, hdr4Len)).ToArray())}"
    );
    int spsLen = (hdr4[0] << 24 | hdr4[1] << 16 | hdr4[2] << 8 | hdr4[3]) & 0x7FFFFFFF;
    Console.WriteLine($"[x264] parsed spsLen={spsLen}, first NAL type byte=0x{hdr4[4]:X2}");

    // 合成 NV12: 移动竖条纹 + 渐变色度
    byte[] y = new byte[W * H];
    byte[] uv = new byte[W * H / 2];
    byte[] frame4 = new byte[4 * 1024 * 1024];
    int x264Bytes = 0,
        x264Frames = 0;
    const int Frames = 90;
    for (int f = 0; f < Frames; f++)
    {
        Gen.Nv12(y, uv, W, H, f);
        int r = Sh.ss_x264_encode(
            e4,
            y,
            W,
            uv,
            W,
            (long)(f * 10000000L / 30),
            frame4,
            frame4.Length,
            out int n
        );
        Check.Ok(r, $"x264 encode frame {f}");
        if (n > 0)
        {
            File.WriteAllBytes(
                Path.Combine(smokeDir, $"x264_f{x264Frames:03}.h264"),
                frame4.AsSpan(0, n).ToArray()
            );
            x264Bytes += n;
            x264Frames++;
        }
    }
    Check.Ok(Sh.ss_x264_flush(e4, frame4, frame4.Length, out int flN), "x264 flush");
    x264Bytes += flN;
    Sh.ss_x264_close(e4);
    Sh.ss_x264_param_free(p4);
    File.WriteAllBytes(
        Path.Combine(smokeDir, "x264_headers.bin"),
        hdr4.AsSpan(0, hdr4Len).ToArray()
    );
    Console.WriteLine(
        $"[x264] {Frames} frames -> {x264Frames} NAL buffers, {x264Bytes} bytes total, flush {flN} bytes"
    );

    // ---------- 2. x265 HDR: P010 10-bit -> HEVC ----------
    Console.WriteLine();
    Console.WriteLine("=== x265 (planar P010 -> HEVC HDR10, 1280x720@30, CRF 24) ===");
    IntPtr p5 = Check.Ptr(Sh.ss_x265_param_create(), "x265 param create");
    Check.Ok(Sh.ss_x265_preset(p5, "faster", "ssim"), "x265 preset");
    Check.Ok(Sh.ss_x265_parse(p5, "annexb", "0"), "x265 annexb=0 (MP4 length-prefixed NALs)");
    Sh.ss_x265_set_geometry(p5, W, H, 30, 1);
    Sh.ss_x265_set_hdr(p5, 10, 9, 16, 9); // 10-bit, BT.2020 / PQ / BT.2020nc
    Check.Ok(Sh.ss_x265_parse(p5, "level-idc", "5.1"), "x265 level");
    Sh.ss_x265_set_crf(p5, 24, 30, 3, 4);

    IntPtr e5 = Check.Ptr(Sh.ss_x265_open(p5), "x265 open");
    byte[] hdr5 = new byte[8192];
    Check.Ok(
        Sh.ss_x265_headers(e5, hdr5, hdr5.Length, out int hdr5Len, out int hdr5Nals),
        "x265 headers"
    );
    Console.WriteLine($"[x265] header NALs: {hdr5Len} bytes ({hdr5Nals} nals):");
    {
        int off = 0;
        for (int i = 0; i < hdr5Nals && off + 4 <= hdr5Len; i++)
        {
            int nlen = hdr5[off] << 24 | hdr5[off + 1] << 16 | hdr5[off + 2] << 8 | hdr5[off + 3];
            int type = (hdr5[off + 4] >> 1) & 0x3F; // NAL 类型 (32=VPS 33=SPS 34=PPS 39=SEI 19=IDR)
            string name = type switch
            {
                32 => "VPS",
                33 => "SPS",
                34 => "PPS",
                39 => "SEI",
                19 => "IDR",
                _ => "?",
            };
            Console.WriteLine($"  nal{i}: len={nlen} type={type} ({name})");
            off += 4 + nlen; // [len4][NAL] 布局
        }
    }

    byte[] y16 = new byte[W * H * 2];
    byte[] u16 = new byte[W / 2 * H / 2 * 2];
    byte[] v16 = new byte[W / 2 * H / 2 * 2];
    byte[] frame5 = new byte[4 * 1024 * 1024];
    int x265Bytes = 0,
        x265Frames = 0;
    for (int f = 0; f < Frames; f++)
    {
        Gen.P010(y16, u16, v16, W, H, f);
        int r = Sh.ss_x265_encode(
            e5,
            y16,
            W * 2,
            u16,
            W,
            v16,
            W,
            (long)(f * 10000000L / 30),
            frame5,
            frame5.Length,
            out int n
        );
        Check.Ok(r, $"x265 encode frame {f}");
        if (n > 0)
        {
            File.WriteAllBytes(
                Path.Combine(smokeDir, $"x265_f{x265Frames:03}.hevc"),
                frame5.AsSpan(0, n).ToArray()
            );
            x265Bytes += n;
            x265Frames++;
        }
    }
    Check.Ok(Sh.ss_x265_flush(e5, frame5, frame5.Length, out int fl5N), "x265 flush");
    x265Bytes += fl5N;
    Sh.ss_x265_close(e5);
    Sh.ss_x265_param_free(p5);
    File.WriteAllBytes(
        Path.Combine(smokeDir, "x265_headers.bin"),
        hdr5.AsSpan(0, hdr5Len).ToArray()
    );
    Console.WriteLine(
        $"[x265] {Frames} frames -> {x265Frames} NAL buffers, {x265Bytes} bytes total, flush {fl5N} bytes"
    );

    // ---------- 3. faac: float32 -> AAC-LC ----------
    Console.WriteLine();
    Console.WriteLine("=== faac (48kHz stereo float32 -> AAC-LC 96kbps/ch) ===");
    IntPtr ea = Check.Ptr(
        Sh.ss_faac_open(48000, 2, 96000, 100, out uint faacFrame, out uint faacMaxOut),
        "faac open"
    );
    Console.WriteLine($"[faac] frameSamples={faacFrame} maxOutBytes={faacMaxOut}");
    byte[] asc = new byte[64];
    Check.Ok(Sh.ss_faac_asc(ea, asc, asc.Length, out int ascLen), "faac asc");
    Console.WriteLine($"[faac] ASC: {ascLen} bytes, aot={(asc[0] >> 3) & 0x1F} (2=LC)");

    int sr = 48000,
        secs = 3,
        ch = 2;
    float[] pcm = new float[sr * secs * ch];
    for (int i = 0; i < pcm.Length / ch; i++)
    {
        float s = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * i / sr));
        pcm[i * ch] = s;
        pcm[i * ch + 1] = s;
    }
    byte[] aacOut = new byte[Math.Max(1, (int)faacMaxOut)];
    int aacBytes = 0,
        aacFrames = 0;
    for (int off = 0; off < pcm.Length / ch; off += (int)faacFrame)
    {
        int n = Math.Min((int)faacFrame, pcm.Length / ch - off);
        int w = Sh.ss_faac_encode(
            ea,
            pcm,
            (uint)(n * ch),
            aacOut,
            (uint)aacOut.Length,
            out uint written
        );
        Check.Ok(w, "faac encode");
        if (written > 0)
        {
            File.WriteAllBytes(
                Path.Combine(smokeDir, $"aac_f{aacFrames:03}.aac"),
                aacOut.AsSpan(0, (int)written).ToArray()
            );
            aacBytes += (int)written;
            aacFrames++;
        }
    }
    while (true)
    {
        int w = Sh.ss_faac_encode(ea, null, 0, aacOut, (uint)aacOut.Length, out uint written);
        Check.Ok(w, "faac flush");
        if (written == 0)
            break;
        File.WriteAllBytes(
            Path.Combine(smokeDir, $"aac_f{aacFrames:03}.aac"),
            aacOut.AsSpan(0, (int)written).ToArray()
        );
        aacBytes += (int)written;
        aacFrames++;
    }
    Sh.ss_faac_close(ea);
    int expectFrames = (int)Math.Ceiling((double)(sr * secs) / faacFrame);
    Console.WriteLine(
        $"[faac] {secs}s -> {aacFrames} frames (expect ~{expectFrames}), {aacBytes} bytes"
    );

    Console.WriteLine();
    Console.WriteLine($"[smoke] all outputs in {smokeDir}");
}
finally
{
    try
    {
        Sh.ss_shutdown();
    }
    catch { }
}
return 0;

// ---------- 垫片 P/Invoke ----------
static class Sh
{
    const string Lib = "Starshot.Codecs.dll";

    // 预加载垫片 DLL (让后续 P/Invoke 生效); 编码器 DLL 由 ss_init 内部加载
    public static void Load(string dir) => NativeLibrary.Load(Path.Combine(dir, Lib));

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ss_init(string x264Path, string x265Path, string faacPath);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_shutdown();

    // x264
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ss_x264_param_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x264_param_free(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ss_x264_preset(IntPtr h, string preset, string tune);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ss_x264_parse(IntPtr h, string name, string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x264_set_geometry(IntPtr s, int w, int h, int fpsNum, int fpsDen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x264_set_color(IntPtr h, int cp, int tf, int mx, int fr);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x264_set_crf(
        IntPtr h,
        double crf,
        int keyint,
        int bframes,
        int threads
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ss_x264_open(IntPtr param);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_x264_headers(IntPtr h, byte[] buf, int cap, out int bytes);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_x264_encode(
        IntPtr h,
        byte[] y,
        int strideY,
        byte[] uv,
        int strideUV,
        long pts,
        byte[] buf,
        int cap,
        out int bytes
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_x264_flush(IntPtr h, byte[] buf, int cap, out int bytes);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x264_close(IntPtr h);

    // x265
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ss_x265_param_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x265_param_free(IntPtr h);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ss_x265_preset(IntPtr h, string preset, string tune);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int ss_x265_parse(IntPtr h, string name, string value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x265_set_geometry(IntPtr s, int w, int h, int fpsNum, int fpsDen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x265_set_hdr(IntPtr h, int depth, int cp, int tf, int mx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x265_set_crf(
        IntPtr h,
        double crf,
        int keyint,
        int bframes,
        int threads
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ss_x265_open(IntPtr param);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_x265_headers(
        IntPtr h,
        byte[] buf,
        int cap,
        out int bytes,
        out int numNals
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_x265_encode(
        IntPtr h,
        byte[] y,
        int sy,
        byte[] u,
        int su,
        byte[] v,
        int sv,
        long pts,
        byte[] buf,
        int cap,
        out int bytes
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_x265_flush(IntPtr h, byte[] buf, int cap, out int bytes);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_x265_close(IntPtr h);

    // faac
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ss_faac_open(
        int sr,
        int ch,
        int br,
        int q,
        out uint frameSamples,
        out uint maxOut
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_faac_asc(IntPtr h, byte[] buf, int cap, out int len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ss_faac_encode(
        IntPtr h,
        float[] inBuf,
        uint samples,
        byte[] outBuf,
        uint cap,
        out uint written
    );

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ss_faac_close(IntPtr h);
}

static class Check
{
    public static IntPtr Ptr(IntPtr p, string what)
    {
        if (p == IntPtr.Zero)
            throw new Exception($"failed: {what}");
        return p;
    }

    public static void Ok(int rc, string what)
    {
        if (rc < 0)
            throw new Exception($"failed ({rc}): {what}");
    }
}

// 合成 NV12 / P010 测试帧
static class Gen
{
    // 合成 NV12: 移动竖条纹 (luma) + 慢变色度
    public static void Nv12(byte[] y, byte[] uv, int w, int h, int frame)
    {
        // luma 平面
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
            y[row * w + col] = (byte)((col + frame * 8) & 0xFF);
        // NV12 色度平面: chh=h/2 行, 每行 w 字节 (chw=w/2 个 UV 对, 各 2 字节)
        int chw = w / 2,
            chh = h / 2;
        for (int cr = 0; cr < chh; cr++)
        for (int cc = 0; cc < chw; cc++)
        {
            int i = cr * w + cc * 2;
            uv[i] = (byte)(128 + ((cr / 8 + frame) % 64) - 32); // U
            uv[i + 1] = (byte)(128 + ((cc / 8 + frame) % 64) - 32); // V
        }
    }

    // 合成 P010 (planar 10-bit, 16-bit 容器): 移动渐变 luma + 中灰色度
    public static void P010(byte[] y, byte[] u, byte[] v, int w, int h, int frame)
    {
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                int idx = (row * w + col) * 2;
                int lum = (col + frame * 16) & 1023;
                y[idx] = (byte)(lum & 0xFF);
                y[idx + 1] = (byte)(lum >> 8);
            }
        }
        int cw = w / 2,
            chh = h / 2;
        for (int row = 0; row < chh; row++)
        {
            for (int col = 0; col < cw; col++)
            {
                int idx = (row * cw + col) * 2;
                const int mid = 512; // 中灰色度 (10-bit)
                u[idx] = (byte)(mid & 0xFF);
                u[idx + 1] = (byte)(mid >> 8);
                v[idx] = (byte)(mid & 0xFF);
                v[idx + 1] = (byte)(mid >> 8);
            }
        }
    }
}
