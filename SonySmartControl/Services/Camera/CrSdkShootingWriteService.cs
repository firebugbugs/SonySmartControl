using SonySmartControl.Interop;

namespace SonySmartControl.Services.Camera;

/// <summary>将 <see cref="SonyCrSdk"/> 的写入类调用收敛到 Infrastructure 侧实现（与 .cursorrules 中「SDK 封装」一致）。</summary>
public sealed class CrSdkShootingWriteService : ICrSdkShootingWriteService
{
    public void ApplyMonitorDispMode(byte mode) => SonyCrSdk.ApplyMonitorDispMode(mode);

    public void SetShootingProperty(uint propertyCode, ulong rawValue, CrSdkDataType dataType) =>
        SonyCrSdk.SetShootingProperty(propertyCode, rawValue, dataType);
}
