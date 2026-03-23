using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SonySmartControl.Interop;

namespace SonySmartControl.ViewModels;

public partial class MainWindowViewModel
{
    private DispatcherTimer? _shootingPollTimer;
    private bool _shootingSyncFromCamera;

    [ObservableProperty] private bool _isVideoShootingMode;

    [ObservableProperty] private double _shootingPanelOpacity = 1.0;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _exposureModeChoices = new();

    /// <summary>与拍摄侧栏 <c>WheelPickerCombo</c> 的 SelectedIndex 一致；-1 表示未选。</summary>
    [ObservableProperty] private int _selectedExposureModeIndex = -1;

    [ObservableProperty] private bool _exposureModeEnabled;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _apertureChoices = new();

    [ObservableProperty] private int _selectedApertureIndex = -1;

    [ObservableProperty] private bool _apertureEnabled;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _shutterChoices = new();

    [ObservableProperty] private int _selectedShutterIndex = -1;

    [ObservableProperty] private bool _shutterEnabled;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _shutterTypeChoices = new();

    [ObservableProperty] private int _selectedShutterTypeIndex = -1;

    [ObservableProperty] private bool _shutterTypeEnabled;

    private DateTime? _shootingPollSuppressStUtc;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _isoChoices = new();

    [ObservableProperty] private int _selectedIsoIndex = -1;

    [ObservableProperty] private bool _isoEnabled;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _exposureBiasChoices = new();

    [ObservableProperty] private int _selectedExposureBiasIndex = -1;

    [ObservableProperty] private bool _exposureBiasEnabled;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _focusModeChoices = new();

    [ObservableProperty] private int _selectedFocusModeIndex = -1;

    [ObservableProperty] private bool _focusModeEnabled;
    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _flashModeChoices = new();
    [ObservableProperty] private int _selectedFlashModeIndex = -1;
    [ObservableProperty] private bool _flashModeEnabled;
    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _flashCompensationChoices = new();
    [ObservableProperty] private int _selectedFlashCompensationIndex = -1;
    [ObservableProperty] private bool _flashCompensationEnabled;

    /// <summary>单张 / 连拍 / 延时自拍（与 <see cref="DriveModeCategoryLabels"/> 下标对应）。</summary>
    [ObservableProperty] private int _selectedDriveCategoryIndex = -1;

    /// <summary>连拍速度或延时秒数（候选来自机身）。</summary>
    [ObservableProperty] private int _selectedDriveSubIndex = -1;

    [ObservableProperty] private ObservableCollection<ShootingChoiceItem> _driveModeSubChoices = new();

    [ObservableProperty] private bool _driveModeCategoryEnabled;

    /// <summary>选中连拍或延时时显示第二行（连拍速度 / 延时时间）。</summary>
    [ObservableProperty] private bool _driveModeSubRowVisible;

    [ObservableProperty] private string _driveModeSubLabel = "连拍速度";

    /// <summary>机身为包围/间隔等时的提示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDriveModeOtherHint))]
    private string _driveModeOtherHint = "";

    public bool HasDriveModeOtherHint => !string.IsNullOrEmpty(DriveModeOtherHint);

    /// <summary>侧栏「拍摄方式」三项文案。</summary>
    public ObservableCollection<string> DriveModeCategoryLabels { get; } = new() { "单张", "连拍", "延时自拍" };

    /// <summary>曝光补偿不可调或数据缺失时的说明（绑定到界面提示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExposureCompensationHint))]
    private string _exposureCompensationHint = "";

    public bool HasExposureCompensationHint => !string.IsNullOrEmpty(ExposureCompensationHint);

    private CrSdkShootingState? _lastShootingState;

    /// <summary>用户从下拉/滚轮改某项后，短时间内不要用轮询结果覆盖该项选中。</summary>
    private DateTime? _shootingPollSuppressEpUtc;

    private DateTime? _shootingPollSuppressFnUtc;

    private DateTime? _shootingPollSuppressSsUtc;

