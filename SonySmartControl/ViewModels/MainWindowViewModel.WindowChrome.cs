using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SonySmartControl.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _isAuthorInfoPopupOpen;

    [ObservableProperty] private bool _isCloseConfirmPopupOpen;

    [RelayCommand]
    private void MinimizeWindow() => _mainWindowShell.Minimize();

    [RelayCommand]
    private void ToggleMaximizeWindow() => _mainWindowShell.ToggleMaximize();

    [RelayCommand]
    private void ShowCloseConfirm() => IsCloseConfirmPopupOpen = true;

    [RelayCommand]
    private void CancelCloseConfirm() => IsCloseConfirmPopupOpen = false;

    [RelayCommand]
    private void ConfirmClose()
    {
        IsCloseConfirmPopupOpen = false;
        _mainWindowShell.RequestClose();
    }

    [RelayCommand]
    private async Task CopyBilibiliIdAsync() => await CopyTextToClipboardAsync("43096314").ConfigureAwait(true);

    [RelayCommand]
    private async Task CopyQqGroupAsync() => await CopyTextToClipboardAsync("1094431427").ConfigureAwait(true);

    private async Task CopyTextToClipboardAsync(string text)
    {
        var top = _topLevelProvider.GetTopLevel();
        if (top?.Clipboard == null)
            return;
        await top.Clipboard.SetTextAsync(text).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenAuthorHomepage() => _externalUriLauncher.OpenUri("https://www.cheems.online/");

    [RelayCommand]
    private void OpenGithubRepo() => _externalUriLauncher.OpenUri("https://github.com/firebugbugs/SonySmartControl");

    [RelayCommand]
    private void OpenGiteeRepo() => _externalUriLauncher.OpenUri("https://gitee.com/unbengable/SonySmartControl");
}
