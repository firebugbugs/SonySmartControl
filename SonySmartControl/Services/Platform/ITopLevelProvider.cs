using Avalonia.Controls;

namespace SonySmartControl.Services.Platform;

/// <summary>
/// 提供当前主窗口对应的 <see cref="TopLevel"/>，供文件夹选择器等需要 <see cref="TopLevel.StorageProvider"/> 的服务使用。
/// </summary>
public interface ITopLevelProvider
{
    void SetTopLevel(TopLevel? topLevel);

    TopLevel? GetTopLevel();
}