    private DateTime? _shootingPollSuppressIsoUtc;

    private DateTime? _shootingPollSuppressEvUtc;

    private DateTime? _shootingPollSuppressFmUtc;

    private DateTime? _shootingPollSuppressDmUtc;

    private DateTime? _shootingPollSuppressDrvUtc;
    private DateTime? _shootingPollSuppressFlmUtc;
    private DateTime? _shootingPollSuppressFlcUtc;

    /// <summary>熄屏前一次的非 MonitorOff 模式，用于重新开屏时恢复。</summary>
    private byte? _lastNonOffDispMode;

    [ObservableProperty] private bool _cameraScreenPowerOn = true;

    [ObservableProperty] private bool _cameraScreenPowerEnabled;
    [ObservableProperty] private bool _liveViewEnabled = true;
    [ObservableProperty] private bool _liveViewEnabledControlEnabled;
    private bool _liveViewSyncFromSession;
    private bool _liveViewSwitching;

    private static readonly TimeSpan ShootingUserEditPollSuppressDuration = TimeSpan.FromMilliseconds(2000);

    private static bool ShouldSuppressPollBind(ref DateTime? suppressUntilUtc)
    {
        if (!suppressUntilUtc.HasValue)
            return false;
        if (DateTime.UtcNow < suppressUntilUtc.Value)
            return true;
        suppressUntilUtc = null;
        return false;
    }

    private void MarkUserShootingEdit(ref DateTime? suppressUntilUtc) =>
        suppressUntilUtc = DateTime.UtcNow.Add(ShootingUserEditPollSuppressDuration);

    private void EnsureShootingTimer()
    {
        if (_shootingPollTimer != null)
            return;
        _shootingPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _shootingPollTimer.Tick += OnShootingPollTick;
    }

    private void OnShootingPollTick(object? sender, EventArgs e) => ApplyShootingJsonFromSession();

    private void StopShootingPoll()
    {
        if (_shootingPollTimer != null)
        {
            _shootingPollTimer.Stop();
            _shootingPollTimer.Tick -= OnShootingPollTick;
            _shootingPollTimer = null;
        }

        ClearShootingUi();
    }

    private void UpdateShootingPollForSession()
    {
        if (IsSessionActive && _session != null)
        {
            EnsureShootingTimer();
            _shootingPollTimer!.Start();
            ApplyShootingJsonFromSession();
        }
        else
            StopShootingPoll();
    }

    private void ClearShootingUi()
    {
        _lastShootingState = null;
        _shootingPollSuppressEpUtc = null;
        _shootingPollSuppressFnUtc = null;
        _shootingPollSuppressSsUtc = null;
        _shootingPollSuppressIsoUtc = null;
        _shootingPollSuppressEvUtc = null;
        _shootingPollSuppressFmUtc = null;
        _shootingPollSuppressDmUtc = null;
        _shootingPollSuppressDrvUtc = null;
        _shootingPollSuppressFlmUtc = null;
        _shootingPollSuppressFlcUtc = null;
        _lastNonOffDispMode = null;
        CameraScreenPowerEnabled = false;
        LiveViewEnabledControlEnabled = false;
        _liveViewSyncFromSession = true;
        LiveViewEnabled = true;
        _liveViewSyncFromSession = false;
        IsVideoShootingMode = false;
        ShootingPanelOpacity = 1.0;
        ExposureModeEnabled = false;
        ApertureEnabled = false;
        ShutterEnabled = false;
        ShutterTypeEnabled = false;
        IsoEnabled = false;
        ExposureBiasEnabled = false;
        FocusModeEnabled = false;
        FlashModeEnabled = false;
        FlashCompensationEnabled = false;
        DriveModeCategoryEnabled = false;
        DriveModeSubRowVisible = false;
        DriveModeOtherHint = "";
        DriveModeSubChoices.Clear();
        _shootingSyncFromCamera = true;
        try
        {
            SelectedExposureModeIndex = -1;
            SelectedApertureIndex = -1;
            SelectedShutterIndex = -1;
            SelectedShutterTypeIndex = -1;
            SelectedIsoIndex = -1;
            SelectedExposureBiasIndex = -1;
            SelectedFocusModeIndex = -1;
            SelectedFlashModeIndex = -1;
            SelectedFlashCompensationIndex = -1;
            SelectedDriveCategoryIndex = -1;
            SelectedDriveSubIndex = -1;
            ExposureModeChoices.Clear();
            ApertureChoices.Clear();
            ShutterChoices.Clear();
            ShutterTypeChoices.Clear();
            IsoChoices.Clear();
            ExposureBiasChoices.Clear();
            FocusModeChoices.Clear();
            FlashModeChoices.Clear();
            FlashCompensationChoices.Clear();
        }
        finally
        {
            _shootingSyncFromCamera = false;
        }
        ExposureCompensationHint = "";
    }

