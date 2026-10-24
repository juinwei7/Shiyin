-- 4.1：Qwen3-ASR 引擎的次數（design §32、R43）。已經部署過的資料庫跑一次：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-qwen.sql
ALTER TABLE usage_daily ADD COLUMN engine_qwen INTEGER NOT NULL DEFAULT 0;
