using Avalonia.Controls;

namespace SonySmartControl.Services.Platform;

public sealed class MainWindowShellService : IMainWindowShellService
{
    private readonly ITopLevelProvider _topLevelProvider;
    private Window? _window;

    public MainWindowShellService(ITopLevelProvider topLevelProvider) =>
        _topLevelProvider = topLevelProvider;

    public void Attach(Window window)
    {
        _window = window;
        _topLevelProvider.SetTopLevel(window);
    }

    public void Minimize()
    {
        if (_window != null)
            _window.WindowState = WindowState.Minimized;
    }

    public void ToggleMaximize()
    {
        if (_window == null)
            return;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void RequestClose() => _window?.Close();
}
