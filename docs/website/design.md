# 拾音官網：設計（2026-10-17，第二版方向已確認）

對應 `docs/website/requirements.md`。依 frontend-design skill 的流程：先列方案，對照「AI 產生的頁面常見的樣子」檢查，修掉落入通用模板的部分，再定版。第一版做出樣稿後，使用者覺得「底色是綠色，很 AI」，改成現在的第二版（§3）。

## 1. 題材從哪裡來

- **圖示**：青瓷綠的方塊，白色聲波，中間一條琥珀色的播放線
- **青瓷綠 #2D6A5D**：app 裡的時間點、按鈕、選取
- **時間點**：每一條紀錄後面都有 `00:14` 這樣的時間點，點了就從那裡播

產品最核心、別人沒有的是：**錄音不出電腦，每一條紀錄都點得回原話，AI 沒把握的地方會問你。**

## 2. 第一版（已放棄）

青瓷綠整片底色的第一屏、宋體大標題、仿造 app 視窗的可點示範、其他區塊用帶綠的灰白底。

使用者看過樣稿的意見：底色是綠色，很 AI。問題出在「大號襯線標題＋整片色塊＋假的 app 視窗」這個組合，正是產生式頁面最常見的樣子，跟用了哪個顏色無關。

## 3. 第二版：像開發者自己寫的 Mac app 產品頁

| 第一版 | 第二版 |
|---|---|
| 青瓷綠整片底色 | 白底（深色模式深灰），青瓷綠**只**用在下載按鈕和連結 |
| 宋體大標題 | 只用系統字體（Mac 上是蘋方、SF），標題粗體 |
| 仿造的 app 視窗、可以點的示範 | **真的截圖**（README 用的那三張），不做假介面 |
| 每個特色一種呈現方式 | 一欄由上往下讀的說明文，截圖比文字寬一點，跟著說明出現 |

仍然避免：大寫英文小標籤、`A · B · C` 中黑點串接、按鈕後面加「→」、等寬字體排小標籤、接近黑的 #111、捲動時淡入或懸停浮起這類動畫。數字編號只用在真的有順序的「第一次打開」步驟。

網站**沒有 JavaScript**：語言切換是連結，深色模式跟著系統，常見問題用 `<details>`。

## 4. 定版

### 4.1 色彩（接近 macOS 系統色，看起來像 Mac app 的說明頁）

| 名稱 | 淺色 | 深色 | 用在哪裡 |
|---|---|---|---|
| 背景 | #FFFFFF | #1C1C1E | 頁面 |
| 文字 | #1D1D1F | #F2F2F7 | 內文、標題 |
| 次要文字 | #6E6E73 | #98989F | 說明、日期、頁尾 |
| 線 | #E5E5EA | #38383A | 分隔線 |
| 淺底 | #F5F5F7 | #2C2C2E | 首頁「最近更新」的區塊 |
| 青瓷 | #2D6A5D | #6DB2A1 | 下載按鈕、連結、焦點框 |

深色模式的下載按鈕用深色字（#10201C），對比才夠。

### 4.2 字體

- 只用系統字體，不載入網路字型：`-apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang TC", "Noto Sans TC", "Microsoft JhengHei", sans-serif`
- 內文 17px、行高 1.75；標題 40（手機 30）／26／17px
- 文字欄最寬 680px，截圖最寬 960px
- 第一屏置中（圖示、標題、一句說明、下載按鈕），其他內容靠左

### 4.3 首頁版面

