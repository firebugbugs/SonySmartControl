using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SonySmartControl.Controls;

/// <summary>紧凑亮度直方图条（归一化 bins，用于缩略图 Tooltip）。</summary>
public sealed class HistogramMiniControl : Control
{
    public static readonly StyledProperty<double[]?> BinsProperty =
        AvaloniaProperty.Register<HistogramMiniControl, double[]?>(nameof(Bins));

    public double[]? Bins
    {
        get => GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    static HistogramMiniControl()
    {
        AffectsRender<HistogramMiniControl>(BinsProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bins = Bins;
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (bins == null || bins.Length == 0 || w <= 0 || h <= 0)
            return;

        var n = bins.Length;
        var barW = w / n;
        var fill = new SolidColorBrush(Color.FromArgb(220, 120, 190, 255));
        var baseline = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

        context.FillRectangle(baseline, new Rect(0, h - 1, w, 1));

        for (var i = 0; i < n; i++)
        {
            var t = bins[i];
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            var bh = t * (h - 4);
            if (bh < 0.5 && t > 0)
                bh = 0.5;
            var x = i * barW;
            var r = new Rect(x + 0.15, h - bh - 1, Math.Max(0.4, barW - 0.3), bh);
            context.FillRectangle(fill, r);
        }
    }
}
