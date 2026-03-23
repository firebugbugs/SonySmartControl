/**
 * Sony CrSDK C 桥接 — SCRSDK::Connect / GetLiveViewImage 等，供 C# 实时预览。
 */
#if !defined(SONY_CR_BRIDGE_STUB)
#define SONY_CR_BRIDGE_STUB 1
#endif

#include "SonyCrBridgeApi.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <future>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#endif

#if !SONY_CR_BRIDGE_STUB
#include "CameraRemote_SDK.h"
#include "IDeviceCallback.h"
#endif

#if !SONY_CR_BRIDGE_STUB
namespace
{
using namespace SCRSDK;

ICrEnumCameraObjectInfo* g_enumList = nullptr;
bool g_inited = false;
CrDeviceHandle g_deviceHandle = 0;
std::mutex g_mutex;

/** GetRemoteTransferContentsDataFile 异步完成信号（与 SimpleCli RemoteTransferMode 一致，依赖 OnNotifyRemoteTransferResult）。 */
std::mutex g_remoteTransferWaitMutex;
std::promise<void>* g_remoteTransferPromise = nullptr;

class BridgeDeviceCallback final : public IDeviceCallback
{
public:
    std::atomic_bool connected{false};

    void OnConnected(DeviceConnectionVersioin /*version*/) override { connected.store(true); }

    void OnDisconnected(CrInt32u /*error*/) override { connected.store(false); }

    void OnPropertyChanged() override {}

    void OnPropertyChangedCodes(CrInt32u /*num*/, CrInt32u* /*codes*/) override {}

    void OnLvPropertyChanged() override {}

    void OnLvPropertyChangedCodes(CrInt32u /*num*/, CrInt32u* /*codes*/) override {}

    void OnCompleteDownload(CrChar* /*filename*/, CrInt32u /*type*/) override {}

    void OnNotifyContentsTransfer(CrInt32u /*notify*/, CrContentHandle /*handle*/, CrChar* /*filename*/) override {}

    void OnNotifyRemoteTransferResult(CrInt32u notify, CrInt32u /*per*/, CrChar* /*filename*/) override
    {
        std::lock_guard<std::mutex> lk(g_remoteTransferWaitMutex);
        if (!g_remoteTransferPromise)
            return;
        std::promise<void>* const p = g_remoteTransferPromise;
        switch (notify)
        {
        case CrNotify_RemoteTransfer_Result_OK:
            p->set_value();
            g_remoteTransferPromise = nullptr;
            break;
        case CrNotify_RemoteTransfer_Result_NG:
        case CrNotify_RemoteTransfer_Result_DeviceBusy:
            try
            {
                p->set_exception(std::make_exception_ptr(std::runtime_error("RemoteTransfer_NG")));
            }
            catch (...)
            {
            }
            g_remoteTransferPromise = nullptr;
            break;
        default:
            break;
        }
    }

    void OnNotifyRemoteTransferResult(CrInt32u /*notify*/, CrInt32u /*per*/, CrInt8u* /*data*/, CrInt64u /*size*/) override {}

    void OnWarning(CrInt32u /*warning*/) override {}

    void OnError(CrInt32u /*error*/) override {}
};

BridgeDeviceCallback g_callback;

std::unique_ptr<CrImageDataBlock> g_imageBlock;
std::vector<CrInt8u> g_imageBuffer;
CrInt32u g_cachedBufSize = 0;

std::mutex g_focusFramesJsonMutex;
std::string g_lastFocusFramesJson = "[]";

static void UpdateFocusFramesJsonFromLiveViewProps(CrLiveViewProperty* props, CrInt32 num)
{
    if (!props || num <= 0)
    {
        std::lock_guard<std::mutex> fj(g_focusFramesJsonMutex);
        g_lastFocusFramesJson = "[]";
        return;
    }

    std::string json;
    json.push_back('[');
    bool first = true;
    for (CrInt32 i = 0; i < num; ++i)
    {
        CrLiveViewProperty& p = props[i];
        if (p.GetFrameInfoType() != CrFrameInfoType_FocusFrameInfo)
            continue;
        const CrInt32u sz = p.GetValueSize();
        CrInt8u* val = p.GetValue();
        if (!val || sz < sizeof(CrFocusFrameInfo))
            continue;
        const int count = static_cast<int>(sz / sizeof(CrFocusFrameInfo));
        const CrFocusFrameInfo* frames = reinterpret_cast<const CrFocusFrameInfo*>(val);
        for (int j = 0; j < count; ++j)
        {
            const CrFocusFrameInfo& f = frames[j];
            if (!first)
                json.push_back(',');
            first = false;
            char buf[384];
            std::snprintf(
                buf,
                sizeof(buf),
                "{\"t\":%u,\"s\":%u,\"xn\":%u,\"xd\":%u,\"yn\":%u,\"yd\":%u,\"w\":%u,\"h\":%u}",
                static_cast<unsigned>(f.type),
                static_cast<unsigned>(f.state),
                static_cast<unsigned>(f.xNumerator),
                static_cast<unsigned>(f.xDenominator),
                static_cast<unsigned>(f.yNumerator),
                static_cast<unsigned>(f.yDenominator),
                static_cast<unsigned>(f.width),
                static_cast<unsigned>(f.height));
            json += buf;
        }
    }
    if (first)
        json = "[]";
    else
        json.push_back(']');
    std::lock_guard<std::mutex> fj(g_focusFramesJsonMutex);
    g_lastFocusFramesJson = std::move(json);
}

static SonyCrStatus ToStatus(bool ok) { return ok ? SONY_CR_OK : SONY_CR_ERR_INIT_FAILED; }

static SonyCrStatus ToEnumStatus(CrError err)
{
    return CR_FAILED(err) ? SONY_CR_ERR_ENUM_FAILED : SONY_CR_OK;
}

static SonyCrStatus ToConnectStatus(CrError err)
{
    return CR_FAILED(err) ? SONY_CR_ERR_CONNECT_FAILED : SONY_CR_OK;
}

/** 与 RemoteCli（CameraDevice.cpp）一致：PriorityKeySettings 常用 CrDataType_UInt32Array；失败时再试 UInt16。 */
static CrError SetPriorityKeyPcRemote(CrDeviceHandle h)
{
    SCRSDK::CrDeviceProperty pk;
    pk.SetCode(SCRSDK::CrDevicePropertyCode::CrDeviceProperty_PriorityKeySettings);
    pk.SetCurrentValue(static_cast<CrInt64u>(SCRSDK::CrPriorityKey_PCRemote));
    pk.SetValueType(SCRSDK::CrDataType::CrDataType_UInt32Array);
    CrError err = SCRSDK::SetDeviceProperty(h, &pk);
    if (!CR_FAILED(err))
        return err;
    pk.SetValueType(SCRSDK::CrDataType::CrDataType_UInt16);
    return SCRSDK::SetDeviceProperty(h, &pk);
}

static std::string CrModelToUtf8(const ICrCameraObjectInfo* info)
{
    if (!info)
        return {};
#if defined(_WIN32) && (defined(UNICODE) || defined(_UNICODE))
    const auto* w = reinterpret_cast<const wchar_t*>(info->GetModel());
    const CrInt32u nChars = info->GetModelSize();
    if (!w || nChars == 0)
        return {};
    const int cch = static_cast<int>(nChars);
    const int need = WideCharToMultiByte(CP_UTF8, 0, w, cch, nullptr, 0, nullptr, nullptr);
    if (need <= 0)
        return {};
    std::string out(static_cast<size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, cch, out.data(), need, nullptr, nullptr);
    return out;
#else
    const char* s = reinterpret_cast<const char*>(info->GetModel());
    const CrInt32u n = info->GetModelSize();
    if (!s || n == 0)
        return {};
    return std::string(s, static_cast<size_t>(n));
#endif
}

static const ICrCameraObjectInfo* GetInfoOrNull(int index)
{
    if (!g_enumList || index < 0)
        return nullptr;
    const CrInt32u n = g_enumList->GetCount();
    if (static_cast<CrInt32u>(index) >= n)
        return nullptr;
    return g_enumList->GetCameraObjectInfo(static_cast<CrInt32u>(index));
}

static void ClearLiveViewBuffers()
{
    g_imageBlock.reset();
    g_imageBuffer.clear();
    g_cachedBufSize = 0;
}

static void DisconnectDeviceUnlocked()
{
    if (g_deviceHandle != 0)
    {
        Disconnect(g_deviceHandle);
        ReleaseDevice(g_deviceHandle);
        g_deviceHandle = 0;
    }
    g_callback.connected.store(false);
    ClearLiveViewBuffers();
    {
        std::lock_guard<std::mutex> fj(g_focusFramesJsonMutex);
        g_lastFocusFramesJson = "[]";
    }
}

static bool WaitForConnected(int timeoutMs)
{
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeoutMs);
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (g_callback.connected.load())
            return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return g_callback.connected.load();
}

