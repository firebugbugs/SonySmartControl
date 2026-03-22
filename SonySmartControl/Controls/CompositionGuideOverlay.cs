using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SonySmartControl.Helpers;

namespace SonySmartControl.Controls;

/// <summary>预览画面上叠加的构图辅助线（与 Uniform 图像区域对齐）。</summary>
public sealed class CompositionGuideOverlay : Control
{
    public static readonly StyledProperty<int> ModeIndexProperty =
        AvaloniaProperty.Register<CompositionGuideOverlay, int>(nameof(ModeIndex));

    public static readonly StyledProperty<IImage?> SourceImageProperty =
        AvaloniaProperty.Register<CompositionGuideOverlay, IImage?>(nameof(SourceImage));

    public int ModeIndex
    {
        get => GetValue(ModeIndexProperty);
        set => SetValue(ModeIndexProperty, value);
    }

    public IImage? SourceImage
    {
        get => GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    static CompositionGuideOverlay()
    {
        AffectsRender<CompositionGuideOverlay>(ModeIndexProperty, SourceImageProperty);
    }

    public CompositionGuideOverlay()
    {
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (ModeIndex <= 0)
            return;
        if (SourceImage is not Bitmap bmp)
            return;

        var dest = UniformImageDestRect.Compute(Bounds.Size, bmp.PixelSize);
        if (dest.Width <= 1 || dest.Height <= 1)
            return;

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.0);

        switch (ModeIndex)
        {
            case 1: // 三分法
                DrawThirds(context, dest, pen);
                break;
            case 2: // 十字对准线
                DrawCenterCross(context, dest, pen);
                break;
            case 3: // 对角线
                DrawDiagonals(context, dest, pen);
                break;
            case 4: // 安全区
                DrawSafeArea(context, dest, pen);
                break;
        }
    }

    private static void DrawThirds(DrawingContext context, Rect dest, IPen pen)
    {
        var x1 = dest.X + dest.Width / 3.0;
        var x2 = dest.X + dest.Width * 2.0 / 3.0;
        var y1 = dest.Y + dest.Height / 3.0;
        var y2 = dest.Y + dest.Height * 2.0 / 3.0;
        context.DrawLine(pen, new Point(x1, dest.Y), new Point(x1, dest.Bottom));
        context.DrawLine(pen, new Point(x2, dest.Y), new Point(x2, dest.Bottom));
        context.DrawLine(pen, new Point(dest.X, y1), new Point(dest.Right, y1));
        context.DrawLine(pen, new Point(dest.X, y2), new Point(dest.Right, y2));
    }

    private static void DrawCenterCross(DrawingContext context, Rect dest, IPen pen)
    {
        var cx = dest.X + dest.Width * 0.5;
        var cy = dest.Y + dest.Height * 0.5;
        context.DrawLine(pen, new Point(dest.X, cy), new Point(dest.Right, cy));
        context.DrawLine(pen, new Point(cx, dest.Y), new Point(cx, dest.Bottom));
    }

    private static void DrawDiagonals(DrawingContext context, Rect dest, IPen pen)
    {
        context.DrawLine(pen, dest.TopLeft, dest.BottomRight);
        context.DrawLine(pen, dest.TopRight, dest.BottomLeft);
    }

    private static void DrawSafeArea(DrawingContext context, Rect dest, IPen pen)
    {
        const double inset = 0.05;
        var mx = dest.Width * inset;
        var my = dest.Height * inset;
        var inner = new Rect(dest.X + mx, dest.Y + my, dest.Width - 2 * mx, dest.Height - 2 * my);
        if (inner.Width > 0 && inner.Height > 0)
            context.DrawRectangle(pen, inner);
    }
}
