namespace SonySmartControl.Interop;

/// <summary>CrControlCode.h 中的常用值（完整列表见官方 SDK 头文件）。</summary>
public static class CrSdkControlCodes
{
    public const uint RemoteTouchOperation = 0x0000_D2E4;
    public const uint CancelRemoteTouchOperation = 0x0000_D2E5;
    public const uint S1Button = 0x0000_D2C1;
    public const uint S2Button = 0x0000_D2C2;
    public const uint Release = 0x0001_0001;
}
