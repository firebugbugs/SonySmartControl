using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Behaviors;

/// <summary>同步预览区布局尺寸到 ViewModel（对焦框关闭按钮等布局用）。</summary>
public static class PreviewLayoutSyncBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>(
            "IsLayoutSyncEnabled",
            typeof(PreviewLayoutSyncBehavior));

    static PreviewLayoutSyncBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>(OnIsEnabledChanged);
    }

    public static bool GetIsLayoutSyncEnabled(Border element) => element.GetValue(IsEnabledProperty);

    public static void SetIsLayoutSyncEnabled(Border element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Border border, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
            border.SizeChanged -= Border_OnSizeChanged;
        if (e.NewValue is bool n && n)
            border.SizeChanged += Border_OnSizeChanged;
    }

    private static void Border_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Border b && b.DataContext is MainWindowViewModel vm)
            vm.PreviewLayoutSize = b.Bounds.Size;
    }
}

/// <summary>预览区按下/拖动对焦手势：仅做输入捕获并转发到 ViewModel。</summary>
public static class PreviewFocusGestureBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>(
            "IsPreviewGestureEnabled",
            typeof(PreviewFocusGestureBehavior));

    static PreviewFocusGestureBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>(OnIsEnabledChanged);
    }

    public static bool GetIsPreviewGestureEnabled(Border element) => element.GetValue(IsEnabledProperty);

    public static void SetIsPreviewGestureEnabled(Border element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Border border, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
        {
            border.PointerPressed -= Border_OnPointerPressed;
            border.PointerReleased -= Border_OnPointerReleased;
            border.PointerCaptureLost -= Border_OnPointerCaptureLost;
        }

        if (e.NewValue is bool n && n)
        {
            border.PointerPressed += Border_OnPointerPressed;
            border.PointerReleased += Border_OnPointerReleased;
            border.PointerCaptureLost += Border_OnPointerCaptureLost;
        }
    }

    private static async void Border_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not MainWindowViewModel vm)
            return;
        if (e.Source is Visual vis && vis.FindAncestorOfType<Button>() != null)
            return;
        if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(border).Properties.IsRightButtonPressed)
            return;

        e.Pointer.Capture(border);
        vm.PreviewLayoutSize = border.Bounds.Size;
        var pt = e.GetPosition(border);
        await vm.OnPreviewFocusGesturePressedAsync(pt, border.Bounds.Size).ConfigureAwait(true);
    }

    private static async void Border_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not MainWindowViewModel vm)
            return;
        e.Pointer.Capture(null);
        await vm.OnPreviewFocusGestureReleasedAsync().ConfigureAwait(true);
    }

    private static async void Border_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not MainWindowViewModel vm)
            return;
        await vm.OnPreviewFocusGestureReleasedAsync().ConfigureAwait(true);
    }
}

/// <summary>预览区鼠标滚轮：在 MF（手动对焦）下做相对对焦（上滚=远处，下滚=近处）。</summary>
public static class PreviewRelativeFocusWheelBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Border, bool>(
            "IsRelativeFocusWheelEnabled",
            typeof(PreviewRelativeFocusWheelBehavior));

    static PreviewRelativeFocusWheelBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>(OnIsEnabledChanged);
    }

    public static bool GetIsRelativeFocusWheelEnabled(Border element) => element.GetValue(IsEnabledProperty);

    public static void SetIsRelativeFocusWheelEnabled(Border element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(Border border, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is bool old && old)
            border.PointerWheelChanged -= Border_OnPointerWheelChanged;

        if (e.NewValue is bool n && n)
            border.PointerWheelChanged += Border_OnPointerWheelChanged;
    }

    private static async void Border_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not MainWindowViewModel vm)
            return;

        // 仅处理鼠标滚轮（触控板也可能触发，但我们把它当作同一类输入即可）。
        if (e.Delta.Y == 0)
            return;

        // 防止父容器/滚动控件抢走滚轮事件。
        e.Handled = true;

        // 上滚=远处；下滚=近处
        var deltaY = e.Delta.Y;
        await vm.OnPreviewRelativeFocusWheelAsync(deltaY).ConfigureAwait(true);
    }
}
