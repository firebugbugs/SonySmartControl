using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Behaviors;

/// <summary>
/// 侧栏「对焦」按住：Button 内部会吞掉指针事件，需 AddHandler(..., handledEventsToo: true)。
/// </summary>
public static class FocusHoldButtonBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>(
            "IsFocusHoldEnabled",
            typeof(FocusHoldButtonBehavior));

    static FocusHoldButtonBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>(OnIsEnabledChanged);
    }

    public static bool GetIsFocusHoldEnabled(Button element) => element.GetValue(IsEnabledProperty);

    public static void SetIsFocusHoldEnabled(Button element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Button btn, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
            Unhook(btn);
        if (e.NewValue is bool n && n)
            Hook(btn);
    }

    private static void Hook(Button btn)
    {
        btn.AddHandler(InputElement.PointerPressedEvent, Btn_OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        btn.AddHandler(InputElement.PointerReleasedEvent, Btn_OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        btn.AddHandler(InputElement.PointerCaptureLostEvent, Btn_OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        btn.AddHandler(InputElement.KeyDownEvent, Btn_OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        btn.AddHandler(InputElement.KeyUpEvent, Btn_OnKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void Unhook(Button btn)
    {
        btn.RemoveHandler(InputElement.PointerPressedEvent, Btn_OnPointerPressed);
        btn.RemoveHandler(InputElement.PointerReleasedEvent, Btn_OnPointerReleased);
        btn.RemoveHandler(InputElement.PointerCaptureLostEvent, Btn_OnPointerCaptureLost);
        btn.RemoveHandler(InputElement.KeyDownEvent, Btn_OnKeyDown);
        btn.RemoveHandler(InputElement.KeyUpEvent, Btn_OnKeyUp);
    }

    private static async void Btn_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(btn).Properties.IsRightButtonPressed)
            return;

        e.Pointer.Capture(btn);
        await vm.FocusPointerPressedAsync().ConfigureAwait(true);
    }

    private static async void Btn_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        e.Pointer.Capture(null);
        await vm.FocusPointerReleasedAsync().ConfigureAwait(true);
    }

    private static async void Btn_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        await vm.FocusPointerReleasedAsync().ConfigureAwait(true);
    }

    private static async void Btn_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        await vm.FocusPointerPressedAsync().ConfigureAwait(true);
    }

    private static async void Btn_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        await vm.FocusPointerReleasedAsync().ConfigureAwait(true);
    }
}