/** 将「遥控触摸」设为点对焦（Spot AF），与 ExecuteControlCode RemoteTouch 配合；失败则忽略。 */
static void TrySetRemoteTouchSpotAf(CrDeviceHandle h)
{
    CrDeviceProperty prop;
    prop.SetCode(CrDevicePropertyCode::CrDeviceProperty_FunctionOfRemoteTouchOperation);
    prop.SetCurrentValue(static_cast<CrInt64u>(CrFunctionOfRemoteTouchOperation_Spot_AF));
    prop.SetValueType(CrDataType::CrDataType_UInt8);
    (void)SetDeviceProperty(h, &prop);
}

} // namespace
#endif

#if !SONY_CR_BRIDGE_STUB
namespace
{
using namespace SCRSDK;

static bool IsMovieExposureProgram(CrInt32u ep)
{
    switch (static_cast<CrExposureProgram>(ep))
    {
    case CrExposure_Movie_P:
    case CrExposure_Movie_A:
    case CrExposure_Movie_S:
    case CrExposure_Movie_M:
    case CrExposure_Movie_Auto:
    case CrExposure_Movie_F:
    case CrExposure_Movie_SQMotion_P:
    case CrExposure_Movie_SQMotion_A:
    case CrExposure_Movie_SQMotion_S:
    case CrExposure_Movie_SQMotion_M:
    case CrExposure_Movie_SQMotion_AUTO:
    case CrExposure_Movie_SQMotion_F:
    case CrExposure_HiFrameRate_P:
    case CrExposure_HiFrameRate_A:
    case CrExposure_HiFrameRate_S:
    case CrExposure_HiFrameRate_M:
    case CrExposure_MOVIE:
    case CrExposure_F_MovieOrSQMotion:
    case CrExposure_Movie_IntervalRec_F:
    case CrExposure_Movie_IntervalRec_P:
    case CrExposure_Movie_IntervalRec_A:
    case CrExposure_Movie_IntervalRec_S:
    case CrExposure_Movie_IntervalRec_M:
    case CrExposure_Movie_IntervalRec_AUTO:
        return true;
    default:
        return false;
    }
}

static void AppendU64Array(std::string& out, const CrInt8u* buf, CrInt32u byteSize, std::size_t elemSize)
{
    out.push_back('[');
    if (buf && byteSize >= static_cast<CrInt32u>(elemSize) && elemSize > 0)
    {
        const std::size_t n = static_cast<std::size_t>(byteSize) / elemSize;
        for (std::size_t i = 0; i < n; ++i)
        {
            if (i)
                out.push_back(',');
            unsigned long long v = 0;
            if (elemSize == 2)
            {
                std::uint16_t x;
                std::memcpy(&x, buf + i * elemSize, sizeof(x));
                v = x;
            }
            else if (elemSize == 4)
            {
                std::uint32_t x;
                std::memcpy(&x, buf + i * elemSize, sizeof(x));
                v = x;
            }
            else if (elemSize == 8)
            {
                std::uint64_t x;
                std::memcpy(&x, buf + i * elemSize, sizeof(x));
                v = x;
            }
            else if (elemSize == 1)
            {
                v = static_cast<unsigned long long>(buf[i]);
            }
            out += std::to_string(v);
        }
    }
    out.push_back(']');
}

static void AppendProp(
    std::string& out,
    const char* key,
    const CrDeviceProperty* prop,
    std::size_t candElemSize,
    CrInt32u setDataType)
{
    out.push_back('"');
    out += key;
    out += "\":";
    if (!prop)
    {
        out += "null";
        return;
    }
    out += "{\"v\":";
    out += std::to_string(static_cast<unsigned long long>(prop->GetCurrentValue()));
    out += ",\"w\":";
    out += prop->IsSetEnableCurrentValue() ? "1" : "0";
    out += ",\"set\":";
    out += std::to_string(static_cast<unsigned long long>(setDataType));
    out += ",\"g\":";
    out += prop->IsGetEnableCurrentValue() ? "1" : "0";
    out += ",\"c\":";
    AppendU64Array(out, prop->GetValues(), prop->GetValueSize(), candElemSize);
    out.push_back('}');
}

static void BuildShootingStateJson(std::string& out)
{
    // 批量 8 项；ShutterType 单独查，避免部分固件对「9 码一批」失败。
    CrInt32u codes[8] = {
        CrDevicePropertyCode::CrDeviceProperty_ExposureProgramMode,
        CrDevicePropertyCode::CrDeviceProperty_FNumber,
        CrDevicePropertyCode::CrDeviceProperty_ShutterSpeed,
        CrDevicePropertyCode::CrDeviceProperty_IsoSensitivity,
        CrDevicePropertyCode::CrDeviceProperty_ExposureBiasCompensation,
        CrDevicePropertyCode::CrDeviceProperty_FocusMode,
        CrDevicePropertyCode::CrDeviceProperty_RemoteTouchOperationEnableStatus,
        CrDevicePropertyCode::CrDeviceProperty_DispModeStill,
    };

    CrDeviceProperty* propList = nullptr;
    CrInt32 nprop = 0;
    const CrError err = GetSelectDeviceProperties(g_deviceHandle, 8, codes, &propList, &nprop);
    if (CR_FAILED(err) || propList == nullptr || nprop < 1)
    {
        if (propList != nullptr)
            ReleaseDeviceProperties(g_deviceHandle, propList);
        out = "{\"video\":false,\"ep\":null,\"fn\":null,\"ss\":null,\"iso\":null,\"ev\":null,\"fm\":null,\"rtouch\":null,\"dm\":null,\"st\":null,\"drv\":null}";
        return;
    }

    const CrDeviceProperty* ep = nullptr;
    const CrDeviceProperty* fn = nullptr;
    const CrDeviceProperty* ss = nullptr;
    const CrDeviceProperty* iso = nullptr;
    const CrDeviceProperty* ev = nullptr;
    const CrDeviceProperty* fm = nullptr;
    const CrDeviceProperty* rtouch = nullptr;
    const CrDeviceProperty* dm = nullptr;
    CrDeviceProperty* rtouchOnly = nullptr;
    CrInt32 nRtouchOnly = 0;
    CrDeviceProperty* dmStillOnly = nullptr;
    CrInt32 nDmStillOnly = 0;
    CrDeviceProperty* dmGenericOnly = nullptr;
    CrInt32 nDmGenericOnly = 0;

    for (CrInt32 i = 0; i < nprop; ++i)
    {
        const CrInt32u c = propList[i].GetCode();
        if (c == CrDevicePropertyCode::CrDeviceProperty_ExposureProgramMode)
            ep = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_FNumber)
            fn = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_ShutterSpeed)
            ss = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_IsoSensitivity)
            iso = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_ExposureBiasCompensation)
            ev = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_FocusMode)
            fm = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_RemoteTouchOperationEnableStatus)
            rtouch = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_DispModeStill)
            dm = &propList[i];
    }

    // 部分机型批量 GetSelectDeviceProperties 不返回遥控触摸项：再单独查询一次。
    if (rtouch == nullptr)
    {
        CrInt32u codeRtouch = CrDevicePropertyCode::CrDeviceProperty_RemoteTouchOperationEnableStatus;
        const CrError errRt = GetSelectDeviceProperties(g_deviceHandle, 1, &codeRtouch, &rtouchOnly, &nRtouchOnly);
        if (!CR_FAILED(errRt) && rtouchOnly != nullptr && nRtouchOnly >= 1)
            rtouch = &rtouchOnly[0];
    }

    // 静态拍照背屏优先 DispModeStill；批量缺省时单独补查。
    if (dm == nullptr)
    {
        CrInt32u codeDm = CrDevicePropertyCode::CrDeviceProperty_DispModeStill;
        const CrError errDm =
            GetSelectDeviceProperties(g_deviceHandle, 1, &codeDm, &dmStillOnly, &nDmStillOnly);
        if (!CR_FAILED(errDm) && dmStillOnly != nullptr && nDmStillOnly >= 1)
            dm = &dmStillOnly[0];
    }
    if (dm == nullptr)
    {
        CrInt32u codeDm = CrDevicePropertyCode::CrDeviceProperty_DispMode;
        const CrError errDm =
            GetSelectDeviceProperties(g_deviceHandle, 1, &codeDm, &dmGenericOnly, &nDmGenericOnly);
        if (!CR_FAILED(errDm) && dmGenericOnly != nullptr && nDmGenericOnly >= 1)
            dm = &dmGenericOnly[0];
    }
    const CrDeviceProperty* shtype = nullptr;
    CrDeviceProperty* shtypeOnly = nullptr;
    CrInt32 nShtypeOnly = 0;
    {
        CrInt32u codeSt = CrDevicePropertyCode::CrDeviceProperty_ShutterType;
        const CrError errSt = GetSelectDeviceProperties(g_deviceHandle, 1, &codeSt, &shtypeOnly, &nShtypeOnly);
        if (!CR_FAILED(errSt) && shtypeOnly != nullptr && nShtypeOnly >= 1)
            shtype = &shtypeOnly[0];
    }

    const CrDeviceProperty* drv = nullptr;
    CrDeviceProperty* drvOnly = nullptr;
    CrInt32 nDrvOnly = 0;
    {
        CrInt32u codeDrv = CrDevicePropertyCode::CrDeviceProperty_DriveMode;
        const CrError errDrv =
            GetSelectDeviceProperties(g_deviceHandle, 1, &codeDrv, &drvOnly, &nDrvOnly);
        if (!CR_FAILED(errDrv) && drvOnly != nullptr && nDrvOnly >= 1)
            drv = &drvOnly[0];
    }

    bool video = false;
    if (ep != nullptr)
        video = IsMovieExposureProgram(static_cast<CrInt32u>(ep->GetCurrentValue()));

    out.clear();
    out.reserve(4096);
    out += "{\"video\":";
    out += video ? "true" : "false";
    out += ',';

    // 候选列表元素宽度与 RemoteCli load_properties 一致；Set 时的 CrDataType 与 CameraDevice 示例一致。
    AppendProp(out, "ep", ep, sizeof(std::uint32_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "fn", fn, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "ss", ss, sizeof(std::uint32_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt32Array));
    out += ',';
    AppendProp(out, "iso", iso, sizeof(std::uint32_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt32Array));
    out += ',';
    AppendProp(out, "ev", ev, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "fm", fm, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "rtouch", rtouch, sizeof(std::uint8_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt8Array));
    out += ',';
    AppendProp(out, "dm", dm, sizeof(std::uint8_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt8Array));
    out += ',';
    AppendProp(out, "st", shtype, sizeof(std::uint8_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt8Array));
    out += ',';
    AppendProp(out, "drv", drv, sizeof(std::uint32_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt32Array));

    out.push_back('}');

    if (rtouchOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, rtouchOnly);
    if (dmStillOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, dmStillOnly);
    if (dmGenericOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, dmGenericOnly);
    if (shtypeOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, shtypeOnly);
    if (drvOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, drvOnly);
    ReleaseDeviceProperties(g_deviceHandle, propList);
}
} // namespace
#endif

