-- 2026-10-23：待確認的題數（R41.7、docs/design.md §31.3）。已經建好的資料庫跑一次：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-review.sql
-- 新建的資料庫用 schema.sql 就有這三欄，不用跑這個。
ALTER TABLE usage_daily ADD COLUMN review_answered INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN review_confirmed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN review_skipped INTEGER NOT NULL DEFAULT 0;
