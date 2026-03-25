using SonySmartControl.Settings;

namespace SonySmartControl.Services.Settings;

/// <summary>相机相关用户设置的「多配置」存储（可列出/选择/新建/删除/更新）。</summary>
public interface ICameraSettingsProfilesStore
{
    Task<IReadOnlyList<CameraSettingsProfileInfo>> ListAsync(CancellationToken ct = default);

    Task<CameraUserSettings?> LoadAsync(long id, CancellationToken ct = default);

    Task<long> CreateAsync(string name, CameraUserSettings settings, CancellationToken ct = default);

    Task RenameAsync(long id, string newName, CancellationToken ct = default);

    Task UpdateAsync(long id, CameraUserSettings settings, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<long?> GetCurrentProfileIdAsync(CancellationToken ct = default);

    Task SetCurrentProfileIdAsync(long? id, CancellationToken ct = default);
}

public sealed record CameraSettingsProfileInfo(long Id, string Name, DateTimeOffset UpdatedUtc);

