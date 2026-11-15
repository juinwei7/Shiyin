-- 2026-11-15：整理失敗的「其他」分成幾種（R86、docs/design.md §61.1）。已經建好的資料庫跑一次，再 deploy worker：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-summarize-other.sql
-- 新建的資料庫用 schema.sql 就有這一欄，不用跑這個。
ALTER TABLE usage_daily ADD COLUMN summarize_err_other_detail TEXT;