```
┌──────────────────────────────────────────────────────┐
│ [圖示] 拾音              功能  隱私  版本說明  English │
│                                                      │
│                       [圖示]                          │
│                Mac 上的會議紀錄 app                    │
│   錄音、轉錄、整理成會議紀錄，都在你的 Mac 上完成。     │
│   每一條紀錄都附時間點，點一下就聽得到原話。            │
│                  ( 下載拾音 3.3.0 )                    │
│       免費、開源，需要 macOS 26 和 Apple Silicon        │
│                                                      │
│   ┌──────────── 會議紀錄截圖 ────────────┐             │
│                                                      │
│   開會時，邊錄邊看逐字稿（兩段說明）＋錄音中截圖          │
│   停止後，在 Mac 上正式轉錄（兩段說明）＋逐字稿截圖       │
│   整理成會議紀錄，不確定的地方會問你                     │
│   錄音不會離開你的 Mac（留在 Mac／送到整理工具 兩欄清單） │
│   下載（版本、系統、空間、費用；DMG 按鈕；第一次打開 1-2-3）│
│   最近更新（最新兩版，連到版本說明）                     │
│   常見問題（點開收合）                                  │
│ ──────────────────────────────────────────────────── │
│ 拾音 Shiyin，個人開源專案   GitHub  版本說明  English    │
└──────────────────────────────────────────────────────┘
```

第一屏的下載按鈕先捲到「下載」區（那裡有系統需求和第一次打開的步驟），「下載」區的按鈕才直接下載 DMG。

語言切換：頁首最右邊的下拉選單，跟 app 的語言選單同一個樣子（地球圖示、目前的語言、向下箭頭）。點開列出「中文」「English」「日本語」，目前的語言前面打勾，選了就到同一頁的那個語言。用 `<details>` 做，不需要 JavaScript；點選單外面不會自動收起，再點一次按鈕收起。頁尾也列出另外兩種語言。

手機：導覽列只留「版本說明」和語言選單，「留在 Mac／送到整理工具」改成上下排，其他不變。

### 4.4 版本說明頁

一欄，最新的在最上面。每一版：「拾音 3.2.0」、發布日期、（最新版）下載連結、GitHub Release 連結；有副標就放副標，再列每一條。每一版有自己的錨點（`#v3.2.0`），首頁和 app 都可以直接連過來。

## 5. 首頁標題（已定）

**Mac 上的會議紀錄 app**，英文 **A meeting-notes app for your Mac**。

文案原則：講清楚做什麼，不堆形容詞；跟 app 用同樣的詞（「整理工具」「待確認」「逐字稿」「標記重點」）；不寫「革命性」「無縫」這種字。

## 6. 技術

- **不用框架，一支沒有相依套件的 Node 程式**（`site/build.mjs`，只用 Node 內建功能）產生靜態網頁到 `site/dist/`。
  - 原本打算用 Astro。改掉的原因：專案放在有 iCloud 同步的桌面，Astro 會帶進幾百 MB 的 `node_modules`；網站只有首頁和版本說明兩頁（各兩種語言），用不到框架；也不用追套件更新。
  - 本機需要 Node 20 以上；CI 用 GitHub 的 ubuntu 機器內建的 Node。
- 檔案：
  - `site/src/content.mjs`：中英文的所有文字
  - `site/src/releases.mjs`：讀取、檢查 GitHub Releases
  - `site/src/pages.mjs`：首頁、版本說明、404 的 HTML
  - `site/src/style.css`：建置時直接寫進每一頁（少一個請求）
  - `site/public/`：圖示、分享預覽圖、截圖的 AVIF，原樣複製
  - `site/serve.mjs`：本機預覽，會照 `_redirects` 轉址、找不到時回 404 頁
  - `site/make-images.sh`：把 `docs/images` 的截圖轉成 AVIF（macOS 的 `sips`），截圖更新時跑一次
- 截圖：只有一份來源，是 README 用的 `docs/images`（英文版用 `docs/images/en`）。建置時複製 PNG 當備援；網頁優先用 AVIF（1920 和 960 寬兩種，品質 60，大小約原本的四分之一，文字看不出差別）。缺 AVIF 時建置失敗，提醒先跑 `make-images.sh`。
- 網址：`/`、`/changelog/`、`/en/`、`/en/changelog/`、`/ja/`、`/ja/changelog/`、`/download`、404 頁（`/404.html`、`/en/404.html`、`/ja/404.html`）。
- 日文版：介面名稱照 app 的英文寫（「Record」「To confirm」），常見問題和下載區寫清楚 app 只有英文、繁體中文介面；截圖用英文版的；字體改用 Hiragino Sans（`:lang(ja)`），不然漢字會用蘋方的中文字形。
- SEO：每頁的標題和說明、canonical、`hreflang`（三種語言互相指向，x-default 指中文）、`sitemap.xml`、分享預覽（Open Graph、1200×630 的圖）。網站網址從環境變數 `SITE_URL` 讀，預設 `https://shiyin.pages.dev`。
- `_headers`：`nosniff`、Referrer-Policy、CSP（不允許任何 script）、圖片快取一天。

