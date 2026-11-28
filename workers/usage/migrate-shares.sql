-- 分享會議（R125.10、design §99.4）：透過企業伺服器分享、從「與我分享」匯入、停止分享、從連結匯入的次數。
-- 已經部署過的資料庫先跑這個，再部署新的 worker.js：
--   npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-shares.sql
ALTER TABLE usage_daily ADD COLUMN shares_shared INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN shares_imported INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN shares_stopped INTEGER NOT NULL DEFAULT 0;
ALTER TABLE usage_daily ADD COLUMN shares_link_imported INTEGER NOT NULL DEFAULT 0;
