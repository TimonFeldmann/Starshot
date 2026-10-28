using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

/// <summary>
/// 无界面验证"擦除"渲染管线（1x1 裁剪 → 放大 → 贴回）在本 Win2D 投影里每一阶段是否生效：
/// 画一个已知底图（黑底 + 左上红圆 + 右上绿圆），把各管线变体画进新 RT，
/// 用 RT.GetPixelBytes()（本 Win2D 投影自带的读回接口，主工程 ImageSaver 同款用法）
/// 读回目标像素并打印（绿 = 该阶段工作正常，全黑 = 该阶段产出为空/错位）。
/// 内存布局按 D2D1 BGRA；即使通道顺序相反，绿/红/黑依然可区分（四通道全部打印）。
/// </summary>
internal static class Program
{
    private static CanvasRenderTarget MakeRt()
    {
        return new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            200,
            200,
            96,
            DirectXPixelFormat.R8G8B8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied
        );
    }

    private static CanvasRenderTarget MakeBase()
    {
        var rt = MakeRt();
        using var ds = rt.CreateDrawingSession();
        ds.Clear(Colors.Black);
        ds.FillEllipse(new Vector2(50, 50), 40, 40, Colors.Red);
        ds.FillEllipse(new Vector2(150, 50), 40, 40, Colors.Green);
        return rt;
    }

    /// <summary>从 RT 读回单个像素（200x200 BGRA；c0..c2 不假定通道序，A 恒为第 4 字节）。</summary>
    private static string Px(CanvasRenderTarget rt, int x, int y)
    {
        byte[] px = rt.GetPixelBytes();
        int i = y * 200 * 4 + x * 4;
        return $"c0={px[i]:X2} c1={px[i + 1]:X2} c2={px[i + 2]:X2} A={px[i + 3]:X2}";
    }

    /// <summary>统计"绿色系"像素并报告首个坐标（yMin 起扫，用于发现错位/空输出）。</summary>
    private static string ScanGreen(CanvasRenderTarget rt, int yMin)
    {
        byte[] px = rt.GetPixelBytes();
        int count = 0;
        int fx = -1,
            fy = -1;
        for (int y = Math.Max(0, yMin); y < 200; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                int i = y * 200 * 4 + x * 4;
                // 底图绿色在本 RT 色彩空间下实测 c0=00 c1=80 c2=00；绿色系 = c1 高、c0/c2 低
                if (px[i + 1] > 0x40 && px[i] < 0x40 && px[i + 2] < 0x40)
                {
                    count++;
                    if (fx < 0)
                    {
                        fx = x;
                        fy = y;
                    }
                }
            }
        }
        return fx < 0 ? $"no green in y>={yMin}" : $"green count={count} first=({fx},{fy})";
    }

    private static void RunVariant(
        string label,
        Action<CanvasDrawingSession, ICanvasImage> draw,
        ICanvasImage baseImg,
        (int x, int y)[] checks
    )
    {
        var rt = MakeRt();
        using (var ds = rt.CreateDrawingSession())
        {
            ds.DrawImage(baseImg);
            draw(ds, baseImg);
        }
        foreach (var c in checks)
        {
            if (c.x < 0)
            {
                Console.WriteLine($"{label} scan       : {ScanGreen(rt, c.y)}");
            }
            else
            {
                Console.WriteLine($"{label} ({c.x},{c.y}) : {Px(rt, c.x, c.y)}");
            }
        }
    }

    private static int Main()
    {
        CanvasRenderTarget baseRt = MakeBase();
        ICanvasImage baseImg = baseRt;

        // 底图自检：(50,50) 应为红，(150,50) 应为绿
        Console.WriteLine($"BASE        ( 50, 50) : {Px(baseRt, 50, 50)}  (expect red)");
        Console.WriteLine($"BASE        (150, 50) : {Px(baseRt, 150, 50)}  (expect green)");
        Console.WriteLine();

        // H: 对照组 —— 普通 RT（无效果链）4 参贴回 (src, destX, destY, sourceRect)
        {
            CanvasRenderTarget srcRt = MakeRt();
            using (var sds = srcRt.CreateDrawingSession())
            {
                sds.Clear(Colors.Transparent);
                sds.FillEllipse(new Vector2(50, 50), 40, 40, Colors.Green); // 左上 60x60 内为绿
            }
            RunVariant(
                "H  plainRT 4arg",
                (ds, img) =>
                {
                    ds.DrawImage(srcRt, 20f, 120f, new Rect(0, 0, 60, 60));
                },
                baseImg,
                [(50, 150), (-1, 100)] // 若 4 参 = 目标偏移 + 源矩形：应为绿色
            );
        }

        // X1: 全图"恒等"裁剪（SourceRectangle = 整图）+ 1 参 DrawImage —— 判定 CropEffect 输出是否全空
        RunVariant(
            "X1 cropFull 1arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(0, 0, 200, 200),
                };
                ds.DrawImage(crop);
            },
            baseImg,
            [(150, 50), (50, 50)] // 若恒等裁剪生效：两个圆都应可见
        );

        // X2: 60x60 裁剪 + 1 参 DrawImage（不指定源矩形）—— 若紧凑裁剪，绿色应出现在左上 (30,30) 附近
        RunVariant(
            "X2 crop60 1arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(120, 20, 60, 60),
                };
                ds.DrawImage(crop);
            },
            baseImg,
            [(30, 30), (-1, 0)] // 紧凑裁剪 → 左上出现绿色；扫描全图报告首个绿色坐标
        );

        // X3: 恒等 Transform2DEffect + 1 参 DrawImage —— 判定"效果链输出"是否普遍可用
        RunVariant(
            "X3 transform 1arg",
            (ds, img) =>
            {
                var xf = new Transform2DEffect { Source = img };
                ds.DrawImage(xf);
            },
            baseImg,
            [(150, 50), (50, 50)] // 恒等变换 → 原样可见
        );

        // X4: GaussianBlurEffect(2) + 1 参 DrawImage —— 模糊效果链（主工程模糊工具同款）对照
        RunVariant(
            "X4 blur2 1arg",
            (ds, img) =>
            {
                var blur = new GaussianBlurEffect { Source = img, BlurAmount = 2f };
                ds.DrawImage(blur);
            },
            baseImg,
            [(150, 50)] // 模糊后绿圆中心仍应为绿色
        );

        // A: CropEffect 1x1（采样点 = 绿圆中心）+ 4 参贴回单像素
        RunVariant(
            "A  crop1x1 4arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(150, 50, 1, 1),
                };
                ds.DrawImage(crop, 20f, 120f, new Rect(0, 0, 1, 1));
            },
            baseImg,
            [(20, 120), (-1, 100)] // 若 crop 1x1 生效：应为绿色
        );

        // B: CropEffect 1x1 + ScaleEffect 60x60 + 4 参贴回 (20,120)（当前擦除管线的形态）
        RunVariant(
            "B  +scale 4arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(150, 50, 1, 1),
                };
                var patch = new ScaleEffect { Source = crop, Scale = new Vector2(60, 60) };
                ds.DrawImage(patch, 20f, 120f, new Rect(0, 0, 60, 60));
            },
            baseImg,
            [(50, 150), (-1, 100)] // 若管线生效：应为绿色
        );

        // C: 同 B 但 3 参 (src, x, y) —— 判定 3 参重载是目标偏移还是源偏移
        RunVariant(
            "C  +scale 3arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(150, 50, 1, 1),
                };
                var patch = new ScaleEffect { Source = crop, Scale = new Vector2(60, 60) };
                ds.DrawImage(patch, 20f, 120f);
            },
            baseImg,
            [(50, 150), (20, 120), (50, 50), (-1, 100)]
        );

        // D: 对照组 —— CropEffect 60x60 直接 4 参贴回（与模糊贴回同款，已在主工程验证可用）
        RunVariant(
            "D  crop60  4arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(120, 20, 60, 60),
                };
                ds.DrawImage(crop, 20f, 120f, new Rect(0, 0, 60, 60));
            },
            baseImg,
            [(50, 150), (-1, 100)] // 应为绿色（对照）
        );

        // E: 4x4 裁剪 + 放大（规避 1x1 的特殊性）
        RunVariant(
            "E  crop4x4 4arg",
            (ds, img) =>
            {
                var crop = new CropEffect
                {
                    Source = img,
                    SourceRectangle = new Rect(148, 48, 4, 4),
                };
                var patch = new ScaleEffect { Source = crop, Scale = new Vector2(60, 60) };
                ds.DrawImage(patch, 20f, 120f, new Rect(0, 0, 60, 60));
            },
            baseImg,
            [(50, 150), (-1, 100)] // 应为绿色
        );

        // F: 后备方案 —— CPU 侧纯色位图 (CreateFromColors，Color[] 需恰为 w*h) + 4 参贴回
        {
            var colors = new Color[60 * 60];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = Color.FromArgb(0xFF, 0x00, 0xFF, 0x00);
            }
            var solid = CanvasBitmap.CreateFromColors(
                CanvasDevice.GetSharedDevice(),
                colors,
                60,
                60
            );
            RunVariant(
                "F  solid  4arg",
                (ds, img) =>
                {
                    ds.DrawImage(solid, 20f, 120f, new Rect(0, 0, 60, 60));
                },
                baseImg,
                [(50, 150)] // 应为绿色
            );
        }

        // G: 后备方案 —— ColorSourceEffect（无限纯色源）+ 4 参限定 60x60 贴回
        RunVariant(
            "G  colorsrc 4arg",
            (ds, img) =>
            {
                var solid = new ColorSourceEffect
                {
                    Color = Color.FromArgb(0xFF, 0x00, 0xFF, 0x00),
                };
                ds.DrawImage(solid, 20f, 120f, new Rect(0, 0, 60, 60));
            },
            baseImg,
            [(50, 150)] // 若纯色源可用：应为绿色
        );

        return 0;
    }
}
