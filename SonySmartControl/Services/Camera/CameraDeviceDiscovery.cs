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
        var st = SonyCrBridgeNative.SonyCr_Init();
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"{nameof(SonyCrBridgeNative.SonyCr_Init)} 失败: {(SonyCrStatus)st} ({st})");

        try
        {
            st = SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh();
            if (st != (int)SonyCrStatus.Ok)
                throw new InvalidOperationException(
                    $"{nameof(SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh)} 失败: {(SonyCrStatus)st} ({st})");

            st = SonyCrBridgeNative.SonyCr_GetCameraDeviceCount(out var count);
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
            SonyCrBridgeNative.TryReleaseSdk();
        }
    }
}