extern "C" {

SONY_CR_API SonyCrStatus SonyCr_Init(void)
{
#if SONY_CR_BRIDGE_STUB
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_inited)
        return SONY_CR_OK;
    const bool ok = Init();
    g_inited = ok;
    return ToStatus(ok);
#endif
}

SONY_CR_API void SonyCr_Release(void)
{
#if !SONY_CR_BRIDGE_STUB
    std::lock_guard lock(g_mutex);
    DisconnectDeviceUnlocked();
    if (g_enumList)
    {
        g_enumList->Release();
        g_enumList = nullptr;
    }
    if (g_inited)
    {
        Release();
        g_inited = false;
    }
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetSdkVersion(unsigned int* outVersion)
{
#if SONY_CR_BRIDGE_STUB
    if (outVersion)
        *outVersion = 0;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!outVersion)
        return SONY_CR_ERR_NOT_INITIALIZED;
    *outVersion = static_cast<unsigned int>(GetSDKVersion());
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_EnumCameraDevicesRefresh(void)
{
#if SONY_CR_BRIDGE_STUB
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (!g_inited)
        return SONY_CR_ERR_NOT_INITIALIZED;
    DisconnectDeviceUnlocked();
    if (g_enumList)
    {
        g_enumList->Release();
        g_enumList = nullptr;
    }
    const CrError st = EnumCameraObjects(&g_enumList);
    if (CR_FAILED(st))
    {
        if (g_enumList != nullptr)
        {
            g_enumList->Release();
            g_enumList = nullptr;
        }
        return ToEnumStatus(st);
    }
    if (g_enumList == nullptr)
        return SONY_CR_ERR_ENUM_FAILED;
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetCameraDeviceCount(int* outCount)
{
#if SONY_CR_BRIDGE_STUB
    if (outCount)
        *outCount = 0;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!outCount)
        return SONY_CR_ERR_NOT_INITIALIZED;
    if (!g_enumList)
        return SONY_CR_ERR_ENUM_FAILED;
    *outCount = static_cast<int>(g_enumList->GetCount());
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetCameraModelUtf8Length(int index, int* outLengthBytes)
{
#if SONY_CR_BRIDGE_STUB
    if (outLengthBytes)
        *outLengthBytes = 0;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!outLengthBytes)
        return SONY_CR_ERR_NOT_INITIALIZED;
    const auto* info = GetInfoOrNull(index);
    if (!info)
        return SONY_CR_ERR_INVALID_INDEX;
    const std::string u8 = CrModelToUtf8(info);
    *outLengthBytes = static_cast<int>(u8.size() + 1);
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetCameraModelUtf8(int index, char* buffer, int bufferSizeBytes)
{
#if SONY_CR_BRIDGE_STUB
    (void)index;
    (void)buffer;
    (void)bufferSizeBytes;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    const auto* info = GetInfoOrNull(index);
    if (!info)
        return SONY_CR_ERR_INVALID_INDEX;
    const std::string u8 = CrModelToUtf8(info);
    const int need = static_cast<int>(u8.size() + 1);
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, u8.c_str(), static_cast<size_t>(need));
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_ConnectRemoteByIndex(int index)
{
#if SONY_CR_BRIDGE_STUB
    (void)index;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (!g_inited)
        return SONY_CR_ERR_NOT_INITIALIZED;
    if (!g_enumList)
        return SONY_CR_ERR_ENUM_FAILED;

    const auto* infoConst = GetInfoOrNull(index);
    if (!infoConst)
        return SONY_CR_ERR_INVALID_INDEX;

    DisconnectDeviceUnlocked();

    auto* info = const_cast<ICrCameraObjectInfo*>(infoConst);
    CrError err = Connect(info, &g_callback, &g_deviceHandle, CrSdkControlMode_Remote);
    if (CR_FAILED(err))
        return ToConnectStatus(err);

    if (!WaitForConnected(20000))
    {
        DisconnectDeviceUnlocked();
        return SONY_CR_ERR_CONNECT_FAILED;
    }

    // 部分机型需在菜单中开启 Live View；若此处 SetDeviceSetting 失败可忽略，依赖机身默认
    {
        const CrError lvErr = SetDeviceSetting(g_deviceHandle, Setting_Key_EnableLiveView, 1);
        (void)lvErr;
    }

    // 遥控触摸默认可能是「跟踪」等，点击坐标对焦需尽量设为 Spot AF（仍可能被菜单/对焦模式限制）
    TrySetRemoteTouchSpotAf(g_deviceHandle);

    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_Disconnect(void)
{
#if SONY_CR_BRIDGE_STUB
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (!g_inited)
        return SONY_CR_ERR_NOT_INITIALIZED;
    DisconnectDeviceUnlocked();
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_LiveView_GetLastJpeg(unsigned char* buffer, int bufferSize, int* outWritten)
{
#if SONY_CR_BRIDGE_STUB
    (void)buffer;
    (void)bufferSize;
    if (outWritten)
        *outWritten = 0;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!outWritten)
        return SONY_CR_ERR_NOT_INITIALIZED;
    *outWritten = 0;
    if (!buffer || bufferSize < 1)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;

    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    CrLiveViewProperty* props = nullptr;
    CrInt32 num = 0;
    CrError err = GetLiveViewProperties(g_deviceHandle, &props, &num);
    if (CR_FAILED(err))
    {
        UpdateFocusFramesJsonFromLiveViewProps(nullptr, 0);
        return SONY_CR_ERR_NOT_CONNECTED;
    }
    if (props != nullptr)
    {
        UpdateFocusFramesJsonFromLiveViewProps(props, num);
        ReleaseLiveViewProperties(g_deviceHandle, props);
    }
    else
        UpdateFocusFramesJsonFromLiveViewProps(nullptr, 0);

    CrImageInfo inf;
    err = GetLiveViewImageInfo(g_deviceHandle, &inf);
    if (CR_FAILED(err))
        return SONY_CR_ERR_NOT_CONNECTED;

    const CrInt32u bufSize = inf.GetBufferSize();
    if (bufSize < 1)
        return SONY_CR_ERR_NOT_CONNECTED;

    if (g_cachedBufSize != bufSize || !g_imageBlock)
    {
        g_imageBuffer.resize(bufSize);
        g_imageBlock = std::make_unique<CrImageDataBlock>();
        g_imageBlock->SetSize(bufSize);
        g_imageBlock->SetData(g_imageBuffer.data());
        g_cachedBufSize = bufSize;
    }

    err = GetLiveViewImage(g_deviceHandle, g_imageBlock.get());
    if (CR_FAILED(err))
    {
        // 与官方 Sample 一致：帧未更新时跳过本次，不报错
        if (err == CrWarning_Frame_NotUpdated) // CrError.h
            return SONY_CR_OK;
        return SONY_CR_ERR_NOT_CONNECTED;
    }

    const CrInt32u imageSize = g_imageBlock->GetImageSize();
    CrInt8u* imgData = g_imageBlock->GetImageData();
    if (!imgData || imageSize == 0)
        return SONY_CR_OK;

    if (static_cast<int>(imageSize) > bufferSize)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;

    std::memcpy(buffer, imgData, static_cast<size_t>(imageSize));
    *outWritten = static_cast<int>(imageSize);
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_RemoteTouchAf(int x, int y)
{
#if SONY_CR_BRIDGE_STUB
    (void)x;
    (void)y;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    if (x < 0 || x > 639 || y < 0 || y > 479)
        return SONY_CR_ERR_INVALID_PARAM;
    const CrInt64u value = (static_cast<CrInt64u>(static_cast<CrInt32u>(x) & 0xFFFFu) << 16)
        | static_cast<CrInt64u>(static_cast<CrInt32u>(y) & 0xFFFFu);
    const CrError err =
        SCRSDK::ExecuteControlCodeValue(g_deviceHandle, SCRSDK::CrControlCode_RemoteTouchOperation, value);
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_ExecuteControlCodeValue(unsigned int code, unsigned long long value)
{
#if SONY_CR_BRIDGE_STUB
    (void)code;
    (void)value;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    const CrError err = SCRSDK::ExecuteControlCodeValue(
        g_deviceHandle, static_cast<SCRSDK::CrControlCode>(code), static_cast<CrInt64u>(value));
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_ExecuteControlCodeString(unsigned int code, const unsigned short* utf16, unsigned int length)
{
#if SONY_CR_BRIDGE_STUB
    (void)code;
    (void)utf16;
    (void)length;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!utf16 && length > 0)
        return SONY_CR_ERR_INVALID_PARAM;
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    if (length > 0xFFFFu)
        return SONY_CR_ERR_INVALID_PARAM;
    const CrError err = SCRSDK::ExecuteControlCodeString(
        g_deviceHandle,
        static_cast<SCRSDK::CrControlCode>(code),
        static_cast<CrInt16u>(length),
        reinterpret_cast<const CrInt16u*>(utf16));
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_SetDevicePropertyU64(unsigned int propertyCode, unsigned long long value, unsigned int crDataType)
{
#if SONY_CR_BRIDGE_STUB
    (void)propertyCode;
    (void)value;
    (void)crDataType;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    using SCRSDK::CrDataType;
    using SCRSDK::CrDeviceProperty;
    const auto dtPrimary = static_cast<CrDataType>(crDataType);
    CrError lastErr = SCRSDK::CrError_None;
    for (int attempt = 0; attempt < 6; ++attempt)
    {
        if (attempt > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(130));

        CrDeviceProperty prop;
        prop.SetCode(static_cast<CrInt32u>(propertyCode));
        prop.SetCurrentValue(static_cast<CrInt64u>(value));
        prop.SetValueType(dtPrimary);
        lastErr = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
        if (!CR_FAILED(lastErr))
            return SONY_CR_OK;

        // 部分属性 UInt8Array 与 UInt8 在固件侧等价；失败时试 UInt8（与 RemoteCli 声明仍以 Array 为准）。
        if (dtPrimary == CrDataType::CrDataType_UInt8Array)
        {
            prop.SetValueType(CrDataType::CrDataType_UInt8);
            lastErr = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
            if (!CR_FAILED(lastErr))
                return SONY_CR_OK;
        }
        // DriveMode 等：部分固件 UInt32Array 与 UInt32 等价（与 UInt8/UInt8Array 同理）。
        if (dtPrimary == CrDataType::CrDataType_UInt32Array)
        {
            prop.SetValueType(CrDataType::CrDataType_UInt32);
            lastErr = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
            if (!CR_FAILED(lastErr))
                return SONY_CR_OK;
        }
    }
    return SONY_CR_ERR_CONTROL_FAILED;
#endif
}

#if !SONY_CR_BRIDGE_STUB
static CrError ApplyMonitorDispModeOnePath(CrDeviceHandle h, bool useStill, CrInt8u mode)
{
    using SCRSDK::CrDeviceProperty;
    using SCRSDK::CrDevicePropertyCode;
    using SCRSDK::CrDataType;

    CrInt32u codeCand = static_cast<CrInt32u>(
        useStill ? CrDevicePropertyCode::CrDeviceProperty_DispModeCandidateStill
                 : CrDevicePropertyCode::CrDeviceProperty_DispModeCandidate);
    CrDeviceProperty* plist = nullptr;
    CrInt32 n = 0;
    CrError e = GetSelectDeviceProperties(h, 1, &codeCand, &plist, &n);
    CrInt32u mask = 0;
    if (!CR_FAILED(e) && plist != nullptr && n >= 1)
    {
        const CrInt8u* buf = plist[0].GetValues();
        const CrInt32u vsz = plist[0].GetValueSize();
        if (buf != nullptr && vsz >= 4u)
        {
            for (CrInt32u i = 0; i + 4u <= vsz; i += 4u)
            {
                CrInt32u v = 0;
                std::memcpy(&v, buf + i, sizeof(v));
                mask |= v;
            }
        }
        ReleaseDeviceProperties(h, plist);
    }
    if (mask == 0)
        mask = 0x3FFu;

    CrDeviceProperty propSetting;
    propSetting.SetCode(useStill ? CrDevicePropertyCode::CrDeviceProperty_DispModeSettingStill
                                 : CrDevicePropertyCode::CrDeviceProperty_DispModeSetting);
    propSetting.SetCurrentValue(static_cast<CrInt64u>(mask));
    propSetting.SetValueType(CrDataType::CrDataType_UInt32Array);
    e = SCRSDK::SetDeviceProperty(h, &propSetting);
    if (CR_FAILED(e))
        return e;
    std::this_thread::sleep_for(std::chrono::milliseconds(220));

    CrDeviceProperty propDm;
    propDm.SetCode(useStill ? CrDevicePropertyCode::CrDeviceProperty_DispModeStill
                            : CrDevicePropertyCode::CrDeviceProperty_DispMode);
    propDm.SetCurrentValue(static_cast<CrInt64u>(static_cast<CrInt8u>(mode)));
    propDm.SetValueType(CrDataType::CrDataType_UInt8Array);
    e = SCRSDK::SetDeviceProperty(h, &propDm);
    if (CR_FAILED(e))
    {
        propDm.SetValueType(CrDataType::CrDataType_UInt8);
        e = SCRSDK::SetDeviceProperty(h, &propDm);
    }
    return e;
}
#endif

SONY_CR_API SonyCrStatus SonyCr_ApplyMonitorDispMode(unsigned char mode)
{
#if SONY_CR_BRIDGE_STUB
    (void)mode;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    if (CR_FAILED(SetPriorityKeyPcRemote(g_deviceHandle)))
        return SONY_CR_ERR_CONTROL_FAILED;

    CrError e = ApplyMonitorDispModeOnePath(g_deviceHandle, true, static_cast<CrInt8u>(mode));
    if (CR_FAILED(e))
        e = ApplyMonitorDispModeOnePath(g_deviceHandle, false, static_cast<CrInt8u>(mode));
    return CR_FAILED(e) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_HalfPressShutterS1AfOnly(unsigned int holdMs)
{
#if SONY_CR_BRIDGE_STUB
    (void)holdMs;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    const CrError errPk = SetPriorityKeyPcRemote(g_deviceHandle);
    if (CR_FAILED(errPk))
        return SONY_CR_ERR_CONTROL_FAILED;

    SCRSDK::CrDeviceProperty prop;
    prop.SetCode(SCRSDK::CrDevicePropertyCode::CrDeviceProperty_S1);
    prop.SetCurrentValue(static_cast<CrInt64u>(SCRSDK::CrLockIndicator::CrLockIndicator_Locked));
    prop.SetValueType(SCRSDK::CrDataType::CrDataType_UInt16);
    CrError err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    if (CR_FAILED(err))
        return SONY_CR_ERR_CONTROL_FAILED;

    const unsigned int ms = (holdMs > 60000u) ? 60000u : holdMs;
    if (ms > 0u)
        std::this_thread::sleep_for(std::chrono::milliseconds(ms));

    prop.SetCurrentValue(static_cast<CrInt64u>(SCRSDK::CrLockIndicator::CrLockIndicator_Unlocked));
    err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_HalfPressShutterS1Press(void)
{
#if SONY_CR_BRIDGE_STUB
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    const CrError errPk = SetPriorityKeyPcRemote(g_deviceHandle);
    if (CR_FAILED(errPk))
        return SONY_CR_ERR_CONTROL_FAILED;

    SCRSDK::CrDeviceProperty prop;
    prop.SetCode(SCRSDK::CrDevicePropertyCode::CrDeviceProperty_S1);
    prop.SetCurrentValue(static_cast<CrInt64u>(SCRSDK::CrLockIndicator::CrLockIndicator_Locked));
    prop.SetValueType(SCRSDK::CrDataType::CrDataType_UInt16);
    CrError err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    if (CR_FAILED(err))
    {
        // Live View 等可能在两次 Set 之间抢锁；短延迟后重试一次，减轻 ErrControlFailed(-8)。
        std::this_thread::sleep_for(std::chrono::milliseconds(120));
        err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    }
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_ReleaseShutterDownUpThenS1Unlock(void)
{
#if SONY_CR_BRIDGE_STUB
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    using SCRSDK::CrCommandId;
    using SCRSDK::CrCommandParam;
    using SCRSDK::CrDataType;
    using SCRSDK::CrDeviceProperty;
    using SCRSDK::CrDevicePropertyCode;
    using SCRSDK::CrLockIndicator;

    CrError err = SCRSDK::SendCommand(
        g_deviceHandle,
        static_cast<CrInt32u>(CrCommandId::CrCommandId_Release),
        CrCommandParam::CrCommandParam_Down);
    if (CR_FAILED(err))
        return SONY_CR_ERR_CONTROL_FAILED;

    std::this_thread::sleep_for(std::chrono::milliseconds(35));

    err = SCRSDK::SendCommand(
        g_deviceHandle,
        static_cast<CrInt32u>(CrCommandId::CrCommandId_Release),
        CrCommandParam::CrCommandParam_Up);
    if (CR_FAILED(err))
        return SONY_CR_ERR_CONTROL_FAILED;

    // RemoteCli af_shutter：Release Up 后等待 1s 再 S1 Unlocked。
    std::this_thread::sleep_for(std::chrono::milliseconds(1000));

    CrDeviceProperty prop;
    prop.SetCode(CrDevicePropertyCode::CrDeviceProperty_S1);
    prop.SetCurrentValue(static_cast<CrInt64u>(CrLockIndicator::CrLockIndicator_Unlocked));
    prop.SetValueType(CrDataType::CrDataType_UInt16);
    err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_HalfPressShutterS1Release(void)
{
#if SONY_CR_BRIDGE_STUB
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    SCRSDK::CrDeviceProperty prop;
    prop.SetCode(SCRSDK::CrDevicePropertyCode::CrDeviceProperty_S1);
    prop.SetCurrentValue(static_cast<CrInt64u>(SCRSDK::CrLockIndicator::CrLockIndicator_Unlocked));
    prop.SetValueType(SCRSDK::CrDataType::CrDataType_UInt16);
    const CrError err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_SetRemoteTouchOperationEnable(int enable)
{
#if SONY_CR_BRIDGE_STUB
    (void)enable;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (enable != 0 && enable != 1)
        return SONY_CR_ERR_INVALID_PARAM;
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;

    (void)SetPriorityKeyPcRemote(g_deviceHandle);

    SCRSDK::CrDeviceProperty prop;
    prop.SetCode(SCRSDK::CrDevicePropertyCode::CrDeviceProperty_RemoteTouchOperationEnableStatus);
    prop.SetCurrentValue(static_cast<CrInt64u>(static_cast<CrInt8u>(enable)));
    prop.SetValueType(SCRSDK::CrDataType::CrDataType_UInt8Array);
    const CrError err = SCRSDK::SetDeviceProperty(g_deviceHandle, &prop);
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_SendCommand(unsigned int commandId, unsigned int commandParam)
{
#if SONY_CR_BRIDGE_STUB
    (void)commandId;
    (void)commandParam;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    const CrError err = SCRSDK::SendCommand(
        g_deviceHandle, static_cast<CrInt32u>(commandId), static_cast<SCRSDK::CrCommandParam>(commandParam));
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_SetDeviceSetting(unsigned int key, unsigned int value)
{
#if SONY_CR_BRIDGE_STUB
    (void)key;
    (void)value;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    const CrError err = SCRSDK::SetDeviceSetting(g_deviceHandle, static_cast<CrInt32u>(key), static_cast<CrInt32u>(value));
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_SetSaveInfoUtf16(const unsigned short* pathUtf16, const unsigned short* prefixUtf16, int saveNumber)
{
#if SONY_CR_BRIDGE_STUB
    (void)pathUtf16;
    (void)prefixUtf16;
    (void)saveNumber;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!pathUtf16)
        return SONY_CR_ERR_INVALID_PARAM;
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    CrChar* const path = const_cast<CrChar*>(reinterpret_cast<const CrChar*>(pathUtf16));
    CrChar* const prefix =
        prefixUtf16 ? const_cast<CrChar*>(reinterpret_cast<const CrChar*>(prefixUtf16)) : nullptr;
    const CrError err = SCRSDK::SetSaveInfo(g_deviceHandle, path, prefix, static_cast<CrInt32>(saveNumber));
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

#if !SONY_CR_BRIDGE_STUB
// 官方 ContentsTransfer 示例在 ReleaseContentsHandleList 之后再 PullContentsFile；句柄值为 CrInt32u，可安全复制。
// Remote 模式下连拉两次易 ErrControlFailed(-8)：两次 Pull 之间须间隔，且单次失败需短延迟重试。
static CrError PullContentsFileWithRetry(SCRSDK::CrContentHandle contentHandle, CrChar* destPath)
{
    CrError lastErr = SCRSDK::CrError_None;
    for (int attempt = 0; attempt < 6; ++attempt)
    {
        if (attempt > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(220));
        std::lock_guard lock(g_mutex);
        if (g_deviceHandle == 0 || !g_callback.connected.load())
            return SCRSDK::CrError_Connect_Disconnected;
        lastErr = SCRSDK::PullContentsFile(
            g_deviceHandle,
            contentHandle,
            SCRSDK::CrPropertyStillImageTransSize_Original,
            destPath,
            nullptr);
        if (!CR_FAILED(lastErr))
            return lastErr;
    }
    return lastErr;
}

static void AbandonRemoteTransferPromise(std::promise<void>* p)
{
    std::lock_guard<std::mutex> lk(g_remoteTransferWaitMutex);
    if (g_remoteTransferPromise == p)
        g_remoteTransferPromise = nullptr;
}

static CrError WaitRemoteTransferFuture(std::future<void>& fut, std::promise<void>* p)
{
    const std::future_status st = fut.wait_for(std::chrono::minutes(3));
    if (st != std::future_status::ready)
    {
        AbandonRemoteTransferPromise(p);
        return SCRSDK::CrError_Generic_Unknown;
    }
    try
    {
        fut.get();
    }
    catch (...)
    {
        return SCRSDK::CrError_Generic_Unknown;
    }
    return SCRSDK::CrError_None;
}

static std::string SdkFileBasenameLowerUtf8(const CrInt8* p)
{
    if (!p)
        return {};
    const char* s = reinterpret_cast<const char*>(p);
    const std::string full(s);
    const size_t slash = full.find_last_of("/\\");
    const std::string base = slash == std::string::npos ? full : full.substr(slash + 1);
    std::string out = base;
    for (char& c : out)
    {
        const auto uc = static_cast<unsigned char>(c);
        if (uc <= 127 && c >= 'A' && c <= 'Z')
            c = static_cast<char>(c + 32);
    }
    return out;
}

#if defined(_WIN32)
static bool TargetUtf16ToLowerUtf8(const unsigned short* wz, std::string& out)
{
    if (!wz || !wz[0])
        return false;
    const int n = WideCharToMultiByte(CP_UTF8, 0, reinterpret_cast<LPCWCH>(wz), -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1)
        return false;
    out.resize(static_cast<size_t>(n - 1));
    WideCharToMultiByte(CP_UTF8, 0, reinterpret_cast<LPCWCH>(wz), -1, out.data(), n, nullptr, nullptr);
    for (char& c : out)
    {
        const auto uc = static_cast<unsigned char>(c);
        if (uc <= 127 && c >= 'A' && c <= 'Z')
            c = static_cast<char>(c + 32);
    }
    return true;
}
#else
static bool TargetUtf16ToLowerUtf8(const unsigned short* /*wz*/, std::string& /*out*/) { return false; }
#endif

static bool FindRemoteTransferContentByBasename(
    const unsigned short* fileNameUtf16, CrSlotNumber& outSlot, CrInt32u& outContentId)
{
    std::string target;
    if (!TargetUtf16ToLowerUtf8(fileNameUtf16, target))
        return false;

    const CrSlotNumber slots[] = {CrSlotNumber_Slot1, CrSlotNumber_Slot2};
    for (CrSlotNumber slot : slots)
    {
        CrCaptureDate* dateList = nullptr;
        CrInt32u dateNums = 0;
        CrError err = SCRSDK::CrError_None;
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle == 0 || !g_callback.connected.load())
                return false;
            err = GetRemoteTransferCapturedDateList(g_deviceHandle, slot, &dateList, &dateNums);
        }
        if (CR_FAILED(err) || dateList == nullptr || dateNums == 0)
        {
            if (dateList != nullptr)
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
            }
            continue;
        }

        bool found = false;
        for (int di = static_cast<int>(dateNums) - 1; di >= 0; --di)
        {
            CrContentsInfo* infoList = nullptr;
            CrInt32u infoNums = 0;
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle == 0 || !g_callback.connected.load())
                {
                    if (g_deviceHandle != 0)
                        ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
                    return false;
                }
                err = GetRemoteTransferContentsInfoList(
                    g_deviceHandle,
                    slot,
                    CrGetContentsInfoListType_Range_Day,
                    &dateList[static_cast<CrInt32u>(di)],
                    0,
                    &infoList,
                    &infoNums);
            }
            if (CR_FAILED(err) || infoList == nullptr || infoNums == 0)
            {
                if (infoList != nullptr)
                {
                    std::lock_guard lock(g_mutex);
                    if (g_deviceHandle != 0)
                        ReleaseRemoteTransferContentsInfoList(g_deviceHandle, infoList);
                }
                continue;
            }

            for (CrInt32u ii = 0; ii < infoNums && !found; ++ii)
            {
                const CrContentsInfo& ci = infoList[ii];
                if (ci.files == nullptr || ci.filesNum == 0)
                    continue;
                for (CrInt32u fi = 0; fi < ci.filesNum; ++fi)
                {
                    const CrContentsFile& f = ci.files[fi];
                    const std::string base = SdkFileBasenameLowerUtf8(f.filePath);
                    if (!base.empty() && base == target)
                    {
                        outSlot = slot;
                        outContentId = ci.contentId;
                        found = true;
                        break;
                    }
                }
            }

            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferContentsInfoList(g_deviceHandle, infoList);
            }

            if (found)
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
                return true;
            }
        }

        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle != 0 && dateList != nullptr)
                ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
        }
    }

    return false;
}

static SonyCrStatus DeleteRemoteContentMatchingFileNameUtf16Body(const unsigned short* fileNameUtf16)
{
    CrSlotNumber slot = CrSlotNumber_Slot1;
    CrInt32u contentId = 0;
    if (!FindRemoteTransferContentByBasename(fileNameUtf16, slot, contentId))
        return SONY_CR_ERR_NOT_FOUND;

    std::promise<void> prom;
    std::future<void> fut = prom.get_future();
    {
        std::lock_guard<std::mutex> wlk(g_remoteTransferWaitMutex);
        g_remoteTransferPromise = &prom;
    }

    CrError delErr = SCRSDK::CrError_None;
    {
        std::lock_guard lock(g_mutex);
        if (g_deviceHandle == 0 || !g_callback.connected.load())
        {
            AbandonRemoteTransferPromise(&prom);
            return SONY_CR_ERR_NOT_CONNECTED;
        }
        delErr = DeleteRemoteTransferContentsFile(g_deviceHandle, slot, contentId);
    }

    if (CR_FAILED(delErr))
    {
        AbandonRemoteTransferPromise(&prom);
        return SONY_CR_ERR_CONTROL_FAILED;
    }

    if (WaitRemoteTransferFuture(fut, &prom) != SCRSDK::CrError_None)
        return SONY_CR_ERR_CONTROL_FAILED;

    return SONY_CR_OK;
}

static bool CaptureDateGreaterUtc(const CrCaptureDate& a, const CrCaptureDate& b)
{
    if (a.year != b.year)
        return a.year > b.year;
    if (a.month != b.month)
        return a.month > b.month;
    if (a.day != b.day)
        return a.day > b.day;
    if (a.hour != b.hour)
        return a.hour > b.hour;
    if (a.minute != b.minute)
        return a.minute > b.minute;
    if (a.sec != b.sec)
        return a.sec > b.sec;
    return a.msec > b.msec;
}

/**
 * 官方 RemoteTransferMode / contents_transfer_with_remote_control：
 * RAW+JPEG 为同一 CrContentsInfo 下 filesNum>1，需对每个 fileId 调用 GetRemoteTransferContentsDataFile。
 * 与 GetDateFolderList+PullContentsFile（MTP）语义不同；双格式时优先此路径。
 */
static bool TryPullLatestStillsViaRemoteTransfer(CrChar* destFolderPath, int pullCount)
{
    if (pullCount < 2 || !destFolderPath)
        return false;

    CrSlotNumber chosenSlot = CrSlotNumber_Slot1;
    CrInt32u contentId = 0;
    CrInt32u fileIds[16];
    CrInt32u fileCount = 0;

    const CrSlotNumber slots[] = {CrSlotNumber_Slot1, CrSlotNumber_Slot2};
    bool planned = false;

    for (CrSlotNumber slot : slots)
    {
        CrCaptureDate* dateList = nullptr;
        CrInt32u dateNums = 0;
        CrError err = SCRSDK::CrError_None;
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle == 0 || !g_callback.connected.load())
                return false;
            err = GetRemoteTransferCapturedDateList(g_deviceHandle, slot, &dateList, &dateNums);
        }
        if (CR_FAILED(err) || dateList == nullptr || dateNums == 0)
        {
            if (dateList != nullptr)
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
            }
            continue;
        }

        bool slotPlanned = false;
        for (int di = static_cast<int>(dateNums) - 1; di >= 0; --di)
        {
            CrContentsInfo* infoList = nullptr;
            CrInt32u infoNums = 0;
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle == 0 || !g_callback.connected.load())
                {
                    ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
                    return false;
                }
                // 与 SimpleCli 一致：maxNums=0 表示由 SDK 决定条数上限
                err = GetRemoteTransferContentsInfoList(
                    g_deviceHandle,
                    slot,
                    CrGetContentsInfoListType_Range_Day,
                    &dateList[static_cast<CrInt32u>(di)],
                    0,
                    &infoList,
                    &infoNums);
            }
            if (CR_FAILED(err) || infoList == nullptr || infoNums == 0)
            {
                if (infoList != nullptr)
                {
                    std::lock_guard lock(g_mutex);
                    if (g_deviceHandle != 0)
                        ReleaseRemoteTransferContentsInfoList(g_deviceHandle, infoList);
                }
                continue;
            }

            CrInt32u bestIdx = 0;
            for (CrInt32u i = 1; i < infoNums; ++i)
            {
                if (CaptureDateGreaterUtc(
                        infoList[i].creationDatetimeUTC, infoList[bestIdx].creationDatetimeUTC))
                    bestIdx = i;
            }

            const CrContentsInfo& ci = infoList[bestIdx];
            if (ci.files == nullptr || ci.filesNum == 0)
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferContentsInfoList(g_deviceHandle, infoList);
                continue;
            }

            const CrInt32u want = static_cast<CrInt32u>(
                std::min<int>(pullCount, static_cast<int>(std::min(ci.filesNum, CrInt32u{16}))));

            contentId = ci.contentId;
            chosenSlot = slot;
            for (CrInt32u fi = 0; fi < want; ++fi)
                fileIds[fi] = static_cast<CrInt32u>(ci.files[fi].fileId);
            fileCount = want;

            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferContentsInfoList(g_deviceHandle, infoList);
            }
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
            }
            slotPlanned = true;
            break;
        }

        if (!slotPlanned)
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle != 0 && dateList != nullptr)
                ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
            continue;
        }

        planned = true;
        break;
    }

    if (!planned || fileCount == 0)
        return false;

