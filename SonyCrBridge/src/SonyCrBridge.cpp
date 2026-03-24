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
#include <limits>
#include <stdexcept>
#include <string>
#include <filesystem>
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
static void AddDownloadBytes(unsigned long long v);
static unsigned long long TryGetLocalFileSizeBytes(const CrChar* filename);
static void TrySetRemoteTouchSpotAf(CrDeviceHandle h);

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

    void OnCompleteDownload(CrChar* filename, CrInt32u /*type*/) override
    {
        AddDownloadBytes(TryGetLocalFileSizeBytes(filename));
    }

    void OnNotifyContentsTransfer(CrInt32u /*notify*/, CrContentHandle /*handle*/, CrChar* /*filename*/) override {}

    void OnNotifyRemoteTransferResult(CrInt32u notify, CrInt32u /*per*/, CrChar* filename) override
    {
        if (notify == CrNotify_RemoteTransfer_Result_OK)
            AddDownloadBytes(TryGetLocalFileSizeBytes(filename));
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
std::mutex g_sdUsageDebugMutex;
std::string g_lastSdUsageDebug = "sd-usage: not-run";
std::mutex g_capturePullDebugMutex;
std::string g_lastCapturePullDebug = "capture-pull: not-run";
std::atomic_ullong g_transportUploadBytes{0};
std::atomic_ullong g_transportDownloadBytes{0};

static void ResetTransportStats()
{
    g_transportUploadBytes.store(0);
    g_transportDownloadBytes.store(0);
}

static void AddUploadBytes(unsigned long long v)
{
    if (v > 0)
        g_transportUploadBytes.fetch_add(v);
}

static void AddDownloadBytes(unsigned long long v)
{
    if (v > 0)
        g_transportDownloadBytes.fetch_add(v);
}

static unsigned long long TryGetLocalFileSizeBytes(const CrChar* filename)
{
    if (!filename || filename[0] == 0)
        return 0;
    std::error_code ec;
#if defined(_WIN32)
    const std::filesystem::path p(reinterpret_cast<const wchar_t*>(filename));
#else
    const std::filesystem::path p(reinterpret_cast<const char*>(filename));
#endif
    const auto sz = std::filesystem::file_size(p, ec);
    if (ec)
        return 0;
    return static_cast<unsigned long long>(sz);
}

static std::string TryGetLatestFileSummaryInFolder(const CrChar* folderPath)
{
    if (!folderPath || folderPath[0] == 0)
        return "latest=none";
    std::error_code ec;
#if defined(_WIN32)
    const std::filesystem::path dir(reinterpret_cast<const wchar_t*>(folderPath));
#else
    const std::filesystem::path dir(reinterpret_cast<const char*>(folderPath));
#endif
    if (!std::filesystem::exists(dir, ec) || ec)
        return "latest=none";

    std::filesystem::path bestPath;
    std::filesystem::file_time_type bestTime{};
    bool found = false;
    for (const auto& it : std::filesystem::directory_iterator(dir, ec))
    {
        if (ec)
            break;
        if (!it.is_regular_file(ec) || ec)
            continue;
        const auto t = it.last_write_time(ec);
        if (ec)
            continue;
        if (!found || t > bestTime)
        {
            bestTime = t;
            bestPath = it.path();
            found = true;
        }
    }

    if (!found)
        return "latest=none";

    const auto size = std::filesystem::file_size(bestPath, ec);
    const auto sz = ec ? 0ull : static_cast<unsigned long long>(size);
    std::string out = "latest=" + bestPath.filename().string();
    out += " size=" + std::to_string(sz);
    return out;
}

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
    const CrInt32u rawSize = info->GetModelSize();
    if (!w || rawSize == 0)
        return {};
    const int rawCount = static_cast<int>(rawSize / sizeof(wchar_t));
    if (rawCount <= 0)
        return {};
    int cch = 0;
    while (cch < rawCount && w[cch] != L'\0')
        ++cch;
    if (cch <= 0)
        return {};
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

static std::string CrUtf16ToUtf8(const CrInt16u* p)
{
    if (!p)
        return {};
#if defined(_WIN32)
    // CrDataType_STR 在 CrDeviceProperty::GetCurrentStr 中是“长度前缀 + UTF-16 字符串”。
    const int length = static_cast<int>(p[0]);
    if (length <= 1)
        return {};
    const wchar_t* w = reinterpret_cast<const wchar_t*>(p + 1);
    int cch = length - 1;
    if (cch > 0 && w[cch - 1] == L'\0')
        --cch;
    if (cch <= 0)
        return {};
    const int need = WideCharToMultiByte(CP_UTF8, 0, w, cch, nullptr, 0, nullptr, nullptr);
    if (need <= 0)
        return {};
    std::string out(static_cast<size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w, cch, out.data(), need, nullptr, nullptr);
    return out;
#else
    // 非 Windows 下尽量保留 ASCII，可读性优先（当前项目主要在 Windows）。
    std::string out;
    for (int i = 0; i < 512 && p[i] != 0; ++i)
    {
        const CrInt16u ch = p[i];
        out.push_back(ch <= 0x7F ? static_cast<char>(ch) : '?');
    }
    return out;
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
    ResetTransportStats();
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

static bool ReconnectByIndexWithModeUnlocked(int index, CrSdkControlMode mode, bool enableLiveViewAfterConnect)
{
    if (!g_inited || !g_enumList)
        return false;
    const auto* infoConst = GetInfoOrNull(index);
    if (!infoConst)
        return false;

    DisconnectDeviceUnlocked();
    auto* info = const_cast<ICrCameraObjectInfo*>(infoConst);
    const CrError err = Connect(info, &g_callback, &g_deviceHandle, mode);
    if (CR_FAILED(err))
        return false;
    if (!WaitForConnected(15000))
    {
        DisconnectDeviceUnlocked();
        return false;
    }

    if (enableLiveViewAfterConnect)
    {
        const CrError lvErr = SetDeviceSetting(g_deviceHandle, Setting_Key_EnableLiveView, 1);
        (void)lvErr;
        TrySetRemoteTouchSpotAf(g_deviceHandle);
    }

    ResetTransportStats();
    return true;
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

static void AppendJsonEscapedString(std::string& out, const std::string& s)
{
    out.push_back('"');
    for (const char ch : s)
    {
        switch (ch)
        {
        case '\\': out += "\\\\"; break;
        case '\"': out += "\\\""; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            out.push_back(ch);
            break;
        }
    }
    out.push_back('"');
}

static void AppendStringField(std::string& out, const char* key, const std::string& value)
{
    out.push_back('"');
    out += key;
    out += "\":";
    if (value.empty())
        out += "null";
    else
        AppendJsonEscapedString(out, value);
}

static void AppendIntField(std::string& out, const char* key, int value, bool hasValue)
{
    out.push_back('"');
    out += key;
    out += "\":";
    if (!hasValue)
    {
        out += "null";
        return;
    }
    out += std::to_string(value);
}

static void BuildShootingStateJson(std::string& out)
{
    // 批量 13 项；其余单独查，避免部分固件对大批量查询失败。
    CrInt32u codes[13] = {
        CrDevicePropertyCode::CrDeviceProperty_ExposureProgramMode,
        CrDevicePropertyCode::CrDeviceProperty_FNumber,
        CrDevicePropertyCode::CrDeviceProperty_ShutterSpeed,
        CrDevicePropertyCode::CrDeviceProperty_IsoSensitivity,
        CrDevicePropertyCode::CrDeviceProperty_ExposureBiasCompensation,
        CrDevicePropertyCode::CrDeviceProperty_FocusMode,
        CrDevicePropertyCode::CrDeviceProperty_RemoteTouchOperationEnableStatus,
        CrDevicePropertyCode::CrDeviceProperty_DispModeStill,
        CrDevicePropertyCode::CrDeviceProperty_StillImageQuality,
        CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_ImageQuality,
        CrDevicePropertyCode::CrDeviceProperty_ImageSize,
        CrDevicePropertyCode::CrDeviceProperty_AspectRatio,
        CrDevicePropertyCode::CrDeviceProperty_RAW_FileCompressionType,
    };

    CrDeviceProperty* propList = nullptr;
    CrInt32 nprop = 0;
    const CrError err = GetSelectDeviceProperties(g_deviceHandle, 13, codes, &propList, &nprop);
    if (CR_FAILED(err) || propList == nullptr || nprop < 1)
    {
        if (propList != nullptr)
            ReleaseDeviceProperties(g_deviceHandle, propList);
        out = "{\"video\":false,\"ep\":null,\"fn\":null,\"ss\":null,\"iso\":null,\"ev\":null,\"fm\":null,\"rtouch\":null,\"dm\":null,\"iq\":null,\"isz\":null,\"ar\":null,\"rawc\":null,\"st\":null,\"drv\":null,\"flm\":null,\"flc\":null}";
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
    const CrDeviceProperty* iqStill = nullptr;
    const CrDeviceProperty* iqSlot1 = nullptr;
    const CrDeviceProperty* isz = nullptr;
    const CrDeviceProperty* ar = nullptr;
    const CrDeviceProperty* rawc = nullptr;
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
        else if (c == CrDevicePropertyCode::CrDeviceProperty_StillImageQuality)
            iqStill = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_ImageQuality)
            iqSlot1 = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_ImageSize)
            isz = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_AspectRatio)
            ar = &propList[i];
        else if (c == CrDevicePropertyCode::CrDeviceProperty_RAW_FileCompressionType)
            rawc = &propList[i];
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
    const CrDeviceProperty* flm = nullptr;
    CrDeviceProperty* flmOnly = nullptr;
    CrInt32 nFlmOnly = 0;
    {
        CrInt32u codeFlm = CrDevicePropertyCode::CrDeviceProperty_FlashMode;
        const CrError errFlm = GetSelectDeviceProperties(g_deviceHandle, 1, &codeFlm, &flmOnly, &nFlmOnly);
        if (!CR_FAILED(errFlm) && flmOnly != nullptr && nFlmOnly >= 1)
            flm = &flmOnly[0];
    }
    const CrDeviceProperty* flc = nullptr;
    CrDeviceProperty* flcOnly = nullptr;
    CrInt32 nFlcOnly = 0;
    {
        CrInt32u codeFlc = CrDevicePropertyCode::CrDeviceProperty_FlashCompensation;
        const CrError errFlc = GetSelectDeviceProperties(g_deviceHandle, 1, &codeFlc, &flcOnly, &nFlcOnly);
        if (!CR_FAILED(errFlc) && flcOnly != nullptr && nFlcOnly >= 1)
            flc = &flcOnly[0];
    }

    const CrDeviceProperty* lensModel = nullptr;
    CrDeviceProperty* lensModelOnly = nullptr;
    CrInt32 nLensModelOnly = 0;
    {
        CrInt32u codeLensModel = CrDevicePropertyCode::CrDeviceProperty_LensModelName;
        const CrError errLensModel =
            GetSelectDeviceProperties(g_deviceHandle, 1, &codeLensModel, &lensModelOnly, &nLensModelOnly);
        if (!CR_FAILED(errLensModel) && lensModelOnly != nullptr && nLensModelOnly >= 1)
            lensModel = &lensModelOnly[0];
    }

    CrDeviceProperty* batteryRemainOnly = nullptr;
    CrInt32 nBatteryRemainOnly = 0;
    CrDeviceProperty* totalBatteryRemainOnly = nullptr;
    CrInt32 nTotalBatteryRemainOnly = 0;
    CrDeviceProperty* batteryLevelOnly = nullptr;
    CrInt32 nBatteryLevelOnly = 0;
    CrDeviceProperty* totalBatteryLevelOnly = nullptr;
    CrInt32 nTotalBatteryLevelOnly = 0;
    CrDeviceProperty* batteryUnitOnly = nullptr;
    CrInt32 nBatteryUnitOnly = 0;

    const CrDeviceProperty* batteryRemain = nullptr;
    const CrDeviceProperty* totalBatteryRemain = nullptr;
    const CrDeviceProperty* batteryLevel = nullptr;
    const CrDeviceProperty* totalBatteryLevel = nullptr;
    const CrDeviceProperty* batteryUnit = nullptr;

    {
        CrInt32u code = CrDevicePropertyCode::CrDeviceProperty_BatteryRemain;
        const CrError e = GetSelectDeviceProperties(g_deviceHandle, 1, &code, &batteryRemainOnly, &nBatteryRemainOnly);
        if (!CR_FAILED(e) && batteryRemainOnly != nullptr && nBatteryRemainOnly >= 1)
            batteryRemain = &batteryRemainOnly[0];
    }
    {
        CrInt32u code = CrDevicePropertyCode::CrDeviceProperty_TotalBatteryRemain;
        const CrError e = GetSelectDeviceProperties(g_deviceHandle, 1, &code, &totalBatteryRemainOnly, &nTotalBatteryRemainOnly);
        if (!CR_FAILED(e) && totalBatteryRemainOnly != nullptr && nTotalBatteryRemainOnly >= 1)
            totalBatteryRemain = &totalBatteryRemainOnly[0];
    }
    {
        CrInt32u code = CrDevicePropertyCode::CrDeviceProperty_BatteryLevel;
        const CrError e = GetSelectDeviceProperties(g_deviceHandle, 1, &code, &batteryLevelOnly, &nBatteryLevelOnly);
        if (!CR_FAILED(e) && batteryLevelOnly != nullptr && nBatteryLevelOnly >= 1)
            batteryLevel = &batteryLevelOnly[0];
    }
    {
        CrInt32u code = CrDevicePropertyCode::CrDeviceProperty_TotalBatteryLevel;
        const CrError e = GetSelectDeviceProperties(g_deviceHandle, 1, &code, &totalBatteryLevelOnly, &nTotalBatteryLevelOnly);
        if (!CR_FAILED(e) && totalBatteryLevelOnly != nullptr && nTotalBatteryLevelOnly >= 1)
            totalBatteryLevel = &totalBatteryLevelOnly[0];
    }
    {
        CrInt32u code = CrDevicePropertyCode::CrDeviceProperty_BatteryRemainDisplayUnit;
        const CrError e = GetSelectDeviceProperties(g_deviceHandle, 1, &code, &batteryUnitOnly, &nBatteryUnitOnly);
        if (!CR_FAILED(e) && batteryUnitOnly != nullptr && nBatteryUnitOnly >= 1)
            batteryUnit = &batteryUnitOnly[0];
    }

    CrDeviceProperty* allProps = nullptr;
    CrInt32 nAllProps = 0;
    if (lensModel == nullptr || batteryRemain == nullptr || totalBatteryRemain == nullptr
        || batteryLevel == nullptr || totalBatteryLevel == nullptr || batteryUnit == nullptr)
    {
        const CrError eAll = GetDeviceProperties(g_deviceHandle, &allProps, &nAllProps);
        if (!CR_FAILED(eAll) && allProps != nullptr && nAllProps > 0)
        {
            for (CrInt32 i = 0; i < nAllProps; ++i)
            {
                const CrInt32u code = allProps[i].GetCode();
                if (lensModel == nullptr && code == CrDevicePropertyCode::CrDeviceProperty_LensModelName)
                    lensModel = &allProps[i];
                else if (batteryRemain == nullptr && code == CrDevicePropertyCode::CrDeviceProperty_BatteryRemain)
                    batteryRemain = &allProps[i];
                else if (totalBatteryRemain == nullptr && code == CrDevicePropertyCode::CrDeviceProperty_TotalBatteryRemain)
                    totalBatteryRemain = &allProps[i];
                else if (batteryLevel == nullptr && code == CrDevicePropertyCode::CrDeviceProperty_BatteryLevel)
                    batteryLevel = &allProps[i];
                else if (totalBatteryLevel == nullptr && code == CrDevicePropertyCode::CrDeviceProperty_TotalBatteryLevel)
                    totalBatteryLevel = &allProps[i];
                else if (batteryUnit == nullptr && code == CrDevicePropertyCode::CrDeviceProperty_BatteryRemainDisplayUnit)
                    batteryUnit = &allProps[i];
            }
        }
    }

    std::string lensModelName;
    if (lensModel != nullptr && lensModel->GetCurrentStr() != nullptr)
        lensModelName = CrUtf16ToUtf8(lensModel->GetCurrentStr());

    bool hasBatteryPercent = false;
    int batteryPercent = 0;
    {
        const CrInt64u remainRaw = (totalBatteryRemain != nullptr)
            ? totalBatteryRemain->GetCurrentValue()
            : (batteryRemain != nullptr ? batteryRemain->GetCurrentValue() : static_cast<CrInt64u>(CrBatteryRemain_Untaken));
        const CrInt64u unitRaw = batteryUnit != nullptr ? batteryUnit->GetCurrentValue() : 0;
        const int remainVal = static_cast<int>(remainRaw & 0xFFFFu);
        if (remainVal >= 0 && remainVal <= 100
            && unitRaw == static_cast<CrInt64u>(CrBatteryRemainDisplayUnit_percent))
        {
            hasBatteryPercent = true;
            batteryPercent = remainVal;
        }
        else
        {
            const CrInt64u levelRaw = (totalBatteryLevel != nullptr)
                ? totalBatteryLevel->GetCurrentValue()
                : (batteryLevel != nullptr ? batteryLevel->GetCurrentValue() : static_cast<CrInt64u>(CrBatteryLevel_BatteryNotInstalled));
            switch (static_cast<CrBatteryLevel>(levelRaw))
            {
            case CrBatteryLevel_PreEndBattery:
            case CrBatteryLevel_PreEnd_PowerSupply:
                hasBatteryPercent = true;
                batteryPercent = 5;
                break;
            case CrBatteryLevel_1_4:
            case CrBatteryLevel_1_4_PowerSupply:
                hasBatteryPercent = true;
                batteryPercent = 25;
                break;
            case CrBatteryLevel_2_4:
            case CrBatteryLevel_2_4_PowerSupply:
                hasBatteryPercent = true;
                batteryPercent = 50;
                break;
            case CrBatteryLevel_3_4:
            case CrBatteryLevel_3_4_PowerSupply:
                hasBatteryPercent = true;
                batteryPercent = 75;
                break;
            case CrBatteryLevel_4_4:
                hasBatteryPercent = true;
                batteryPercent = 100;
                break;
            case CrBatteryLevel_1_3:
                hasBatteryPercent = true;
                batteryPercent = 33;
                break;
            case CrBatteryLevel_2_3:
                hasBatteryPercent = true;
                batteryPercent = 66;
                break;
            case CrBatteryLevel_3_3:
                hasBatteryPercent = true;
                batteryPercent = 100;
                break;
            default:
                break;
            }
        }
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
    const CrDeviceProperty* iq = iqStill != nullptr ? iqStill : iqSlot1;
    AppendProp(out, "iq", iq, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "isz", isz, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "ar", ar, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "rawc", rawc, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "st", shtype, sizeof(std::uint8_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt8Array));
    out += ',';
    AppendProp(out, "drv", drv, sizeof(std::uint32_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt32Array));
    out += ',';
    AppendProp(out, "flm", flm, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendProp(out, "flc", flc, sizeof(std::uint16_t), static_cast<CrInt32u>(CrDataType::CrDataType_UInt16Array));
    out += ',';
    AppendStringField(out, "lensModelName", lensModelName);
    out += ',';
    AppendIntField(out, "batteryPercent", batteryPercent, hasBatteryPercent);

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
    if (flmOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, flmOnly);
    if (flcOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, flcOnly);
    if (lensModelOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, lensModelOnly);
    if (batteryRemainOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, batteryRemainOnly);
    if (totalBatteryRemainOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, totalBatteryRemainOnly);
    if (batteryLevelOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, batteryLevelOnly);
    if (totalBatteryLevelOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, totalBatteryLevelOnly);
    if (batteryUnitOnly != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, batteryUnitOnly);
    if (allProps != nullptr)
        ReleaseDeviceProperties(g_deviceHandle, allProps);
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
    // 主链路稳定性优先：默认 Remote 模式，确保拍照后回传/保存行为与既有版本一致。
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
    ResetTransportStats();

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
    AddDownloadBytes(static_cast<unsigned long long>(imageSize));
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
    AddUploadBytes(12);
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
    AddUploadBytes(12);
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
    AddUploadBytes(static_cast<unsigned long long>(length) * 2ULL + 8ULL);
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
    AddUploadBytes(8);
    return CR_FAILED(err) ? SONY_CR_ERR_CONTROL_FAILED : SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetTransportStats(unsigned long long* outUploadBytes, unsigned long long* outDownloadBytes)
{
#if SONY_CR_BRIDGE_STUB
    (void)outUploadBytes;
    (void)outDownloadBytes;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    if (!outUploadBytes || !outDownloadBytes)
        return SONY_CR_ERR_INVALID_PARAM;
    *outUploadBytes = g_transportUploadBytes.load();
    *outDownloadBytes = g_transportDownloadBytes.load();
    return SONY_CR_OK;
#endif
}

SONY_CR_API void SonyCr_ResetTransportStats(void)
{
#if SONY_CR_BRIDGE_STUB
    return;
#else
    ResetTransportStats();
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetSdCardUsageEstimate(
    unsigned long long* outSlot1TotalBytes,
    unsigned long long* outSlot1UsedBytes,
    int* outSlot1HasCard,
    unsigned long long* outSlot2TotalBytes,
    unsigned long long* outSlot2UsedBytes,
    int* outSlot2HasCard)
{
#if SONY_CR_BRIDGE_STUB
    if (outSlot1TotalBytes) *outSlot1TotalBytes = 0;
    if (outSlot1UsedBytes) *outSlot1UsedBytes = 0;
    if (outSlot1HasCard) *outSlot1HasCard = 0;
    if (outSlot2TotalBytes) *outSlot2TotalBytes = 0;
    if (outSlot2UsedBytes) *outSlot2UsedBytes = 0;
    if (outSlot2HasCard) *outSlot2HasCard = 0;
    return SONY_CR_ERR_SDK_NOT_LINKED;
#else
    using namespace SCRSDK;

    if (!outSlot1TotalBytes || !outSlot1UsedBytes || !outSlot1HasCard || !outSlot2TotalBytes || !outSlot2UsedBytes || !outSlot2HasCard)
        return SONY_CR_ERR_INVALID_PARAM;

    *outSlot1TotalBytes = 0;
    *outSlot1UsedBytes = 0;
    *outSlot1HasCard = 0;
    *outSlot2TotalBytes = 0;
    *outSlot2UsedBytes = 0;
    *outSlot2HasCard = 0;
    CrInt8u slot1ContentsListStatus = 0xFF;
    CrInt8u slot2ContentsListStatus = 0xFF;

    auto safeMulU64 = [](unsigned long long a, unsigned long long b) -> unsigned long long
    {
        if (a == 0 || b == 0)
            return 0;
        const auto max = std::numeric_limits<unsigned long long>::max();
        if (a > max / b)
            return max;
        return a * b;
    };

    // remainingX: 剩余可拍摄“张数”
    // statusX: 用于判断无卡
    CrInt32u remaining1 = 0;
    CrInt32u remaining2 = 0;
    CrInt32u maxRemaining1 = 0;
    CrInt32u maxRemaining2 = 0;
    CrSlotStatus status1 = CrSlotStatus_NoCard;
    CrSlotStatus status2 = CrSlotStatus_NoCard;

    {
        CrInt32u codes[] = {
            static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_RemainingNumber),
            static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_RemainingNumber),
            static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_Status),
            static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_Status),
        };

        CrDeviceProperty* propList = nullptr;
        CrInt32 nprop = 0;
        CrError err = SCRSDK::CrError_None;
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle == 0)
                return SONY_CR_ERR_NOT_CONNECTED;

            err = GetSelectDeviceProperties(g_deviceHandle, 4, codes, &propList, &nprop);
        }

        if (!CR_FAILED(err) && propList != nullptr && nprop >= 1)
        {
            for (CrInt32 i = 0; i < nprop; ++i)
            {
                const CrInt32u c = propList[i].GetCode();
                if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_RemainingNumber))
                {
                    remaining1 = static_cast<CrInt32u>(propList[i].GetCurrentValue());
                    auto tryExtractMax = [](const CrInt8u* p, CrInt32u sz, CrInt32u current, CrInt32u& outMax)
                    {
                        if (p == nullptr || sz < 8u)
                            return;
                        // 不同固件上 values/getSetValues 的布局可能不同，遍历整个缓冲按 UInt32 取候选最大值。
                        for (CrInt32u off = 0; off + 4u <= sz; off += 4u)
                        {
                            CrInt32u v = 0;
                            std::memcpy(&v, p + off, sizeof(CrInt32u));
                            if (v >= current && v > outMax)
                                outMax = v;
                        }
                    };
                    tryExtractMax(propList[i].GetValues(), propList[i].GetValueSize(), remaining1, maxRemaining1);
                    tryExtractMax(propList[i].GetSetValues(), propList[i].GetSetValueSize(), remaining1, maxRemaining1);
                }
                else if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_RemainingNumber))
                {
                    remaining2 = static_cast<CrInt32u>(propList[i].GetCurrentValue());
                    auto tryExtractMax = [](const CrInt8u* p, CrInt32u sz, CrInt32u current, CrInt32u& outMax)
                    {
                        if (p == nullptr || sz < 8u)
                            return;
                        for (CrInt32u off = 0; off + 4u <= sz; off += 4u)
                        {
                            CrInt32u v = 0;
                            std::memcpy(&v, p + off, sizeof(CrInt32u));
                            if (v >= current && v > outMax)
                                outMax = v;
                        }
                    };
                    tryExtractMax(propList[i].GetValues(), propList[i].GetValueSize(), remaining2, maxRemaining2);
                    tryExtractMax(propList[i].GetSetValues(), propList[i].GetSetValueSize(), remaining2, maxRemaining2);
                }
                else if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_Status))
                    status1 = static_cast<CrSlotStatus>(propList[i].GetCurrentValue());
                else if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_Status))
                    status2 = static_cast<CrSlotStatus>(propList[i].GetCurrentValue());
            }
        }

        if (propList != nullptr)
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle != 0)
                ReleaseDeviceProperties(g_deviceHandle, propList);
        }

        // 部分机型在当前状态下 Select 可能拿不到槽位属性，回退全量属性扫描一次。
        if ((remaining1 == 0 && remaining2 == 0)
            && status1 == CrSlotStatus_NoCard && status2 == CrSlotStatus_NoCard)
        {
            CrDeviceProperty* allProps = nullptr;
            CrInt32 nAll = 0;
            CrError errAll = SCRSDK::CrError_None;
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle == 0)
                    return SONY_CR_ERR_NOT_CONNECTED;
                errAll = GetDeviceProperties(g_deviceHandle, &allProps, &nAll);
            }
            if (!CR_FAILED(errAll) && allProps != nullptr && nAll > 0)
            {
                for (CrInt32 i = 0; i < nAll; ++i)
                {
                    const CrInt32u c = allProps[i].GetCode();
                    if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_RemainingNumber))
                        remaining1 = static_cast<CrInt32u>(allProps[i].GetCurrentValue());
                    else if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_RemainingNumber))
                        remaining2 = static_cast<CrInt32u>(allProps[i].GetCurrentValue());
                    else if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_Status))
                        status1 = static_cast<CrSlotStatus>(allProps[i].GetCurrentValue());
                    else if (c == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_Status))
                        status2 = static_cast<CrSlotStatus>(allProps[i].GetCurrentValue());
                }
            }
            if (allProps != nullptr)
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseDeviceProperties(g_deviceHandle, allProps);
            }
        }
    }

    // 官方文档中 ContentsInfoList 受 EnableStatus 影响；未开启时 RemoteTransfer/MTP 统计常为空。
    // 这里尝试将 SLOT1/2 的 ContentsInfoList 置为 Enable（失败时忽略，后续仍走兜底估算）。
    {
        bool retriedAfterEnable = false;
        CrInt32u codes[] = {
            static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_ContentsInfoListEnableStatus),
            static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_ContentsInfoListEnableStatus),
        };
        CrDeviceProperty* plist = nullptr;
        CrInt32 n = 0;
        CrError err = SCRSDK::CrError_None;
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle != 0)
                err = GetSelectDeviceProperties(g_deviceHandle, 2, codes, &plist, &n);
        }

        if (!CR_FAILED(err) && plist != nullptr && n > 0)
        {
            for (CrInt32 i = 0; i < n; ++i)
            {
                const auto code = plist[i].GetCode();
                if (code != static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_ContentsInfoListEnableStatus)
                    && code != static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_ContentsInfoListEnableStatus))
                    continue;

                const auto cur = static_cast<CrInt8u>(plist[i].GetCurrentValue() & 0xFFu);
                if (code == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_ContentsInfoListEnableStatus))
                    slot1ContentsListStatus = cur;
                else if (code == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_ContentsInfoListEnableStatus))
                    slot2ContentsListStatus = cur;
                if (cur == CrContentsInfoListEnableStatus_Enable || !plist[i].IsSetEnableCurrentValue())
                    continue;

                CrDeviceProperty p;
                p.SetCode(code);
                p.SetCurrentValue(static_cast<CrInt64u>(CrContentsInfoListEnableStatus_Enable));
                p.SetValueType(CrDataType::CrDataType_UInt8);
                {
                    std::lock_guard lock(g_mutex);
                    if (g_deviceHandle != 0)
                    {
                        (void)SetPriorityKeyPcRemote(g_deviceHandle);
                        const auto setErr = SetDeviceProperty(g_deviceHandle, &p);
                        retriedAfterEnable = retriedAfterEnable || !CR_FAILED(setErr);
                    }
                }
            }
        }

        if (plist != nullptr)
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle != 0)
                ReleaseDeviceProperties(g_deviceHandle, plist);
        }

        // 某些机型在开启 ContentsInfoList 后需要短暂等待，随后重新读取状态。
        if (retriedAfterEnable)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(150));
            CrDeviceProperty* plist2 = nullptr;
            CrInt32 n2 = 0;
            CrError err2 = SCRSDK::CrError_None;
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    err2 = GetSelectDeviceProperties(g_deviceHandle, 2, codes, &plist2, &n2);
            }

            if (!CR_FAILED(err2) && plist2 != nullptr && n2 > 0)
            {
                for (CrInt32 i = 0; i < n2; ++i)
                {
                    const auto code = plist2[i].GetCode();
                    const auto cur = static_cast<CrInt8u>(plist2[i].GetCurrentValue() & 0xFFu);
                    if (code == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT1_ContentsInfoListEnableStatus))
                        slot1ContentsListStatus = cur;
                    else if (code == static_cast<CrInt32u>(CrDevicePropertyCode::CrDeviceProperty_MediaSLOT2_ContentsInfoListEnableStatus))
                        slot2ContentsListStatus = cur;
                }
            }
            if (plist2 != nullptr)
            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0)
                    ReleaseDeviceProperties(g_deviceHandle, plist2);
            }
        }
    }

    // 统计 used：通过 RemoteTransfer 列表累计 contentId 数（shot）与文件真实字节数。
    struct SlotUsageStats
    {
        unsigned long long usedShots = 0;
        unsigned long long usedBytes = 0;
    };

    auto countSlotUsage = [&](CrSlotNumber slot, SlotUsageStats& stats) -> bool
    {
        stats = {};

        CrCaptureDate* dateList = nullptr;
        CrInt32u dateNums = 0;
        CrError err = SCRSDK::CrError_None;
        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle == 0)
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
            return true;
        }

        for (int di = static_cast<int>(dateNums) - 1; di >= 0; --di)
        {
            CrContentsInfo* infoList = nullptr;
            CrInt32u infoNums = 0;

            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle == 0)
                {
                    if (g_deviceHandle != 0 && dateList != nullptr)
                        ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
                    // 会话短时重连时不要让整条容量读取失败，降级为“本槽统计不到 used”。
                    return true;
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

            if (!CR_FAILED(err) && infoList != nullptr && infoNums > 0)
            {
                stats.usedShots += static_cast<unsigned long long>(infoNums);
                for (CrInt32u ii = 0; ii < infoNums; ++ii)
                {
                    const CrContentsInfo& ci = infoList[ii];
                    if (ci.files == nullptr || ci.filesNum == 0)
                        continue;
                    for (CrInt32u fi = 0; fi < ci.filesNum; ++fi)
                    {
                        stats.usedBytes += static_cast<unsigned long long>(ci.files[fi].fileSize);
                    }
                }
            }

            {
                std::lock_guard lock(g_mutex);
                if (g_deviceHandle != 0 && infoList != nullptr)
                    ReleaseRemoteTransferContentsInfoList(g_deviceHandle, infoList);
            }
        }

        {
            std::lock_guard lock(g_mutex);
            if (g_deviceHandle != 0 && dateList != nullptr)
                ReleaseRemoteTransferCapturedDateList(g_deviceHandle, dateList);
        }

        return true;
    };

    SlotUsageStats slot1Stats;
    SlotUsageStats slot2Stats;
    (void)countSlotUsage(CrSlotNumber_Slot1, slot1Stats);
    (void)countSlotUsage(CrSlotNumber_Slot2, slot2Stats);

    // RemoteTransfer 列表在部分机型/设置下可能不是“全卡内容”。
    // 追加 MTP 全量内容统计作为兜底（只统计总已用字节，不分槽）。
    unsigned long long mtpTotalUsedBytes = 0;
    unsigned long long mtpTotalContentCount = 0;
    {
        std::lock_guard lock(g_mutex);
        if (g_deviceHandle != 0)
        {
            SCRSDK::CrMtpFolderInfo* folders = nullptr;
            CrInt32u folderNums = 0;
            CrError err = SCRSDK::GetDateFolderList(g_deviceHandle, &folders, &folderNums);
            if (!CR_FAILED(err) && folders != nullptr && folderNums > 0)
            {
                for (CrInt32u fi = 0; fi < folderNums; ++fi)
                {
                    SCRSDK::CrContentHandle* handles = nullptr;
                    CrInt32u handleNums = 0;
                    err = SCRSDK::GetContentsHandleList(g_deviceHandle, folders[fi].handle, &handles, &handleNums);
                    if (CR_FAILED(err) || handles == nullptr || handleNums == 0)
                    {
                        if (handles != nullptr)
                            SCRSDK::ReleaseContentsHandleList(g_deviceHandle, handles);
                        continue;
                    }
                    mtpTotalContentCount += static_cast<unsigned long long>(handleNums);

                    for (CrInt32u hi = 0; hi < handleNums; ++hi)
                    {
                        SCRSDK::CrMtpContentsInfo info;
                        std::memset(&info, 0, sizeof(info));
                        const CrError e2 = SCRSDK::GetContentsDetailInfo(g_deviceHandle, handles[hi], &info);
                        if (!CR_FAILED(e2))
                            mtpTotalUsedBytes += static_cast<unsigned long long>(info.contentSize);
                    }

                    SCRSDK::ReleaseContentsHandleList(g_deviceHandle, handles);
                }

                SCRSDK::ReleaseDateFolderList(g_deviceHandle, folders);
            }
            else if (folders != nullptr)
            {
                SCRSDK::ReleaseDateFolderList(g_deviceHandle, folders);
            }
        }
    }

    // 每张（每个 contentId）平均字节数：用于把“remaining shots”换算成剩余字节估算。
    const unsigned long long avg1 =
        slot1Stats.usedShots > 0 ? (slot1Stats.usedBytes / slot1Stats.usedShots) : 0;
    const unsigned long long avg2 =
        slot2Stats.usedShots > 0 ? (slot2Stats.usedBytes / slot2Stats.usedShots) : 0;
    const unsigned long long fallbackAvg =
        avg1 > 0 ? avg1 : (avg2 > 0 ? avg2 : (25ull * 1024ull * 1024ull)); // 双卡都无历史时取 25MB/张兜底

    // 若列表拿不到已用（usedBytes=0），回退到 RemainingNumber 的 [max-current] 来估算已用张数。
    const unsigned long long used1ShotsFromRange =
        (maxRemaining1 > 0 && maxRemaining1 >= remaining1) ? static_cast<unsigned long long>(maxRemaining1 - remaining1) : 0ull;
    const unsigned long long used2ShotsFromRange =
        (maxRemaining2 > 0 && maxRemaining2 >= remaining2) ? static_cast<unsigned long long>(maxRemaining2 - remaining2) : 0ull;

    const unsigned long long perShot1 = avg1 > 0 ? avg1 : fallbackAvg;
    const unsigned long long perShot2 = avg2 > 0 ? avg2 : fallbackAvg;

    unsigned long long used1Bytes =
        slot1Stats.usedBytes > 0 ? slot1Stats.usedBytes : safeMulU64(used1ShotsFromRange, perShot1);
    unsigned long long used2Bytes =
        slot2Stats.usedBytes > 0 ? slot2Stats.usedBytes : safeMulU64(used2ShotsFromRange, perShot2);

    // 若分槽统计仍为 0，但 MTP 全量统计有值，则按插卡情况兜底分配。
    if (mtpTotalUsedBytes == 0 && mtpTotalContentCount > 0)
        mtpTotalUsedBytes = safeMulU64(mtpTotalContentCount, fallbackAvg);

    if (mtpTotalUsedBytes > 0 && used1Bytes == 0 && used2Bytes == 0)
    {
        const bool slot1Present = (status1 != CrSlotStatus_NoCard) || remaining1 > 0;
        const bool slot2Present = (status2 != CrSlotStatus_NoCard) || remaining2 > 0;
        if (slot1Present && !slot2Present)
            used1Bytes = mtpTotalUsedBytes;
        else if (slot2Present && !slot1Present)
            used2Bytes = mtpTotalUsedBytes;
        else if (slot1Present && slot2Present)
        {
            const unsigned long long r1 = static_cast<unsigned long long>(remaining1);
            const unsigned long long r2 = static_cast<unsigned long long>(remaining2);
            if (r1 + r2 > 0)
            {
                // 仅用于兜底，按剩余张数反比分配（剩余越少说明已用越多）。
                const unsigned long long inv1 = r2;
                const unsigned long long inv2 = r1;
                const unsigned long long invSum = inv1 + inv2;
                if (invSum > 0)
                {
                    used1Bytes = (mtpTotalUsedBytes * inv1) / invSum;
                    used2Bytes = mtpTotalUsedBytes - used1Bytes;
                }
            }
        }
    }

    unsigned long long total1Bytes = used1Bytes + safeMulU64(static_cast<unsigned long long>(remaining1), perShot1);
    unsigned long long total2Bytes = used2Bytes + safeMulU64(static_cast<unsigned long long>(remaining2), perShot2);

    if (maxRemaining1 > 0)
    {
        const auto totalByMax = safeMulU64(static_cast<unsigned long long>(maxRemaining1), perShot1);
        if (totalByMax > total1Bytes)
            total1Bytes = totalByMax;
    }
    if (maxRemaining2 > 0)
    {
        const auto totalByMax = safeMulU64(static_cast<unsigned long long>(maxRemaining2), perShot2);
        if (totalByMax > total2Bytes)
            total2Bytes = totalByMax;
    }

    const bool has1 = (status1 != CrSlotStatus_NoCard) || (slot1Stats.usedShots > 0 || remaining1 > 0);
    const bool has2 = (status2 != CrSlotStatus_NoCard) || (slot2Stats.usedShots > 0 || remaining2 > 0);

    *outSlot1HasCard = has1 ? 1 : 0;
    *outSlot1UsedBytes = has1 ? used1Bytes : 0;
    *outSlot1TotalBytes = has1 ? total1Bytes : 0;

    *outSlot2HasCard = has2 ? 1 : 0;
    *outSlot2UsedBytes = has2 ? used2Bytes : 0;
    *outSlot2TotalBytes = has2 ? total2Bytes : 0;

    {
        std::string dbg;
        dbg.reserve(512);
        dbg += "slot1_status=" + std::to_string(static_cast<unsigned>(status1));
        dbg += " slot2_status=" + std::to_string(static_cast<unsigned>(status2));
        dbg += " slot1_remaining=" + std::to_string(remaining1);
        dbg += " slot2_remaining=" + std::to_string(remaining2);
        dbg += " slot1_max_remaining=" + std::to_string(maxRemaining1);
        dbg += " slot2_max_remaining=" + std::to_string(maxRemaining2);
        dbg += " slot1_contents_enable=" + std::to_string(static_cast<unsigned>(slot1ContentsListStatus));
        dbg += " slot2_contents_enable=" + std::to_string(static_cast<unsigned>(slot2ContentsListStatus));
        dbg += " slot1_used_shots=" + std::to_string(slot1Stats.usedShots);
        dbg += " slot2_used_shots=" + std::to_string(slot2Stats.usedShots);
        dbg += " slot1_used_bytes=" + std::to_string(slot1Stats.usedBytes);
        dbg += " slot2_used_bytes=" + std::to_string(slot2Stats.usedBytes);
        dbg += " mtp_used_bytes=" + std::to_string(mtpTotalUsedBytes);
        dbg += " out1=" + std::to_string(*outSlot1UsedBytes) + "/" + std::to_string(*outSlot1TotalBytes);
        dbg += " out2=" + std::to_string(*outSlot2UsedBytes) + "/" + std::to_string(*outSlot2TotalBytes);
        std::lock_guard<std::mutex> lk(g_sdUsageDebugMutex);
        g_lastSdUsageDebug = std::move(dbg);
    }

    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetLastSdUsageDebugUtf8(char* buffer, int bufferSizeBytes, int* outWritten)
{
#if SONY_CR_BRIDGE_STUB
    static const char kStub[] = "sd-usage: stub";
    const int need = static_cast<int>(sizeof(kStub));
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, kStub, static_cast<size_t>(need));
    return SONY_CR_OK;
