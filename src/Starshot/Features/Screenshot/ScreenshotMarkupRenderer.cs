using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace Starshot.Features.Screenshot;

/// <summary>一条标注笔画（自由绘制的路径 + 颜色 + 粗细）。</summary>
internal sealed class ScreenshotStroke
{
    public List<Vector2> Points { get; } = new();

    public Color Color { get; set; }

    public float Thickness { get; set; }

    /// <summary>高亮笔画：渲染到独立图层后以半透明合成，避免半透明段落重叠产生色带。</summary>
    public bool IsHighlight { get; set; }
}

/// <summary>模糊区域（矩形 + 强度，图像坐标）。</summary>
internal sealed class ScreenshotBlurRegion
{
    public Rect Rect { get; set; }

    public float Strength { get; set; }
}

/// <summary>擦除区域（矩形 + 采样点，图像坐标）：用原图采样点像素的颜色填充该区域，"抹掉"其中内容。</summary>
internal sealed class ScreenshotEraseRegion
{
    public Rect Rect { get; set; }

    /// <summary>采样点（图像坐标，已钳制在图像内）：首击该处的像素颜色即区域填充色。</summary>
    public Vector2 Sample { get; set; }

    /// <summary>采样像素原始 4 字节（BGRA，premultiplied，与底图同色彩空间）：由代码层从底图像素缓存采样。</summary>
    public byte[]? SampleBytes { get; set; }

    /// <summary>纯色补丁位图（尺寸 = Rect，由代码层按 SampleBytes 生成并缓存，移动/重采样时重建）。</summary>
    public CanvasBitmap? Patch { get; set; }

    /// <summary>补丁位图对应的采样点（失效判定）。</summary>
    public Vector2 PatchSample { get; set; }

    /// <summary>补丁位图对应的宽度（失效判定）。</summary>
    public int PatchW { get; set; }

    /// <summary>补丁位图对应的高度（失效判定）。</summary>
    public int PatchH { get; set; }
}

/// <summary>形状工具的形状类型：拖拽创建（起点 = 按下点，终点 = 释放点）。</summary>
internal enum ScreenshotShapeKind
{
    /// <summary>箭头（线段 + V 形箭头，起点 → 终点为指向）。</summary>
    Arrow = 0,

    /// <summary>圆（椭圆，对角线定外接圆）。</summary>
    Circle = 1,

    /// <summary>矩形框（对角线定外接框）。</summary>
    Box = 2,
}

/// <summary>形状元素（形状 + 颜色 + 粗细，颜色/粗细与画笔同源）：图像坐标起点/终点。</summary>
internal sealed class ScreenshotShape
{
    public ScreenshotShapeKind Kind { get; set; }

    public Vector2 Start { get; set; }

    public Vector2 End { get; set; }

    public Color Color { get; set; }

    public float Thickness { get; set; }

    /// <summary>包围盒（命中测试 / 移动 / 选中框共用）。</summary>
    public Rect Bounds
    {
        get
        {
            float x = Math.Min(Start.X, End.X);
            float y = Math.Min(Start.Y, End.Y);
            return new Rect(x, y, Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));
        }
    }
}

/// <summary>
/// 一个标注操作（笔画 或 模糊区域 或 擦除区域 或 形状）。统一成单条历史，供 Undo / Clear 使用。
/// </summary>
internal sealed class ScreenshotOp
{
    public ScreenshotStroke? Stroke { get; init; }

    public ScreenshotBlurRegion? Blur { get; init; }

    public ScreenshotEraseRegion? Erase { get; init; }

    public ScreenshotShape? Shape { get; init; }

    public bool IsBlur => Blur is not null;

    public bool IsErase => Erase is not null;
}

