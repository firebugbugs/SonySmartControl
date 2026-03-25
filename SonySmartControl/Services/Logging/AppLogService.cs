using System.Globalization;
using Microsoft.Data.Sqlite;
using SonySmartControl.Models;

namespace SonySmartControl.Services.Logging;

/// <summary>使用本地 SQLite 文件存储应用日志（不加密）。</summary>
public sealed class AppLogService : IAppLogService
{
    private readonly string _dbPath;
    private readonly object _dbLock = new();

    public AppLogService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonySmartControl");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "app_logs.sqlite");
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS app_log (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    created_at TEXT NOT NULL,
                    message TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    /// <inheritdoc />
    public void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        lock (_dbLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO app_log (created_at, message) VALUES ($ts, $msg);";
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$msg", message.Trim());
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public Task<(IReadOnlyList<AppLogEntry> Items, int TotalCount)> GetPageAsync(
        int pageIndex0,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex0 < 0) pageIndex0 = 0;
        if (pageSize < 1) pageSize = 20;

        return Task.Run(() =>
        {
            lock (_dbLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var conn = OpenConnection();
                int total;
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM app_log;";
                    var scalar = countCmd.ExecuteScalar();
                    total = scalar != null ? Convert.ToInt32(scalar) : 0;
                }

                var list = new List<AppLogEntry>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        """
                        SELECT id, created_at, message
                        FROM app_log
                        ORDER BY id DESC
                        LIMIT $lim OFFSET $off;
                        """;
                    cmd.Parameters.AddWithValue("$lim", pageSize);
                    cmd.Parameters.AddWithValue("$off", pageIndex0 * pageSize);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new AppLogEntry
                        {
                            Id = r.GetInt64(0),
                            CreatedAtUtc = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind),
                            Message = r.GetString(2),
                        });
                    }
                }

                return ((IReadOnlyList<AppLogEntry>)list, total);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearAllAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            lock (_dbLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM app_log;";
                cmd.ExecuteNonQuery();
            }
        }, cancellationToken);
}
