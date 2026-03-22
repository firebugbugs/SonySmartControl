namespace SonySmartControl.Interop;

/// <summary>与 CrSDK CrDefines.h 中 CrDataType 数值一致；SetDeviceProperty 时传入。</summary>
public enum CrSdkDataType : uint
{
    Undefined = 0x0000,
    UInt8 = 0x0001,
    UInt16 = 0x0002,
    UInt32 = 0x0003,
    UInt64 = 0x0004,
    UInt128 = 0x0005,
    SignBit = 0x1000,
    SInt8 = SignBit | UInt8,
    SInt16 = SignBit | UInt16,
    SInt32 = SignBit | UInt32,
    SInt64 = SignBit | UInt64,
    SInt128 = SignBit | UInt128,
    ArrayBit = 0x2000,
    UInt8Array = ArrayBit | UInt8,
    UInt16Array = ArrayBit | UInt16,
    UInt32Array = ArrayBit | UInt32,
    UInt64Array = ArrayBit | UInt64,
    RangeBit = 0x4000,
    UInt8Range = RangeBit | UInt8,
    UInt16Range = RangeBit | UInt16,
    UInt32Range = RangeBit | UInt32,
    UInt64Range = RangeBit | UInt64,
    Str = 0xFFFF,
}
