using System.Text.Json;
using System.Text.Json.Serialization;
using SonySmartControl.Interop;

namespace SonySmartControl.Settings;

/// <summary>
/// 可下发到相机的拍摄设置快照（存入配置档，用于“一键应用到相机”）。
/// 仅保存可设置项的 raw value + data type；不保证所有机型都支持全部项，下发时会自动跳过失败项。
/// </summary>
public sealed class CameraShootingProfile
{
    public List<CameraShootingPropertyValue> Items { get; set; } = [];

    /// <summary>是否熄屏（对应 Monitor Disp Mode）。null 表示不控制该项。</summary>
    public bool? CameraScreenPowerOn { get; set; }

    /// <summary>将当前轮询状态转换为可下发快照（仅收集 writable 的属性）。</summary>
    public static CameraShootingProfile FromState(CrSdkShootingState s)
    {
        var items = new List<CameraShootingPropertyValue>();
        Add(items, CrSdkDevicePropertyCodes.ExposureProgramMode, s.ExposureProgram);
        Add(items, CrSdkDevicePropertyCodes.FNumber, s.FNumber);
        Add(items, CrSdkDevicePropertyCodes.ShutterSpeed, s.ShutterSpeed);
        Add(items, CrSdkDevicePropertyCodes.ShutterType, s.ShutterType);
        Add(items, CrSdkDevicePropertyCodes.IsoSensitivity, s.Iso);
        Add(items, CrSdkDevicePropertyCodes.ExposureBiasCompensation, s.ExposureBias);
        Add(items, CrSdkDevicePropertyCodes.FocusMode, s.FocusMode);
        Add(items, CrSdkDevicePropertyCodes.DriveMode, s.DriveMode);
        Add(items, CrSdkDevicePropertyCodes.FlashMode, s.FlashMode);
        Add(items, CrSdkDevicePropertyCodes.FlashCompensation, s.FlashCompensation);
        Add(items, CrSdkDevicePropertyCodes.StillImageQuality, s.ImageQuality);
        Add(items, CrSdkDevicePropertyCodes.ImageSize, s.ImageSize);
        Add(items, CrSdkDevicePropertyCodes.AspectRatio, s.AspectRatio);
        Add(items, CrSdkDevicePropertyCodes.RawFileCompressionType, s.RawCompressionType);

        var p = new CameraShootingProfile { Items = items };
        if (s.DispMode != null)
        {
            var v = (byte)(s.DispMode.Value & 0xFF);
            p.CameraScreenPowerOn = v != (byte)CrSdkDispMode.MonitorOff;
        }

        return p;
    }

    private static void Add(List<CameraShootingPropertyValue> items, uint code, CrSdkShootingPropertySnapshot? snap)
    {
        if (snap == null)
            return;
        if (!snap.Writable)
            return;
        items.Add(new CameraShootingPropertyValue
        {
            Code = code,
            Value = snap.Value,
            DataType = snap.SetDataType,
        });
    }

    public static string ToJson(CameraShootingProfile p)
    {
        return JsonSerializer.Serialize(p, JsonOptions);
    }

    public static bool TryParse(string? json, out CameraShootingProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            profile = JsonSerializer.Deserialize<CameraShootingProfile>(json, JsonOptions);
            return profile != null;
        }
        catch
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class CameraShootingPropertyValue
{
    public uint Code { get; set; }
    public ulong Value { get; set; }
    public CrSdkDataType DataType { get; set; }
}

