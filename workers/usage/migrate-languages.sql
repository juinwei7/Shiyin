-- 4.4：粵語、韓文會議的次數（design §41.6、R63.7）。已經部署過的資料庫先跑一次，再部署新的 worker.js
-- （先部署的話，寫入時找不到欄位會失敗；app 之後會重送，但只留最近幾天）：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-languages.sql
ALTER TABLE usage_daily ADD COLUMN lang_yue INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN lang_ko INTEGER NOT NULL DEFAULT 0;
