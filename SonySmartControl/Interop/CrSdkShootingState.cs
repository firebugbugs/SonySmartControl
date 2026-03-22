using System.Text.Json;

namespace SonySmartControl.Interop;

/// <summary>解析 <c>SonyCr_GetShootingStateJsonUtf8</c> 返回的 JSON。</summary>
public sealed class CrSdkShootingState
{
    public bool IsVideoMode { get; init; }

    public CrSdkShootingPropertySnapshot? ExposureProgram { get; init; }

    public CrSdkShootingPropertySnapshot? FNumber { get; init; }

    public CrSdkShootingPropertySnapshot? ShutterSpeed { get; init; }

    public CrSdkShootingPropertySnapshot? Iso { get; init; }

    /// <summary>曝光补偿（JSON 键 <c>ev</c>）。</summary>
    public CrSdkShootingPropertySnapshot? ExposureBias { get; init; }

    /// <summary>对焦模式（JSON 键 <c>fm</c>）。</summary>
    public CrSdkShootingPropertySnapshot? FocusMode { get; init; }

    /// <summary>遥控触摸/触碰对焦启用（JSON 键 <c>rtouch</c>，0/1）。</summary>
    public CrSdkShootingPropertySnapshot? RemoteTouchEnable { get; init; }

    /// <summary>背屏显示模式（JSON 键 <c>dm</c>，含 Monitor Off）。</summary>
    public CrSdkShootingPropertySnapshot? DispMode { get; init; }

    /// <summary>快门类型机械/电子/自动（JSON 键 <c>st</c>）。</summary>
    public CrSdkShootingPropertySnapshot? ShutterType { get; init; }

    /// <summary>静态拍照驱动模式：单张 / 连拍 / 延时等（JSON 键 <c>drv</c>，CrDeviceProperty_DriveMode）。</summary>
    public CrSdkShootingPropertySnapshot? DriveMode { get; init; }

    public static bool TryParse(string? json, out CrSdkShootingState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            state = new CrSdkShootingState
            {
                IsVideoMode = root.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.True,
                ExposureProgram = ReadProp(root, "ep"),
                FNumber = ReadProp(root, "fn"),
                ShutterSpeed = ReadProp(root, "ss"),
                Iso = ReadProp(root, "iso"),
                ExposureBias = ReadProp(root, "ev"),
                FocusMode = ReadProp(root, "fm"),
                RemoteTouchEnable = ReadProp(root, "rtouch"),
                DispMode = ReadProp(root, "dm"),
                ShutterType = ReadProp(root, "st"),
                DriveMode = ReadProp(root, "drv"),
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CrSdkShootingPropertySnapshot? ReadProp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
            return null;

        ulong[] cand;
        if (!el.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.Array)
            cand = [];
        else
        {
            var list = new List<ulong>();
            foreach (var x in c.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.Number)
                    list.Add(x.GetUInt64());
            }

            cand = list.ToArray();
        }

        return new CrSdkShootingPropertySnapshot
        {
            Value = el.TryGetProperty("v", out var v) ? v.GetUInt64() : 0,
            Writable = el.TryGetProperty("w", out var w) && w.GetInt32() != 0,
            Gettable = ReadSdkBoolOrMissingTrue(el, "g"),
            SetDataType = el.TryGetProperty("set", out var s) ? (CrSdkDataType)s.GetUInt32() : CrSdkDataType.Undefined,
            Candidates = cand,
        };
    }

    /// <summary>
    /// 与 SonyCrBridge 一致：<c>g</c>/<c>w</c> 为数字 0/1；缺省键时与旧逻辑一致（仅 g：缺省视为可读）。
    /// </summary>
    private static bool ReadSdkBoolOrMissingTrue(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var x))
            return true;
        return x.ValueKind switch
        {
            JsonValueKind.Number => x.GetInt32() != 0,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            _ => true,
        };
    }
}
