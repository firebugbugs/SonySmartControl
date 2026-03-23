using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SonySmartControl.Models;

namespace SonySmartControl.Controls;

public partial class FilmstripPhotoItem : UserControl
{
    public FilmstripPhotoItem()
    {
        InitializeComponent();
        ToolTip.AddToolTipOpeningHandler(InfoButton, InfoButton_OnToolTipOpening);
    }

    private void InfoButton_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 不触发胶片条选片，仅展示信息浮层（由 ToolTip 处理悬停）
        e.Handled = true;
    }

    private void InfoButton_OnToolTipOpening(object? sender, CancelRoutedEventArgs e)
    {
        if (sender is not Control host)
            return;

        if (host.DataContext is RecentPhotoEntry entry)
            entry.RequestTooltipSourceMetadataIfNeeded();

        void StripChrome()
        {
            if (host.GetValue(ToolTipDiagnostics.ToolTipProperty) is not ToolTip tip)
                return;
            tip.Background = Brushes.Transparent;
            tip.BorderBrush = Brushes.Transparent;
            tip.BorderThickness = default;
            tip.Padding = default;
            tip.CornerRadius = default;
        }

        StripChrome();
        Dispatcher.UIThread.Post(StripChrome, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(StripChrome, DispatcherPriority.Render);
    }

    private void HitArea_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (DataContext is not RecentPhotoEntry entry)
            return;

        var host = this.FindAncestorOfType<FilmstripPhotosHost>();
        if (host?.IsDragSelecting == true)
            return;
        host?.ClearBatchSelection();
        var cmd = host?.SelectCommand;
        if (cmd == null)
            return;

        var path = entry.FilePath;
        if (cmd.CanExecute(path))
            cmd.Execute(path);

        e.Handled = true;
    }

    private void DeleteMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RecentPhotoEntry entry)
            return;
        var host = this.FindAncestorOfType<FilmstripPhotosHost>();
        host?.EnsureDeleteTargetSelection(entry);
        var cmd = host?.DeleteCommand;
        var path = entry.FilePath;
        if (cmd?.CanExecute(path) == true)
            cmd.Execute(path);
    }
}
