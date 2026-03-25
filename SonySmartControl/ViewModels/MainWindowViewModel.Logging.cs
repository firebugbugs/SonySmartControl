namespace SonySmartControl.ViewModels;

public partial class MainWindowViewModel
{
    partial void OnStatusMessageChanged(string value)
    {
        _appLogService.Append(value);
        ShowReadyHint = string.Equals(value, ReadyHintText, StringComparison.Ordinal);
    }
}
