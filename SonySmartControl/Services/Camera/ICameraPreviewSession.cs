using Avalonia.Media.Imaging;
using SonySmartControl.Interop;

namespace SonySmartControl.Services.Camera;

public readonly record struct LiveViewProbeResult(int TotalProbes, int FramesDetectedProbes)
{
    public bool IsLikelyStopped => FramesDetectedProbes == 0;
}

/// <summary>SD 卡容量/使用量估算结果（以“当前仍图传输大小”换算容量，按卡槽分别统计）。</summary>
public readonly record struct SdCardUsageEstimate(
    bool Slot1HasCard,
    ulong Slot1TotalBytes,
    ulong Slot1UsedBytes,
    bool Slot2HasCard,
    ulong Slot2TotalBytes,
    ulong Slot2UsedBytes);

/// <summary>
/// 相机实时预览会话：连接后通过 <see cref="FrameReceived"/> 推送 JPEG 解码后的位图。
/// </summary>
public interface ICameraPreviewSession : IAsyncDisposable
{
    event EventHandler<Bitmap>? FrameReceived;

    /// <summary>连接成功后的机身型号；未连接或未枚举到时为 null。</summary>
    string? ConnectedCameraModel { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 开始推送预览帧（CrSDK：在连接且应用完遥控保存设置后再调用，避免与桥接全局锁上的 LiveView 争用导致卡死）。
    /// </summary>
    Task StartPreviewAsync(CancellationToken cancellationToken = default);
    /// <summary>设置 LiveView 取流开关：关闭后 CMOS 停止取流，拍照控制链路保持可用。</summary>
    Task SetLiveViewEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在 LiveView 已关闭后，主动探测底层是否仍可读到预览帧（用于验证是否真正停流）。
    /// </summary>
    Task<LiveViewProbeResult> ProbeLiveViewDisabledStateAsync(
        int probeCount = 6,
        int probeIntervalMs = 250,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取当前会话累计上传/下载字节统计（桥接层计数，尽量贴近相机链路）。
    /// </summary>
    bool TryGetTransportStats(out ulong uploadBytes, out ulong downloadBytes);

    /// <summary>
    /// 读取相机端 SD 卡每个卡槽的容量/使用量估算（用于 UI 展示）。
    /// </summary>
    Task<SdCardUsageEstimate?> TryGetSdCardUsageEstimateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取最近一次 SD 容量估算的原始诊断文本（旧版 DLL 或不支持时为 null）。
    /// </summary>
    string? TryGetSdCardUsageDebugText() => null;

    Task DisconnectAsync();

    /// <summary>
    /// 在画面上的归一化坐标 (0..1，相对整幅预览图) 请求触摸/点对焦。
    /// </summary>
    Task RequestTouchAutofocusAsync(double normalizedX, double normalizedY, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消当前遥控触摸对焦区域，使相机回到自动对焦区域行为（CrSDK CancelRemoteTouchOperation）。
    /// </summary>
    Task CancelRemoteTouchOperationAsync(CancellationToken cancellationToken = default);

    /// <summary>遥控拍摄：保存目录、文件名前缀与文件类型（JPEG/RAW 等）。</summary>
    Task ApplyCameraSaveSettingsAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType fileType,
        CancellationToken cancellationToken = default);

    /// <summary>半按快门单次 AF（控制码 S1 键 Down/Up，不拍照）。</summary>
    Task OneShotFocusAsync(CancellationToken cancellationToken = default);

    /// <summary>按下并保持半按对焦（S1 Locked）；与 <see cref="EndHalfPressFocusAsync"/> 成对。</summary>
    Task BeginHalfPressFocusAsync(CancellationToken cancellationToken = default);

    /// <summary>松开半按（S1 Unlocked）。</summary>
    Task EndHalfPressFocusAsync(CancellationToken cancellationToken = default);

    /// <summary>释放快门拍摄一帧；CrSDK 会再次应用保存目录与传 PC 设置。</summary>
    Task CaptureStillAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType stillFileType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 半按已由 <see cref="BeginHalfPressFocusAsync"/> 保持时松开快门：仅 Release + 解锁 S1 与 Pull；不再重复 S1 Locked。
    /// </summary>
    Task CaptureStillReleaseAfterHalfPressAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType stillFileType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 连拍：半按已保持时发送快门全按并保持（Release Down），持续连拍直至 <see cref="CaptureBurstHoldEndAsync"/>。
    /// </summary>
    Task CaptureBurstHoldDownAsync(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType stillFileType,
        CancellationToken cancellationToken = default);

    /// <summary>连拍结束：Release Up 并解锁半按。</summary>
    Task CaptureBurstHoldEndAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// CrSDK：轮询读取曝光模式、光圈、快门、ISO、白平衡（JSON）；未连接或非 CrSDK 会话时为 null。
    /// </summary>
    string? TryGetShootingStateJson() => null;

    /// <summary>
    /// CrSDK：最近一次 Live View 对焦框 JSON（<c>CrFocusFrameInfo</c>）；未连接、非 CrSDK 或旧版 DLL 无导出时为 null。
    /// </summary>
    string? TryGetLiveViewFocusFramesJson() => null;
}
