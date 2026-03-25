using System.Globalization;

namespace SonySmartControl.Models;

/// <summary>SQLite 中一条应用状态/消息日志。</summary>
public sealed class AppLogEntry
{
    public long Id { get; init; }

    /// <summary>UTC，写入数据库时使用 ISO8601 字符串。</summary>
    public DateTime CreatedAtUtc { get; init; }

    public string Message { get; init; } = "";

    public string TimeLocalText =>
        CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
