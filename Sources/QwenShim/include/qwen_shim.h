#pragma once
#include <stdbool.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

/// llama.cpp 的薄層（Qwen3-ASR，design §32.2、D106）：Swift 只看得到這個標頭，llama.h、ggml.h 不進 Swift
/// （whisper 的 XCFramework 也帶一份不同版本的 ggml.h，兩個同時 import 會撞型別）
typedef struct qwen_engine qwen_engine;

/// 回傳 true 表示取消
typedef bool (*qwen_abort_fn)(void *user);

typedef struct {
    const char *context;      ///< 放進 system 訊息的詞彙表（Qwen3-ASR 的 contextual biasing），可以是 NULL
    const char *language;     ///< "Chinese"、"English"、"Japanese"：強制語言（assistant 先填「language X<asr_text>」）
    float repeat_penalty;     ///< 1.0 = 不加
    int32_t repeat_last_n;
    float temperature;        ///< 0 = greedy
    uint32_t seed;
    int32_t max_tokens;       ///< 最多生成幾個 token
} qwen_params;

qwen_params qwen_params_default(void);

/// 載入語言模型和音訊編碼器（mmproj）。n_ctx：文字＋音訊 token 的上限（30 秒音訊約 375 個 token）。失敗回傳 NULL
qwen_engine *qwen_load(const char *model_path, const char *mmproj_path, int32_t n_ctx, int32_t n_threads, bool use_gpu);
void qwen_free(qwen_engine *engine);

/// 音訊取樣率（16000）
int32_t qwen_sample_rate(const qwen_engine *engine);

/// 轉錄一段 16 kHz 單聲道浮點取樣。輸出 UTF-8 寫進 out（最多 out_len - 1 個位元組，一定補 NUL）。
/// 回傳 0 成功、1 到達 max_tokens（可能在跳針）、-1 取消、其他負數是錯誤（-5 輸出緩衝不夠）。n_tokens 回傳生成了幾個 token（可以是 NULL）
int32_t qwen_transcribe(qwen_engine *engine, const float *samples, int32_t n_samples, const qwen_params *params,
                        qwen_abort_fn abort_cb, void *abort_user, char *out, int32_t out_len, int32_t *n_tokens);

/// llama.cpp 的系統資訊（Metal、NEON…），寫 log 用
const char *qwen_system_info(void);

#ifdef __cplusplus
}
#endif
