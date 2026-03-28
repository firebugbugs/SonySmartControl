using System;
using System.Collections.Generic;
using SonySmartControl.Interop;
using SonySmartControl.Models;

namespace SonySmartControl.Services.Camera;

/// <summary>
/// 仅枚举、不保持连接：Init → Enum → 读取列表 → Release，供设备搜索窗口使用。
/// </summary>
public static class CameraDeviceDiscovery
{
    public static IReadOnlyList<DiscoveredCameraDevice> Discover()
    {
        EnsureSdkReady();

        try
        {
            RefreshEnumWithRecovery();

            var st = SonyCrBridgeNative.SonyCr_GetCameraDeviceCount(out var count);
            if (st != (int)SonyCrStatus.Ok)
                throw new InvalidOperationException(
                    $"{nameof(SonyCrBridgeNative.SonyCr_GetCameraDeviceCount)} 失败: {(SonyCrStatus)st} ({st})");

            if (count < 1)
                return Array.Empty<DiscoveredCameraDevice>();

            var list = new List<DiscoveredCameraDevice>(count);
            for (var i = 0; i < count; i++)
            {
                var model = SonyCrBridgeNative.GetCameraModelUtf8(i)?.Trim();
                if (string.IsNullOrEmpty(model))
                    model = $"设备 #{i + 1}";

                var conn = SonyCrBridgeNative.TryGetCameraConnectionTypeUtf8(i)?.Trim();
                var ep = SonyCrBridgeNative.TryGetCameraEndpointUtf8(i)?.Trim();
                list.Add(
                    new DiscoveredCameraDevice(
                        i,
                        model,
                        string.IsNullOrEmpty(conn) ? "—" : conn,
                        string.IsNullOrEmpty(ep) ? "—" : ep));
            }

            return list;
        }
        finally
        {
            // 不在“设备搜索”阶段立即 Release SDK：
            // 某些机型/固件在 Wi-Fi 配对场景会依赖同进程内的 SDK 上下文缓存来复用已配对信息，
            // 若每次搜索后都 Release，再连接时可能被机身当作“新会话”而重复要求配对。
            // 真正的断开与释放由会话层在应用生命周期结束时统一处理。
        }
    }

    private static void EnsureSdkReady()
    {
        var st = SonyCrBridgeNative.SonyCr_Init();
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"{nameof(SonyCrBridgeNative.SonyCr_Init)} 失败: {(SonyCrStatus)st} ({st})");
    }

    private static void RefreshEnumWithRecovery()
    {
        var st = SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh();
        if (st == (int)SonyCrStatus.Ok)
            return;

        if (st != (int)SonyCrStatus.ErrEnumFailed)
            throw new InvalidOperationException(
                $"{nameof(SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh)} 失败: {(SonyCrStatus)st} ({st})");

        // 某些机型在“连接->断开”后会留下 SDK 内部枚举状态，首次刷新可能返回 -3。
        // 做一次 SDK 级重置后重试，避免用户必须重启应用才能再次连接。
        SonyCrBridgeNative.TryDisconnect();
        SonyCrBridgeNative.TryReleaseSdk();

        EnsureSdkReady();
        st = SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh();
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException(
                $"{nameof(SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh)} 重试失败: {(SonyCrStatus)st} ({st})");
    }
}
