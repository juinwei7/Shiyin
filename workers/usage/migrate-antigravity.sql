-- 5.4：整理工具 Antigravity（R121.8、design §95）。整理工具是 Antigravity 的次數、用它整理失敗的次數。
-- 已經部署過的資料庫先跑這個，再部署新的 worker.js：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-antigravity.sql
ALTER TABLE usage_daily ADD COLUMN summarizer_antigravity INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_antigravity INTEGER NOT NULL DEFAULT 0;
