<img src="docs/images/icon.png" width="96" alt="拾音圖示">

**繁體中文** | [English](.github/README.md) | 官網：[shiyin.weibuilds.com](https://shiyin.weibuilds.com)

# 拾音 Shiyin

macOS 的會議紀錄 app。錄下會議（現場或線上），錄音中就能看到即時逐字稿；錄完在本機轉錄、分出說話者，再交給 Claude、Codex 或 API 整理成會議紀錄。紀錄裡每一條都附上錄音時間點，點一下就能聽到原話。

- 錄音、轉錄、說話者辨識都在本機完成，錄音不會上傳
- 不需要安裝 Python、Homebrew 或 ffmpeg，下載 DMG 就能用
- 介面有繁體中文和英文（跟著 macOS 的語言），會議可以是中文、英文或日文
- 需要 Apple Silicon 和 macOS 26

![會議紀錄](docs/images/summary.png)

## 怎麼用

1. **錄音或匯入**：按工具列的「錄音」（⌘R）。線上會議請把錄音來源選「麥克風＋電腦聲音」，對方的聲音也會錄進來。已經有錄音檔的話，拖進視窗就好。
2. **邊錄邊看、邊記**：左邊是即時逐字稿，右邊「我的筆記」每換一行會自動記下錄音時間；重要的地方按「標記重點」（⌘B，在其他 app 裡按 ⌃⌥⌘B）。
3. **停止並轉錄**：停止後馬上用即時逐字稿整理一份草稿，同時在本機正式轉錄並分出說話者。點「說話者 1」可以改成人名，拾音會記住聲音，下次自動帶入。
4. **看會議紀錄**：轉錄完自動整理成摘要、決議、待辦、未決問題，每一條都能點時間點跳回錄音。不確定的人名和專有名詞會變成待確認的題目：有疑問的那一條旁邊有「？」，點了就能當場回答，紀錄頂部的「還有 N 題待確認」可以一次看完；回答後 AI 會依答案修正（按「是」直接確認，不用等）。想知道誰說了什麼，可以在「提問」分頁直接問。
5. **匯出或分享**：會議紀錄存成 Markdown、PDF、Word，逐字稿存成字幕檔，或把整場會議打包成 .shiyin 傳給同事。

## 功能

- **錄音**
  - 來源可以選「只錄麥克風」「麥克風＋電腦聲音（線上會議）」「只錄電腦聲音」。Zoom、Meet、Teams 對方的聲音也錄得到
  - 錄音中顯示即時逐字稿、各來源的音量；可以暫停、標記重點、做筆記
  - 收音提醒：麥克風沒有收到聲音、聲音太小、背景太吵時，錄音畫面會提醒你；人在別的 app 時也會送通知、選單列圖示變成警示
  - 人多、吵雜時，錄音中或匯入時就可以選「大約幾個人」，說話者分得比較準
  - 關掉主視窗後錄音照常進行，選單列可以暫停、停止；在其他 app 裡按 ⌃⌥⌘R 也能開始或停止錄音
  - 錄音中接上或拔掉耳機、換了輸出裝置，會自動接上繼續錄
  - 偵測到 Zoom、Teams、Google Meet 等線上會議開始用麥克風時，提醒你開始錄音；會議好像結束時提醒停止。只提醒，不會自動錄
  - 錄音中在任何 app 按 ⌃⌥⌘B 標記重點，有音效和選單列圖示的回饋
  - 磁碟空間不夠時會提醒；寫不進磁碟時自動停止，已經錄到的部分會保留、照常轉錄
  - app 當掉或電腦突然關機，下次開啟會自動復原錄音
- **匯入**：音訊和影片（m4a、mp3、wav、aac、flac、caf、aiff、mp4、mov）、逐字稿（srt、vtt、txt）、Markdown 筆記、整場會議封包（.shiyin）。可以拖進視窗、⌘O、拖到 Dock 圖示，或在 Finder 用「打開方式」
- **本機轉錄**：whisper.cpp + Breeze ASR 25（聯發科），詞彙表會當成提示詞；設定裡可以改選 llama.cpp + Qwen3-ASR 1.7B（阿里巴巴開源，多人會議漏字少，輸出轉成台灣繁體，全程本機）；日文會議用 Whisper large-v3-turbo（第一次轉錄日文會議時才下載）。也可以改用 macOS 內建辨識（不用下載模型、速度快很多，但不參考詞彙表）。模型還沒下載好時，先用 macOS 內建辨識轉錄，下載好之後可以一鍵重新轉錄。長錄音分段轉錄，4 小時的會議也不會吃掉太多記憶體
- **會議語言**：每場會議可以選中文、英文或日文，轉錄、會議紀錄、匯出的標題都跟著這個語言；日文會議的內容用日文字形，PDF、Word 用日文字型。預設語言在設定裡改
- **自動偵測語言**：開始錄音後聽約 10 秒，判斷是中文、英文還是日文；跟會議語言不同而且有把握時，自動切換即時逐字稿和會議語言，錄音畫面可以改回來
- **逐字稿校正**：轉錄完，整理工具依詞彙表和上下文找出可能聽錯的字（同音字、專有名詞），標在逐字稿裡，你按「接受」才改，可以還原
- **詞彙表建議**：從參加者、說話者名字、校正過的字、會議標題建議要加進詞彙表的詞
- **說話者辨識**：逐字稿每段標上說話者，每個人一個顏色；可以改名、只看某一位說話者
  - 聲紋：替說話者取名後，拾音記住這個人的聲音，之後的會議自動帶入名字（標「自動」，認錯可以改正）。聲紋只存在這台 Mac，設定裡可以查看、刪除、關閉
  - AI 從對話內容猜說話者的名字（例如「謝謝小陳的報告」），列成建議，按「套用」才改；改名時會議紀錄裡的名字一起換
  - 人多、吵雜或手機遠距收音，分出來的人太少時：「更多 → 重新辨識說話者…」選大約幾個人，只重新分辨誰在說話，逐字稿的文字不變
  - 分錯了很好改：同一個人被分成兩位，在說話者名字上點一下選「合併到…」；某幾句分錯人，在逐字稿按右鍵選「這句是誰說的」或「這段連續的話是誰說的」。改過的可以從「更多 → 復原說話者的調整」復原，重新辨識說話者時也會保留
  - 拾音覺得可能把不同的人當成同一位時（人多、吵雜），逐字稿上方會提醒你重新辨識說話者
- **會議紀錄**
  - 範本：一般會議、一對一、訪談、課堂／演講、腦力激盪，也可以自訂
  - 整理工具：Claude Code、Codex CLI、Anthropic API、OpenAI API、Gemini API、OpenAI 相容 API（Groq、OpenRouter、本機的 Ollama、LM Studio），或「不整理」只要逐字稿
  - 停止錄音後先用即時逐字稿整理一份草稿（標「草稿」），正式轉錄完成後自動換成正式版
  - 可以直接編輯；每次修改都留版本，可以復原
- **匯出與分享**

  | 項目 | 格式 |
  |---|---|
  | 會議紀錄 | Markdown、PDF、Word；也可以複製成保留格式的文字（貼到 Word、Notion、郵件）、Markdown、純文字 |
  | 逐字稿 | txt、Markdown、srt、vtt、Word（可選要不要時間點和說話者） |
  | 錄音 | m4a、wav |
  | 整場會議 | .shiyin 封包（錄音、逐字稿、說話者、紀錄、筆記、重點），對方雙擊就能匯入 |

  匯出時也可以直接用 AirDrop、Mail、訊息分享。
- **對會議提問**：在會議的「提問」分頁用自然語言問（例如「預算最後決定多少？」），回答附錄音時間點，點了跳過去。工具列的「提問」可以問所有會議、目前的資料夾或最近 30 天，回答附上是哪一場會議
- **管理**：資料夾（依專案或客戶分類）、全文搜尋所有會議、垃圾桶（30 天內可還原）
- **其他**：倍速播放（0.75×～2×）、用詞修正（逐字稿和紀錄一起改）、轉錄完自動把錄音壓縮成 m4a、完成或失敗時發通知、app 內更新、「輔助說明 → 回報問題…」（附上版本和環境，不含會議內容）

![逐字稿與說話者](docs/images/transcript.png)

<sub>逐字稿截圖的示範錄音取自 [AMI Meeting Corpus](https://groups.inf.ed.ac.uk/ami/corpus/)（ES2004a，CC BY 4.0）。</sub>

![錄音中](docs/images/recording.png)

## 系統需求

| 項目 | 需求 |
|---|---|
| 晶片 | Apple Silicon（M1 以上） |
| 系統 | macOS 26 以上 |
| 記憶體 | 8 GB 以上。轉錄加說話者辨識的峰值約 2.9 GB，長錄音分段轉錄，會議長短影響不大（4 小時的會議 2.9 GB，M3 Pro 實測） |
| 磁碟空間 | 模型約 1.7 GB（第一次使用時下載；改用 macOS 內建辨識就不用），日文會議另外 574 MB、自動偵測語言 60 MB，加上錄音：m4a 約每小時 10～30 MB |
| 整理工具 | 擇一：已登入的 [Claude Code](https://claude.com/claude-code)（`claude`）、Codex CLI（`codex`）、Anthropic API key、OpenAI API key、Gemini API key，或 OpenAI 相容 API（Groq、OpenRouter 填 key；本機的 Ollama、LM Studio 不用 key）。也可以選「不整理」 |
| 網路 | 第一次下載模型、整理會議紀錄時需要；錄音、轉錄、說話者辨識都不需要 |

## 安裝

### 用 DMG 安裝

1. 取得 `Shiyin-<版本>.dmg`（[官網](https://shiyin.weibuilds.com)、這個 repo 的 Releases，或向維護的人拿），打開後把「拾音」拖到旁邊的 Applications（應用程式）。
2. 拾音沒有經過 Apple 公證，第一次打開會被擋下：到「系統設定 → 隱私權與安全性」按「仍要打開」（只要做一次）。DMG 裡的「安裝說明.pdf」有完整步驟（英文版是「Install Guide.pdf」）。
3. 第一次打開會出現引導：允許麥克風、下載轉錄模型（約 1.7 GB）、選整理工具。每一步都可以略過，之後在設定裡補做。
4. 3.1.1 起，拾音會自動檢查這個 repo 的 Releases 有沒有新版，詢問你之後自動更新。

### 從原始碼建置

需要 Xcode 26（Swift 6.2 以上）。第一次建置會用 Swift Package Manager 下載 GRDB、FluidAudio 和 whisper.cpp 的 XCFramework。

```bash
git clone https://github.com/juinwei7/Shiyin.git
cd Shiyin
scripts/make-signing-cert.sh --trust   # 只要做一次：建立並信任自簽憑證（要輸入登入密碼），之後更新 app 時權限不用重新允許
scripts/build-app.sh --install         # 建置 release 版並安裝到 ~/Applications
```

沒有執行 `make-signing-cert.sh --trust` 也能建置，會改用 ad-hoc 簽章，只是每次重新建置後系統可能會再問一次權限。

## Windows 版

Windows 版在 [`windows/`](windows/)（C#、.NET 10、WinUI 3），功能跟 Mac 4.1 相同。到[官網](https://shiyin.weibuilds.com/windows/)或這個 repo 的 Releases 下載（tag 是 `win-vX.Y.Z`）。

| 項目 | 需求 |
|---|---|
| 系統 | Windows 11（x64 或 ARM64）。N 版要先裝「媒體功能套件」（Media Feature Pack） |
| 記憶體 | 8 GB 以上 |
| 磁碟空間 | 第一次使用時下載：快速辨識約 240 MB、說話者辨識約 45 MB、轉錄模型約 1.7 GB；日文會議另外 574 MB；改選 Qwen3-ASR 另外約 2.5 GB（含 llama.cpp 的執行檔） |
| 顯示卡 | 不是必要的。x64 的電腦有 NVIDIA 顯示卡時，拾音會自動下載顯示卡加速（約 685 MB），正式轉錄改用顯示卡（40 分鐘的會議用 RTX 3060 約 8 分鐘，只用 CPU 約 30 分鐘） |
| 效能 | NVIDIA 顯示卡最快：顯示記憶體建議 4 GB 以上、驅動要 527 版以後。只用 CPU 時比較慢，筆電可能比會議本身還久；轉錄最多用 8 個執行緒。等不及的話，「設定 → 錄音與轉錄 → 正式轉錄」改成「快速辨識」（快很多但比較不準）。錄音中的即時逐字稿約用一個 CPU 核心的三分之一 |
| 整理工具 | 同 Mac |

跟 Mac 版不一樣的地方：

- **安裝**：執行 `Shiyin-<版本>-win-x64-Setup.exe`（ARM 的電腦用 `win-arm64` 那個），不用系統管理員權限，裝在 `%LOCALAPPDATA%\ShiyinApp`。安裝檔沒有程式碼簽章：出現「Windows 已保護您的電腦」時按「其他資訊」→「仍要執行」；開著「智慧型應用程式控制」時會被直接擋下，要先到「Windows 安全性 → 應用程式與瀏覽器控制」關掉它。之後有新版時，拾音會在 app 裡問你要不要更新。
- **系統匣**取代選單列：關掉主視窗時拾音留在系統匣，錄音和轉錄不會中斷；Windows 11 預設把新圖示收在工作列的「^」裡。
- **快捷鍵**：⌘ 換成 Ctrl；在其他 app 裡用 Ctrl+Alt+Shift+R 開始或停止錄音、Ctrl+Alt+Shift+B 標記重點。
- **辨識**：即時逐字稿和「快速辨識」（取代 macOS 內建辨識，轉錄模型還沒下載好時先用）是 SenseVoice；說話者辨識是 sherpa-onnx，聲紋跟 Mac 版不通用。.shiyin 封包兩邊可以互相匯入。
- **資料與隱私**：資料在 `%LOCALAPPDATA%\Shiyin`（只有這個 Windows 使用者讀得到），設定是裡面的 `settings.json`；API key 存在 Windows 的「認證管理員」，不寫進檔案或 log。解除安裝不會刪掉錄音和資料庫。其他同上面的〈資料與隱私〉。
- **授權**：另外用了 sherpa-onnx、ONNX Runtime、SenseVoice、pyannote、3D-Speaker、NAudio、Velopack、WinUIEx、Windows App SDK 等，完整的清單和條文在「關於拾音」和安裝資料夾的 `Third-Party Licenses.txt`（`scripts/windows/make-third-party-licenses.py` 產生，更新套件後要重跑）。

從原始碼建置要在 Windows 上，需要 .NET 10 SDK、Visual Studio Build Tools（C++）、CMake：

```powershell
pwsh scripts/windows/build-whisper.ps1 -Arch x64 -Out build\whisper-x64
pwsh scripts/windows/pack.ps1 -Version 4.0.0 -Arch x64 -Whisper build\whisper-x64   # 安裝檔在 dist\win-x64
```

不碰畫面的程式（資料庫、提示詞、整理工具、轉錄流程）在 Mac 上也能測：`cd windows && dotnet test --project tests/Shiyin.Tests`。

## 發布新版

### 用 GitHub Actions（推薦）

1. 改 `Resources/Info.plist` 的 `CFBundleShortVersionString`，commit 並推上去。
2. 打一個帶說明的 tag 並推上去，說明文字就是這版的更新內容：

   ```bash
   git tag -a v3.1.1 -m "這版改了什麼"
   git push origin v3.1.1
   ```

3. [`.github/workflows/release.yml`](.github/workflows/release.yml) 會在 `macos-26` runner 上建置 DMG，建立 GitHub Release 並上傳 DMG 和 `shiyin-update.json`。tag 跟版本號不一致時會直接失敗。

也可以在 Actions 頁面手動執行「打包」，只建置、不發布，DMG 放在那次執行的 artifact。

設定（Settings → Secrets and variables → Actions，都是選填）：

| 名稱 | 種類 | 用途 |
|---|---|---|
| `SHIYIN_SIGNING_P12`、`SHIYIN_SIGNING_P12_PASSWORD` | Secret | 自簽憑證。用 `scripts/export-signing-cert.sh --to-github <owner/repo>` 從自己的電腦設定，CI 簽出來的 app 才會跟你電腦上建的一致；沒有設定就用 ad-hoc 簽章 |
| `SHIYIN_UPDATE_URL` | Variable | 寫進 app 的更新網址 |
| `SHIYIN_USAGE_URL` | Variable | 寫進 app 的匿名使用統計網址（選填；沒設就不送統計） |
| `SHIYIN_RELEASES_REPO`、`SHIYIN_RELEASES_TOKEN` | Variable、Secret | 另外發布到一個公開的 repo（程式碼維持私人、又想讓 app 自動更新時用） |

### 在自己的電腦上發布

1. 改 `Resources/Info.plist` 的 `CFBundleShortVersionString`。
2. 產生 DMG，帶入更新網址和這版的更新內容：

   ```bash
   SHIYIN_UPDATE_URL=https://example.com/shiyin/shiyin-update.json \
   SHIYIN_RELEASE_NOTES="這版改了什麼" scripts/make-dmg.sh
   ```

   `dist/` 裡會有 `Shiyin-<版本>.dmg` 和 `shiyin-update.json`（版本、DMG 檔名、SHA-256、更新內容）。
3. 把這兩個檔案放到更新網址的位置（一定要 https）。已經安裝的拾音會在 24 小時內檢查到新版，詢問使用者後自動下載、驗證並重新開啟。

用 GitHub Releases 發布的話，更新網址可以用 `https://github.com/juinwei7/Shiyin/releases/latest/download/shiyin-update.json`，每次發布時把兩個檔案都上傳：

```bash
gh release create v3.1.1 dist/Shiyin-3.1.1.dmg dist/shiyin-update.json --notes "這版改了什麼"
```

注意：私人 repo 的 Releases 檔案要登入才能下載，app 內更新會失敗；這種情況請把兩個檔案放在公開的 https 位置（公司內部網站、NAS 等）。

app 會檢查新版的簽章是否跟目前的 app 相符。執行過 `make-signing-cert.sh --trust` 之後，只有同一張憑證簽的版本才能透過更新安裝。

## 資料與隱私

| 項目 | 位置與去向 |
|---|---|
| 錄音、逐字稿、資料庫、模型 | `~/Library/Application Support/Shiyin`（權限 700，不會被 iCloud 同步） |
| 錄音、轉錄、說話者辨識 | 完全在本機，不連網 |
| 整理、校稿、提問、猜說話者 | 逐字稿文字、你的筆記、詞彙表、會議基本資料會送到你選的服務（Anthropic、OpenAI、Google，或 OpenAI 相容 API 填的網址）。問所有會議時只送找到的相關片段。錄音檔不會送出 |
| 聲紋 | 說話者的聲音特徵（一組數字，不是錄音）只存在本機資料庫，不上傳、不放進匯出的 .shiyin 封包；設定 → 說話者可以刪除或關閉 |
| 回報問題 | 只在你按「在 GitHub 回報」時用瀏覽器打開 GitHub，內容你看過、自己送出：版本、環境、錯誤的種類，不含會議內容、檔名或 API key |
| 匿名使用統計 | 每天送一份給拾音的開發者：版本、系統、錄音和轉錄的分鐘數、用了哪些功能、失敗次數，加上一個隨機的匿名 ID。不含會議標題、逐字稿、會議紀錄、人名、聲紋或任何檔案。設定 → 進階可以關掉 |
| API key | 存在 macOS 鑰匙圈，不寫進檔案或 log |
| 整理工具選「不整理」，並關掉匿名使用統計 | 完全離線 |
| OpenAI 相容 API 填本機網址（Ollama、LM Studio） | 整理也在這台 Mac 上完成，內容不會離開電腦 |

## 設定（⌘,）

| 分頁 | 內容 |
|---|---|
| 整理工具 | Claude、Codex、Anthropic API、OpenAI API、Gemini API、OpenAI 相容 API 或不整理；登入狀態、測試整理工具；API key；OpenAI 相容 API 的網址（有常用服務可選）；模型；轉錄完是否自動整理、是否檢查可能聽錯的字；範本（內建和自訂、預設範本） |
| 錄音與轉錄 | 錄音來源、麥克風、權限設定；預設會議語言、自動偵測會議語言；正式轉錄引擎（Breeze、Qwen3-ASR 或 macOS 內建）、說話者辨識、即時逐字稿、下載／暫停／刪除模型；偵測線上會議和不提醒的 app、全域快捷鍵（開始停止錄音、標記重點） |
| 說話者與詞彙 | 記住說話者的聲音（聲紋）、認識的人：改名、刪除、全部刪除；詞彙表：人名、系統名、專有名詞的正確寫法，轉錄和整理都會參考；建議加入的詞 |
| 進階 | 更新網址與檢查更新、環境檢查、壓縮錄音、空間用量、重新顯示首次使用引導、重建搜尋索引、匿名使用統計、刪除第二版留下的舊檔案 |

## 開發

本專案採用 SDD（規格驅動開發）：先寫需求、設計、任務，再寫程式，每項任務都要實際跑過才算完成。

| 文件 | 內容 |
|---|---|
| [`docs/requirements.md`](docs/requirements.md) | 需求 |
| [`docs/design.md`](docs/design.md) | 架構、資料模型、提示詞、介面、決策紀錄 |
| [`docs/tasks.md`](docs/tasks.md) | 還沒完成的任務 |
| [`CLAUDE.md`](CLAUDE.md) | 開發與驗證時要注意的事 |

### 專案結構

```
Sources/Shiyin/
├── App/          app 進入點、選單、AppDelegate
├── Capture/      錄音（麥克風、系統聲音）、寫檔、即時逐字稿
├── Transcribe/   解碼、whisper.cpp、macOS 內建辨識、說話者辨識
├── Notes/        整理工具（CLI、API）、提示詞、範本
├── Model/        資料表與會議紀錄的資料結構
├── Store/        資料庫（GRDB）、AppModel
├── IO/           匯出、.shiyin 封包、匯入逐字稿
├── Services/     工作佇列、模型下載、更新、鑰匙圈、全域快捷鍵、通知
├── Views/        SwiftUI 畫面
└── Debug/        DEBUG 版的控制通道（shiyinctl）
Resources/        Info.plist、翻譯表（Localizable.xcstrings）、圖示
scripts/          建置、DMG、自簽憑證、圖示、開發工具
docs/             規格與實驗紀錄
```

### 常用指令

```bash
scripts/build-app.sh               # release 建置，產出 build/Shiyin.app（實際位置在 ~/Library/Caches/ShiyinBuild）
scripts/build-app.sh --debug       # DEBUG 建置，含驗證用的控制通道
scripts/make-dmg.sh                # release 建置並產生 DMG 和 shiyin-update.json
swift scripts/make-icon.swift Resources/AppIcon.icns   # 重新產生圖示

# DEBUG 版的控制通道：不用操作畫面就能觸發功能、讀取狀態、截圖
swiftc scripts/dev/shiyinctl.swift -o ~/Library/Caches/ShiyinBuild/shiyinctl
~/Library/Caches/ShiyinBuild/shiyinctl state /tmp/state.json
~/Library/Caches/ShiyinBuild/shiyinctl snapshot /tmp/summary.png dark detail

scripts/dev/l10n.py check         # 改過介面文字後：抽出字串、列出缺英文翻譯和沒查表的中文
~/Library/Caches/ShiyinBuild/app/Shiyin.app/Contents/MacOS/Shiyin -AppleLanguages "(en)"   # 用英文介面開啟

/usr/bin/log stream --predicate 'subsystem == "com.weiwei.shiyin"'      # 看 log
sqlite3 ~/Library/Application\ Support/Shiyin/shiyin.sqlite             # 看資料
```

## 已知限制

- 沒有 Apple 開發者帳號，app 沒有公證，第一次打開要手動放行。
- 錄音中的即時逐字稿用的是系統內建的辨識，不吃詞彙表，英文專有名詞常常不準（例如 Redis 變成 readis）。錄完後的正式轉錄會用詞彙表修正。
- 轉錄的準確度：在公開的真人中文會議（遠距收音、6 人）上，字錯誤率 Breeze 約 29%、macOS 內建約 25%、可選的 Qwen3-ASR 約 18%，以漏掉搶話和語助詞為主；整理出來的會議紀錄仍然正確。台灣國語的朗讀句子（中英夾雜）兩個模型都約 2%。筆電或耳機收音的會議應該會好很多。
- 說話者辨識：很短、而且大部分時間只有一個人在講的錄音，話少的人可能被併進別人。逐字稿的一段裡如果換人說話，整段只會標一個說話者；Qwen3-ASR 的段落（最長 15 秒）比 Breeze 的粗，搶話多的會議標錯的比例高一些。
- Claude 整理通常要 30～70 秒，偶爾會到幾分鐘。
- 英文會議的轉錄：在公開的英文會議片段（AMI，4 人）上，字錯誤率 Breeze 約 36%、Qwen3-ASR 約 34%、macOS 內建約 37%（去掉語助詞後約 33%、31%、34%），大多是同時說話的漏字。
- 日文會議的轉錄：在日本國會公開的專家聽證（6 人）上，字錯誤率 Whisper large-v3-turbo 約 8～9%。正確答案用的國會會議錄刪掉了語助詞，實際的錯誤率會比這個低。
- 還沒有：中文、英文、日文以外的語言、日文介面、行事曆整合、iPhone 版。

## 使用的開源專案與模型

| 名稱 | 用途 | 授權 |
|---|---|---|
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | 正式轉錄（Breeze、日文）、語言偵測 | MIT |
| [Breeze ASR 25](https://huggingface.co/MediaTek-Research/Breeze-ASR-25)（[GGML 轉檔](https://huggingface.co/shdennlin/breeze-asr-25-ggml)） | 轉錄模型（繁體中文、中英夾雜） | Apache-2.0 |
| [Whisper](https://github.com/openai/whisper) large-v3-turbo、base（[GGML 轉檔](https://huggingface.co/ggerganov/whisper.cpp)） | 日文轉錄、自動偵測語言 | MIT |
| [Qwen3-ASR 1.7B](https://huggingface.co/Qwen/Qwen3-ASR-1.7B)（[GGUF 轉檔](https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF)） | 可選的轉錄模型（中文、英文；輸出用 [OpenCC](https://github.com/BYVoid/OpenCC) 的字表轉成台灣繁體） | Apache-2.0 |
| [llama.cpp](https://github.com/ggml-org/llama.cpp) | 跑 Qwen3-ASR | MIT |
| [FluidAudio](https://github.com/FluidInference/FluidAudio) | 說話者辨識 | Apache-2.0 |
| [GRDB.swift](https://github.com/groue/GRDB.swift) | SQLite 資料庫、全文搜尋 | MIT |

轉錄模型和說話者辨識模型不放在這個 repo 裡，第一次使用時才下載。app 和 DMG 都附有上面這些套件的完整授權條款：「關於拾音」視窗、DMG 裡的 `Third-Party Licenses.txt`（`scripts/dev/make-third-party-licenses.sh` 產生，更新套件後要重跑）。

## 贊助

拾音是個人的開源專案，免費使用。覺得好用的話，可以在 Ko-fi 請我喝杯咖啡。

[![在 Ko-fi 請我喝杯咖啡](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/weiwei_chang)

## 授權

[MIT](LICENSE)。使用的開源專案和模型各自依上表的授權條款。
