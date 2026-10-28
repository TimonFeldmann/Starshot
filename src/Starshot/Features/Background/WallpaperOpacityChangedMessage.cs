namespace Starshot.Features.Background;

/// <summary>
/// 壁纸不透明度变更消息（0–100）。设置页拖动滑条时实时 Send，
/// AppBackground 收到后只改 Image.Opacity，不重新加载壁纸（拖动才够顺）。
/// </summary>
public sealed class WallpaperOpacityChangedMessage
{
    public int Opacity { get; init; }
}