/// <summary>
/// 把标注（模糊区域 + 擦除补丁 + 笔画）合成到绘图会话的纯 Win2D 逻辑，供编辑窗口实时显示与保存共用。
/// 按操作顺序严格渲染（擦除补丁可压在前后笔画之上/之下）：
/// 模糊区域 = 从"同强度整图模糊缓存"裁剪贴回；擦除补丁 = 采样像素纯色位图贴回；
/// 不透明画笔直接绘制（加密采样 + 圆盘链，平滑无锯齿）；高亮笔画进 scratch 层按 40% 合成。
/// </summary>
internal static class ScreenshotMarkupRenderer
{
    /// <summary>高亮图层合成不透明度。</summary>
    public const float HighlightOpacity = 0.4f;

    public static void Render(
        CanvasDrawingSession ds,
        ICanvasImage sdrBase,
        IReadOnlyList<ScreenshotOp> ops,
        ScreenshotOp? current,
        CanvasRenderTarget markerLayer,
        Func<ScreenshotBlurRegion, ICanvasImage> blurredProvider,
        Func<ScreenshotEraseRegion, ICanvasImage> eraserProvider,
        ScreenshotOp? selected = null
    )
    {
        ds.DrawImage(sdrBase);

        // 严格按操作顺序：区域（模糊/擦除）对前后笔画的遮挡关系才正确
        foreach (ScreenshotOp op in ops)
        {
            DrawOp(ds, op, markerLayer, blurredProvider, eraserProvider, eraseFill: true);
        }

        // 拖拽中的擦除只画实时框（填充色在释放提交后出现）；其余进行中操作实时预览
        if (current is not null)
        {
            DrawOp(ds, current, markerLayer, blurredProvider, eraserProvider, eraseFill: false);
        }

        // 拖拽中的区域（模糊/擦除/形状）实时框：拖动过程中始终可见
        if (current?.Erase is not null || current?.Blur is not null || current?.Shape is not null)
        {
            Rect r = current.Erase?.Rect ?? current.Blur?.Rect ?? ShapeBounds(current.Shape!);
            if (r.Width > 0f && r.Height > 0f)
            {
                DrawBoxOutline(ds, r);
            }
        }

        // 选中指示框（仅实时显示，保存时 selected 传 null 不画）
        if (selected is not null)
        {
            DrawSelectionOutline(ds, selected);
        }
    }

    private static void DrawOp(
        CanvasDrawingSession ds,
        ScreenshotOp op,
        CanvasRenderTarget markerLayer,
        Func<ScreenshotBlurRegion, ICanvasImage> blurredProvider,
        Func<ScreenshotEraseRegion, ICanvasImage> eraserProvider,
        bool eraseFill
    )
    {
        if (op.Blur is not null)
        {
            BlitBlurRegion(ds, blurredProvider(op.Blur), op.Blur);
        }
        else if (op.Erase is not null && eraseFill)
        {
            BlitErasePatch(ds, eraserProvider(op.Erase), op.Erase);
        }
        else if (op.Stroke is not null)
        {
            if (op.Stroke.IsHighlight)
            {
                // 高亮笔画：scratch 层不透明渲染后按 40% 合成（同一笔画内部重叠区域颜色均匀）
                using var lds = markerLayer.CreateDrawingSession();
                lds.Clear(Colors.Transparent);
                DrawStroke(lds, op.Stroke);
                ds.DrawImage(
                    new OpacityEffect { Source = markerLayer, Opacity = HighlightOpacity }
                );
            }
            else
            {
                // 不透明画笔直接画进主会话（圆盘链同色重叠无色带）
                DrawStroke(ds, op.Stroke);
            }
        }
        else if (op.Shape is not null)
        {
            DrawShape(ds, op.Shape);
        }
    }

    private static void BlitBlurRegion(
        CanvasDrawingSession ds,
        ICanvasImage blurred,
        ScreenshotBlurRegion region
    )
    {
        Rect r = region.Rect;
        if (r.Width <= 0f || r.Height <= 0f)
            return;
        // 4 参重载 (src, destX, destY, sourceRect)：把整图模糊缓存中该区域按原坐标贴回
        //（与 ScreenCaptureService 的合成用法一致；不用 2 参 Vector2 重载——在该 Win2D 投影里是源偏移，
        // 会把内容错移到右下方）
        ds.DrawImage(blurred, (float)r.X, (float)r.Y, r);
    }

