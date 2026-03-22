namespace SonySmartControl.Interop;

/// <summary>CrDispMode（CrDeviceProperty_DispMode，CrDataType_UInt8Array）。</summary>
public enum CrSdkDispMode : byte
{
    GraphicDisplay = 0x01,
    DisplayAllInfo = 0x02,
    NoDispInfo = 0x03,
    Histogram = 0x04,
    Level = 0x05,
    ForViewFinder = 0x06,
    MonitorOff = 0x07,
    HistogramAndLevel = 0x08,
}
