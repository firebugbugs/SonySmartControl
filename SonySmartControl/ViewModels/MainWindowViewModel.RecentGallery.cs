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
    private CancellationTokenSource? _recentCaptureSyncCts;
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
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".hif" or ".heif" or ".heic")
            return true;
        return RawExtensions.Contains(ext);
    }

    /// <summary>
    /// 同一次快门同名多格式的预览优先级：
    /// JPEG > RAW > HEIF > 其他位图。确保 JPEG+RAW 显示 JPEG，HEIF+RAW 显示 RAW；
    /// 无 RAW 时仍可显示 HEIF。
    /// </summary>
    private static int GalleryExtensionPriority(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            return 0;
        if (RawExtensions.Contains(ext))
            return 1;
        if (ext is ".hif" or ".heif" or ".heic")
            return 2;
        if (ext == ".png")
            return 3;
        if (ext is ".webp")
            return 4;
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
        _recentCaptureSyncCts?.Cancel();
        _recentCaptureSyncCts?.Dispose();
        var cts = new CancellationTokenSource();
        _recentCaptureSyncCts = cts;

        try
        {
            await Task.Delay(220, cts.Token).ConfigureAwait(false);

            var stableRounds = 0;
            for (var i = 0; i < 10; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var changed = await Dispatcher.UIThread
                    .InvokeAsync(() => SyncRecentGalleryWithDisk() > 0);

                if (changed)
                    stableRounds = 0;
                else
                    stableRounds++;

                if (stableRounds >= 2)
                    break;

                await Task.Delay(220 + i * 40, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 忽略
        }
        catch
        {
            // 忽略
        }
        finally
        {
            if (ReferenceEquals(_recentCaptureSyncCts, cts))
            {
                _recentCaptureSyncCts = null;
                cts.Dispose();
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    private void RegisterRecentCaptureCore()
    {
        _ = SyncRecentGalleryWithDisk();
    }

    /// <summary>将底部胶片与磁盘最近文件对齐：补新图、补老图、移除已不存在项，并保持目标顺序。</summary>
    private int SyncRecentGalleryWithDisk()
    {
        try
        {
            var dir = SaveDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return 0;

            var desired = EnumerateRecentImagePaths(dir, _filmstripMaxSlots).ToList();
            if (desired.Count == 0)
                return 0;

            var changed = 0;
            var desiredSet = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);

            for (var i = RecentPhotos.Count - 1; i >= 0; i--)
            {
                var current = RecentPhotos[i];
                if (desiredSet.Contains(current.FilePath))
                    continue;
                RecentPhotos.RemoveAt(i);
                current.Dispose();
                changed++;
            }

            for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
            {
                var path = desired[targetIndex];

                var existingIndex = -1;
                for (var i = 0; i < RecentPhotos.Count; i++)
                {
                    if (string.Equals(RecentPhotos[i].FilePath, path, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = i;
                        break;
                    }
                }

                if (existingIndex >= 0)
                {
                    if (existingIndex == targetIndex)
                        continue;

                    var existing = RecentPhotos[existingIndex];
                    RecentPhotos.RemoveAt(existingIndex);
                    RecentPhotos.Insert(targetIndex, existing);
                    changed++;
                    continue;
                }

                var entry = new RecentPhotoEntry(path);
                RecentPhotos.Insert(Math.Min(targetIndex, RecentPhotos.Count), entry);
                entry.LoadThumbnailAsync(OnThumbLoaded);
                changed++;
            }

            while (RecentPhotos.Count > _filmstripMaxSlots)
            {
                var last = RecentPhotos[^1];
                RecentPhotos.RemoveAt(RecentPhotos.Count - 1);
                last.Dispose();
                changed++;
            }

            SyncFilmstripThumbnailsSelection();
            return changed;
        }
        catch
        {
            return 0;
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

    /// <summary>与当前回看文件同目录、同主文件名（不含扩展名）的相册格式文件，用于 RAW+JPEG 一并删除。</summary>
    private static IReadOnlyList<string> ListLocalGalleryFilesSameStem(string primaryPath)
    {
        var dir = Path.GetDirectoryName(primaryPath);
        var stem = Path.GetFileNameWithoutExtension(primaryPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsGalleryImageExtension(Path.GetExtension(f)))
                list.Add(f);
        }

        return list;
    }

    [RelayCommand]
    private void CopyPreviewImageToClipboard(string? path)
    {
        var targetPath = !string.IsNullOrWhiteSpace(path) ? path : ViewingRecentPhotoPath;
        if (string.IsNullOrWhiteSpace(targetPath) || !File.Exists(targetPath))
        {
            StatusMessage = "未找到可复制的原图文件。";
            return;
        }

        try
        {
            using var bmp = new System.Drawing.Bitmap(targetPath);
            System.Windows.Forms.Clipboard.SetImage(bmp);
            StatusMessage = "已复制图片到剪贴板，可在微信等应用中粘贴。";
        }
        catch (Exception ex)
        {
            try
            {
                if (_staticReviewBitmap == null)
                {
                    StatusMessage = "复制失败: " + ex.Message;
                    return;
                }

                using var ms = new MemoryStream();
                _staticReviewBitmap.Save(ms);
                ms.Position = 0;
                using var bmp2 = new System.Drawing.Bitmap(ms);
                System.Windows.Forms.Clipboard.SetImage(bmp2);
                StatusMessage = "已复制当前显示画面到剪贴板。";
            }
            catch (Exception ex2)
            {
                StatusMessage = "复制失败: " + ex2.Message;
            }
        }
    }

    [RelayCommand]
    private async Task DeletePreviewImageAsync(string? path)
    {
        var selectedPrimaries = RecentPhotos
            .Where(e => e.IsBatchSelected)
            .Select(e => e.FilePath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedPrimaries.Count == 0)
        {
            var single = !string.IsNullOrWhiteSpace(path) ? path : ViewingRecentPhotoPath;
            if (!string.IsNullOrWhiteSpace(single))
                selectedPrimaries.Add(single);
        }

        if (selectedPrimaries.Count == 0)
        {
            StatusMessage = "未找到可删除的图片文件。";
            return;
        }

        var localPaths = selectedPrimaries
            .SelectMany(ListLocalGalleryFilesSameStem)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (localPaths.Count == 0)
            return;

        foreach (var p in localPaths)
        {
            try
            {
                File.Delete(p);
            }
            catch
            {
                // 单文件失败不阻断其余
            }
        }

        foreach (var p in localPaths)
        {
            var entry = RecentPhotos.FirstOrDefault(e =>
                string.Equals(e.FilePath, p, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                continue;
            RecentPhotos.Remove(entry);
            entry.Dispose();
        }

        _ = SyncRecentGalleryWithDisk();

        var deletedPathSet = new HashSet<string>(localPaths, StringComparer.OrdinalIgnoreCase);
        var wasViewing = !string.IsNullOrWhiteSpace(ViewingRecentPhotoPath) && deletedPathSet.Contains(ViewingRecentPhotoPath);
        if (wasViewing)
        {
            if (RecentPhotos.Count > 0)
                await ShowRecentPhoto(RecentPhotos[0].FilePath).ConfigureAwait(true);
            else
                ShowLiveMonitor();
        }

        foreach (var e in RecentPhotos)
            e.IsBatchSelected = false;

        SyncFilmstripThumbnailsSelection();
        StatusMessage = selectedPrimaries.Count > 1
            ? $"本地文件已批量删除（{selectedPrimaries.Count} 项）。"
            : "本地文件已删除。";
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