#else
    std::lock_guard<std::mutex> lk(g_sdUsageDebugMutex);
    const std::string& s = g_lastSdUsageDebug.empty() ? std::string("sd-usage: empty") : g_lastSdUsageDebug;
    const int need = static_cast<int>(s.size() + 1);
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, s.c_str(), static_cast<size_t>(need));
    return SONY_CR_OK;
#endif
}

SONY_CR_API SonyCrStatus SonyCr_GetLastCapturePullDebugUtf8(char* buffer, int bufferSizeBytes, int* outWritten)
{
#if SONY_CR_BRIDGE_STUB
    static const char kStub[] = "capture-pull: sdk-not-linked";
    const int need = static_cast<int>(sizeof(kStub));
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, kStub, static_cast<size_t>(need));
    return SONY_CR_OK;
#else
    std::lock_guard<std::mutex> lk(g_capturePullDebugMutex);
    const std::string& s = g_lastCapturePullDebug.empty() ? std::string("capture-pull: empty") : g_lastCapturePullDebug;
    const int need = static_cast<int>(s.size() + 1);
    if (outWritten)
        *outWritten = need;
    if (!buffer || bufferSizeBytes < need)
        return SONY_CR_ERR_BUFFER_TOO_SMALL;
    std::memcpy(buffer, s.c_str(), static_cast<size_t>(need));
    return SONY_CR_OK;
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
    if (pullCount < 1 || !destFolderPath)
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

    // 拍照后给机身极短缓冲，避免 UI 长时间阻塞。
    std::this_thread::sleep_for(std::chrono::milliseconds(320));

    const CrInt32u nPull = static_cast<CrInt32u>(pullCount);
    CrChar* const destPath = const_cast<CrChar*>(reinterpret_cast<const CrChar*>(destFolderUtf16));
    std::string dbg = "capture-pull begin";
    dbg += " pullCount=" + std::to_string(pullCount);

    // 优先 Remote Transfer：按 contentId/fileId 拉原始文件。
    // 实测部分机型在 HEIF 单文件场景下，MTP PullContentsFile 可能只给到低分辨率图；此处先走 Remote Transfer 更稳。
    if (TryPullLatestStillsViaRemoteTransfer(destPath, pullCount))
    {
        dbg += " mode=remote ok";
        dbg += " " + TryGetLatestFileSummaryInFolder(destPath);
        std::lock_guard<std::mutex> lk(g_capturePullDebugMutex);
        g_lastCapturePullDebug = std::move(dbg);
        return SONY_CR_OK;
    }
    dbg += " mode=remote fail";

    std::vector<SCRSDK::CrContentHandle> pullHandles;
    pullHandles.reserve(static_cast<size_t>(nPull));

    // 失败快速返回：上层会决定是否继续后台补偿，不在这里长时间阻塞。
    constexpr int kMaxWaitAttempts = 5;
    for (int attempt = 0; attempt < kMaxWaitAttempts; ++attempt)
    {
        if (attempt > 0)
            std::this_thread::sleep_for(std::chrono::milliseconds(120));

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
        {
            dbg += " mode=mtp ok";
            dbg += " okCount=" + std::to_string(static_cast<unsigned>(okCount));
            dbg += " " + TryGetLatestFileSummaryInFolder(destPath);
            std::lock_guard<std::mutex> lk(g_capturePullDebugMutex);
            g_lastCapturePullDebug = std::move(dbg);
            return SONY_CR_OK;
        }
    }

    dbg += " mode=mtp fail";

    // 官方 sample_RemoteTransferMode 使用 CrSdkControlMode_RemoteTransfer 进行内容拉取。
    // 某些机型在 Remote 模式下会持续 ErrControlFailed(-8)；这里失败后自动切换模式重试一次，再切回 Remote。
    bool switchedToRemoteTransfer = false;
    {
        std::lock_guard lock(g_mutex);
        switchedToRemoteTransfer = ReconnectByIndexWithModeUnlocked(0, CrSdkControlMode_RemoteTransfer, false);
    }
    if (switchedToRemoteTransfer)
    {
        dbg += " modeSwitch=toRemoteTransfer";
        std::this_thread::sleep_for(std::chrono::milliseconds(160));
        if (TryPullLatestStillsViaRemoteTransfer(destPath, pullCount))
        {
            dbg += " mode=remoteTransferSession ok";
            dbg += " " + TryGetLatestFileSummaryInFolder(destPath);
            bool backRemoteOk = false;
            {
                std::lock_guard lock(g_mutex);
                backRemoteOk = ReconnectByIndexWithModeUnlocked(0, CrSdkControlMode_Remote, true);
            }
            dbg += backRemoteOk ? " backRemote=ok" : " backRemote=fail";
            std::lock_guard<std::mutex> lk(g_capturePullDebugMutex);
            g_lastCapturePullDebug = std::move(dbg);
            return SONY_CR_OK;
        }
        dbg += " mode=remoteTransferSession fail";
        bool backRemoteOk = false;
        {
            std::lock_guard lock(g_mutex);
            backRemoteOk = ReconnectByIndexWithModeUnlocked(0, CrSdkControlMode_Remote, true);
        }
        dbg += backRemoteOk ? " backRemote=ok" : " backRemote=fail";
    }
    else
    {
        dbg += " modeSwitch=toRemoteTransfer-fail";
    }

    std::lock_guard<std::mutex> lk(g_capturePullDebugMutex);
    g_lastCapturePullDebug = std::move(dbg);
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
