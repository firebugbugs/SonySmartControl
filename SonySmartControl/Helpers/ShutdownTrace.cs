using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SonySmartControl.Helpers;

/// <summary>
/// 关窗/退出链路诊断日志（写入历史日志 SQLite），用于定位“窗口关闭但进程未退出/后台线程残留”。
/// 不依赖 DI，任何阶段都可调用；写入 app_logs.sqlite 的 app_log 表，历史日志窗口可直接查看。
/// </summary>
public static class ShutdownTrace
{
    private static readonly object Gate = new();
    private static volatile bool _enabled;

    /// <summary>仅在真正进入“应用退出流程”时打开诊断，避免手动断开时刷屏。</summary>
    public static void EnableForShutdown() => _enabled = true;

    private static string GetDbPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SonySmartControl");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "app_logs.sqlite");
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
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

    public static void Write(string message, Exception? ex = null)
    {
        if (!_enabled)
            return;

        try
        {
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var pid = 0;
            try { pid = Environment.ProcessId; } catch { /* ignore */ }
            var tid = 0;
            try { tid = Environment.CurrentManagedThreadId; } catch { /* ignore */ }

            var line = ex == null
                ? $"[退出诊断] pid={pid} tid={tid} {message}"
                : $"[退出诊断] pid={pid} tid={tid} {message} | ex={ex.GetType().Name}: {ex.Message}\n{ex}";

            lock (Gate)
            {
                using var conn = new SqliteConnection($"Data Source={GetDbPath()}");
                conn.Open();

                // 退出阶段 SQLite 可能正被“历史日志窗口”读取；这里加 busy timeout + WAL，
                // 尽量保证诊断日志能写入，而不是因为短暂锁竞争丢失。
                try
                {
                    using var pragma = conn.CreateCommand();
                    pragma.CommandText =
                        """
                        PRAGMA journal_mode=WAL;
                        PRAGMA synchronous=NORMAL;
                        PRAGMA busy_timeout=1200;
                        """;
                    pragma.ExecuteNonQuery();
                }
                catch
                {
                    // 忽略：不影响主流程
                }

                EnsureSchema(conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO app_log (created_at, message) VALUES ($ts, $msg);";
                cmd.Parameters.AddWithValue("$ts", now);
                cmd.Parameters.AddWithValue("$msg", line);
                cmd.ExecuteNonQuery();
            }

            try { Debug.WriteLine(line); } catch { /* ignore */ }
        }
        catch
        {
            // 兜底：日志本身不应影响退出链路
        }
    }
}

