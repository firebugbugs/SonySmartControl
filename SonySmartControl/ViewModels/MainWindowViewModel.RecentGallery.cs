using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SonySmartControl.Helpers;
using SonySmartControl.Interop;
using SonySmartControl.Models;

namespace SonySmartControl.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>与 <see cref="Controls.FilmstripPhotoItem"/> 宽度及 <see cref="Controls.FilmstripPhotosHost"/> 内横向间距一致，用于估算可容纳条数。</summary>
    private const double FilmstripTileWidthPx = 126;

    private const double FilmstripTileSpacingPx = 10;
    private const int FilmstripMaxSlotsCap = 200;

    private CancellationTokenSource? _filmstripWidthDebounceCts;
    private int _filmstripMaxSlots = 10;

    /// <summary>快速切换胶片条目时递增，用于丢弃过期的全图解码结果。</summary>
    private int _recentPhotoLoadGeneration;

    /// <summary>底部条：最近保存的静态照片（不含实时预览项）。</summary>
    public ObservableCollection<RecentPhotoEntry> RecentPhotos { get; } = new();

    /// <summary>为 true 时主画面跟随 Live View；为 false 时显示选中的历史照片。</summary>
    [ObservableProperty]
    private bool _isViewingLiveMonitor = true;

    /// <summary>主画面正在回看的磁盘路径（实时预览时为 null，用于底部缩略图持久选中框）。</summary>
    [ObservableProperty]
    private string? _viewingRecentPhotoPath;

    /// <summary>查看历史照片时持有解码位图，切回实时时释放。</summary>
    private Bitmap? _staticReviewBitmap;

    partial void OnViewingRecentPhotoPathChanged(string? value)
    {
        foreach (var e in RecentPhotos)
            e.SyncFilmstripSelection(value);
    }

    partial void OnIsViewingLiveMonitorChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSdkAfFramesOverlayVisible));
        OnPropertyChanged(nameof(IsFocusReticleCloseButtonVisible));
        OnPropertyChanged(nameof(IsHistogramOverlayVisible));
        OnPropertyChanged(nameof(IsGuideOverlayVisible));
        OnPropertyChanged(nameof(IsLivePreviewVisible));
        OnPropertyChanged(nameof(IsStaticReviewVisible));
    }

    /// <summary>从当前保存目录按修改时间取最近若干张图，重建底部列表（启动、更换目录时调用）。</summary>
    private void RefreshRecentGalleryFromDisk()
    {
        try
        {
            ShowLiveMonitor();
            RepopulateRecentGalleryEntries();
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>清空并重新填充底部胶片（不切换主画面实时/回看状态）。</summary>
    private void RepopulateRecentGalleryEntries()
    {
        foreach (var e in RecentPhotos.ToList())
        {
            RecentPhotos.Remove(e);
            e.Dispose();
        }

        var dir = SaveDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        foreach (var path in EnumerateRecentImagePaths(dir, _filmstripMaxSlots))
        {
            var entry = new RecentPhotoEntry(path);
            RecentPhotos.Add(entry);
            entry.LoadThumbnailAsync(OnThumbLoaded);
        }
    }

    private void SyncFilmstripThumbnailsSelection()
    {
        foreach (var e in RecentPhotos)
            e.SyncFilmstripSelection(ViewingRecentPhotoPath);
    }

    /// <summary>底部可视槽位数量变化时：缩小时只移除尾部；放大时若与当前列表前缀一致则只追加新槽位对应文件并解码，否则再全量重载。</summary>
    private void ApplyFilmstripSlotCapacityChange(int newSlots)
    {
        try
        {
            _filmstripMaxSlots = newSlots;

            var dir = SaveDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                TrimRecentPhotosToSlotLimit();
                return;
            }

            List<string> desired;
            try
            {
                desired = EnumerateRecentImagePaths(dir, newSlots).ToList();
            }
            catch
            {
                RepopulateRecentGalleryEntries();
                SyncFilmstripThumbnailsSelection();
                return;
            }

            if (RecentPhotos.Count > newSlots)
            {
                while (RecentPhotos.Count > newSlots)
                {
                    var last = RecentPhotos[^1];
                    RecentPhotos.RemoveAt(RecentPhotos.Count - 1);
                    last.Dispose();
                }

                SyncFilmstripThumbnailsSelection();
                return;
            }

            if (RecentPhotos.Count < newSlots)
            {
                var n = RecentPhotos.Count;
                if (n == 0)
                {
                    foreach (var path in desired)
                    {
                        var entry = new RecentPhotoEntry(path);
                        RecentPhotos.Add(entry);
                        entry.LoadThumbnailAsync(OnThumbLoaded);
                    }

                    SyncFilmstripThumbnailsSelection();
                    return;
                }

                var compareLen = Math.Min(n, desired.Count);
                var prefixOk = true;
                for (var i = 0; i < compareLen; i++)
                {
                    if (!string.Equals(RecentPhotos[i].FilePath, desired[i], StringComparison.OrdinalIgnoreCase))
                    {
                        prefixOk = false;
                        break;
                    }
                }

                if (prefixOk)
                {
                    if (desired.Count > n)
                    {
                        for (var i = n; i < desired.Count; i++)
                        {
                            var entry = new RecentPhotoEntry(desired[i]);
                            RecentPhotos.Add(entry);
                            entry.LoadThumbnailAsync(OnThumbLoaded);
                        }
                    }

                    SyncFilmstripThumbnailsSelection();
                    return;
                }
            }

            RepopulateRecentGalleryEntries();
            SyncFilmstripThumbnailsSelection();
        }
        catch
        {
            // 忽略
        }
    }

    private void TrimRecentPhotosToSlotLimit()
    {
        while (RecentPhotos.Count > _filmstripMaxSlots)
        {
            var last = RecentPhotos[^1];
            RecentPhotos.RemoveAt(RecentPhotos.Count - 1);
            last.Dispose();
        }

        SyncFilmstripThumbnailsSelection();
    }

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".srf", ".sr2", ".dng", ".nef", ".nrw", ".cr2", ".cr3", ".orf", ".rw2", ".pef", ".raf",
        ".3fr", ".mrw", ".raw",
    };

    private static bool IsGalleryImageExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp")
            return true;
        return RawExtensions.Contains(ext);
    }

    /// <summary>同一次快门若同时产生 RAW+JPEG，优先展示 JPEG/PNG（与官方「双格式」习惯一致）。</summary>
    private static int GalleryExtensionPriority(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            return 0;
        if (ext == ".png")
            return 1;
        if (ext is ".webp")
            return 2;
        if (ext is ".hif" or ".heif" or ".heic")
            return 3;
        return 10;
    }

    /// <summary>按「同底名只保留一张」规则选出要在胶片条展示的文件，再按时间取最近 <paramref name="maxCount"/> 条。</summary>
    private static IEnumerable<string> EnumerateRecentImagePaths(string dir, int maxCount)
    {
        var files = Directory.EnumerateFiles(dir)
            .Select(p => new FileInfo(p))
            .Where(f => IsGalleryImageExtension(Path.GetExtension(f.Name)))
            .ToList();

        var chosen = new List<FileInfo>();
        foreach (var g in files.GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase))
        {
            var pick = g
                .OrderBy(f => GalleryExtensionPriority(Path.GetExtension(f.Name)))
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .First();
            chosen.Add(pick);
        }

        return chosen
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(maxCount)
            .Select(f => f.FullName);
    }

    [RelayCommand]
    private void ShowLiveMonitor()
    {
        _staticReviewBitmap?.Dispose();
        _staticReviewBitmap = null;
        StaticReviewImage = null;
        ViewingRecentPhotoPath = null;
        IsViewingLiveMonitor = true;
        PreviewImage = _lastFrameOwner;
        if (_lastFrameOwner != null)
            LuminanceHistogramBins = HistogramLuminance.ComputeNormalized(_lastFrameOwner) ?? new double[256];
        else
            LuminanceHistogramBins = null;

        if (IsSessionActive && _session != null)
        {
            var fj = _session.TryGetLiveViewFocusFramesJson();
            if (CrSdkLiveViewFocusFrameParser.TryParse(fj, out var list))
                SdkAfFocusFrames = list;
            else
                SdkAfFocusFrames = null;
        }
        else
            SdkAfFocusFrames = null;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ShowRecentPhoto(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var loadGeneration = Interlocked.Increment(ref _recentPhotoLoadGeneration);

        var entry = RecentPhotos.FirstOrDefault(e =>
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));

        try
        {
            // 先切到回看模式、选中胶片与缩略图，避免等全图解码才响应
            IsViewingLiveMonitor = false;
            SdkAfFocusFrames = null;
            ViewingRecentPhotoPath = path;

            _staticReviewBitmap?.Dispose();
            _staticReviewBitmap = null;

            StaticReviewImage = entry?.Thumbnail;

            var targetPath = path;
            var bmp = await DiskImagePreviewLoader.LoadFullAsync(targetPath).ConfigureAwait(true);

            if (loadGeneration != Volatile.Read(ref _recentPhotoLoadGeneration))
            {
                bmp?.Dispose();
                return;
            }

            if (!string.Equals(ViewingRecentPhotoPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                bmp?.Dispose();
                return;
            }

            if (bmp == null)
            {
                if (StaticReviewImage == null)
                    StatusMessage = "无法打开或解码照片。";
                return;
            }

            _staticReviewBitmap = bmp;
            StaticReviewImage = bmp;
        }
        catch (OperationCanceledException)
        {
            // 忽略
        }
        catch (Exception ex)
        {
            StatusMessage = "无法打开照片: " + ex.Message;
        }
    }

    /// <summary>在已选中一张胶片照片时，按列表顺序左右切换；实时取景时不生效。</summary>
    public Task NavigateRecentGalleryAsync(int delta)
    {
        if (delta == 0 || IsViewingLiveMonitor || string.IsNullOrEmpty(ViewingRecentPhotoPath))
            return Task.CompletedTask;

        var idx = -1;
        for (var i = 0; i < RecentPhotos.Count; i++)
        {
            if (string.Equals(RecentPhotos[i].FilePath, ViewingRecentPhotoPath, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
            return Task.CompletedTask;

        var next = idx + delta;
        if (next < 0 || next >= RecentPhotos.Count)
            return Task.CompletedTask;

        return ShowRecentPhoto(RecentPhotos[next].FilePath);
    }

    /// <summary>拍照成功后扫描保存目录，将最新 JPEG/PNG 插入最近列表（条数受底部胶片可视宽度限制）。</summary>
    private async Task RegisterRecentCaptureAsync()
    {
        try
        {
            await Task.Delay(450).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(RegisterRecentCaptureCore);
        }
        catch
        {
            // 忽略
        }
    }

    private void RegisterRecentCaptureCore()
    {
        try
        {
            var dir = SaveDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            var path = EnumerateRecentImagePaths(dir, 1).FirstOrDefault();
            if (path == null)
                return;
            if (RecentPhotos.Any(x => string.Equals(x.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                return;

            var entry = new RecentPhotoEntry(path);
            RecentPhotos.Insert(0, entry);
            while (RecentPhotos.Count > _filmstripMaxSlots)
            {
                var last = RecentPhotos[^1];
                RecentPhotos.RemoveAt(RecentPhotos.Count - 1);
                last.Dispose();
            }

            entry.LoadThumbnailAsync(OnThumbLoaded);
            entry.SyncFilmstripSelection(ViewingRecentPhotoPath);
        }
        catch
        {
            // 忽略
        }
    }

    private void OnThumbLoaded(RecentPhotoEntry entry, IImage? image)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!RecentPhotos.Contains(entry))
            {
                if (image is Bitmap bb)
                    bb.Dispose();
                return;
            }

            entry.Thumbnail = image;
            entry.SyncFilmstripSelection(ViewingRecentPhotoPath);
        });
    }

    /// <summary>由底部胶片区域 <see cref="SizeChanged"/> 调用；按可视宽度估算条数并防抖后刷新列表。</summary>
    public void NotifyFilmstripHostWidthChanged(double hostWidthPixels)
    {
        _filmstripWidthDebounceCts?.Cancel();
        _filmstripWidthDebounceCts?.Dispose();
        _filmstripWidthDebounceCts = new CancellationTokenSource();
        var token = _filmstripWidthDebounceCts.Token;
        _ = ApplyFilmstripHostWidthDebouncedAsync(hostWidthPixels, token);
    }

    private async Task ApplyFilmstripHostWidthDebouncedAsync(double hostWidthPixels, CancellationToken ct)
    {
        try
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var slots = ComputeFilmstripSlots(hostWidthPixels);
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (slots == _filmstripMaxSlots)
                        return;
                    ApplyFilmstripSlotCapacityChange(slots);
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static int ComputeFilmstripSlots(double hostWidthPixels)
    {
        if (hostWidthPixels <= 1)
            return 10;
        var unit = FilmstripTileWidthPx + FilmstripTileSpacingPx;
        var n = (int)Math.Floor((hostWidthPixels + FilmstripTileSpacingPx) / unit);
        return Math.Clamp(Math.Max(1, n), 1, FilmstripMaxSlotsCap);
    }

    private void CancelFilmstripHostWidthUpdates()
    {
        try
        {
            _filmstripWidthDebounceCts?.Cancel();
        }
        catch
        {
        }

        _filmstripWidthDebounceCts?.Dispose();
        _filmstripWidthDebounceCts = null;
    }
}
