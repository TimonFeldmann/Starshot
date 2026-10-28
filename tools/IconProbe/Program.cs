using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

// ScreenshotEditWindow 工具栏图标预览：path data 与 XAML 同源（16x16 网格），
// 在浅/深两种底上按 4x（细节）与 1x（工具栏实际尺寸）渲染并导出 PNG 目检；
// 模糊图标同时给出「合成高斯模糊」与「径向渐变填充」两种方案对照。
// 注：本 Win2D 投影（Starward.Win2D）没有公开的 CanvasPathGeometry/Build()，
// 几何用 CanvasPathBuilder 直接画；描边颜色与宽度走 DrawGeometry 参数（CanvasStrokeStyle 只管 cap/join/dash）。
// 运行：dotnet run → 输出到 out/*.png
internal static class Program
{
    private static readonly CanvasDevice Device = CanvasDevice.GetSharedDevice();
    private static readonly DirectXPixelFormat Fmt = DirectXPixelFormat.R8G8B8A8UIntNormalized;

    // ---- 图标 path data（16x16 网格，与 ScreenshotEditWindow.xaml 同源；仅用 M/L/H/V/Z，避开本 Win2D 损坏的 Q 段/填充）----
    private const string BlurSquareData =
        "M5.5,2 L10.5,2 L14,5.5 L14,10.5 L10.5,14 L5.5,14 L2,10.5 L2,5.5 Z";
    private const string HighlightBodyData =
        "M12.1,2.1 L13.9,3.9 L8.9,8.9 L6.6,11.6 L4.4,9.4 L7.1,7.1 Z M7.1,7.1 L8.9,8.9";
    private const string HighlightSwipeData = "M3.2,13 L7.2,13";
    private const string EraseHeadData = "M2.6,10 L5.1,7.5 L8.5,10.9 L6,13.4 Z";
    private const string EraseBodyData = "M2.6,10 L10,2.6 L13.4,6 L6,13.4 Z M5.1,7.5 L8.5,10.9";
    private const string SelectArrowData = "M2.4,2.5 L7,14 L8.4,9.4 L13,8 Z";

    private static int Main()
    {
        RenderSheet(4f, "out/icons-4x.png");
        RenderSheet(1f, "out/icons-1x.png");
        RenderBlurVariants();
        return 0;
    }

