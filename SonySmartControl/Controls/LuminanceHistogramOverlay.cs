using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SonySmartControl.Helpers;

namespace SonySmartControl.Controls;

/// <summary>叠加在预览上的亮度直方图（归一化柱高），带边框；锚定在 Uniform 图像内容区域的右上角。</summary>
public sealed class LuminanceHistogramOverlay : Control
{
    public static readonly StyledProperty<IImage?> SourceImageProperty =
        AvaloniaProperty.Register<LuminanceHistogramOverlay, IImage?>(nameof(SourceImage));

    public static readonly StyledProperty<double[]?> BinsProperty =
        AvaloniaProperty.Register<LuminanceHistogramOverlay, double[]?>(nameof(Bins));

    public IImage? SourceImage
    {
        get => GetValue(SourceImageProperty);
        set => SetValue(SourceImageProperty, value);
    }

    public double[]? Bins
    {
        get => GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    static LuminanceHistogramOverlay()
    {
        AffectsRender<LuminanceHistogramOverlay>(BinsProperty, SourceImageProperty);
    }

    public LuminanceHistogramOverlay()
    {
        IsHitTestVisible = false;
    }

    private const double HistogramWidth = 208;
    private const double HistogramHeight = 112;
    private const double OuterMargin = 10;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var container = Bounds.Size;
        if (container.Width <= 1 || container.Height <= 1)
            return;

        Rect chartBounds;
        if (SourceImage is Bitmap bmp)
        {
            var dest = UniformImageDestRect.Compute(container, bmp.PixelSize);
            if (dest.Width <= 1 || dest.Height <= 1)
                return;
            chartBounds = new Rect(
                dest.Right - HistogramWidth - OuterMargin,
                dest.Y + OuterMargin,
                HistogramWidth,
                HistogramHeight);
        }
        else
        {
            chartBounds = new Rect(
                container.Width - HistogramWidth - OuterMargin,
                OuterMargin,
                HistogramWidth,
                HistogramHeight);
        }

        DrawChart(context, chartBounds, Bins);
    }

    private static void DrawChart(DrawingContext context, Rect bounds, double[]? bins)
    {
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        var bg = new SolidColorBrush(Color.FromArgb(140, 20, 20, 24));
        context.FillRectangle(bg, bounds);

        const double borderW = 1.25;
        const double innerPad = 5;
        var inset = borderW + innerPad;

        if (bins == null || bins.Length == 0)
        {
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 220, 220, 230)), borderW);
            var half = borderW * 0.5;
            context.DrawRectangle(borderPen, new Rect(bounds.X + half, bounds.Y + half, bounds.Width - borderW, bounds.Height - borderW));
            return;
        }

        var chartW = bounds.Width - inset * 2;
        var chartH = bounds.Height - inset * 2;
        if (chartW <= 0 || chartH <= 0)
            return;

        var n = bins.Length;
        var barW = chartW / n;
        var fill = new SolidColorBrush(Color.FromArgb(230, 110, 255, 150));

        for (var i = 0; i < n; i++)
        {
            var t = bins[i];
            if (t <= 0)
                continue;
            var bh = t * chartH;
            var x = bounds.X + inset + i * barW;
            var y = bounds.Y + inset + chartH - bh;
            var w = Math.Max(0.8, barW - 0.3);
            context.FillRectangle(fill, new Rect(x, y, w, bh));
        }

        {
            var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 220, 220, 230)), borderW);
            var half = borderW * 0.5;
            context.DrawRectangle(
                borderPen,
                new Rect(bounds.X + half, bounds.Y + half, bounds.Width - borderW, bounds.Height - borderW));
        }
    }
}
