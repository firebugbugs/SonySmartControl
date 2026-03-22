namespace SonySmartControl.Interop;

/// <summary>静态拍照驱动模式（CrDeviceProperty_DriveMode / CrDriveMode），用于单张 / 连拍 / 延时分类。</summary>
public enum CrSdkShootingDriveCategoryKind
{
    /// <summary>CrDrive_Single（0x00000001）。</summary>
    Single,

    /// <summary>连拍 Hi/Lo/Mid 及单拍连发等（候选列表内筛选）。</summary>
    Burst,

    /// <summary>延时自拍 2s/5s/10s（CrDrive_Timer_*）。</summary>
    SelfTimer,

    /// <summary>包围、对焦包围、间隔等本 UI 未单独列出。</summary>
    Other,
}

/// <summary>与 RemoteCli <c>CrDriveMode</c> 对齐的辅助方法（显示名 + 候选筛选）。</summary>
public static class CrSdkShootingDriveMode
{
    /// <summary>CrDrive_Single。</summary>
    public const ulong CrDriveSingle = 0x00000001;

    /// <summary>CrDrive_Timer_2s / 5s / 10s 连续取值范围（含）。</summary>
    public static bool IsSelfTimerDrive(ulong v)
    {
        var x = (uint)v;
        return x >= 0x00030001 && x <= 0x00030003;
    }

    /// <summary>连拍速度：连续拍摄族 + 单拍连发族（与 SDK 枚举块一致）。</summary>
    public static bool IsBurstSpeedDrive(ulong v)
    {
        var x = (uint)v;
        if (x == (uint)CrDriveSingle)
            return false;
        if (IsSelfTimerDrive(v))
            return false;
        // CrDrive_Continuous_Hi .. CrDrive_Continuous_Lo_Live
        if (x >= 0x00010001 && x <= 0x00010009)
            return true;
        // CrDrive_SingleBurstShooting_lo .. hi
        if (x >= 0x00011001 && x <= 0x00011003)
            return true;
        return false;
    }

    public static CrSdkShootingDriveCategoryKind Classify(ulong v)
    {
        if (v == CrDriveSingle)
            return CrSdkShootingDriveCategoryKind.Single;
        if (IsSelfTimerDrive(v))
            return CrSdkShootingDriveCategoryKind.SelfTimer;
        if (IsBurstSpeedDrive(v))
            return CrSdkShootingDriveCategoryKind.Burst;
        return CrSdkShootingDriveCategoryKind.Other;
    }

    public static ulong[] FilterCandidates(ulong[]? candidates, CrSdkShootingDriveCategoryKind cat)
    {
        if (candidates == null || candidates.Length == 0)
            return [];

        IEnumerable<ulong> q = cat switch
        {
            CrSdkShootingDriveCategoryKind.Single => candidates.Where(v => v == CrDriveSingle),
            CrSdkShootingDriveCategoryKind.Burst => candidates.Where(IsBurstSpeedDrive),
            CrSdkShootingDriveCategoryKind.SelfTimer => candidates.Where(IsSelfTimerDrive),
            _ => [],
        };

        return q.Distinct().OrderBy(v => v).ToArray();
    }

    /// <summary>中文简短标签；未知值回退为十六进制。</summary>
    public static string FormatDriveMode(ulong v)
    {
        var x = (uint)v;
        return x switch
        {
            0x00000001 => "单张",
            0x00010001 => "连拍 Hi",
            0x00010002 => "连拍 Hi+",
            0x00010003 => "连拍 Hi Live",
            0x00010004 => "连拍 Lo",
            0x00010005 => "连拍",
            0x00010006 => "连拍 速度优先",
            0x00010007 => "连拍 Mid",
            0x00010008 => "连拍 Mid Live",
            0x00010009 => "连拍 Lo Live",
            0x00011001 => "单拍连发 Lo",
            0x00011002 => "单拍连发 Mid",
            0x00011003 => "单拍连发 Hi",
            0x00030001 => "延时 2 秒",
            0x00030002 => "延时 5 秒",
            0x00030003 => "延时 10 秒",
            _ => $"0x{x:X}",
        };
    }
}
