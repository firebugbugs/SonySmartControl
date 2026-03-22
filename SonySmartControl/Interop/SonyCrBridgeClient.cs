namespace SonySmartControl.Interop;

/// <summary>
/// 对 <see cref="SonyCrBridgeNative"/> 的托管封装：初始化、枚举相机型号，便于 ViewModel / 服务层调用。
/// 连接 / Live View 需在 C++ 桥中实现 <see cref="SonyCrBridgeNative.SonyCr_ConnectRemoteByIndex"/> 等后再接业务。
/// </summary>
public sealed class SonyCrBridgeClient : IDisposable
{
    private bool _initialized;
    private bool _disposed;

    public bool IsInitialized => _initialized;

    /// <summary>调用 <c>SonyCr_Init</c>；失败时抛出 <see cref="InvalidOperationException"/>。</summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return;

        var st = SonyCrBridgeNative.SonyCr_Init();
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SonyCr_Init 失败: {(SonyCrStatus)st} ({st})");

        _initialized = true;
    }

    /// <summary>刷新枚举并返回当前检测到的相机型号列表（UTF-8）。</summary>
    public IReadOnlyList<string> EnumerateCameraModels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("请先调用 Initialize。");

        var st = SonyCrBridgeNative.SonyCr_EnumCameraDevicesRefresh();
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SonyCr_EnumCameraDevicesRefresh 失败: {(SonyCrStatus)st} ({st})");

        st = SonyCrBridgeNative.SonyCr_GetCameraDeviceCount(out var count);
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SonyCr_GetCameraDeviceCount 失败: {(SonyCrStatus)st} ({st})");

        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var model = SonyCrBridgeNative.GetCameraModelUtf8(i);
            list.Add(model ?? $"#{i}");
        }

        return list;
    }

    /// <summary>打包版本号，语义同 SCRSDK::GetSDKVersion。</summary>
    public uint GetSdkVersionPacked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("请先调用 Initialize。");

        var st = SonyCrBridgeNative.SonyCr_GetSdkVersion(out var v);
        if (st != (int)SonyCrStatus.Ok)
            throw new InvalidOperationException($"SonyCr_GetSdkVersion 失败: {(SonyCrStatus)st} ({st})");

        return v;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_initialized)
            SonyCrBridgeNative.SonyCr_Release();

        _initialized = false;
        _disposed = true;
    }
}
