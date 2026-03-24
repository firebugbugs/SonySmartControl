using SonySmartControl.Interop;

namespace SonySmartControl.Services.Camera;

/// <summary>
/// 机身拍摄属性写入与监视器显示模式（CrSDK）；由 ViewModel 经构造函数注入，不直接依赖桥接静态入口。
/// </summary>
public interface ICrSdkShootingWriteService
{
    void ApplyMonitorDispMode(byte mode);

    void SetShootingProperty(uint propertyCode, ulong rawValue, CrSdkDataType dataType);
}
