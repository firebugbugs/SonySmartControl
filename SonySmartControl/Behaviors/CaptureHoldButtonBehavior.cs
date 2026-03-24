using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Behaviors;

/// <summary>
/// 侧栏「拍照」按住/连拍：与 ViewModel 的半按、全按序列对齐；须 await 按下任务再松手以避免竞态。
/// </summary>
public static class CaptureHoldButtonBehavior
{
    private static readonly ConditionalWeakTable<Button, CapturePressState> States = new();

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>(
            "IsCaptureHoldEnabled",
            typeof(CaptureHoldButtonBehavior));

    static CaptureHoldButtonBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>(OnIsEnabledChanged);
    }

    public static bool GetIsCaptureHoldEnabled(Button element) => element.GetValue(IsEnabledProperty);

    public static void SetIsCaptureHoldEnabled(Button element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static CapturePressState GetState(Button b) => States.GetValue(b, _ => new CapturePressState());

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
        var state = GetState(btn);
        var pressTask = vm.CapturePointerPressedAsync();
        state.PressInFlight = pressTask;
        try
        {
            await pressTask.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(state.PressInFlight, pressTask))
                state.PressInFlight = null;
        }
    }

    private static async void Btn_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        var state = GetState(btn);
        var pressTask = state.PressInFlight;
        if (pressTask != null)
            await pressTask.ConfigureAwait(true);
        await vm.CapturePointerReleasedAsync().ConfigureAwait(true);
        e.Pointer.Capture(null);
    }

    private static async void Btn_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        var state = GetState(btn);
        var pressTask = state.PressInFlight;
        if (pressTask != null)
            await pressTask.ConfigureAwait(true);
        await vm.CapturePointerCancelledAsync().ConfigureAwait(true);
    }

    private static async void Btn_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        var state = GetState(btn);
        var pressTask = vm.CapturePointerPressedAsync();
        state.PressInFlight = pressTask;
        try
        {
            await pressTask.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(state.PressInFlight, pressTask))
                state.PressInFlight = null;
        }
    }

    private static async void Btn_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        var state = GetState(btn);
        var pressTask = state.PressInFlight;
        if (pressTask != null)
            await pressTask.ConfigureAwait(true);
        await vm.CapturePointerReleasedAsync().ConfigureAwait(true);
    }

    private sealed class CapturePressState
    {
        public Task? PressInFlight;
    }
}
