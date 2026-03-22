using Avalonia;

namespace SonySmartControl.Helpers;

/// <summary>
/// 将 Uniform 拉伸后的点击坐标映射到位图像素上的归一化坐标 (0..1)。
/// </summary>
internal static class PreviewHitTest
{
    public static bool TryGetNormalizedImageCoords(
        Point localInControl,
        Size controlSize,
        PixelSize bitmapSize,
        out double nx,
        out double ny)
    {
        nx = ny = 0;
        var iw = bitmapSize.Width;
        var ih = bitmapSize.Height;
        if (iw <= 0 || ih <= 0)
            return false;

        var cw = controlSize.Width;
        var ch = controlSize.Height;
        if (cw <= 0 || ch <= 0)
            return false;

        var scale = Math.Min(cw / iw, ch / ih);
        var dw = iw * scale;
        var dh = ih * scale;
        var ox = (cw - dw) * 0.5;
        var oy = (ch - dh) * 0.5;

        var x = localInControl.X - ox;
        var y = localInControl.Y - oy;
        if (x < 0 || y < 0 || x > dw || y > dh)
            return false;

        var px = x / dw * iw;
        var py = y / dh * ih;
        nx = px / iw;
        ny = py / ih;
        return true;
    }
}
