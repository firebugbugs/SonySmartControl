using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SonySmartControl.Helpers;
using SonySmartControl.Interop;
using SonySmartControl.Services.Camera;
using SonySmartControl.Services.Logging;
using SonySmartControl.Services.Platform;
using SonySmartControl.Services.Settings;
using SonySmartControl.Settings;
using SonySmartControl.Views;

namespace SonySmartControl.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ICameraSettingsProfilesStore _profilesStore;
    private readonly IUserCameraSettingsService _userCameraSettings;
    private readonly IFolderPickerService _folderPicker;
    private readonly MainWindowCameraOperations _cameraOps;
    private readonly ICrSdkShootingWriteService _crSdkShootingWrite;
    private readonly IAppLogService _appLogService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ITopLevelProvider _topLevelProvider;
    private readonly IMainWindowShellService _mainWindowShell;
    private readonly IExternalUriLauncher _externalUriLauncher;

    // ---- 互斥窗口：设备搜索 / 日志窗口（同一时间只允许打开一个）----
    private readonly object _exclusiveToolWindowGate = new();
    private Window? _exclusiveToolWindow;
    private string? _exclusiveToolWindowTitle;

    private bool _persistEnabled;
    private long? _currentProfileId;

    private int _saveSettingsDbPersistVersion;
    private int _cameraShootingProfilePersistVersion;

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

    public const string ReadyHintText = "就绪：点击顶部按钮打开设备搜索并选择机身。";

    [ObservableProperty] private string _statusMessage = ReadyHintText;

    [ObservableProperty] private bool _showReadyHint = true;
    [ObservableProperty] private string _transportSpeedText = "↑0 B/s ↓0 B/s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectEnabled))]
    [NotifyPropertyChangedFor(nameof(IsDisconnectEnabled))]
    [NotifyPropertyChangedFor(nameof(ShootingPanelVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterShootingBlockVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterExposureSectionVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterFlashSectionVisible))]
    [NotifyPropertyChangedFor(nameof(IsSdkAfFramesOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeText))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeForeground))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeBorderBrush))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private bool _isSessionActive;

    /// <summary>连接 CrSDK 进行中（阻塞在后台线程，用于禁用「连接」与状态提示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectEnabled))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeText))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeForeground))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionBadgeBorderBrush))]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private bool _isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private string _saveDirectory = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private int _captureFormatIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StillImageQualityLabel))]
    [NotifyPropertyChangedFor(nameof(StillImageSizeLabel))]
    private int _selectedStillCodecIndex;

    [ObservableProperty] private int _selectedStillFileFormatIndex;

    [ObservableProperty] private ObservableCollection<string> _stillCodecChoices = new(new[] { "JPEG", "HEIF" });

    private static readonly IReadOnlyList<string> JpegStillFileFormats = new[] { "JPEG", "RAW", "RAW + JPEG" };
    private static readonly IReadOnlyList<string> HeifStillFileFormats = new[] { "HEIF", "RAW", "RAW + HEIF" };

    public IReadOnlyList<string> CurrentStillFileFormatChoices =>
        SelectedStillCodecIndex == 1 ? HeifStillFileFormats : JpegStillFileFormats;

    public string StillImageQualityLabel => SelectedStillCodecIndex == 1 ? "HEIF 质量" : "JPEG 质量";
    public string StillImageSizeLabel => SelectedStillCodecIndex == 1 ? "HEIF 尺寸" : "JPEG 尺寸";

    [ObservableProperty] private string _fileNamePrefix = "";

    [ObservableProperty] private string _timelapseSaveDirectory = "";

    [ObservableProperty] private int _timelapseIntervalSeconds;

    [ObservableProperty] private bool _isTimelapseRunning;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimelapseProgressText))]
    private int _timelapseTargetFrames;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TimelapseProgressText))]
    private int _timelapseCapturedFrames;

    [ObservableProperty] private bool _isTimelapsePaused;
    [ObservableProperty] private bool _isTimelapseStopping;

    [ObservableProperty] private string _timelapseProgressText = "";

    private DispatcherTimer? _timelapseProgressTimer;
    private int _timelapseRunningIntervalSeconds;
    private int _timelapseRunningTargetFrames;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private string _connectedCameraModelName = "未知";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private string _connectedCameraLensModelName = "暂未识别";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraConnectionTooltip))]
    private string _connectedCameraBatteryLevelText = "暂未识别";

    public bool IsConnectEnabled => !IsSessionActive && !IsConnecting;

    public bool IsDisconnectEnabled => IsSessionActive;

    public bool ShootingPanelVisible => IsSessionActive;

    private static readonly string[] SidebarSaveFilterKeywords =
    {
        "保存设置", "保存", "目录", "浏览", "前缀", "文件名", "DSC",
    };

    private static readonly string[] SidebarExposureFilterKeywords =
    {
        "拍照设置", "对焦", "拍摄", "JPEG", "HEIF", "RAW", "格式", "压缩",
        "质量", "尺寸", "横纵", "曝光", "光圈", "快门", "ISO", "补偿",
        "曝光模式", "驱动", "连拍", "延时", "机械", "电子", "快门类型",
        "文件格式", "JPEG/HEIF"
    };

    private static readonly string[] SidebarFlashFilterKeywords =
    {
        "闪光灯设置", "闪光", "闪光模式", "闪光补偿",
    };

    private static readonly string[] SidebarAuxFilterKeywords =
    {
        "辅助显示", "直方图", "辅助线", "三分", "十字", "对角", "安全", "构图",
    };

    private static readonly string[] SidebarTimelapseFilterKeywords =
    {
        "延时摄影", "延时", "间隔", "张数", "无限制", "暂停", "继续", "停止", "开始",
    };

    private bool SidebarFilterIsEmpty() => string.IsNullOrWhiteSpace(SidebarSettingsSearchText);

    private bool SidebarFilterMatches(params string[] keywords)
    {
        var q = SidebarSettingsSearchText?.Trim() ?? "";
        if (q.Length == 0) return true;
        foreach (var k in keywords)
        {
            if (k.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public bool SidebarFilterSaveSectionVisible => SidebarFilterMatches(SidebarSaveFilterKeywords);

    public bool SidebarFilterExposureSectionVisible =>
        ShootingPanelVisible && SidebarFilterMatches(SidebarExposureFilterKeywords);

    public bool SidebarFilterFlashSectionVisible =>
        ShootingPanelVisible && SidebarFilterMatches(SidebarFlashFilterKeywords);

    public bool SidebarFilterShootingBlockVisible =>
        ShootingPanelVisible &&
        (SidebarFilterIsEmpty() ||
         SidebarFilterMatches(SidebarExposureFilterKeywords) ||
         SidebarFilterMatches(SidebarFlashFilterKeywords));

    public bool SidebarFilterAuxDisplayVisible => SidebarFilterMatches(SidebarAuxFilterKeywords);

    public bool SidebarFilterTimelapseSectionVisible => SidebarFilterMatches(SidebarTimelapseFilterKeywords);

    public string CameraConnectionBadgeText =>
        IsConnecting ? "连接中" : IsSessionActive ? "已连接" : "未连接";
    /// <summary>徽标背景固定为白底，由 <see cref="CameraConnectionBadgeBorderBrush"/> 与前景色区分状态。</summary>
    public string CameraConnectionBadgeBackground => "#FFFFFF";

    public string CameraConnectionBadgeForeground =>
        IsConnecting ? "#9A6A00" : IsSessionActive ? "#0E7A43" : "#3F4A5C";

    /// <summary>徽标描边：未连接时略加深，与标题栏区分更明显。</summary>
    public string CameraConnectionBadgeBorderBrush =>
        IsConnecting ? "#E5C266" : IsSessionActive ? "#7BC49A" : "#A8B2C2";

    private string CaptureFormatLabel =>
        CaptureFormatIndex switch
        {
            1 => "RAW",
            2 => "RAW + JPEG",
            3 => "HEIF",
            4 => "RAW + HEIF",
            _ => "JPEG",
        };
    public string CameraConnectionTooltip =>
        $"状态：{CameraConnectionBadgeText}\n型号：{(string.IsNullOrWhiteSpace(ConnectedCameraModelName) ? "未知" : ConnectedCameraModelName)}\n镜头：{ConnectedCameraLensModelName}\n电量：{ConnectedCameraBatteryLevelText}\n保存目录：{SaveDirectory}";

    /// <summary>构图辅助线下拉项（顺序与 <see cref="GuideOverlayIndex"/> 一致）。</summary>
    public ObservableCollection<string> GuideOverlayChoices { get; } = new(
        new[] { "无", "三分法", "十字对准线", "对角线", "安全区" });

    [ObservableProperty] private bool _saveSectionExpanded = true;

    [ObservableProperty] private bool _generalSectionExpanded = true;

    [ObservableProperty] private bool _exposureSectionExpanded = true;
    [ObservableProperty] private bool _flashSectionExpanded = true;

    [ObservableProperty] private bool _auxDisplaySectionExpanded = true;
    [ObservableProperty] private bool _timelapseSectionExpanded = true;

    /// <summary>侧栏设置列表搜索过滤（空则显示全部区块）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarFilterSaveSectionVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterShootingBlockVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterExposureSectionVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterFlashSectionVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterAuxDisplayVisible))]
    [NotifyPropertyChangedFor(nameof(SidebarFilterTimelapseSectionVisible))]
    private string _sidebarSettingsSearchText = "";

    [ObservableProperty] private bool _showFormatSdCardConfirm;

    /// <summary>已连接徽标弹出的相机操作浮层（与 <see cref="ToggleCameraActionsPopupCommand"/> 联动）。</summary>
    [ObservableProperty] private bool _isConnectedActionsPopupOpen;

    // SD 卡容量/使用量（估算）：用于“格式化 SD 卡”浮窗里的进度条展示。
    [ObservableProperty] private bool _sdCardSlot1HasCard;
    [ObservableProperty] private string _sdCardSlot1SummaryText = "—";
    [ObservableProperty] private double _sdCardSlot1UsagePercent;
    [ObservableProperty] private IBrush _sdCardSlot1ProgressBrush = new SolidColorBrush(Color.Parse("#2FBF71")); // 绿色：<=80%

    [ObservableProperty] private bool _sdCardSlot2HasCard;
    [ObservableProperty] private string _sdCardSlot2SummaryText = "—";
    [ObservableProperty] private double _sdCardSlot2UsagePercent;
    [ObservableProperty] private IBrush _sdCardSlot2ProgressBrush = new SolidColorBrush(Color.Parse("#F59E0B")); // 橙色：>80%，默认先给橙色防止误显示绿色
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSdCardUsageDebugText))]
    private string _sdCardUsageDebugText = "";
    public bool HasSdCardUsageDebugText => !string.IsNullOrWhiteSpace(SdCardUsageDebugText);

    /// <summary>右侧遥控区：0=拍照，1=摄影。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPhotoSidebarMode))]
    [NotifyPropertyChangedFor(nameof(IsVideoSidebarMode))]
    [NotifyPropertyChangedFor(nameof(PhotoSidebarModeTextColor))]
    [NotifyPropertyChangedFor(nameof(VideoSidebarModeTextColor))]
    private int _remoteSidebarMode;

    [ObservableProperty]
    private double _remoteSidebarThumbOffset;

    public bool IsPhotoSidebarMode => RemoteSidebarMode == 0;

    public bool IsVideoSidebarMode => RemoteSidebarMode == 1;

    public string PhotoSidebarModeTextColor => IsPhotoSidebarMode ? "#FFFFFF" : "#4286F5";

    public string VideoSidebarModeTextColor => IsVideoSidebarMode ? "#FFFFFF" : "#4286F5";

    /// <summary>运行期由 DI 解析；依赖具体服务实现。</summary>
    public MainWindowViewModel(
        ICameraSettingsProfilesStore profilesStore,
        IUserCameraSettingsService userCameraSettings,
        IFolderPickerService folderPicker,
        MainWindowCameraOperations cameraOps,
        ICrSdkShootingWriteService crSdkShootingWrite,
        IAppLogService appLogService,
        IServiceProvider serviceProvider,
        ITopLevelProvider topLevelProvider,
        IMainWindowShellService mainWindowShell,
        IExternalUriLauncher externalUriLauncher)
    {
        _profilesStore = profilesStore;
        _userCameraSettings = userCameraSettings;
        _folderPicker = folderPicker;
        _cameraOps = cameraOps;
        _crSdkShootingWrite = crSdkShootingWrite;
        _appLogService = appLogService;
        _serviceProvider = serviceProvider;
        _topLevelProvider = topLevelProvider;
        _mainWindowShell = mainWindowShell;
        _externalUriLauncher = externalUriLauncher;
        _persistEnabled = false;
        ApplySettingsToUi(new CameraUserSettings());
        _persistEnabled = true;

        // 异步初始化：确保默认配置存在，并加载当前配置到界面。
        _ = InitializeProfilesAndLoadCurrentAsync();
        RefreshRecentGalleryFromDisk();
        _appLogService.Append(StatusMessage);
    }

    /// <summary>设计器与 MainWindow.axaml 预览用默认实现。</summary>
    public MainWindowViewModel()
        : this(
            new SqliteCameraSettingsProfilesStore(),
            new UserCameraSettingsService(),
            new AvaloniaFolderPickerService(new TopLevelProvider()),
            new MainWindowCameraOperations(
                new CrSdkCameraPreviewSessionFactory(),
                new CrSdkSdCardMediaFormatService(),
                () => throw new InvalidOperationException()),
            new CrSdkShootingWriteService(),
            DesignTimeAppLog,
            new ServiceCollection()
                .AddSingleton<IAppLogService>(DesignTimeAppLog)
                .AddTransient<LogHistoryViewModel>()
                .BuildServiceProvider(),
            new TopLevelProvider(),
            new MainWindowShellService(new TopLevelProvider()),
            new ProcessExternalUriLauncher())
    {
    }

    private async Task InitializeProfilesAndLoadCurrentAsync()
    {
        try
        {
            var profiles = await _profilesStore.ListAsync().ConfigureAwait(true);
            if (profiles.Count == 0)
            {
                // 程序第一次启动：自动创建“默认配置”，并设为当前。
                var id = await _profilesStore
                    .CreateAsync("默认配置", new CameraUserSettings())
                    .ConfigureAwait(true);
                await _profilesStore.SetCurrentProfileIdAsync(id).ConfigureAwait(true);
                _currentProfileId = id;
            }
            else
            {
                _currentProfileId = await _profilesStore.GetCurrentProfileIdAsync().ConfigureAwait(true);
                if (_currentProfileId == null)
                {
                    // 若没有 current_profile_id，则取最新的一条作为当前。
                    _currentProfileId = profiles[0].Id;
                    await _profilesStore.SetCurrentProfileIdAsync(_currentProfileId).ConfigureAwait(true);
                }
            }

            if (_currentProfileId is long pid)
            {
                var s = await _profilesStore.LoadAsync(pid).ConfigureAwait(true);
                if (s != null)
                {
                    _persistEnabled = false;
                    ApplySettingsToUi(s);
                    _persistEnabled = true;
                }
            }
        }
        catch
        {
            // 初始化失败不影响 UI；继续用默认值
        }
    }

    private void ApplySettingsToUi(CameraUserSettings s)
    {
        // 这里必须通过生成的属性赋值（避免 MVVMTK0034 警告），
        // 且调用方会先把 _persistEnabled 置为 false，避免触发写库。
        SaveDirectory = s.SaveDirectory;
        CaptureFormatIndex = s.CaptureFormatIndex;
        RefreshStillFormatUiFromCaptureFormat(CaptureFormatIndex);
        FileNamePrefix = s.FileNamePrefix;
        TimelapseSaveDirectory = s.TimelapseSaveDirectory;
        TimelapseIntervalSeconds = Math.Max(2, s.TimelapseIntervalSeconds);
        TimelapseTargetFrames = Math.Max(0, s.TimelapseTargetFrames);
        ShowHistogram = s.ShowHistogram ?? true;
        GuideOverlayIndex = s.GuideOverlayIndex;
    }

    private void SchedulePersistSettingsToDbDebounced()
    {
        if (!_persistEnabled)
            return;
        var v = ++_saveSettingsDbPersistVersion;
        _ = PersistSettingsToDbAfterDelayAsync(v);
    }

    private void SchedulePersistCameraShootingProfileFromStateDebounced(CrSdkShootingState s)
    {
        if (!_persistEnabled)
            return;
        if (_currentProfileId is not long)
            return;
        var v = ++_cameraShootingProfilePersistVersion;
        _ = PersistCameraShootingProfileFromStateAfterDelayAsync(v, s);
    }

    private async Task PersistCameraShootingProfileFromStateAfterDelayAsync(int version, CrSdkShootingState s)
    {
        try
        {
            await Task.Delay(250).ConfigureAwait(true);
            if (version != _cameraShootingProfilePersistVersion)
                return;
            if (_currentProfileId is not long pid)
                return;
            var profile = CameraShootingProfile.FromState(s);
            var json = CameraShootingProfile.ToJson(profile);
            await _profilesStore.UpdateCameraSettingsJsonAsync(pid, json).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    private async Task PersistSettingsToDbAfterDelayAsync(int version)
    {
        try
        {
            await Task.Delay(200).ConfigureAwait(true);
            if (version != _saveSettingsDbPersistVersion)
                return;
            if (_currentProfileId is not long pid)
                return;
            await _profilesStore.UpdateAsync(pid, ToSettings()).ConfigureAwait(true);
        }
        catch
        {
        }
    }

    private static readonly AppLogService DesignTimeAppLog = new();

    [RelayCommand]
    private void OpenSettingsProfiles()
    {
        if (_currentProfileId is not long pid)
        {
            StatusMessage = "配置尚未初始化，请稍候…";
            return;
        }

        var win = new SettingsProfilesWindow();
        var vm = new SettingsProfilesViewModel(
            _profilesStore,
            pid,
            async id => await ApplyProfileAndSetCurrentAsync(id).ConfigureAwait(true),
            () => win.Close());
        win.DataContext = vm;
        if (_topLevelProvider.GetTopLevel() is Window owner)
            win.Show(owner);
        else
            win.Show();
    }

    private async Task ApplyProfileAndSetCurrentAsync(long id)
    {
        try
        {
            await _profilesStore.SetCurrentProfileIdAsync(id).ConfigureAwait(true);
            _currentProfileId = id;
            var s = await _profilesStore.LoadAsync(id).ConfigureAwait(true);
            if (s == null)
                return;
            _persistEnabled = false;
            ApplySettingsToUi(s);
            _persistEnabled = true;

            // 切换配置后，把关键保存设置同步到相机（避免仍按旧设置拍摄）
            ScheduleSyncSaveSettingsToCameraDebounced();
            if (IsSessionActive && _cameraOps.Session != null)
                _ = PushCaptureFormatToCameraAsync();
        }
        catch
        {
        }
    }

    /// <summary>主窗口将左右方向键交给 ViewModel；在文本框内聚焦时不应抢键。</summary>
    public bool TryHandleGalleryArrowNavigation(Key key, object? focusedElement)
    {
        if (key != Key.Left && key != Key.Right)
            return false;
        if (focusedElement is TextBox or ComboBox)
            return false;
        if (IsViewingLiveMonitor || string.IsNullOrEmpty(ViewingRecentPhotoPath))
            return false;
        var delta = key == Key.Left ? -1 : 1;
        _ = NavigateRecentGalleryAsync(delta);
        return true;
    }

    [RelayCommand]
    private async Task ToggleCameraActionsPopupAsync()
    {
        // 未连接时：相当于点击“连接”，打开设备搜索窗口。
        if (!IsSessionActive)
        {
            OpenDeviceSearch();
            return;
        }
        IsConnectedActionsPopupOpen = !IsConnectedActionsPopupOpen;
        if (IsConnectedActionsPopupOpen)
        {
            ShowFormatSdCardConfirm = false;
            await RefreshSdCardUsageAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenLogHistory()
    {
        if (TryActivateExclusiveToolWindow("日志窗口"))
            return;

        var vm = _serviceProvider.GetRequiredService<LogHistoryViewModel>();
        var win = new LogHistoryWindow { DataContext = vm };
        RegisterExclusiveToolWindow(win, "日志窗口");
        if (_topLevelProvider.GetTopLevel() is Window owner)
            win.Show(owner);
        else
            win.Show();
    }

    [RelayCommand]
    private void ExpandAllSections()
    {
        SaveSectionExpanded = true;
        GeneralSectionExpanded = true;
        ExposureSectionExpanded = true;
        FlashSectionExpanded = true;
        AuxDisplaySectionExpanded = true;
        TimelapseSectionExpanded = true;
    }

    [RelayCommand]
    private void CollapseAllSections()
    {
        SaveSectionExpanded = false;
        GeneralSectionExpanded = false;
        ExposureSectionExpanded = false;
        FlashSectionExpanded = false;
        AuxDisplaySectionExpanded = false;
        TimelapseSectionExpanded = false;
    }

    [RelayCommand]
    private void SelectPhotoSidebarMode() => RemoteSidebarMode = 0;

    [RelayCommand]
    private void SelectVideoSidebarMode() => RemoteSidebarMode = 1;

    partial void OnRemoteSidebarModeChanged(int value)
    {
        // 大切换按钮的滑块位移：左=0（拍照），右=136（摄影）。
        RemoteSidebarThumbOffset = value == 1 ? 136d : 0d;
    }

    [RelayCommand]
    private async Task BrowseSaveFolder()
    {
        try
        {
            var path = await _folderPicker.PickFolderAsync("选择遥控拍摄保存目录").ConfigureAwait(true);
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

    [RelayCommand]
    private async Task BrowseTimelapseSaveFolder()
    {
        try
        {
            var path = await _folderPicker.PickFolderAsync("选择延时摄影保存目录").ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(path))
                TimelapseSaveDirectory = path;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenTimelapseSaveFolder()
    {
        try
        {
            var raw = TimelapseSaveDirectory?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                StatusMessage = "请先填写或选择延时摄影保存目录。";
                return;
            }

            var full = Path.GetFullPath(raw);
            if (!Directory.Exists(full))
            {
                StatusMessage = "该延时摄影文件夹尚不存在，请先创建或选择有效路径。";
                return;
            }

            Process.Start(
                new ProcessStartInfo { FileName = full, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "无法打开延时摄影文件夹: " + ex.Message;
        }
    }

    private bool CanStartTimelapse() =>
        IsSessionActive && !_captureTaskActive && !IsTimelapseRunning && !IsTimelapseStopping;

    private bool CanPauseTimelapse() =>
        IsSessionActive && IsTimelapseRunning && !IsTimelapsePaused && !IsTimelapseStopping;

    private bool CanContinueTimelapse() =>
        IsSessionActive && IsTimelapseRunning && IsTimelapsePaused && !IsTimelapseStopping;

    private bool CanStopTimelapse() =>
        IsSessionActive && IsTimelapseRunning && !IsTimelapseStopping;

    private void StartTimelapseProgressTimer()
    {
        if (_timelapseProgressTimer == null)
        {
            _timelapseProgressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1),
            };

            _timelapseProgressTimer.Tick += (_, _) => UpdateTimelapseProgressText();
        }

        _timelapseProgressTimer.Start();
    }

    private void StopTimelapseProgressTimer() => _timelapseProgressTimer?.Stop();

    private void UpdateTimelapseProgressText()
    {
        if (!IsTimelapseRunning)
        {
            TimelapseProgressText = "";
            return;
        }

        if (_timelapseRunningTargetFrames <= 0)
        {
            TimelapseProgressText = $"已拍摄 {TimelapseCapturedFrames} 张（无限制）";
            return;
        }

        if (IsTimelapsePaused)
        {
            TimelapseProgressText =
                $"已暂停：已拍摄 {TimelapseCapturedFrames}/{_timelapseRunningTargetFrames} 张。";
            return;
        }

        // 首帧在开始后立刻拍一次（和按钮点击同一轮），所以剩余的“间隔次数”应为 (目标 - 已拍 - 1)。
        var remainingShots = TimelapseCapturedFrames <= 0
            ? Math.Max(0, _timelapseRunningTargetFrames - 1)
            : Math.Max(0, _timelapseRunningTargetFrames - TimelapseCapturedFrames);
        var intervalSeconds = Math.Max(TimelapseMinIntervalSeconds, _timelapseRunningIntervalSeconds);
        var remaining = TimeSpan.FromSeconds((long)remainingShots * intervalSeconds);

        var now = DateTime.Now;
        var endAt = now + remaining;

        var remainText = FormatDuration(remaining);
        var endText = FormatEndAt(endAt, now);

        TimelapseProgressText =
            $"已拍摄 {TimelapseCapturedFrames}/{_timelapseRunningTargetFrames} 张，剩余 {remainText}；预计结束 {endText}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds <= 0)
            return "0秒";

        if (duration.TotalMinutes < 60)
            return $"{duration.Minutes}分{duration.Seconds}秒";

        if (duration.TotalHours < 24)
            return $"{duration.Hours}小时{duration.Minutes}分{duration.Seconds}秒";

        // 跨天也展示完整日时，避免用户只看到小时数不知何时结束。
        return $"{(int)duration.TotalDays}天{duration.Hours}小时{duration.Minutes}分{duration.Seconds}秒";
    }

    private static string FormatEndAt(DateTime endAt, DateTime now)
    {
        if (endAt.Date == now.Date)
            return endAt.ToString("HH:mm:ss");

        if (endAt.Year == now.Year)
            return endAt.ToString("MM-dd HH:mm:ss");

        return endAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    [RelayCommand(CanExecute = nameof(CanStartTimelapse))]
    private async Task StartTimelapseAsync()
    {
        if (!CanCrSdkCameraOps() || _cameraOps.Session == null)
        {
            StatusMessage = "未连接相机，无法开始延时摄影。";
            return;
        }

        var dir = TimelapseSaveDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            StatusMessage = "请先填写或选择延时摄影保存目录。";
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            StatusMessage = "无法创建延时摄影目录: " + ex.Message;
            return;
        }

        var intervalSeconds = Math.Max(TimelapseMinIntervalSeconds, TimelapseIntervalSeconds);
        var targetFrames = Math.Max(0, TimelapseTargetFrames);
        var fileType = IndexToFileType(CaptureFormatIndex);
        var prefix = FileNamePrefix;

        lock (_timelapseGate)
        {
            _timelapseCts = new CancellationTokenSource();
            var ct = _timelapseCts.Token;

            _timelapseRunningIntervalSeconds = intervalSeconds;
            _timelapseRunningTargetFrames = targetFrames;

            IsTimelapseRunning = true;
            IsTimelapsePaused = false;
            IsTimelapseStopping = false;
            TimelapseCapturedFrames = 0;
            SetCaptureTaskActive(true); // 禁止预览触摸对焦/半按等，避免与快门 S1 争用
            _timelapsePauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _timelapsePauseTcs.TrySetResult(true);

            _timelapseLoopTask = TimelapseLoopAsync(
                intervalSeconds,
                targetFrames,
                fileType,
                prefix,
                dir,
                ct);
        }

        StatusMessage = targetFrames <= 0
            ? $"延时摄影已开始：间隔 {intervalSeconds} 秒（无限制）；保存到：{dir}"
            : $"延时摄影已开始：间隔 {intervalSeconds} 秒；目标 {targetFrames} 张；保存到：{dir}";

        UpdateTimelapseProgressText();
        StartTimelapseProgressTimer();
    }

    [RelayCommand(CanExecute = nameof(CanPauseTimelapse))]
    private void PauseTimelapse()
    {
        lock (_timelapseGate)
        {
            if (!CanPauseTimelapse())
                return;

            IsTimelapsePaused = true;
            _timelapsePauseTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        StatusMessage = "延时摄影已暂停。";
        StopTimelapseProgressTimer();
        UpdateTimelapseProgressText();
    }

    [RelayCommand(CanExecute = nameof(CanContinueTimelapse))]
    private void ContinueTimelapse()
    {
        TaskCompletionSource<bool>? tcs;
        lock (_timelapseGate)
        {
            if (!CanContinueTimelapse())
                return;

            IsTimelapsePaused = false;
            tcs = _timelapsePauseTcs;
        }

        tcs?.TrySetResult(true);
        StatusMessage = "延时摄影已继续。";
        UpdateTimelapseProgressText();
        StartTimelapseProgressTimer();
    }

    [RelayCommand(CanExecute = nameof(CanStopTimelapse))]
    private async Task StopTimelapseAsync()
    {
        await StopTimelapseInternalAsync().ConfigureAwait(true);
        StatusMessage = "延时摄影已停止。";
    }

    private async Task StopTimelapseInternalAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_timelapseGate)
        {
            if (IsTimelapseStopping)
                return;

            IsTimelapseStopping = true;

            cts = _timelapseCts;
            loop = _timelapseLoopTask;

            _timelapseCts = null;
            _timelapseLoopTask = null;
        }

        StopTimelapseProgressTimer();
        TimelapseProgressText = "";
        _timelapseRunningIntervalSeconds = 0;
        _timelapseRunningTargetFrames = 0;

        if (cts == null)
        {
            IsTimelapseStopping = false;
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch
        {
        }

        try
        {
            if (loop != null)
                await loop.ConfigureAwait(false);
        }
        catch
        {
            // 忽略：取消/断开时不应影响 UI
        }

        try
        {
            cts.Dispose();
        }
        catch
        {
        }
    }

    private async Task TimelapseLoopAsync(
        int intervalSeconds,
        int targetFrames,
        CrSdkFileType fileType,
        string prefix,
        string saveDirectory,
        CancellationToken ct)
    {
        try
        {
            var captured = 0;
            while (!ct.IsCancellationRequested)
            {
                await WaitForTimelapseUnpausedAsync(ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    break;

                await TimelapseCaptureOnceAsync(saveDirectory, fileType, prefix, ct).ConfigureAwait(true);
                captured++;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    TimelapseCapturedFrames = captured;
                    StatusMessage = targetFrames <= 0
                        ? $"延时摄影：已拍摄 {captured} 张。"
                        : $"延时摄影：已拍摄 {captured}/{targetFrames} 张。";
                    UpdateTimelapseProgressText();
                });

                if (targetFrames > 0 && captured >= targetFrames)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StatusMessage = $"延时摄影已完成：{captured} 张。";
                        TimelapseProgressText =
                            $"已拍摄 {captured}/{targetFrames} 张，已完成（{FormatEndAt(DateTime.Now, DateTime.Now)}）。";
                    });
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = "延时摄影失败: " + ex.Message;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTimelapseRunning = false;
                IsTimelapsePaused = false;
                IsTimelapseStopping = false;
                StopTimelapseProgressTimer();
                try
                {
                    SetCaptureTaskActive(false);
                }
                catch
                {
                }
            });
        }
    }

    private Task WaitForTimelapseUnpausedAsync(CancellationToken ct)
    {
        Task pauseTask;
        lock (_timelapseGate)
        {
            pauseTask = _timelapsePauseTcs?.Task ?? Task.CompletedTask;
        }

        return pauseTask.WaitAsync(ct);
    }

    private async Task TimelapseCaptureOnceAsync(
        string saveDirectory,
        CrSdkFileType fileType,
        string prefix,
        CancellationToken ct)
    {
        // 与半按/释放等 S1 相关操作串行化，避免 ErrControlFailed(-8)
        await _shutterPipelineLock.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (_cameraOps.Session == null)
                return;

            await _cameraOps.Session
                .CaptureStillAsync(saveDirectory, prefix, fileType, ct)
                .ConfigureAwait(true);
        }
        finally
        {
            try
            {
                _shutterPipelineLock.Release();
            }
            catch
            {
            }
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
            if (!IsSessionActive || _cameraOps.Session == null)
                return;
            await _cameraOps.Session
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

    // 相对对焦功能已移除（不再通过滚轮驱动镜头 MF 对焦）。

    /// <summary>连拍：已向机身发送 Release Down 且尚未 Up（与半按并存时需先 HoldEnd 再解锁）。</summary>
    private bool _captureBurstShutterDownActive;

    /// <summary>
    /// 串行化所有会触发 S1 半按的操作（对焦键、预览按住、拍照键），避免与快门释放竞态导致 ErrControlFailed(-8)。
    /// </summary>
    private readonly SemaphoreSlim _shutterPipelineLock = new(1, 1);
    private bool _captureFormatUiSync;

    /// <summary>
    /// 从「拍照键按下」到释放快门、冷却结束整段完成前为 true；期间拒绝新的对焦/预览半按指令。
    /// </summary>
    private volatile bool _captureTaskActive;

    private CancellationTokenSource? _timelapseCts;
    private Task? _timelapseLoopTask;

    private const int TimelapseMinIntervalSeconds = 2;
    private readonly object _timelapseGate = new();
    private TaskCompletionSource<bool>? _timelapsePauseTcs;

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
        if (!CanCrSdkCameraOps() || _cameraOps.Session == null)
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

                await _cameraOps.Session.BeginHalfPressFocusAsync().ConfigureAwait(true);
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

        if (!shouldRelease || _cameraOps.Session == null)
            return;

        try
        {
            await _cameraOps.Session.EndHalfPressFocusAsync().ConfigureAwait(true);
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
        if (!CanCrSdkCameraOps() || _cameraOps.Session == null)
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

                var halfPressTask = _cameraOps.Session.BeginHalfPressFocusAsync();
                _captureHalfPressInFlight = halfPressTask;
                try
                {
                    await halfPressTask.ConfigureAwait(true);
                    releaseWaitOnFailure = false;

                    if (IsBurstDriveMode())
                    {
                        try
                        {
                            await _cameraOps.Session
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
                                await _cameraOps.Session.EndHalfPressFocusAsync().ConfigureAwait(true);
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
        if (!_hasPendingFocusPoint || _cameraOps.Session == null)
            return true;

        if (_lastShootingState?.RemoteTouchEnable != null && (_lastShootingState.RemoteTouchEnable.Value & 0xFF) == 0)
        {
            StatusMessage = "已标记对焦点，但机身当前关闭遥控触摸/触碰对焦；请在机身菜单中开启后再试。";
            return false;
        }

        if (_touchAfAlreadySentForPendingPoint)
            return true;

        await _cameraOps.Session
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

        if (_cameraOps.Session != null && _captureBurstShutterDownActive)
        {
            SetCaptureTaskActive(true);
            try
            {
                await _cameraOps.Session.CaptureBurstHoldEndAsync().ConfigureAwait(true);
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
            if (_cameraOps.Session != null)
            {
                var capturedType = IndexToFileType(CaptureFormatIndex);
                await _cameraOps.Session
                    .CaptureStillReleaseAfterHalfPressAsync(
                        SaveDirectory,
                        FileNamePrefix,
                        capturedType)
                    .ConfigureAwait(true);
                var setupDbg = SonyCrSdk.GetLastCaptureTransferSetupDebugText();
                var pullDbg = SonyCrSdk.TryGetLastCapturePullDebugText();
                StatusMessage = string.IsNullOrWhiteSpace(pullDbg)
                    ? $"已拍照。传输诊断：{setupDbg}"
                    : $"已拍照。传输诊断：{setupDbg} | {pullDbg}";
                _ = RegisterRecentCaptureAsync();
                _ = TryRepairHeifOriginalInBackgroundAsync(capturedType, SaveDirectory);
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

        if (_cameraOps.Session == null)
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
                await _cameraOps.Session.CaptureBurstHoldEndAsync().ConfigureAwait(true);
            else
                await _cameraOps.Session.EndHalfPressFocusAsync().ConfigureAwait(true);
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

        if (_cameraOps.Session != null && IsSessionActive)
        {
            try
            {
                await _cameraOps.Session.CancelRemoteTouchOperationAsync().ConfigureAwait(true);
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

        if (!needUnlock || _cameraOps.Session == null)
        {
            SetCaptureTaskActive(false);
            return;
        }

        try
        {
            if (burstDown)
                await _cameraOps.Session.CaptureBurstHoldEndAsync().ConfigureAwait(true);
            else
                await _cameraOps.Session.EndHalfPressFocusAsync().ConfigureAwait(true);
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

    private bool CanCrSdkCameraOps() => IsSessionActive && _cameraOps.Session != null;

    /// <summary>
    /// HEIF 机型拍后常遇到“机身仍在写卡，立即拉取返回 busy/-8”。
    /// 这里做后台延迟补拉，不阻塞拍照主流程；成功后刷新胶片列表。
    /// </summary>
    private async Task TryRepairHeifOriginalInBackgroundAsync(CrSdkFileType capturedType, string saveDir)
    {
        // 重要：内容拉取会触发相机端“导入影像”弹窗；为满足“相机端不能出现弹窗”，禁用自动后台补拉。
        return;

        if (!IsSessionActive)
            return;
        if (capturedType is not (CrSdkFileType.Heif or CrSdkFileType.RawHeif))
            return;
        if (string.IsNullOrWhiteSpace(saveDir))
            return;

        var dir = saveDir.Trim();
        var pullCount = capturedType == CrSdkFileType.RawHeif ? 2 : 1;
        Exception? last = null;
        // HEIF 写卡与内容列表刷新通常更慢，尤其是高码率/高速连拍后；按官方 ContentsTransfer/RemoteTransfer 的 Busy 警告做更长等待。
        var delaysMs = new[] { 2000, 4500, 8000, 12000 };
        var shouldResumeLiveView = false;
        try
        {
            try
            {
                // 关键：停止会话层 LiveView 拉帧线程，避免与内容传输并发导致 -8。
                if (_cameraOps.Session != null && LiveViewEnabled)
                {
                    await _cameraOps.Session.SetLiveViewEnabledAsync(false).ConfigureAwait(true);
                    shouldResumeLiveView = true;
                }
            }
            catch
            {
                shouldResumeLiveView = false;
            }

            for (var i = 0; i < delaysMs.Length; i++)
            {
                if (!IsSessionActive)
                    return;
                await Task.Delay(delaysMs[i]).ConfigureAwait(true);
                try
                {
                    if (!SonyCrBridgeNative.TryPullLatestStillsToFolderUtf16(dir, pullCount))
                        continue;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _ = SyncRecentGalleryWithDisk();
                        StatusMessage = "HEIF 原图后台补拉成功。";
                    });
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }
        }
        finally
        {
            if (shouldResumeLiveView && _cameraOps.Session != null && IsSessionActive)
            {
                try
                {
                    await _cameraOps.Session.SetLiveViewEnabledAsync(true).ConfigureAwait(true);
                }
                catch
                {
                }
            }
        }

        if (last != null)
        {
            var dbg = SonyCrSdk.TryGetLastCapturePullDebugText();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = string.IsNullOrWhiteSpace(dbg)
                    ? "HEIF 原图后台补拉失败：" + last.Message
                    : $"HEIF 原图后台补拉失败：{last.Message} | {dbg}";
            });
        }
    }

    /// <summary>机身驱动模式为连拍（Hi/Lo/Mid 等）时，拍照键采用「按住连拍、松手结束」。</summary>
    private bool IsBurstDriveMode() =>
        _lastShootingState?.DriveMode != null
        && CrSdkShootingDriveMode.Classify(_lastShootingState.DriveMode.Value)
            == CrSdkShootingDriveCategoryKind.Burst;

    partial void OnSaveDirectoryChanged(string value)
    {
        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
        RefreshRecentGalleryFromDisk();
        ScheduleSyncSaveSettingsToCameraDebounced();
    }

    partial void OnTimelapseSaveDirectoryChanged(string value)
    {
        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
    }

    partial void OnTimelapseIntervalSecondsChanged(int value)
    {
        const int minSeconds = 2;
        if (value < minSeconds && TimelapseIntervalSeconds != minSeconds)
            TimelapseIntervalSeconds = minSeconds;

        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
    }

    partial void OnTimelapseTargetFramesChanged(int value)
    {
        if (value < 0 && TimelapseTargetFrames != 0)
            TimelapseTargetFrames = 0;

        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
    }

    partial void OnCaptureFormatIndexChanged(int value)
    {
        RefreshStillFormatUiFromCaptureFormat(value);
        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
        if (IsSessionActive && _cameraOps.Session != null)
            _ = PushCaptureFormatToCameraAsync();
    }

    partial void OnSelectedStillCodecIndexChanged(int value)
    {
        if (_captureFormatUiSync)
            return;

        var codec = value == 1 ? 1 : 0;
        // 切换编码后默认回到首项（JPEG 或 HEIF），避免沿用旧索引导致联动错乱。
        const int fileFmt = 0;

        _captureFormatUiSync = true;
        try
        {
            OnPropertyChanged(nameof(CurrentStillFileFormatChoices));
            SelectedStillFileFormatIndex = fileFmt;
        }
        finally
        {
            _captureFormatUiSync = false;
        }

        CaptureFormatIndex = ComposeCaptureFormatIndex(codec, fileFmt);
    }

    partial void OnSelectedStillFileFormatIndexChanged(int value)
    {
        if (_captureFormatUiSync)
            return;
        var codec = SelectedStillCodecIndex == 1 ? 1 : 0;
        var fileFmt = value;
        if (fileFmt < 0 || fileFmt > 2)
            fileFmt = 0;
        CaptureFormatIndex = ComposeCaptureFormatIndex(codec, fileFmt);
    }

    private void RefreshStillFormatUiFromCaptureFormat(int captureFormatIndex)
    {
        var codec = captureFormatIndex is 3 or 4 ? 1 : 0;
        var fileFmt = captureFormatIndex switch
        {
            1 => 1,
            2 => 2,
            4 => 2,
            3 => 0,
            _ => 0,
        };

        _captureFormatUiSync = true;
        try
        {
            SelectedStillCodecIndex = codec;
            OnPropertyChanged(nameof(CurrentStillFileFormatChoices));
            SelectedStillFileFormatIndex = fileFmt;
        }
        finally
        {
            _captureFormatUiSync = false;
        }
    }

    private static int ComposeCaptureFormatIndex(int codec, int fileFmt)
    {
        if (codec == 1)
        {
            return fileFmt switch
            {
                1 => 1,
                2 => 4,
                _ => 3,
            };
        }

        return fileFmt switch
        {
            1 => 1,
            2 => 2,
            _ => 0,
        };
    }


    /// <summary>用户更改文件格式后立即同步 <see cref="CrSdkDevicePropertyCodes.FileType"/>，避免仍按旧格式拍摄。</summary>
    private async Task PushCaptureFormatToCameraAsync()
    {
        if (_cameraOps.Session == null)
            return;
        try
        {
            await _cameraOps.Session
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
            SchedulePersistSettingsToDbDebounced();
        ScheduleSyncSaveSettingsToCameraDebounced();
    }

    partial void OnShowHistogramChanged(bool value)
    {
        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
        OnPropertyChanged(nameof(IsHistogramOverlayVisible));
    }

    partial void OnGuideOverlayIndexChanged(int value)
    {
        if (_persistEnabled)
            SchedulePersistSettingsToDbDebounced();
        OnPropertyChanged(nameof(IsGuideOverlayVisible));
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        DismissPreviewTouchFocusCommand.NotifyCanExecuteChanged();
        UpdateShootingPollForSession();
        OnPropertyChanged(nameof(ShutterActionButtonsEnabled));

        StartTimelapseCommand.NotifyCanExecuteChanged();
        PauseTimelapseCommand.NotifyCanExecuteChanged();
        ContinueTimelapseCommand.NotifyCanExecuteChanged();
        StopTimelapseCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTimelapseRunningChanged(bool value)
    {
        StartTimelapseCommand.NotifyCanExecuteChanged();
        PauseTimelapseCommand.NotifyCanExecuteChanged();
        ContinueTimelapseCommand.NotifyCanExecuteChanged();
        StopTimelapseCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTimelapsePausedChanged(bool value)
    {
        PauseTimelapseCommand.NotifyCanExecuteChanged();
        ContinueTimelapseCommand.NotifyCanExecuteChanged();
        StopTimelapseCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTimelapseStoppingChanged(bool value)
    {
        StartTimelapseCommand.NotifyCanExecuteChanged();
        PauseTimelapseCommand.NotifyCanExecuteChanged();
        ContinueTimelapseCommand.NotifyCanExecuteChanged();
        StopTimelapseCommand.NotifyCanExecuteChanged();
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
            TimelapseSaveDirectory = TimelapseSaveDirectory,
            TimelapseIntervalSeconds = TimelapseIntervalSeconds,
            TimelapseTargetFrames = TimelapseTargetFrames,
        };

    internal static CrSdkFileType IndexToFileType(int index) =>
        index switch
        {
            1 => CrSdkFileType.Raw,
            2 => CrSdkFileType.RawJpeg,
            3 => CrSdkFileType.Heif,
            4 => CrSdkFileType.RawHeif,
            _ => CrSdkFileType.Jpeg,
        };

    [RelayCommand]
    private void OpenDeviceSearch()
    {
        if (TryActivateExclusiveToolWindow("设备搜索"))
            return;

        var win = new DeviceSearchWindow();
        var vm = new DeviceSearchViewModel(this, () => win.Close(), _appLogService);
        win.DataContext = vm;
        RegisterExclusiveToolWindow(win, "设备搜索");
        if (_topLevelProvider.GetTopLevel() is Window owner)
            win.Show(owner);
        else
            win.Show();
    }

    private bool TryActivateExclusiveToolWindow(string requestedTitle)
    {
        lock (_exclusiveToolWindowGate)
        {
            if (_exclusiveToolWindow == null)
                return false;

            try
            {
                if (!_exclusiveToolWindow.IsVisible)
                {
                    _exclusiveToolWindow = null;
                    _exclusiveToolWindowTitle = null;
                    return false;
                }

                if (_exclusiveToolWindow.WindowState == WindowState.Minimized)
                    _exclusiveToolWindow.WindowState = WindowState.Normal;
                _exclusiveToolWindow.Activate();

                var opened = _exclusiveToolWindowTitle ?? "窗口";
                StatusMessage = $"已打开「{opened}」，不再重复打开「{requestedTitle}」。";
                return true;
            }
            catch
            {
                _exclusiveToolWindow = null;
                _exclusiveToolWindowTitle = null;
                return false;
            }
        }
    }

    private void RegisterExclusiveToolWindow(Window win, string title)
    {
        lock (_exclusiveToolWindowGate)
        {
            _exclusiveToolWindow = win;
            _exclusiveToolWindowTitle = title;
        }

        win.Closed += (_, _) =>
        {
            lock (_exclusiveToolWindowGate)
            {
                if (ReferenceEquals(_exclusiveToolWindow, win))
                {
                    _exclusiveToolWindow = null;
                    _exclusiveToolWindowTitle = null;
                }
            }
        };
    }

    /// <summary>由设备搜索窗口调用：连接用户在列表中选择的设备（CrSDK 枚举索引）。</summary>
    public Task<bool> ConnectToCameraAsync(int deviceIndex, bool isIp = false) =>
        _cameraOps.ConnectToCameraAsync(deviceIndex, isIp);

    internal async Task PrepareCameraShutdownInternalAsync()
    {
        await StopTimelapseInternalAsync().ConfigureAwait(true);
        await ReleaseAnyShutterHalfPressAsync().ConfigureAwait(true);
        StopShootingPoll();
    }

    [RelayCommand]
    private Task Disconnect() => _cameraOps.DisconnectAsync();

    /// <summary>
    /// 刷新“SD 卡容量/使用量估算”进度条数据（给「格式化 SD 卡」浮窗展示）。
    /// </summary>
    public Task RefreshSdCardUsageAsync(bool force = false) =>
        _cameraOps.RefreshSdCardUsageAsync(force);

    private bool CanFormatSdCard() => IsSessionActive && !_captureTaskActive && !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanFormatSdCard))]
    private void RequestFormatSdCardConfirm() => ShowFormatSdCardConfirm = true;

    [RelayCommand]
    private void CancelFormatSdCardConfirm() => ShowFormatSdCardConfirm = false;

    /// <summary>格式化相机存储卡（SD 卡）。按官方 CrCommandId_MediaFormat：SLOT1=Up，SLOT2=Down。</summary>
    [RelayCommand(CanExecute = nameof(CanFormatSdCard))]
    private Task ConfirmFormatSdCard() => _cameraOps.ConfirmFormatSdCardAsync();

    /// <summary>断开连接或切回实时取景时清空预览与对焦点标记（预览位图所有权在 <see cref="MainWindowCameraOperations"/>）。</summary>
    internal void ClearPreviewUi()
    {
        _cameraOps.ClearPreviewFrameOwnership();
        PreviewImage = null;
        StaticReviewImage = null;
        _staticReviewBitmap?.Dispose();
        _staticReviewBitmap = null;
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
        if (_cameraOps.Session == null || PreviewImage is not Bitmap bmp || !IsSessionActive)
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
            await _cameraOps.Session!.RequestTouchAutofocusAsync(nx, ny).ConfigureAwait(true);
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
        if (!IsSessionActive || _cameraOps.Session == null)
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
        ShutdownTrace.Write("MainWindowViewModel.DisposeAsync: begin");
        CancelFilmstripHostWidthUpdates();
        CancelRecentCaptureSyncAndSdRefresh();

        // 保持 UI 线程：关断路径会改 VM 属性并 NotifyCanExecuteChanged，否则会触发 Avalonia VerifyAccess。
        await _cameraOps.DisposeConnectingIfNeededAsync().ConfigureAwait(true);
        await _cameraOps.ShutdownCameraSessionAsync().ConfigureAwait(true);
        ShutdownTrace.Write("MainWindowViewModel.DisposeAsync: end");
    }

    private void CancelRecentCaptureSyncAndSdRefresh()
    {
        try
        {
            _recentCaptureSyncCts?.Cancel();
        }
        catch
        {
        }

        _recentCaptureSyncCts?.Dispose();
        _recentCaptureSyncCts = null;

        _cameraOps.CancelSdCardUsageRefresh();
    }
}
