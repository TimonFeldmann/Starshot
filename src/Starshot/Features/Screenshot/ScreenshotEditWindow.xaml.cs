using System;
using System.Collections.Generic;
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
    }

    /// <summary>HDR（R16G16B16A16Float）复用保存管线的色调映射；SDR 直接 8 位拷贝。</summary>
    private static CanvasRenderTarget BuildSdrBase(
        ICanvasImage source,
        ScreenshotEditContext ctx
    )
    {
        if (
            source is CanvasBitmap bmp
            && bmp.Format is DirectXPixelFormat.R16G16B16A16Float
        )
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
                    new GaussianBlurEffect
                    {
                        Source = _sdrBase,
                        BlurAmount = region.Strength,
                    }
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

    /// <summary>五个工具按钮（模糊/高亮/画笔/擦除/选择）共用：单选互斥，切换后刷新内联选项面板。</summary>
    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
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
        UpdateOptionsPanel();
        Redraw();
    }

    private void UpdateOptionsPanel()
    {
        bool colorActive =
            ToggleButton_Highlight.IsChecked == true
            || ToggleButton_Brush.IsChecked == true;
        bool selectActive = ToggleButton_Select.IsChecked == true;
        Panel_Strength.Visibility = _blurOn ? Visibility.Visible : Visibility.Collapsed;
        Panel_ColorOptions.Visibility = colorActive ? Visibility.Visible : Visibility.Collapsed;
        Panel_Delete.Visibility = selectActive ? Visibility.Visible : Visibility.Collapsed;
        Button_Delete.IsEnabled = selectActive && _selected is not null;
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
            TextBlock_Status.Text = $"{Lang.ScreenshotEditWindow_SavedTo}  {path}";
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
            _current = new ScreenshotOp
            {
                Erase = er,
            };
            _isDrawing = true;
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
            e.Handled = true;
            Redraw();
        }
    }

    private void Image_Image_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
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
    }

    private void RootGrid_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndMove();
        CommitCurrent();
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
        return new Vector2(
            Math.Clamp(p.X, 0f, _width - 1f),
            Math.Clamp(p.Y, 0f, _height - 1f)
        );
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
                if (
                    p.X >= r.X && p.X <= r.X + r.Width
                    && p.Y >= r.Y && p.Y <= r.Y + r.Height
                )
                {
                    return op;
                }
            }
            else if (op.Stroke is not null && StrokeHit(op.Stroke, p))
            {
                return op;
            }
        }
        return null;
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
            return Vector2.Zero;
        float offX = (cw - w * scale) / 2f;
        float offY = (ch - h * scale) / 2f;
        float px = (float)p.X;
        float py = (float)p.Y;
        float x = Math.Clamp((px - offX) / scale, 0f, w);
        float y = Math.Clamp((py - offY) / scale, 0f, h);
        return new Vector2(x, y);
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
