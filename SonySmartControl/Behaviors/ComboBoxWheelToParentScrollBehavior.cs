using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace SonySmartControl.Behaviors;

/// <summary>
/// 快门等下拉未展开时，将滚轮事件转为外层 <see cref="ScrollViewer"/> 的纵向滚动（避免误改选中项）。
/// </summary>
public static class ComboBoxWheelToParentScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>(
            "IsWheelToParentScrollEnabled",
            typeof(ComboBoxWheelToParentScrollBehavior));

    static ComboBoxWheelToParentScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<ComboBox>(OnIsEnabledChanged);
    }

    public static bool GetIsWheelToParentScrollEnabled(ComboBox element) =>
        element.GetValue(IsEnabledProperty);

    public static void SetIsWheelToParentScrollEnabled(ComboBox element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ComboBox cb, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
            cb.PointerWheelChanged -= ComboBox_OnPointerWheelChanged;
        if (e.NewValue is bool n && n)
            cb.PointerWheelChanged += ComboBox_OnPointerWheelChanged;
    }

    private static void ComboBox_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ComboBox cb || cb.IsDropDownOpen)
            return;

        var sv = cb.FindAncestorOfType<ScrollViewer>();
        if (sv == null)
        {
            e.Handled = true;
            return;
        }

        var maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        if (maxY <= 0)
        {
            e.Handled = true;
            return;
        }

        const double linePixels = 48.0;
        const double typicalNotch = 120.0;
        var deltaY = -e.Delta.Y / typicalNotch * linePixels;
        if (Math.Abs(deltaY) < 1e-6)
        {
            e.Handled = true;
            return;
        }

        var newY = Math.Clamp(sv.Offset.Y + deltaY, 0, maxY);
        sv.Offset = new Vector(sv.Offset.X, newY);
        e.Handled = true;
    }
}
