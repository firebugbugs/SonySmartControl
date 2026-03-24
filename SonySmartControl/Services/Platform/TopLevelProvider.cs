using Avalonia.Controls;

namespace SonySmartControl.Services.Platform;

/// <summary>由主窗口在 Loaded 时注册，生命周期内保持对当前 <see cref="TopLevel"/> 的引用。</summary>
public sealed class TopLevelProvider : ITopLevelProvider
{
    private TopLevel? _topLevel;

    public void SetTopLevel(TopLevel? topLevel) => _topLevel = topLevel;

    public TopLevel? GetTopLevel() => _topLevel;
}
