using System.Text.Json;

namespace SonySmartControl.Settings;

/// <summary>将 <see cref="CameraUserSettings"/> 读写至本地（%LocalAppData%\SonySmartControl）。</summary>
public static class UserCameraSettingsStore
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

    public static CameraUserSettings Load()
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

    public static void Save(CameraUserSettings settings)
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
