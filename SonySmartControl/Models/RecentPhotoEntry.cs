using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SonySmartControl.Helpers;

namespace SonySmartControl.Models;

/// <summary>底部「最近照片」条目中的一项（缩略图在后台解码，界面仅缩小显示）。</summary>
public sealed partial class RecentPhotoEntry : ObservableObject, IDisposable
{
    public string FilePath { get; }

    public string FileLabel => Path.GetFileName(FilePath);

    [ObservableProperty]
    private IImage? _thumbnail;

    [ObservableProperty]
    private string _metaLinePixel = "—";

    [ObservableProperty]
    private string _metaLineFile = "";

    [ObservableProperty]
    private string _metaLineTime = "";

    [ObservableProperty]
    private double[]? _tooltipHistogramBins;

    [ObservableProperty]
    private string _metaExifCamera = "—";

    [ObservableProperty]
    private string _metaExifLens = "—";

    [ObservableProperty]
    private string _metaExifIso = "—";

    [ObservableProperty]
    private string _metaExifAperture = "—";

    [ObservableProperty]
    private string _metaExifShutter = "—";

    [ObservableProperty]
    private string _metaExifEv = "—";

    [ObservableProperty]
    private string _metaExifFocal = "—";

    [ObservableProperty]
    private string _metaExifCaptureTime = "—";

    private int _disposedFlag;

    private int _tooltipSourceMetaScheduled;

    /// <summary>当前主画面正在回看本条目对应文件时为 true（与 <c>ViewingRecentPhotoPath</c> 同步）。</summary>
    [ObservableProperty]
    private bool _isFilmstripSelected;

    public RecentPhotoEntry(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        RefreshFileMetadata();
    }

    /// <summary>首次悬停弹出信息前调用：后台按<strong>源文件</strong>全分辨率解码并计算直方图（仅触发一次）。</summary>
    public void RequestTooltipSourceMetadataIfNeeded()
    {
        if (Interlocked.CompareExchange(ref _tooltipSourceMetaScheduled, 1, 0) != 0)
            return;
        MetaLinePixel = "正在解码源图…";
        _ = LoadTooltipSourceMetadataAsync();
    }

    /// <summary>由主窗口 VM 在切换回看路径时调用，刷新底部选中描边。</summary>
    public void SyncFilmstripSelection(string? viewingRecentPhotoPath)
    {
        var on = !string.IsNullOrEmpty(viewingRecentPhotoPath)
                 && string.Equals(viewingRecentPhotoPath, FilePath, StringComparison.OrdinalIgnoreCase);
        IsFilmstripSelected = on;
    }

    /// <summary>在后台读盘、按 <see cref="DiskImagePreviewLoader.UiDisplayScale"/> 缩小后解码，再通过回调交 UI 赋值。</summary>
    public void LoadThumbnailAsync(Action<RecentPhotoEntry, IImage?> onUiThread)
    {
        var path = FilePath;
        _ = LoadThumbCore();

        async Task LoadThumbCore()
        {
            Bitmap? img = null;
            try
            {
                img = await DiskImagePreviewLoader.LoadScaledAsync(path).ConfigureAwait(false);
            }
            catch
            {
                img?.Dispose();
                img = null;
            }

            onUiThread(this, img);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
            return;
        if (Thumbnail is Bitmap b)
            b.Dispose();
        Thumbnail = null;
    }

    private void RefreshFileMetadata()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            MetaLineFile = $"大小 {FormatSize(fi.Length)} · {NormalizeExt(fi.Extension)}";
            MetaLineTime = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            MetaLineFile = "";
            MetaLineTime = "";
        }
    }

    private static string NormalizeExt(string ext)
    {
        ext = ext.Trim();
        if (string.IsNullOrEmpty(ext))
            return "—";
        return ext.TrimStart('.').ToUpperInvariant();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double v = bytes;
        var u = new[] { "KB", "MB", "GB", "TB" };
        var i = -1;
        do
        {
            v /= 1024;
            i++;
        } while (v >= 1024 && i < u.Length - 1);

        return $"{v:0.##} {u[i]}";
    }

    /// <summary>悬停信息：EXIF + 按<strong>源文件</strong>全分辨率解码后得到尺寸与亮度直方图（与底栏缩略图解码无关）。</summary>
    private async Task LoadTooltipSourceMetadataAsync()
    {
        var path = FilePath;

        var exif = await Task.Run(() => PhotoExifReader.TryRead(path)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _disposedFlag) != 0)
                return;
            ApplyExifSnapshot(exif);
        });

        Bitmap? bmp = null;
        try
        {
            bmp = await DiskImagePreviewLoader.LoadFullAsync(path).ConfigureAwait(false);
            if (Volatile.Read(ref _disposedFlag) != 0)
                return;

            if (bmp == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposedFlag) != 0)
                        return;
                    MetaLinePixel = "无法解码源图";
                    TooltipHistogramBins = null;
                });
                return;
            }

            var ps = bmp.PixelSize;
            var line = $"{ps.Width} × {ps.Height} px · 源图像全分辨率 · BGRA/sRGB";
            var bins = HistogramLuminance.ComputeNormalized(bmp, 64);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposedFlag) != 0)
                    return;
                MetaLinePixel = line;
                TooltipHistogramBins = bins;
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposedFlag) != 0)
                    return;
                MetaLinePixel = "无法读取源图（格式不支持或文件损坏）";
                TooltipHistogramBins = null;
            });
        }
        finally
        {
            bmp?.Dispose();
        }
    }

    private void ApplyExifSnapshot(PhotoExifReader.Snapshot s)
    {
        MetaExifCamera = s.CameraModel;
        MetaExifLens = s.LensModel;
        MetaExifIso = s.Iso;
        MetaExifAperture = s.Aperture;
        MetaExifShutter = s.ExposureTime;
        MetaExifEv = s.ExposureCompensation;
        MetaExifFocal = s.FocalLength;
        MetaExifCaptureTime = s.CaptureTime;
    }
}
