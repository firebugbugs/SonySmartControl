using System.IO;
using Avalonia.Media.Imaging;
using SonySmartControl.Interop;

namespace SonySmartControl.Services.Camera;

/// <summary>
/// 通过 <c>SonyCrBridge.dll</c> 连接相机并循环拉取 Live View JPEG，解码为 <see cref="Bitmap"/>。
/// 需先将桥接 DLL 与 Cr_Core / CrAdapter 复制到输出目录，并用 CMake 以 <c>SONY_CR_BRIDGE_STUB=OFF</c> 链接真实 CrSDK。
/// </summary>
public sealed class CrSdkCameraPreviewSession : ICameraPreviewSession
{
    private const int InitialJpegBufferBytes = 16 * 1024 * 1024;

    private readonly object _gate = new();
    private bool _sdkConnected;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    public string? ConnectedCameraModel { get; private set; }
    private bool _liveViewEnabled = true;

    public event EventHandler<Bitmap>? FrameReceived;

    public Task ConnectAsync(int deviceIndex = 0, CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_sdkConnected)
                        return;

                    try
                    {
                        var initOk = false;
                        var st = SonyCrBridgeNative.SonyCr_Init();
                        if (st != (int)SonyCrStatus.Ok)
                            throw CrEx(st, "SonyCr_Init");
                        initOk = true;

                        st = SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh();
                        if (st != (int)SonyCrStatus.Ok)
                        {
                            throw CrEx(st, "SonyCr_EnumCameraDevicesRefresh");
                        }

                        st = SonyCrBridgeNative.SonyCr_GetCameraDeviceCount(out var count);
                        if (st != (int)SonyCrStatus.Ok)
                            throw CrEx(st, "SonyCr_GetCameraDeviceCount");

                        if (count < 1)
                            throw new InvalidOperationException("未检测到相机：请 USB/网线连接并设为「遥控拍摄」后重试。");

                        if (deviceIndex < 0 || deviceIndex >= count)
                            throw new ArgumentOutOfRangeException(nameof(deviceIndex), "所选设备索引无效，请重新搜索后连接。");

                        ConnectedCameraModel = SonyCrBridgeNative.GetCameraModelUtf8(deviceIndex);

                        st = SonyCrBridgeNative.SonyCr_ConnectRemoteByIndex(deviceIndex);
                        if (st != (int)SonyCrStatus.Ok)
                            throw CrEx(st, $"SonyCr_ConnectRemoteByIndex({deviceIndex})");

                        _sdkConnected = true;
                    }
                    catch (DllNotFoundException ex)
                    {
                        var baseDir = AppContext.BaseDirectory;
                        var hint = $" 运行时目录: {baseDir}（请确认 SonyCrBridge.dll、Cr_Core.dll 与此 exe 同目录。）";
                        throw new InvalidOperationException(
                            "缺少 SonyCrBridge.dll、其依赖 Cr_Core.dll，或 VC++ 运行库。官方 ZIP 里没有桥接 DLL，必须用 C++ 编译生成："
                            + " 在资源管理器中打开项目下的 native\\SonyCrBridge，用 PowerShell 执行 .\\build-windows.ps1。"
                            + " 成功后重新执行「生成」SonySmartControl（不要只运行旧输出）。"
                            + " 另请安装 x64 的 VC++ 可再发行组件：https://aka.ms/vs/17/release/vc_redist.x64.exe 。"
                            + " 详细: " + ex.Message + hint,
                            ex);
                    }
                }
            },
            cancellationToken);

    public Task StartPreviewAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_loopTask != null)
                return Task.CompletedTask;
            if (!_sdkConnected)
                throw new InvalidOperationException("未连接相机，无法启动预览。");
            if (!_liveViewEnabled)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _loopTask = Task.Run(() => LiveViewLoopAsync(token), token);
        }

        return Task.CompletedTask;
    }

    public async Task SetLiveViewEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Task? loopToWait = null;
        lock (_gate)
        {
            if (!_sdkConnected)
                throw new InvalidOperationException("未连接相机，无法设置 LiveView。");
            if (_liveViewEnabled == enabled && (!enabled || _loopTask != null))
                return;
            _liveViewEnabled = enabled;
            if (!enabled)
            {
                loopToWait = _loopTask;
                _loopTask = null;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
        {
            if (loopToWait != null)
            {
                try
                {
                    await loopToWait.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            // 关闭传感器取流，进入静默待机（相机端停流，不只是 PC 端停拉帧）。
            SonyCrSdk.EnsurePriorityKeyPcRemote();
            SonyCrSdk.SetDeviceSetting(CrSdkSettingKey.EnableLiveView, 0);

            // 关流后桥接/SDK 可能残留 1 帧缓存；主动冲刷，避免后续探测把缓存误判为“仍在取流”。
            DrainPendingLiveViewFrames();
            return;
        }

        SonyCrSdk.EnsurePriorityKeyPcRemote();
        SonyCrSdk.SetDeviceSetting(CrSdkSettingKey.EnableLiveView, 1);
        await StartPreviewAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<LiveViewProbeResult> ProbeLiveViewDisabledStateAsync(
        int probeCount = 6,
        int probeIntervalMs = 250,
        CancellationToken cancellationToken = default)
    {
        probeCount = Math.Clamp(probeCount, 1, 30);
        probeIntervalMs = Math.Clamp(probeIntervalMs, 50, 2000);

        lock (_gate)
        {
            if (!_sdkConnected)
                throw new InvalidOperationException("未连接相机，无法探测 LiveView 状态。");
            if (_liveViewEnabled)
                throw new InvalidOperationException("LiveView 仍处于开启状态，无法进行停流探测。");
            if (_loopTask != null)
                throw new InvalidOperationException("预览循环仍在运行，无法进行停流探测。");
        }

        var hits = 0;
        var buffer = new byte[InitialJpegBufferBytes];

        // 预热读取并丢弃 1 次，规避关流后偶发缓存首帧。
        TryPullOneLiveViewFrame(ref buffer, out _);

        for (var i = 0; i < probeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var st = TryPullOneLiveViewFrame(ref buffer, out var written);

            if (st == (int)SonyCrStatus.Ok && written > 0)
                hits++;

            if (i < probeCount - 1)
                await Task.Delay(probeIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        return new LiveViewProbeResult(probeCount, hits);
    }

    public bool TryGetTransportStats(out ulong uploadBytes, out ulong downloadBytes)
    {
        uploadBytes = 0;
        downloadBytes = 0;
        lock (_gate)
        {
            if (!_sdkConnected)
                return false;
        }

        try
        {
            var st = SonyCrBridgeNative.SonyCr_GetTransportStats(out uploadBytes, out downloadBytes);
            return st == (int)SonyCrStatus.Ok;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public Task<SdCardUsageEstimate?> TryGetSdCardUsageEstimateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<SdCardUsageEstimate?>(
            () =>
            {
                ulong slot1Total = 0;
                ulong slot1Used = 0;
                int slot1HasCardInt = 0;
                ulong slot2Total = 0;
                ulong slot2Used = 0;
                int slot2HasCardInt = 0;

                lock (_gate)
                {
                    if (!_sdkConnected)
                        return null;
                }

                try
                {
                    var st = SonyCrBridgeNative.SonyCr_GetSdCardUsageEstimate(
                        out slot1Total,
                        out slot1Used,
                        out slot1HasCardInt,
                        out slot2Total,
                        out slot2Used,
                        out slot2HasCardInt);

                    if (st != (int)SonyCrStatus.Ok)
                        throw new InvalidOperationException(
                            $"SonyCr_GetSdCardUsageEstimate 失败: {(SonyCrStatus)st} ({st})");

                    return new SdCardUsageEstimate(
                        slot1HasCardInt != 0,
                        slot1Total,
                        slot1Used,
                        slot2HasCardInt != 0,
                        slot2Total,
                        slot2Used);
                }
                catch (EntryPointNotFoundException)
                {
                    return null;
                }
                catch (DllNotFoundException)
                {
                    return null;
                }
            },
            cancellationToken);
    }

    public string? TryGetSdCardUsageDebugText()
    {
        lock (_gate)
        {
            if (!_sdkConnected)
                return null;
        }

        return SonyCrBridgeNative.TryGetLastSdUsageDebugUtf8();
    }

    private static void DrainPendingLiveViewFrames()
    {
        var buffer = new byte[InitialJpegBufferBytes];
        for (var i = 0; i < 4; i++)
        {
            var st = TryPullOneLiveViewFrame(ref buffer, out var written);
            if (st != (int)SonyCrStatus.Ok || written <= 0)
                break;
        }
    }

    private static int TryPullOneLiveViewFrame(ref byte[] buffer, out int written)
    {
        var st = SonyCrBridgeNative.SonyCr_LiveView_GetLastJpeg(buffer, buffer.Length, out written);
        if (st != (int)SonyCrStatus.ErrBufferTooSmall)
            return st;

        buffer = new byte[Math.Min(buffer.Length * 2, 64 * 1024 * 1024)];
        return SonyCrBridgeNative.SonyCr_LiveView_GetLastJpeg(buffer, buffer.Length, out written);
    }

    public async Task DisconnectAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loopTask;
            _loopTask = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        if (loop != null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        SonyCrBridgeNative.TryDisconnect();
        lock (_gate)
        {
            _sdkConnected = false;
            ConnectedCameraModel = null;
            _liveViewEnabled = true;
        }
    }

    public Task RequestTouchAutofocusAsync(double normalizedX, double normalizedY, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.RemoteTouchAfFromNormalized(normalizedX, normalizedY);
            },
            cancellationToken);
    }

    public Task CancelRemoteTouchOperationAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.CancelRemoteTouchOperation();
            },
            cancellationToken);

    public Task ApplyCameraSaveSettingsAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType fileType,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(saveDirectory))
                    Directory.CreateDirectory(saveDirectory.Trim());
                SonyCrSdk.ApplyCaptureSaveSettings(saveDirectory, filePrefix, fileType);
            },
            cancellationToken);
    }

    public Task OneShotFocusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.HalfPressShutterForAutofocusOnly();
            },
            cancellationToken);

    public Task BeginHalfPressFocusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.HalfPressShutterS1Press();
            },
            cancellationToken);

    public Task EndHalfPressFocusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.HalfPressShutterS1Release();
            },
            cancellationToken);

    public Task CaptureStillAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType stillFileType,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.CaptureStill(saveDirectory, filePrefix, stillFileType);
            },
            cancellationToken);

    public Task CaptureStillReleaseAfterHalfPressAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType stillFileType,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.CaptureStillReleaseAfterHalfPress(saveDirectory, filePrefix, stillFileType);
            },
            cancellationToken);

    public Task CaptureBurstHoldDownAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType stillFileType,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.CaptureBurstHoldDown(saveDirectory, filePrefix, stillFileType);
            },
            cancellationToken);

    public Task CaptureBurstHoldEndAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SonyCrSdk.CaptureBurstHoldEnd();
            },
            cancellationToken);

    public string? TryGetShootingStateJson()
    {
        lock (_gate)
        {
            if (!_sdkConnected)
                return null;
        }

        return SonyCrBridgeNative.TryGetShootingStateJsonUtf8();
    }

    public string? TryGetLiveViewFocusFramesJson()
    {
        lock (_gate)
        {
            if (!_sdkConnected)
                return null;
        }

        return SonyCrBridgeNative.TryGetLiveViewFocusFramesJsonUtf8();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch
        {
            // 断开或后台 Live View 循环异常时仍须尽力释放 native，避免向上冒泡导致界面任务崩溃。
        }
    }

    private async Task LiveViewLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[InitialJpegBufferBytes];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var st = SonyCrBridgeNative.SonyCr_LiveView_GetLastJpeg(buffer, buffer.Length, out var written);
                if (st == (int)SonyCrStatus.ErrBufferTooSmall)
                {
                    buffer = new byte[Math.Min(buffer.Length * 2, 64 * 1024 * 1024)];
                    continue;
                }

                if (st == (int)SonyCrStatus.Ok && written > 0)
                {
                    using var ms = new MemoryStream(buffer, 0, written, writable: false, publiclyVisible: true);
                    var bmp = new Bitmap(ms);
                    FrameReceived?.Invoke(this, bmp);
                }
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested)
                    break;
            }

            try
            {
                await Task.Delay(33, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static Exception CrEx(int st, string step) =>
        new InvalidOperationException($"{step} 失败: {(SonyCrStatus)st} ({st})");
}
