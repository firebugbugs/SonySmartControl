using Avalonia;

namespace SonySmartControl.Helpers;

/// <summary>与 <see cref="Avalonia.Controls.Image"/> <c>Stretch.Uniform</c> 一致的图像内容矩形（用于叠加层对齐）。</summary>
public static class UniformImageDestRect
{
    public static Rect Compute(Size container, PixelSize imagePixels)
    {
        if (imagePixels.Width <= 0 || imagePixels.Height <= 0)
            return default;
        var cw = container.Width;
        var ch = container.Height;
        if (cw <= 0 || ch <= 0)
            return default;

        var iw = imagePixels.Width;
        var ih = imagePixels.Height;
        var scale = Math.Min(cw / iw, ch / ih);
        var w = iw * scale;
        var h = ih * scale;
        var x = (cw - w) * 0.5;
        var y = (ch - h) * 0.5;
        return new Rect(x, y, w, h);
    }
}
