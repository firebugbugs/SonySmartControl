namespace SonySmartControl.Interop;

/// <summary>与 native SonyCrBridgeApi.h 中 SonyCrStatus 对应。</summary>
public enum SonyCrStatus
{
    Ok = 0,
    ErrInitFailed = -1,
    ErrNotInitialized = -2,
    ErrEnumFailed = -3,
    ErrInvalidIndex = -4,
    ErrBufferTooSmall = -5,
    ErrConnectFailed = -6,
    ErrNotConnected = -7,
    ErrControlFailed = -8,
    ErrInvalidParam = -9,
    ErrSdkNotLinked = -999,
    ErrNotImplemented = -100,
}
