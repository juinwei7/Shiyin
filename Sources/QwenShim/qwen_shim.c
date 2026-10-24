#include "qwen_shim.h"
// llama.cpp 的薄層（design §32.2、D106）：把 Qwen3-ASR 的一段音訊轉成文字。提示詞是 GGUF 裡的 ChatML 樣板：
// system = 詞彙表、user = 音訊、assistant 先填「language X<asr_text>」強制語言（Qwen3-ASR 官方的做法）。
// 每段開始前清掉 KV cache；greedy 取樣，重試時才加重複懲罰和溫度（§32.4）。
#include <llama/llama.h>
#include <llama/mtmd.h>
#include <llama/mtmd-helper.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct qwen_engine {
    struct llama_model *model;
    struct llama_context *lctx;
    mtmd_context *mctx;
    const struct llama_vocab *vocab;
    int32_t n_batch;
};

static void quiet_log(enum ggml_log_level level, const char *text, void *user) { (void)level; (void)text; (void)user; }

qwen_params qwen_params_default(void) {
    qwen_params p = { NULL, "Chinese", 1.0f, 64, 0.0f, 0, 512 };
    return p;
}

const char *qwen_system_info(void) {
    llama_backend_init();
    return llama_print_system_info();
}

qwen_engine *qwen_load(const char *model_path, const char *mmproj_path, int32_t n_ctx, int32_t n_threads, bool use_gpu) {
    llama_log_set(quiet_log, NULL);
    mtmd_helper_log_set(quiet_log, NULL);
    llama_backend_init();
    struct llama_model_params mp = llama_model_default_params();
    mp.n_gpu_layers = use_gpu ? 999 : 0;
    struct llama_model *model = llama_model_load_from_file(model_path, mp);
    if (!model) return NULL;

    struct llama_context_params cp = llama_context_default_params();
    cp.n_ctx = (uint32_t)n_ctx;
    cp.n_batch = 2048;
    cp.n_ubatch = 512;
    cp.n_threads = n_threads;
    cp.n_threads_batch = n_threads;
    cp.no_perf = true;
    struct llama_context *lctx = llama_init_from_model(model, cp);
    if (!lctx) { llama_model_free(model); return NULL; }

    struct mtmd_context_params mcp = mtmd_context_params_default();
    mcp.use_gpu = use_gpu;
    mcp.print_timings = false;
    mcp.n_threads = n_threads;
    mcp.warmup = false;
    mtmd_context *mctx = mtmd_init_from_file(mmproj_path, model, mcp);
    if (!mctx || !mtmd_support_audio(mctx)) {
        if (mctx) mtmd_free(mctx);
        llama_free(lctx);
        llama_model_free(model);
        return NULL;
    }
    qwen_engine *e = calloc(1, sizeof(*e));
    e->model = model; e->lctx = lctx; e->mctx = mctx;
    e->vocab = llama_model_get_vocab(model);
    e->n_batch = (int32_t)cp.n_batch;
    return e;
}

void qwen_free(qwen_engine *e) {
    if (!e) return;
    mtmd_free(e->mctx);
    llama_free(e->lctx);
    llama_model_free(e->model);
    free(e);
}

int32_t qwen_sample_rate(const qwen_engine *e) { return mtmd_get_audio_sample_rate(e->mctx); }

int32_t qwen_transcribe(qwen_engine *e, const float *samples, int32_t n_samples, const qwen_params *params,
                       qwen_abort_fn abort_cb, void *abort_user, char *out, int32_t out_len, int32_t *n_tokens_out) {
    if (out_len > 0) out[0] = 0;
    if (n_tokens_out) *n_tokens_out = 0;
    qwen_params p = params ? *params : qwen_params_default();

    // 提示詞：ChatML（GGUF 裡的樣板）；system = context，user = 音訊，assistant 先填語言前綴（Qwen3-ASR 的輸出格式）
    const char *marker = mtmd_default_marker();
    const char *context = p.context ? p.context : "";
    size_t cap = strlen(context) + strlen(marker) + strlen(p.language) + 160;
    char *prompt = malloc(cap);
    snprintf(prompt, cap, "<|im_start|>system\n%s<|im_end|>\n<|im_start|>user\n%s<|im_end|>\n<|im_start|>assistant\nlanguage %s<asr_text>",
             context, marker, p.language);

    int32_t rc = 0;
    mtmd_bitmap *bitmap = mtmd_bitmap_init_from_audio((size_t)n_samples, samples);
    mtmd_input_chunks *chunks = mtmd_input_chunks_init();
    struct llama_sampler *smpl = NULL;
    if (!bitmap || !chunks) { rc = -2; goto done; }

    struct mtmd_input_text text = { prompt, strlen(prompt), true, true };
    const mtmd_bitmap *bitmaps[1] = { bitmap };
    if (mtmd_tokenize(e->mctx, chunks, &text, bitmaps, 1) != 0) { rc = -3; goto done; }

    llama_memory_clear(llama_get_memory(e->lctx), true);
    llama_pos n_past = 0;
    if (mtmd_helper_eval_chunks(e->mctx, e->lctx, chunks, 0, 0, e->n_batch, true, &n_past) != 0) { rc = -4; goto done; }
    if (abort_cb && abort_cb(abort_user)) { rc = -1; goto done; }

    struct llama_sampler_chain_params sp = llama_sampler_chain_default_params();
    sp.no_perf = true;
    smpl = llama_sampler_chain_init(sp);
    if (p.repeat_penalty > 1.0f && p.repeat_last_n > 0) {
        llama_sampler_chain_add(smpl, llama_sampler_init_penalties(llama_vocab_n_tokens(e->vocab), p.repeat_last_n, p.repeat_penalty, 0.0f, 0.0f));
    }
    if (p.temperature > 0) {
        llama_sampler_chain_add(smpl, llama_sampler_init_temp(p.temperature));
        llama_sampler_chain_add(smpl, llama_sampler_init_dist(p.seed));
    } else {
        llama_sampler_chain_add(smpl, llama_sampler_init_greedy());
    }

    int32_t written = 0, produced = 0;
    char piece[256];
    for (int32_t i = 0; i < p.max_tokens; i++) {
        llama_token token = llama_sampler_sample(smpl, e->lctx, -1);
        llama_sampler_accept(smpl, token);
        if (llama_vocab_is_eog(e->vocab, token)) break;
        int32_t n = llama_token_to_piece(e->vocab, token, piece, (int32_t)sizeof(piece), 0, false);
        if (n > 0) {
            if (written + n >= out_len) { rc = -5; break; }
            memcpy(out + written, piece, (size_t)n);
            written += n;
            out[written] = 0;
        }
        produced++;
        if (produced >= p.max_tokens) { rc = 1; break; }
        if (abort_cb && abort_cb(abort_user)) { rc = -1; break; }
        struct llama_batch batch = llama_batch_get_one(&token, 1);
        if (llama_decode(e->lctx, batch) != 0) { rc = -6; break; }
        n_past++;
    }
    if (n_tokens_out) *n_tokens_out = produced;
done:
    if (smpl) llama_sampler_free(smpl);
    if (chunks) mtmd_input_chunks_free(chunks);
    if (bitmap) mtmd_bitmap_free(bitmap);
    free(prompt);
    return rc;
}