#if defined(_WIN32)
    constexpr CrInt32u kDivision = 0x5000000;
#else
    constexpr CrInt32u kDivision = 0x1000000;
#endif

    for (CrInt32u fi = 0; fi < fileCount; ++fi)
    {
        if (fi > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(260));

        std::promise<void> prom;
        std::future<void> fut = prom.get_future();
        {
            std::lock_guard<std::mutex> wlk(g_remoteTransferWaitMutex);
            g_remoteTransferPromise = &prom;
        }

        CrError transferErr = SCRSDK::CrError_None;
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle == 0 || !g_callback.connected.load())
            {
                AbandonRemoteTransferPromise(&prom);
                return false;
            }
            transferErr = GetRemoteTransferContentsDataFile(
                g_deviceHandle,
                chosenSlot,
                contentId,
                fileIds[fi],
                kDivision,
                destFolderPath,
                nullptr);
        }

        if (CR_FAILED(transferErr))
        {
            AbandonRemoteTransferPromise(&prom);
            return false;
        }

        if (WaitRemoteTransferFuture(fut, &prom) != SCRSDK::CrError_None)
            return false;
    }

    return true;
}
#endif

SONY_CR_API SonyCrStatus SonyCr_DeleteRemoteContentMatchingFileNameUtf16(const unsigned short* fileNameUtf16)
{
#if SONY_CR_BRIDGE_STUB
    (void)fileNameUtf16;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!fileNameUtf16 || fileNameUtf16[0] == 0)
        return SONY_CR_ERR_INVALID_PARAM;
    return DeleteRemoteContentMatchingFileNameUtf16Body(fileNameUtf16);
