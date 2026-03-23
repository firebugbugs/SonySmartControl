/**
 * Sony Camera Remote SDK (CrSDK) — C 语言桥接接口，供 C# / Avalonia 通过 P/Invoke 调用。
 * 实现见 src/SonyCrBridge.cpp；需链接官方 Cr_Core 与 app/CRSDK 头文件。
 */
#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#ifdef SONY_CR_BRIDGE_EXPORTS
#define SONY_CR_API __declspec(dllexport)
#else
#define SONY_CR_API __declspec(dllimport)
#endif
#else
#define SONY_CR_API
#endif

/** 与 C# SonyCrStatus 保持一致 */
typedef int SonyCrStatus;

#define SONY_CR_OK 0
#define SONY_CR_ERR_INIT_FAILED -1
#define SONY_CR_ERR_NOT_INITIALIZED -2
#define SONY_CR_ERR_ENUM_FAILED -3
#define SONY_CR_ERR_INVALID_INDEX -4
#define SONY_CR_ERR_BUFFER_TOO_SMALL -5
#define SONY_CR_ERR_CONNECT_FAILED -6
#define SONY_CR_ERR_NOT_CONNECTED -7
#define SONY_CR_ERR_CONTROL_FAILED -8
#define SONY_CR_ERR_INVALID_PARAM -9
/** Remote Transfer 列表中未找到与给定文件名（不含路径）匹配的条目。 */
#define SONY_CR_ERR_NOT_FOUND -10
#define SONY_CR_ERR_SDK_NOT_LINKED -999
#define SONY_CR_ERR_NOT_IMPLEMENTED -100

/** 初始化 SCRSDK（对应 SCRSDK::Init）。 */
SONY_CR_API SonyCrStatus SonyCr_Init(void);

/** 释放 SCRSDK 与枚举缓存（对应 SCRSDK::Release）。会自动断开设备。 */
SONY_CR_API void SonyCr_Release(void);

/** 获取打包版本号（对应 SCRSDK::GetSDKVersion）。 */
SONY_CR_API SonyCrStatus SonyCr_GetSdkVersion(unsigned int* outVersion);

/** 重新枚举相机并缓存列表（对应 SCRSDK::EnumCameraObjects）。 */
SONY_CR_API SonyCrStatus SonyCr_EnumCameraDevicesRefresh(void);

/** 当前缓存列表中的相机数量。需先 SonyCr_EnumCameraDevicesRefresh。 */
SONY_CR_API SonyCrStatus SonyCr_GetCameraDeviceCount(int* outCount);

/**
 * 获取相机型号字符串（UTF-8，含结尾 0）。
 * @param index 从 0 开始
 */
SONY_CR_API SonyCrStatus SonyCr_GetCameraModelUtf8(int index, char* buffer, int bufferSizeBytes);

/** 返回型号 UTF-8 字节数（含 \\0），便于分配 buffer。 */
SONY_CR_API SonyCrStatus SonyCr_GetCameraModelUtf8Length(int index, int* outLengthBytes);

/*
 * 以 Remote 模式连接相机（SCRSDK::Connect + CrSdkControlMode_Remote）。
 * 需先 SonyCr_EnumCameraDevicesRefresh；成功后回调 OnConnected 再开启 Live View。
 */
SONY_CR_API SonyCrStatus SonyCr_ConnectRemoteByIndex(int index);

/** 断开连接（Disconnect + ReleaseDevice），不释放全局 SDK。 */
SONY_CR_API SonyCrStatus SonyCr_Disconnect(void);

/**
 * 拉取一帧 Live View JPEG 到 buffer。
 * @param outWritten 实际写入字节数；若缓冲区不足返回 SONY_CR_ERR_BUFFER_TOO_SMALL 且可据此扩容。
 */
SONY_CR_API SonyCrStatus SonyCr_LiveView_GetLastJpeg(unsigned char* buffer, int bufferSize, int* outWritten);

/**
 * 遥控触摸对焦：与 CrControlCode_RemoteTouchOperation 一致，x/y 为相机坐标系 0~639 / 0~479。
 * 需机身开启 Remote touch 且 CrDeviceProperty_RemoteTouchOperationEnableStatus 为 Enable。
 * 等价于 SonyCr_ExecuteControlCodeValue(0x0000D2E4, (x<<16)|y)（便捷封装，新功能可只用通用 API）。
 */
