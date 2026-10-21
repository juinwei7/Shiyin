// 拾音的 whisper.cpp 轉接層（design-windows §8、D56）：C# 只呼叫這裡的 shiyin_* 函式。
// whisper_full_params 用傳值的方式傳、裡面有 1 byte 的 bool、巢狀 struct 和 callback，在 C# 宣告容易對錯，所以在 C 這邊填好。
#pragma once
#include <stdint.h>

#if defined(_WIN32)
#define SHIYIN_API __declspec(dllexport)
#else
#define SHIYIN_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*shiyin_progress_cb)(int32_t percent, void * user);
typedef int32_t (*shiyin_abort_cb)(void * user);   // 回傳非 0 表示要取消

typedef struct {
    int32_t beam_size;
    int32_t best_of;
    int32_t n_threads;
    int32_t suppress_blank;   // 0 或 1
    const char * language;     // "zh"、"en"、"ja"
    const char * initial_prompt;
    shiyin_progress_cb progress;
    void * progress_user;
    shiyin_abort_cb abort;
    void * abort_user;
} shiyin_full_params;

SHIYIN_API int32_t shiyin_version(void);
SHIYIN_API void shiyin_silence_logs(void);
// 有哪些後端（CPU、Metal、Vulkan…），寫進 buffer，回傳長度
SHIYIN_API int32_t shiyin_system_info(char * buffer, int32_t size);
// whisper 會用的顯示卡：「名稱（說明）」寫進 buffer，回傳長度；只有 CPU 時是 0
SHIYIN_API int32_t shiyin_gpu_device(char * buffer, int32_t size);

SHIYIN_API void * shiyin_init(const char * model_path, int32_t use_gpu, int32_t flash_attn);
SHIYIN_API void shiyin_free(void * ctx);
SHIYIN_API int32_t shiyin_full(void * ctx, const shiyin_full_params * params, const float * samples, int32_t n_samples);

SHIYIN_API int32_t shiyin_n_segments(void * ctx);
SHIYIN_API const char * shiyin_segment_text(void * ctx, int32_t i);
SHIYIN_API int64_t shiyin_segment_t0(void * ctx, int32_t i);
SHIYIN_API int64_t shiyin_segment_t1(void * ctx, int32_t i);
SHIYIN_API float shiyin_segment_no_speech_prob(void * ctx, int32_t i);
SHIYIN_API int32_t shiyin_n_tokens(void * ctx, int32_t i);
SHIYIN_API int32_t shiyin_token_id(void * ctx, int32_t i, int32_t j);
SHIYIN_API float shiyin_token_p(void * ctx, int32_t i, int32_t j);
SHIYIN_API int32_t shiyin_token_eot(void * ctx);

// 語言偵測（M§18.8）
SHIYIN_API int32_t shiyin_pcm_to_mel(void * ctx, const float * samples, int32_t n_samples, int32_t n_threads);
SHIYIN_API int32_t shiyin_lang_max_id(void);
SHIYIN_API int32_t shiyin_lang_id(const char * lang);
SHIYIN_API int32_t shiyin_lang_auto_detect(void * ctx, int32_t n_threads, float * probs);

#ifdef __cplusplus
}
#endif