## 7. 版本說明自動更新

**只有一個來源：GitHub Release。** 發布時 tag 的說明文字會變成 Release 的內容，網站在建置時讀 GitHub Releases API 產生版本說明。

- 發布流程不用改，不會有兩份內容要對
- 只有真的發布出去的版本才會出現（草稿和預先發布的版本不列）

格式沿用現在的寫法（3.1.0～3.2.0 都是這樣），寫進 CLAUDE.md 當規定：

```
拾音 3.3.0：日文會議、自動偵測會議語言
- 第一條
- 第二條

Shiyin 3.3.0: Japanese meetings and automatic language detection
- First item
- Second item
```

中文一段、空一行、英文一段；第一行是標題（「：」或「: 」後面是副標，可以省略），版本號要跟 tag 一致，後面每行一條 `- `。格式不對時建置失敗並指出是哪一版哪一行，不會部署壞掉的頁面。

日文版的版本說明頁和首頁「最近更新」直接用英文那一段（標上 `lang="en"`），頁面上說明更新內容是英文，不要求每次發布多寫一段日文。

最新一版一定要有 `Shiyin-<版本>.dmg`。`/download` 在建置時產生轉址（Cloudflare Pages 的 `_redirects`），永遠指向最新版。網頁上的下載按鈕直接連到 DMG。

本機測試可以用 `--releases <檔案>` 讀一份跟 API 同樣格式的 JSON，不連網路。

## 8. 自動部署

`.github/workflows/site.yml`，用 GitHub Actions 加 Cloudflare 的 wrangler 部署到 Cloudflare Pages：

| 什麼時候 | 怎麼觸發 |
|---|---|
| 推到 main，改到 `site/`、`docs/images/` 或這個 workflow | `push` |
| 發布新版 | `workflow_run`：「打包」流程跑完、而且是推 tag 觸發的才部署（手動只建置 DMG 的不部署） |
| 手動 | Actions 頁面按「Run workflow」 |

- 不用改 `release.yml`。Release 是用 `GITHUB_TOKEN` 建立的，不會觸發 `release` 事件，所以改用「打包流程跑完」當觸發點。
- 不用 Cloudflare 後台的「連接 GitHub」：那種方式只有推 commit 才會建置，發布新版不會觸發。
- 讀 Releases API 時帶 `GITHUB_TOKEN`，避免沒登入的次數限制。
- Pages 專案不存在時自動建立（production branch 是 `main`）。
- 還沒設定 Cloudflare 的 secrets 時，只建置、跳過部署，不會讓每次推 main 都失敗。

**只有你能做的設定**（一次就好）：

1. Cloudflare 後台 → 右上角頭像 → My Profile → API Tokens → Create Token，範本選「Edit Cloudflare Workers」，或自訂權限 Account → Cloudflare Pages → Edit。複製 token。
2. Cloudflare 後台首頁右邊（或網址列）找到 Account ID。
3. GitHub repo → Settings → Secrets and variables → Actions：
   - Secrets 加上 `CLOUDFLARE_API_TOKEN`、`CLOUDFLARE_ACCOUNT_ID`
   - 第一次部署後看 Pages 給的網址；如果 `shiyin.pages.dev` 被用走（會變成 `shiyin-xxx.pages.dev`），或之後接自己的網域，在 Variables 加上 `SHIYIN_SITE_URL`（例如 `https://shiyin-xxx.pages.dev`），重新執行一次
4. 有自己的網域的話，在 Pages 專案的 Custom domains 加上

## 9. 品質

- 手機 390px、桌機 1440px、淺色深色都截圖檢查
- 鍵盤可以操作所有連結，看得到焦點框；顏色對比符合 WCAG AA
- 不載入追蹤程式、不用 cookie、沒有 script
- Lighthouse 效能、無障礙、SEO 都 95 分以上
