using System.Text.Json;
using Microsoft.Data.Sqlite;
using SonySmartControl.Settings;

namespace SonySmartControl.Services.Settings;

/// <summary>
/// SQLite 版「多配置」存储（%LocalAppData%\SonySmartControl\settings.sqlite）。
/// - profiles: 保存多条配置（JSON 形式存储 CameraUserSettings）
/// - app_state: 保存当前选中的 profile_id
/// 同时支持从旧版 camera_user_settings.json 迁移（仅在库为空时导入为“默认配置”）。
/// </summary>
public sealed class SqliteCameraSettingsProfilesStore : ICameraSettingsProfilesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private static string DbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonySmartControl",
            "settings.sqlite");

    private static string LegacyDbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonySmartControl",
            "settings.db");

    private static string LegacyJsonPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonySmartControl",
            "camera_user_settings.json");

    private static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            TryMigrateLegacyDbFile();

            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              PRAGMA journal_mode=WAL;
                              PRAGMA foreign_keys=ON;

                              CREATE TABLE IF NOT EXISTS profiles (
                                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                                  name TEXT NOT NULL UNIQUE,
                                  settings_json TEXT NOT NULL,
                                  camera_settings_json TEXT NOT NULL DEFAULT '',
                                  created_utc TEXT NOT NULL,
                                  updated_utc TEXT NOT NULL
                              );

                              CREATE TABLE IF NOT EXISTS app_state (
                                  key TEXT PRIMARY KEY,
                                  value TEXT
                              );
                              """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // 兼容旧库：早期版本没有 camera_settings_json 列，运行时补齐。
            await EnsureProfilesHasCameraSettingsColumnAsync(conn, ct).ConfigureAwait(false);

            _initialized = true;
            await TryMigrateLegacyJsonAsync(conn, ct).ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static void TryMigrateLegacyDbFile()
    {
        try
        {
            // 若用户已有旧文件，但新文件不存在，则迁移/重命名。
            if (File.Exists(LegacyDbPath) && !File.Exists(DbPath))
                File.Move(LegacyDbPath, DbPath);
        }
        catch
        {
            // 迁移失败不影响后续逻辑：仍可能通过旧 JSON 做初始化。
        }
    }

    private static async Task EnsureProfilesHasCameraSettingsColumnAsync(SqliteConnection conn, CancellationToken ct)
    {
        try
        {
            var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(profiles);";
            await using var r = await pragma.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var has = false;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                // PRAGMA table_info returns: cid, name, type, notnull, dflt_value, pk
                var name = r.GetString(1);
                if (string.Equals(name, "camera_settings_json", StringComparison.OrdinalIgnoreCase))
                {
                    has = true;
                    break;
                }
            }

            if (has)
                return;

            var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE profiles ADD COLUMN camera_settings_json TEXT NOT NULL DEFAULT '';";
            await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 忽略：可能并发初始化或权限问题
        }
    }

    private static async Task TryMigrateLegacyJsonAsync(SqliteConnection conn, CancellationToken ct)
    {
        try
        {
            // 仅当库还没有任何配置时迁移，避免覆盖用户已有多配置。
            var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(1) FROM profiles;";
            var countObj = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var count = countObj is long l ? l : Convert.ToInt64(countObj);
            if (count > 0)
                return;

            if (!File.Exists(LegacyJsonPath))
                return;

            var json = await File.ReadAllTextAsync(LegacyJsonPath, ct).ConfigureAwait(false);
            var s = JsonSerializer.Deserialize<CameraUserSettings>(json, JsonOptions);
            if (s == null)
                return;

            var now = DateTimeOffset.UtcNow.ToString("O");
            var ins = conn.CreateCommand();
            ins.CommandText = """
                              INSERT INTO profiles (name, settings_json, camera_settings_json, created_utc, updated_utc)
                              VALUES ($name, $json, '', $created, $updated);
                              """;
            ins.Parameters.AddWithValue("$name", "默认配置");
            ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(s, JsonOptions));
            ins.Parameters.AddWithValue("$created", now);
            ins.Parameters.AddWithValue("$updated", now);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            var idCmd = conn.CreateCommand();
            idCmd.CommandText = "SELECT last_insert_rowid();";
            var idObj = await idCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var id = idObj is long lid ? lid : Convert.ToInt64(idObj);

            var up = conn.CreateCommand();
            up.CommandText = """
                             INSERT INTO app_state(key, value) VALUES ('current_profile_id', $v)
                             ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                             """;
            up.Parameters.AddWithValue("$v", id.ToString());
            await up.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }

    public async Task<IReadOnlyList<CameraSettingsProfileInfo>> ListAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            var list = new List<CameraSettingsProfileInfo>();
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, updated_utc FROM profiles ORDER BY updated_utc DESC, id DESC;";
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = r.GetInt64(0);
                var name = r.GetString(1);
                var updated = DateTimeOffset.TryParse(r.GetString(2), out var u) ? u : DateTimeOffset.MinValue;
                list.Add(new CameraSettingsProfileInfo(id, name, updated));
            }

            return list;
        }
        catch
        {
            return Array.Empty<CameraSettingsProfileInfo>();
        }
    }

    public async Task<CameraUserSettings?> LoadAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT settings_json FROM profiles WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not string json || string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<CameraUserSettings>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public async Task<long> CreateAsync(string name, CameraUserSettings settings, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO profiles (name, settings_json, camera_settings_json, created_utc, updated_utc)
                          VALUES ($name, $json, '', $created, $updated);
                          """;
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, JsonOptions));
        cmd.Parameters.AddWithValue("$created", now);
        cmd.Parameters.AddWithValue("$updated", now);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid();";
        var idObj = await idCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return idObj is long lid ? lid : Convert.ToInt64(idObj);
    }

    public async Task RenameAsync(long id, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          UPDATE profiles
                          SET name=$name, updated_utc=$updated
                          WHERE id=$id;
                          """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", newName.Trim());
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(long id, CameraUserSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              UPDATE profiles
                              SET settings_json=$json, updated_utc=$updated
                              WHERE id=$id;
                              """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, JsonOptions));
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // 若删除的是当前配置，顺便清空 current_profile_id
            var current = await GetCurrentProfileIdAsync(ct).ConfigureAwait(false);
            if (current == id)
                await SetCurrentProfileIdAsync(null, ct).ConfigureAwait(false);

            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM profiles WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async Task<long?> GetCurrentProfileIdAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_state WHERE key='current_profile_id';";
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not string s || string.IsNullOrWhiteSpace(s))
                return null;
            return long.TryParse(s, out var id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task SetCurrentProfileIdAsync(long? id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              INSERT INTO app_state(key, value) VALUES ('current_profile_id', $v)
                              ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                              """;
            cmd.Parameters.AddWithValue("$v", id?.ToString() ?? "");
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public async Task<string?> LoadCameraSettingsJsonAsync(long id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT camera_settings_json FROM profiles WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return obj as string;
        }
        catch
        {
            return null;
        }
    }

    public async Task UpdateCameraSettingsJsonAsync(long id, string cameraSettingsJson, CancellationToken ct = default)
    {
        cameraSettingsJson ??= "";
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            cmd.CommandText = """
                              UPDATE profiles
                              SET camera_settings_json=$json, updated_utc=$updated
                              WHERE id=$id;
                              """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$json", cameraSettingsJson);
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}

