using SonySmartControl.Settings;

namespace SonySmartControl.Services.Settings;

/// <summary>用户相机相关本地设置的加载与持久化。</summary>
public interface IUserCameraSettingsService
{
    CameraUserSettings Load();

    void Save(CameraUserSettings settings);
}