SONY_CR_API SonyCrStatus SonyCr_RemoteTouchAf(int x, int y);

/* ========== 通用扩展（优先在 C# 调用以下接口，避免每次改 C++ 重编 DLL）========== */
/** 对应 SCRSDK::ExecuteControlCodeValue；code 见 CrControlCode.h（如 0x0000D2E4 = RemoteTouch）。 */
SONY_CR_API SonyCrStatus SonyCr_ExecuteControlCodeValue(unsigned int code, unsigned long long value);

/** 对应 SCRSDK::ExecuteControlCodeString；utf16 为 UTF-16 码元，length 为码元个数（非字节）。 */
SONY_CR_API SonyCrStatus SonyCr_ExecuteControlCodeString(unsigned int code, const unsigned short* utf16, unsigned int length);

/**
 * 设置单个标量机身属性：propertyCode 见 CrDeviceProperty.h 的 CrDevicePropertyCode；
 * crDataType 见 CrDefines.h 的 CrDataType（如 0x0001 = UInt8）。
 */
SONY_CR_API SonyCrStatus SonyCr_SetDevicePropertyU64(unsigned int propertyCode, unsigned long long value, unsigned int crDataType);

/**
 * 背屏 DISP：与 RemoteCli 一致先写 DispModeSetting（候选 OR 掩码）再写 DispMode；优先静态拍照用的 DispModeStill，失败再试通用 DispMode。
 * mode 为 CrDispMode（如 0x07 = MonitorOff 熄屏）。
 */
SONY_CR_API SonyCrStatus SonyCr_ApplyMonitorDispMode(unsigned char mode);

/**
 * 仅 AF：在单线程内持全局锁完成 PriorityKey(PCRemote) + S1 Locked、保持 holdMs、再 S1 Unlocked。
 * 避免托管侧分两次 SetDeviceProperty + Sleep 时锁被释放，Live View 等插入导致半按状态异常。
 */
SONY_CR_API SonyCrStatus SonyCr_HalfPressShutterS1AfOnly(unsigned int holdMs);

/** 半按按下：PriorityKey(PCRemote) + S1 Locked（与官方 TS「Shutter Half Release / Auto Focus」顺序一致）。 */
SONY_CR_API SonyCrStatus SonyCr_HalfPressShutterS1Press(void);

/** 半按松开：仅 S1 Unlocked。 */
SONY_CR_API SonyCrStatus SonyCr_HalfPressShutterS1Release(void);

/**
 * 在 S1 已半按锁定状态下：单次持锁完成 Release Down → 35ms → Release Up → 1000ms → S1 Unlocked。
 * 与官方 RemoteCli CameraDevice::af_shutter() 一致；避免分多次 P/Invoke 时 g_mutex 被 Live View 插入导致 ErrControlFailed / 卡死。
 */
SONY_CR_API SonyCrStatus SonyCr_ReleaseShutterDownUpThenS1Unlock(void);

/**
 * 设置遥控触摸启用（CrDeviceProperty_RemoteTouchOperationEnableStatus，0=关 1=开）。
 * 内部先设 PriorityKeySettings=PCRemote（与官方 Remote 说明一致），再 SetDeviceProperty。
 */
SONY_CR_API SonyCrStatus SonyCr_SetRemoteTouchOperationEnable(int enable);

/** 对应 SCRSDK::SendCommand；commandId 见 CrCommandData.h 的 CrCommandId。 */
SONY_CR_API SonyCrStatus SonyCr_SendCommand(unsigned int commandId, unsigned int commandParam);

/** 对应 SCRSDK::SetDeviceSetting；key 见 CrDefines.h SettingKey（如 0 = EnableLiveView）。 */
SONY_CR_API SonyCrStatus SonyCr_SetDeviceSetting(unsigned int key, unsigned int value);

/** 获取桥接层统计到的相机会话上传/下载总字节数（会话维度，非系统总网速）。 */
SONY_CR_API SonyCrStatus SonyCr_GetTransportStats(unsigned long long* outUploadBytes, unsigned long long* outDownloadBytes);

