namespace SonySmartControl.Interop;

/// <summary>曝光下拉项：显示名 + 机身枚举/编码值。</summary>
public sealed class ShootingChoiceItem(string display, ulong value)
{
    public string Display { get; } = display;

    public ulong Value { get; } = value;

    /// <summary>侧栏 <c>WheelPickerCombo</c> 与列表项默认文本均用此项。</summary>
    public override string ToString() => Display;
}
