namespace SonySmartControl.Interop;

/// <summary>CrControlCode.h 中的常用值（完整列表见官方 SDK 头文件）。</summary>
public static class CrSdkControlCodes
{
    public const uint RemoteTouchOperation = 0x0000_D2E4;
    public const uint CancelRemoteTouchOperation = 0x0000_D2E5;
    public const uint S1Button = 0x0000_D2C1;
    public const uint S2Button = 0x0000_D2C2;
    
    // ---- Focus operation (相对/绝对对焦) ----
    // 取自官方 CrControlCode.h（CrControlCode enum：CrControlCode_FocusOperation / CrControlCode_FocusOperationWithInt16 等）
    public const uint CancelFocusPosition = 0x0000F002;
    public const uint FocusOperation = 0x0000D2EF;
    public const uint FocusOperationWithInt16 = 0x0000F004;
    public const uint LoadZoomAndFocusPosition = 0x0000D2EA;

    public const uint Release = 0x0001_0001;
}
