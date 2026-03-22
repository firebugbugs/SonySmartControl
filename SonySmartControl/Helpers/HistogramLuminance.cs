using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace SonySmartControl.Helpers;

/// <summary>
/// 从位图复制像素（BGRA）后采样计算亮度直方图（256 档），并归一化到 [0,1] 便于绘制。
/// 使用 <see cref="Bitmap.CopyPixels"/>：解码后的 <see cref="Bitmap"/> 不一定提供 <c>Lock()</c>（仅 <see cref="WriteableBitmap"/> 有）。
/// </summary>
public static class HistogramLuminance
{
    /// <summary>目标最大采样点数，过大时自动增大步长。</summary>
    private const int TargetMaxSamples = 96_000;

    /// <returns>长度恒为 <paramref name="bins"/> 的数组；无法读取像素时返回 null。</returns>
    public static double[]? ComputeNormalized(Bitmap bitmap, int bins = 256)
    {
        if (bins < 2)
            return null;

        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        if (w <= 0 || h <= 0)
            return null;

        var stride = w * 4;
        var bufferSize = checked(stride * h);
        var buffer = new byte[bufferSize];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bufferSize, stride);
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Free();
        }

        var step = ComputeStep(w, h);
        var counts = new int[bins];

        for (var y = 0; y < h; y += step)
        {
            var rowStart = y * stride;
            for (var x = 0; x < w; x += step)
            {
                var offset = rowStart + x * 4;
                var b = buffer[offset];
                var g = buffer[offset + 1];
                var r = buffer[offset + 2];
                // 整数近似：Y ≈ 0.299R + 0.587G + 0.114B
                var lum = (r * 77 + g * 150 + b * 29) >> 8;
                var binIndex = (lum * bins) / 256;
                if (binIndex >= bins)
                    binIndex = bins - 1;
                counts[binIndex]++;
            }
        }

        var max = 0;
        for (var i = 0; i < bins; i++)
        {
            if (counts[i] > max)
                max = counts[i];
        }

        var result = new double[bins];
        if (max <= 0)
            return result;

        var inv = 1.0 / max;
        for (var i = 0; i < bins; i++)
            result[i] = counts[i] * inv;

        return result;
    }

    private static int ComputeStep(int width, int height)
    {
        var total = (long)width * height;
        if (total <= TargetMaxSamples)
            return 1;
        var step = (int)Math.Ceiling(Math.Sqrt((double)total / TargetMaxSamples));
        return Math.Max(2, step);
    }
}
