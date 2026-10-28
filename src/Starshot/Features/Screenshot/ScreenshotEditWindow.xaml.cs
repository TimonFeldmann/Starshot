using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starshot.Frameworks;
using Starshot.Language;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using ToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;

namespace Starshot.Features.Screenshot;

/// <summary>
/// 捕获后弹出的标注编辑窗口：显示捕获图，提供基础标注工具（区域模糊（强度可调）/ 高亮 / 画笔，均可选色），
/// 以及一个保存按钮——把带标注的合成结果写到截图目录（与原始截图同目录、-marked 后缀）。
/// 原始截图由捕获流程自动保存，本窗口只负责写出带标注副本。
/// </summary>
public sealed partial class ScreenshotEditWindow : WindowEx
{
    private readonly ILogger<ScreenshotEditWindow> _logger =
        AppConfig.GetLogger<ScreenshotEditWindow>();

    private readonly ScreenshotEditContext _ctx;

    private readonly int _width;

    private readonly int _height;

    /// <summary>8 位 SDR 底图（HDR 源已 tonemap）：显示与保存共用的基准层，由本窗口持有（调用方可释放原图）。</summary>
    private readonly CanvasRenderTarget _sdrBase;

    /// <summary>底图像素缓存（BGRA，premultiplied，与 _sdrBase 同色彩空间）：擦除工具用它采样首击像素。</summary>
    private readonly byte[] _basePixels;

    /// <summary>实时合成层（底图 + 笔画），供 UI 显示。本 Win2D 版本非 IDisposable（与代码库其他窗口一致）。</summary>
    private readonly CanvasImageSource _surface;

    /// <summary>高亮笔画 scratch 层（不透明填充，逐笔画复用），合成时按 40% 不透明度混合。</summary>
    private readonly CanvasRenderTarget _markerLayer;

    /// <summary>同强度整图模糊缓存（区域模糊用），按强度惰性生成。</summary>
    private readonly Dictionary<float, CanvasRenderTarget> _blurredCache = new();

    private readonly List<ScreenshotOp> _ops = new();

    private ScreenshotOp? _current;

    /// <summary>选择工具当前选中的元素（来自 _ops），用于显示指示框与删除。</summary>
    private ScreenshotOp? _selected;

    /// <summary>选择工具拖拽移动中的元素（= _selected），按下时记录原始几何，拖拽按增量平移。</summary>
    private ScreenshotOp? _moving;

    /// <summary>移动拖拽的按下点（图像坐标）。</summary>
    private Vector2 _movePressStart;

    /// <summary>移动目标区域（模糊/擦除）按下时的原始矩形。</summary>
    private Rect _moveOrigRect;

    /// <summary>移动目标擦除区域按下时的原始采样点。</summary>
    private Vector2 _moveOrigSample;

    /// <summary>移动目标笔画按下时的原始点列（拷贝）。</summary>
    private List<Vector2>? _moveOrigPoints;

    private bool _blurOn;

    private bool _isDrawing;

    /// <summary>模糊区域拖拽起点（图像坐标）。</summary>
    private Vector2 _blurPressStart;

    /// <summary>形状工具当前形状类型（箭头/圆/框）：颜色面板的形状选择弹层控制，默认箭头。</summary>
    private ScreenshotShapeKind _shapeKind;

    /// <summary>移动目标形状按下时的原始起点。</summary>
    private Vector2 _moveOrigShapeStart;

    /// <summary>移动目标形状按下时的原始终点。</summary>
    private Vector2 _moveOrigShapeEnd;

    // ---- 工具按钮拖拽换序 ----

    /// <summary>正在拖拽换序的工具按钮（null = 未拖拽）。</summary>
    private Microsoft.UI.Xaml.Controls.Primitives.ToggleButton? _toolDragButton;

    /// <summary>指针按下位置（工具按钮组坐标）。</summary>
    private Vector2 _toolDragStart;

    /// <summary>拖拽是否超过阈值实际移动过（区分点击与拖拽）；拖拽后的残留 Click 据此丢弃。</summary>
    private bool _toolDragMoved;

    /// <summary>被拖按钮按下时的 IsChecked（拖拽结束后恢复，避免误切换工具）。</summary>
    private bool _toolPressWasChecked;

    // ---- 画笔光标预览 ----

    /// <summary>光标预览最后位置（Image 控件坐标）：颜色/粗细变化时刷新用。</summary>
    private Windows.Foundation.Point _lastPreviewPoint;

    internal ScreenshotEditWindow(
        ICanvasImage source,
        ScreenshotEditContext ctx,
        IReadOnlyList<ScreenshotOp>? initialOps = null
    )
    {
        InitializeComponent();

        _ctx = ctx;
        _width = ctx.Width;
        _height = ctx.Height;

        // 同步把源图转成 8 位 SDR 副本持有：此后调用方可安全释放原图（全屏/区域路径的 finally）。
        _sdrBase = BuildSdrBase(source, ctx);
        // 一次性读回底图像素（擦除工具按采样点取原始字节生成纯色补丁；
        // CropEffect 部分区域裁剪在该 Win2D 投影里输出为空，GPU 采样路径不可用）
        _basePixels = _sdrBase.GetPixelBytes();
        // 从嵌入元数据恢复的元素（图库重开标注文件）：直接并入历史列表。
        // 擦除元素不持久化 SampleBytes：按新底图重采样（GetErasePatch 首帧渲染时惰性补齐），
        // 采样点先钳制在图像内
        if (initialOps is { Count: > 0 })
        {
            foreach (ScreenshotOp op in initialOps)
            {
                if (op.Erase is not null)
                {
                    op.Erase.Sample = ClampSample(op.Erase.Sample);
                }
                _ops.Add(op);
            }
        }
        _surface = new CanvasImageSource(CanvasDevice.GetSharedDevice(), _width, _height, 96);
        _markerLayer = NewFullSizeLayer();
        Image_Image.Source = _surface;

        // 标题走代码后置设置，避免在 WindowEx 根节点上用静态 x:Bind。
        this.Title = Lang.ScreenshotEditWindow_Title;

        InitializeWindow();
        ColorPicker_Color.Color = Colors.Red;
        UpdateSwatch();
        // 选色弹层关闭时刷新色块（lambda 避免显式引用事件参数类型——本项目的 C# 引用集缺该类型）
        Flyout_Color.Closed += (_, __) => UpdateSwatch();
        // 形状选择默认箭头，同步形状色块
        UpdateShapeSwatch();
        // 应用上一次会话的工具按钮顺序（拖拽换序持久化）
        ApplySavedToolbarOrder();
        // 工具按钮换序：透明覆盖层（Overlay_ToolDrag）统一拦截按钮组上方的指针事件——
        // ToggleButton 会在隧道阶段吞掉左键按下（内部点击态），按钮级/根级都收不到左键事件；
        // 普通 Grid 覆盖层无内部点击态：左键按下即捕获指针、移动实时换位、释放时持久化并透传点击
        //（仅左键启动；右键/中键忽略）
        Overlay_ToolDrag.PointerPressed += Overlay_ToolDrag_PointerPressed;
        Overlay_ToolDrag.PointerMoved += Overlay_ToolDrag_PointerMoved;
        Overlay_ToolDrag.PointerReleased += Overlay_ToolDrag_PointerReleased;
        Overlay_ToolDrag.PointerCanceled += Overlay_ToolDrag_PointerCanceled;
        // 颜色/粗细变化时刷新光标预览
        ColorPicker_Color.ColorChanged += (_, __) => RefreshBrushPreview();
        Slider_Thickness.ValueChanged += (_, __) => RefreshBrushPreview();
        Redraw();

        this.Closed += ScreenshotEditWindow_Closed;
        CenterInScreen(960, 700);
    }

