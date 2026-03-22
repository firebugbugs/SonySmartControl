namespace SonySmartControl.Interop;

/// <summary>CrDeviceProperty_S1 等使用的锁定状态（半按快门对焦）。</summary>
public enum CrSdkLockIndicator : ushort
{
    Unknown = 0x0000,
    Unlocked = 0x0001,
    Locked = 0x0002,
}
