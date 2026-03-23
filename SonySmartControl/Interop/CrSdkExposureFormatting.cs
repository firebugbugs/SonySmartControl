using System.Globalization;

namespace SonySmartControl.Interop;

/// <summary>与 RemoteCli PropertyValueTable 对齐的显示字符串（简化移植）。</summary>
public static class CrSdkExposureFormatting
{
    private const uint CrFnumberUnknown = 0xFFFE;
    private const uint CrFnumberNothing = 0xFFFF;
    private const uint CrShutterSpeedBulb = 0x00000000;
    private const uint CrShutterSpeedNothing = 0xFFFFFFFF;
    private const uint CrIsoAuto = 0xFFFFFF;
    private const uint CrIsoMultiFrameNr = 0x01;
    private const uint CrIsoMultiFrameNrHigh = 0x02;

    public static string FormatFNumber(ushort fNumber)
    {
        if (fNumber == 0 || fNumber == CrFnumberUnknown)
            return "--";
        if (fNumber == CrFnumberNothing)
            return string.Empty;

        // 机身以「光圈值×100」的整数上报；用 double 做除法再 ToString 会出现 2.699999…（二进制浮点误差）。
        var mod = fNumber % 100;
        if (mod > 0)
        {
            var v = Math.Round((decimal)fNumber / 100m, 1, MidpointRounding.AwayFromZero);
            return "F" + v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        return "F" + (fNumber / 100).ToString(CultureInfo.InvariantCulture);
    }

    public static string FormatIso(uint iso)
    {
        var isoMode = (iso >> 24) & 0x0F;
        var isoValue = iso & 0x00FFFFFF;

        var prefix = isoMode switch
        {
            CrIsoMultiFrameNr => "Multi Frame NR ",
            CrIsoMultiFrameNrHigh => "Multi Frame NR High ",
            _ => "",
        };

        if (isoValue == CrIsoAuto)
            return prefix + "ISO AUTO";
        return prefix + "ISO " + isoValue;
    }

    /// <summary>CrShutterType（UInt8）：自动 / 机械 / 电子。</summary>
    public static string FormatShutterType(ulong raw)
    {
        var b = (byte)(raw & 0xFF);
        return b switch
        {
            (byte)CrSdkShutterType.Auto => "自动",
            (byte)CrSdkShutterType.MechanicalShutter => "机械快门",
            (byte)CrSdkShutterType.ElectronicShutter => "电子快门",
            _ => "0x" + b.ToString("X2", CultureInfo.InvariantCulture),
        };
    }

    public static string FormatShutterSpeed(uint shutterSpeed)
    {
        var numerator = (ushort)((shutterSpeed >> 16) & 0xFFFF);
        var denominator = (ushort)(shutterSpeed & 0xFFFF);

        if (shutterSpeed == CrShutterSpeedBulb)
            return "Bulb";
        if (shutterSpeed == CrShutterSpeedNothing)
            return "—";

        if (numerator == 1)
            return numerator + "/" + denominator;
        if (denominator != 0 && numerator % denominator == 0)
            return (numerator / denominator) + "\"";

        if (denominator != 0)
        {
            var div = numerator / denominator;
            var rem = numerator % denominator;
            return div + "." + rem + "\"";
        }

        return "0x" + shutterSpeed.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>CrSDK：曝光补偿为「EV×1000」的有符号量，以 UInt16 位型式传递（负补偿为高位补码）。</summary>
    public static string FormatExposureBias(ulong raw)
    {
        var u = (ushort)(raw & 0xFFFF);
        var milli = unchecked((short)u);
        if (milli == 0)
            return "0 EV";
        var ev = milli / 1000.0;
        var s = (ev >= 0 ? "+" : "") + ev.ToString("0.0", CultureInfo.InvariantCulture);
        return s + " EV";
    }

    /// <summary>CrFocusMode（UInt16）；中文名便于用户理解。</summary>
    public static string FormatFocusMode(ulong raw)
    {
        var m = (ushort)(raw & 0xFFFF);
        return m switch
        {
            0x0001 => "手动对焦 (MF)",
            0x0002 => "单次自动对焦 (AF-S)",
            0x0003 => "连续自动对焦 (AF-C)",
            0x0004 => "自动对焦 (AF-A)",
            0x0005 => "广域/跟踪对焦 (AF-D)",
            0x0006 => "直接手动对焦 (DMF)",
            0x0007 => "电动变焦预设对焦 (PF)",
            _ => "0x" + m.ToString("X4", CultureInfo.InvariantCulture),
        };
    }

    public static string FormatExposureProgram(uint ep) =>
        ep switch
        {
            0x00000001 => "M",
            0x00000002 => "P",
            0x00000003 => "A",
            0x00000004 => "S",
            0x00008000 => "Auto",
            0x00008050 => "Movie P",
            0x00008051 => "Movie A",
            0x00008052 => "Movie S",
            0x00008053 => "Movie M",
            0x00008054 => "Movie Auto",
            0x00008055 => "Movie F",
            0x00008088 => "MOVIE",
            0x00008089 => "STILL",
            _ => "0x" + ep.ToString("X8", CultureInfo.InvariantCulture),
        };

    /// <summary>CrFlashMode（UInt16）。</summary>
    public static string FormatFlashMode(ulong raw)
    {
        var m = (ushort)(raw & 0xFFFF);
        return m switch
        {
            0x0001 => "自动",
            0x0002 => "关闭",
            0x0003 => "强制闪光",
            0x0004 => "外部同步",
            0x0005 => "慢速同步",
            0x0006 => "后帘同步",
            _ => "0x" + m.ToString("X4", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>CrFlashCompensation（EV×1000，UInt16 补码）。</summary>
    public static string FormatFlashCompensation(ulong raw) => FormatExposureBias(raw);

    /// <summary>
    /// 侧栏曝光模式下拉：仅保留「照片」常用项，去掉摄像/视频相关与未知 <c>0x……</c> 占位。
    /// </summary>
    public static bool IsStillPhotoExposureProgramListItem(uint ep)
    {
        var label = FormatExposureProgram(ep);
        if (label.StartsWith("0x", StringComparison.Ordinal))
            return false;
        if (label.StartsWith("Movie ", StringComparison.Ordinal) || label is "MOVIE")
            return false;
        return true;
    }
}
