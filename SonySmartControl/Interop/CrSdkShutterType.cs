namespace SonySmartControl.Interop;

/// <summary>CrShutterType（CrDeviceProperty_ShutterType，CrDataType_UInt8Array）。</summary>
public enum CrSdkShutterType : byte
{
    Auto = 0x01,
    MechanicalShutter = 0x02,
    ElectronicShutter = 0x03,
}
