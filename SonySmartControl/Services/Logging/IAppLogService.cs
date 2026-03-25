using SonySmartControl.Models;

namespace SonySmartControl.Services.Logging;

/// <summary>将主界面状态消息等写入 SQLite，供历史日志查询。</summary>
public interface IAppLogService
{
    /// <summary>追加一条日志（与主界面当前显示文案一致）。</summary>
    void Append(string message);

    /// <summary>按页查询：第 0 页为最新记录；按 id 降序。</summary>
    Task<(IReadOnlyList<AppLogEntry> Items, int TotalCount)> GetPageAsync(
        int pageIndex0,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>清空全部日志。</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
