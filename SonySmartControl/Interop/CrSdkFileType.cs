namespace SonySmartControl.Interop;

/// <summary>CrDeviceProperty_FileType / CrFileType（CrDeviceProperty.h）。</summary>
public enum CrSdkFileType : ushort
{
    None = 0x0000,
    Jpeg = 0x0001,
    Raw = 0x0002,
    RawJpeg = 0x0003,
    RawHeif = 0x0004,
    Heif = 0x0005,
}
