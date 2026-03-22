namespace SonySmartControl.Interop;

/// <summary>
/// CrPropertyRAWJPCSaveImage（CrDeviceProperty_RAW_J_PC_Save_Image，CrDataType_UInt16Array）。
/// 遥控保存到 PC 时，在 RAW+JPEG 等双格式下需显式指定传到电脑的文件；否则机身可能仅为 JPEG。
/// </summary>
public enum CrSdkRawJpcPcSaveImage : ushort
{
    RawAndJpeg = 0,
    JpegOnly = 1,
    RawOnly = 2,
    RawAndHeif = 3,
    HeifOnly = 4,
}
