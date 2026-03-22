using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace SonySmartControl.Helpers;

/// <summary>
/// 将带 EXIF Orientation 的 JPEG/内嵌预览解码为「所见即所得」位图（像素方向与构图一致）。
/// </summary>
public static class ExifOrientationNormalizer
{
    /// <summary>从整文件及内嵌 JPEG 段尝试读取 Orientation；失败返回 1。</summary>
    public static int TryReadOrientation(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return 1;

        try
        {
            using var stream = File.OpenRead(path);
            var o = TryReadOrientationFromDirectories(ImageMetadataReader.ReadMetadata(stream));
            if (o != 1)
                return o;
        }
        catch
        {
            // 忽略
        }

        foreach (var seg in EmbeddedJpegPreviewLoader.GetEmbeddedJpegSegmentsForMetadata(path))
        {
            try
            {
                using var ms = new MemoryStream(seg, writable: false);
                var o = TryReadOrientationFromDirectories(ImageMetadataReader.ReadMetadata(ms));
                if (o != 1)
                    return o;
            }
            catch
            {
                // 忽略单段
            }
        }

        return 1;
    }

    private static int TryReadOrientationFromDirectories(
        IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        foreach (var d in directories)
        {
            if (d.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 1 and <= 8)
                return o;
        }

        return 1;
    }

    /// <summary>
    /// 按 EXIF 方向校正；若无需变换则返回 <paramref name="bitmap"/>，否则释放原图并返回新图。
    /// </summary>
    public static Bitmap Apply(Bitmap bitmap, int orientation)
    {
        if (orientation <= 1 || orientation > 8)
            return bitmap;

        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        if (w <= 0 || h <= 0)
            return bitmap;

        var transformed = Transform(bitmap, orientation, w, h);
        if (!ReferenceEquals(transformed, bitmap))
            bitmap.Dispose();
        return transformed;
    }

    private static Bitmap Transform(Bitmap source, int orientation, int w, int h)
    {
        // 变换矩阵与 Canvas/HTML 常见 EXIF 校正一致（坐标系 Y 向下）。
        return orientation switch
        {
            2 => DrawWithMatrix(source, new PixelSize(w, h), new Matrix(-1, 0, 0, 1, w, 0)),
            3 => DrawWithMatrix(source, new PixelSize(w, h), new Matrix(-1, 0, 0, -1, w, h)),
            4 => DrawWithMatrix(source, new PixelSize(w, h), new Matrix(1, 0, 0, -1, 0, h)),
            5 => DrawWithMatrix(source, new PixelSize(h, w), new Matrix(0, 1, 1, 0, 0, 0)),
            6 => DrawWithMatrix(source, new PixelSize(h, w), new Matrix(0, 1, -1, 0, h, 0)),
            7 => DrawWithMatrix(source, new PixelSize(h, w),
                Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(-w, h) * Matrix.CreateScale(1, -1)),
            8 => DrawWithMatrix(source, new PixelSize(h, w), new Matrix(0, -1, 1, 0, 0, w)),
            _ => source,
        };
    }

    private static Bitmap DrawWithMatrix(Bitmap source, PixelSize destSize, Matrix transform)
    {
        var rtb = new RenderTargetBitmap(destSize, source.Dpi);
        using (var ctx = rtb.CreateDrawingContext(true))
        using (ctx.PushTransform(transform))
        {
            ctx.DrawImage(source, new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height));
        }

        return rtb;
    }
}