#endif
}

SONY_CR_API SonyCrStatus SonyCr_PullLatestStillsToFolderUtf16(const unsigned short* destFolderUtf16, int pullCount)
{
#if SONY_CR_BRIDGE_STUB
    (void)destFolderUtf16;
    (void)pullCount;
    return SONY_CR_OK;
#else
    if (!destFolderUtf16 || pullCount < 1)
        return SONY_CR_ERR_INVALID_PARAM;
    if (pullCount > 16)
        pullCount = 16;

    std::this_thread::sleep_for(std::chrono::milliseconds(1300));

    const CrInt32u nPull = static_cast<CrInt32u>(pullCount);
    CrChar* const destPath = const_cast<CrChar*>(reinterpret_cast<const CrChar*>(destFolderUtf16));

    // RAW+JPEG：官方 Remote Transfer 列表中单条 CrContentsInfo 含多个 fileId；MTP 的 PullContentsFile 易只拉到一条。
    if (pullCount >= 2 && TryPullLatestStillsViaRemoteTransfer(destPath, pullCount))
        return SONY_CR_OK;

    std::vector<SCRSDK::CrContentHandle> pullHandles;
    pullHandles.reserve(static_cast<size_t>(nPull));

    constexpr int kMaxWaitAttempts = 32;
    for (int attempt = 0; attempt < kMaxWaitAttempts; ++attempt)
    {
        if (attempt > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(280));

        pullHandles.clear();

        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle == 0 || !g_callback.connected.load())
                return SONY_CR_ERR_NOT_CONNECTED;

            SCRSDK::CrMtpFolderInfo* f_list = nullptr;
            CrInt32u f_nums = 0;
            CrError err = SCRSDK::GetDateFolderList(g_deviceHandle, &f_list, &f_nums);
            if (CR_FAILED(err) || f_nums == 0 || f_list == nullptr)
            {
                if (f_list != nullptr)
                    SCRSDK::ReleaseDateFolderList(g_deviceHandle, f_list);
                continue;
            }

            const SCRSDK::CrMtpFolderInfo& folder = f_list[f_nums - 1];

            SCRSDK::CrContentHandle* c_list = nullptr;
            CrInt32u c_nums = 0;
            err = SCRSDK::GetContentsHandleList(g_deviceHandle, folder.handle, &c_list, &c_nums);
            if (CR_FAILED(err) || c_nums == 0 || c_list == nullptr)
            {
                SCRSDK::ReleaseDateFolderList(g_deviceHandle, f_list);
                if (c_list != nullptr)
                    SCRSDK::ReleaseContentsHandleList(g_deviceHandle, c_list);
                continue;
            }

            if (c_nums < nPull)
            {
                SCRSDK::ReleaseContentsHandleList(g_deviceHandle, c_list);
                SCRSDK::ReleaseDateFolderList(g_deviceHandle, f_list);
                continue;
            }

            const CrInt32u start = c_nums - nPull;
            for (CrInt32u i = start; i < c_nums; ++i)
                pullHandles.push_back(c_list[i]);

            SCRSDK::ReleaseContentsHandleList(g_deviceHandle, c_list);
            SCRSDK::ReleaseDateFolderList(g_deviceHandle, f_list);
        }

        if (pullHandles.size() != static_cast<size_t>(nPull))
            continue;

        CrInt32u okCount = 0;
        for (size_t idx = 0; idx < pullHandles.size(); ++idx)
        {
            if (idx > 0)
                std::this_thread::sleep_for(std::chrono::milliseconds(220));
            const CrError e = PullContentsFileWithRetry(pullHandles[idx], destPath);
            if (!CR_FAILED(e))
                ++okCount;
        }

        if (okCount > 0)
            return SONY_CR_OK;
    }

    return SONY_CR_ERR_CONTROL_FAILED;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_PullLatestStillToFolderUtf16(const unsigned short* destFolderUtf16)
{
    return SonyCr_PullLatestStillsToFolderUtf16(destFolderUtf16, 1);
}

