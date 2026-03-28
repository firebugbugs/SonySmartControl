using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Visuals;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SonySmartControl.Helpers;
using SonySmartControl.Services.Platform;
using SonySmartControl.ViewModels;

namespace SonySmartControl.Views;

/// <summary>
/// 主窗口：无边框窗口 chrome、拖拽/缩放、与必须依赖 <see cref="Window"/> API 的交互；其余由 ViewModel 与命令绑定。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>为 true 时表示资源已释放，允许真正关闭窗口（避免首次 Closing 在 await 返回前就结束了进程）。</summary>
    private bool _allowWindowClose;

    private static readonly BoxShadows CardRestoredShadow = BoxShadows.Parse("0 12 40 0 #28000000");

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosingAsync;
        Closed += OnWindowClosedScheduleHardExit;
    }

    private void MainWindow_OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.Services.GetRequiredService<IMainWindowShellService>().Attach(this);
        SyncChromeWindowState();
        ApplyTaskbarWindowIcon();
    }

    /// <summary>无边框窗口在部分 Windows 版本上需再次设置 HWND 图标，任务栏才显示自定义 ico。</summary>
    private void ApplyTaskbarWindowIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AppIconUris.WindowIcon));
            Icon = new WindowIcon(stream);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>方向键切换回看图：具体规则在 ViewModel，此处只做焦点与路由。</summary>
    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm
            && vm.TryHandleGalleryArrowNavigation(e.Key, FocusManager?.GetFocusedElement()))
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

    /// <summary>作者信息 Popup 的锚点取决于点击的是图标还是标题，需在视图层设置 <see cref="Avalonia.Controls.Primitives.Popup.PlacementTarget"/>。</summary>
    private void AuthorInfoTrigger_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Visual visual || !IsLeftButtonPressed(e, visual))
            return;
        if (AuthorInfoPopup == null)
            return;
        if (sender is Control control)
            AuthorInfoPopup.PlacementTarget = control;
        if (DataContext is MainWindowViewModel vm)
            vm.IsAuthorInfoPopupOpen = true;
        e.Handled = true;
    }

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
}
