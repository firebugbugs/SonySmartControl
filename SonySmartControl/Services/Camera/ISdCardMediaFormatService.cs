namespace SonySmartControl.Services.Camera;

/// <summary>通过 CrSDK 对相机存储卡槽执行格式化（原生同步命令在实现内放入线程池）。</summary>
public interface ISdCardMediaFormatService
{
    /// <summary>
    /// 依次尝试 SLOT1/SLOT2；若至少一个成功可能附带部分槽错误说明。
    /// </summary>
    Task<SdCardFormatResult> FormatAllSlotsAsync(CancellationToken cancellationToken = default);
}

/// <param name="PartialErrorText">有槽位失败但其它槽成功时的合并错误文案；全成功时为 null。</param>
public readonly record struct SdCardFormatResult(string? PartialErrorText);
