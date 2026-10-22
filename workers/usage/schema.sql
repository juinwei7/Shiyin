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
  engine_builtin INTEGER NOT NULL DEFAULT 0,   -- Mac：macOS 內建辨識；Windows：快速辨識（SenseVoice）
  engine_whisper_ja INTEGER NOT NULL DEFAULT 0,
  lang_zh INTEGER NOT NULL DEFAULT 0,
  lang_en INTEGER NOT NULL DEFAULT 0,
  lang_ja INTEGER NOT NULL DEFAULT 0,
  diarization_runs INTEGER NOT NULL DEFAULT 0,
  summarizer_claude_code INTEGER NOT NULL DEFAULT 0,
  summarizer_codex INTEGER NOT NULL DEFAULT 0,
  summarizer_api INTEGER NOT NULL DEFAULT 0,
  summarizer_local INTEGER NOT NULL DEFAULT 0,
  questions_asked INTEGER NOT NULL DEFAULT 0,
  errors_recording INTEGER NOT NULL DEFAULT 0,
  errors_transcribe INTEGER NOT NULL DEFAULT 0,
  errors_diarize INTEGER NOT NULL DEFAULT 0,
  errors_summarize INTEGER NOT NULL DEFAULT 0,
  received_at TEXT NOT NULL,
  PRIMARY KEY (install_id, date)
);
CREATE INDEX IF NOT EXISTS usage_daily_date ON usage_daily (date);
CREATE INDEX IF NOT EXISTS usage_daily_platform_date ON usage_daily (platform, date);
