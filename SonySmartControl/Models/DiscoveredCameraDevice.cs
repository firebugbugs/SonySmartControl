using System.Globalization;

namespace SonySmartControl.Models;

/// <summary>CrSDK 枚举到的一台机身（未连接）。</summary>
public sealed record DiscoveredCameraDevice(
    int Index,
    string ModelName,
    string ConnectionTypeText,
    string EndpointText)
{
    /// <summary>列表序号（从 1 开始展示）。</summary>
    public string OrdinalLabel => (Index + 1).ToString(CultureInfo.InvariantCulture);
}
