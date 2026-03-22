namespace SonySmartControl.Interop;

/// <summary>桥接 JSON 中单项属性快照。</summary>
public sealed class CrSdkShootingPropertySnapshot
{
    public ulong Value { get; init; }

    public bool Writable { get; init; }

    public bool Gettable { get; init; }

    public CrSdkDataType SetDataType { get; init; }

    public ulong[] Candidates { get; init; } = [];
}
