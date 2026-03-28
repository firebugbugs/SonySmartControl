using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SonySmartControl.Helpers;
using SonySmartControl.Interop;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Services.Camera;

/// <summary>
/// 主窗口相机会话生命周期、预览帧、SD 卡与格式化等；快门/延时/拍摄轮询协调仍由 <see cref="MainWindowViewModel"/> 负责。
/// </summary>
public sealed class MainWindowCameraOperations
{
    private readonly ICameraPreviewSessionFactory _cameraPreviewSessionFactory;
    private readonly ISdCardMediaFormatService _sdCardMediaFormat;
    private readonly Func<MainWindowViewModel> _vmFactory;

    private ICameraPreviewSession? _session;
    private ICameraPreviewSession? _connectingSession;
    private CancellationTokenSource? _connectCts;
    private Bitmap? _lastFrameOwner;
    private bool _sdCardUsageRefreshInFlight;
    private CancellationTokenSource? _sdCardUsageRefreshCts;

    public MainWindowCameraOperations(
        ICameraPreviewSessionFactory cameraPreviewSessionFactory,
        ISdCardMediaFormatService sdCardMediaFormat,
        Func<MainWindowViewModel> vmFactory)
    {
        _cameraPreviewSessionFactory = cameraPreviewSessionFactory;
        _sdCardMediaFormat = sdCardMediaFormat;
        _vmFactory = vmFactory;
    }

    private MainWindowViewModel Vm => _vmFactory();

    public ICameraPreviewSession? Session => _session;

    public Bitmap? LastPreviewFrameOwner => _lastFrameOwner;

    public void CancelSdCardUsageRefresh()
    {
        try
        {
            _sdCardUsageRefreshCts?.Cancel();
        }
        catch
        {
        }

        _sdCardUsageRefreshCts?.Dispose();
        _sdCardUsageRefreshCts = null;
    }

    public async ValueTask DisposeConnectingIfNeededAsync()
    {
        _connectCts?.Cancel();
        try
        {
            _connectCts?.Dispose();
        }
        catch
        {
        }

        _connectCts = null;
        try
        {
            if (_connectingSession != null)
            {
                ShutdownTrace.Write("MainWindowCameraOperations.DisposeConnectingIfNeededAsync: disposing connecting session");
                var s = _connectingSession;
                _connectingSession = null;
                s.FrameReceived -= OnFrameReceived;
                await s.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            ShutdownTrace.Write("MainWindowCameraOperations.DisposeConnectingIfNeededAsync: exception swallowed");
        }
    }

    public async Task<bool> ConnectToCameraAsync(int deviceIndex, bool isIp = false)
    {
        if (_session != null || Vm.IsConnecting)
            return false;

        Vm.IsConnecting = true;
        Vm.StatusMessage = isIp
            ? "正在通过 IP 连接相机：请在相机端确认配对/连接提示（最长等待约 60 秒）…"
            : "正在连接相机，请稍候…";
        Vm.ConnectedCameraModelName = "未知";
        Vm.ConnectedCameraLensModelName = "暂未识别";
        Vm.ConnectedCameraBatteryLevelText = "暂未识别";
        Vm.SdCardUsageDebugText = "";

        var session = _cameraPreviewSessionFactory.Create();

        session.FrameReceived += OnFrameReceived;
        _connectCts = new CancellationTokenSource();
        _connectingSession = session;

        try
        {
            try
            {
                await session.ConnectAsync(deviceIndex, _connectCts.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                session.FrameReceived -= OnFrameReceived;
                if (_connectingSession == session)
                {
                    _connectingSession = null;
                    try
                    {
                        await session.DisposeAsync().ConfigureAwait(true);
                    }
                    catch
                    {
                    }
                }

                var debug = SonyCrBridgeNative.TryGetLastConnectDebugUtf8();
                if (!string.IsNullOrWhiteSpace(debug))
                    Vm.StatusMessage = (ex is OperationCanceledException ? "连接已取消。" : ex.Message) + $"（Connect调试：{debug}）";
                else
                    Vm.StatusMessage = ex is OperationCanceledException ? "连接已取消。" : ex.Message;
                return false;
            }

            _connectingSession = null;
            _session = session;
            Vm.IsSessionActive = true;
            Vm.ConnectedCameraModelName = NormalizeCameraModelName(session.ConnectedCameraModel);
            Vm.LiveViewSyncFromSession = true;
            Vm.LiveViewEnabled = true;
            Vm.LiveViewSyncFromSession = false;
            Vm.LiveViewEnabledControlEnabled = true;
            Vm.IsConnecting = false;

            try
            {
                Vm.UpdateShootingPollForSession();
            }
            catch (Exception ex)
            {
                Vm.StatusMessage = "连接后初始化失败: " + ex.Message;
                await ShutdownCameraSessionAsync().ConfigureAwait(true);
                return false;
            }

            try
            {
                await _session.ApplyCameraSaveSettingsAsync(
                        Vm.SaveDirectory,
                        Vm.FileNamePrefix,
                        MainWindowViewModel.IndexToFileType(Vm.CaptureFormatIndex))
                    .ConfigureAwait(true);
                Vm.StatusMessage = "已连接相机，并已应用保存目录与格式。";
            }
            catch (Exception ex)
            {
                Vm.StatusMessage = "已连接相机，但应用保存设置失败: " + ex.Message;
            }

            if (isIp)
            {
                var connDbg = SonyCrBridgeNative.TryGetLastConnectDebugUtf8();
                if (!string.IsNullOrWhiteSpace(connDbg) &&
                    (connDbg.Contains("pairNec=", StringComparison.Ordinal) || connDbg.Contains("pair", StringComparison.OrdinalIgnoreCase)))
                {
                    Vm.StatusMessage += "\n提示：若要后续免进「配对」页面直连，请在本次配对成功后将相机关机，等待约 10 秒再开机（让机身保存已配对主机信息）。";
                }
            }

            try
            {
                await _session.StartPreviewAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Vm.StatusMessage = "已连接相机，但启动实时预览失败: " + ex.Message;
                await ShutdownCameraSessionAsync().ConfigureAwait(true);
                return false;
            }

            _ = Task.Run(
                async () =>
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (!Vm.IsSessionActive || Vm.PreviewImage != null)
                        return;
                    var dbg = SonyCrBridgeNative.TryGetLastLiveViewDebugUtf8();
                    if (!string.IsNullOrWhiteSpace(dbg))
                    {
                        var likelyPcRemoteDisabled =
                            dbg.Contains("pk=33794", StringComparison.Ordinal)
                            || dbg.Contains("pk=0x8402", StringComparison.OrdinalIgnoreCase);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                            Vm.StatusMessage =
                                (likelyPcRemoteDisabled
                                    ? "已连接相机，但机身拒绝进入「PC Remote/遥控拍摄」控制状态（PriorityKey=PCRemote 被拒绝）。请在相机菜单里开启 PC Remote/远程拍摄（Wi‑Fi/IP），并完成首次配对授权后再试。"
                                    : "已连接相机，但 2 秒内未收到取景帧。多半是机身 LiveView 尚未启用/尚未就绪，请稍等或检查相机端「遥控拍摄/实时取景」相关设置。")
                                + $"（LiveView调试：{dbg}）");
                    }
                });

            return true;
        }
        finally
        {
            if (Vm.IsConnecting)
                Vm.IsConnecting = false;
            try
            {
                _connectCts?.Dispose();
            }
            catch
            {
            }

            _connectCts = null;
        }
    }

