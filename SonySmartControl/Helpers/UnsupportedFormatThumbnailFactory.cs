using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SonySmartControl.Helpers;

public static class UnsupportedFormatThumbnailFactory
{
    public static Bitmap CreateHeifPlaceholder(int width, int height)
    {
        width = Math.Clamp(width, 32, 1024);
        height = Math.Clamp(height, 32, 1024);

        var bmp = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext(true))
        {
            var bg = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#3A3F4A"), 0),
                    new GradientStop(Color.Parse("#1F2229"), 1),
                },
            };

            ctx.FillRectangle(bg, new Rect(0, 0, width, height));
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#808894")), 2),
                new Rect(1, 1, width - 2, height - 2), 10);

            // 不依赖 FormattedText 的测量 API：用斜纹/叉号做占位「糊图」效果，确保跨版本可编译。
            var stripePen = new Pen(new SolidColorBrush(Color.Parse("#40FFFFFF")), 2);
            var step = Math.Max(10, Math.Min(width, height) / 10);
            for (var x = -height; x < width; x += step)
            {
                ctx.DrawLine(stripePen, new Point(x, 0), new Point(x + height, height));
            }

            var crossPen = new Pen(new SolidColorBrush(Color.Parse("#90FFFFFF")), 3);
            ctx.DrawLine(crossPen, new Point(width * 0.25, height * 0.25), new Point(width * 0.75, height * 0.75));
            ctx.DrawLine(crossPen, new Point(width * 0.75, height * 0.25), new Point(width * 0.25, height * 0.75));
        }

        return bmp;
    }
}

