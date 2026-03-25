using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SonySmartControl.Models;
using SonySmartControl.Services.Logging;
using SonySmartControl.Services.Platform;

namespace SonySmartControl.ViewModels;

public partial class LogHistoryViewModel : ViewModelBase
{
    public const int PageSize = 20;

    private readonly IAppLogService _logService;
    private readonly ITopLevelProvider _topLevelProvider;

    public LogHistoryViewModel(IAppLogService logService, ITopLevelProvider topLevelProvider)
    {
        _logService = logService;
        _topLevelProvider = topLevelProvider;
        _ = LoadPageAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfoText))]
    [NotifyPropertyChangedFor(nameof(TotalPages))]
    private int _currentPageIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageInfoText))]
    [NotifyPropertyChangedFor(nameof(TotalPages))]
    private int _totalCount;

    [ObservableProperty] private bool _showClearConfirm;

    [ObservableProperty] private string _toastText = "";

    public ObservableCollection<AppLogEntry> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public bool HasToastText => !string.IsNullOrWhiteSpace(ToastText);

    public int TotalPages =>
        TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public string PageInfoText =>
        $"第 {CurrentPageIndex + 1} / {TotalPages} 页 · 共 {TotalCount} 条";

    private async Task LoadPageAsync()
    {
        var (items, total) = await _logService.GetPageAsync(CurrentPageIndex, PageSize).ConfigureAwait(true);
        Items.Clear();
        foreach (var e in items)
            Items.Add(e);
        TotalCount = total;
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnToastTextChanged(string value) => OnPropertyChanged(nameof(HasToastText));

    [RelayCommand]
    private async Task ReloadAsync() => await LoadPageAsync();

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (CurrentPageIndex <= 0)
            return;
        CurrentPageIndex--;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPageIndex >= TotalPages - 1)
            return;
        CurrentPageIndex++;
        await LoadPageAsync();
    }

    [RelayCommand]
    private void RequestClear() => ShowClearConfirm = true;

    [RelayCommand]
    private void CancelClear() => ShowClearConfirm = false;

    [RelayCommand]
    private async Task ConfirmClearAsync()
    {
        await _logService.ClearAllAsync().ConfigureAwait(true);
        ShowClearConfirm = false;
        CurrentPageIndex = 0;
        await LoadPageAsync();
    }

    [RelayCommand]
    private async Task CopyLineAsync(AppLogEntry? entry)
    {
        if (entry == null)
            return;
        var top = _topLevelProvider.GetTopLevel();
        if (top?.Clipboard == null)
            return;
        await top.Clipboard.SetTextAsync(entry.Message).ConfigureAwait(true);
        ToastText = "已复制";
        await Task.Delay(1800).ConfigureAwait(true);
        ToastText = "";
    }
}