    private void ApplyShootingJsonFromSession()
    {
        if (!IsSessionActive || _session == null)
            return;
        var json = _session.TryGetShootingStateJson();
        ApplyShootingJson(json);
    }

    private void ApplyShootingJson(string? json)
    {
        if (!CrSdkShootingState.TryParse(json, out var parsed) || parsed == null)
            return;

        var s = parsed;
        _lastShootingState = s;

        IsVideoShootingMode = s.IsVideoMode;
        ShootingPanelOpacity = s.IsVideoMode ? 0.5 : 1.0;

        var allow = IsSessionActive && !s.IsVideoMode;

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressEpUtc))
        {
            BindExposureProgramForStillPhoto(
                s.ExposureProgram,
                allow,
                out var epEnabled);
            ExposureModeEnabled = epEnabled;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressFnUtc))
        {
            BindProp(
                s.FNumber,
                ApertureChoices,
                v => CrSdkExposureFormatting.FormatFNumber((ushort)v),
                x => SelectedApertureIndex = x,
                () => SelectedApertureIndex,
                allow,
                true,
                out var apEn);
            ApertureEnabled = apEn;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressSsUtc))
        {
            BindProp(
                s.ShutterSpeed,
                ShutterChoices,
                v => CrSdkExposureFormatting.FormatShutterSpeed((uint)v),
                x => SelectedShutterIndex = x,
                () => SelectedShutterIndex,
                allow,
                true,
                out var shEn);
            ShutterEnabled = shEn;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressStUtc))
        {
            BindProp(
                s.ShutterType,
                ShutterTypeChoices,
                CrSdkExposureFormatting.FormatShutterType,
                x => SelectedShutterTypeIndex = x,
                () => SelectedShutterTypeIndex,
                allow,
                true,
                out var stEn);
            ShutterTypeEnabled = stEn;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressIsoUtc))
        {
            BindProp(
                s.Iso,
                IsoChoices,
                v => CrSdkExposureFormatting.FormatIso((uint)v),
                x => SelectedIsoIndex = x,
                () => SelectedIsoIndex,
                allow,
                true,
                out var isoEn);
            IsoEnabled = isoEn;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressEvUtc))
        {
            BindProp(
                s.ExposureBias,
                ExposureBiasChoices,
                CrSdkExposureFormatting.FormatExposureBias,
                x => SelectedExposureBiasIndex = x,
                () => SelectedExposureBiasIndex,
                allow,
                true,
                out var evEn);
            ExposureBiasEnabled = evEn;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressFmUtc))
            ApplyFocusModeFromState(s, allow);

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressDrvUtc))
            ApplyDriveModeFromState(s, allow);

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressFlmUtc))
        {
            BindProp(
                s.FlashMode,
                FlashModeChoices,
                CrSdkExposureFormatting.FormatFlashMode,
                x => SelectedFlashModeIndex = x,
                () => SelectedFlashModeIndex,
                allow,
                true,
                out var flmEn);
            FlashModeEnabled = flmEn;
        }

        if (!ShouldSuppressPollBind(ref _shootingPollSuppressFlcUtc))
        {
            BindProp(
                s.FlashCompensation,
                FlashCompensationChoices,
                CrSdkExposureFormatting.FormatFlashCompensation,
                x => SelectedFlashCompensationIndex = x,
                () => SelectedFlashCompensationIndex,
                allow,
                true,
                out var flcEn);
            FlashCompensationEnabled = flcEn;
        }

        ApplyCameraScreenFromState(s, allow);

        UpdateExposureCompensationHint(s);
    }

    private void ApplyCameraScreenFromState(CrSdkShootingState s, bool allowStill)
    {
        if (ShouldSuppressPollBind(ref _shootingPollSuppressDmUtc))
            return;

        if (s.DispMode != null)
        {
            var v = (byte)(s.DispMode.Value & 0xFF);
            if (v != (byte)CrSdkDispMode.MonitorOff)
                _lastNonOffDispMode = v;

            _shootingSyncFromCamera = true;
            try
            {
                CameraScreenPowerOn = v != (byte)CrSdkDispMode.MonitorOff;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            // 部分机身 Writable 误报为 false，仍允许操作；失败时由 Set 抛错提示。
            CameraScreenPowerEnabled = allowStill;
        }
        else
            CameraScreenPowerEnabled = false;
    }

    partial void OnCameraScreenPowerOnChanged(bool value)
    {
        if (_shootingSyncFromCamera || !IsSessionActive || _lastShootingState?.DispMode == null)
            return;

        MarkUserShootingEdit(ref _shootingPollSuppressDmUtc);
        try
        {
            byte mode;
            if (value)
            {
                mode = _lastNonOffDispMode ?? (byte)CrSdkDispMode.DisplayAllInfo;
            }
            else
            {
                if (_lastShootingState.DispMode != null)
                {
                    var cur = (byte)(_lastShootingState.DispMode.Value & 0xFF);
                    if (cur != (byte)CrSdkDispMode.MonitorOff)
                        _lastNonOffDispMode = cur;
                }

                mode = (byte)CrSdkDispMode.MonitorOff;
            }

            SonyCrSdk.ApplyMonitorDispMode(mode);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnLiveViewEnabledChanged(bool value)
    {
        if (_liveViewSyncFromSession || !IsSessionActive || _session == null || _liveViewSwitching)
            return;

        _ = ApplyLiveViewEnabledAsync(value);
    }

    private async Task ApplyLiveViewEnabledAsync(bool enabled)
    {
        if (_session == null)
            return;

        _liveViewSwitching = true;
        LiveViewEnabledControlEnabled = false;
        try
        {
            await _session.SetLiveViewEnabledAsync(enabled).ConfigureAwait(true);
            if (!enabled)
            {
                PreviewImage = null;
                LuminanceHistogramBins = null;
                SdkAfFocusFrames = null;
                StatusMessage = "LiveView 已关闭：相机进入静默待机（取流停止，拍照仍可用）。";
            }
            else
            {
                StatusMessage = "LiveView 已开启。";
            }
        }
        catch (Exception ex)
        {
            _liveViewSyncFromSession = true;
            LiveViewEnabled = !enabled;
            _liveViewSyncFromSession = false;
            StatusMessage = "切换 LiveView 失败: " + ex.Message;
        }
        finally
        {
            LiveViewEnabledControlEnabled = IsSessionActive;
            _liveViewSwitching = false;
        }
    }

    private void UpdateExposureCompensationHint(CrSdkShootingState s)
    {
        if (!IsSessionActive)
        {
            ExposureCompensationHint = "";
            return;
        }

        if (s.ExposureBias == null)
        {
            ExposureCompensationHint =
                "未读取到曝光补偿（JSON 无 ev）。请重新编译并部署含曝光补偿的 SonyCrBridge.dll。";
            return;
        }

        if (s.IsVideoMode)
        {
            ExposureCompensationHint = "摄像/视频相关模式下曝光项以机身为准。";
            return;
        }

        if (!s.ExposureBias.Writable)
        {
            var ep = (uint)(s.ExposureProgram?.Value ?? 0);
            // CrExposure_M_Manual = 0x00000001（与 CrDeviceProperty.h 一致）
            if (ep == 0x00000001)
            {
                ExposureCompensationHint =
                    "M 挡（全手动）下机身通常锁定曝光补偿：曝光由光圈、快门、ISO 直接决定。若需用 EV，请改到 P / A / S 等模式（部分机型在 M+自动 ISO 时也可调，以机身为准）。";
            }
            else
            {
                ExposureCompensationHint =
                    "机身当前不允许改写曝光补偿（可能受模式、菜单或锁定影响）。";
            }

            return;
        }

        ExposureCompensationHint = "";
    }

    private static ulong NormalizeFocusModeRaw(ulong raw) =>
        (ulong)(ushort)(raw & 0xFFFF);

    /// <summary>机身未返回候选值时使用的对焦模式列表（与 CrFocusMode 一致）。</summary>
    private static readonly ulong[] DefaultFocusModeOrder =
    [
        0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0007,
    ];

    /// <summary>
    /// 对焦模式：不单独依赖 Writable（部分机身误报禁写）；候选为空时用默认列表并合并当前值；数值按 UInt16 对齐。
    /// </summary>
    private void ApplyFocusModeFromState(CrSdkShootingState s, bool allow)
    {
        var snap = s.FocusMode;
        if (snap == null)
        {
            if (FocusModeChoices.Count > 0)
                FocusModeChoices.Clear();
            _shootingSyncFromCamera = true;
            try
            {
                SelectedFocusModeIndex = -1;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            FocusModeEnabled = false;
            return;
        }

        var current = NormalizeFocusModeRaw(snap.Value);
        var built = new List<ulong>(16);
        if (snap.Candidates.Length > 0)
        {
            foreach (var c in snap.Candidates)
            {
                var n = NormalizeFocusModeRaw(c);
                if (!built.Contains(n))
                    built.Add(n);
            }
        }
        else
        {
            foreach (var v in DefaultFocusModeOrder)
                built.Add(v);
        }

        if (!built.Contains(current))
            built.Add(current);

        var rebuild = FocusModeChoices.Count != built.Count;
        if (!rebuild)
        {
            for (var i = 0; i < built.Count; i++)
            {
                if (i >= FocusModeChoices.Count || FocusModeChoices[i].Value != built[i])
                {
                    rebuild = true;
                    break;
                }
            }
        }

        if (rebuild)
        {
            FocusModeChoices.Clear();
            foreach (var v in built)
                FocusModeChoices.Add(new ShootingChoiceItem(CrSdkExposureFormatting.FormatFocusMode(v), v));
        }

        var targetIndex = built.IndexOf(current);
        if (targetIndex < 0)
        {
            _shootingSyncFromCamera = true;
            try
            {
                SelectedFocusModeIndex = -1;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            FocusModeEnabled = false;
            return;
        }

        if (SelectedFocusModeIndex != targetIndex)
        {
            _shootingSyncFromCamera = true;
            try
            {
                SelectedFocusModeIndex = targetIndex;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }
        }

        FocusModeEnabled = allow && built.Count > 0;
    }

    private void ApplyDriveModeFromState(CrSdkShootingState s, bool allowStill)
    {
        var snap = s.DriveMode;
        if (snap == null)
        {
            DriveModeCategoryEnabled = false;
            DriveModeSubRowVisible = false;
            DriveModeOtherHint = "";
            if (DriveModeSubChoices.Count > 0)
                DriveModeSubChoices.Clear();
            _shootingSyncFromCamera = true;
            try
            {
                SelectedDriveCategoryIndex = -1;
                SelectedDriveSubIndex = -1;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            return;
        }

        DriveModeCategoryEnabled = allowStill && snap.Writable && snap.Candidates.Length > 0;
        var v = snap.Value;
        var kind = CrSdkShootingDriveMode.Classify(v);
        _shootingSyncFromCamera = true;
        try
        {
            if (kind == CrSdkShootingDriveCategoryKind.Other)
            {
                SelectedDriveCategoryIndex = -1;
                SelectedDriveSubIndex = -1;
                DriveModeSubRowVisible = false;
                DriveModeOtherHint =
                    "机身当前：" + CrSdkShootingDriveMode.FormatDriveMode(v)
                    + "（包围/对焦包围等请在机身调整，或改下列拍摄方式）";
                DriveModeSubChoices.Clear();
                return;
            }

            DriveModeOtherHint = "";
            SelectedDriveCategoryIndex = kind switch
            {
                CrSdkShootingDriveCategoryKind.Single => 0,
                CrSdkShootingDriveCategoryKind.Burst => 1,
                CrSdkShootingDriveCategoryKind.SelfTimer => 2,
                _ => -1,
            };

            if (kind == CrSdkShootingDriveCategoryKind.Single)
            {
                DriveModeSubRowVisible = false;
                DriveModeSubChoices.Clear();
                SelectedDriveSubIndex = -1;
                return;
            }

            DriveModeSubLabel = kind == CrSdkShootingDriveCategoryKind.Burst ? "连拍速度" : "延时时间";
            FillDriveModeSubChoices(kind, snap);
            DriveModeSubRowVisible = DriveModeSubChoices.Count > 0;

            var idx = -1;
            for (var i = 0; i < DriveModeSubChoices.Count; i++)
            {
                if (DriveModeSubChoices[i].Value != v)
                    continue;
                idx = i;
                break;
            }

            SelectedDriveSubIndex = idx >= 0 ? idx : (DriveModeSubChoices.Count > 0 ? 0 : -1);
        }
        finally
        {
            _shootingSyncFromCamera = false;
        }
    }

    private void FillDriveModeSubChoices(CrSdkShootingDriveCategoryKind kind, CrSdkShootingPropertySnapshot snap)
    {
        DriveModeSubChoices.Clear();
        foreach (var raw in CrSdkShootingDriveMode.FilterCandidates(snap.Candidates, kind))
            DriveModeSubChoices.Add(new ShootingChoiceItem(CrSdkShootingDriveMode.FormatDriveMode(raw), raw));
    }

    private void TrySetDriveModeValue(ulong raw)
    {
        var snap = _lastShootingState?.DriveMode;
        if (snap == null)
            return;
        try
        {
            var dt = snap.SetDataType != CrSdkDataType.Undefined ? snap.SetDataType : CrSdkDataType.UInt32Array;
            SonyCrSdk.SetShootingProperty(CrSdkDevicePropertyCodes.DriveMode, raw, dt);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedDriveCategoryIndexChanged(int value)
    {
        if (_shootingSyncFromCamera)
            return;
        if (value < 0 || value > 2)
            return;
        var snap = _lastShootingState?.DriveMode;
        if (snap == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressDrvUtc);
        var kind = value switch
        {
            0 => CrSdkShootingDriveCategoryKind.Single,
            1 => CrSdkShootingDriveCategoryKind.Burst,
            2 => CrSdkShootingDriveCategoryKind.SelfTimer,
            _ => CrSdkShootingDriveCategoryKind.Single
        };
        if (kind == CrSdkShootingDriveCategoryKind.Single)
        {
            DriveModeSubRowVisible = false;
            DriveModeSubChoices.Clear();
            _shootingSyncFromCamera = true;
            try
            {
                SelectedDriveSubIndex = -1;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            if (snap.Candidates.Contains(CrSdkShootingDriveMode.CrDriveSingle))
                TrySetDriveModeValue(CrSdkShootingDriveMode.CrDriveSingle);
            else
                StatusMessage = "机身未返回单张（CrDrive_Single）候选。";
            return;
        }

        DriveModeSubLabel = kind == CrSdkShootingDriveCategoryKind.Burst ? "连拍速度" : "延时时间";
        FillDriveModeSubChoices(kind, snap);
        DriveModeSubRowVisible = DriveModeSubChoices.Count > 0;
        if (DriveModeSubChoices.Count == 0)
        {
            StatusMessage = "机身未提供可用的连拍或延时候选。";
            return;
        }

        _shootingSyncFromCamera = true;
        try
        {
            SelectedDriveSubIndex = 0;
        }
        finally
        {
            _shootingSyncFromCamera = false;
        }

        TrySetDriveModeValue(DriveModeSubChoices[0].Value);
    }

    partial void OnSelectedDriveSubIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= DriveModeSubChoices.Count)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressDrvUtc);
        TrySetDriveModeValue(DriveModeSubChoices[value].Value);
    }

    /// <summary>曝光模式：去掉视频/摄像项与未知 <c>0x……</c>，避免下拉过长且难懂。</summary>
    private void BindExposureProgramForStillPhoto(
        CrSdkShootingPropertySnapshot? snap,
        bool allowPanel,
        out bool enabled)
    {
        enabled = false;
        if (snap == null)
        {
            if (ExposureModeChoices.Count > 0)
                ExposureModeChoices.Clear();
            _shootingSyncFromCamera = true;
            try
            {
                SelectedExposureModeIndex = -1;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            return;
        }

        const bool requireGettable = true;
        enabled = allowPanel && snap.Writable && (!requireGettable || snap.Gettable);

        static bool Keep(ulong v) => CrSdkExposureFormatting.IsStillPhotoExposureProgramListItem((uint)v);

        var filtered = snap.Candidates.Where(Keep).ToArray();

        var rebuild = ExposureModeChoices.Count != filtered.Length;
        if (!rebuild)
        {
            for (var i = 0; i < filtered.Length; i++)
            {
                if (i >= ExposureModeChoices.Count || ExposureModeChoices[i].Value != filtered[i])
                {
                    rebuild = true;
                    break;
                }
            }
        }

        if (rebuild)
        {
            ExposureModeChoices.Clear();
            foreach (var v in filtered)
                ExposureModeChoices.Add(
                    new ShootingChoiceItem(CrSdkExposureFormatting.FormatExposureProgram((uint)v), v));
        }

        ShootingChoiceItem? match = ExposureModeChoices.FirstOrDefault(x => x.Value == snap.Value);
        if (match == null && Keep(snap.Value))
            match = new ShootingChoiceItem(CrSdkExposureFormatting.FormatExposureProgram((uint)snap.Value), snap.Value);

        if (match != null && ExposureModeChoices.All(x => x.Value != match.Value))
            ExposureModeChoices.Add(match);

        var targetIndex = -1;
        for (var i = 0; i < ExposureModeChoices.Count; i++)
        {
            if (ExposureModeChoices[i].Value != match?.Value)
                continue;
            targetIndex = i;
            break;
        }

        if (targetIndex < 0)
        {
            _shootingSyncFromCamera = true;
            try
            {
                SelectedExposureModeIndex = -1;
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            return;
        }

        if (SelectedExposureModeIndex == targetIndex)
            return;

        _shootingSyncFromCamera = true;
        try
        {
            SelectedExposureModeIndex = targetIndex;
        }
        finally
        {
            _shootingSyncFromCamera = false;
        }
    }

    private void BindProp(
        CrSdkShootingPropertySnapshot? snap,
        ObservableCollection<ShootingChoiceItem> list,
        Func<ulong, string> format,
        Action<int> setIndex,
        Func<int> getIndex,
        bool allowPanel,
        bool requireGettable,
        out bool enabled)
    {
        enabled = false;
        if (snap == null)
        {
            if (list.Count > 0)
                list.Clear();
            _shootingSyncFromCamera = true;
            try
            {
                setIndex(-1);
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            return;
        }

        enabled = allowPanel && snap.Writable && (!requireGettable || snap.Gettable);

        var rebuild = list.Count != snap.Candidates.Length;
        if (!rebuild)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Value != snap.Candidates[i])
                {
                    rebuild = true;
                    break;
                }
            }
        }

        if (rebuild)
        {
            list.Clear();
            foreach (var v in snap.Candidates)
                list.Add(new ShootingChoiceItem(format(v), v));
        }

        var match = list.FirstOrDefault(x => x.Value == snap.Value)
                    ?? new ShootingChoiceItem(format(snap.Value), snap.Value);

        if (list.All(x => x.Value != match.Value))
            list.Add(match);

        var targetIndex = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Value != match.Value)
                continue;
            targetIndex = i;
            break;
        }

        if (targetIndex < 0)
        {
            _shootingSyncFromCamera = true;
            try
            {
                setIndex(-1);
            }
            finally
            {
                _shootingSyncFromCamera = false;
            }

            return;
        }

        if (getIndex() == targetIndex)
            return;

        _shootingSyncFromCamera = true;
        try
        {
            setIndex(targetIndex);
        }
        finally
        {
            _shootingSyncFromCamera = false;
        }
    }

    partial void OnSelectedExposureModeIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= ExposureModeChoices.Count || _lastShootingState?.ExposureProgram == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressEpUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.ExposureProgramMode,
                ExposureModeChoices[value].Value,
                _lastShootingState.ExposureProgram.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedApertureIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= ApertureChoices.Count || _lastShootingState?.FNumber == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressFnUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.FNumber,
                ApertureChoices[value].Value,
                _lastShootingState.FNumber.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedShutterIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= ShutterChoices.Count || _lastShootingState?.ShutterSpeed == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressSsUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.ShutterSpeed,
                ShutterChoices[value].Value,
                _lastShootingState.ShutterSpeed.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedShutterTypeIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= ShutterTypeChoices.Count || _lastShootingState?.ShutterType == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressStUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.ShutterType,
                ShutterTypeChoices[value].Value,
                _lastShootingState.ShutterType.SetDataType != CrSdkDataType.Undefined
                    ? _lastShootingState.ShutterType.SetDataType
                    : CrSdkDataType.UInt8Array);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedIsoIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= IsoChoices.Count || _lastShootingState?.Iso == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressIsoUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.IsoSensitivity,
                IsoChoices[value].Value,
                _lastShootingState.Iso.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedExposureBiasIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= ExposureBiasChoices.Count || _lastShootingState?.ExposureBias == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressEvUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.ExposureBiasCompensation,
                ExposureBiasChoices[value].Value,
                _lastShootingState.ExposureBias.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedFocusModeIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= FocusModeChoices.Count || _lastShootingState?.FocusMode == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressFmUtc);
        try
        {
            var dt = _lastShootingState.FocusMode.SetDataType;
            if (dt == CrSdkDataType.Undefined)
                dt = CrSdkDataType.UInt16Array;
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.FocusMode,
                NormalizeFocusModeRaw(FocusModeChoices[value].Value),
                dt);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedFlashModeIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= FlashModeChoices.Count || _lastShootingState?.FlashMode == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressFlmUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.FlashMode,
                FlashModeChoices[value].Value,
                _lastShootingState.FlashMode.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    partial void OnSelectedFlashCompensationIndexChanged(int value)
    {
        if (_shootingSyncFromCamera || value < 0 || value >= FlashCompensationChoices.Count || _lastShootingState?.FlashCompensation == null)
            return;
        MarkUserShootingEdit(ref _shootingPollSuppressFlcUtc);
        try
        {
            SonyCrSdk.SetShootingProperty(
                CrSdkDevicePropertyCodes.FlashCompensation,
                FlashCompensationChoices[value].Value,
                _lastShootingState.FlashCompensation.SetDataType);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
