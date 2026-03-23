using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SonySmartControl.Helpers;
using SonySmartControl.Interop;
using SonySmartControl.Services.Camera;
using SonySmartControl.Settings;

namespace SonySmartControl.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly bool _persistEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistogramOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsGuideOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsSdkAfFramesOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsFocusReticleCloseButtonVisible))]
    private IImage? _previewImage;

    /// <summary>回看磁盘照片时的原图（仅静态回看区绑定；实时流仍用 <see cref="PreviewImage"/>）。</summary>
    [ObservableProperty]
    private IImage? _staticReviewImage;

    /// <summary>已发起遥控触摸对焦点（关闭按钮在机身对焦框上，见 <see cref="SdkAfFocusFrames"/>）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFocusReticleCloseButtonVisible))]
    private bool _showFocusReticle;

    /// <summary>预览区可用尺寸（与对焦框关闭按钮定位一致，由视图 SizeChanged 同步）。</summary>
    [ObservableProperty] private Size _previewLayoutSize;

    /// <summary>关闭按钮 Margin（Left/Top），锚定在机身对焦框右上角。</summary>
    [ObservableProperty] private Thickness _focusReticleCloseMargin;

    /// <summary>机身 Live View 返回的对焦框（半按/自动对焦时绿框，与 CrFocusFrameInfo 一致）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSdkAfFramesOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(IsFocusReticleCloseButtonVisible))]
    private IReadOnlyList<CrSdkLiveViewFocusFrameView>? _sdkAfFocusFrames;

    /// <summary>CrSDK 预览且存在至少一个机身对焦框时显示。</summary>
    public bool IsSdkAfFramesOverlayVisible =>
        PreviewImage != null
        && IsSessionActive
        && SdkAfFocusFrames is { Count: > 0 }
        && IsViewingLiveMonitor;

    /// <summary>已标记触摸对焦且机身已返回对焦框时显示关闭按钮。</summary>
    public bool IsFocusReticleCloseButtonVisible =>
        ShowFocusReticle
        && PreviewImage != null
        && SdkAfFocusFrames is { Count: > 0 }
        && IsViewingLiveMonitor;

    /// <summary>预览区直方图数据（256 档归一化亮度）。</summary>
    [ObservableProperty] private double[]? _luminanceHistogramBins;

    /// <summary>是否显示亮度直方图（默认开启，可持久化）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistogramOverlayVisible))]
    private bool _showHistogram = true;

    /// <summary>构图辅助线：0=无，1=三分法，2=十字对准线，3=对角线，4=安全区。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuideOverlayVisible))]
    private int _guideOverlayIndex;

    /// <summary>有预览且开启直方图时显示直方图叠加层。</summary>
    public bool IsHistogramOverlayVisible =>
        IsViewingLiveMonitor && PreviewImage != null && ShowHistogram;

    /// <summary>有预览且选择了辅助线模式时显示辅助线。</summary>
    public bool IsGuideOverlayVisible =>
        IsViewingLiveMonitor && PreviewImage != null && GuideOverlayIndex > 0;

    /// <summary>实时取景层（含触摸对焦等）。</summary>
    public bool IsLivePreviewVisible => IsViewingLiveMonitor;

    /// <summary>静态原图回看区（可平移缩放，无辅助线）。</summary>
    public bool IsStaticReviewVisible => !IsViewingLiveMonitor;

    [ObservableProperty] private string _statusMessage = "就绪：点击「连接」通过 CrSDK 接入相机。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDisconnectEnabled))]
    [NotifyPropertyChangedFor(nameof(ShootingPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsSdkAfFramesOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeText))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeForeground))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private bool _isSessionActive;

    /// <summary>连接 CrSDK 进行中（阻塞在后台线程，用于禁用「连接」与状态提示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectEnabled))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeText))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeForeground))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private string _saveDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private int _captureFormatIndex;

    [ObservableProperty] private string _fileNamePrefix;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private string _connectedCameraModelName = "未知";

    public bool IsConnectEnabled => !IsSessionActive && !IsConnecting;

    public bool IsDisconnectEnabled => IsSessionActive;

    public bool ShootingPanelVisible => IsSessionActive;
    public string CameraConnectionBadgeText =>
        IsConnecting ? "连接中" : IsSessionActive ? "已连接" : "未连接";
    public string CameraConnectionBadgeBackground =>
        IsConnecting ? "#FFF6DE" : IsSessionActive ? "#DDF6E9" : "#ECEFF4";
    public string CameraConnectionBadgeForeground =>
        IsConnecting ? "#9A6A00" : IsSessionActive ? "#0E7A43" : "#6B7280";
    private string CaptureFormatLabel =>
        CaptureFormatIndex switch
        {
            1 => "RAW",
            2 => "RAW + JPEG",
            _ => "JPEG",
        };
    public string CameraConnectionTooltip =>
        $"状态：{CameraConnectionBadgeText}\n型号：{(string.IsNullOrWhiteSpace(ConnectedCameraModelName) ? "未知" : ConnectedCameraModelName)}\n保存目录：{SaveDirectory}\n保存格式：{CaptureFormatLabel}";

    /// <summary>构图辅助线下拉项（顺序与 <see cref="GuideOverlayIndex"/> 一致）。</summary>
    public ObservableCollection<string> GuideOverlayChoices { get; } = new(
        new[] { "无", "三分法", "十字对准线", "对角线", "安全区" });

    [ObservableProperty] private bool _saveSectionExpanded = true;

    [ObservableProperty] private bool _generalSectionExpanded = true;

    [ObservableProperty] private bool _exposureSectionExpanded = true;

    [ObservableProperty] private bool _auxDisplaySectionExpanded = true;

    /// <summary>右侧遥控区：0=拍照，1=摄影。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhotoSidebarMode))]
    [NotifyPropertyChangedFor(nameof(IsVideoSidebarMode))]
    private int _remoteSidebarMode;

    public bool IsPhotoSidebarMode => RemoteSidebarMode == 0;

    public bool IsVideoSidebarMode => RemoteSidebarMode == 1;

    private ICameraPreviewSession? _session;
    private Bitmap? _lastFrameOwner;

    public MainWindowViewModel()
    {
        var s = UserCameraSettingsStore.Load();
        _persistEnabled = false;
        _saveDirectory = s.SaveDirectory;
        _captureFormatIndex = s.CaptureFormatIndex;
        _fileNamePrefix = s.FileNamePrefix;
        _showHistogram = s.ShowHistogram ?? true;
        _guideOverlayIndex = s.GuideOverlayIndex;
        _persistEnabled = true;
        RefreshRecentGalleryFromDisk();
    }

    /// <summary>由主窗口在 Loaded 中注入，用于选择保存文件夹。</summary>
    public Func<Task<string?>>? PickSaveFolderAsync { get; set; }

    [RelayCommand]
    private void ExpandAllSections()
    {
        SaveSectionExpanded = true;
        GeneralSectionExpanded = true;
        ExposureSectionExpanded = true;
        AuxDisplaySectionExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllSections()
    {
        SaveSectionExpanded = false;
        GeneralSectionExpanded = false;
        ExposureSectionExpanded = false;
        AuxDisplaySectionExpanded = false;
    }

    [RelayCommand]
    private void SelectPhotoSidebarMode() => RemoteSidebarMode = 0;

    [RelayCommand]
    private void SelectVideoSidebarMode() => RemoteSidebarMode = 1;

    [RelayCommand]
    private async Task BrowseSaveFolder()
    {
        if (PickSaveFolderAsync == null)
        {
            StatusMessage = "无法打开文件夹选择器（视图未就绪）。";
            return;
        }

        try
        {
            var path = await PickSaveFolderAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(path))
                SaveDirectory = path;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenSaveFolder()
    {
        try
        {
            var raw = SaveDirectory?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                StatusMessage = "请先填写或选择保存目录。";
                return;
            }

            var full = Path.GetFullPath(raw);
            if (!Directory.Exists(full))
            {
                StatusMessage = "该文件夹尚不存在，请先创建或选择有效路径。";
                return;
            }

            Process.Start(
                new ProcessStartInfo { FileName = full, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "无法打开文件夹: " + ex.Message;
        }
    }

    /// <summary>保存目录/前缀输入时防抖后再同步到已连接相机，避免每键一次 SDK 调用。</summary>
    private int _saveSettingsCameraSyncVersion;

    private void ScheduleSyncSaveSettingsToCameraDebounced()
    {
        if (!_persistEnabled)
            return;
        var v = ++_saveSettingsCameraSyncVersion;
        _ = SyncSaveSettingsToCameraAfterDelayAsync(v);
    }

    private async Task SyncSaveSettingsToCameraAfterDelayAsync(int version)
    {
        try
        {
            await Task.Delay(400).ConfigureAwait(true);
            if (version != _saveSettingsCameraSyncVersion)
                return;
            if (!IsSessionActive || _session == null)
                return;
            await _session
                .ApplyCameraSaveSettingsAsync(SaveDirectory, FileNamePrefix, IndexToFileType(CaptureFormatIndex))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "同步保存目录与格式到相机失败: " + ex.Message;
        }
    }

    private readonly object _shutterHoldLock = new();
    private bool _focusS1Held;
    private bool _captureShutterHeld;

    /// <summary>
    /// 拍照键当前这次半按对应的 <see cref="ICameraPreviewSession.BeginHalfPressFocusAsync"/> 任务（在 Task.Run 中执行 S1 Locked）。
    /// 松手时必须先 await 完成，否则会在机身尚未半按时就发 Release（与官方 af_shutter 顺序不符）。
    /// </summary>
    private Task? _captureHalfPressInFlight;

    /// <summary>预览上选中的对焦点（归一化 0..1）；半按时先在该点遥控触摸（若尚未发送）。</summary>
    private bool _hasPendingFocusPoint;

    private double _pendingFocusNx;
    private double _pendingFocusNy;

    /// <summary>点击预览后是否已向机身发送过 RemoteTouch（避免半按时重复发送）。</summary>
    private bool _touchAfAlreadySentForPendingPoint;

    /// <summary>预览按住对焦手势世代：松开时递增，使尚未完成的「选点→半按」在松手后不再启动半按。</summary>
    private int _previewFocusEpoch;

    /// <summary>预览指针是否仍视为按下（避免 Released 与 CaptureLost 各触发一次时世代连加两次）。</summary>
    private bool _previewFocusPointerDown;

    /// <summary>连拍：已向机身发送 Release Down 且尚未 Up（与半按并存时需先 HoldEnd 再解锁）。</summary>
    private bool _captureBurstShutterDownActive;

    /// <summary>
    /// 串行化所有会触发 S1 半按的操作（对焦键、预览按住、拍照键），避免与快门释放竞态导致 ErrControlFailed(-8)。
    /// </summary>
    private readonly SemaphoreSlim _shutterPipelineLock = new(1, 1);

    /// <summary>
    /// 从「拍照键按下」到释放快门、冷却结束整段完成前为 true；期间拒绝新的对焦/预览半按指令。
    /// </summary>
    private volatile bool _captureTaskActive;

    /// <summary>已连接且当前无进行中的拍照任务时，侧栏「对焦」「拍照」可用（半按对焦期间须保持为 true，否则 IsEnabled=false 会失捕并误判取消半按）。</summary>
    public bool ShutterActionButtonsEnabled => IsSessionActive && !_captureTaskActive;

    private void SetCaptureTaskActive(bool value)
    {
        if (_captureTaskActive == value)
            return;
        _captureTaskActive = value;
        OnPropertyChanged(nameof(ShutterActionButtonsEnabled));
    }

    /// <summary>对焦按钮指针/触摸按下：若已标记对焦点则先遥控触摸该点，再 S1 Locked。</summary>
    public async Task FocusPointerPressedAsync()
    {
        if (!CanCrSdkCameraOps() || _session == null)
            return;

        if (_captureTaskActive)
            return;

        if (!await _shutterPipelineLock.WaitAsync(0, CancellationToken.None).ConfigureAwait(true))
            return;
        var releaseWaitOnFailure = true;
        try
        {
            lock (_shutterHoldLock)
                _focusS1Held = true;

            try
            {
                if (!await TryApplyPendingFocusTouchBeforeHalfPressAsync().ConfigureAwait(true))
                {
                    lock (_shutterHoldLock)
                        _focusS1Held = false;
                    return;
                }

                await _session.BeginHalfPressFocusAsync().ConfigureAwait(true);
                releaseWaitOnFailure = false;
            }
            catch (Exception ex)
            {
                lock (_shutterHoldLock)
                    _focusS1Held = false;
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            if (releaseWaitOnFailure)
                _shutterPipelineLock.Release();
        }
    }

    /// <summary>对焦按钮松开或失捕：S1 Unlocked。</summary>
    public async Task FocusPointerReleasedAsync()
    {
        bool shouldRelease;
        lock (_shutterHoldLock)
        {
            shouldRelease = _focusS1Held;
            _focusS1Held = false;
        }

        if (!shouldRelease || _session == null)
            return;

        try
        {
            await _session.EndHalfPressFocusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            try
            {
                _shutterPipelineLock.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>拍照按钮按下：单张为半按 S1；连拍驱动模式下在半按完成后发送快门全按并保持（Release Down）。</summary>
    public async Task CapturePointerPressedAsync()
    {
        if (!CanCrSdkCameraOps() || _session == null)
            return;

        if (_captureTaskActive)
        {
            StatusMessage = "拍照任务进行中，请稍候。";
            return;
        }

        _captureBurstShutterDownActive = false;

        // 勿在半按阶段 SetCaptureTaskActive(true)：该标志绑定按钮 IsEnabled，禁用会触发 PointerCaptureLost，
        // 先于 PointerReleased 执行 CapturePointerCancelledAsync，导致松手时 _captureShutterHeld 已被清空、无法拍照。
        await _shutterPipelineLock.WaitAsync().ConfigureAwait(true);
        var releaseWaitOnFailure = true;
        try
        {
            lock (_shutterHoldLock)
                _captureShutterHeld = true;

            try
            {
                if (!await TryApplyPendingFocusTouchBeforeHalfPressAsync().ConfigureAwait(true))
                {
                    lock (_shutterHoldLock)
                        _captureShutterHeld = false;
                    return;
                }

                var halfPressTask = _session.BeginHalfPressFocusAsync();
                _captureHalfPressInFlight = halfPressTask;
                try
                {
                    await halfPressTask.ConfigureAwait(true);
                    releaseWaitOnFailure = false;

                    if (IsBurstDriveMode())
                    {
                        try
                        {
                            await _session
                                .CaptureBurstHoldDownAsync(
                                    SaveDirectory,
                                    FileNamePrefix,
                                    IndexToFileType(CaptureFormatIndex))
                                .ConfigureAwait(true);
                            _captureBurstShutterDownActive = true;
                        }
                        catch (Exception exBurst)
                        {
                            lock (_shutterHoldLock)
                                _captureShutterHeld = false;
                            try
                            {
                                await _session.EndHalfPressFocusAsync().ConfigureAwait(true);
                            }
                            catch
                            {
                            }

                            StatusMessage = exBurst.Message;
                        }
                    }
                }
                finally
                {
                    if (ReferenceEquals(_captureHalfPressInFlight, halfPressTask))
                        _captureHalfPressInFlight = null;
                }
            }
            catch (Exception ex)
            {
                lock (_shutterHoldLock)
                    _captureShutterHeld = false;
                StatusMessage = ex.Message;
            }
        }
        finally
        {
            if (releaseWaitOnFailure)
                _shutterPipelineLock.Release();
        }
    }

    /// <summary>半按前：若用户在预览上标记过点且点击时尚未发送触摸，则补发一次遥控触摸。返回 false 时不应继续半按。</summary>
    private async Task<bool> TryApplyPendingFocusTouchBeforeHalfPressAsync()
    {
        if (!_hasPendingFocusPoint || _session == null)
            return true;

        if (_lastShootingState?.RemoteTouchEnable != null && (_lastShootingState.RemoteTouchEnable.Value & 0xFF) == 0)
        {
            StatusMessage = "已标记对焦点，但机身当前关闭遥控触摸/触碰对焦；请在机身菜单中开启后再试。";
            return false;
        }

        if (_touchAfAlreadySentForPendingPoint)
            return true;

        await _session
            .RequestTouchAutofocusAsync(_pendingFocusNx, _pendingFocusNy)
            .ConfigureAwait(true);
        return true;
    }

    /// <summary>拍照按钮松开：单张为一次 Release 脉冲；连拍为 Release Up 并结束半按。</summary>
    public async Task CapturePointerReleasedAsync()
    {
        bool shouldShoot;
        lock (_shutterHoldLock)
        {
            shouldShoot = _captureShutterHeld;
            _captureShutterHeld = false;
            _focusS1Held = false;
        }

        if (!shouldShoot)
        {
            // 快松手、或与对焦抢管道等边缘情况：确保「拍照中」状态被清掉（不释放信号量：本路径未持有）
            SetCaptureTaskActive(false);
            return;
        }

        // 官方 RemoteCli af_shutter：先 S1 Locked，再 Release Down/Up，最后 S1 Unlocked。
        // BeginHalfPressFocusAsync 在后台线程执行；若此处不等待完成就发 Release，机身可能仍处于未半按状态。
        await (_captureHalfPressInFlight ?? Task.CompletedTask).ConfigureAwait(true);

        if (_session != null && _captureBurstShutterDownActive)
        {
            SetCaptureTaskActive(true);
            try
            {
                await _session.CaptureBurstHoldEndAsync().ConfigureAwait(true);
                _captureBurstShutterDownActive = false;
                StatusMessage = "连拍已结束；若未看到全部文件，请查看保存目录与传输进度。";
                _ = RegisterRecentCaptureAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
            }
            finally
            {
                try
                {
                    _shutterPipelineLock.Release();
                }
                catch (SemaphoreFullException)
                {
                }

                SetCaptureTaskActive(false);
            }

            return;
        }

        SetCaptureTaskActive(true);
        try
        {
            if (_session != null)
            {
                await _session
                    .CaptureStillReleaseAfterHalfPressAsync(
                        SaveDirectory,
                        FileNamePrefix,
                        IndexToFileType(CaptureFormatIndex))
                    .ConfigureAwait(true);
                StatusMessage = "已拍照；若未看到文件，请确认所选文件夹且机身允许遥控存到电脑（大文件传完前勿断开）。";
                _ = RegisterRecentCaptureAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            try
            {
                _shutterPipelineLock.Release();
            }
            catch (SemaphoreFullException)
            {
            }

            SetCaptureTaskActive(false);
        }
    }

    /// <summary>拍照按钮失捕未抬起：连拍时先结束 Release Up；否则仅解锁 S1。</summary>
    public async Task CapturePointerCancelledAsync()
    {
        bool held;
        var burstDown = false;
        lock (_shutterHoldLock)
        {
            held = _captureShutterHeld;
            _captureShutterHeld = false;
            burstDown = _captureBurstShutterDownActive;
            _captureBurstShutterDownActive = false;
        }

        if (!held)
        {
            // 松手拍照时 Released 已清空 _captureShutterHeld；此处勿 SetCaptureTaskActive(false)，否则会打断正在 await 的拍照任务。
            return;
        }

        if (_session == null)
        {
            try
            {
                _shutterPipelineLock.Release();
            }
            catch (SemaphoreFullException)
            {
            }

            SetCaptureTaskActive(false);
            return;
        }

        try
        {
            if (burstDown)
                await _session.CaptureBurstHoldEndAsync().ConfigureAwait(true);
            else
                await _session.EndHalfPressFocusAsync().ConfigureAwait(true);
            lock (_shutterHoldLock)
                _focusS1Held = false;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            try
            {
                _shutterPipelineLock.Release();
            }
            catch (SemaphoreFullException)
            {
            }

            SetCaptureTaskActive(false);
        }
    }

    private bool CanDismissPreviewTouchFocus() => ShowFocusReticle && IsSessionActive;

    [RelayCommand(CanExecute = nameof(CanDismissPreviewTouchFocus))]
    private async Task DismissPreviewTouchFocusAsync()
    {
        _hasPendingFocusPoint = false;
        _touchAfAlreadySentForPendingPoint = false;
        ShowFocusReticle = false;

        if (_session != null && IsSessionActive)
        {
            try
            {
                await _session.CancelRemoteTouchOperationAsync().ConfigureAwait(true);
                StatusMessage = "已取消触摸对焦点，并已通知机身恢复自动对焦区域。";
            }
            catch (Exception ex)
            {
                StatusMessage = "已清除本地标记；取消遥控触摸失败: " + ex.Message;
            }
        }
        else
        {
            StatusMessage = "已清除本地对焦点标记。";
        }
    }

    /// <summary>断开/关窗前：若连拍全按未弹起则结束连拍；否则仅解锁半按。</summary>
    private async Task ReleaseAnyShutterHalfPressAsync()
    {
        bool needUnlock;
        var hadS1HalfPressFromUser = false;
        var burstDown = false;
        lock (_shutterHoldLock)
        {
            hadS1HalfPressFromUser = _focusS1Held || _captureShutterHeld;
            needUnlock = _focusS1Held || _captureShutterHeld;
            burstDown = _captureBurstShutterDownActive;
            _captureBurstShutterDownActive = false;
            _focusS1Held = false;
            _captureShutterHeld = false;
            _captureHalfPressInFlight = null;
        }

        if (!needUnlock || _session == null)
        {
            SetCaptureTaskActive(false);
            return;
        }

        try
        {
            if (burstDown)
                await _session.CaptureBurstHoldEndAsync().ConfigureAwait(true);
            else
                await _session.EndHalfPressFocusAsync().ConfigureAwait(true);
        }
        catch
        {
            // 断开时机身可能已离线
        }

        if (hadS1HalfPressFromUser)
        {
            try
            {
                _shutterPipelineLock.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        SetCaptureTaskActive(false);
    }

    private bool CanCrSdkCameraOps() => IsSessionActive && _session != null;

    /// <summary>机身驱动模式为连拍（Hi/Lo/Mid 等）时，拍照键采用「按住连拍、松手结束」。</summary>
    private bool IsBurstDriveMode() =>
        _lastShootingState?.DriveMode != null
        && CrSdkShootingDriveMode.Classify(_lastShootingState.DriveMode.Value)
            == CrSdkShootingDriveCategoryKind.Burst;

    partial void OnSaveDirectoryChanged(string value)
    {
        if (_persistEnabled)
            UserCameraSettingsStore.Save(ToSettings());
        RefreshRecentGalleryFromDisk();
        ScheduleSyncSaveSettingsToCameraDebounced();
    }

    partial void OnCaptureFormatIndexChanged(int value)
    {
        if (_persistEnabled)
            UserCameraSettingsStore.Save(ToSettings());
        if (IsSessionActive && _session != null)
            _ = PushCaptureFormatToCameraAsync();
    }

    /// <summary>用户更改「仅 JPEG / RAW / RAW+JPEG」后立即同步 <see cref="CrSdkDevicePropertyCodes.FileType"/>，避免仍按旧格式拍摄。</summary>
    private async Task PushCaptureFormatToCameraAsync()
    {
        if (_session == null)
            return;
        try
        {
            await _session
                .ApplyCameraSaveSettingsAsync(SaveDirectory, FileNamePrefix, IndexToFileType(CaptureFormatIndex))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "同步拍摄格式到相机失败: " + ex.Message;
        }
    }

    partial void OnFileNamePrefixChanged(string value)
    {
        if (_persistEnabled)
            UserCameraSettingsStore.Save(ToSettings());
        ScheduleSyncSaveSettingsToCameraDebounced();
    }

    partial void OnShowHistogramChanged(bool value)
    {
        if (_persistEnabled)
            UserCameraSettingsStore.Save(ToSettings());
        OnPropertyChanged(nameof(IsHistogramOverlayVisible));
    }

    partial void OnGuideOverlayIndexChanged(int value)
    {
        if (_persistEnabled)
            UserCameraSettingsStore.Save(ToSettings());
        OnPropertyChanged(nameof(IsGuideOverlayVisible));
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        DismissPreviewTouchFocusCommand.NotifyCanExecuteChanged();
        UpdateShootingPollForSession();
        OnPropertyChanged(nameof(ShutterActionButtonsEnabled));
    }

    partial void OnShowFocusReticleChanged(bool value)
    {
        DismissPreviewTouchFocusCommand.NotifyCanExecuteChanged();
        UpdateFocusReticleCloseMargin();
    }

    partial void OnPreviewImageChanged(IImage? value)
    {
        DismissPreviewTouchFocusCommand.NotifyCanExecuteChanged();
        UpdateFocusReticleCloseMargin();
        OnPropertyChanged(nameof(IsHistogramOverlayVisible));
        OnPropertyChanged(nameof(IsGuideOverlayVisible));
    }

    partial void OnSdkAfFocusFramesChanged(IReadOnlyList<CrSdkLiveViewFocusFrameView>? value)
    {
        UpdateFocusReticleCloseMargin();
    }

    partial void OnPreviewLayoutSizeChanged(Size value) => UpdateFocusReticleCloseMargin();

    private static CrSdkLiveViewFocusFrameView? PickAnchorFrameForCloseButton(
        IReadOnlyList<CrSdkLiveViewFocusFrameView>? frames)
    {
        if (frames == null || frames.Count == 0)
            return null;
        foreach (var f in frames)
        {
            if (f.IsFocused)
                return f;
        }

        return frames[0];
    }

    private void UpdateFocusReticleCloseMargin()
    {
        if (!ShowFocusReticle || PreviewImage is not Bitmap bmp || PreviewLayoutSize.Width <= 0)
        {
            FocusReticleCloseMargin = default;
            return;
        }

        var anchor = PickAnchorFrameForCloseButton(SdkAfFocusFrames);
        if (anchor == null)
        {
            FocusReticleCloseMargin = default;
            return;
        }

        if (!FocusReticleLayout.TryComputeCloseButtonMarginForNormalizedRect(
                PreviewLayoutSize,
                bmp.PixelSize,
                anchor.Left,
                anchor.Top,
                anchor.Width,
                anchor.Height,
                FocusReticleLayout.CloseButtonSize,
                out var m))
        {
            FocusReticleCloseMargin = default;
            return;
        }

        FocusReticleCloseMargin = m;
    }

    private CameraUserSettings ToSettings() =>
        new()
        {
            SaveDirectory = SaveDirectory,
            CaptureFormatIndex = CaptureFormatIndex,
            FileNamePrefix = FileNamePrefix,
            ShowHistogram = ShowHistogram,
            GuideOverlayIndex = GuideOverlayIndex,
        };

    private static CrSdkFileType IndexToFileType(int index) =>
        index switch
        {
            1 => CrSdkFileType.Raw,
            2 => CrSdkFileType.RawJpeg,
            _ => CrSdkFileType.Jpeg,
        };

    [RelayCommand]
    private async Task Connect()
    {
        if (_session != null || IsConnecting)
            return;

        IsConnecting = true;
        StatusMessage = "正在连接相机，请稍候…";
        ConnectedCameraModelName = "未知";

        var session = new CrSdkCameraPreviewSession();

        session.FrameReceived += OnFrameReceived;

        try
        {
            try
            {
                await session.ConnectAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                session.FrameReceived -= OnFrameReceived;
                try
                {
                    await session.DisposeAsync().ConfigureAwait(true);
                }
                catch
                {
                    // CrSDK 未部署 DLL 时 Dispose 内仍可能触发 P/Invoke；忽略二次异常
                }

                StatusMessage = ex.Message;
                return;
            }

            _session = session;
            IsSessionActive = true;
            ConnectedCameraModelName = string.IsNullOrWhiteSpace(session.ConnectedCameraModel)
                ? "未知"
                : session.ConnectedCameraModel;
            IsConnecting = false;

            try
            {
                UpdateShootingPollForSession();
            }
            catch (Exception ex)
            {
                StatusMessage = "连接后初始化失败: " + ex.Message;
                await ShutdownCameraSessionAsync().ConfigureAwait(true);
                return;
            }

            try
            {
                await _session.ApplyCameraSaveSettingsAsync(
                        SaveDirectory,
                        FileNamePrefix,
                        IndexToFileType(CaptureFormatIndex))
                    .ConfigureAwait(true);
                StatusMessage = "已连接相机，并已应用保存目录与格式。";
            }
            catch (Exception ex)
            {
                StatusMessage = "已连接相机，但应用保存设置失败: " + ex.Message;
            }

            try
            {
                await _session.StartPreviewAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = "已连接相机，但启动实时预览失败: " + ex.Message;
                await ShutdownCameraSessionAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            if (IsConnecting)
                IsConnecting = false;
        }
    }

    /// <summary>停止拍摄轮询、断开并释放相机会话（关窗口与「断开」共用）。</summary>
    private async Task ShutdownCameraSessionAsync()
    {
        await ReleaseAnyShutterHalfPressAsync().ConfigureAwait(true);

        StopShootingPoll();
        if (_session == null)
            return;

        _session.FrameReceived -= OnFrameReceived;
        var sessionToDispose = _session;
        _session = null;

        try
        {
            await sessionToDispose.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 断开时 native 已释放；忽略二次异常以免闪退
        }

        // 先置空 _session，避免 UI 队列中滞后的预览帧再次写入；再清空绑定位图（须在 UI 线程）。
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ClearPreview();
            IsSessionActive = false;
            ConnectedCameraModelName = "未知";
        });
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        if (_session == null)
        {
            ClearPreview();
            return;
        }

        await ShutdownCameraSessionAsync().ConfigureAwait(true);
        StatusMessage = "已断开。";
    }

    private void OnFrameReceived(object? sender, Bitmap frame)
    {
        void Apply()
        {
            try
            {
                // 已断开但 Post 仍排队时：丢弃位图，避免重复 Dispose / 恢复已清空的预览。
                if (_session == null)
                {
                    frame.Dispose();
                    return;
                }

                _lastFrameOwner?.Dispose();
                _lastFrameOwner = frame;
                if (!IsViewingLiveMonitor)
                    return;

                PreviewImage = frame;
                LuminanceHistogramBins = HistogramLuminance.ComputeNormalized(frame) ?? new double[256];
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
            catch
            {
                try
                {
                    if (ReferenceEquals(PreviewImage, frame))
                        PreviewImage = null;
                    if (ReferenceEquals(_lastFrameOwner, frame))
                        _lastFrameOwner = null;
                    frame.Dispose();
                }
                catch
                {
                }
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void ClearPreview()
    {
        PreviewImage = null;
        StaticReviewImage = null;
        _staticReviewBitmap?.Dispose();
        _staticReviewBitmap = null;
        _lastFrameOwner?.Dispose();
        _lastFrameOwner = null;
        LuminanceHistogramBins = null;
        ViewingRecentPhotoPath = null;
        IsViewingLiveMonitor = true;
        _hasPendingFocusPoint = false;
        _touchAfAlreadySentForPendingPoint = false;
        ShowFocusReticle = false;
        SdkAfFocusFrames = null;
        _previewFocusPointerDown = false;
    }

    public async Task OnPreviewTappedAsync(Point positionInBorder, Size borderSize)
    {
        if (!IsViewingLiveMonitor)
            return;
        if (_session == null || PreviewImage is not Bitmap bmp || !IsSessionActive)
            return;

        if (!PreviewHitTest.TryGetNormalizedImageCoords(positionInBorder, borderSize, bmp.PixelSize, out var nx, out var ny))
            return;

        _pendingFocusNx = nx;
        _pendingFocusNy = ny;
        _hasPendingFocusPoint = true;
        _touchAfAlreadySentForPendingPoint = false;
        ShowFocusReticle = true;
        UpdateFocusReticleCloseMargin();

        if (_lastShootingState?.RemoteTouchEnable != null && (_lastShootingState.RemoteTouchEnable.Value & 0xFF) == 0)
        {
            StatusMessage = "已标记位置；请在机身菜单中开启遥控触摸/触碰对焦后再点击画面或半按对焦。";
            await Task.CompletedTask;
            return;
        }

        var px = (nx * 100.0).ToString("0.0", CultureInfo.InvariantCulture);
        var py = (ny * 100.0).ToString("0.0", CultureInfo.InvariantCulture);

        try
        {
            await _session!.RequestTouchAutofocusAsync(nx, ny).ConfigureAwait(true);
            _touchAfAlreadySentForPendingPoint = true;
            StatusMessage =
                $"已同步对焦点（约 {px}%×{py}%）；保持按住预览即半按对焦，松开即释放。";
        }
        catch (Exception ex)
        {
            StatusMessage = "遥控触摸失败: " + ex.Message;
        }
    }

    /// <summary>
    /// 预览按下：先完成选点与遥控触摸，再开始半按对焦（与侧栏「对焦」按住相同）；预览松开请调用 <see cref="OnPreviewFocusGestureReleasedAsync"/>。
    /// </summary>
    public async Task OnPreviewFocusGesturePressedAsync(Point positionInBorder, Size borderSize)
    {
        if (!IsViewingLiveMonitor)
            return;
        if (_captureTaskActive)
            return;

        _previewFocusPointerDown = true;
        var epoch = ++_previewFocusEpoch;
        await OnPreviewTappedAsync(positionInBorder, borderSize).ConfigureAwait(true);
        if (!IsSessionActive || _session == null)
            return;
        if (epoch != _previewFocusEpoch)
            return;
        await FocusPointerPressedAsync().ConfigureAwait(true);
    }

    /// <summary>预览松开：结束半按，并取消尚未完成的半按启动（避免快点导致 S1 卡住）。</summary>
    public Task OnPreviewFocusGestureReleasedAsync()
    {
        if (_previewFocusPointerDown)
        {
            _previewFocusPointerDown = false;
            _previewFocusEpoch++;
        }

        return FocusPointerReleasedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        CancelFilmstripHostWidthUpdates();
        await ShutdownCameraSessionAsync().ConfigureAwait(false);
    }
}
