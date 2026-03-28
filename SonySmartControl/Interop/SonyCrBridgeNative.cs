using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SonySmartControl.Interop;

/// <summary>
/// P/Invoke 到 <c>SonyCrBridge.dll</c>（由 <c>native/SonyCrBridge</c> CMake 工程生成）。
/// 运行前需将 <c>SonyCrBridge.dll</c> 与索尼 <c>Cr_Core.dll</c>、<c>CrAdapter</c> 等置于输出目录。
/// </summary>
internal static class SonyCrBridgeNative
{
    private const string Dll = "SonyCrBridge";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_Init();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void SonyCr_Release();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetSdkVersion(out uint outVersion);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_EnumCameraDevicesRefresh();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraDeviceCount(out int outCount);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraModelUtf8Length(int index, out int outLengthBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraModelUtf8(int index, [Out] byte[] buffer, int bufferSizeBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraConnectionTypeUtf8Length(int index, out int outLengthBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraConnectionTypeUtf8(int index, [Out] byte[] buffer, int bufferSizeBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraEndpointUtf8Length(int index, out int outLengthBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetCameraEndpointUtf8(int index, [Out] byte[] buffer, int bufferSizeBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetFocusOperationWithInt16EnableStatus(out int outEnable);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_ConnectRemoteByIndex(int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_Disconnect();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_LiveView_GetLastJpeg([Out] byte[]? buffer, int bufferSize, out int outWritten);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_RemoteTouchAf(int x, int y);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_ExecuteControlCodeValue(uint code, ulong value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_ExecuteControlCodeString(uint code, IntPtr utf16, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_SetDevicePropertyU64(uint propertyCode, ulong value, uint crDataType);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_ApplyMonitorDispMode(byte mode);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_HalfPressShutterS1AfOnly(uint holdMs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_HalfPressShutterS1Press();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_HalfPressShutterS1Release();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_ReleaseShutterDownUpThenS1Unlock();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_SendCommand(uint commandId, uint commandParam);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_SetDeviceSetting(uint key, uint value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetTransportStats(out ulong outUploadBytes, out ulong outDownloadBytes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void SonyCr_ResetTransportStats();

    /// <summary>
    /// 读取相机端 SD 卡容量/使用量估算（桥接层在 native 侧统计两卡槽）。
    /// 返回中的“容量/使用量”是基于仍图传输大小的估算值，不保证等同于卡厂商标称容量。
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int SonyCr_GetSdCardUsageEstimate(
        out ulong outSlot1TotalBytes,
        out ulong outSlot1UsedBytes,
        out int outSlot1HasCard,
        out ulong outSlot2TotalBytes,
        out ulong outSlot2UsedBytes,
        out int outSlot2HasCard);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_GetLastSdUsageDebugUtf8Delegate(
        [Out] byte[] buffer,
        int bufferSizeBytes,
        out int outWritten);

    private static readonly object SdUsageDebugLock = new();
    private static SonyCr_GetLastSdUsageDebugUtf8Delegate? _getSdUsageDebug;
    private static bool _getSdUsageDebugExportUnavailable;

    internal static string? TryGetLastSdUsageDebugUtf8()
    {
        lock (SdUsageDebugLock)
        {
            if (_getSdUsageDebugExportUnavailable)
                return null;
            if (_getSdUsageDebug == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _getSdUsageDebugExportUnavailable = true;
                    return null;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_GetLastSdUsageDebugUtf8", out var addr))
                {
                    _getSdUsageDebugExportUnavailable = true;
                    return null;
                }

                _getSdUsageDebug =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_GetLastSdUsageDebugUtf8Delegate>(addr);
            }
        }

        for (var cap = 1024; cap <= 64 * 1024; cap *= 2)
        {
            var buf = new byte[cap];
            var st = _getSdUsageDebug!(buf, buf.Length, out var written);
            if (st == (int)SonyCrStatus.ErrBufferTooSmall)
                continue;
            if (st == (int)SonyCrStatus.Ok && written > 1)
                return Encoding.UTF8.GetString(buf.AsSpan(0, written - 1));
            return null;
        }
        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_GetLastCapturePullDebugUtf8Delegate(
        [Out] byte[] buffer,
        int bufferSizeBytes,
        out int outWritten);

    private static readonly object CapturePullDebugLock = new();
    private static SonyCr_GetLastCapturePullDebugUtf8Delegate? _getCapturePullDebug;
    private static bool _getCapturePullDebugExportUnavailable;

    internal static string? TryGetLastCapturePullDebugUtf8()
    {
        lock (CapturePullDebugLock)
        {
            if (_getCapturePullDebugExportUnavailable)
                return null;
            if (_getCapturePullDebug == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _getCapturePullDebugExportUnavailable = true;
                    return null;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_GetLastCapturePullDebugUtf8", out var addr))
                {
                    _getCapturePullDebugExportUnavailable = true;
                    return null;
                }

                _getCapturePullDebug =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_GetLastCapturePullDebugUtf8Delegate>(addr);
            }
        }

        for (var cap = 1024; cap <= 64 * 1024; cap *= 2)
        {
            var buf = new byte[cap];
            var st = _getCapturePullDebug!(buf, buf.Length, out var written);
            if (st == (int)SonyCrStatus.ErrBufferTooSmall)
                continue;
            if (st == (int)SonyCrStatus.Ok && written > 1)
                return Encoding.UTF8.GetString(buf.AsSpan(0, written - 1));
            return null;
        }
        return null;
    }

    private delegate int SonyCr_GetLastConnectDebugUtf8Delegate(
        [Out] byte[] buffer,
        int bufferSizeBytes,
        out int outWritten);

    private static readonly object ConnectDebugLock = new();
    private static SonyCr_GetLastConnectDebugUtf8Delegate? _getConnectDebug;
    private static bool _getConnectDebugExportUnavailable;

    internal static string? TryGetLastConnectDebugUtf8()
    {
        lock (ConnectDebugLock)
        {
            if (_getConnectDebugExportUnavailable)
                return null;
            if (_getConnectDebug == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _getConnectDebugExportUnavailable = true;
                    return null;
                }
                if (!NativeLibrary.TryGetExport(h, "SonyCr_GetLastConnectDebugUtf8", out var addr))
                {
                    _getConnectDebugExportUnavailable = true;
                    return null;
                }
                _getConnectDebug =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_GetLastConnectDebugUtf8Delegate>(addr);
            }

            var buf = new byte[2048];
            var st = _getConnectDebug!(buf, buf.Length, out var written);
            if (st != 0 || written <= 0)
                return null;
            var len = Math.Min(written, buf.Length);
            if (len > 0 && buf[len - 1] == 0)
                len--;
            return Encoding.UTF8.GetString(buf, 0, len);
        }
    }

    private delegate int SonyCr_GetLastLiveViewDebugUtf8Delegate(
        [Out] byte[] buffer,
        int bufferSizeBytes,
        out int outWritten);

    private static readonly object LiveViewDebugLock = new();
    private static SonyCr_GetLastLiveViewDebugUtf8Delegate? _getLiveViewDebug;
    private static bool _getLiveViewDebugExportUnavailable;

    internal static string? TryGetLastLiveViewDebugUtf8()
    {
        lock (LiveViewDebugLock)
        {
            if (_getLiveViewDebugExportUnavailable)
                return null;
            if (_getLiveViewDebug == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _getLiveViewDebugExportUnavailable = true;
                    return null;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_GetLastLiveViewDebugUtf8", out var addr))
                {
                    _getLiveViewDebugExportUnavailable = true;
                    return null;
                }

                _getLiveViewDebug =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_GetLastLiveViewDebugUtf8Delegate>(addr);
            }
        }

        for (var cap = 512; cap <= 16 * 1024; cap *= 2)
        {
            var buf = new byte[cap];
            var st = _getLiveViewDebug!(buf, buf.Length, out var written);
            if (st == (int)SonyCrStatus.ErrBufferTooSmall)
                continue;
            if (st == (int)SonyCrStatus.Ok && written > 1)
                return Encoding.UTF8.GetString(buf.AsSpan(0, written - 1));
            return null;
        }
        return null;
    }

    /// <summary>
    /// 不使用 DllImport 直接绑定：旧版 SonyCrBridge.dll 可能未导出 <c>SonyCr_SetSaveInfoUtf16</c>，
    /// 若用 DllImport 会在首次调用时抛 <see cref="EntryPointNotFoundException"/>。
    /// 改为运行时 <see cref="NativeLibrary.TryGetExport"/>，缺失时由托管侧跳过 SetSaveInfo。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_SetSaveInfoUtf16Delegate(
        [MarshalAs(UnmanagedType.LPWStr)] string pathUtf16,
        [MarshalAs(UnmanagedType.LPWStr)] string? prefixUtf16,
        int saveNumber);

    private static readonly object SaveInfoLock = new();
    private static SonyCr_SetSaveInfoUtf16Delegate? _setSaveInfo;
    private static bool _saveInfoExportUnavailable;

    /// <returns>成功为 true；DLL 无此导出时为 false（不抛异常）；SDK 返回错误时抛 <see cref="InvalidOperationException"/>。</returns>
    internal static bool TrySetSaveInfoUtf16(string pathUtf16, string? prefixUtf16, int saveNumber)
    {
        lock (SaveInfoLock)
        {
            if (_saveInfoExportUnavailable)
                return false;
            if (_setSaveInfo == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _saveInfoExportUnavailable = true;
                    return false;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_SetSaveInfoUtf16", out var addr))
                {
                    _saveInfoExportUnavailable = true;
                    return false;
                }

                _setSaveInfo = Marshal.GetDelegateForFunctionPointer<SonyCr_SetSaveInfoUtf16Delegate>(addr);
            }
        }

        var st = _setSaveInfo!(pathUtf16, prefixUtf16, saveNumber);
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SetSaveInfo 失败: {(SonyCrStatus)st} ({st})");
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_PullLatestStillToFolderUtf16Delegate(
        [MarshalAs(UnmanagedType.LPWStr)] string destFolderUtf16);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_PullLatestStillsToFolderUtf16Delegate(
        [MarshalAs(UnmanagedType.LPWStr)] string destFolderUtf16,
        int pullCount);

    private static readonly object PullLatestLock = new();
    private static SonyCr_PullLatestStillToFolderUtf16Delegate? _pullLatest;
    private static SonyCr_PullLatestStillsToFolderUtf16Delegate? _pullLatestMulti;
    private static bool _pullLatestExportUnavailable;

    /// <returns>成功 true；DLL 无导出 false；SDK 非 Ok 时抛异常。</returns>
    internal static bool TryPullLatestStillToFolderUtf16(string destFolderUtf16) =>
        TryPullLatestStillsToFolderUtf16(destFolderUtf16, 1);

    /// <summary>从卡上拉取末尾若干条内容（RAW+JPEG 时请传 pullCount=2）。旧 DLL 无多文件导出时仅拉取 1 条。</summary>
    internal static bool TryPullLatestStillsToFolderUtf16(string destFolderUtf16, int pullCount)
    {
        if (pullCount < 1)
            pullCount = 1;
        if (pullCount > 16)
            pullCount = 16;

        lock (PullLatestLock)
        {
            if (_pullLatestExportUnavailable)
                return false;

            if (_pullLatestMulti == null && _pullLatest == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _pullLatestExportUnavailable = true;
                    return false;
                }

                if (NativeLibrary.TryGetExport(h, "SonyCr_PullLatestStillsToFolderUtf16", out var addrMulti))
                    _pullLatestMulti =
                        Marshal.GetDelegateForFunctionPointer<SonyCr_PullLatestStillsToFolderUtf16Delegate>(addrMulti);
                else if (NativeLibrary.TryGetExport(h, "SonyCr_PullLatestStillToFolderUtf16", out var addr))
                    _pullLatest = Marshal.GetDelegateForFunctionPointer<SonyCr_PullLatestStillToFolderUtf16Delegate>(addr);
                else
                {
                    _pullLatestExportUnavailable = true;
                    return false;
                }
            }
        }

        if (_pullLatestMulti != null)
        {
            var st = _pullLatestMulti(destFolderUtf16, pullCount);
            if (st != (int)SonyCrStatus.Ok)
                throw new InvalidOperationException($"从存储卡拉取照片失败: {(SonyCrStatus)st} ({st})");
            return true;
        }

        if (_pullLatest == null)
            return false;

        // 旧 DLL 仅支持单条拉取；pullCount>1 时需新导出 SonyCr_PullLatestStillsToFolderUtf16。
        if (pullCount > 1)
            return false;

        var stOne = _pullLatest(destFolderUtf16);
        if (stOne != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"从存储卡拉取照片失败: {(SonyCrStatus)stOne} ({stOne})");
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_DeleteRemoteContentMatchingFileNameUtf16Delegate(
        [MarshalAs(UnmanagedType.LPWStr)] string fileNameUtf16);

    private static readonly object DeleteRemoteLock = new();
    private static SonyCr_DeleteRemoteContentMatchingFileNameUtf16Delegate? _deleteRemoteByFileName;
    private static bool _deleteRemoteExportUnavailable;

    /// <summary>
    /// 在 Remote Transfer 列表中按<strong>文件名不含路径</strong>匹配并删除机身内容（RAW+JPEG 同一条 contentId）。
    /// 旧 DLL 无导出、未连接或列表中无匹配时返回 null；否则返回状态码（含 <see cref="SonyCrStatus.ErrNotFound"/>）。
    /// </summary>
    internal static SonyCrStatus? TryDeleteRemoteContentMatchingFileName(string fileNameOnly)
    {
        if (string.IsNullOrWhiteSpace(fileNameOnly))
            return null;

        var name = Path.GetFileName(fileNameOnly.Trim());
        if (string.IsNullOrEmpty(name))
            return null;

        lock (DeleteRemoteLock)
        {
            if (_deleteRemoteExportUnavailable)
                return null;

            if (_deleteRemoteByFileName == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _deleteRemoteExportUnavailable = true;
                    return null;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_DeleteRemoteContentMatchingFileNameUtf16", out var addr))
                {
                    _deleteRemoteExportUnavailable = true;
                    return null;
                }

                _deleteRemoteByFileName =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_DeleteRemoteContentMatchingFileNameUtf16Delegate>(addr);
            }
        }

        var st = _deleteRemoteByFileName!(name);
        return (SonyCrStatus)st;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_GetShootingStateJsonUtf8Delegate(
        [Out] byte[] buffer,
        int bufferSizeBytes,
        out int outWritten);

    private static readonly object ShootingJsonLock = new();
    private static SonyCr_GetShootingStateJsonUtf8Delegate? _getShootingStateJson;

    /// <summary>旧版桥接 DLL 可能无此导出：缺失时返回 null。</summary>
    internal static string? TryGetShootingStateJsonUtf8()
    {
        lock (ShootingJsonLock)
        {
            if (_getShootingStateJson == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    return null;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_GetShootingStateJsonUtf8", out var addr))
                    return null;

                _getShootingStateJson =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_GetShootingStateJsonUtf8Delegate>(addr);
            }
        }

        for (var cap = 4096; cap <= 256 * 1024; cap *= 2)
        {
            var buf = new byte[cap];
            var st = _getShootingStateJson!(buf, buf.Length, out var written);
            if (st == (int)SonyCrStatus.ErrBufferTooSmall)
                continue;
            if (st == (int)SonyCrStatus.Ok && written > 1)
                return Encoding.UTF8.GetString(buf.AsSpan(0, written - 1));
            return null;
        }

        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_GetLiveViewFocusFramesJsonUtf8Delegate(
        [Out] byte[] buffer,
        int bufferSizeBytes,
        out int outWritten);

    private static readonly object FocusFramesJsonLock = new();
    private static SonyCr_GetLiveViewFocusFramesJsonUtf8Delegate? _getLiveViewFocusFramesJson;
    private static bool _focusFramesJsonExportUnavailable;

    /// <summary>旧版桥接 DLL 可能无此导出：缺失时返回 null。</summary>
    internal static string? TryGetLiveViewFocusFramesJsonUtf8()
    {
        lock (FocusFramesJsonLock)
        {
            if (_focusFramesJsonExportUnavailable)
                return null;
            if (_getLiveViewFocusFramesJson == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _focusFramesJsonExportUnavailable = true;
                    return null;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_GetLiveViewFocusFramesJsonUtf8", out var addr))
                {
                    _focusFramesJsonExportUnavailable = true;
                    return null;
                }

                _getLiveViewFocusFramesJson =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_GetLiveViewFocusFramesJsonUtf8Delegate>(addr);
            }
        }

        for (var cap = 1024; cap <= 128 * 1024; cap *= 2)
        {
            var buf = new byte[cap];
            var st = _getLiveViewFocusFramesJson!(buf, buf.Length, out var written);
            if (st == (int)SonyCrStatus.ErrBufferTooSmall)
                continue;
            if (st == (int)SonyCrStatus.Ok && written > 1)
                return Encoding.UTF8.GetString(buf.AsSpan(0, written - 1));
            return null;
        }

        return null;
    }

    private static readonly object RemoteTouchEnableLock = new();
    private static SonyCr_SetRemoteTouchOperationEnableDelegate? _setRemoteTouchOperationEnable;
    private static bool _setRemoteTouchOperationEnableExportUnavailable;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_SetRemoteTouchOperationEnableDelegate(int enable);

    /// <summary>
    /// 新 DLL：桥接内先设 PC 遥控优先再写遥控触摸开关。无此导出时返回 false（由托管侧回退到 <see cref="SonyCrSdk.SetShootingProperty"/>）。
    /// SDK 非 Ok 时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    internal static bool TrySetRemoteTouchOperationEnable(int enable)
    {
        if (enable != 0 && enable != 1)
            throw new ArgumentOutOfRangeException(nameof(enable));
        lock (RemoteTouchEnableLock)
        {
            if (_setRemoteTouchOperationEnableExportUnavailable)
                return false;

            if (_setRemoteTouchOperationEnable == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _setRemoteTouchOperationEnableExportUnavailable = true;
                    return false;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_SetRemoteTouchOperationEnable", out var addr))
                {
                    _setRemoteTouchOperationEnableExportUnavailable = true;
                    return false;
                }

                _setRemoteTouchOperationEnable =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_SetRemoteTouchOperationEnableDelegate>(addr);
            }
        }

        var st = _setRemoteTouchOperationEnable!(enable);
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SonyCr_SetRemoteTouchOperationEnable 失败: {(SonyCrStatus)st} ({st})");
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SonyCr_SetRemoteSaveImageSizeDelegate(uint value);

    private static readonly object RemoteSaveImageSizeLock = new();
    private static SonyCr_SetRemoteSaveImageSizeDelegate? _setRemoteSaveImageSize;
    private static bool _remoteSaveImageSizeExportUnavailable;

    /// <summary>
    /// 设定遥控保存图片尺寸（CrDeviceProperty_RemoteSaveImageSize）：1=Large，2=Small。
    /// 旧 DLL 无导出时返回 false；SDK 非 Ok 时抛异常。
    /// </summary>
    internal static bool TrySetRemoteSaveImageSize(uint value)
    {
        if (value is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(value));

        lock (RemoteSaveImageSizeLock)
        {
            if (_remoteSaveImageSizeExportUnavailable)
                return false;
            if (_setRemoteSaveImageSize == null)
            {
                IntPtr h;
                try
                {
                    h = NativeLibrary.Load(Dll);
                }
                catch (DllNotFoundException)
                {
                    _remoteSaveImageSizeExportUnavailable = true;
                    return false;
                }

                if (!NativeLibrary.TryGetExport(h, "SonyCr_SetRemoteSaveImageSize", out var addr))
                {
                    _remoteSaveImageSizeExportUnavailable = true;
                    return false;
                }

                _setRemoteSaveImageSize =
                    Marshal.GetDelegateForFunctionPointer<SonyCr_SetRemoteSaveImageSizeDelegate>(addr);
            }
        }

        var st = _setRemoteSaveImageSize!(value);
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SonyCr_SetRemoteSaveImageSize 失败: {(SonyCrStatus)st} ({st})");
        return true;
    }

    internal static string? GetCameraModelUtf8(int index)
    {
        var st = SonyCr_GetCameraModelUtf8Length(index, out var len);
        if (st != (int)SonyCrStatus.Ok || len <= 0)
            return null;
        var buf = new byte[len];
        st = SonyCr_GetCameraModelUtf8(index, buf, len);
        if (st != (int)SonyCrStatus.Ok)
            return null;

        return Encoding.UTF8.GetString(buf.AsSpan(0, len - 1));
    }

    internal static string? TryGetCameraConnectionTypeUtf8(int index)
    {
        try
        {
            var st = SonyCr_GetCameraConnectionTypeUtf8Length(index, out var len);
            if (st != (int)SonyCrStatus.Ok || len <= 0)
                return null;
            var buf = new byte[len];
            st = SonyCr_GetCameraConnectionTypeUtf8(index, buf, len);
            if (st != (int)SonyCrStatus.Ok)
                return null;
            return Encoding.UTF8.GetString(buf.AsSpan(0, len - 1));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    internal static string? TryGetCameraEndpointUtf8(int index)
    {
        try
        {
            var st = SonyCr_GetCameraEndpointUtf8Length(index, out var len);
            if (st != (int)SonyCrStatus.Ok || len <= 0)
                return null;
            var buf = new byte[len];
            st = SonyCr_GetCameraEndpointUtf8(index, buf, len);
            if (st != (int)SonyCrStatus.Ok)
                return null;
            return Encoding.UTF8.GetString(buf.AsSpan(0, len - 1));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 在未部署 SonyCrBridge.dll / Cr_Core.dll 时，普通 P/Invoke 会抛异常；清理路径必须吞掉以免进程崩溃。
    /// </summary>
    internal static void TryDisconnect()
    {
        try
        {
            SonyCr_Disconnect();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }
    }

    /// <summary>
    /// 对应 <c>SonyCr_Release</c>：断开设备并释放 CrSDK 全局状态（原生侧线程/资源）。
    /// 在会话 <see cref="DisconnectAsync"/> 之后调用；未部署 DLL 时静默忽略。
    /// </summary>
    internal static void TryReleaseSdk()
    {
        try
        {
            SonyCr_Release();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }
    }
}