    /// <summary>
    /// 擦除补丁：把采样像素的纯色位图（尺寸 = 区域，由代码层从底图像素缓存生成，位级一致无色差）贴回原位，
    /// 使区域内容与前后笔画都被"抹平"成采样色。
    /// （不用 CropEffect 1x1 采样 + ScaleEffect：该 Win2D 投影中"部分区域"裁剪输出为空，
    /// 纯色填充整段不可见——Win2dProbe 无界面实测确认，见 tools/Win2dProbe。）
    /// </summary>
    private static void BlitErasePatch(
        CanvasDrawingSession ds,
        ICanvasImage patch,
        ScreenshotEraseRegion er
    )
    {
        Rect r = er.Rect;
        if (r.Width <= 0f || r.Height <= 0f)
            return;
        // 4 参 (src, destX, destY, sourceRect) 目标偏移重载（普通位图源，与 BlitBlurRegion 一致）
        ds.DrawImage(
            patch,
            (float)r.X,
            (float)r.Y,
            new Rect(0, 0, (float)r.Width, (float)r.Height)
        );
    }

    /// <summary>选中指示框：外黑内白双线框（深浅底色上都可见），不参与保存输出。</summary>
    private static void DrawSelectionOutline(CanvasDrawingSession ds, ScreenshotOp op)
    {
        if (op.Blur is not null)
        {
            DrawBoxOutline(ds, op.Blur.Rect);
        }
        else if (op.Erase is not null)
        {
            DrawBoxOutline(ds, op.Erase.Rect);
        }
        else if (op.Stroke is not null)
        {
            DrawStrokeOutline(ds, op.Stroke);
        }
        else if (op.Shape is not null)
        {
            DrawBoxOutline(ds, ShapeBounds(op.Shape));
        }
    }

    /// <summary>形状包围盒（该 Win2D 投影里 Windows.Foundation.Rect 的成员是 double）。</summary>
    public static Rect ShapeBounds(ScreenshotShape sh)
    {
        return sh.Bounds;
    }

