// 拾音匿名使用統計的後台（docs/design.md §30.10、requirements R40.6）：收 app 每天送一次的匯總，寫進 D1。
// 格式見 design §30.4。不存 IP、不存標頭；同一台同一天（installId + date）重送就覆蓋，不重複算。
// 格式不對回 4xx（app 收到 4xx 會丟掉那一天、不再重送）。

const MAX_BODY = 8 * 1024;
const MAX_NUMBER = 1_000_000;
const MAX_TEXT = 64;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

// §30.4 的欄位 → 資料表的欄位（schema.sql）
const APP = { version: 'app_version', os: 'os', chip: 'chip', uiLanguage: 'ui_language' };
const PLATFORMS = ['mac', 'windows'];
const USAGE = {
  meetingsRecorded: 'meetings_recorded', recordedSeconds: 'recorded_seconds',
  meetingsImported: 'meetings_imported', importedSeconds: 'imported_seconds',
  transcribedSeconds: 'transcribed_seconds', transcribeWallSeconds: 'transcribe_wall_seconds',
  diarizationRuns: 'diarization_runs', questionsAsked: 'questions_asked',
};
const USAGE_DICTS = {
  engines: { breeze: 'engine_breeze', builtin: 'engine_builtin', whisperJa: 'engine_whisper_ja', qwen: 'engine_qwen' },
  meetingLanguages: { zh: 'lang_zh', en: 'lang_en', ja: 'lang_ja' },
  summarizer: { claudeCode: 'summarizer_claude_code', codex: 'summarizer_codex', api: 'summarizer_api', local: 'summarizer_local' },
  // 2026-10-23 加（R41.7、§31.3）：舊版 app 沒有這組就全部當 0
  review: { answered: 'review_answered', confirmed: 'review_confirmed', skipped: 'review_skipped' },
};
const ERRORS = { recording: 'errors_recording', transcribe: 'errors_transcribe', diarize: 'errors_diarize', summarize: 'errors_summarize' };
// 2026-10-25 加（R47、design §34.5）：整理失敗的種類、當時的整理工具、其中幾次是草稿；舊版 app 沒有 errorDetail 就全部當 0
const ERROR_DETAIL_DICTS = {
  summarize: {
    notInstalled: 'summarize_err_not_installed', notLoggedIn: 'summarize_err_not_logged_in', missingKey: 'summarize_err_missing_key',
    notConfigured: 'summarize_err_not_configured', keyRejected: 'summarize_err_key_rejected', limited: 'summarize_err_limited',
    timeout: 'summarize_err_timeout', unreachable: 'summarize_err_unreachable', badOutput: 'summarize_err_bad_output',
    other: 'summarize_err_other',
  },
  summarizeProvider: {
    claudeCode: 'summarize_err_claude_code', codex: 'summarize_err_codex', api: 'summarize_err_api', local: 'summarize_err_local',
  },
};
const ERROR_DETAIL_NUMBERS = { summarizeDraft: 'summarize_err_draft' };

const text = (status, body) => new Response(body, { status, headers: { 'Content-Type': 'text/plain; charset=utf-8' } });

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === '/v1/health' && request.method === 'GET') return text(200, 'ok');
    if (url.pathname !== '/v1/usage') return text(404, 'not found');
    if (request.method !== 'POST') return new Response('method not allowed', { status: 405, headers: { Allow: 'POST' } });

    // 頻率限制：IP 只拿來算次數，不存（wrangler.toml 的 ratelimits；本機測試時沒有就略過）
    if (env.LIMITER) {
      const { success } = await env.LIMITER.limit({ key: request.headers.get('CF-Connecting-IP') || 'unknown' });
      if (!success) return text(429, 'too many requests');
    }
    if (!(request.headers.get('Content-Type') || '').toLowerCase().startsWith('application/json')) {
      return text(415, 'content type must be application/json');
    }
    if (Number(request.headers.get('Content-Length') || 0) > MAX_BODY) return text(413, 'payload too large');
    const raw = await request.text();
    if (raw.length > MAX_BODY) return text(413, 'payload too large');

    let body;
    try {
      body = JSON.parse(raw);
    } catch {
      return text(400, 'not json');
    }
    const row = toRow(body, new Date());
    if (typeof row === 'string') return text(400, row);
    await upsert(env.DB, row);
    return new Response(null, { status: 204 });
  },
};

