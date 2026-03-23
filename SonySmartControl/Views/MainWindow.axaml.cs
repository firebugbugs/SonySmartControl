using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using SonySmartControl.Controls;
using SonySmartControl.ViewModels;
using RoutingStrategies = Avalonia.Interactivity.RoutingStrategies;

namespace SonySmartControl.Views;

public partial class MainWindow : Window
{
    /// <summary>为 true 时表示资源已释放，允许真正关闭窗口（避免首次 Closing 在 await 返回前就结束了进程）。</summary>
    private bool _allowWindowClose;

    private bool _focusHoldHandlersHooked;
    private bool _captureHoldHandlersHooked;

    private static readonly BoxShadows CardRestoredShadow = BoxShadows.Parse("0 12 40 0 #28000000");

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosingAsync;
    }

    /// <summary>快门速度/快门类型下拉：未展开时吞掉滚轮，避免误改。</summary>
    private void ShutterCombo_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is ComboBox cb && !cb.IsDropDownOpen)
            e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            SyncChromeWindowState();
    }

    private void SyncChromeWindowState()
    {
        if (CardRoot == null)
            return;
        var max = WindowState == WindowState.Maximized;
        CardRoot.CornerRadius = max ? new CornerRadius(0) : new CornerRadius(14);
        CardRoot.BoxShadow = max ? default : CardRestoredShadow;
        if (MaximizeGlyph != null)
            MaximizeGlyph.IsVisible = !max;
        if (RestoreGlyph != null)
            RestoreGlyph.IsVisible = max;

        var edge = max ? 0 : 6;
        if (OuterChromeGrid != null)
        {
            OuterChromeGrid.RowDefinitions[0].Height = new GridLength(edge);
            OuterChromeGrid.RowDefinitions[3].Height = new GridLength(edge);
            OuterChromeGrid.ColumnDefinitions[0].Width = new GridLength(edge);
            OuterChromeGrid.ColumnDefinitions[2].Width = new GridLength(edge);
        }

        var showEdges = !max;
        if (ResizeNw != null) ResizeNw.IsVisible = showEdges;
        if (ResizeN != null) ResizeN.IsVisible = showEdges;
        if (ResizeNe != null) ResizeNe.IsVisible = showEdges;
        if (ResizeW != null) ResizeW.IsVisible = showEdges;
        if (ResizeE != null) ResizeE.IsVisible = showEdges;
        if (ResizeSw != null) ResizeSw.IsVisible = showEdges;
        if (ResizeS != null) ResizeS.IsVisible = showEdges;
        if (ResizeSe != null) ResizeSe.IsVisible = showEdges;
    }

    private void MainWindow_OnLoaded(object? sender, RoutedEventArgs e)
    {
        SyncChromeWindowState();
        HookFocusHoldButtonEvents();
        HookCaptureHoldButtonEvents();

        // 隧道阶段拦截滚轮，转成横向滚动（与横向滚动条联动；避免仅纵向分量被忽略）
        if (RecentFilmstripBar != null)
        {
            RecentFilmstripBar.AddHandler(
                InputElement.PointerWheelChangedEvent,
                RecentFilmstripBar_OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }

        if (DataContext is not MainWindowViewModel vm)
            return;
        if (PreviewBorder != null)
            vm.PreviewLayoutSize = PreviewBorder.Bounds.Size;
        if (FilmstripPhotos != null && FilmstripPhotos.Bounds.Width > 0)
            vm.NotifyFilmstripHostWidthChanged(FilmstripPhotos.Bounds.Width);
        vm.PickSaveFolderAsync = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择遥控拍摄保存目录",
                AllowMultiple = false,
            }).ConfigureAwait(false);
            if (folders.Count == 0)
                return null;
            return folders[0].TryGetLocalPath();
        };

        vm.PickTimelapseSaveFolderAsync = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择延时摄影保存目录",
                AllowMultiple = false,
            }).ConfigureAwait(false);
            if (folders.Count == 0)
                return null;
            return folders[0].TryGetLocalPath();
        };
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Left && e.Key != Key.Right)
            return;
        if (FocusManager?.GetFocusedElement() is TextBox or ComboBox)
            return;
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (vm.IsViewingLiveMonitor || string.IsNullOrEmpty(vm.ViewingRecentPhotoPath))
            return;
        e.Handled = true;
        var delta = e.Key == Key.Left ? -1 : 1;
        _ = vm.NavigateRecentGalleryAsync(delta);
    }

    private static bool IsLeftButtonPressed(PointerPressedEventArgs e, Visual relativeTo) =>
        e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed;

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButtonPressed(e, this))
            return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void ChromeMinimize_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ChromeMaximize_OnClick(object? sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void ChromeClose_OnClick(object? sender, RoutedEventArgs e) =>
        Close();

    private void ResizeNw_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.NorthWest, e);
    }

    private void ResizeN_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.North, e);
    }

    private void ResizeNe_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.NorthEast, e);
    }

    private void ResizeW_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.West, e);
    }

    private void ResizeE_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.East, e);
    }

    private void ResizeSw_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.SouthWest, e);
    }

    private void ResizeS_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.South, e);
    }

    private void ResizeSe_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual v || !IsLeftButtonPressed(e, v))
            return;
        BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    /// <summary>近期图片条：纵向滚轮与触控板横向分量均驱动横向 ScrollViewer。</summary>
    private void RecentFilmstripBar_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var sv = FilmstripPhotos?.PhotosScrollViewer;
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

        // Windows 常见一档 ±120；换算为约 48px，与列表「一行」接近
        const double linePixels = 48.0;
        const double typicalNotch = 120.0;
        var deltaX = horizontal / typicalNotch * linePixels;
        var newX = Math.Clamp(sv.Offset.X + deltaX, 0, maxX);
        sv.Offset = new Vector(newX, sv.Offset.Y);
        e.Handled = true;
    }

    /// <summary>
    /// Button 内部会将指针事件标为 Handled，普通 += 可能收不到；用 AddHandler(..., handledEventsToo: true)。
    /// </summary>
    private void HookFocusHoldButtonEvents()
    {
        if (_focusHoldHandlersHooked || FocusHoldButton == null)
            return;
        _focusHoldHandlersHooked = true;

        FocusHoldButton.AddHandler(InputElement.PointerPressedEvent, FocusHoldButton_OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        FocusHoldButton.AddHandler(InputElement.PointerReleasedEvent, FocusHoldButton_OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        FocusHoldButton.AddHandler(InputElement.PointerCaptureLostEvent, FocusHoldButton_OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        FocusHoldButton.AddHandler(InputElement.KeyDownEvent, FocusHoldButton_OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        FocusHoldButton.AddHandler(InputElement.KeyUpEvent, FocusHoldButton_OnKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void HookCaptureHoldButtonEvents()
    {
        if (_captureHoldHandlersHooked || CaptureHoldButton == null)
            return;
        _captureHoldHandlersHooked = true;

        CaptureHoldButton.AddHandler(InputElement.PointerPressedEvent, CaptureHoldButton_OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        CaptureHoldButton.AddHandler(InputElement.PointerReleasedEvent, CaptureHoldButton_OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        CaptureHoldButton.AddHandler(InputElement.PointerCaptureLostEvent, CaptureHoldButton_OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        CaptureHoldButton.AddHandler(InputElement.KeyDownEvent, CaptureHoldButton_OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        CaptureHoldButton.AddHandler(InputElement.KeyUpEvent, CaptureHoldButton_OnKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void PreviewBorder_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Border b && DataContext is MainWindowViewModel vm)
            vm.PreviewLayoutSize = b.Bounds.Size;
    }

    private void FilmstripPhotos_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        vm.NotifyFilmstripHostWidthChanged(e.NewSize.Width);
    }

    private async void PreviewBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || DataContext is not MainWindowViewModel vm)
            return;
        if (e.Source is Visual vis && vis.FindAncestorOfType<Button>() != null)
            return;
        if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(border).Properties.IsRightButtonPressed)
            return;

        e.Pointer.Capture(border);
        vm.PreviewLayoutSize = border.Bounds.Size;
        var pt = e.GetPosition(border);
        await vm.OnPreviewFocusGesturePressedAsync(pt, border.Bounds.Size);
    }

    private async void PreviewBorder_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border border || DataContext is not MainWindowViewModel vm)
            return;
        e.Pointer.Capture(null);
        await vm.OnPreviewFocusGestureReleasedAsync();
    }

    private async void PreviewBorder_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        await vm.OnPreviewFocusGestureReleasedAsync();
    }

    private async void FocusHoldButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm)
            return;
        if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(btn).Properties.IsRightButtonPressed)
            return;

        e.Pointer.Capture(btn);
        await vm.FocusPointerPressedAsync();
    }

    private async void FocusHoldButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm)
            return;
        e.Pointer.Capture(null);
        await vm.FocusPointerReleasedAsync();
    }

    private async void FocusHoldButton_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        await vm.FocusPointerReleasedAsync();
    }

    private async void FocusHoldButton_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        await vm.FocusPointerPressedAsync();
    }

    private async void FocusHoldButton_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        await vm.FocusPointerReleasedAsync();
    }

    private async void CaptureHoldButton_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm)
            return;
        if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(btn).Properties.IsRightButtonPressed)
            return;

        e.Pointer.Capture(btn);
        await vm.CapturePointerPressedAsync();
    }

    private async void CaptureHoldButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Button btn || DataContext is not MainWindowViewModel vm)
            return;
        // 须先完成「松开快门」再 ClearCapture：否则 Capture(null) 会同步触发 PointerCaptureLost，
        // CapturePointerCancelledAsync 先于 CapturePointerReleasedAsync 清空半按状态，导致本次松手不拍照。
        await vm.CapturePointerReleasedAsync();
        e.Pointer.Capture(null);
    }

    private async void CaptureHoldButton_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        await vm.CapturePointerCancelledAsync();
    }

    private async void CaptureHoldButton_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        await vm.CapturePointerPressedAsync();
    }

    private async void CaptureHoldButton_OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (e.Key != Key.Space && e.Key != Key.Enter)
            return;
        e.Handled = true;
        await vm.CapturePointerReleasedAsync();
    }

    private async void OnClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Window window)
            return;
        if (_allowWindowClose)
            return;

        e.Cancel = true;
        try
        {
            if (window.DataContext is MainWindowViewModel vm)
                await vm.DisposeAsync().ConfigureAwait(true);
        }
        finally
        {
            _allowWindowClose = true;
            window.Close();
        }
    }
}
