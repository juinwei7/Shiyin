# 拾音官網：任務（2026-10-17）

對應 `requirements.md`、`design.md`。每一項做完都要截圖或實際操作確認，結果記在這裡。網站在分支 `website` 上做（從 main 開），跟 3.3 的分支分開。

## 階段 S：網站

- [~] **S0 設定（使用者）**
  - 已定：首頁標題「Mac 上的會議紀錄 app」、網址先用 `shiyin.pages.dev`、視覺改第二版（design §3、§5）
  - 待使用者：Cloudflare API token、Account ID 加到 GitHub Secrets（design §8）
- [x] **S1 建立專案**（design §6）
  - `site/build.mjs`（不用框架、沒有相依套件）、`site/src/` 的文字、版本說明、頁面、樣式，`site/serve.mjs` 本機預覽
  - 驗證：Node 26 和 Node 20（GitHub ubuntu 機器上的版本）都能建置，產生的圖檔一樣
- [x] **S2 首頁**（design §4.3）
  - 第一屏（圖示、標題、說明、下載按鈕）、三張真截圖、錄音／轉錄／整理／隱私／下載／最近更新／常見問題、頁尾
  - 截圖用 AVIF（`site/make-images.sh`），PNG 當備援；截圖換過沒重新產生時建置失敗
  - 驗證：1440 寬淺色、深色，390 寬中英文都截圖看過；沒有橫向捲動。修了兩個問題：英文說明在手機上兩句黏在一起（換行隱藏後少了空格）、深色模式截圖邊緣看不出來（加一圈淡邊）
- [x] **S3 版本說明頁與下載轉址**（design §4.4、§7）
  - 驗證：讀真的 GitHub Releases，3.2.0、3.1.1、3.1.0 都正確顯示（3.2.0 沒有副標、3.1.0 有）；`/download` 轉到 Shiyin-3.2.0.dmg
  - 用假的 3.3.0（跟 API 同樣格式的 JSON）建置：首頁按鈕、下載區、最近更新、版本說明、`/download` 都換成 3.3.0
  - 格式錯誤會讓建置失敗並指出哪一版哪一行：少了中英文之間的空行、條目不是「- 」開頭、標題版本跟 tag 不一致、最新版沒有 DMG；預先發布的版本不列
- [x] **S4 英文版**
  - `/en/`、`/en/changelog/`、`/en/404.html`；中英文互相連結、`hreflang`
  - 驗證：英文首頁、版本說明、404 截圖看過，內容跟中文一致
- [x] **S4b 日文版與語言選單**（使用者 2026-10-17 要求：多語下拉選中文、英文、日文）
  - `/ja/`、`/ja/changelog/`、`/ja/404.html`、`/ja/download`；頁首語言選單（跟 app 的語言選單同樣子，目前的語言打勾，`<details>` 不用 script）；頁尾列另外兩種語言
  - 日文版：按鈕名稱照 app 的英文寫；下載區、常見問題寫清楚 app 沒有日文介面；版本說明用英文那一段（標 `lang="en"`）；字體用 Hiragino；分享預覽圖 `og-ja.png`
  - 驗證：選單打開時桌機淺色、深色、手機截圖看過，不會超出畫面；日文首頁、版本說明、404 截圖看過；`hreflang` 三種語言互相指向、sitemap 每頁三個；Lighthouse 日文首頁、日文版本說明都是 100。修了一個問題：常見問題的樣式寫在全域的 `details`，會套到語言選單，改成只套常見問題
- [x] **S5 分享預覽與 SEO**（design §6）
  - 分享預覽圖 `og.png`、`og-en.png`（1200×630，左邊圖示和標題，右邊會議紀錄截圖）、Open Graph、canonical、sitemap、robots、404 頁（noindex）
  - 驗證：Lighthouse（本機）首頁中英文手機版、版本說明手機版、首頁桌機版：效能、無障礙、最佳做法、SEO 都是 100
  - 待上線後：用 Facebook、Threads 的連結預覽看標題、說明和圖
- [~] **S6 自動部署**（design §8）
  - `.github/workflows/site.yml`：推到 main 改到網站或截圖、「打包」流程推 tag 跑完、手動；沒有 Cloudflare secrets 時只建置不部署
  - CLAUDE.md 加上版本說明的格式規定和官網指令
  - 驗證：本機建置、YAML 解析通過。**還沒在 GitHub 上跑過**，要等合併到 main、設定好 secrets 之後：手動執行一次、推一個只改網站的 commit，確認 Cloudflare 自動部署
- [~] **S7 上線**
  - 3.3.0 發布之後再合併到 main（首頁寫了日文會議，3.2.0 還沒有）
  - README 中英文加上官網連結；GitHub repo 的 About 加網址
  - 驗證：正式網址的首頁、版本說明、下載都正常；發布新版後版本說明自動更新
  - 結果（2026-10-17）：3.3.0 發布後合併到 main。3.3.0 的版本說明先用 `--releases` 拿假的 JSON 試建置，中文、英文版本說明各 6 條都正確。合併時把中文、英文的「磁碟空間」補上日文會議的約 570 MB（日文版本來就有）
  - **待使用者**：（1）`shiyin.pages.dev` 已經被別人用了（打開是 nginx 的預設頁），Pages 專案名稱要換一個，或改用自己的網域，再設 `CLOUDFLARE_PAGES_PROJECT`、`SHIYIN_SITE_URL`；（2）GitHub Secrets 還沒有 `CLOUDFLARE_API_TOKEN`、`CLOUDFLARE_ACCOUNT_ID`，現在推到 main 只會建置、不會部署。網址定了、部署成功之後再加 README 連結和 repo 的 About

## 變更紀錄

- 2026-10-17：第一版樣稿（青瓷綠色塊、宋體大標、可以點的示範）使用者覺得太像 AI，改成第二版（白底、系統字體、真截圖），拿掉原本的「S2 可以點的示範」。
- 2026-10-17：不用 Astro，改成沒有相依套件的 Node 程式（design §6）：專案在 iCloud 同步的桌面，不放 `node_modules`。
- 2026-10-17：加上日文版和語言下拉選單（S4b）。日文版的版本說明直接用英文，不要求每次發布多寫日文。
- 2026-10-17：發布後重新部署改用 `workflow_run`（打包流程跑完）觸發，不用改 `release.yml`（design §8）。
