using System.Text.Json.Serialization;

namespace SonySmartControl.Interop;

/// <summary>桥接 DLL 中 <c>CrFocusFrameInfo</c> 的 JSON 表示（与 SonyCr_GetLiveViewFocusFramesJsonUtf8 一致）。</summary>
public sealed class CrSdkLiveViewFocusFrameDto
{
    [JsonPropertyName("t")] public uint Type { get; set; }

    [JsonPropertyName("s")] public uint State { get; set; }

    [JsonPropertyName("xn")] public uint XNumerator { get; set; }

    [JsonPropertyName("xd")] public uint XDenominator { get; set; }

    [JsonPropertyName("yn")] public uint YNumerator { get; set; }

    [JsonPropertyName("yd")] public uint YDenominator { get; set; }

    [JsonPropertyName("w")] public uint Width { get; set; }

    [JsonPropertyName("h")] public uint Height { get; set; }
}

/// <summary>归一化到 0..1 的矩形（相对整幅 Live View 图像），用于叠加绘制。</summary>
public sealed class CrSdkLiveViewFocusFrameView
{
    public CrSdkLiveViewFocusFrameView(double left, double top, double width, double height, bool isFocused)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        IsFocused = isFocused;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    /// <summary>与 CrFocusFrameState_Focused（0x0002）一致。</summary>
    public bool IsFocused { get; }
}

/// <summary>解析桥接 JSON 并换算为预览叠加坐标。</summary>
public static class CrSdkLiveViewFocusFrameParser
{
    /// <summary>CrDeviceProperty.h：CrFocusFrameState_Focused = 0x0002。</summary>
    public const uint FocusedState = 0x0002;

    public static bool TryParse(string? json, out List<CrSdkLiveViewFocusFrameView> frames)
    {
        frames = new List<CrSdkLiveViewFocusFrameView>();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        CrSdkLiveViewFocusFrameDto[]? arr;
        try
        {
            arr = System.Text.Json.JsonSerializer.Deserialize<CrSdkLiveViewFocusFrameDto[]>(json);
        }
        catch
        {
            return false;
        }

        if (arr == null || arr.Length == 0)
            return false;

        foreach (var f in arr)
        {
            if (f.XDenominator == 0 || f.YDenominator == 0)
                continue;

            var cx = f.XNumerator / (double)f.XDenominator;
            var cy = f.YNumerator / (double)f.YDenominator;
            var nw = f.Width / (double)f.XDenominator;
            var nh = f.Height / (double)f.YDenominator;

            double left;
            double top;
            double w;
            double h;

            if (f.Width > 0 && f.Height > 0)
            {
                left = cx - nw * 0.5;
                top = cy - nh * 0.5;
                w = nw;
                h = nh;
            }
            else
            {
                var side = 0.11;
                left = cx - side * 0.5;
                top = cy - side * 0.5;
                w = side;
                h = side;
            }

            left = Math.Clamp(left, 0, 1);
            top = Math.Clamp(top, 0, 1);
            w = Math.Clamp(w, 0, 1);
            h = Math.Clamp(h, 0, 1);
            if (left + w > 1)
                w = Math.Max(0, 1 - left);
            if (top + h > 1)
                h = Math.Max(0, 1 - top);

            var isFocused = (f.State & 0xFFFF) == FocusedState;
            frames.Add(new CrSdkLiveViewFocusFrameView(left, top, w, h, isFocused));
        }

        return frames.Count > 0;
    }
}
