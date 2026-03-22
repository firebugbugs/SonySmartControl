using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SonySmartControl.Helpers;
using SonySmartControl.Interop;

namespace SonySmartControl.Controls;

/// <summary>机身 Live View 对焦框叠加（多矩形，与 Uniform 图像区域对齐）。</summary>
public sealed class AfFocusFramesOverlay : Control
{
    public static readonly StyledProperty<IImage?> SourceImageProperty =
        AvaloniaProperty.Register<AfFocusFramesOverlay, IImage?>(nameof(SourceImage));

    public static readonly StyledProperty<IReadOnlyList<CrSdkLiveViewFocusFrameView>?> FramesProperty =
        AvaloniaProperty.Register<AfFocusFramesOverlay, IReadOnlyList<CrSdkLiveViewFocusFrameView>?>(nameof(Frames));

    public IImage? SourceImage
    {
        get => GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    public IReadOnlyList<CrSdkLiveViewFocusFrameView>? Frames
    {
        get => GetValue(FramesProperty);
        set => SetValue(FramesProperty, value);
    }

    static AfFocusFramesOverlay()
    {
        AffectsRender<AfFocusFramesOverlay>(SourceImageProperty, FramesProperty);
    }

    public AfFocusFramesOverlay()
    {
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (SourceImage is not Bitmap bmp || Frames is not { Count: > 0 } list)
            return;

        var dest = UniformImageDestRect.Compute(Bounds.Size, bmp.PixelSize);
        if (dest.Width <= 1 || dest.Height <= 1)
            return;

        foreach (var f in list)
        {
            var l = Math.Clamp(f.Left, 0, 1);
            var t = Math.Clamp(f.Top, 0, 1);
            var rw = Math.Clamp(f.Width, 0, 1);
            var rh = Math.Clamp(f.Height, 0, 1);
            var rect = new Rect(
                dest.X + l * dest.Width,
                dest.Y + t * dest.Height,
                rw * dest.Width,
                rh * dest.Height);
            if (rect.Width < 0.5 || rect.Height < 0.5)
                continue;

            var color = f.IsFocused
                ? Color.FromArgb(255, 40, 220, 90)
                : Color.FromArgb(230, 255, 255, 255);
            var pen = new Pen(new SolidColorBrush(color), 2.0);
            context.DrawRectangle(null, pen, rect);
        }
    }
}
