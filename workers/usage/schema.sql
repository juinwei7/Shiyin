-- 拾音匿名使用統計的資料表（docs/design.md §30.10）。一台（install_id）一天（date）一列，重送就覆蓋。
-- 欄位攤平成一欄一個數字，直接用 SQL 加總。沒有 IP、沒有任何會議內容。
CREATE TABLE IF NOT EXISTS usage_daily (
  install_id TEXT NOT NULL,
  date TEXT NOT NULL,
  platform TEXT NOT NULL,          -- mac、windows
  app_version TEXT,
  os TEXT,
  chip TEXT,
  ui_language TEXT,
  meetings_recorded INTEGER NOT NULL DEFAULT 0,
  recorded_seconds INTEGER NOT NULL DEFAULT 0,
  meetings_imported INTEGER NOT NULL DEFAULT 0,
  imported_seconds INTEGER NOT NULL DEFAULT 0,
  transcribed_seconds INTEGER NOT NULL DEFAULT 0,
  transcribe_wall_seconds INTEGER NOT NULL DEFAULT 0,
  engine_breeze INTEGER NOT NULL DEFAULT 0,
  engine_qwen INTEGER NOT NULL DEFAULT 0,
  engine_builtin INTEGER NOT NULL DEFAULT 0,   -- Mac：macOS 內建辨識；Windows：快速辨識（SenseVoice）
  engine_whisper_ja INTEGER NOT NULL DEFAULT 0,
  engine_server INTEGER NOT NULL DEFAULT 0,    -- 4.2（R52.2）：企業伺服器轉錄；已經建好的資料庫跑 migrate-enterprise.sql
  lang_zh INTEGER NOT NULL DEFAULT 0,
  lang_en INTEGER NOT NULL DEFAULT 0,
  lang_ja INTEGER NOT NULL DEFAULT 0,
  diarization_runs INTEGER NOT NULL DEFAULT 0,
  summarizer_claude_code INTEGER NOT NULL DEFAULT 0,
  summarizer_codex INTEGER NOT NULL DEFAULT 0,
  summarizer_api INTEGER NOT NULL DEFAULT 0,
  summarizer_local INTEGER NOT NULL DEFAULT 0,
  summarizer_enterprise INTEGER NOT NULL DEFAULT 0,   -- 4.2（R52.2）：整理工具是「公司」
  questions_asked INTEGER NOT NULL DEFAULT 0,
  review_answered INTEGER NOT NULL DEFAULT 0,   -- 2026-10-23 加（R41.7）：待確認送出的題數；已經建好的資料庫跑 migrate-review.sql
  review_confirmed INTEGER NOT NULL DEFAULT 0,
  review_skipped INTEGER NOT NULL DEFAULT 0,
  errors_recording INTEGER NOT NULL DEFAULT 0,
  errors_transcribe INTEGER NOT NULL DEFAULT 0,
  errors_diarize INTEGER NOT NULL DEFAULT 0,
  errors_summarize INTEGER NOT NULL DEFAULT 0,
  -- 2026-10-25 加（R47、docs/design.md §34.5）：整理失敗的種類、整理工具、草稿；已經建好的資料庫跑 migrate-summarize-errors.sql
  summarize_err_not_installed INTEGER NOT NULL DEFAULT 0,
  summarize_err_not_logged_in INTEGER NOT NULL DEFAULT 0,
  summarize_err_missing_key INTEGER NOT NULL DEFAULT 0,
  summarize_err_not_configured INTEGER NOT NULL DEFAULT 0,
  summarize_err_key_rejected INTEGER NOT NULL DEFAULT 0,
  summarize_err_limited INTEGER NOT NULL DEFAULT 0,
  summarize_err_timeout INTEGER NOT NULL DEFAULT 0,
  summarize_err_unreachable INTEGER NOT NULL DEFAULT 0,
  summarize_err_bad_output INTEGER NOT NULL DEFAULT 0,
  summarize_err_other INTEGER NOT NULL DEFAULT 0,
  summarize_err_claude_code INTEGER NOT NULL DEFAULT 0,
  summarize_err_codex INTEGER NOT NULL DEFAULT 0,
  summarize_err_api INTEGER NOT NULL DEFAULT 0,
  summarize_err_local INTEGER NOT NULL DEFAULT 0,
  summarize_err_enterprise INTEGER NOT NULL DEFAULT 0,   -- 4.2（R52.2）
  summarize_err_draft INTEGER NOT NULL DEFAULT 0,
  received_at TEXT NOT NULL,
  PRIMARY KEY (install_id, date)
);
CREATE INDEX IF NOT EXISTS usage_daily_date ON usage_daily (date);
CREATE INDEX IF NOT EXISTS usage_daily_platform_date ON usage_daily (platform, date);
