-- 4.2：企業伺服器（R52.2、design §36）。用企業伺服器轉錄的秒數、整理工具是「公司」的次數、用公司整理失敗的次數。
-- 已經部署過的資料庫跑一次：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-enterprise.sql
ALTER TABLE usage_daily ADD COLUMN engine_server INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarizer_enterprise INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN summarize_err_enterprise INTEGER NOT NULL DEFAULT 0;