    public async Task ShutdownCameraSessionAsync()
    {
        ShutdownTrace.Write("MainWindowCameraOperations.ShutdownCameraSessionAsync: begin");
        await Vm.PrepareCameraShutdownInternalAsync().ConfigureAwait(true);

        if (_session == null)
        {
            ShutdownTrace.Write("MainWindowCameraOperations.ShutdownCameraSessionAsync: no session, end");
            return;
        }

        _session.FrameReceived -= OnFrameReceived;
        var sessionToDispose = _session;
        _session = null;
        ShutdownTrace.Write("MainWindowCameraOperations.ShutdownCameraSessionAsync: disposing session");

        try
        {
            await sessionToDispose.DisposeAsync().ConfigureAwait(false);
            ShutdownTrace.Write("MainWindowCameraOperations.ShutdownCameraSessionAsync: session disposed");
        }
        catch
        {
            ShutdownTrace.Write("MainWindowCameraOperations.ShutdownCameraSessionAsync: session dispose exception swallowed");
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Vm.ClearPreviewUi();
            Vm.IsSessionActive = false;
            Vm.ConnectedCameraModelName = "未知";
            Vm.ConnectedCameraLensModelName = "暂未识别";
            Vm.ConnectedCameraBatteryLevelText = "暂未识别";
            Vm.SdCardUsageDebugText = "";
        });
        ShutdownTrace.Write("MainWindowCameraOperations.ShutdownCameraSessionAsync: UI cleared, end");
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_session == null)
            {
                Vm.ClearPreviewUi();
                return;
            }

            await ShutdownCameraSessionAsync().ConfigureAwait(true);
            Vm.StatusMessage = "已断开。";
        }
        catch (Exception ex)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Vm.ClearPreviewUi();
                    Vm.IsSessionActive = false;
                    Vm.ConnectedCameraModelName = "未知";
                    Vm.ConnectedCameraLensModelName = "暂未识别";
                    Vm.ConnectedCameraBatteryLevelText = "暂未识别";
                    Vm.SdCardUsageDebugText = "";
                });
            }
            catch
            {
            }

            Vm.StatusMessage = "断开连接失败（已尽力清理本地状态）: " + ex.Message;
        }
    }

    public async Task RefreshSdCardUsageAsync(bool force = false)
    {
        if (!Vm.IsSessionActive || _session == null)
            return;

        if (_sdCardUsageRefreshInFlight && !force)
            return;

        _sdCardUsageRefreshInFlight = true;

        try
        {
            CancellationTokenSource? old = null;
            lock (this)
            {
                old = _sdCardUsageRefreshCts;
                _sdCardUsageRefreshCts = new CancellationTokenSource();
            }

            old?.Cancel();
            old?.Dispose();

            var token = _sdCardUsageRefreshCts?.Token ?? CancellationToken.None;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Vm.SdCardSlot1HasCard = false;
                Vm.SdCardSlot1SummaryText = "加载中…";
                Vm.SdCardSlot1UsagePercent = 0;

                Vm.SdCardSlot2HasCard = false;
                Vm.SdCardSlot2SummaryText = "加载中…";
                Vm.SdCardSlot2UsagePercent = 0;
                Vm.SdCardUsageDebugText = "诊断：读取中…";
            });

            var usage = await _session.TryGetSdCardUsageEstimateAsync(token).ConfigureAwait(false);
            var debugText = _session.TryGetSdCardUsageDebugText();
            if (!Vm.IsSessionActive)
                return;
            if (usage == null)
            {
                Vm.StatusMessage = "获取 SD 卡容量失败：桥接未返回结果（可能是旧版 DLL 或会话瞬断）。";
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Vm.SdCardUsageDebugText = string.IsNullOrWhiteSpace(debugText)
                        ? "诊断：桥接未返回调试文本（可能仍在使用旧版 DLL）。"
                        : $"诊断：{debugText}";
                });
                return;
            }

            static string FormatStorageHuman(ulong bytes)
            {
                const double kb = 1024d;
                const double mb = 1024d * 1024d;
                const double gb = 1024d * 1024d * 1024d;
                if (bytes >= (ulong)gb)
                    return $"{(bytes / gb):0.0}GB";
                if (bytes >= (ulong)mb)
                    return $"{(bytes / mb):0.0}MB";
                if (bytes >= (ulong)kb)
                    return $"{(bytes / kb):0.0}KB";
                return $"{bytes}B";
            }

            static double Clamp0to100(double v) => Math.Max(0, Math.Min(100, v));
            static int TryReadDiagInt(string? dbg, string key)
            {
                if (string.IsNullOrWhiteSpace(dbg) || string.IsNullOrWhiteSpace(key))
                    return int.MinValue;
                var marker = key + "=";
                var idx = dbg.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0)
                    return int.MinValue;
                idx += marker.Length;
                var end = dbg.IndexOf(' ', idx);
                var token = end >= idx ? dbg[idx..end] : dbg[idx..];
                return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                    ? v
                    : int.MinValue;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (usage.Value.Slot1HasCard)
                {
                    Vm.SdCardSlot1HasCard = true;
                    var slot1ContentsEnable = TryReadDiagInt(debugText, "slot1_contents_enable");
                    var slot1RemainingShots = TryReadDiagInt(debugText, "slot1_remaining");
                    var percent = usage.Value.Slot1TotalBytes == 0
                        ? 0
                        : Clamp0to100(usage.Value.Slot1UsedBytes * 100d / usage.Value.Slot1TotalBytes);
                    var slot1UsageUnavailable = slot1ContentsEnable == 0 && usage.Value.Slot1UsedBytes == 0;
                    var slot1HasRemainingShots = slot1RemainingShots >= 0;

                    Vm.SdCardSlot1UsagePercent = percent;
                    Vm.SdCardSlot1ProgressBrush = slot1UsageUnavailable
                        ? new SolidColorBrush(Color.Parse("#9CA3AF"))
                        : percent > 80
                            ? new SolidColorBrush(Color.Parse("#F59E0B"))
                            : new SolidColorBrush(Color.Parse("#2FBF71"));
                    Vm.SdCardSlot1SummaryText = slot1HasRemainingShots
                        ? $"容量 {FormatStorageHuman(usage.Value.Slot1TotalBytes)}，剩余约 {slot1RemainingShots} 张"
                        : $"容量 {FormatStorageHuman(usage.Value.Slot1TotalBytes)}，使用 {FormatStorageHuman(usage.Value.Slot1UsedBytes)} ({percent:0.0}%)";
                }
                else
                {
                    Vm.SdCardSlot1HasCard = false;
                    Vm.SdCardSlot1SummaryText = "未插卡";
                    Vm.SdCardSlot1UsagePercent = 0;
                }

                if (usage.Value.Slot2HasCard)
                {
                    Vm.SdCardSlot2HasCard = true;
                    var slot2ContentsEnable = TryReadDiagInt(debugText, "slot2_contents_enable");
                    var slot2RemainingShots = TryReadDiagInt(debugText, "slot2_remaining");
                    var percent = usage.Value.Slot2TotalBytes == 0
                        ? 0
                        : Clamp0to100(usage.Value.Slot2UsedBytes * 100d / usage.Value.Slot2TotalBytes);
                    var slot2UsageUnavailable = slot2ContentsEnable == 0 && usage.Value.Slot2UsedBytes == 0;
                    var slot2HasRemainingShots = slot2RemainingShots >= 0;

                    Vm.SdCardSlot2UsagePercent = percent;
                    Vm.SdCardSlot2ProgressBrush = slot2UsageUnavailable
                        ? new SolidColorBrush(Color.Parse("#9CA3AF"))
                        : percent > 80
                            ? new SolidColorBrush(Color.Parse("#F59E0B"))
                            : new SolidColorBrush(Color.Parse("#2FBF71"));
                    Vm.SdCardSlot2SummaryText = slot2HasRemainingShots
                        ? $"容量 {FormatStorageHuman(usage.Value.Slot2TotalBytes)}，剩余约 {slot2RemainingShots} 张"
                        : $"容量 {FormatStorageHuman(usage.Value.Slot2TotalBytes)}，使用 {FormatStorageHuman(usage.Value.Slot2UsedBytes)} ({percent:0.0}%)";
                }
                else
                {
                    Vm.SdCardSlot2HasCard = false;
                    Vm.SdCardSlot2SummaryText = "未插卡";
                    Vm.SdCardSlot2UsagePercent = 0;
                }

                Vm.SdCardUsageDebugText = string.IsNullOrWhiteSpace(debugText)
                    ? "诊断：桥接未返回调试文本（可能仍在使用旧版 DLL）。"
                    : $"诊断：{debugText}";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = "获取 SD 卡容量失败：" + ex.Message;
        }
        finally
        {
            _sdCardUsageRefreshInFlight = false;
        }
    }

    public async Task ConfirmFormatSdCardAsync()
    {
        if (_session == null)
            return;

        Vm.ShowFormatSdCardConfirm = false;
        Vm.StatusMessage = "正在格式化 SD 卡（SLOT1/2），请稍候…";
        try
        {
            var result = await _sdCardMediaFormat.FormatAllSlotsAsync().ConfigureAwait(true);
            Vm.StatusMessage = result.PartialErrorText == null
                ? "SD 卡格式化完成（如有需要请等待机身重建列表）。"
                : "SD 卡格式化完成，但部分槽失败：" + result.PartialErrorText;
        }
        catch (Exception ex)
        {
            Vm.StatusMessage = "格式化 SD 卡失败: " + ex.Message;
        }

        _ = RefreshSdCardUsageAsync(force: true);
    }

    private void OnFrameReceived(object? sender, Bitmap frame)
    {
        void Apply()
        {
            try
            {
                if (_session == null)
                {
                    frame.Dispose();
                    return;
                }

                _lastFrameOwner?.Dispose();
                _lastFrameOwner = frame;
                if (!Vm.IsViewingLiveMonitor)
                    return;

                Vm.PreviewImage = frame;
                Vm.LuminanceHistogramBins = HistogramLuminance.ComputeNormalized(frame) ?? new double[256];
                if (Vm.IsSessionActive && _session != null)
                {
                    var fj = _session.TryGetLiveViewFocusFramesJson();
                    if (CrSdkLiveViewFocusFrameParser.TryParse(fj, out var list))
                        Vm.SdkAfFocusFrames = list;
                    else
                        Vm.SdkAfFocusFrames = null;
                }
                else
                    Vm.SdkAfFocusFrames = null;
            }
            catch
            {
                try
                {
                    if (ReferenceEquals(Vm.PreviewImage, frame))
                        Vm.PreviewImage = null;
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

    public void ClearPreviewFrameOwnership()
    {
        _lastFrameOwner?.Dispose();
        _lastFrameOwner = null;
    }

    private static string NormalizeCameraModelName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "未知";
        var s = raw.Trim();
        var nul = s.IndexOf('\0');
        if (nul >= 0)
            s = s[..nul];
        Span<char> buffer = stackalloc char[s.Length];
        var n = 0;
        foreach (var ch in s)
        {
            if (!char.IsControl(ch))
                buffer[n++] = ch;
        }

        var normalized = new string(buffer[..n]).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? "未知" : normalized;
    }
}
