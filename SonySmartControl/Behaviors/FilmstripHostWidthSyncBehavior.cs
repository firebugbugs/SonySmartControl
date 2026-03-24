using Avalonia;
using Avalonia.Controls;
using SonySmartControl.Controls;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Behaviors;

/// <summary>胶片宿主宽度变化时通知 ViewModel，用于动态计算可显示缩略图数量。</summary>
public static class FilmstripHostWidthSyncBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<FilmstripPhotosHost, bool>(
            "IsWidthSyncEnabled",
            typeof(FilmstripHostWidthSyncBehavior));

    static FilmstripHostWidthSyncBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<FilmstripPhotosHost>(OnIsEnabledChanged);
    }

    public static bool GetIsWidthSyncEnabled(FilmstripPhotosHost element) =>
        element.GetValue(IsEnabledProperty);

    public static void SetIsWidthSyncEnabled(FilmstripPhotosHost element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(FilmstripPhotosHost host, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
            host.SizeChanged -= Host_OnSizeChanged;
        if (e.NewValue is bool n && n)
            host.SizeChanged += Host_OnSizeChanged;
    }

    private static void Host_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is FilmstripPhotosHost h && h.DataContext is MainWindowViewModel vm)
            vm.NotifyFilmstripHostWidthChanged(e.NewSize.Width);
    }
}
