using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using SonySmartControl.Services.Settings;
using SonySmartControl.Settings;

namespace SonySmartControl.ViewModels;

public sealed partial class SettingsProfilesViewModel : ViewModelBase
{
    private readonly ICameraSettingsProfilesStore _store;
    private readonly Func<long, Task> _applyProfileAsync;
    private readonly Action _close;

    [ObservableProperty] private string _hintText = "提示：默认配置会自动保存。切换配置后，后续修改将写入当前配置。";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorText))]
    private string _errorText = "";
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    public ObservableCollection<SettingsProfileListItemViewModel> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplySelected))]
    [NotifyPropertyChangedFor(nameof(CanDeleteSelected))]
    [NotifyPropertyChangedFor(nameof(CanRenameSelected))]
    private SettingsProfileListItemViewModel? _selectedProfile;

    [ObservableProperty] private long _currentProfileId;

    public bool CanApplySelected => SelectedProfile != null && SelectedProfile.Id != CurrentProfileId;

    public bool CanDeleteSelected =>
        SelectedProfile != null && SelectedProfile.Id != CurrentProfileId;

    public bool CanRenameSelected => SelectedProfile != null;
    public bool HasErrorText => !string.IsNullOrWhiteSpace(ErrorText);

    public SettingsProfilesViewModel(
        ICameraSettingsProfilesStore store,
        long currentProfileId,
        Func<long, Task> applyProfileAsync,
        Action close)
    {
        _store = store;
        _applyProfileAsync = applyProfileAsync;
        _close = close;
        _currentProfileId = currentProfileId;
        _ = ReloadAsync();
    }

    /// <summary>设计器预览。</summary>
    public SettingsProfilesViewModel()
        : this(
            new SqliteCameraSettingsProfilesStore(),
            1,
            _ => Task.CompletedTask,
            () => { })
    {
        Profiles.Add(new SettingsProfileListItemViewModel(1, "默认配置", DateTimeOffset.UtcNow, isCurrent: true));
        Profiles.Add(new SettingsProfileListItemViewModel(2, "人像", DateTimeOffset.UtcNow.AddMinutes(-10), isCurrent: false));
        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        ErrorText = "";
        IsRenaming = false;
        RenameText = "";
        var list = await _store.ListAsync().ConfigureAwait(true);
        Profiles.Clear();
        foreach (var p in list)
            Profiles.Add(new SettingsProfileListItemViewModel(p.Id, p.Name, p.UpdatedUtc, p.Id == CurrentProfileId));
        SelectedProfile = Profiles.FirstOrDefault(x => x.Id == CurrentProfileId) ?? Profiles.FirstOrDefault();
        ApplySelectedProfileCommand.NotifyCanExecuteChanged();
        DeleteSelectedProfileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ApplySelectedProfileAsync()
    {
        if (SelectedProfile == null)
            return;
        if (SelectedProfile.Id == CurrentProfileId)
            return;

        await _applyProfileAsync(SelectedProfile.Id).ConfigureAwait(true);
        CurrentProfileId = SelectedProfile.Id;
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteSelectedProfileAsync()
    {
        ErrorText = "";
        if (SelectedProfile == null)
            return;
        if (SelectedProfile.Id == CurrentProfileId)
            return;

        await _store.DeleteAsync(SelectedProfile.Id).ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        ErrorText = "";
        // 先用简单默认名（避免引入复杂输入控件）；若重名则自动递增。
        var baseName = "新建配置";
        var existing = Profiles.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        var n = 1;
        while (existing.Contains(name))
        {
            n++;
            name = $"{baseName}{n}";
        }

        // 用当前配置内容复制一份。
        var current = await _store.LoadAsync(CurrentProfileId).ConfigureAwait(true) ?? new CameraUserSettings();
        var id = await _store.CreateAsync(name, current).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await ReloadAsync().ConfigureAwait(true);
            SelectedProfile = Profiles.FirstOrDefault(x => x.Id == id) ?? SelectedProfile;
        });
    }

    [RelayCommand]
    private void BeginRenameSelected()
    {
        ErrorText = "";
        if (SelectedProfile == null)
            return;
        IsRenaming = true;
        RenameText = SelectedProfile.Name;
    }

    [RelayCommand]
    private void CancelRename()
    {
        ErrorText = "";
        IsRenaming = false;
        RenameText = "";
    }

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        ErrorText = "";
        if (SelectedProfile == null)
            return;
        var name = (RenameText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText = "名称不能为空。";
            return;
        }

        try
        {
            await _store.RenameAsync(SelectedProfile.Id, name).ConfigureAwait(true);
            IsRenaming = false;
            RenameText = "";
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            ErrorText = "名称已存在，请换一个。";
        }
        catch (Exception ex)
        {
            ErrorText = "改名失败：" + ex.Message;
        }
    }
}

public sealed partial class SettingsProfileListItemViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    public DateTimeOffset UpdatedUtc { get; }

    [ObservableProperty] private bool _isCurrent;

    public string UpdatedText =>
        "更新于 " + UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public SettingsProfileListItemViewModel(long id, string name, DateTimeOffset updatedUtc, bool isCurrent)
    {
        Id = id;
        Name = name;
        UpdatedUtc = updatedUtc;
        _isCurrent = isCurrent;
    }
}

