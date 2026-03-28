using Avalonia.Controls;

namespace SonySmartControl.Services.Platform;

/// <summary>
/// 主窗口外壳操作（最小化、最大化、请求关闭），由主窗口在 Loaded 时 <see cref="Attach"/> 后供 ViewModel 调用。
/// </summary>
public interface IMainWindowShellService
{
    void Attach(Window window);

    void Minimize();

    void ToggleMaximize();

    void RequestClose();
}