    private CanvasRenderTarget NewFullSizeLayer()
    {
        return new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            _width,
            _height,
            96,
            DirectXPixelFormat.R8G8B8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied
        );
    }

    /// <summary>编辑窗口最大化打开；用户还原后回到 960x700 居中尺寸。</summary>
    public override void Show()
    {
        base.Show();
        Maximize();
    }

    private void InitializeWindow()
    {
        // 用系统标准标题栏（内容不延伸到标题栏）：工具栏落在窗口顶边框之下，
        // 顶边框保留原生的拖动/最小化/最大化行为，工具栏控件交互不会误触发窗口拖动
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = false;
        Image_Image.PointerPressed += Image_Image_PointerPressed;
        Image_Image.PointerMoved += Image_Image_PointerMoved;
        // 释放/取消可能在指针已离开图像区时发生，事件冒泡到窗口根统一收尾
        RootGrid.PointerReleased += RootGrid_PointerReleased;
        RootGrid.PointerCanceled += RootGrid_PointerCanceled;
        // 工具按钮拖拽换序：根级接收冒泡的指针移动（覆盖层捕获丢失时的兜底，跨按钮拖拽始终生效）
        RootGrid.PointerMoved += RootGrid_ToolDragPointerMoved;
    }

    /// <summary>HDR（R16G16B16A16Float）复用保存管线的色调映射；SDR 直接 8 位拷贝。</summary>
    private static CanvasRenderTarget BuildSdrBase(ICanvasImage source, ScreenshotEditContext ctx)
    {
        if (source is CanvasBitmap bmp && bmp.Format is DirectXPixelFormat.R16G16B16A16Float)
        {
            return ScreenCaptureService.TonemapToSdr(bmp, ctx.SdrWhiteLevel);
        }
        var rt = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            ctx.Width,
            ctx.Height,
            96,
            DirectXPixelFormat.R8G8B8A8UIntNormalized,
            CanvasAlphaMode.Premultiplied
        );
        using var ds = rt.CreateDrawingSession();
        ds.DrawImage(source);
        return rt;
    }

    /// <summary>取（按需生成并缓存）指定强度的整图模糊图，供区域模糊裁剪贴回。</summary>
    private ICanvasImage GetBlurred(ScreenshotBlurRegion region)
    {
        if (!_blurredCache.TryGetValue(region.Strength, out var rt))
        {
            rt = NewFullSizeLayer();
            using (var ds = rt.CreateDrawingSession())
            {
                ds.DrawImage(
                    new GaussianBlurEffect { Source = _sdrBase, BlurAmount = region.Strength }
                );
            }
            _blurredCache[region.Strength] = rt;
        }
        return rt;
    }

    /// <summary>从底图像素缓存取采样点像素的原始 4 字节（BGRA，premultiplied，与底图同色彩空间）。</summary>
    private byte[] SampleErasePixel(Vector2 p)
    {
        int x = (int)Math.Clamp(p.X, 0f, _width - 1f);
        int y = (int)Math.Clamp(p.Y, 0f, _height - 1f);
        byte[] src = _basePixels;
        int i = (y * _width + x) * 4;
        return new byte[4] { src[i], src[i + 1], src[i + 2], src[i + 3] };
    }

    /// <summary>
    /// 取（按需生成并缓存）擦除区域的纯色补丁位图：采样像素原始字节铺满 w×h
    /// （与底图同格式同色彩空间，位级一致、无色彩空间往返转换）。
    /// 采样点或尺寸变化时重建；移动擦除区域期间逐帧重建（小区域开销可忽略）。
    /// </summary>
    private ICanvasImage GetErasePatch(ScreenshotEraseRegion region)
    {
        byte[] sb = region.SampleBytes ?? SampleErasePixel(region.Sample);
        region.SampleBytes = sb;
        int w = Math.Max(1, (int)region.Rect.Width);
        int h = Math.Max(1, (int)region.Rect.Height);
        if (
            region.Patch is not null
            && region.PatchSample == region.Sample
            && region.PatchW == w
            && region.PatchH == h
        )
        {
            return region.Patch;
        }
        byte[] bytes = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            bytes[o] = sb[0];
            bytes[o + 1] = sb[1];
            bytes[o + 2] = sb[2];
            bytes[o + 3] = sb[3];
        }
        region.Patch?.Dispose();
        region.Patch = CanvasBitmap.CreateFromBytes(
            CanvasDevice.GetSharedDevice(),
            bytes,
            w,
            h,
            DirectXPixelFormat.R8G8B8A8UIntNormalized
        );
        region.PatchSample = region.Sample;
        region.PatchW = w;
        region.PatchH = h;
        return region.Patch;
    }

    /// <summary>释放操作持有的擦除补丁位图（Undo / Clear / Delete / 关窗时调用）。</summary>
    private static void DisposeOpPatch(ScreenshotOp op)
    {
        op.Erase?.Patch?.Dispose();
    }

    #region 工具

    /// <summary>六个工具按钮（模糊/高亮/画笔/形状/擦除/选择）共用：单选互斥，切换后刷新内联选项面板。</summary>
    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        // 拖拽换序结束时，释放点若落在按钮上仍会触发一次 Click（指针被捕获在它身上）：
        // 拖拽实际移动过就丢弃这次 Click，并把勾选态恢复到按下前（避免误切换工具）
        //（RoutedEventArgs 无 Handled 成员——本投影里只有指针/按键等派生参数有，此处直接 return）
        if (_toolDragMoved)
        {
            _toolDragMoved = false;
            if (_toolDragButton is not null && ReferenceEquals(sender, _toolDragButton))
            {
                SetToolChecked(_toolDragButton, _toolPressWasChecked);
            }
            return;
        }
        // 点击后恰好"被点中的按钮" IsChecked 翻转为激活态：把处于激活态、但不是被点中者的其余按钮关掉
        //（ToggleButton 类型名在本项目引用集不可解析：用引用比较代替类型模式匹配）
        if (ToggleButton_Blur.IsChecked == true && !ReferenceEquals(sender, ToggleButton_Blur))
        {
            ToggleButton_Blur.IsChecked = false;
        }
        if (
            ToggleButton_Highlight.IsChecked == true
            && !ReferenceEquals(sender, ToggleButton_Highlight)
        )
        {
            ToggleButton_Highlight.IsChecked = false;
        }
        if (ToggleButton_Brush.IsChecked == true && !ReferenceEquals(sender, ToggleButton_Brush))
        {
            ToggleButton_Brush.IsChecked = false;
        }
        if (ToggleButton_Shapes.IsChecked == true && !ReferenceEquals(sender, ToggleButton_Shapes))
        {
            ToggleButton_Shapes.IsChecked = false;
        }
        if (ToggleButton_Erase.IsChecked == true && !ReferenceEquals(sender, ToggleButton_Erase))
        {
            ToggleButton_Erase.IsChecked = false;
        }
        if (ToggleButton_Select.IsChecked == true && !ReferenceEquals(sender, ToggleButton_Select))
        {
            ToggleButton_Select.IsChecked = false;
        }
        _blurOn = ToggleButton_Blur.IsChecked == true;
        // 工具切换时清掉选中与移动状态
        _selected = null;
        EndMove();
        // 切换工具时收起画笔光标预览
        Grid_BrushPreview.Visibility = Visibility.Collapsed;
        UpdateOptionsPanel();
        Redraw();
    }

    private void UpdateOptionsPanel()
    {
        bool colorActive =
            ToggleButton_Highlight.IsChecked == true || ToggleButton_Brush.IsChecked == true;
        bool shapesActive = ToggleButton_Shapes.IsChecked == true;
        bool selectActive = ToggleButton_Select.IsChecked == true;
        Panel_Strength.Visibility = _blurOn ? Visibility.Visible : Visibility.Collapsed;
        Panel_ColorOptions.Visibility =
            colorActive || shapesActive ? Visibility.Visible : Visibility.Collapsed;
        // 形状选择入口仅形状工具可见（画笔/高亮没有形状概念，颜色与粗细面板共用）
        Button_ShapeSwatch.Visibility = shapesActive ? Visibility.Visible : Visibility.Collapsed;
        Panel_Delete.Visibility = selectActive ? Visibility.Visible : Visibility.Collapsed;
        Button_Delete.IsEnabled = selectActive && _selected is not null;
    }

    /// <summary>形状选择弹层（箭头/圆/框）：单选互斥（引用比较模式），更新当前形状类型与色块。</summary>
    private void ShapeKindButton_Click(object sender, RoutedEventArgs e)
    {
        if (Button_ShapeArrow.IsChecked == true && !ReferenceEquals(sender, Button_ShapeArrow))
        {
            Button_ShapeArrow.IsChecked = false;
        }
        if (Button_ShapeCircle.IsChecked == true && !ReferenceEquals(sender, Button_ShapeCircle))
        {
            Button_ShapeCircle.IsChecked = false;
        }
        if (Button_ShapeBox.IsChecked == true && !ReferenceEquals(sender, Button_ShapeBox))
        {
            Button_ShapeBox.IsChecked = false;
        }
        _shapeKind =
            Button_ShapeCircle.IsChecked == true ? ScreenshotShapeKind.Circle
            : Button_ShapeBox.IsChecked == true ? ScreenshotShapeKind.Box
            : ScreenshotShapeKind.Arrow;
        UpdateShapeSwatch();
    }

    /// <summary>把形状工具色块同步到当前选中的形状。</summary>
    private void UpdateShapeSwatch()
    {
        ShapeSwatch_Arrow.Visibility =
            _shapeKind == ScreenshotShapeKind.Arrow ? Visibility.Visible : Visibility.Collapsed;
        ShapeSwatch_Circle.Visibility =
            _shapeKind == ScreenshotShapeKind.Circle ? Visibility.Visible : Visibility.Collapsed;
        ShapeSwatch_Box.Visibility =
            _shapeKind == ScreenshotShapeKind.Box ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把工具按钮的勾选态设为指定值（ToggleButton 类型名不可解析，用引用比较分发）。</summary>
    private void SetToolChecked(ToggleButton btn, bool isChecked)
    {
        if (ReferenceEquals(btn, ToggleButton_Blur))
        {
            ToggleButton_Blur.IsChecked = isChecked;
        }
        else if (ReferenceEquals(btn, ToggleButton_Highlight))
        {
            ToggleButton_Highlight.IsChecked = isChecked;
        }
        else if (ReferenceEquals(btn, ToggleButton_Brush))
        {
            ToggleButton_Brush.IsChecked = isChecked;
        }
        else if (ReferenceEquals(btn, ToggleButton_Shapes))
        {
            ToggleButton_Shapes.IsChecked = isChecked;
        }
        else if (ReferenceEquals(btn, ToggleButton_Erase))
        {
            ToggleButton_Erase.IsChecked = isChecked;
        }
        else if (ReferenceEquals(btn, ToggleButton_Select))
        {
            ToggleButton_Select.IsChecked = isChecked;
        }
    }

    private bool IsHighlightActive() => ToggleButton_Highlight.IsChecked == true;

    private Color GetCurrentColor() => ColorPicker_Color.Color;

    /// <summary>把当前选色同步到工具栏色块。</summary>
    private void UpdateSwatch()
    {
        ColorSwatch_Fill.Fill = new SolidColorBrush(ColorPicker_Color.Color);
    }

    private float GetCurrentThickness()
    {
        float t = (float)Slider_Thickness.Value;
        if (IsHighlightActive())
        {
            // 高亮带更宽
            t *= 1.6f;
        }
        return t;
    }

    private float GetCurrentStrength() => (float)Slider_Strength.Value;

    /// <summary>画笔/高亮工具是否激活（光标预览仅这两种工具需要）。</summary>
    private bool IsBrushOrHighlightActive()
    {
        return ToggleButton_Highlight.IsChecked == true || ToggleButton_Brush.IsChecked == true;
    }

    /// <summary>图像像素 → 控件像素的显示缩放（ToImagePoint 的逆，光标预览定径用）。</summary>
    private float DisplayScale()
    {
        float cw = (float)Image_Image.ActualWidth;
        float ch = (float)Image_Image.ActualHeight;
        if (cw <= 0f || ch <= 0f)
        {
            return 0f;
        }
        return MathF.Min(cw / _width, ch / _height);
    }

    /// <summary>
    /// 画笔光标预览：圆圈直径 = 当前粗细 × 显示缩放（高亮自动含 ×1.6），中心对齐指针。
    /// 仅画笔/高亮激活且非绘制/移动中显示；其余情况收起。
    /// </summary>
    private void UpdateBrushPreview(Windows.Foundation.Point controlPoint)
    {
        if (!IsBrushOrHighlightActive() || _isDrawing || _moving is not null)
        {
            Grid_BrushPreview.Visibility = Visibility.Collapsed;
            return;
        }
        float scale = DisplayScale();
        if (scale <= 0f)
        {
            Grid_BrushPreview.Visibility = Visibility.Collapsed;
            return;
        }
        float size = Math.Max(2f, GetCurrentThickness() * scale);
        _lastPreviewPoint = controlPoint;
        // 外圈黑色底环（深底色上可见），内圈当前色 + 浅色填充（显示颜色与高亮的半透明感）
        Ellipse_BrushPreviewOuter.Width = size + 3f;
        Ellipse_BrushPreviewOuter.Height = size + 3f;
        Ellipse_BrushPreviewInner.Width = size;
        Ellipse_BrushPreviewInner.Height = size;
        Color c = GetCurrentColor();
        var fill = new SolidColorBrush(c);
        fill.Opacity = 0.2f;
        Ellipse_BrushPreviewInner.Stroke = new SolidColorBrush(c);
        Ellipse_BrushPreviewInner.Fill = fill;
        Grid_BrushPreview.Margin = new Thickness(
            (float)controlPoint.X - (size + 3f) / 2f,
            (float)controlPoint.Y - (size + 3f) / 2f,
            0,
            0
        );
        Grid_BrushPreview.Visibility = Visibility.Visible;
    }

    /// <summary>颜色/粗细变化时按最后位置刷新光标预览（预览不可见时不做事）。</summary>
    private void RefreshBrushPreview()
    {
        if (Grid_BrushPreview.Visibility != Visibility.Visible)
        {
            return;
        }
        UpdateBrushPreview(_lastPreviewPoint);
    }

    private void Button_Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_ops.Count == 0)
            return;
        if (ReferenceEquals(_ops[^1], _selected))
        {
            _selected = null;
        }
        ScreenshotOp removed = _ops[^1];
        _ops.RemoveAt(_ops.Count - 1);
        DisposeOpPatch(removed);
        UpdateOptionsPanel();
        Redraw();
    }

    private void Button_Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (ScreenshotOp op in _ops)
        {
            DisposeOpPatch(op);
        }
        _ops.Clear();
        _current = null;
        _selected = null;
        if (_blurOn)
        {
            _blurOn = false;
            ToggleButton_Blur.IsChecked = false;
            UpdateOptionsPanel();
        }
        Redraw();
    }

    private void Button_Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelected();
    }

    /// <summary>删除当前选中的元素（模糊/擦除区域、笔画）。</summary>
    private void DeleteSelected()
    {
        if (_selected is null || !_ops.Remove(_selected))
            return;
        DisposeOpPatch(_selected);
        _selected = null;
        EndMove();
        UpdateOptionsPanel();
        Redraw();
    }

    /// <summary>Delete / Backspace 删除选中元素（仅选择工具激活时）。</summary>
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (
            e.Key is Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back
            && ToggleButton_Select.IsChecked == true
            && _selected is not null
        )
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private async void Button_Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Button_Save.IsEnabled = false;
            using var rt = NewFullSizeLayer();
            using (var ds = rt.CreateDrawingSession())
            {
                // 保存不带选中指示框（selected 缺省 null）
                ScreenshotMarkupRenderer.Render(
                    ds,
                    _sdrBase,
                    _ops,
                    _current,
                    _markerLayer,
                    GetBlurred,
                    GetErasePatch
                );
            }
            // 元素列表随 XMP 嵌入（供重新打开时恢复）；XMP 由服务统一生成
            string path = await ScreenCaptureService.SaveMarkedUpAsync(rt, _ctx, _ops);
            // 标注结果同步放剪贴板：读回刚保存的文件 → 复用捕获流程的 CF_DIB 机制
            //（显式保存动作，force 不受自动复制开关约束；本投影无 CanvasBitmap.From/RT.GetPixels）
            using (var savedFile = File.OpenRead(path))
            {
                var marked = await CanvasBitmap.LoadAsync(
                    CanvasDevice.GetSharedDevice(),
                    savedFile.AsRandomAccessStream(),
                    96
                );
                await ScreenCaptureService.CopyCaptureToClipboardAsync(marked, force: true);
                marked.Dispose();
            }
            TextBlock_Status.Text = $"{Lang.ScreenshotEditWindow_SavedTo}  {path}";
            Close(); // 保存成功即关闭编辑器
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save marked-up image");
            TextBlock_Status.Text = Lang.ScreenshotEditWindow_SaveFailed;
        }
        finally
        {
            Button_Save.IsEnabled = true;
        }
    }

    private void Button_Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region 绘制

    private void Redraw()
    {
        if (_surface is null)
            return;
        using var ds = _surface.CreateDrawingSession(Colors.Transparent);
        ScreenshotMarkupRenderer.Render(
            ds,
            _sdrBase,
            _ops,
            _current,
            _markerLayer,
            GetBlurred,
            GetErasePatch,
            _selected
        );
    }

    #endregion

    #region 指针

    private void Image_Image_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Vector2 pos = ToImagePoint(e.GetCurrentPoint(Image_Image).Position);
        if (ToggleButton_Select.IsChecked == true)
        {
            // 选择工具：点击命中最上层的已创建元素；点空白取消选择。
            // 命中后继续拖动 = 移动该元素（按下时记录原始几何，拖动按增量平移）
            ScreenshotOp? hit = HitTest(pos);
            _selected = hit;
            _moving = hit;
            _movePressStart = pos;
            if (hit?.Blur is not null)
            {
                _moveOrigRect = hit.Blur.Rect;
            }
            else if (hit?.Erase is not null)
            {
                _moveOrigRect = hit.Erase.Rect;
                _moveOrigSample = hit.Erase.Sample;
            }
            else if (hit?.Stroke is not null)
            {
                _moveOrigPoints = new List<Vector2>(hit.Stroke.Points);
            }
            else if (hit?.Shape is not null)
            {
                _moveOrigShapeStart = hit.Shape.Start;
                _moveOrigShapeEnd = hit.Shape.End;
            }
            UpdateOptionsPanel();
            Redraw();
            e.Handled = true;
            return;
        }
        if (ToggleButton_Erase.IsChecked == true)
        {
            // 擦除：按下点采样原图颜色（原始像素字节），拖出矩形区域填充该颜色
            _blurPressStart = pos;
            var er = new ScreenshotEraseRegion
            {
                Rect = MakeRect(pos, pos),
                Sample = ClampSample(pos),
            };
            er.SampleBytes = SampleErasePixel(er.Sample);
            _current = new ScreenshotOp { Erase = er };
            _isDrawing = true;
            e.Handled = true;
            Redraw();
            return;
        }
        if (ToggleButton_Shapes.IsChecked == true)
        {
            // 形状：按下点 = 起点，拖到终点（箭头为指向；圆/框为对角线）
            _current = new ScreenshotOp
            {
                Shape = new ScreenshotShape
                {
                    Kind = _shapeKind,
                    Start = pos,
                    End = pos,
                    Color = GetCurrentColor(),
                    // 形状粗细取滑块原值（×1.6 加宽仅高亮笔适用）
                    Thickness = (float)Slider_Thickness.Value,
                },
            };
            _isDrawing = true;
            Grid_BrushPreview.Visibility = Visibility.Collapsed;
            e.Handled = true;
            Redraw();
            return;
        }
        if (_blurOn)
        {
            // 区域模糊：从按下点开始拖出矩形
            _blurPressStart = pos;
            _current = new ScreenshotOp
            {
                Blur = new ScreenshotBlurRegion
                {
                    Rect = MakeRect(pos, pos),
                    Strength = GetCurrentStrength(),
                },
            };
            _isDrawing = true;
            e.Handled = true;
            return;
        }
        if (IsHighlightActive() || ToggleButton_Brush.IsChecked == true)
        {
            _current = new ScreenshotOp
            {
                Stroke = new ScreenshotStroke
                {
                    Color = GetCurrentColor(),
                    Thickness = GetCurrentThickness(),
                    IsHighlight = IsHighlightActive(),
                },
            };
            _current.Stroke.Points.Add(pos);
            _isDrawing = true;
            // 开始绘制即收起光标预览
            Grid_BrushPreview.Visibility = Visibility.Collapsed;
            e.Handled = true;
            Redraw();
        }
    }

    private void Image_Image_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 画笔光标预览：非绘制/移动状态下跟随指针（UpdateBrushPreview 内部按工具状态决定显示/收起）
        if (_moving is null && !_isDrawing)
        {
            UpdateBrushPreview(e.GetCurrentPoint(Image_Image).Position);
        }
        if (_moving is not null)
        {
            // 移动选中元素：从按下点起按增量平移原始几何（点没动则 delta≈0，纯点击不产生移动）
            Vector2 p = ToImagePoint(e.GetCurrentPoint(Image_Image).Position);
            Vector2 d = p - _movePressStart;
            if (_moving.Blur is not null)
            {
                _moving.Blur.Rect = TranslateRect(_moveOrigRect, d);
            }
            else if (_moving.Erase is not null)
            {
                _moving.Erase.Rect = TranslateRect(_moveOrigRect, d);
                _moving.Erase.Sample = ClampSample(_moveOrigSample + d);
                _moving.Erase.SampleBytes = SampleErasePixel(_moving.Erase.Sample);
            }
            else if (_moving.Stroke is not null && _moveOrigPoints is not null)
            {
                List<Vector2> movePts = _moving.Stroke.Points;
                for (int i = 0; i < movePts.Count; i++)
                {
                    movePts[i] = _moveOrigPoints[i] + d;
                }
            }
            else if (_moving.Shape is not null)
            {
                _moving.Shape.Start = _moveOrigShapeStart + d;
                _moving.Shape.End = _moveOrigShapeEnd + d;
            }
            e.Handled = true;
            Redraw();
            return;
        }
        if (!_isDrawing || _current is null)
            return;
        Vector2 pos = ToImagePoint(e.GetCurrentPoint(Image_Image).Position);
        if (_current.Blur is not null)
        {
            _current.Blur.Rect = MakeRect(_blurPressStart, pos);
            _current.Blur.Strength = GetCurrentStrength();
            e.Handled = true;
            Redraw();
            return;
        }
        if (_current.Erase is not null)
        {
            _current.Erase.Rect = MakeRect(_blurPressStart, pos);
            e.Handled = true;
            Redraw();
            return;
        }
        if (_current.Shape is not null)
        {
            _current.Shape.End = pos;
            e.Handled = true;
            Redraw();
            return;
        }
        List<Vector2> pts = _current.Stroke.Points;
        Vector2 last = pts[^1];
        if (Vector2.Distance(pos, last) < 1.5f)
            return;
        pts.Add(pos);
        e.Handled = true;
        Redraw();
    }

    private void RootGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndMove();
        CommitCurrent();
        bool wasToolDrag = _toolDragMoved;
        EndToolDrag();
        // 换序拖拽开始时在根捕获过指针，这里释放（EndToolDrag 保留 _toolDragMoved 供 Click 抑制）
        if (wasToolDrag)
        {
            try
            {
                ((Microsoft.UI.Xaml.UIElement)RootGrid).ReleasePointerCapture(e.Pointer);
            }
            catch { }
        }
    }

    private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndMove();
        CommitCurrent();
        bool wasToolDrag = _toolDragMoved;
        EndToolDrag();
        if (wasToolDrag)
        {
            try
            {
                ((Microsoft.UI.Xaml.UIElement)RootGrid).ReleasePointerCapture(e.Pointer);
            }
            catch { }
        }
    }

    /// <summary>结束选中元素的移动拖拽（幂等）。</summary>
    private void EndMove()
    {
        if (_moving is null)
            return;
        _moving = null;
        _moveOrigPoints = null;
    }

    private static Rect TranslateRect(Rect r, Vector2 d)
    {
        return new Rect((float)(r.X + d.X), (float)(r.Y + d.Y), (float)r.Width, (float)r.Height);
    }

    /// <summary>提交当前操作（幂等：释放事件可能同时到达图像区与窗口根）。</summary>
    private void CommitCurrent()
    {
        if (!_isDrawing)
            return;
        _isDrawing = false;
        if (_current?.Blur is not null || _current?.Erase is not null)
        {
            // 拖得太小视为误点，丢弃
            Windows.Foundation.Rect r = _current.Blur?.Rect ?? _current.Erase!.Rect;
            if (r.Width >= 4f && r.Height >= 4f)
            {
                _ops.Add(_current);
            }
        }
        else if (_current?.Shape is not null)
        {
            ScreenshotShape sh = _current.Shape!;
            // 拖得太小视为误点丢弃：箭头按长度，圆/框按包围盒
            bool ok =
                sh.Kind == ScreenshotShapeKind.Arrow
                    ? Vector2.Distance(sh.Start, sh.End) >= 4f
                    : sh.Bounds.Width >= 4f && sh.Bounds.Height >= 4f;
            if (ok)
            {
                _ops.Add(_current);
            }
        }
        else if (_current?.Stroke is not null && _current.Stroke.Points.Count > 0)
        {
            _ops.Add(_current);
        }
        _current = null;
        Redraw();
    }

    private static Rect MakeRect(Vector2 a, Vector2 b)
    {
        float x = Math.Min(a.X, b.X);
        float y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>擦除采样点：钳制在图像边界内（1x1 裁剪不能越界）。</summary>
    private Vector2 ClampSample(Vector2 p)
    {
        return new Vector2(Math.Clamp(p.X, 0f, _width - 1f), Math.Clamp(p.Y, 0f, _height - 1f));
    }

    /// <summary>命中测试：返回点（图像坐标）命中的最上层操作（后创建在上层），未命中返回 null。</summary>
    private ScreenshotOp? HitTest(Vector2 p)
    {
        for (int i = _ops.Count - 1; i >= 0; i--)
        {
            ScreenshotOp op = _ops[i];
            if (op.Blur is not null || op.Erase is not null)
            {
                Rect r = op.Blur?.Rect ?? op.Erase!.Rect;
                if (p.X >= r.X && p.X <= r.X + r.Width && p.Y >= r.Y && p.Y <= r.Y + r.Height)
                {
                    return op;
                }
            }
            else if (op.Shape is not null && ShapeHit(op.Shape, p))
            {
                return op;
            }
            else if (op.Stroke is not null && StrokeHit(op.Stroke, p))
            {
                return op;
            }
        }
        return null;
    }

    /// <summary>点是否在形状轮廓上（箭头到主干/箭头两笔、圆/框到外接框边缘的距离 ≤ 半宽 + 容差）。</summary>
    private static bool ShapeHit(ScreenshotShape sh, Vector2 p)
    {
        float tol = sh.Thickness / 2f + 3f;
        if (sh.Kind is ScreenshotShapeKind.Circle or ScreenshotShapeKind.Box)
        {
            Rect r = sh.Bounds;
            // 到矩形边缘的距离（圆按其外接框近似，命中测试容差内足够）
            float x0 = (float)r.X;
            float y0 = (float)r.Y;
            float x1 = (float)(r.X + r.Width);
            float y1 = (float)(r.Y + r.Height);
            float dx = Math.Max(Math.Max(x0 - p.X, 0f), p.X - x1);
            float dy = Math.Max(Math.Max(y0 - p.Y, 0f), p.Y - y1);
            return MathF.Sqrt(dx * dx + dy * dy) <= tol;
        }
        // 箭头：主干 + 箭头 V 两笔（头部几何与渲染层共用）
        var (shoulder, side) = ScreenshotMarkupRenderer.ArrowHead(sh.Start, sh.End, sh.Thickness);
        if (side == Vector2.Zero)
        {
            return Vector2.Distance(p, sh.Start) <= tol;
        }
        return DistanceToSegment(p, sh.Start, shoulder) <= tol
            || DistanceToSegment(p, shoulder + side, sh.End) <= tol
            || DistanceToSegment(p, sh.End, shoulder - side) <= tol;
    }

    /// <summary>点是否在笔画覆盖范围内（到折线任一端的距离 ≤ 半径 + 容差）。</summary>
    private static bool StrokeHit(ScreenshotStroke st, Vector2 p)
    {
        List<Vector2> pts = st.Points;
        if (pts.Count == 0)
            return false;
        float radius = st.Thickness / 2f + 3f;
        if (pts.Count == 1)
            return Vector2.Distance(p, pts[0]) <= radius;
        for (int i = 1; i < pts.Count; i++)
        {
            if (DistanceToSegment(p, pts[i - 1], pts[i]) <= radius)
                return true;
        }
        return false;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = Vector2.Dot(ab, ab);
        if (len2 < 1e-6f)
            return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// 把 Image 控件内的指针位置换算成原图像素坐标（Stretch=Uniform，含 letterbox 偏移）。
    /// </summary>
    private Vector2 ToImagePoint(Windows.Foundation.Point p)
    {
        float cw = (float)Image_Image.ActualWidth;
        float ch = (float)Image_Image.ActualHeight;
        float w = _width;
        float h = _height;
        float scale = MathF.Min(cw / w, ch / h);
        if (scale <= 0f)
        {
            return Vector2.Zero;
        }
        float offX = (cw - w * scale) / 2f;
        float offY = (ch - h * scale) / 2f;
        float px = (float)p.X;
        float py = (float)p.Y;
        float x = Math.Clamp((px - offX) / scale, 0f, w);
        float y = Math.Clamp((py - offY) / scale, 0f, h);
        return new Vector2(x, y);
    }

    #endregion

    #region 工具按钮拖拽换序

    /// <summary>全部工具按钮（默认排布顺序；换序后按工具按钮组的实际顺序操作）。</summary>
    private ToggleButton[] AllToolButtons()
    {
        return
        [
            ToggleButton_Blur,
            ToggleButton_Highlight,
            ToggleButton_Brush,
            ToggleButton_Shapes,
            ToggleButton_Erase,
            ToggleButton_Select,
        ];
    }

    /// <summary>
    /// 按钮组覆盖层按下：仅左键按下启动换序——按指针 X 定位当前排布序中的按钮、记录拖拽起点，
    /// 并立即在覆盖层捕获指针。普通 Grid 无内部点击态，捕获可靠（ToggleButton 左键按下会吞事件，
    /// 整个换序由覆盖层驱动）；简单点击由移动阈值（见 TryReorderToolDrag）区分并在释放时透传，
    /// 右键/中键按下直接忽略（不启动拖拽、不透传点击）。
    /// </summary>
    private void Overlay_ToolDrag_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 仅左键按下启动换序（右键/中键/触摸/笔一律不劫持）。
        // 两个按键判定源：IsLeftButtonPressed 与 PointerUpdateKind——任一判定为左键即放行，
        // 避免单个 API 在本投影里失真时把左键也挡掉；两者都非左键才拒绝
        bool leftByFlag = e.GetCurrentPoint(Panel_ToolButtons).Properties.IsLeftButtonPressed;
        int kindVal = -1; // -1 = 判定失败（按非左键处理时仍有 leftByFlag 兜底）
        try
        {
            kindVal = (int)e.GetCurrentPoint(Panel_ToolButtons).Properties.PointerUpdateKind;
        }
        catch { }
        bool leftByKind = kindVal == (int)Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed;
        if (!leftByFlag && !leftByKind)
        {
            return; // 右键/中键/触摸/笔：不劫持
        }
        Windows.Foundation.Point p0 = e.GetCurrentPoint(Panel_ToolButtons).Position;
        int idx = ToolButtonIndexAtX((float)p0.X);
        if (idx < 0)
        {
            return;
        }
        _toolDragButton = (ToggleButton)Panel_ToolButtons.Children[idx];
        _toolDragMoved = false;
        _toolPressWasChecked = _toolDragButton.IsChecked == true;
        _toolDragStart = new Vector2((float)p0.X, (float)p0.Y);
        ((Microsoft.UI.Xaml.UIElement)Overlay_ToolDrag).CapturePointer(e.Pointer);
    }

    /// <summary>覆盖层指针移动：拖拽期间按指针 X 实时换位（捕获后所有移动都路由到覆盖层）。</summary>
    private void Overlay_ToolDrag_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        TryReorderToolDrag(e);
    }

    /// <summary>覆盖层指针释放：拖拽过则持久化顺序；未移动则透传为释放点下按钮的一次点击。</summary>
    private void Overlay_ToolDrag_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishToolDrag(e);
    }

    /// <summary>覆盖层指针取消（系统打断等）：仅收尾状态，不透传点击、不持久化。</summary>
    private void Overlay_ToolDrag_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishToolDrag(e, isCancel: true);
    }

    /// <summary>
    /// 结束覆盖层换序拖拽：释放捕获并复位状态。拖拽过则持久化顺序；
    /// 简单点击（未超阈值且非取消）则模拟被点按钮的切换并走公共选择逻辑
    ///（覆盖层挡住了按钮的原生 Click，需在此透传，否则点按无法选中工具）。
    /// </summary>
    private void FinishToolDrag(PointerRoutedEventArgs e, bool isCancel = false)
    {
        ToggleButton btn = _toolDragButton;
        bool moved = _toolDragMoved;
        if (btn is null)
        {
            return;
        }
        _toolDragButton = null;
        try
        {
            ((Microsoft.UI.Xaml.UIElement)Overlay_ToolDrag).ReleasePointerCapture(e.Pointer);
        }
        catch { }
        if (moved)
        {
            PersistToolbarOrder();
            // _toolDragMoved 保留不重置——若释放后仍有余响 Click，ToolButton_Click 会消费并复位
            return;
        }
        if (isCancel)
        {
            return;
        }
        // 简单点击：透传给释放点下的按钮（模拟 ToggleButton 自身的勾选翻转 + 公共选择逻辑）
        Windows.Foundation.Point p = e.GetCurrentPoint(Panel_ToolButtons).Position;
        int idx = ToolButtonIndexAtX((float)p.X);
        if (idx >= 0)
        {
            ToggleButton target = (ToggleButton)Panel_ToolButtons.Children[idx];
            SetToolChecked(target, target.IsChecked != true);
            ToolButton_Click(target, null!); // 事件参数仅用于签名，本方法体不读取 e
        }
    }

    /// <summary>按指针 X 找当前排布序中中心点最近的工具按钮下标（覆盖层与按钮组同宽，必有结果）。</summary>
    private int ToolButtonIndexAtX(float x)
    {
        var children = Panel_ToolButtons.Children;
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < children.Count; i++)
        {
            float d = MathF.Abs(ToolButtonCenterX(children[i]) - x);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// 工具拖拽换序统一入口（幂等）：由覆盖层的 PointerMoved 调用（按下时已在覆盖层捕获指针，
    /// 移动全部路由到覆盖层）；窗口根的冒泡 PointerMoved 作为捕获丢失时的兜底。
    /// 按指针 X 实时换位，保证跨按钮拖拽始终生效（仅左键按住启动，右键/中键不启动）。
    /// </summary>
    private void TryReorderToolDrag(PointerRoutedEventArgs e)
    {
        if (_toolDragButton is null)
        {
            return;
        }
        Windows.Foundation.Point p = e.GetCurrentPoint(Panel_ToolButtons).Position;
        var pv = new Vector2((float)p.X, (float)p.Y);
        if (!_toolDragMoved && Vector2.Distance(pv, _toolDragStart) < 4f)
        {
            return; // 未超阈值按普通点击处理
        }
        if (!_toolDragMoved)
        {
            // 首次越过阈值：确保覆盖层持有指针捕获（按下时已捕获，此处幂等保险——
            // 若捕获丢失，重新捕获后所有移动仍路由到覆盖层）
            ((Microsoft.UI.Xaml.UIElement)Overlay_ToolDrag).CapturePointer(e.Pointer);
        }
        _toolDragMoved = true;
        ReorderToolButton((float)p.X);
    }

    /// <summary>窗口根级指针移动：工具拖拽期间覆盖层捕获丢失时的兜底换位（冒泡事件）。</summary>
    private void RootGrid_ToolDragPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        TryReorderToolDrag(e);
    }

    /// <summary>
    /// 结束工具按钮换序拖拽（幂等）：移动过则持久化顺序。由窗口根的释放/取消统一调用，
    /// 作为覆盖层收尾（FinishToolDrag）的兜底——释放若冒泡到根而覆盖层未处理，此处仍会持久化。
    /// </summary>
    private void EndToolDrag()
    {
        if (_toolDragButton is null)
        {
            return;
        }
        if (_toolDragMoved)
        {
            PersistToolbarOrder();
        }
        _toolDragButton = null;
        // 注意：_toolDragMoved 保留不重置——若释放后仍有余响 Click，
        // ToolButton_Click 会消费它并复位标志
    }

    /// <summary>
    /// 实时换位：按被拖按钮与其他按钮的水平中心比较计算目标序（指针越过谁的中点就换到谁的位置），
    /// 目标序变化时重排工具按钮组子项（布局随子项变更在下一帧自动刷新，中心计算随之更新）。
    /// </summary>
    private void ReorderToolButton(float pointerX)
    {
        if (_toolDragButton is null)
        {
            return;
        }
        var children = Panel_ToolButtons.Children;
        int from = -1;
        var centers = new float[children.Count];
        for (int i = 0; i < centers.Length; i++)
        {
            centers[i] = ToolButtonCenterX(children[i]);
            if (ReferenceEquals(children[i], _toolDragButton))
            {
                from = i;
            }
        }
        if (from < 0)
        {
            return;
        }
        // 目标序 = 中心落在指针左侧的其他按钮个数（自身除外）：
        // 指针越过谁的中点，被拖按钮就换到该位置。必须用指针 X 而非被拖按钮自身的
        // 布局中心——后者在换位前是静态的，会导致目标序恒等于当前序、永不换位。
        int target = 0;
        for (int i = 0; i < centers.Length; i++)
        {
            if (i != from && centers[i] < pointerX)
            {
                target++;
            }
        }
        if (target != from)
        {
            children.RemoveAt(from);
            children.Insert(target, _toolDragButton);
            // 换位后强制一次布局：否则渲染层中心仍停留在旧位置，
            // 后续移动事件会按过期中心计算目标序、按钮卡住不跟随指针。
            Panel_ToolButtons.UpdateLayout();
        }
    }

    /// <summary>
    /// 按钮中心 X（工具按钮组坐标）。
    /// （本投影的 VisualTreeHelper 缺 GetOffset、UIElement 缺 ActualWidth：
    /// 用 UIElement.TransformToVisual + FrameworkElement.ActualWidth 组合）
    /// </summary>
    private float ToolButtonCenterX(object btn)
    {
        var fe = (Microsoft.UI.Xaml.FrameworkElement)btn;
        var t = fe.TransformToVisual(Panel_ToolButtons);
        Windows.Foundation.Point p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
        return (float)p.X + (float)fe.ActualWidth / 2f;
    }

    /// <summary>工具按钮 ↔ 持久化名（配置存储用稳定名，不存控件引用）。</summary>
    private string? ToolButtonName(object? btn)
    {
        if (ReferenceEquals(btn, ToggleButton_Blur))
        {
            return "Blur";
        }
        if (ReferenceEquals(btn, ToggleButton_Highlight))
        {
            return "Highlight";
        }
        if (ReferenceEquals(btn, ToggleButton_Brush))
        {
            return "Brush";
        }
        if (ReferenceEquals(btn, ToggleButton_Shapes))
        {
            return "Shapes";
        }
        if (ReferenceEquals(btn, ToggleButton_Erase))
        {
            return "Erase";
        }
        if (ReferenceEquals(btn, ToggleButton_Select))
        {
            return "Select";
        }
        return null;
    }

    private object? ToolButtonByName(string name)
    {
        return name switch
        {
            "Blur" => ToggleButton_Blur,
            "Highlight" => ToggleButton_Highlight,
            "Brush" => ToggleButton_Brush,
            "Shapes" => ToggleButton_Shapes,
            "Erase" => ToggleButton_Erase,
            "Select" => ToggleButton_Select,
            _ => null,
        };
    }

    /// <summary>把当前工具按钮顺序写入配置（跨会话持久化）。</summary>
    private void PersistToolbarOrder()
    {
        var names = new List<string>();
        foreach (var child in Panel_ToolButtons.Children)
        {
            string? n = ToolButtonName(child);
            if (n is not null)
            {
                names.Add(n);
            }
        }
        AppConfig.ScreenshotEditToolbarOrder = names;
    }

    /// <summary>按配置恢复工具按钮顺序（新加入的工具不在保存顺序中时追加在末尾）。</summary>
    private void ApplySavedToolbarOrder()
    {
        List<string> saved = AppConfig.ScreenshotEditToolbarOrder;
        if (saved.Count == 0)
        {
            return;
        }
        var wanted = new List<object>();
        foreach (string name in saved)
        {
            object? btn = ToolButtonByName(name);
            if (btn is not null && !wanted.Contains(btn))
            {
                wanted.Add(btn);
            }
        }
        foreach (ToggleButton btn in AllToolButtons())
        {
            if (!wanted.Contains(btn))
            {
                wanted.Add(btn);
            }
        }
        var children = Panel_ToolButtons.Children;
        foreach (ToggleButton btn in AllToolButtons())
        {
            children.Remove(btn);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            children.Insert(i, (Microsoft.UI.Xaml.UIElement)wanted[i]);
        }
    }

    #endregion

    private void ScreenshotEditWindow_Closed(object sender, WindowEventArgs args)
    {
        this.Closed -= ScreenshotEditWindow_Closed;
        // CanvasImageSource 在本 Win2D 版本非 IDisposable（与代码库其他窗口一致，交给 GC）
        _sdrBase?.Dispose();
        _markerLayer?.Dispose();
        foreach (ScreenshotOp op in _ops)
        {
            DisposeOpPatch(op);
        }
        foreach (CanvasRenderTarget rt in _blurredCache.Values)
        {
            rt.Dispose();
        }
    }
}