    // 4 行（模糊/高亮/擦除/选择）× 2 列（浅底/深底），图标 16s 居中
    private static void RenderSheet(float s, string path)
    {
        int pad = Math.Max(8, (int)(4 * s));
        int icon = (int)(16 * s);
        int cell = icon + 2 * pad;
        int w = 2 * cell;
        int h = 4 * cell;
        var rt = new CanvasRenderTarget(Device, w, h, 96, Fmt, CanvasAlphaMode.Premultiplied);
        using (var ds = rt.CreateDrawingSession())
        {
            for (int col = 0; col < 2; col++)
            {
                Color bg =
                    col == 0
                        ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
                Color ink =
                    col == 0
                        ? Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C)
                        : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                ds.FillRectangle(new Rect(col * cell, 0, cell, h), bg);
                for (int row = 0; row < 4; row++)
                {
                    var pos = new Vector2(
                        col * cell + (cell - icon) / 2f,
                        row * cell + (cell - icon) / 2f
                    );
                    switch (row)
                    {
                        case 0:
                            DrawBlur(ds, pos, s, ink);
                            break;
                        case 1:
                            DrawHighlight(ds, pos, s, ink);
                            break;
                        case 2:
                            DrawErase(ds, pos, s, ink);
                            break;
                        case 3:
                            DrawSelect(ds, pos, s, ink);
                            break;
                    }
                }
            }
        }
        SavePng(path, w, h, rt.GetPixelBytes());
        Console.WriteLine($"wrote {path} ({w}x{h})");
        int yEdge = Math.Max(1, cell / 2 - (int)(2 * s));
        PrintPx(
            rt,
            w,
            path,
            (pad / 2, pad / 2),
            (cell / 2, cell / 2),
            (cell / 2, yEdge),
            (cell + cell / 2, cell / 2)
        );
    }

    // 行序：0 模糊 1 高亮 2 擦除 3 选择
    private static void DrawBlur(CanvasDrawingSession ds, Vector2 pos, float s, Color ink)
    {
        // 本 Win2D 填充/模糊均损坏（见 variants 诊断），此处只验形状：画外轮廓
        var pen = MakePen();
        ds.DrawGeometry(Geom(BlurSquareData, s), pos, ink, 1.5f * s, pen);
    }

    private static void DrawHighlight(CanvasDrawingSession ds, Vector2 pos, float s, Color ink)
    {
        var pen = MakePen();
        ds.DrawGeometry(Geom(HighlightBodyData, s), pos, ink, 1.5f * s, pen);
        var swipePen = MakePen();
        ds.DrawGeometry(Geom(HighlightSwipeData, s), pos, ink, 2f * s, swipePen);
    }

    private static void DrawErase(CanvasDrawingSession ds, Vector2 pos, float s, Color ink)
    {
        var soft = Color.FromArgb(0xFF, 0x55, 0x55, 0x55);
        ds.DrawGeometry(Geom(EraseHeadData, s), pos, soft);
        var pen = MakePen();
        ds.DrawGeometry(Geom(EraseBodyData, s), pos, ink, 1.5f * s, pen);
    }

    private static void DrawSelect(CanvasDrawingSession ds, Vector2 pos, float s, Color ink)
    {
        var geom = Geom(SelectArrowData, s);
        ds.DrawGeometry(geom, pos, ink);
        var pen = MakePen();
        ds.DrawGeometry(geom, pos, ink, 1f * s, pen);
    }

    private static CanvasStrokeStyle MakePen()
    {
        return new CanvasStrokeStyle
        {
            LineJoin = CanvasLineJoin.Round,
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
        };
    }

    // 模糊图标方案对照 + 填充能力诊断（本 Win2D 疑似只支持描边不支持填充）：
    // 1 = 内建矩形纯色填充  2 = CreatePath 纯色填充  3 = 内建圆角矩形+渐变  4 = 高斯模糊
    private static void RenderBlurVariants()
    {
        const float s = 4f;
        const int cell = 96;
        int w = 4 * cell;
        int h = cell;
        var rt = new CanvasRenderTarget(Device, w, h, 96, Fmt, CanvasAlphaMode.Premultiplied);
        using (var ds = rt.CreateDrawingSession())
        {
            Color ink = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C);
            ds.FillRectangle(new Rect(0, 0, w, h), Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            var pos = new Vector2((cell - 64) / 2f, (h - 64) / 2f);
            var lSquare = "M5,2 L11,2 L14,5 L14,11 L11,14 L5,14 L2,11 L2,5 Z";
            // 1：内建矩形（不走 path builder）纯色填充
            var rect = CanvasGeometry.CreateRectangle(Device, 2f * s, 2f * s, 12f * s, 12f * s);
            ds.DrawGeometry(rect, new Vector2(pos.X, pos.Y), ink);
            // 2：CreatePath 纯色填充
            ds.DrawGeometry(Geom(lSquare, s), new Vector2(cell + pos.X, pos.Y), ink);
            // 3：内建圆角矩形 + 径向渐变
            var grad = new CanvasRadialGradientBrush(
                Device,
                new[]
                {
                    new CanvasGradientStop(0f, Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1C)),
                    new CanvasGradientStop(1f, Color.FromArgb(0x00, 0x1C, 0x1C, 0x1C)),
                }
            );
            grad.Center = new Vector2(0.5f, 0.5f);
            grad.RadiusX = 0.62f;
            grad.RadiusY = 0.62f;
            var rr = CanvasGeometry.CreateRoundedRectangle(
                Device,
                2f * s,
                2f * s,
                12f * s,
                12f * s,
                3f * s,
                3f * s
            );
            ds.DrawGeometry(rr, new Vector2(2 * cell + pos.X, pos.Y), grad);
            // 4：高斯模糊（独立 RT + 效果链）
            float blur = 1.2f * s;
            int pad = (int)(blur * 3f + 4f);
            int size = (int)(16 * s + 2 * pad);
            var tmp = new CanvasRenderTarget(
                Device,
                size,
                size,
                96,
                Fmt,
                CanvasAlphaMode.Premultiplied
            );
            using (var tds = tmp.CreateDrawingSession())
            {
                tds.Clear(Colors.Transparent);
                tds.DrawGeometry(Geom(lSquare, s), new Vector2(pad, pad), ink);
            }
            var blurFx = new GaussianBlurEffect { Source = tmp, BlurAmount = blur };
            ds.DrawImage(blurFx, 3 * cell + pos.X - pad, pos.Y - pad);
        }
        SavePng("out/icons-blur-variants.png", w, h, rt.GetPixelBytes());
        Console.WriteLine("wrote out/icons-blur-variants.png");
        // 四格中心：内建矩形 / CreatePath 填充 / 渐变 / 模糊
        PrintPx(rt, w, "out/icons-blur-variants.png", (48, 48), (144, 48), (240, 48), (336, 48));
    }

    // ---- path data → CanvasPathBuilder（仅支持 M/H/V/L/Q/C/Z，与 XAML Path.Data 同源；坐标按 scale 放大）----

    private readonly struct Seg
    {
        // Kind: 0=直线 1=二次贝塞尔 2=三次贝塞尔；P1=端点/控制点1，P2=控制点2，P3=端点
        public Seg(int kind, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            Kind = kind;
            P1 = p1;
            P2 = p2;
            P3 = p3;
        }

        public int Kind { get; }
        public Vector2 P1 { get; }
        public Vector2 P2 { get; }
        public Vector2 P3 { get; }
    }

    // 本 Win2D 投影里 CanvasPathBuilder 不是 CanvasGeometry，用 CanvasGeometry.CreatePath 固化
    private static CanvasGeometry Geom(string data, float scale)
    {
        return CanvasGeometry.CreatePath(ToBuilder(data, scale));
    }

    private static CanvasPathBuilder ToBuilder(string data, float scale)
    {
        var builder = new CanvasPathBuilder(Device);
        foreach (var (start, closed, segs) in ParseFigures(data))
        {
            builder.BeginFigure(ScaleVec(start, scale));
            foreach (var seg in segs)
            {
                switch (seg.Kind)
                {
                    case 0:
                        builder.AddLine(ScaleVec(seg.P1, scale));
                        break;
                    case 1:
                        builder.AddQuadraticBezier(
                            ScaleVec(seg.P1, scale),
                            ScaleVec(seg.P2, scale)
                        );
                        break;
                    case 2:
                        builder.AddCubicBezier(
                            ScaleVec(seg.P1, scale),
                            ScaleVec(seg.P2, scale),
                            ScaleVec(seg.P3, scale)
                        );
                        break;
                }
            }
            builder.EndFigure(closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        }
        return builder;
    }

    private static Vector2 ScaleVec(Vector2 v, float scale)
    {
        return new Vector2(v.X * scale, v.Y * scale);
    }

    private static IEnumerable<(Vector2 start, bool closed, List<Seg> segs)> ParseFigures(
        string data
    )
    {
        int i = 0;
        while (i < data.Length)
        {
            SkipSep(data, ref i);
            if (i >= data.Length)
            {
                break;
            }
            char cmd = data[i++];
            if (cmd != 'M')
            {
                throw new FormatException($"figure must start with M, got '{cmd}'");
            }
            float mx = Num(data, ref i);
            SkipSep(data, ref i);
            float my = Num(data, ref i);
            SkipSep(data, ref i);
            var prev = new Vector2(mx, my);
            var segs = new List<Seg>();
            bool closed = false;
            while (i < data.Length)
            {
                SkipSep(data, ref i);
                if (i >= data.Length)
                {
                    break;
                }
                char sc = data[i];
                if (!char.IsLetter(sc))
                {
                    break;
                }
                i++;
                switch (sc)
                {
                    case 'H':
                    {
                        float x = Num(data, ref i);
                        segs.Add(new Seg(0, new Vector2(x, prev.Y), Vector2.Zero, Vector2.Zero));
                        prev = new Vector2(x, prev.Y);
                        break;
                    }
                    case 'V':
                    {
                        float y = Num(data, ref i);
                        segs.Add(new Seg(0, new Vector2(prev.X, y), Vector2.Zero, Vector2.Zero));
                        prev = new Vector2(prev.X, y);
                        break;
                    }
                    case 'L':
                    {
                        float x = Num(data, ref i);
                        SkipSep(data, ref i);
                        float y = Num(data, ref i);
                        segs.Add(new Seg(0, new Vector2(x, y), Vector2.Zero, Vector2.Zero));
                        prev = new Vector2(x, y);
                        break;
                    }
                    case 'Q':
                    {
                        float c1x = Num(data, ref i);
                        SkipSep(data, ref i);
                        float c1y = Num(data, ref i);
                        SkipSep(data, ref i);
                        float x = Num(data, ref i);
                        SkipSep(data, ref i);
                        float y = Num(data, ref i);
                        segs.Add(
                            new Seg(1, new Vector2(c1x, c1y), new Vector2(x, y), Vector2.Zero)
                        );
                        prev = new Vector2(x, y);
                        break;
                    }
                    case 'C':
                    {
                        float c1x = Num(data, ref i);
                        SkipSep(data, ref i);
                        float c1y = Num(data, ref i);
                        SkipSep(data, ref i);
                        float c2x = Num(data, ref i);
                        SkipSep(data, ref i);
                        float c2y = Num(data, ref i);
                        SkipSep(data, ref i);
                        float x = Num(data, ref i);
                        SkipSep(data, ref i);
                        float y = Num(data, ref i);
                        segs.Add(
                            new Seg(
                                2,
                                new Vector2(c1x, c1y),
                                new Vector2(c2x, c2y),
                                new Vector2(x, y)
                            )
                        );
                        prev = new Vector2(x, y);
                        break;
                    }
                    case 'Z':
                    {
                        closed = true;
                        goto figureEnd;
                    }
                }
            }
            figureEnd:
            yield return (new Vector2(mx, my), closed, segs);
        }
    }

    private static void SkipSep(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == ','))
        {
            i++;
        }
    }

    private static float Num(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-'))
        {
            i++;
        }
        if (i == start)
        {
            throw new FormatException("expected number at index " + start);
        }
        return float.Parse(s.AsSpan(start, i - start), CultureInfo.InvariantCulture);
    }

    // ---- 像素读回（BGRA，打印 R/G/B/A 十六进制）----

    private static void PrintPx(
        CanvasRenderTarget rt,
        int width,
        string label,
        params (int x, int y)[] points
    )
    {
        byte[] px = rt.GetPixelBytes();
        foreach (var (x, y) in points)
        {
            int i = (y * width + x) * 4;
            Console.WriteLine(
                $"  [px] {label} ({x},{y}) = R{px[i + 2]:X2} G{px[i + 1]:X2} B{px[i]:X2} A{px[i + 3]:X2}"
            );
        }
    }

    // ---- 依赖无关的 PNG 导出（RGB24，手写 IHDR/IDAT(zlib)/IEND）----

    private static void SavePng(string path, int w, int h, byte[] bgra)
    {
        int stride = 1 + w * 3;
        var raw = new byte[h * stride];
        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            raw[row] = 0; // 滤波器：none
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                int di = row + 1 + x * 3;
                raw[di] = bgra[si + 2]; // R
                raw[di + 1] = bgra[si + 1]; // G
                raw[di + 2] = bgra[si]; // B
            }
        }
        var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteInt32BE(ihdr, 0, w);
        WriteInt32BE(ihdr, 4, h);
        ihdr[8] = 8; // 位深
        ihdr[9] = 2; // 颜色类型：truecolor
        WriteChunk(ms, "IHDR", ihdr);
        var deflated = new MemoryStream();
        using (var ds = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            ds.Write(raw, 0, raw.Length);
        }
        int defLen = (int)deflated.Length;
        var zlib = new byte[2 + defLen + 4];
        zlib[0] = 0x78;
        zlib[1] = 0x01;
        deflated.Position = 0;
        deflated.Read(zlib.AsSpan(2, defLen));
        WriteInt32BE(zlib, 2 + defLen, Adler32(raw));
        WriteChunk(ms, "IDAT", zlib);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteInt32BE(byte[] buf, int off, int v)
    {
        buf[off] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }

    private static void WriteChunk(Stream ms, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt32BE(len, 0, data.Length);
        ms.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
        var crc = new byte[4];
        WriteInt32BE(crc, 0, (int)Crc32(crcInput));
        ms.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)((crc & 1) == 1 ? -1 : 0));
            }
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static int Adler32(byte[] data)
    {
        const int mod = 65521;
        int a = 1;
        int c = 0;
        foreach (var b in data)
        {
            a = (a + b) % mod;
            c = (c + a) % mod;
        }
        return (c << 16) | a;
    }
}
