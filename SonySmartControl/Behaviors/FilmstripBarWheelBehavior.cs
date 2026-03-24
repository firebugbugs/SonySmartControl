using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SonySmartControl.Controls;

namespace SonySmartControl.Behaviors;

/// <summary>将纵向滚轮与触控板横向分量映射为底部胶片条横向滚动。</summary>
public static class FilmstripBarWheelBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>(
            "IsFilmstripWheelEnabled",
            typeof(FilmstripBarWheelBehavior));

    static FilmstripBarWheelBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(OnIsEnabledChanged);
    }

    public static bool GetIsFilmstripWheelEnabled(Control element) => element.GetValue(IsEnabledProperty);

    public static void SetIsFilmstripWheelEnabled(Control element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
            control.RemoveHandler(InputElement.PointerWheelChangedEvent, Bar_OnPointerWheelChanged);
        if (e.NewValue is bool n && n)
            control.AddHandler(
                InputElement.PointerWheelChangedEvent,
                Bar_OnPointerWheelChanged,
                Avalonia.Interactivity.RoutingStrategies.Tunnel,
                handledEventsToo: true);
    }

    private static void Bar_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Control host)
            return;

        var filmstrip = host.GetVisualDescendants().OfType<FilmstripPhotosHost>().FirstOrDefault();
        var sv = filmstrip?.PhotosScrollViewer;
        if (sv == null)
            return;

        var d = e.Delta;
        double horizontal;
        if (Math.Abs(d.X) >= Math.Abs(d.Y))
            horizontal = d.X;
        else
            horizontal = -d.Y;

        if (Math.Abs(horizontal) < 1e-6)
            return;
        var maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        if (maxX <= 0)
            return;

        const double linePixels = 48.0;
        const double typicalNotch = 120.0;
        var deltaX = horizontal / typicalNotch * linePixels;
        var newX = Math.Clamp(sv.Offset.X + deltaX, 0, maxX);
        sv.Offset = new Vector(newX, sv.Offset.Y);
        e.Handled = true;
    }
}
