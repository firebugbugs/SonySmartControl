namespace SonySmartControl.Interop;

/// <summary>
/// CrDeviceProperty.h 中 CrDevicePropertyCode 的数值（升级 SDK 时请用官方头文件核对）。
/// </summary>
public static class CrSdkDevicePropertyCodes
{
    public const uint S1 = 0x01;

    public const uint FNumber = 0x0100;

    /// <summary>CrDeviceProperty_ExposureBiasCompensation（曝光补偿，数值×1000）。</summary>
    public const uint ExposureBiasCompensation = 0x0101;
    /// <summary>CrDeviceProperty_FlashCompensation（闪光补偿，数值×1000）。</summary>
    public const uint FlashCompensation = 0x0102;

    public const uint ShutterSpeed = 0x0103;
    public const uint IsoSensitivity = 0x0104;
    public const uint ExposureProgramMode = 0x0105;

    /// <summary>CrDeviceProperty_FocusMode（与 CrSDK 枚举一致）。</summary>
    public const uint FocusMode = 0x0109;
    /// <summary>CrDeviceProperty_FlashMode（自动/关闭/强制/慢速等）。</summary>
    public const uint FlashMode = 0x010A;

    /// <summary>CrDeviceProperty_DriveMode（静态拍照驱动：单张 / 连拍 / 延时等，CrDataType_UInt32Array）。</summary>
    public const uint DriveMode = 0x010E;

    /// <summary>CrDeviceProperty_FileType。</summary>
    public const uint FileType = 0x0106;

    /// <summary>CrDeviceProperty_StillImageStoreDestination（LiveViewDisplayEffect=0x0118 的下一项）。</summary>
    public const uint StillImageStoreDestination = 0x0119;

    /// <summary>CrDeviceProperty_PriorityKeySettings（StillImageStoreDestination 的下一项）。</summary>
    public const uint PriorityKeySettings = 0x011A;

    /// <summary>CrDeviceProperty_RAW_J_PC_Save_Image（S2=0x0500 起算第 8 项 → 0x0507）。</summary>
    public const uint RawJpcPcSaveImage = 0x0507;

    /// <summary>CrDeviceProperty_DispMode（背屏 DISP；Monitor Off=熄屏）。</summary>
    public const uint DispMode = 0x0142;

    /// <summary>CrDeviceProperty_DispModeStill（静态拍照时背屏 DISP，优先于通用 DispMode）。</summary>
    public const uint DispModeStill = 0x033F;

    /// <summary>CrDeviceProperty_ShutterType（机械/电子/自动快门；CrDataType_UInt8Array）。</summary>
    /// <remarks>
    /// 须与官方 <c>CrDeviceProperty.h</c> 中枚举值一致。切勿写成 <c>0x01AE</c>：该值为
    /// <c>CrDeviceProperty_PictureProfile_BlackGammaLevel</c>，会导致写快门类型实际改到无关属性或报 ErrControlFailed。
    /// </remarks>
    public const uint ShutterType = 0x1A9;

    /// <summary>CrFunctionOfRemoteTouchOperation 等属性使用。</summary>
    public const uint FunctionOfRemoteTouchOperation = 0x191;

    /// <summary>CrDeviceProperty_RemoteTouchOperationEnableStatus（是否允许遥控触摸对焦）。</summary>
    public const uint RemoteTouchOperationEnableStatus = 0x754;
}