SONY_CR_API SonyCrStatus SonyCr_GetLiveViewFocusFramesJsonUtf8(char* buffer, int bufferSizeBytes, int* outWritten)
{
#if SONY_CR_BRIDGE_STUB
    static const char kEmpty[] = "[]";
    const int need = static_cast<int>(sizeof(kEmpty));
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, kEmpty, static_cast<size_t>(need));
    return SONY_CR_OK;
#else
    std::lock_guard<std::mutex> fj(g_focusFramesJsonMutex);
    const std::string& json = g_lastFocusFramesJson.empty() ? std::string("[]") : g_lastFocusFramesJson;
    const int need = static_cast<int>(json.size() + 1);
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, json.c_str(), static_cast<size_t>(need));
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetShootingStateJsonUtf8(char* buffer, int bufferSizeBytes, int* outWritten)
{
    static const char kStub[] =
        "{\"video\":false,\"ep\":{\"v\":2,\"w\":1,\"set\":8194,\"g\":1,\"c\":[1,2,3,4]},"
        "\"fn\":{\"v\":280,\"w\":1,\"set\":8194,\"g\":1,\"c\":[250,280,320]},"
        "\"ss\":{\"v\":65661,\"w\":1,\"set\":8195,\"g\":1,\"c\":[65661,327680]},"
        "\"iso\":{\"v\":16777215,\"w\":1,\"set\":8195,\"g\":1,\"c\":[16777215,100,400]},"
        "\"ev\":{\"v\":0,\"w\":1,\"set\":8194,\"g\":1,\"c\":[0,300,600,1000,3000]},"
        "\"fm\":{\"v\":2,\"w\":1,\"set\":8194,\"g\":1,\"c\":[1,2,3,4,5,6,7]},"
        "\"rtouch\":{\"v\":1,\"w\":1,\"set\":8193,\"g\":1,\"c\":[0,1]}}";
#if SONY_CR_BRIDGE_STUB
    const int need = static_cast<int>(sizeof(kStub));
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, kStub, static_cast<size_t>(need));
    return SONY_CR_OK;
#else
    std::lock_guard lock(g_mutex);
    if (g_deviceHandle == 0 || !g_callback.connected.load())
        return SONY_CR_ERR_NOT_CONNECTED;
    std::string json;
    BuildShootingStateJson(json);
    const int need = static_cast<int>(json.size() + 1);
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, json.c_str(), static_cast<size_t>(need));
    return SONY_CR_OK;
#endif
}

} // extern "C"
