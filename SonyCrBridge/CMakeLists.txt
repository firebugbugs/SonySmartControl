cmake_minimum_required(VERSION 3.20)
project(SonyCrBridge LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(SONY_CR_BRIDGE_STUB "Build stub without Sony CrSDK headers/libs (for CI / 无 SDK 机器)" ON)
# 示例（v2.01 Win64）：.../CrSDK_v2.01.00_20260203a_Win64/RemoteCli（其下含 app/CRSDK 与 external/crsdk）
set(SONY_CRSDK_ROOT "" CACHE PATH "官方包中的 RemoteCli 目录（含 app/CRSDK 与 external/crsdk）")

add_library(SonyCrBridge SHARED
    src/SonyCrBridge.cpp
)
target_include_directories(SonyCrBridge PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(SonyCrBridge PRIVATE SONY_CR_BRIDGE_EXPORTS)

if(WIN32)
    target_compile_definitions(SonyCrBridge PRIVATE UNICODE _UNICODE)
endif()

if(SONY_CR_BRIDGE_STUB)
    target_compile_definitions(SonyCrBridge PRIVATE SONY_CR_BRIDGE_STUB=1)
    message(STATUS "SonyCrBridge: STUB build (no CrSDK link). Set -DSONY_CR_BRIDGE_STUB=OFF -DSONY_CRSDK_ROOT=...")
else()
    target_compile_definitions(SonyCrBridge PRIVATE SONY_CR_BRIDGE_STUB=0)
    if(NOT SONY_CRSDK_ROOT OR NOT EXISTS "${SONY_CRSDK_ROOT}")
        message(FATAL_ERROR "SONY_CR_BRIDGE_STUB=OFF 时必须设置有效的 SONY_CRSDK_ROOT")
    endif()

    set(_CRSDK_INC "${SONY_CRSDK_ROOT}/app/CRSDK")
    if(NOT EXISTS "${_CRSDK_INC}/CameraRemote_SDK.h")
        message(FATAL_ERROR "未找到 ${_CRSDK_INC}/CameraRemote_SDK.h，请确认 SONY_CRSDK_ROOT 为官方包根目录")
    endif()

    target_include_directories(SonyCrBridge PRIVATE "${_CRSDK_INC}")

    if(WIN32)
        find_library(_CR_CORE_LIB Cr_Core PATHS "${SONY_CRSDK_ROOT}/external/crsdk" NO_DEFAULT_PATH REQUIRED)
        target_link_libraries(SonyCrBridge PRIVATE "${_CR_CORE_LIB}")
    else()
        message(FATAL_ERROR "当前 CMake 仅配置 Windows x64；其他平台请按索尼文档调整链接方式")
    endif()
endif()
