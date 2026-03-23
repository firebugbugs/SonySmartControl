using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    public static readonly StyledProperty<ICommand?> CopyCommandProperty =
        AvaloniaProperty.Register<FilmstripPhotosHost, ICommand?>(nameof(CopyCommand));
    public static readonly StyledProperty<ICommand?> DeleteCommandProperty =
        AvaloniaProperty.Register<FilmstripPhotosHost, ICommand?>(nameof(DeleteCommand));

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

    public ICommand? CopyCommand
    {
        get => GetValue(CopyCommandProperty);
        set => SetValue(CopyCommandProperty, value);
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
    }

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
}
