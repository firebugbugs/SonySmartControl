namespace SonySmartControl.Settings;

/// <summary>保存目录、辅助显示等用户偏好（持久化到本地 JSON）。</summary>
public sealed class CameraUserSettings
{
    /// <summary>遥控拍摄时保存到 PC 的目录（绝对路径）。</summary>
    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SonySmartControl");

    /// <summary>0=仅 JPEG，1=仅 RAW，2=RAW+JPEG（与机身 CrFileType 对应）。</summary>
    public int CaptureFormatIndex { get; set; }

    /// <summary>文件名前缀（可为空）。</summary>
    public string FileNamePrefix { get; set; } = "DSC";

    /// <summary>是否显示预览区亮度直方图；未写入过配置文件时为 null，由界面按默认「开」处理。</summary>
    public bool? ShowHistogram { get; set; }

    /// <summary>构图辅助线：0=无，1=三分法，2=十字对准线，3=对角线，4=安全区。</summary>
    public int GuideOverlayIndex { get; set; }

    /// <summary>延时摄影保存目录（绝对路径）。</summary>
    public string TimelapseSaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SonySmartControl", "Timelapse");

    /// <summary>延时摄影间隔（秒），C# 定时器实现；最低为 2 秒。</summary>
    public int TimelapseIntervalSeconds { get; set; } = 5;

    /// <summary>延时摄影总张数（0=无限制）；C# 定时器实现。</summary>
    public int TimelapseTargetFrames { get; set; }
}