/** 檢查 §30.4 的格式，回傳資料表的一列；不對就回傳原因（字串） */
export function toRow(body, now) {
  if (!body || typeof body !== 'object' || Array.isArray(body)) return 'body must be an object';
  if (body.schema !== 1) return 'unsupported schema';
  if (typeof body.installId !== 'string' || !UUID.test(body.installId)) return 'bad installId';
  if (typeof body.date !== 'string' || !DATE.test(body.date)) return 'bad date';
  // app 用本機時區的日期；接受 UTC 的今天前 7 天到明天
  const day = (offset) => new Date(now.getTime() + offset * 86_400_000).toISOString().slice(0, 10);
  if (body.date < day(-7) || body.date > day(1)) return 'date out of range';

  const row = { install_id: body.installId, date: body.date, received_at: now.toISOString() };
  const app = body.app;
  if (!app || typeof app !== 'object') return 'bad app';
  if (!PLATFORMS.includes(app.platform)) return 'bad app.platform';
  row.platform = app.platform;
  for (const [key, column] of Object.entries(APP)) {
    const value = app[key];
    if (typeof value !== 'string' || value.length > MAX_TEXT) return `bad app.${key}`;
    row[column] = value;
  }

  const usage = body.usage;
  if (!usage || typeof usage !== 'object') return 'bad usage';
  for (const [key, column] of Object.entries(USAGE)) {
    const value = number(usage[key]);
    if (value === null) return `bad usage.${key}`;
    row[column] = value;
  }
  for (const [dict, columns] of Object.entries(USAGE_DICTS)) {
    const values = usage[dict] ?? {};
    if (typeof values !== 'object' || Array.isArray(values)) return `bad usage.${dict}`;
    for (const [key, column] of Object.entries(columns)) {
      const value = number(values[key] ?? 0);   // 少的鍵當 0，多的鍵忽略
      if (value === null) return `bad usage.${dict}.${key}`;
      row[column] = value;
    }
  }
  const errors = body.errors ?? {};
  if (typeof errors !== 'object' || Array.isArray(errors)) return 'bad errors';
  for (const [key, column] of Object.entries(ERRORS)) {
    const value = number(errors[key] ?? 0);
    if (value === null) return `bad errors.${key}`;
    row[column] = value;
  }
  const detail = body.errorDetail ?? {};
  if (typeof detail !== 'object' || Array.isArray(detail)) return 'bad errorDetail';
  for (const [dict, columns] of Object.entries(ERROR_DETAIL_DICTS)) {
    const values = detail[dict] ?? {};
    if (typeof values !== 'object' || Array.isArray(values)) return `bad errorDetail.${dict}`;
    for (const [key, column] of Object.entries(columns)) {
      const value = number(values[key] ?? 0);
      if (value === null) return `bad errorDetail.${dict}.${key}`;
      row[column] = value;
    }
  }
  for (const [key, column] of Object.entries(ERROR_DETAIL_NUMBERS)) {
    const value = number(detail[key] ?? 0);
    if (value === null) return `bad errorDetail.${key}`;
    row[column] = value;
  }
  return row;
}

function number(value) {
  return Number.isInteger(value) && value >= 0 && value <= MAX_NUMBER ? value : null;
}

/** 同一台同一天重送就覆蓋（主鍵 install_id + date） */
async function upsert(db, row) {
  const columns = Object.keys(row);
  const updates = columns.filter((c) => c !== 'install_id' && c !== 'date').map((c) => `${c} = excluded.${c}`);
  const sql = `INSERT INTO usage_daily (${columns.join(', ')}) VALUES (${columns.map(() => '?').join(', ')})
    ON CONFLICT (install_id, date) DO UPDATE SET ${updates.join(', ')}`;
  await db.prepare(sql).bind(...columns.map((c) => row[c])).run();
}
