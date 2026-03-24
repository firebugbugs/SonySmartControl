using Avalonia.Platform.Storage;

namespace SonySmartControl.Services.Platform;

/// <summary>基于当前 <see cref="ITopLevelProvider"/> 的异步文件夹选择实现。</summary>
public sealed class AvaloniaFolderPickerService(ITopLevelProvider topLevelProvider) : IFolderPickerService
{
    private readonly ITopLevelProvider _topLevelProvider = topLevelProvider;

    public async Task<string?> PickFolderAsync(string title, CancellationToken cancellationToken = default)
    {
        var top = _topLevelProvider.GetTopLevel();
        if (top?.StorageProvider is not { } storage)
            return null;

        var folders = await storage
            .OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false,
                })
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (folders.Count == 0)
            return null;
        return folders[0].TryGetLocalPath();
    }
}
