using System;
using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SonySmartControl.Controls;

/// <summary>
/// 自绘下拉：显示文本仅由 <see cref="SelectedIndex"/> + <see cref="ItemsSource"/> 推导，避免 Avalonia ComboBox 在「先点选再滚轮」时闭合框不刷新。
/// </summary>
public partial class WheelPickerCombo : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<WheelPickerCombo, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<WheelPickerCombo, int>(nameof(SelectedIndex), defaultValue: -1);

    /// <summary>为 true 时滚轮方向与默认相反（例如曝光补偿：上滚为「加 EV」）。</summary>
    public static readonly StyledProperty<bool> InvertWheelProperty =
        AvaloniaProperty.Register<WheelPickerCombo, bool>(nameof(InvertWheel));

    /// <summary>
    /// 为 true 时允许在控件上悬停后用滚轮逐项切换；对焦模式、曝光模式等下拉应置为 false，以免误触并保留侧栏滚动。
    /// </summary>
    public static readonly StyledProperty<bool> WheelAdjustEnabledProperty =
        AvaloniaProperty.Register<WheelPickerCombo, bool>(nameof(WheelAdjustEnabled), defaultValue: false);

    private INotifyCollectionChanged? _itemsNotify;
    private bool _allowCloseOnSelection;
    private bool _syncingListSelection;

    private static readonly TimeSpan WheelHoverRequired = TimeSpan.FromMilliseconds(250);

    /// <summary>指针进入 <see cref="RootBorder"/> 的 UTC 时间；离开则清空。滚轮仅当悬停持续满 <see cref="WheelHoverRequired"/> 后生效。</summary>
    private DateTime? _wheelHoverStartedUtc;

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public bool InvertWheel
    {
        get => GetValue(InvertWheelProperty);
        set => SetValue(InvertWheelProperty, value);
    }

    public bool WheelAdjustEnabled
    {
        get => GetValue(WheelAdjustEnabledProperty);
        set => SetValue(WheelAdjustEnabledProperty, value);
    }

    public WheelPickerCombo()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncDropListItemsSource();
    }

    private void SyncDropListItemsSource()
    {
        if (DropList != null)
            DropList.ItemsSource = ItemsSource;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
        {
            if (_itemsNotify != null)
            {
                _itemsNotify.CollectionChanged -= ItemsSource_CollectionChanged;
                _itemsNotify = null;
            }

            if (change.NewValue is INotifyCollectionChanged n)
            {
                _itemsNotify = n;
                n.CollectionChanged += ItemsSource_CollectionChanged;
            }

            SyncDropListItemsSource();
            UpdateDisplayText();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            if (!_syncingListSelection && DropList != null)
            {
                var idx = SelectedIndex;
                if (idx >= 0 && idx < CountItems())
                {
                    _syncingListSelection = true;
                    try
                    {
                        DropList.SelectedIndex = idx;
                    }
                    finally
                    {
                        _syncingListSelection = false;
                    }
                }
            }

            UpdateDisplayText();
        }
        else if (change.Property == IsEnabledProperty)
        {
            RootBorder.Opacity = IsEnabled ? 1 : 0.45;
            if (!IsEnabled)
                _wheelHoverStartedUtc = null;
        }
        else if (change.Property == WheelAdjustEnabledProperty)
        {
            if (!WheelAdjustEnabled)
                _wheelHoverStartedUtc = null;
        }
    }

    private void ItemsSource_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateDisplayText();

    private int CountItems()
    {
        if (ItemsSource is IList l)
            return l.Count;
        return 0;
    }

    private void UpdateDisplayText()
    {
        if (DisplayText == null)
            return;
        var idx = SelectedIndex;
        var items = ItemsSource as IList;
        if (items == null || idx < 0 || idx >= items.Count)
        {
            DisplayText.Text = "";
            return;
        }

        var o = items[idx];
        DisplayText.Text = o?.ToString() ?? "";
    }

    private void RootBorder_OnPointerEntered(object? sender, PointerEventArgs e) =>
        _wheelHoverStartedUtc = WheelAdjustEnabled ? DateTime.UtcNow : null;

    private void RootBorder_OnPointerExited(object? sender, PointerEventArgs e) =>
        _wheelHoverStartedUtc = null;

    private void RootBorder_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!IsEnabled)
            return;
        if (!WheelAdjustEnabled)
            return;
        if (_wheelHoverStartedUtc is not { } started ||
            DateTime.UtcNow - started < WheelHoverRequired)
        {
            e.Handled = true;
            return;
        }

        var items = ItemsSource as IList;
        if (items == null || items.Count == 0)
            return;

        var step = e.Delta.Y > 0 ? 1 : -1;
        if (InvertWheel)
            step = -step;

        var idx = SelectedIndex;
        if (idx < 0 || idx >= items.Count)
            return;

        var n = Math.Clamp(idx + step, 0, items.Count - 1);
        if (n == idx)
        {
            e.Handled = true;
            return;
        }

        SelectedIndex = n;
        e.Handled = true;
    }

    private void RootBorder_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled)
            return;
        DropPopup.IsOpen = !DropPopup.IsOpen;
        e.Handled = true;
    }

    private void DropPopup_OnOpened(object? sender, EventArgs e)
    {
        if (DropPopupBorder != null && RootBorder != null)
            DropPopupBorder.MinWidth = Math.Max(RootBorder.Bounds.Width, 1);

        _allowCloseOnSelection = false;
        var idx = SelectedIndex;
        if (DropList != null && idx >= 0 && idx < CountItems())
        {
            _syncingListSelection = true;
            try
            {
                DropList.SelectedIndex = idx;
            }
            finally
            {
                _syncingListSelection = false;
            }
        }

        Dispatcher.UIThread.Post(() => { _allowCloseOnSelection = true; }, DispatcherPriority.Loaded);
    }

    private void DropList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingListSelection || !_allowCloseOnSelection)
            return;
        if (DropList == null)
            return;
        if (DropList.SelectedIndex < 0)
            return;

        SelectedIndex = DropList.SelectedIndex;
        DropPopup.IsOpen = false;
    }
}
