using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using SonySmartControl.Helpers;

namespace SonySmartControl.Views;

public partial class SettingsProfilesWindow : Window
{
    private static readonly BoxShadows CardRestoredShadow = BoxShadows.Parse("0 12 40 0 #28000000");

    public SettingsProfilesWindow()
    {
        InitializeComponent();
    }

    private void SettingsProfilesWindow_OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyTaskbarWindowIcon();
        SyncChromeWindowState();
    }

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

    private void ChromeMinimize_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ChromeMaximize_OnClick(object? sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void ChromeClose_OnClick(object? sender, RoutedEventArgs e) => Close();

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

