using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Visuals;
using Avalonia.VisualTree;
using SonySmartControl.Models;

namespace SonySmartControl.Controls;

/// <summary>
/// 近期图片横向列表：不用 ItemsControl/Button，避免 Fluent 条目容器在点击时整排闪边框。
/// </summary>
public partial class FilmstripPhotosHost : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<FilmstripPhotosHost, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<ICommand?> SelectCommandProperty =
        AvaloniaProperty.Register<FilmstripPhotosHost, ICommand?>(nameof(SelectCommand));
    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<FilmstripPhotosHost, ICommand?>(nameof(DeleteCommand));
    public static readonly StyledProperty<bool> IsDragSelectingProperty =
        AvaloniaProperty.Register<FilmstripPhotosHost, bool>(nameof(IsDragSelecting));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ICommand? SelectCommand
    {
        get => GetValue(SelectCommandProperty);
        set => SetValue(SelectCommandProperty, value);
    }

    public bool IsDragSelecting
    {
        get => GetValue(IsDragSelectingProperty);
        private set => SetValue(IsDragSelectingProperty, value);
    }

    public ICommand? DeleteCommand
    {
        get => GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    /// <summary>供主窗口把滚轮映射为横向滚动。</summary>
    public ScrollViewer? PhotosScrollViewer => ScrollHost;

    public FilmstripPhotosHost()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, RootHost_OnPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, RootHost_OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, RootHost_OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, RootHost_OnPointerCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private bool _dragSelectingActive;
    private bool _dragSelectionStarted;
    private Point _dragStart;
    private Point _dragCurrent;
    private RecentPhotoEntry? _pressedEntry;

    static FilmstripPhotosHost()
    {
        ItemsSourceProperty.Changed.AddClassHandler<FilmstripPhotosHost>((o, e) => o.OnItemsSourceChanged(e));
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        RebuildItems();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnhookCollection(ItemsSource);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnItemsSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        UnhookCollection(e.OldValue as IEnumerable);
        HookCollection(e.NewValue as IEnumerable);
        RebuildItems();
    }

    private void HookCollection(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged n)
            n.CollectionChanged += OnBackingCollectionChanged;
    }

    private void UnhookCollection(IEnumerable? items)
    {
        if (items is INotifyCollectionChanged n)
            n.CollectionChanged -= OnBackingCollectionChanged;
    }

    private void OnBackingCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildItems);
    }

    private void RebuildItems()
    {
        if (ItemsPanelHost == null)
            return;

        ItemsPanelHost.Children.Clear();
        if (ItemsSource == null)
            return;

        foreach (var item in ItemsSource)
        {
            if (item is not RecentPhotoEntry entry)
                continue;
            ItemsPanelHost.Children.Add(new FilmstripPhotoItem { DataContext = entry });
        }
    }

    public void ClearBatchSelection()
    {
        if (ItemsSource == null)
            return;
        foreach (var item in ItemsSource)
        {
            if (item is RecentPhotoEntry entry)
                entry.IsBatchSelected = false;
        }
    }

    public void SetSingleBatchSelection(RecentPhotoEntry target)
    {
        if (ItemsSource == null)
            return;
        foreach (var item in ItemsSource)
        {
            if (item is not RecentPhotoEntry entry)
                continue;
            entry.IsBatchSelected = ReferenceEquals(entry, target);
        }
    }

    public void EnsureDeleteTargetSelection(RecentPhotoEntry target)
    {
        if (ItemsSource == null)
            return;

        var hasAnyBatch = false;
        var targetAlreadyInBatch = false;
        foreach (var item in ItemsSource)
        {
            if (item is not RecentPhotoEntry entry || !entry.IsBatchSelected)
                continue;
            hasAnyBatch = true;
            if (ReferenceEquals(entry, target))
                targetAlreadyInBatch = true;
        }

        if (hasAnyBatch && targetAlreadyInBatch)
            return;

        SetSingleBatchSelection(target);
    }

    private void RootHost_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || e.GetCurrentPoint(RootHost).Properties.IsLeftButtonPressed == false)
            return;

        _dragSelectingActive = true;
        _dragSelectionStarted = false;
        _dragStart = e.GetPosition(RootHost);
        _dragCurrent = _dragStart;
        IsDragSelecting = false;
        SelectionRect.IsVisible = false;
        _pressedEntry = TryResolveEntryFromEventSource(e.Source);
        e.Pointer.Capture(RootHost);
    }

    private void RootHost_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragSelectingActive)
            return;

        _dragCurrent = e.GetPosition(RootHost);
        var rect = NormalizeRect(_dragStart, _dragCurrent);
        var dx = _dragCurrent.X - _dragStart.X;
        var dy = _dragCurrent.Y - _dragStart.Y;
        if (!_dragSelectionStarted && (dx * dx + dy * dy) < 64) // 8px 阈值，避免轻微抖动吞掉单击
            return;

        if (!_dragSelectionStarted)
        {
            _dragSelectionStarted = true;
            ClearBatchSelection();
        }

        IsDragSelecting = true;
        UpdateSelectionRectVisual(rect);
        ApplyDragSelection(rect);
    }

    private void RootHost_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSelectingActive && !_dragSelectionStarted)
            TrySelectPressedEntry();
        EndDragSelection(e.Pointer);
    }

    private void RootHost_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndDragSelection(null);
    }

    private void EndDragSelection(IPointer? pointer)
    {
        if (!_dragSelectingActive)
            return;

        var shouldOpenSingle = IsDragSelecting;
        _dragSelectingActive = false;
        _dragSelectionStarted = false;
        SelectionRect.IsVisible = false;
        pointer?.Capture(null);
        IsDragSelecting = false;
        _pressedEntry = null;

        if (shouldOpenSingle)
            TryOpenSingleBatchSelection();
    }

    private void UpdateSelectionRectVisual(Rect rectInRoot)
    {
        if (rectInRoot.Width <= 0 || rectInRoot.Height <= 0)
        {
            SelectionRect.IsVisible = false;
            return;
        }

        SelectionRect.Margin = new Thickness(rectInRoot.X, rectInRoot.Y, 0, 0);
        SelectionRect.Width = rectInRoot.Width;
        SelectionRect.Height = rectInRoot.Height;
        SelectionRect.IsVisible = true;
    }

    private void ApplyDragSelection(Rect selectionRectInRoot)
    {
        if (ItemsPanelHost == null)
            return;

        foreach (var child in ItemsPanelHost.Children)
        {
            if (child is not Control control || control.DataContext is not RecentPhotoEntry entry)
                continue;

            var topLeft = control.TranslatePoint(new Point(0, 0), RootHost);
            if (topLeft == null)
                continue;
            var bounds = new Rect(topLeft.Value, control.Bounds.Size);
            entry.IsBatchSelected = selectionRectInRoot.Intersects(bounds);
        }
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        var x1 = Math.Min(a.X, b.X);
        var y1 = Math.Min(a.Y, b.Y);
        var x2 = Math.Max(a.X, b.X);
        var y2 = Math.Max(a.Y, b.Y);
        return new Rect(new Point(x1, y1), new Point(x2, y2));
    }

    private void TryOpenSingleBatchSelection()
    {
        if (ItemsSource == null || SelectCommand == null)
            return;

        string? onePath = null;
        var selectedCount = 0;
        foreach (var item in ItemsSource)
        {
            if (item is not RecentPhotoEntry entry || !entry.IsBatchSelected)
                continue;
            selectedCount++;
            if (selectedCount > 1)
                return;
            onePath = entry.FilePath;
        }

        if (selectedCount == 1 && !string.IsNullOrWhiteSpace(onePath) && SelectCommand.CanExecute(onePath))
            SelectCommand.Execute(onePath);
    }

    private void TrySelectPressedEntry()
    {
        if (SelectCommand == null || _pressedEntry == null)
            return;
        ClearBatchSelection();
        if (SelectCommand.CanExecute(_pressedEntry.FilePath))
            SelectCommand.Execute(_pressedEntry.FilePath);
    }

    private static RecentPhotoEntry? TryResolveEntryFromEventSource(object? source)
    {
        if (source is not Visual visual)
            return null;
        var item = visual.FindAncestorOfType<FilmstripPhotoItem>();
        return item?.DataContext as RecentPhotoEntry;
    }
}
