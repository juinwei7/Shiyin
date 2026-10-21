#include "shiyin_whisper.h"
#include "whisper.h"
#include "ggml-backend.h"
#include <string.h>

#if defined(_WIN32)
#include <windows.h>

// Windows 的 ggml 後端（ggml-cpu-*、ggml-vulkan）是執行時才載入的 DLL（GGML_BACKEND_DL），whisper 本身不會載入，
// 沒有後端時 whisper_init 會在 GGML_ASSERT(device) 結束程式（whisper-cli 是在 main 開頭自己呼叫 ggml_backend_load_all）。
// 第一次用到時，從這個 DLL 所在的資料夾載入（路徑用 UTF-8，使用者名稱是中文也可以）
static BOOL CALLBACK load_backends(PINIT_ONCE once, void * param, void ** context) {
    (void)once; (void)param; (void)context;
    if (ggml_backend_dev_by_type(GGML_BACKEND_DEVICE_TYPE_CPU) != NULL) return TRUE;
    HMODULE self = NULL;
    wchar_t path[4096];
    DWORD n = 0;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(void *)&load_backends, &self))
        n = GetModuleFileNameW(self, path, (DWORD)(sizeof(path) / sizeof(path[0])));
    while (n > 0 && path[n - 1] != L'\\' && path[n - 1] != L'/') n--;
    char dir[4096 * 3];
    if (n > 1 && WideCharToMultiByte(CP_UTF8, 0, path, (int)(n - 1), dir, (int)sizeof(dir) - 1, NULL, NULL) > 0) {
        dir[WideCharToMultiByte(CP_UTF8, 0, path, (int)(n - 1), NULL, 0, NULL, NULL)] = 0;
        ggml_backend_load_all_from_path(dir);
    } else {
        ggml_backend_load_all();
    }
    return TRUE;
}

static void ensure_backends(void) {
    static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
    InitOnceExecuteOnce(&once, load_backends, NULL, NULL);
}
#else
// Mac：whisper、ggml 靜態連結，後端（CPU、Metal）一開始就註冊好了
static void ensure_backends(void) {}
#endif

int32_t shiyin_version(void) { return 1; }

static void quiet(enum ggml_log_level level, const char * text, void * user) { (void)level; (void)text; (void)user; }

void shiyin_silence_logs(void) { whisper_log_set(quiet, NULL); }

int32_t shiyin_system_info(char * buffer, int32_t size) {
    ensure_backends();
    const char * info = whisper_print_system_info();
    int32_t n = (int32_t)strlen(info);
    if (buffer && size > 0) {
        int32_t copy = n < size - 1 ? n : size - 1;
        memcpy(buffer, info, (size_t)copy);
        buffer[copy] = 0;
    }
    return n;
}

void * shiyin_init(const char * model_path, int32_t use_gpu, int32_t flash_attn) {
    ensure_backends();
    struct whisper_context_params params = whisper_context_default_params();
    params.use_gpu = use_gpu != 0;
    params.flash_attn = flash_attn != 0;
    return whisper_init_from_file_with_params(model_path, params);
}

void shiyin_free(void * ctx) { if (ctx) whisper_free((struct whisper_context *)ctx); }

typedef struct { const shiyin_full_params * p; } callbacks;

static void on_progress(struct whisper_context * ctx, struct whisper_state * state, int progress, void * user) {
    (void)ctx; (void)state;
    const shiyin_full_params * p = (const shiyin_full_params *)user;
    if (p->progress) p->progress(progress, p->progress_user);
}

static bool on_abort(void * user) {
    const shiyin_full_params * p = (const shiyin_full_params *)user;
    return p->abort ? p->abort(p->abort_user) != 0 : false;
}

// 參數跟 Mac 的 WhisperEngine.run 一樣：beam search、print 全關（M§6.2）
int32_t shiyin_full(void * ctx, const shiyin_full_params * p, const float * samples, int32_t n_samples) {
    struct whisper_full_params params = whisper_full_default_params(WHISPER_SAMPLING_BEAM_SEARCH);
    params.beam_search.beam_size = p->beam_size;
    params.greedy.best_of = p->best_of;
    params.n_threads = p->n_threads;
    params.print_progress = false;
    params.print_realtime = false;
    params.print_timestamps = false;
    params.print_special = false;
    params.suppress_blank = p->suppress_blank != 0;
    params.language = p->language;
    params.initial_prompt = p->initial_prompt;
    params.progress_callback = on_progress;
    params.progress_callback_user_data = (void *)p;
    params.abort_callback = on_abort;
    params.abort_callback_user_data = (void *)p;
    return whisper_full((struct whisper_context *)ctx, params, samples, n_samples);
}

int32_t shiyin_n_segments(void * ctx) { return whisper_full_n_segments((struct whisper_context *)ctx); }
const char * shiyin_segment_text(void * ctx, int32_t i) { return whisper_full_get_segment_text((struct whisper_context *)ctx, i); }
int64_t shiyin_segment_t0(void * ctx, int32_t i) { return whisper_full_get_segment_t0((struct whisper_context *)ctx, i); }
int64_t shiyin_segment_t1(void * ctx, int32_t i) { return whisper_full_get_segment_t1((struct whisper_context *)ctx, i); }
float shiyin_segment_no_speech_prob(void * ctx, int32_t i) { return whisper_full_get_segment_no_speech_prob((struct whisper_context *)ctx, i); }
int32_t shiyin_n_tokens(void * ctx, int32_t i) { return whisper_full_n_tokens((struct whisper_context *)ctx, i); }
int32_t shiyin_token_id(void * ctx, int32_t i, int32_t j) { return whisper_full_get_token_id((struct whisper_context *)ctx, i, j); }
float shiyin_token_p(void * ctx, int32_t i, int32_t j) { return whisper_full_get_token_p((struct whisper_context *)ctx, i, j); }
int32_t shiyin_token_eot(void * ctx) { return whisper_token_eot((struct whisper_context *)ctx); }

int32_t shiyin_pcm_to_mel(void * ctx, const float * samples, int32_t n_samples, int32_t n_threads) {
    return whisper_pcm_to_mel((struct whisper_context *)ctx, samples, n_samples, n_threads);
}
int32_t shiyin_lang_max_id(void) { return whisper_lang_max_id(); }
int32_t shiyin_lang_id(const char * lang) { return whisper_lang_id(lang); }
int32_t shiyin_lang_auto_detect(void * ctx, int32_t n_threads, float * probs) {
    return whisper_lang_auto_detect((struct whisper_context *)ctx, 0, n_threads, probs);
}
