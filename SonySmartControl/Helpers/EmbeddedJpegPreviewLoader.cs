using System;
using System.IO;
using Avalonia.Media.Imaging;

namespace SonySmartControl.Helpers;

/// <summary>
/// 部分 RAW（如 Sony ARW）内含嵌入式 JPEG 预览；Skia 无法整文件解码时，从文件中尝试各 SOI 起解码（解码器读到 JPEG 末尾即停）。
/// </summary>
public static class EmbeddedJpegPreviewLoader
{
    private const int MinDecodedPixels = 256 * 256;

    /// <summary>在直接 <see cref="Bitmap(Stream)"/> 失败时调用；成功则调用方负责释放返回的 <see cref="Bitmap"/>。</summary>
    public static Bitmap? TryDecodeLargestEmbeddedJpeg(string path)
    {
        return TryDecodeLargestEmbeddedJpeg(path, MinDecodedPixels);
    }

    /// <summary>
    /// 尝试解码文件中最大的内嵌 JPEG。<paramref name="minDecodedPixels"/> 可用于放宽阈值（例如 HEIF 的 160×120 预览也需要显示）。
    /// </summary>
    public static Bitmap? TryDecodeLargestEmbeddedJpeg(string path, int minDecodedPixels)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        if (minDecodedPixels < 1)
            minDecodedPixels = 1;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        var scanLen = Math.Min(bytes.Length, 64 * 1024 * 1024);

        Bitmap? best = null;
        var bestArea = 0;
        var attempts = 0;
        const int maxAttempts = 48;

        for (var s = 0; s < scanLen - 2 && attempts < maxAttempts; s++)
        {
            if (bytes[s] != 0xFF || bytes[s + 1] != 0xD8)
                continue;

            attempts++;
            Bitmap? candidate = null;
            try
            {
                using var ms = new MemoryStream(bytes, s, scanLen - s, writable: false, publiclyVisible: true);
                candidate = new Bitmap(ms);
                var area = candidate.PixelSize.Width * candidate.PixelSize.Height;
                if (area < minDecodedPixels)
                {
                    candidate.Dispose();
                    continue;
                }

                if (area > bestArea)
                {
                    best?.Dispose();
                    best = candidate;
                    candidate = null;
                    bestArea = area;
                }
                else
                    candidate.Dispose();
            }
            catch
            {
                candidate?.Dispose();
            }
        }

        return best;
    }

    /// <summary>
    /// 枚举文件中各 SOI→EOI 的 JPEG 段（用于 EXIF：大图预览常无完整 EXIF，需与较小缩略图合并）。
    /// </summary>
    public static IReadOnlyList<byte[]> GetEmbeddedJpegSegmentsForMetadata(string path, int maxSegments = 160)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Array.Empty<byte[]>();

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return Array.Empty<byte[]>();
        }

        var scanLen = Math.Min(bytes.Length, 64 * 1024 * 1024);
        // 缩略图可较小但仍含完整 EXIF；过小则易把噪声当 SOI
        const int minLen = 512;
        var list = new List<byte[]>(16);
        var attempts = 0;

        for (var s = 0; s < scanLen - 2 && attempts < maxSegments; s++)
        {
            if (bytes[s] != 0xFF || bytes[s + 1] != 0xD8)
                continue;

            attempts++;
            for (var e = s + 2; e < scanLen - 1; e++)
            {
                if (bytes[e] != 0xFF || bytes[e + 1] != 0xD9)
                    continue;

                var len = e + 2 - s;
                if (len < minLen)
                    break;

                var copy = new byte[len];
                Buffer.BlockCopy(bytes, s, copy, 0, len);
                list.Add(copy);
                break;
            }
        }

        return list;
    }

    /// <summary>提取文件中最大的 JPEG 段字节（SOI→EOI），供 EXIF 等解析；与 <see cref="TryDecodeLargestEmbeddedJpeg"/> 同源扫描逻辑。</summary>
    public static byte[]? GetLargestEmbeddedJpegBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }

        var scanLen = Math.Min(bytes.Length, 64 * 1024 * 1024);
        byte[]? best = null;
        var bestLen = 0;
        var attempts = 0;
        const int maxAttempts = 48;
        const int minLen = 4096;

        for (var s = 0; s < scanLen - 2 && attempts < maxAttempts; s++)
        {
            if (bytes[s] != 0xFF || bytes[s + 1] != 0xD8)
                continue;

            attempts++;
            for (var e = s + 2; e < scanLen - 1; e++)
            {
                if (bytes[e] != 0xFF || bytes[e + 1] != 0xD9)
                    continue;

                var len = e + 2 - s;
                if (len < minLen)
                    break;

                if (len > bestLen)
                {
                    best = new byte[len];
                    Buffer.BlockCopy(bytes, s, best, 0, len);
                    bestLen = len;
                }

                break;
            }
        }

        return best;
    }
}