/** 重置桥接层上传/下载字节统计。通常在连接建立后调用。 */
SONY_CR_API void SonyCr_ResetTransportStats(void);

/**
 * 读取相机端 SD 卡容量/使用量估算（桥接层在 native 侧统计两卡槽）。
 *
 * - outSlotXUsedBytes/outSlotXTotalBytes：基于仍图“传输大小”dp_Still_Image_Trans_Size 与
 *   (已存在内容个数 + MediaSLOTx_RemainingNumber) 进行换算的估算值。
 * - outSlotXHasCard：卡槽存在且可用于统计时为 1，否则为 0。
 */
SONY_CR_API SonyCrStatus SonyCr_GetSdCardUsageEstimate(
    unsigned long long* outSlot1TotalBytes,
    unsigned long long* outSlot1UsedBytes,
    int* outSlot1HasCard,
    unsigned long long* outSlot2TotalBytes,
    unsigned long long* outSlot2UsedBytes,
    int* outSlot2HasCard);

/**
 * 对应 SCRSDK::SetSaveInfo（遥控拍摄保存到 PC 的路径/前缀）。
 * Windows 下 path/prefix 为 UTF-16（wchar_t）；saveNumber 为 CrSETSAVEINFO_AUTO_NUMBER(-1) 表示自动编号。
 */
SONY_CR_API SonyCrStatus SonyCr_SetSaveInfoUtf16(const unsigned short* pathUtf16, const unsigned short* prefixUtf16, int saveNumber);

/**
 * 拍照后若机身仅写入存储卡：从卡上拉取「当前日期文件夹中最后一个内容」到本机目录（与 PullContentsFile 一致）。
 * 调用前会短时 sleep，且须在 Remote 已连接状态下调用。
 */
SONY_CR_API SonyCrStatus SonyCr_PullLatestStillToFolderUtf16(const unsigned short* destFolderUtf16);

/**
 * 同上，但可一次拉取末尾多条（RAW+JPEG 时通常为 2）。pullCount 建议 1～2。
 * pullCount≥2 时优先走官方「Contents transfer with remote control」：
 * GetRemoteTransferContentsInfoList + 对同一 CrContentsInfo 下每个 fileId 调用 GetRemoteTransferContentsDataFile（见 CrSDK v2.01 文档与 SimpleCli RemoteTransferMode）；
 * 失败时再回退到 GetDateFolderList + PullContentsFile。
 */
SONY_CR_API SonyCrStatus SonyCr_PullLatestStillsToFolderUtf16(const unsigned short* destFolderUtf16, int pullCount);

/**
 * 在已连接且支持 Remote Transfer 列表时：按「保存文件名」（不含路径，如 DSC00001.JPG）查找对应 CrContentsInfo，
 * 并调用 DeleteRemoteTransferContentsFile（RAW+JPEG 等为同一 contentId，一次删除多文件）。
 * 异步完成依赖 OnNotifyRemoteTransferResult，与 GetRemoteTransferContentsDataFile 相同。
 * 未找到匹配时返回 SONY_CR_ERR_NOT_FOUND。
 */
SONY_CR_API SonyCrStatus SonyCr_DeleteRemoteContentMatchingFileNameUtf16(const unsigned short* fileNameUtf16);

/**
 * 读取曝光模式、光圈、快门、ISO、白平衡的当前值/可写/候选，序列化为 UTF-8 JSON（含结尾 \\0）。
 * STUB 编译时返回占位 JSON。缓冲区不足时返回 SONY_CR_ERR_BUFFER_TOO_SMALL，并在 *outWritten 写入所需字节数（含 \\0）。
 */
SONY_CR_API SonyCrStatus SonyCr_GetShootingStateJsonUtf8(char* buffer, int bufferSizeBytes, int* outWritten);

/**
 * 最近一次 Live View 拉流中由 GetLiveViewProperties 解析的对焦框（CrFocusFrameInfo）。
 * JSON 为 UTF-8 数组，元素字段：t=type、s=state、xn/xd/yn/yd=分数字坐标、w/h=框尺寸（与分母同坐标系）。
 */
SONY_CR_API SonyCrStatus SonyCr_GetLiveViewFocusFramesJsonUtf8(char* buffer, int bufferSizeBytes, int* outWritten);

#ifdef __cplusplus
}
#endif
