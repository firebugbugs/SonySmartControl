namespace SonySmartControl.Services.Platform;

/// <summary>系统异步文件夹选择（由 Avalonia 存储提供程序实现）。</summary>
public interface IFolderPickerService
{
    /// <summary>弹出文件夹选择；用户取消时返回 null。</summary>
    Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default);
}
