using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace SonySmartControl.Helpers;

/// <summary>从磁盘加载照片并缩放到适合界面展示的尺寸，避免整幅原图常驻内存与 GPU。</summary>
public static class DiskImagePreviewLoader
{
    /// <summary>相对原图宽、高的显示比例（0.1 = 各边保留 10%，约 1% 像素）。</summary>
    public const double UiDisplayScale = 0.1;

    private static readonly SemaphoreSlim DecodeConcurrency = new(3);
    private static readonly string[] HeifLikeExtensions = [".hif", ".heif", ".heic"];

    private static bool IsHeifLike(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(ext))
            return false;
        ext = ext.ToLowerInvariant();
        foreach (var e in HeifLikeExtensions)
        {
            if (ext == e)
                return true;
        }
        return false;
    }

    public static Bitmap? LoadScaled(string path, double scale = UiDisplayScale)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        if (scale is <= 0 or > 1)
            scale = UiDisplayScale;

        Bitmap? full = null;
        try
        {
            using var stream = File.OpenRead(path);
            full = new Bitmap(stream);
        }
        catch
        {
            // HEIF 先走系统解码，避免误用文件内极小 JPEG 预览（常见 160x120）。
            if (IsHeifLike(path))
            {
                full = WindowsShellImageDecoder.TryDecode(path, 1024);
                full ??= EmbeddedJpegPreviewLoader.TryDecodeLargestEmbeddedJpeg(path);
            }
            else
            {
                full = EmbeddedJpegPreviewLoader.TryDecodeLargestEmbeddedJpeg(path);
                full ??= WindowsShellImageDecoder.TryDecode(path, 1024);
            }
            if (full == null)
                return null;
        }

        try
        {
            var pw = full.PixelSize.Width;
            var ph = full.PixelSize.Height;
            if (pw <= 0 || ph <= 0)
                return null;

            var w = Math.Max(1, (int)Math.Round(pw * scale));
            var h = Math.Max(1, (int)Math.Round(ph * scale));
            // 先缩小再按 EXIF 转正：避免对全尺寸 RenderTargetBitmap 做 ResizeBitmap（易失败），并降低内存。
            var scaled = full.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.LowQuality);
            return ExifOrientationNormalizer.Apply(scaled, ExifOrientationNormalizer.TryReadOrientation(path));
        }
        finally
        {
            full.Dispose();
        }
    }

    /// <summary>在线程池解码并限制并发，避免启动时多张原图同时解压占满 CPU/内存。</summary>
    public static async Task<Bitmap?> LoadScaledAsync(string path, double scale = UiDisplayScale, CancellationToken ct = default)
    {
        await DecodeConcurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LoadScaled(path, scale), ct).ConfigureAwait(false);
        }
        finally
        {
            DecodeConcurrency.Release();
        }
    }

    /// <summary>全分辨率解码（主界面回看大图用；底栏缩略图仍用 <see cref="LoadScaledAsync"/>）。</summary>
    public static Bitmap? LoadFull(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        Bitmap? decoded = null;
        try
        {
            using var stream = File.OpenRead(path);
            decoded = new Bitmap(stream);
        }
        catch
        {
            // 全图优先系统解码，HEIF 场景不优先内嵌 JPEG 预览。
            if (IsHeifLike(path))
            {
                decoded = WindowsShellImageDecoder.TryDecode(path, 2048);
                decoded ??= EmbeddedJpegPreviewLoader.TryDecodeLargestEmbeddedJpeg(path);
            }
            else
            {
                decoded = EmbeddedJpegPreviewLoader.TryDecodeLargestEmbeddedJpeg(path);
                decoded ??= WindowsShellImageDecoder.TryDecode(path, 2048);
            }
            if (decoded == null)
                return null;
        }

        return ExifOrientationNormalizer.Apply(decoded, ExifOrientationNormalizer.TryReadOrientation(path));
    }

    public static async Task<Bitmap?> LoadFullAsync(string path, CancellationToken ct = default)
    {
        await DecodeConcurrency.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => LoadFull(path), ct).ConfigureAwait(false);
        }
        finally
        {
            DecodeConcurrency.Release();
        }
    }
}
