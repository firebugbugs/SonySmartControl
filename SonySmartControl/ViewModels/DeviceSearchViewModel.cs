using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SonySmartControl.Models;
using SonySmartControl.Services.Camera;
using SonySmartControl.Services.Logging;

namespace SonySmartControl.ViewModels;

public partial class DeviceSearchViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly Action _closeWindow;
    private readonly IAppLogService _appLogService;

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusHint = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _showDiscoveryError;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private DiscoveredCameraDevice? _selectedDevice;

    public ObservableCollection<DiscoveredCameraDevice> Devices { get; } = new();

    public bool ConnectButtonEnabled => SelectedDevice != null && !IsSearching;

    public bool CanRefresh => !IsSearching;

    public DeviceSearchViewModel(MainWindowViewModel main, Action closeWindow, IAppLogService appLogService)
    {
        _main = main;
        _closeWindow = closeWindow;
        _appLogService = appLogService;
    }

    partial void OnSelectedDeviceChanged(DiscoveredCameraDevice? value)
    {
        OnPropertyChanged(nameof(ConnectButtonEnabled));
    }

    partial void OnIsSearchingChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonEnabled));
        OnPropertyChanged(nameof(CanRefresh));
    }

    public async Task LoadAsync()
    {
        if (IsSearching)
            return;

        IsSearching = true;
        ErrorMessage = null;
        ShowDiscoveryError = false;
        StatusHint = "正在通过 Sony Camera Remote SDK 枚举设备…";
        _appLogService.Append("设备搜索：开始枚举可连接设备。");
        Devices.Clear();
        SelectedDevice = null;
        IsEmpty = false;

        try
        {
            var list = await Task.Run(() => CameraDeviceDiscovery.Discover()).ConfigureAwait(true);
            foreach (var d in list)
                Devices.Add(d);

            IsEmpty = list.Count == 0;
            StatusHint = IsEmpty
                ? "未发现可用设备。请确认 USB/网线连接、机身已开启「遥控拍摄」后点击「重新搜索」。"
                : $"共找到 {list.Count} 台设备，请选择后点击「连接」。";
            _appLogService.Append(
                IsEmpty
                    ? "设备搜索：未发现可用设备。"
                    : $"设备搜索：已发现 {list.Count} 台设备。");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ShowDiscoveryError = true;
            StatusHint = "枚举失败。";
            IsEmpty = true;
            _appLogService.Append("设备搜索失败：" + ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task ConnectToSelectedAsync()
    {
        if (SelectedDevice == null || _main.IsConnecting || _main.IsSessionActive)
            return;

        _appLogService.Append(
            $"设备搜索：用户选择连接 [{SelectedDevice.OrdinalLabel}] {SelectedDevice.ModelName} ({SelectedDevice.ConnectionTypeText}/{SelectedDevice.EndpointText})。");
        var ok = await _main.ConnectToCameraAsync(SelectedDevice.Index).ConfigureAwait(true);
        _appLogService.Append(
            ok
                ? $"设备搜索：连接成功 [{SelectedDevice.OrdinalLabel}] {SelectedDevice.ModelName}。"
                : $"设备搜索：连接失败 [{SelectedDevice.OrdinalLabel}] {SelectedDevice.ModelName}。");
        if (ok)
            _closeWindow();
    }
}
