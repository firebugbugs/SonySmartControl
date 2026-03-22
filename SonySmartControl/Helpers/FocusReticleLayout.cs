using Avalonia;

namespace SonySmartControl.Helpers;

/// <summary>遥控触摸取消按钮位置（锚定在机身 Live View 对焦框右上角）。</summary>
public static class FocusReticleLayout
{
    public const double CloseButtonSize = 22;

    /// <summary>关闭按钮左上角相对预览容器的 Margin（Left/Top）；归一化坐标为相对整幅预览图 0..1。</summary>
    public static bool TryComputeCloseButtonMarginForNormalizedRect(
        Size previewSize,
        PixelSize bitmapPixelSize,
        double normalizedLeft,
        double normalizedTop,
        double normalizedWidth,
        double normalizedHeight,
        double closeButtonSize,
        out Thickness margin)
    {
        margin = default;
        if (previewSize.Width <= 0 || previewSize.Height <= 0)
            return false;

        var dest = UniformImageDestRect.Compute(previewSize, bitmapPixelSize);
        if (dest.Width <= 1 || dest.Height <= 1)
            return false;

        var l = Math.Clamp(normalizedLeft, 0, 1);
        var t = Math.Clamp(normalizedTop, 0, 1);
        var rw = Math.Clamp(normalizedWidth, 0, 1);
        var rh = Math.Clamp(normalizedHeight, 0, 1);
        var rect = new Rect(
            dest.X + l * dest.Width,
            dest.Y + t * dest.Height,
            rw * dest.Width,
            rh * dest.Height);
        if (rect.Width < 0.5 || rect.Height < 0.5)
            return false;

        // 按钮中心与对焦框右上角重合（与原先小框一致）
        var half = closeButtonSize * 0.5;
        var bx = rect.Right - half;
        var by = rect.Top - half;
        margin = new Thickness(bx, by, 0, 0);
        return true;
    }
}
