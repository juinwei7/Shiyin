# 建 shiyin_whisper（design-windows §8）：whisper.cpp 固定在 b5454（commit d1be6fd，跟 Mac 的 Package.swift 同一版）
#   cmake -S windows/native/whisper -B <建置資料夾> -DWHISPER_SOURCE=<whisper.cpp 的原始碼> [-DSHIYIN_VULKAN=ON]
# Mac：靜態連結 whisper、ggml，一個 dylib（Metal 內嵌）。Windows：ggml 的後端分開成 DLL，執行時依 CPU 選（GGML_BACKEND_DL）
cmake_minimum_required(VERSION 3.21)
project(shiyin_whisper C CXX)

set(WHISPER_SOURCE "" CACHE PATH "whisper.cpp 的原始碼（b5454）")
option(SHIYIN_VULKAN "x64 加上 Vulkan" OFF)
if(NOT WHISPER_SOURCE)
    message(FATAL_ERROR "要給 -DWHISPER_SOURCE")
endif()

set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
if(WIN32)
    set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
    set(GGML_BACKEND_DL ON CACHE BOOL "" FORCE)
    set(GGML_CPU_ALL_VARIANTS ON CACHE BOOL "" FORCE)
    set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
    set(GGML_VULKAN ${SHIYIN_VULKAN} CACHE BOOL "" FORCE)
else()
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    set(GGML_METAL ON CACHE BOOL "" FORCE)
    set(GGML_METAL_EMBED_LIBRARY ON CACHE BOOL "" FORCE)
endif()
if(WIN32)
    # 後端（ggml-cpu-*、ggml-vulkan）是執行時才載入的 DLL，沒有被 shiyin_whisper 連結，要跟著「全部」一起建
    add_subdirectory(${WHISPER_SOURCE} whisper)
else()
    add_subdirectory(${WHISPER_SOURCE} whisper EXCLUDE_FROM_ALL)
endif()

add_library(shiyin_whisper SHARED shiyin_whisper.c)
target_link_libraries(shiyin_whisper PRIVATE whisper)
set_target_properties(shiyin_whisper PROPERTIES C_VISIBILITY_PRESET hidden)
