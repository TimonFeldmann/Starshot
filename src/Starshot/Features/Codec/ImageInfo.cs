using System;
using Microsoft.Graphics.Canvas;
using Starward.Codec.ICC;

namespace Starshot.Features.Codec;

internal class ImageInfo : IDisposable
{
    public CanvasBitmap CanvasBitmap { get; set; }

    public bool HDR { get; set; }

    public ColorPrimaries ColorPrimaries { get; set; }

    public byte[]? IccData { get; set; }

    /// <summary>XMP 元数据（含 Starshot 嵌入的标注元素 ss:Markup，供编辑器恢复元素用）。</summary>
    public byte[]? XmpData { get; set; }

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing) { }
            CanvasBitmap.Dispose();
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
