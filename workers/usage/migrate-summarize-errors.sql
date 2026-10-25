-- 2026-10-25：整理失敗的種類、當時的整理工具、其中幾次是草稿（R47、docs/design.md §34.5）。已經建好的資料庫跑一次：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-summarize-errors.sql
-- 新建的資料庫用 schema.sql 就有這些欄位，不用跑這個。
ALTER TABLE usage_daily ADD COLUMN summarize_err_not_installed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_not_logged_in INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_missing_key INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_not_configured INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_key_rejected INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_limited INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_timeout INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_unreachable INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_bad_output INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_other INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_claude_code INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_codex INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_api INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_local INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_draft INTEGER NOT NULL DEFAULT 0;