    /// <summary>
    /// 形状绘制：圆/矩形 = 描边（几何向内缩半个线宽，描边外缘正好落在包围盒上）；
    /// 箭头 = 主干线 + V 形箭头两笔（圆头圆角）。
    /// </summary>
    private static void DrawShape(CanvasDrawingSession ds, ScreenshotShape sh)
    {
        Color c = sh.Color;
        float t = sh.Thickness;
        var pen = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };
        switch (sh.Kind)
        {
            case ScreenshotShapeKind.Circle:
            {
                Rect r = sh.Bounds;
                if (r.Width < 1f || r.Height < 1f)
                {
                    ds.FillEllipse(sh.Start, t / 2f, t / 2f, c);
                    break;
                }
                ds.DrawEllipse(
                    (float)r.X + t / 2f,
                    (float)r.Y + t / 2f,
                    (float)r.Width / 2f,
                    (float)r.Height / 2f,
                    c,
                    t,
                    pen
                );
                break;
            }
            case ScreenshotShapeKind.Box:
            {
                Rect r = sh.Bounds;
                if (r.Width < 1f || r.Height < 1f)
                {
                    ds.FillEllipse(sh.Start, t / 2f, t / 2f, c);
                    break;
                }
                float rad = Math.Min(4f, Math.Min((float)r.Width, (float)r.Height) / 4f);
                ds.DrawRoundedRectangle(
                    (float)r.X + t / 2f,
                    (float)r.Y + t / 2f,
                    Math.Max(t, (float)r.Width - t),
                    Math.Max(t, (float)r.Height - t),
                    rad,
                    rad,
                    c,
                    t,
                    pen
                );
                break;
            }
            default:
            {
                // 箭头：主干画到「肩部」，V 形两笔在终点张开
                var (shoulder, side) = ArrowHead(sh.Start, sh.End, sh.Thickness);
                ds.DrawLine(sh.Start, shoulder, c, t, pen);
                ds.DrawLine(shoulder + side, sh.End, c, t, pen);
                ds.DrawLine(sh.End, shoulder - side, c, t, pen);
                break;
            }
        }
    }

    /// <summary>箭头头部几何：肩部（主干终点）与半宽向量（V 两笔的偏移），与窗口层命中测试共用。</summary>
    public static (Vector2 Shoulder, Vector2 Side) ArrowHead(
        Vector2 start,
        Vector2 end,
        float thickness
    )
    {
        Vector2 d = end - start;
        float len = MathF.Sqrt(Vector2.Dot(d, d));
        if (len < 1e-3f)
        {
            return (end, Vector2.Zero);
        }
        Vector2 u = d / len;
        float head = MathF.Min(MathF.Max(thickness * 2.5f, 8f), len * 0.9f);
        Vector2 shoulder = end - u * head;
        Vector2 side = new Vector2(-u.Y, u.X) * (head * 0.5f);
        return (shoulder, side);
    }

    /// <summary>外黑内白双线矩形框（深浅底色上都可见）。</summary>
    private static void DrawBoxOutline(CanvasDrawingSession ds, Rect r)
    {
        ds.DrawRectangle(
            new Rect(r.X - 4f, r.Y - 4f, r.Width + 8f, r.Height + 8f),
            Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
            4f
        );
        ds.DrawRectangle(
            new Rect(r.X - 2f, r.Y - 2f, r.Width + 4f, r.Height + 4f),
            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            2f
        );
    }

    /// <summary>笔画的选中框：点列包围盒 + 半宽留白。</summary>
    private static void DrawStrokeOutline(CanvasDrawingSession ds, ScreenshotStroke st)
    {
        List<Vector2> pts = st.Points;
        if (pts.Count == 0)
            return;
        float minX = pts[0].X,
            minY = pts[0].Y,
            maxX = pts[0].X,
            maxY = pts[0].Y;
        foreach (Vector2 p in pts)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        float pad = st.Thickness / 2f + 3f;
        DrawBoxOutline(
            ds,
            new Rect(minX - pad, minY - pad, maxX - minX + 2f * pad, maxY - minY + 2f * pad)
        );
    }

    private static void DrawStroke(CanvasDrawingSession ds, ScreenshotStroke st)
    {
        List<Vector2> pts = Resample(st.Points, st.Thickness);
        if (pts.Count == 0)
            return;
        float r = st.Thickness / 2f;
        foreach (Vector2 p in pts)
        {
            ds.FillEllipse(p, r, r, st.Color);
        }
    }

    /// <summary>
    /// 把原始采样点加密成步长 ≤ thickness/4 的点列：相邻圆盘重叠 75% 以上，
    /// 圆盘链无缝衔接、端点圆润，消除"一段段不相连"的锯齿观感。
    /// </summary>
    private static List<Vector2> Resample(IReadOnlyList<Vector2> raw, float thickness)
    {
        if (raw.Count == 0)
            return new List<Vector2>();
        if (raw.Count == 1)
        {
            return new List<Vector2>(raw);
        }
        float step = Math.Max(1f, thickness * 0.25f);
        var outp = new List<Vector2>(raw.Count * 4) { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            Vector2 a = raw[i - 1],
                b = raw[i];
            float dist = Vector2.Distance(a, b);
            int n = (int)MathF.Ceiling(dist / step);
            if (n < 1)
                n = 1;
            for (int k = 1; k <= n; k++)
            {
                outp.Add(Vector2.Lerp(a, b, (float)k / n));
            }
        }
        return outp;
    }
}
