using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SonySmartControl.Interop;

/// <summary>
/// 对 SonyCrBridge.dll 的托管封装：优先通过 <see cref="ExecuteControlCodeValue"/>、
/// <see cref="SetDevicePropertyU64"/> 等通用入口扩展功能，一般无需再改 C++ 或重编桥接 DLL。
/// 常量请对照官方 CrSDK 头文件（CrControlCode.h、CrDeviceProperty.h 等）。
/// </summary>
public static class SonyCrSdk
{
    public static void ExecuteControlCodeValue(uint code, ulong value) =>
        ThrowIfFailed(SonyCrBridgeNative.SonyCr_ExecuteControlCodeValue(code, value), nameof(ExecuteControlCodeValue));

    /// <summary>UTF-16 字符串，与 SDK ExecuteControlCodeString 一致。</summary>
    public static void ExecuteControlCodeString(uint code, string utf16Text)
    {
        if (string.IsNullOrEmpty(utf16Text))
        {
            ThrowIfFailed(
                SonyCrBridgeNative.SonyCr_ExecuteControlCodeString(code, IntPtr.Zero, 0),
                nameof(ExecuteControlCodeString));
            return;
        }

        var bytes = new byte[Encoding.Unicode.GetByteCount(utf16Text)];
        Encoding.Unicode.GetBytes(utf16Text, 0, utf16Text.Length, bytes, 0);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var p = handle.AddrOfPinnedObject();
            var codeUnits = (uint)(bytes.Length / 2);
            ThrowIfFailed(
                SonyCrBridgeNative.SonyCr_ExecuteControlCodeString(code, p, codeUnits),
                nameof(ExecuteControlCodeString));
        }
        finally
        {
            handle.Free();
        }
    }

    public static void SetDevicePropertyU64(uint propertyCode, ulong value, CrSdkDataType dataType) =>
        ThrowIfFailed(
            SonyCrBridgeNative.SonyCr_SetDevicePropertyU64(propertyCode, value, (uint)dataType),
            nameof(SetDevicePropertyU64));

    /// <summary>与官方 RemoteCli / TS 一致：PriorityKeySettings 常用 UInt32Array，失败再试 UInt16。</summary>
    public static void EnsurePriorityKeyPcRemote()
    {
        if (TrySetDevicePropertyWithDataType(
                CrSdkDevicePropertyCodes.PriorityKeySettings,
                (ulong)CrSdkPriorityKeySettings.PCRemote,
                CrSdkDataType.UInt32Array))
            return;
        TrySetDevicePropertyUInt16(CrSdkDevicePropertyCodes.PriorityKeySettings, (ushort)CrSdkPriorityKeySettings.PCRemote);
    }

    /// <summary>在 EnsurePriorityKeyPcRemote 之后设置机身属性（曝光模式/光圈/快门等）。</summary>
    public static void SetShootingProperty(uint propertyCode, ulong value, CrSdkDataType dataType)
    {
        EnsurePriorityKeyPcRemote();
        SetDevicePropertyU64(propertyCode, value, dataType);
    }

    /// <summary>
    /// 背屏 DISP：与 RemoteCli 一致先写 DispModeSetting 掩码再写 DispMode；优先静态拍照用的 DispModeStill。
    /// <paramref name="crDispMode"/> 为 CrDispMode（如 0x07 = MonitorOff）。
    /// </summary>
    public static void ApplyMonitorDispMode(byte crDispMode)
    {
        try
        {
            ThrowIfFailed(
                SonyCrBridgeNative.SonyCr_ApplyMonitorDispMode(crDispMode),
                nameof(ApplyMonitorDispMode));
        }
        catch (EntryPointNotFoundException)
        {
            SetShootingProperty(CrSdkDevicePropertyCodes.DispMode, crDispMode, CrSdkDataType.UInt8Array);
        }
    }

    public static void SendCommand(uint commandId, CrSdkCommandParam param) =>
        ThrowIfFailed(
            SonyCrBridgeNative.SonyCr_SendCommand(commandId, (uint)param),
            nameof(SendCommand));

    public static void SetDeviceSetting(CrSdkSettingKey key, uint value) =>
        ThrowIfFailed(SonyCrBridgeNative.SonyCr_SetDeviceSetting((uint)key, value), nameof(SetDeviceSetting));

    private static string NormalizeSaveDirectory(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("保存目录不能为空。", nameof(folderPath));
        var full = Path.GetFullPath(folderPath.Trim());
        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>拍照流程：S1 半按锁定后、全按快门前的等待（与 RemoteCli af_shutter 前段衔接）。</summary>
    private const int ShutterHalfPressAfSettleMs = 280;

    /// <summary>仅对焦：与 RemoteCli <c>s1_shooting</c> 一致，S1 Locked 后保持约 1s 再 Unlocked。</summary>
    private const int AfOnlyS1PropertyHoldMs = 1000;

    /// <summary>
    /// 半按快门单次 AF（不拍照）。仅用 <c>CrDeviceProperty_S1</c>（Locked/Unlocked）+ <see cref="CrSdkDataType.UInt16"/>，
    /// 与 RemoteCli <c>s1_shooting</c> 一致。勿用 <c>CrCommandId_S1andRelease</c>：官方说明该命令同时涉及 S1/S2，易误触拍摄。
    /// </summary>
    public static void HalfPressShutterForAutofocusOnly()
    {
        // 在 SonyCrBridge 内单次持锁完成优先键 + S1 锁定/等待/解锁，避免两次 SetDeviceProperty 之间 Live View 插入。
        ThrowIfFailed(
            SonyCrBridgeNative.SonyCr_HalfPressShutterS1AfOnly(AfOnlyS1PropertyHoldMs),
            nameof(HalfPressShutterForAutofocusOnly));
    }

    /// <summary>按下并保持半按对焦：桥接内 PriorityKey（UInt32Array/UInt16 回退）+ S1 Locked。</summary>
    public static void HalfPressShutterS1Press() =>
        ThrowIfFailed(SonyCrBridgeNative.SonyCr_HalfPressShutterS1Press(), nameof(HalfPressShutterS1Press));

    /// <summary>结束半按对焦：S1 Unlocked（须在重编含导出的 SonyCrBridge.dll 后使用）。</summary>
    public static void HalfPressShutterS1Release() =>
        ThrowIfFailed(SonyCrBridgeNative.SonyCr_HalfPressShutterS1Release(), nameof(HalfPressShutterS1Release));

    /// <summary>
    /// 释放快门拍照一次。与 RemoteCli <c>af_shutter</c> 一致：S1 属性半按 → 等待 → <see cref="CrSdkCommandIds.Release"/> Down/Up → S1 松开。
    /// <paramref name="saveDirectory"/> / <paramref name="filePrefix"/> 用于在按快门<strong>之前</strong>再次执行
    /// 「存储目标 + SetSaveInfo」（官方要求 StillImageStoreDestination 须在释放快门前设置）。
    /// </summary>
    /// <param name="stillFileType">当前遥控拍摄文件格式（RAW+JPEG 时从卡拉取需拉两条）。</param>
    public static void CaptureStill(string saveDirectory, string filePrefix, CrSdkFileType stillFileType = CrSdkFileType.Jpeg)
    {
        var releasedFullSequence = false;
        try
        {
            var setSavePathOk = ApplyCaptureSaveSettings(saveDirectory, filePrefix, stillFileType);
            SetDevicePropertyU64(CrSdkDevicePropertyCodes.S1, (ulong)CrSdkLockIndicator.Locked, CrSdkDataType.UInt16);
            Thread.Sleep(ShutterHalfPressAfSettleMs);
            ReleaseShutterCommandsThenS1Unlock();
            releasedFullSequence = true;
            TryPullLatestStillIfSaveInfoFailed(setSavePathOk, saveDirectory, stillFileType);
        }
        catch
        {
            if (!releasedFullSequence)
            {
                try
                {
                    HalfPressShutterS1Release();
                }
                catch
                {
                    // 尽力恢复 S1；已解锁时可能仍失败
                }
            }

            throw;
        }
    }

    /// <summary>
    /// 半按已由 <see cref="HalfPressShutterS1Press"/> 保持时：仅再次应用保存目标 + Release Down/Up + S1 Unlocked（与机身「半按对焦、松开全按」一致）。
    /// </summary>
    public static void CaptureStillReleaseAfterHalfPress(string saveDirectory, string filePrefix, CrSdkFileType stillFileType = CrSdkFileType.Jpeg)
    {
        var releasedFullSequence = false;
        try
        {
            // S1 仍为 Locked：勿再 Set FileType，否则易 ErrControlFailed(-8)；fileType 仅用于 Pull 条数，属性以此前同步为准。
            var setSavePathOk = ApplyCaptureSaveSettings(saveDirectory, filePrefix, stillFileType, setFileTypeProperty: false);
            ReleaseShutterCommandsThenS1Unlock();
            releasedFullSequence = true;
            TryPullLatestStillIfSaveInfoFailed(setSavePathOk, saveDirectory, stillFileType);
        }
        catch
        {
            if (!releasedFullSequence)
            {
                try
                {
                    HalfPressShutterS1Release();
                }
                catch
                {
                }
            }

            throw;
        }
    }

    /// <summary>
    /// 连拍：半按已由 <see cref="HalfPressShutterS1Press"/> 保持 S1 Locked 时，应用保存路径后发送
    /// <c>CrCommandId_Release</c> Down 并保持（与 RemoteCli <c>continuous_shooting</c> 一致），机身持续连拍直至 <see cref="CaptureBurstHoldEnd"/>。
    /// </summary>
    public static void CaptureBurstHoldDown(string saveDirectory, string filePrefix, CrSdkFileType stillFileType)
    {
        EnsurePriorityKeyPcRemote();
        ApplyCaptureSaveSettings(saveDirectory, filePrefix, stillFileType, setFileTypeProperty: false);
        // 与 af_shutter 类似：半按就绪后再全按；略短于单次 CaptureStill，因半按已在 UI 层完成。
        Thread.Sleep(Math.Min(ShutterHalfPressAfSettleMs, 200));
        SendCommand(CrSdkCommandIds.Release, CrSdkCommandParam.Down);
    }

    /// <summary>结束连拍：Release Up → 等待 → S1 Unlocked（与单次拍照尾部一致）。</summary>
    public static void CaptureBurstHoldEnd()
    {
        EnsurePriorityKeyPcRemote();
        SendCommand(CrSdkCommandIds.Release, CrSdkCommandParam.Up);
        Thread.Sleep(PostReleaseBeforeS1UnlockMs);
        HalfPressShutterS1Release();
    }

    /// <summary>官方 RemoteCli <c>af_shutter</c>：Release Up 与 S1 Unlocked 之间间隔 1s。</summary>
    private const int PostReleaseBeforeS1UnlockMs = 1000;

    /// <summary>
    /// 与官方 RemoteCli <c>af_shutter</c> 一致：Release Down/Up 后等待 1s 再 S1 Unlocked。
    /// 优先走桥接单次持锁；旧版 DLL 无导出时回退为分步调用（Live View 仍可能插入，易 -8，请重编 native）。
    /// </summary>
    private static void ReleaseShutterCommandsThenS1Unlock()
    {
        try
        {
            ThrowIfFailed(
                SonyCrBridgeNative.SonyCr_ReleaseShutterDownUpThenS1Unlock(),
                nameof(SonyCrBridgeNative.SonyCr_ReleaseShutterDownUpThenS1Unlock));
        }
        catch (EntryPointNotFoundException)
        {
            SendCommand(CrSdkCommandIds.Release, CrSdkCommandParam.Down);
            Thread.Sleep(35);
            SendCommand(CrSdkCommandIds.Release, CrSdkCommandParam.Up);
            Thread.Sleep(PostReleaseBeforeS1UnlockMs);
            SetDevicePropertyU64(CrSdkDevicePropertyCodes.S1, (ulong)CrSdkLockIndicator.Unlocked, CrSdkDataType.UInt16);
        }
    }

    /// <summary>
    /// 仅当 <c>SetSaveInfo</c> 未成功时从卡拉取兜底。成功时 SDK 已按路径直传 PC，再 Pull 易与机身状态冲突（ErrControlFailed -8），
    /// 且会拖长拍照任务、对焦/拍照按钮长时间不可用；图已在胶片条时切勿再 Pull。
    /// </summary>
    private static void TryPullLatestStillIfSaveInfoFailed(bool setSavePathOk, string saveDirectory, CrSdkFileType stillFileType)
    {
        if (setSavePathOk)
            return;

        var dir = NormalizeSaveDirectory(saveDirectory);
        var pullCount = stillFileType == CrSdkFileType.RawJpeg ? 2 : 1;

        if (TryPullStillsToFolderWithDllFallback(dir, pullCount))
            return;

        throw new InvalidOperationException(
            "从存储卡拉取照片失败。请确认已重新编译并部署 native\\SonyCrBridge 生成的 SonyCrBridge.dll（含 SonyCr_PullLatestStillsToFolderUtf16），"
            + "且相机内格式与存储卡就绪。若仍失败，可暂选「仅 JPEG」试拍。");
    }

    /// <summary>
    /// RAW+JPEG 拉两条时机身忙易 ErrControlFailed(-8)：先尝试 pullCount，失败再试只拉 1 条，避免整单报错。
    /// </summary>
    private static bool TryPullStillsToFolderWithDllFallback(string dir, int pullCount)
    {
        try
        {
            return SonyCrBridgeNative.TryPullLatestStillsToFolderUtf16(dir, pullCount);
        }
        catch (InvalidOperationException)
        {
            if (pullCount <= 1)
                throw;
            return SonyCrBridgeNative.TryPullLatestStillsToFolderUtf16(dir, 1);
        }
    }

    /// <summary>遥控拍摄：保存目标（须为 PC）、路径、前缀；可选写入 <see cref="CrSdkDevicePropertyCodes.FileType"/>。</summary>
    /// <param name="setFileTypeProperty">
    /// 为 false 时跳过 FileType：半按保持 S1 Locked 时机身常拒写 FileType，会报 ErrControlFailed(-8)。
    /// 格式应在连接后及用户改保存格式时由会话层再次同步（与官方 af_shutter 在半按阶段不再改属性一致）。
    /// </param>
    /// <returns>是否成功调用 <c>SetSaveInfo</c>（旧 DLL 无导出时为 false）。</returns>
    public static bool ApplyCaptureSaveSettings(
        string saveDirectory,
        string filePrefix,
        CrSdkFileType fileType,
        bool setFileTypeProperty = true)
    {
        var ok = EnsureRemoteStillImageTransferToPc(saveDirectory, filePrefix);
        if (setFileTypeProperty)
            SetDevicePropertyU64(CrSdkDevicePropertyCodes.FileType, (ulong)fileType, CrSdkDataType.UInt16);
        // 与官方「RAW+J PC Save Image」一致：双格式时须指定传到 PC 的内容，否则常见为仅 JPEG。
        TryApplyRawJpcPcSaveImageForFileType(fileType);
        return ok;
    }

    /// <summary>
    /// 按当前 <see cref="CrSdkFileType"/> 同步 <c>CrDeviceProperty_RAW_J_PC_Save_Image</c>（UInt16Array）。
    /// 失败时忽略：部分机型/模式下属性不可用。
    /// </summary>
    private static void TryApplyRawJpcPcSaveImageForFileType(CrSdkFileType fileType)
    {
        if (!TryResolveRawJpcPcSaveImageValue(fileType, out var packed))
            return;
        TrySetDevicePropertyWithDataType(
            CrSdkDevicePropertyCodes.RawJpcPcSaveImage,
            packed,
            CrSdkDataType.UInt16Array);
    }

    private static bool TryResolveRawJpcPcSaveImageValue(CrSdkFileType fileType, out ulong packed)
    {
        packed = 0;
        CrSdkRawJpcPcSaveImage v;
        switch (fileType)
        {
            case CrSdkFileType.Raw:
                v = CrSdkRawJpcPcSaveImage.RawOnly;
                break;
            case CrSdkFileType.RawJpeg:
                v = CrSdkRawJpcPcSaveImage.RawAndJpeg;
                break;
            case CrSdkFileType.RawHeif:
                v = CrSdkRawJpcPcSaveImage.RawAndHeif;
                break;
            case CrSdkFileType.Heif:
                v = CrSdkRawJpcPcSaveImage.HeifOnly;
                break;
            default:
                return false;
        }

        packed = (ulong)(ushort)v;
        return true;
    }

    /// <summary>
    /// 官方 Troubleshooting：若照片未传到 PC，请确认在释放快门前已将 StillImageStoreDestination 设为 HostPC 或 HostPCAndMemoryCard；
    /// 并建议将 PriorityKeySettings 设为 PCRemote。此处优先 PC+卡（兼容性通常更好），失败再仅 HostPC。
    /// </summary>
    /// <returns>是否成功调用 <c>SetSaveInfo</c>（旧 DLL 无导出时为 false，拍照后会尝试从卡 Pull）。</returns>
    private static bool EnsureRemoteStillImageTransferToPc(string saveDirectory, string filePrefix)
    {
        var dir = NormalizeSaveDirectory(saveDirectory);
        Directory.CreateDirectory(dir);

        TrySetDevicePropertyUInt16(
            CrSdkDevicePropertyCodes.PriorityKeySettings,
            (ushort)CrSdkPriorityKeySettings.PCRemote);

        if (!TrySetDevicePropertyUInt16(
                CrSdkDevicePropertyCodes.StillImageStoreDestination,
                (ushort)CrSdkStillImageStoreDestination.HostPCAndMemoryCard) &&
            !TrySetDevicePropertyUInt16(
                CrSdkDevicePropertyCodes.StillImageStoreDestination,
                (ushort)CrSdkStillImageStoreDestination.HostPC))
        {
            throw new InvalidOperationException(
                "无法将「静态影像保存目标」设为电脑（HostPC 或 PC+存储卡）。"
                + " 请在相机菜单中确认「遥控拍摄」下允许保存到电脑，或解除相关锁定后再试。");
        }

        // 旧版 SonyCrBridge.dll 可能未导出 SonyCr_SetSaveInfoUtf16：用动态绑定，缺失时跳过（拍照后改从卡 Pull）。
        return SonyCrBridgeNative.TrySetSaveInfoUtf16(
            dir,
            string.IsNullOrWhiteSpace(filePrefix) ? null : filePrefix,
            -1);
    }

    private static bool TrySetDevicePropertyUInt16(uint propertyCode, ushort value)
    {
        try
        {
            SetDevicePropertyU64(propertyCode, value, CrSdkDataType.UInt16);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySetDevicePropertyWithDataType(uint propertyCode, ulong value, CrSdkDataType dataType)
    {
        try
        {
            SetDevicePropertyU64(propertyCode, value, dataType);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>遥控触摸点对焦；坐标为 SDK 规定的 0~639 / 0~479。</summary>
    public static void RemoteTouchAf(int x, int y) =>
        ThrowIfFailed(SonyCrBridgeNative.SonyCr_RemoteTouchAf(x, y), nameof(RemoteTouchAf));

    /// <summary>将画面归一化坐标转为 SDK 触摸坐标并请求对焦。</summary>
    public static void RemoteTouchAfFromNormalized(double normalizedX, double normalizedY)
    {
        var sx = (int)Math.Clamp(Math.Round(normalizedX * 639.0), 0, 639);
        var sy = (int)Math.Clamp(Math.Round(normalizedY * 479.0), 0, 479);
        RemoteTouchAf(sx, sy);
    }

    /// <summary>
    /// 取消遥控触摸对焦点（与官方 <c>CrCommandId_CancelRemoteTouchOperation</c> 一致：先 Down 再 Up；
    /// 需机身 <c>CrDeviceProperty_CancelRemoteTouchOperationEnableStatus</c> 为 Enable 时方可执行）。
    /// </summary>
    public static void CancelRemoteTouchOperation()
    {
        SendCommand(CrSdkCommandIds.CancelRemoteTouchOperation, CrSdkCommandParam.Down);
        Thread.Sleep(35);
        SendCommand(CrSdkCommandIds.CancelRemoteTouchOperation, CrSdkCommandParam.Up);
    }

    /// <summary>
    /// 在已连接且支持 Remote Transfer 列表时，按<strong>文件名（不含路径）</strong>删除机身中对应内容（RAW+JPEG 等为同一 contentId）。
    /// 旧桥接 DLL 无导出时返回 null；列表无匹配为 <see cref="SonyCrStatus.ErrNotFound"/>。
    /// </summary>
    public static SonyCrStatus? TryDeleteRemoteContentMatchingFileName(string fileNameOnly) =>
        SonyCrBridgeNative.TryDeleteRemoteContentMatchingFileName(fileNameOnly);

    private static void ThrowIfFailed(int st, string operation)
    {
        if (st == (int)SonyCrStatus.Ok)
            return;
        throw new InvalidOperationException($"{operation} 失败: {(SonyCrStatus)st} ({st})");
    }
}
