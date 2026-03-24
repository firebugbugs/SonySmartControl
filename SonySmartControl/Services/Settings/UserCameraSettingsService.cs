using System.Text.Json;
using SonySmartControl.Settings;

namespace SonySmartControl.Services.Settings;

/// <summary>
/// 用户相机设置持久化（%LocalAppData%\SonySmartControl），无全局静态门面，符合构造函数注入与可测试替换。
/// </summary>
public sealed class UserCameraSettingsService : IUserCameraSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonySmartControl",
            "camera_user_settings.json");

    public CameraUserSettings Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return new CameraUserSettings();
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<CameraUserSettings>(json, JsonOptions);
            return s ?? new CameraUserSettings();
        }
        catch
        {
            return new CameraUserSettings();
        }
    }

    public void Save(CameraUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 忽略磁盘错误，避免影响 UI
        }
    }
}
