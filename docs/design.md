# 拾音 Shiyin 3.0：設計

> 狀態：已確認（2026-10-15，第三版；使用者以「全部做好再找我」授權開工）
> 對應 `requirements.md` 的需求編號（R#、N#）。實作中如果偏離本文件，要先改本文件再改程式。
> 沿用第二版的部分會標「沿用 v2 §n」，第二版的設計用 `git show b621ed6:docs/archive/v2/design.md` 看。文中的任務編號（T-…）的驗證紀錄用 `git show b621ed6:docs/tasks.md` 看。

## 1. 架構

```
Shiyin.app（SwiftUI，單一程序，不依賴 Python、Homebrew、ffmpeg）
 ├─ Capture        AVAudioEngine（麥克風）＋ Core Audio process tap（系統聲音）
 │    ├─► RecordingWriter   16 kHz WAV，每個來源一個聲道，意外結束也能修復（沿用 v2 的 WAVRepair）
 │    └─► LiveTranscriber   SpeechAnalyzer zh_TW → 即時逐字稿（暫定＋定稿）
 ├─ JobRunner      兩條線（沿用 v2 §4）
 │    ├─ 轉錄線：解碼（AVFoundation）→ whisper.cpp + Breeze → 說話者辨識（FluidAudio）→ 壓縮成 m4a
 │    └─ 整理線：LLMProvider（Claude CLI／Codex CLI／Anthropic API／OpenAI API）
 ├─ ModelStore     下載、續傳、驗證模型
 ├─ AppDatabase    GRDB / SQLite（WAL）＋ FTS5 全文索引
 ├─ Exporter / Importer   錄音、逐字稿、會議紀錄、.shiyin 封包
 └─ Scenes         主視窗、錄音視窗、首次使用引導、使用說明、設定、MenuBarExtra
```

## 2. 技術選型

| 需求 | 選擇 | 依據 |
|---|---|---|
| 即時逐字稿（R2） | SpeechAnalyzer（macOS 26 內建）zh_TW | 實驗：暫定文字約 0 秒、定稿中位數 2.3 秒；系統內建不用下載；不佔 GPU |
| 正式轉錄（R4） | whisper.cpp（官方 XCFramework，binaryTarget）＋ Breeze ASR 25 GGML q8_0（1.66 GB） | 實驗：9.5 倍速、峰值記憶體 2.8 GB、詞彙提示下 CER 3.2%、關鍵詞 5/6 |
| 說話者辨識（R5） | FluidAudio（Swift，Apache-2.0，pyannote 模型在 ANE 上跑） | 需要實驗 T-A3 確認準確度 |
| 系統聲音（R1.2） | Core Audio process tap（`AudioHardwareCreateProcessTap`）＋ aggregate device | macOS 14.4 以上的正式 API；需要實驗 T-A2 |
| 解碼匯入的檔案 | AVFoundation（AVAudioFile、AVAssetReader） | 取代 ffmpeg |
| 整理（R6） | CLI（Process）＋ API（URLSession） | CLI 沿用 v2 §6.2；API 讓沒有 CLI 的人也能用 |
| 資料庫 | GRDB 7，沿用 v2 的 schema 並遷移 | 加上 FTS5 trigram 做中文全文搜尋 |
| 匯出 PDF、docx | 先產生 HTML，再用 WKWebView 轉 PDF；docx 用 `NSAttributedString`（officeOpenXML） | 都是系統內建，所以 docx 從 P1 提前到 P0 |
| 封包 | 資料夾加 `manifest.json`，用 `/usr/bin/ditto` 壓成 zip，副檔名 `.shiyin` | 系統內建工具 |
| 簽章 | 固定的自簽憑證（`scripts/make-signing-cert.sh` 只要建一次） | 沒有開發者帳號；用固定身分簽章，更新後 TCC 權限才會保留（R10.7） |
| 更新（P1） | Sparkle 2（EdDSA 簽章，不需要 Apple 帳號） | appcast 的網址等 Q1 決定 |

不採用的方案寫在 §14 決策紀錄。

## 3. 檔案配置

**原始碼**（`~/Desktop/Shiyin`）：

```
Sources/Shiyin/
├── App/          ShiyinApp、AppDelegate、Commands
├── Model/        Records（GRDB）、MeetingNotes（v2 格式）、Template、ExportFormat
├── Store/        AppDatabase（migration、FTS）、AppModel
├── Capture/      AudioCapture（引擎與 tap）、SystemAudioTap、RecordingWriter、WAVRepair、LiveTranscriber
├── Transcribe/   AudioDecoder、WhisperEngine（whisper.cpp 包裝）、Diarizer、TranscriptMerger
├── Notes/        LLMClient（LLMProvider、LLMBackend）、Backends/（Claude CLI、Codex CLI、Anthropic、OpenAI、Gemini、OpenAI 相容）、NotesPrompt、Templates
├── IO/           Exporters（audio、transcript、notes、package）、Importers（package、srt/vtt/txt、markdown）
├── Services/     JobRunner、ModelStore、Keychain、Paths、Notifier、Player、Search、Subprocess
├── Views/        Main、Recording、Detail（摘要／詳細／逐字稿／待確認／筆記）、Onboarding、Guide、Settings、MenuBar
└── Debug/        DebugControl（只在 DEBUG 建置）
Resources/        Info.plist、Assets.xcassets、AppIcon.icns、templates/*.json、export/notes.css
scripts/          build-app.sh、make-dmg.sh、make-signing-cert.sh、make-icon.swift、dev/shiyinctl.swift
```

第二版的 `Resources/python/` 整個移除。

**資料**（`~/Library/Application Support/Shiyin`，權限 700）：

```
shiyin.sqlite
audio/<會議id>.m4a｜.wav        正式的錄音
audio/recording/<uuid>.wav      錄音進行中的檔案
models/breeze-asr-25-q8_0.bin    正式轉錄模型
models/fluidaudio/               說話者辨識模型
backups/                         資料庫遷移前的備份
tmp/                             用完即刪（沿用 v2）
```

## 4. 資料模型（migration `v2`，從第二版的 `v1` 升級）

遷移前先把 `shiyin.sqlite` 複製到 `backups/shiyin-<日期>-v1.sqlite`（N3）。

```sql
ALTER TABLE meetings ADD COLUMN template_id  TEXT NOT NULL DEFAULT 'general';
ALTER TABLE meetings ADD COLUMN user_notes   TEXT NOT NULL DEFAULT '';   -- Markdown，每行可以有 [mm:ss] 時間點（R2.3）
ALTER TABLE meetings ADD COLUMN source       TEXT NOT NULL DEFAULT 'import';  -- recording / import / package / transcript
ALTER TABLE meetings ADD COLUMN channels     TEXT NOT NULL DEFAULT 'mic';     -- mic / system / both（錄音來源）
ALTER TABLE meetings ADD COLUMN deleted_at   TEXT;                         -- 垃圾桶（R9.3）
ALTER TABLE meetings ADD COLUMN folder       TEXT NOT NULL DEFAULT '';     -- P1

ALTER TABLE segments ADD COLUMN speaker INTEGER;                            -- 說話者編號，沒有辨識就是 NULL
ALTER TABLE segments ADD COLUMN kind TEXT NOT NULL DEFAULT 'final';         -- live（即時預覽）/ final

CREATE TABLE speakers (meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                       idx INTEGER NOT NULL, name TEXT NOT NULL, PRIMARY KEY (meeting_id, idx));
CREATE TABLE bookmarks (id INTEGER PRIMARY KEY AUTOINCREMENT,
                        meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                        time REAL NOT NULL, label TEXT NOT NULL DEFAULT '');
CREATE TABLE templates (id TEXT PRIMARY KEY, name TEXT NOT NULL, builtin INTEGER NOT NULL,
                        definition TEXT NOT NULL, updated_at TEXT NOT NULL);       -- definition 是 JSON（§8.2）
CREATE VIRTUAL TABLE search USING fts5(meeting_id UNINDEXED, kind UNINDEXED, ref UNINDEXED,
                                       text, tokenize = 'trigram');               -- R9.1
```

- `jobs.kind` 新增 `compress`（WAV 轉 m4a）。說話者辨識是轉錄工作裡的一個階段，不另外排工作。
- `meetings.notes` 改用 v2 格式（§8.1）。遷移時把舊格式轉換過去，舊的版本留在 `notes_versions`。
- 搜尋索引由 `AppDatabase` 在寫入時同步更新：標題、每段逐字稿、每條紀錄、筆記各一列。trigram 至少要 3 個字才能比對，所以 1～2 個字的查詢改用 `LIKE`。

## 5. 錄音（R1、R2）

### 5.1 收音

- 麥克風：`AVAudioEngine.inputNode`。選擇輸入裝置的方式，是在 inputNode 的 AudioUnit 上設定 `kAudioOutputUnitProperty_CurrentDevice`。
- 系統聲音：
  1. 用 `CATapDescription` 建立排除自己的全域 tap（`AudioHardwareCreateProcessTap`）
  2. 建立包含這個 tap 的 aggregate device
  3. 用 `AudioDeviceCreateIOProcIDWithBlock` 取得音訊
  4. 停止時一定要依序 destroy（沒有正確清理會讓 HAL 卡住）

  Info.plist 加上 `NSAudioCaptureUsageDescription`。這部分要先做實驗 T-A2。
- 兩個來源都轉成 16 kHz、單聲道 Float32，並且用同一個時鐘對齊時間。

### 5.2 寫檔

- 用 `AVAudioFile` 寫 16 kHz、16-bit 的 WAV，存在 `audio/recording/<uuid>.wav`。
  - 兩者都錄的時候是 2 個聲道：左聲道麥克風、右聲道系統聲音。之後可以利用這點判斷「誰是我」（P1）。
  - 一小時約 115 MB（單聲道）或 230 MB（雙聲道）。轉錄完成後壓縮成 m4a（§6.4）。
- 意外結束的處理沿用 v2 的 WAVRepair：下次啟動時修好檔頭，建立「復原的錄音」。
- 暫停時，兩個來源都停止寫入和送去即時轉錄，所以錄音檔裡沒有暫停那一段，時間軸是連續的。

### 5.3 即時逐字稿

- SpeechAnalyzer 的串流模式：`SpeechTranscriber(locale: zh_TW, reportingOptions: [.volatileResults], attributeOptions: [.audioTimeRange])`。輸入是兩個來源混音後的 buffer。
- 暫定結果以灰色顯示在最後一行，定稿後寫進 `segments`（`kind = 'live'`）。
- 第一次使用時，如果系統還沒有 zh_TW 的語音模型，用 `AssetInventory` 下載，併入首次使用引導（§11）。
- 正式轉錄完成後，刪掉 `kind = 'live'` 的段落。

### 5.4 錄音中的畫面

```
┌ 錄音中 ● 12:34   麥克風 ▮▮▮▮▯▯   系統聲音 ▮▮▯▯▯▯   [暫停] [重點 ⌘B] [停止] ┐
│ 即時逐字稿（可以捲動，往回看時不自動跳到最新）  │ 我的筆記                    │
│ [12:01] 第二，上線日期改到下個月…              │ [12:05] 上線日期要跟業務確認 │
│ [12:20] 這個改動阿華這週五前上線…              │ ▌                           │
│ 暫定：第三，Lumio 的定價方案…（灰色）          │                             │
└────────────────────────────────────────────────┴─────────────────────────────┘
```

- 筆記欄位按 Enter 換行時，新的一行會自動加上當下的錄音時間。
- ⌘B 標記重點，會加一筆 bookmark，並在逐字稿上顯示標記。

## 6. 正式轉錄（R4、R5）

### 6.1 解碼

用 `AVAudioFile`（影片檔用 `AVAssetReader`）讀成 16 kHz、單聲道 Float32。雙聲道的錄音先混成單聲道再交給轉錄。

### 6.2 whisper.cpp

- 在 Package.swift 加入官方的 XCFramework（binaryTarget，版本固定），只用 Metal，不另外載入 CoreML 編碼器。
- `whisper_full` 的參數：
  - language 設為 `zh`
  - `initial_prompt` 用 v2 §4.1 的提示詞加詞彙表，150 字以內
  - `progress_callback` 用來回報進度
  - `abort_callback` 用來取消
  - 其他使用預設值
- 段落的平均 token 機率太低，或壓縮比太高時，標成低信心（沿用 v2 的門檻概念，數值在 T-A4 校準）。
- 在程式內的背景 Task 執行，不另外開子程序：沒有 Python 之後，就沒有開子程序的必要。風險是 whisper.cpp 當掉會連 app 一起掛掉，但錄音另有 WAVRepair 保護、工作也會重跑，所以可以接受（D4）。

### 6.2.1 macOS 內建辨識（R4.1、D11）

- 設定 →「錄音」的「正式轉錄」可以選「Breeze ASR 25（建議）」或「macOS 內建」，存在 `transcriptionEngine`（`breeze` / `system`）。
- 選 macOS 內建時：解碼後的 16 kHz 單聲道寫成暫存 WAV，交給 `SpeechAnalyzer.start(inputAudioFile:finishAfterFile:)`（`SpeechTranscriber` zh_TW、`audioTimeRange`）。每個定稿結果就是一段逐字稿，時間取 `result.range`；進度用結果的結束時間除以總長度。系統還沒有 zh_TW 模型時先下載（跟即時逐字稿共用）。
- 不需要 Breeze 模型，所以這種轉錄工作不必等模型下載完。
- 說話者辨識、壓縮、整理都照舊。沒有詞彙提示，也沒有低信心標示。

### 6.3 說話者辨識

- 轉錄完成後，用 FluidAudio 的離線辨識（offline diarizer）處理同一份 16 kHz 音訊，得到「說話者、開始、結束」的時間區段。
- 每段逐字稿標上跟它重疊最多的說話者，並在 `speakers` 建立「說話者 1…n」。
- 有雙聲道時（P1）：如果某個說話者的聲音幾乎都在麥克風聲道，就預設命名為「我」。
- 實驗 T-A3 的準確度不過關時，依 R5.4 處理。

### 6.4 壓縮

轉錄完成後排一筆 `compress` 工作：用 `AVAssetExportSession`（AppleM4A）把 WAV 轉成 m4a，大約每小時 30 MB。成功後更新 `audio_file` 並刪除 WAV（R9.4，可以在設定關掉）。

## 7. 模型管理（R10.3）

- `ModelStore` 的模型清單（寫在程式裡）：

  | 模型 | 來源 | 大小 | 驗證方式 |
  |---|---|---|---|
  | Breeze ASR 25 q8_0 | HF `shdennlin/breeze-asr-25-ggml`，固定 commit | 1.66 GB | SHA-256 |
  | FluidAudio 說話者辨識模型 | FluidAudio 指定的 HF repo | T-A3 確認 | SHA-256 |

- 用 URLSession 下載：暫停時保存 resumeData，斷線後接續；完成後驗證 SHA-256，通過才搬到 `models/`。
- 設定裡的「模型與儲存空間」頁可以看到每個模型的狀態，也可以重新下載或刪除。
- 第二版留下的 `venv/` 和 `models/breeze-asr-25/`（mlx 格式）如果還在，就提示使用者可以刪除（R10.5）。

## 8. 會議紀錄（R6）

### 8.1 會議紀錄格式 v2

每一條紀錄都帶時間點（R6.4）：

```json
{
  "title": "…",
  "summary":      [{"text": "…", "time": "03:12"}],
  "decisions":    [{"text": "…", "time": "…"}],
  "actionItems":  [{"owner": "…", "task": "…", "due": "yyyy-MM-dd", "time": "…"}],
  "openQuestions":[{"text": "…", "time": "…"}],
  "topics":       [{"title": "…", "start": "…", "points": [{"text": "…", "time": "…"}]}],
  "custom":       [{"title": "…", "items": [{"text": "…", "time": "…"}]}],
  "reviewItems":  [{"time": "…", "question": "…"}]
}
```

- JSON schema 沿用 v2 §6.1 的嚴格模式寫法：所有欄位都 required，不知道的值用空字串。
- 讀取時相容 v1 格式：遇到字串陣列，就轉成 `{"text": 字串, "time": ""}`。
- 時間點一律是 `mm:ss`（超過一小時 `h:mm:ss`），不加括號。逐字稿在提示詞裡寫成 `[mm:ss]`，模型偶爾會連括號一起照抄，所以讀取時會去掉時間前後的括號和空白（`TimeFormat.clean`）。
- 範本決定要產生哪些段落，不需要的段落給空陣列。

### 8.2 範本

`templates.definition`：

```json
{"description": "一般會議",
 "sections": ["summary", "decisions", "actionItems", "openQuestions", "topics"],
 "custom": [{"title": "風險", "instruction": "列出討論中提到的風險與因應"}],
 "instruction": "整體的額外指示（選填）"}
```

內建範本放在 `Resources/templates/*.json`：
- 一般會議
- 一對一：近況、回饋、待辦
- 訪談：受訪者背景、重點引述、洞察
- 課堂／演講：大綱、重點概念、問答
- 腦力激盪：點子清單、評估、下一步

使用者可以複製內建範本來修改，也可以從頭新增。

### 8.3 提示詞

沿用 v2 §6.3 的規則，另外加上：
- 每一條紀錄都要填 `time`，填該內容在逐字稿裡出現的時間點
- 逐字稿每行改成 `[時間] 說話者名：內容`
- 附上使用者的筆記和重點標記，並說明「使用者筆記和重點標記代表使用者認為重要的內容，要優先納入」
- 加入範本的段落與指示

### 8.4 整理工具（LLMProvider）

| 工具 | 呼叫方式 | 結構化輸出 |
|---|---|---|
| Claude CLI | 沿用 v2 §6.2 | `--json-schema` |
| Codex CLI | 沿用 v2 §6.2 | `--output-schema` |
| Anthropic API | `POST /v1/messages`，`x-api-key` | 強制呼叫一個 tool（`tool_choice`），`input_schema` 就是會議紀錄的 schema |
| OpenAI API | `POST /v1/responses`，`Authorization: Bearer` | `text.format = {type: json_schema, strict: true}` |
| Gemini API（3.4） | `POST /v1beta/interactions`，`x-goog-api-key`，見 §19.2 | `response_format = {mime_type: application/json, schema}` |
| OpenAI 相容 API（3.4） | `POST <網址>/chat/completions`，見 §19.3 | `response_format = {type: json_schema, strict: true}`，不支援時退回 `json_object` |

- 3.4 起每種整理工具是一個 `LLMBackend` 實作，見 §19.1。
- API key 存在鑰匙圈：service 是 `com.weiwei.shiyin.apikey`，account 是 `anthropic`、`openai`、`gemini` 或 `openaiCompatible`。
- API 模型預設：Anthropic 用 `claude-sonnet-5-5`；OpenAI 的預設模型在實作時查證最新的模型名稱。兩者都可以在設定裡改。
- 逾時 20 分鐘；只重試一次，而且只在網路錯誤時重試。

### 8.5 手動編輯（R6.6）

- 「摘要」和「詳細」分頁新增「編輯」模式：每一條變成可以編輯的文字欄位，可以新增、刪除、調整順序，時間點也能改。
- 按「完成」才存檔，存成一個新版本（原因是「手動編輯」）。
- 待確認的題目也可以直接刪除。

## 9. 播放、逐字稿、搜尋（R8、R9）

- 沿用 v2 §9 的播放器、時間軸、逐字稿分頁。
- 倍速：`AVPlayer.rate`，`audioTimePitchAlgorithm = .timeDomain`，讓人聲在變速時不變調。
- 每段逐字稿前面顯示說話者的名字和顏色（從 8 色調色盤依編號取色）。點名字可以改名（R5.2）。可以篩選成只看某位說話者。
- 會議紀錄的每一條後面都有一個時間點按鈕，點了就跳播（R6.4）。
- 全文搜尋：側欄上方的搜尋框，結果依會議分組並顯示摘錄。點逐字稿的結果會打開逐字稿並跳到該段；點紀錄的結果會打開摘要並高亮那一條。
- 垃圾桶：側欄最下面加一個「垃圾桶」。啟動時清掉 `deleted_at` 超過 30 天的會議。
- 資料夾（R9.2）：
  - 資料就是 `meetings.folder` 欄位（migration v2 已經有），空字串表示不在任何資料夾。不另外建資料夾表：資料夾在至少有一場會議時才存在，最後一場移走就消失
  - 側欄會議列表上方有「資料夾」區塊：「所有會議」和每個資料夾（附場數），點了就只看那個資料夾（`AppModel.folderFilter`）。資料夾的右鍵選單可以改名、刪除（會議移回「不在資料夾」）
  - 會議的右鍵選單和「更多」選單有「移到資料夾」：既有的資料夾、「新資料夾…」、「移出資料夾」
  - 正在看某個資料夾時，新錄的會議和匯入的會議都放進那個資料夾
  - 會議標題下方的資訊列顯示資料夾名稱
  - .shiyin 封包的 `meeting.json` 帶 `folder`，匯入時還原

## 10. 匯入與匯出（R3、R7）

| 項目 | 實作 |
|---|---|
| 錄音 → m4a | `AVAssetExportSession`（AppleM4A） |
| 錄音 → wav | `AVAudioFile` 轉寫 |
| 逐字稿 → txt、md | 每段一行，可以選擇要不要加 `[時間] 說話者：` |
| 逐字稿 → srt、vtt | 依段落的開始和結束時間產生 |
| 逐字稿 → docx | 用 NSAttributedString 寫出 officeOpenXML |
| 紀錄 → md | 沿用 v2 §6.5，加上說話者和時間點 |
| 紀錄 → PDF | 產生 HTML（`Resources/export/notes.css`，標題用宋體）後，用 WKWebView 的 `pdf()` 輸出 |
| 紀錄 → docx | 用 NSAttributedString 寫出 officeOpenXML |
| 複製 | 同時放純文字、Markdown（`public.utf8-plain-text`）、HTML、RTF 到剪貼簿 |
| .shiyin 封包 | 資料夾內有 `manifest.json`（格式版本、app 版本）、`meeting.json`（會議、說話者、筆記、重點、會議紀錄）、`transcript.json`、`audio.m4a`，用 `ditto -c -k` 壓縮。Info.plist 宣告這個檔案類型（`UTExportedTypeDeclarations`），雙擊就會匯入 |
| 匯入 srt、vtt | 解析時間碼產生段落，`source = 'transcript'` |
| 匯入 txt | 沒有時間碼時，整份當成一段，時間點是 0，這種會議不做時間點跳播 |
| 匯入 md | 放進 `user_notes` |

匯出的入口是「匯出…」對話框：選擇項目（錄音、逐字稿、會議紀錄、整場封包）、格式和選項，再存檔或分享（`NSSharingServicePicker`）。

## 11. 首次使用引導（R10.2）

用獨立的 `onboarding` 視窗，第一次開啟時顯示，共五步：

1. **歡迎**：一句話說明 app 在做什麼，以及資料只存在本機
2. **權限**：麥克風、系統聲音、通知，每一項都有「允許」按鈕和用途說明；被拒絕時顯示怎麼到系統設定打開
3. **下載模型**：Breeze 1.66 GB 和說話者辨識模型，顯示進度，可以暫停；如果需要也一起下載 SpeechAnalyzer 的 zh_TW 模型
4. **整理工具**：自動偵測 claude、codex，沒有的話引導填 API key，也可以先選「不整理」
5. **完成**：打開使用說明

任何一步都可以跳過，之後在設定裡補做。

## 12. 發布（R10）

- `scripts/make-signing-cert.sh`：建立一張自簽的程式簽章憑證「Shiyin Self-Signed」，只要做一次。放在獨立的鑰匙圈 `~/Library/Application Support/ShiyinSigning/shiyin-signing.keychain-db`（密碼在同一個資料夾），有效到 2036-10-12。codesign 只肯用受信任的憑證，本機要用的話加 `--trust`（要輸入登入密碼）；沒有信任時 `build-app.sh` 用 ad-hoc。用這張憑證簽章時 designated requirement 是 `identifier "com.weiwei.shiyin" and certificate leaf = H"75f7e2a8…"`，固定不變，TCC 權限在更新後才會保留（R10.7）。
  - 使用者的 Mac 不信任這張憑證：Gatekeeper 照樣擋（第一次要按「仍要打開」），但簽章檢查（`codesign --verify`、app 內更新的 `SecStaticCodeCheckValidity`）不看信任，會通過。
  - 從 ad-hoc 換成這張憑證（2026-10-19 起 CI 用它簽）：ad-hoc 版的 designated requirement 只看 identifier，所以舊版可以用 app 內更新升到憑證版；升級之後就鎖定這張憑證，之後每一版都要用它簽，ad-hoc 或別的憑證簽的會被擋下。
  - 憑證到期：已經裝好的 app 照常打開、權限不受影響（使用者的 Mac 本來就不信任它），但到期後不能再用它簽新版，換新憑證會被舊版的 app 內更新擋下。到期前（2034 年左右）先出一版過渡版本，designated requirement 寫成 `identifier "com.weiwei.shiyin" and (certificate leaf = H"舊" or certificate leaf = H"新")`，大部分人升級後再改用新憑證。
- `scripts/make-dmg.sh`：用 hdiutil 建立 DMG，裡面有 Shiyin.app、Applications 資料夾的捷徑、`安裝說明.pdf` 和 `Install Guide.pdf`（3.1 起，§16.7）。
- 安裝說明的內容：
  - 拖進「應用程式」
  - 第一次打開時被擋的話，到「系統設定 → 隱私權與安全性」按「仍要打開」（macOS 15 之後已經不能按右鍵開啟）
  - 進階做法：`xattr -dr com.apple.quarantine /Applications/Shiyin.app`
- 更新（R10.6，D10）：
  - 更新資訊是一個 JSON 檔（`shiyin-update.json`）：`{"version": "3.0.1", "url": "Shiyin-3.0.1.dmg", "sha256": "…", "notes": "…"}`，`url` 可以是相對於這個 JSON 的路徑。`make-dmg.sh` 會跟 DMG 一起產生，發布時兩個檔案放在同一個位置
  - 更新網址：建置時用環境變數 `SHIYIN_UPDATE_URL` 寫進 Info.plist 的 `ShiyinUpdateURL`；設定 →「進階」可以改。只接受 https（`localhost`、`127.0.0.1` 例外，給測試用）
  - 檢查：有設定網址時，啟動 30 秒後檢查一次，之後每 24 小時一次；設定裡和「拾音」選單的「檢查更新…」可以手動檢查。版本號依數字逐段比較
  - 有新版時跳出對話框（版本、更新內容），使用者按「下載並更新」才開始
  - 安裝流程：錄音中或有工作在跑時拒絕 → 下載 DMG 到 `tmp/` → 驗證 SHA-256 → `hdiutil attach` 唯讀掛載 → 把 Shiyin.app 複製出來 → 卸載 → 用 Security framework 檢查新 app 的簽章有效，而且符合目前這個 app 的 designated requirement（自簽憑證時等於鎖定同一張憑證）→ 寫一個固定內容的小腳本，用參數傳入 PID 和路徑：等 app 結束後把舊 app 改名備份、放進新 app、成功就刪掉備份、失敗就還原，最後重新開啟 → app 結束
  - app 所在的資料夾不能寫入時（一般使用者帳號裝在 /Applications），改成打開下載好的 DMG，請使用者手動拖進去
  - 從網路下載的檔案不會被加上 quarantine（app 沒有開 `LSFileQuarantineEnabled`），所以替換後打開不會再被 Gatekeeper 擋
- CI 打包（GitHub Actions，`.github/workflows/release.yml`）：
  - 推 `v*` 的 tag：在 `macos-26`（Apple Silicon、Xcode 26）上跑 `make-dmg.sh`，產出 DMG 和 `shiyin-update.json`，建立 GitHub Release 並上傳；tag 跟 `Info.plist` 的版本號不一致時直接失敗。tag 的說明文字當成更新內容
  - 手動執行（workflow_dispatch）：只建置，DMG 放在 Actions 的 artifact
  - 簽章：repo secrets 有 `SHIYIN_SIGNING_P12`、`SHIYIN_SIGNING_P12_PASSWORD` 時，`scripts/ci/import-signing-cert.sh` 把憑證放成 `build-app.sh` 認得的位置（`~/Library/Application Support/ShiyinSigning`），並用 sudo 設成系統信任（runner 的 sudo 不用密碼），簽出來跟作者電腦上的一致；沒有就用 ad-hoc。codesign 只在鑰匙圈搜尋清單裡找簽章身分（加了 `--keychain` 也一樣），`build-app.sh` 簽章期間暫時把這個鑰匙圈加進清單，簽完或失敗都還原。secrets 的值網頁和 API 都讀不出來，只有推 tag 和手動執行會用到（不接受 PR 觸發），私鑰只在 runner 上，不會進 artifact 和快取。憑證用 `scripts/export-signing-cert.sh --to-github <repo>` 從作者的電腦匯出
  - 更新網址從 repo 變數 `SHIYIN_UPDATE_URL` 帶入；有設定 `SHIYIN_RELEASES_REPO`（加上 `SHIYIN_RELEASES_TOKEN`）時，也發布到另一個公開的 repo（程式碼維持私人時用）
- 全域快捷鍵（R11.3）：用 Carbon 的 `RegisterEventHotKey` 以獨佔模式（`kEventHotKeyExclusive`）註冊 ⌃⌥⌘R（不需要輔助使用權限）。不加獨佔的話，Carbon 允許不同的 app 註冊同一組按鍵，偵測不到衝突，按下時跟 ⌘R 一樣：沒在錄就開始，在錄就停止。設定 →「錄音」可以關掉；註冊失敗（例如被別的 app 佔用）時在設定裡顯示原因

### 12.1 第三方授權與更新網址（3.1.1，R13）

- `scripts/dev/make-third-party-licenses.sh` 組出 `Resources/ThirdPartyLicenses.txt`：whisper.cpp 取 `Package.swift` 固定版本的 LICENSE，GRDB、FluidAudio 取 `Package.resolved` 版本的 checkout 裡的 LICENSE，FluidAudio 另外附的 `ThirdPartyLicenses/` 全部照貼（它把 TTS、文字正規化的程式也編進同一個 module）。原文不改、不合併重複的 Apache 條文。產生的檔案 commit 進 repo，建置時不需要網路；更新套件版本後要重跑。
- `build-app.sh` 把它放進 `Contents/Resources/`，另外包成 `Credits.html`：系統標準的「關於拾音」視窗會自動顯示，可以捲動。
- `make-dmg.sh` 在 DMG 放一份 `Third-Party Licenses.txt`。
- 更新網址：GitHub 的 repository variable `SHIYIN_UPDATE_URL` 設成 `https://github.com/juinwei7/Shiyin/releases/latest/download/shiyin-update.json`，CI 建置時寫進 app。`releases/latest` 永遠指向最新的 Release，每次發布不用改網址。

## 13. 從第二版遷移（R10.5）

- 資料庫：先備份，再執行 migration `v2`（§4）。會議紀錄轉成 v2 格式；錄音、逐字稿、詞彙表照舊。
- 第二版已經轉錄好的會議不重新轉錄，也沒有說話者資訊。會議頁可以選「重新轉錄」，用新引擎重轉並辨識說話者。
- 偵測到第二版的 `venv/`、`models/breeze-asr-25/` 時，提示「可以刪除，釋放約 4.2 GB」，使用者確認後才刪。

## 16. 多語言（3.1，R12）

### 16.1 字串

- 用 String Catalog：`Resources/Localizable.xcstrings`，來源語言是繁體中文，**鍵就是程式裡的中文原文**，另外填英文翻譯，目前 594 條。好處是呼叫點不用改寫，程式讀起來跟原本一樣（D12）。
- SwiftUI 的字串字面值（`Text("…")`、`Button("…")`、`.help("…")`）本來就會查表。不是 SwiftUI 的地方改用 `String(localized:)`：錯誤訊息（`LocalizedError`）、提示（`noticeMessage`、`errorMessage`）、狀態文字、通知、`NSAlert`、選單列。
- 同一句中文在不同地方要翻成不同英文時（「範本」是 Template 也是設定分頁的 Templates；「錄音」是按鈕 Record、分頁 Recording、儲存空間的 Recordings），那個地方改用 `MeetingLanguage.ui(zh:en:)` 直接寫兩種語言，不查表。
- 工具 `scripts/dev/l10n.py`（D14）：
  - `extract`：用 Swift 編譯器的 `-emit-localized-strings` 抽字串（插值的型別正確，`%@`、`%lld` 跟執行時查的鍵一致），合併進 catalog，程式已經不用的條目直接刪掉。
  - `check`：列出缺英文的條目；逐行比對原始碼裡的中文字串（含全形標點）和編譯器記錄的查表字串數量，找出沒查表的介面中文。略過：`log.`、`zh:`（各語言分開寫好的地方）、標了 `// l10n-skip` 的行，以及 DEBUG 控制通道、提示詞、範本三個檔案。
  - `apply <翻譯.json>`：把英文寫進 catalog；複數寫成 `{"one": …, "other": …}`。
- 建置：`build-app.sh` 用 `xcrun xcstringstool compile` 編出 `en.lproj`、`zh-Hant.lproj` 的 `Localizable.strings`／`.stringsdict`，放進 `Contents/Resources`。
- Info.plist：`CFBundleDevelopmentRegion` 改成 `en`、`CFBundleLocalizations` 是 `en`、`zh-Hant`。系統語言不是中文時顯示英文；繁中的表裡沒有的鍵直接顯示中文原文，所以中文介面不受影響。app 名稱（英文 Shiyin、中文拾音，`LSHasLocalizedDisplayName`）、權限說明、檔案類型名稱放在 Info.plist（英文）和 `zh-Hant.lproj/InfoPlist.strings`。
- 英文介面裡的中文內容（會議標題、逐字稿）要用繁體字形和全形標點：所有視窗套 `.chineseGlyphs()`（`typesettingLanguage(zh-Hant)`），不然系統會用簡體的字形和標點位置。

### 16.2 格式

- 日期、星期用 `Date.FormatStyle`（`DateText.weekday(_:language:)`）。
- 會議長度用 `TimeFormat.duration(_:language:)`：中文「N 分鐘」「N 小時 M 分鐘」，英文「N min」「N hr M min」；介面用介面語言，匯出用會議語言。
- 「3 題」這類數量在 catalog 裡用複數變化處理。

### 16.3 會議語言

- `MeetingLanguage`（`zh`／`en`）：轉錄的 locale 和 whisper 語言碼、提示詞、範本、匯出、預設說話者名稱（「說話者 N」／「Speaker N」，存逐字稿時依會議語言決定）都從這裡取。介面語言另外由 `MeetingLanguage.interface`（`Bundle.main.preferredLocalizations`）判斷。
- migration `v3`：`meetings.language TEXT NOT NULL DEFAULT 'zh'`，舊會議都是中文。
- 設定 →「錄音」加「預設會議語言」（`defaultMeetingLanguage`，沒設定時依介面語言）。錄音按鈕旁的選單、匯入對話框、會議頁（地球圖示的選單）都可以選。已經轉錄的會議改語言不會自動重做，提示使用者從「更多」重新轉錄或整理。
- .shiyin 封包的 `meeting.json` 帶 `language`，舊封包沒有就當成 `zh`。
- 會議頁的標題區：範本、會議語言兩個選單放在標題下方資訊列（日期、長度、說話者）的右邊，標題獨佔一整行。原本範本選單在標題右邊，多了語言選單後，英文標題（通常比中文長）在 960 寬的視窗裡兩行也放不下、被截斷。資訊列加選單放不下時（說話者多、英文），選單改到下一行（`ViewThatFits`），說話者名字不截斷。

### 16.4 轉錄

- Breeze（whisper.cpp）：`language` 用 `zh` 或 `en`；英文的提示詞是 "The following is a recording of a work meeting. Glossary: …"，詞彙表的長度上限中文 150 字、英文 400 個字元。英文的幻覺過濾多一句 "Subtitles by the Amara.org community"。
- macOS 內建、即時逐字稿：locale 用 `zh_TW` 或 `en_US`；第一次用英文時下載 `en_US` 模型（沿用 `ensureAssets`，首次使用引導依預設會議語言下載）。
- 說話者辨識跟語言無關。

### 16.5 整理與範本

- 提示詞依會議語言分成兩份：中文跟 3.0 一字不差；英文是同一套規則的英文版，輸出英文，自訂段落的標題也譯成英文。只在系統提示詞加一句「用英文輸出」，實測容易混進中文段落，所以分開寫。
- 內建範本：名稱、說明、自訂段落的標題和指示都有英文版（`BuiltinTemplates.english`），依 id 取用（資料庫裡存的中文名稱只當作自訂範本的名稱）。介面顯示用介面語言（`displayName`），提示詞用會議語言（`definition(for:)`）。
- 待確認、校稿、手動編輯照舊，只是內容跟著會議語言。

### 16.6 匯出

會議紀錄和逐字稿的標題、欄位名稱（摘要、決議、待辦、負責人、說話者…）用會議語言，跟介面語言無關：`ExportText(language)` 在程式裡直接寫好中英兩組文字，不查 catalog（D15）。HTML（PDF、Word 的來源）的 `lang` 和字型也跟著會議語言：英文用系統字和 New York 標題。中文匯出的內容跟 3.0 完全相同。

### 16.7 文件

`README.en.md`（README.md 最上面加語言連結）；`scripts/install-guide.en.html`，DMG 裡同時放「安裝說明.pdf」和「Install Guide.pdf」。中英文共用一個 DMG，所以磁碟名稱（Shiyin 3.1.0）和「應用程式」捷徑（Applications）改用不分語言的英文。

### 16.8 驗證

用 `-AppleLanguages "(en)"` 啟動 DEBUG 版，所有畫面截圖檢查；英文會議用 AMI 片段跑模擬錄音的全流程；中文用既有的測試流程回歸。

## 17. 轉錄準確度（3.2，R14）

### 17.1 評估工具（R14.1）

- DEBUG 指令 `shiyinctl transcribe-eval <錄音> <設定.json> <輸出.json>`：用 app 真正的轉錄流程（不建立會議、不寫資料庫）跑一次，輸出每段的時間、聲道、說話者、文字。設定可以指定引擎、語言、詞彙表、分聲道、VAD、回音處理，方便同一段錄音換設定重跑。
- `scripts/dev/asr-score.py <標準答案> <輸出.json>`：
  - 正規化：去標點和空白；中文統一成繁體（AISHELL-4 的答案是簡體，用 ICU 轉換）；數字寫法、常見異體字統一（規則在 `scripts/dev/asr-score.swift`）
  - 中文算 CER，英文算 WER；另外算插入、刪除、替換各多少，看錯誤是哪一種
  - 有關鍵詞清單時算辨識對幾個；有說話者標準答案（rttm）時算說話者標對的比例（沿用 `aishell-score.py` 的算法）
- 測試資料：

| 資料 | 用途 | 來源 |
|---|---|---|
| 使用者的真實會議 | 主要依據 | Q2，待使用者提供 |
| AISHELL-4 M_R003S01C01（38 分鐘、6 人） | 遠距收音的中文會議 | 已下載，`~/Library/Caches/ShiyinSpike/aishell4/` |
| AMI ES2004a 片段 | 英文會議 | 已有 |
| 模擬線上會議 | 分聲道、回音 | AMI ES2004a 的個人麥克風（Headset-0～3，CC BY 4.0）：一人放麥克風聲道、另外三人混成電腦聲音聲道；「有回音」版本再把電腦聲音延遲、衰減後加進麥克風聲道，模擬用喇叭開會 |
| 合成的中英夾雜會議（91 秒） | 專有名詞（關鍵詞） | 已有 |

- 每項改進前後的數字和耗時寫在 commit 訊息，結論寫進 requirements 的驗收結果。

### 17.2 偵測說話片段（VAD，R14.3）：評估後不採用

試過 whisper.cpp 內建的 VAD（Silero v5.1.2），預設和寬鬆兩組參數：小聲、遠處的說話被當成安靜跳過，漏字大增（AISHELL 30.5% → 38.4%～47.8%）。只有乾淨的合成錄音變好。程式、模型、設定開關都已移除（D18）。

### 17.3 分聲道轉錄（R14.2）

- 只在會議是雙聲道（`meetings.channels = 'both'`）時啟用；匯入的檔案和單聲道錄音照舊。
- `AudioDecoder.decodeChannels`：解碼時保留聲道（現在的 `decode` 會混成單聲道）。壓縮後的 m4a 也是雙聲道，重新轉錄時一樣能分開。
- 回音處理：轉錄後比對，麥克風聲道的段落如果跟電腦聲音聲道的段落時間重疊一半以上、文字相似（最長共同子序列 ÷ 較短的長度 > 0.6），就刪掉。另外試過轉錄前用 FluidAudio 的 LocalVQE 消回音，只在有回音時比較好，但那種情況分聲道本身就沒有幫助，已移除。
- 兩個聲道各自轉錄、各自做說話者辨識。說話者編號：麥克風那一邊的人先編（說話者 1、2…），電腦聲音那一邊接著編。
- 合併：依開始時間排序成一份逐字稿。`segments` 加 `channel` 欄位（migration v4，可以是空的），之後可以依聲道篩選或分析。
- 代價：Breeze 要轉兩次、說話者辨識做兩次，速度從約 10 倍速降到約 5 倍速。
- 評估結果（2026-10-16）：只在戴耳機的線上會議有幫助（錯誤率少約 3 個百分點），有回音時沒有比較好，而且說話者標對的比例從 95% 降到 79～88%。**先不在設定裡開放**，`AppSettings.splitChannels` 只能用 `defaults write com.weiwei.shiyin splitChannels -bool true` 打開測試；等使用者的真實線上會議（T-I5）再決定。

### 17.4 逐字稿校正（R14.4）

- 新的工作類型 `correct`，排在轉錄之後、整理之前（設定開啟時才排）；「更多」選單的「檢查可能聽錯的字」也可以手動排。這是輔助功能：失敗只記 log，不影響整理會議紀錄，也不發通知。
- 呼叫方式跟整理會議紀錄一樣（`LLMClient.generateJSON`，四種整理工具都能用），只是輸出格式換成校正建議。
- 把逐字稿分段送給整理工具（每次約 150 段），附上詞彙表、參加者、說話者名字、會議標題；輸出用 JSON schema 限制成 `[{segmentId, original, suggestion, reason}]`，`original` 必須跟原文的某一段文字完全一致，不一致的建議直接丟掉。
- 新資料表 `corrections`（migration v4）：段落、原文、建議、理由、狀態（待確認／已接受／已拒絕）。
- 逐字稿分頁：上方一列顯示「N 處可能聽錯的字」和「全部接受」「全部忽略」；段落裡的原文加底線和底色，段落下面逐條列出「原文 → 建議」和理由，可以接受或忽略。接受時改段落文字（含搜尋索引），已接受的顯示「已改成…」，可以還原。段落之後被手動改過、找不到原文時，接受和還原都不做，並提示使用者。
- 提示詞重點：只改「很可能聽錯」的字（同音字、專有名詞、明顯的錯字），不改口語、不潤飾、不補字。
- 評估：比較接受全部建議前後的 CER，並人工檢查改錯的比例；改錯的太多就預設關閉。

### 17.5 詞彙表建議（R14.5）

- 來源：參加者名單、改過名字的說話者（預設的「說話者 N」不算）、接受過的校正建議裡含英文字母的字（產品名、術語），以及中文會議標題裡的英文詞（產品名、專案名；英文會議的標題不拆）。用詞修正時本來就可以勾「加進詞彙表」，不另外建議。
- 已經在詞彙表裡的不重複建議；忽略過的記在 `glossary_dismissed`（migration v5），不再建議。出現越多次排越前面，最多 12 個。
- 位置：設定的「詞彙表」分頁上方列出建議，可以逐個加入或忽略。
- 評估：用合成的中英夾雜會議，比較只用原本的詞彙表、加上建議詞彙後的關鍵詞辨識結果。

### 17.6 預設值與設定

- 只有評估後採用的改進才在設定裡放開關，預設值依評估結果決定（R14 驗收），寫進 requirements 的驗收結果。目前：偵測說話片段不採用；分聲道等真實會議；逐字稿校正的開關放在「整理工具」分頁（它用的是整理工具）。
- 開關全部關閉時，轉錄流程跟 3.1 完全一樣（R14.6）。

## 18. 日文（3.3，R15、R16）

### 18.1 會議語言

- `MeetingLanguage` 加 `ja`：`speechLocale` 是 ja_JP、`whisperCode` 是 ja、名稱「日本語」。
- 依會議語言的文字，現在是 `text(zh:en:)`（約 31 處）。改成 `text(zh:en:ja:)`，日文必填，編譯器會抓出漏掉的地方。依介面語言的 `ui(zh:en:)` 維持兩種（沒有日文介面）。
- 預設會議語言（`systemDefault`）：跟著介面語言；macOS 的第一個語言是日文時（介面是英文）預設日文。
- 封包：`meeting.json` 的 `language` 可以是 `ja`；舊版讀到不認得的語言，照現有的寫法當成 `zh`。

### 18.2 正式轉錄引擎（D19）

- 中文、英文會議維持 Breeze（R15.6）。
- 日文會議用 **Whisper large-v3-turbo q5_0**（574 MB，whisper.cpp 官方轉檔，MIT），加上詞彙表提示和逐字稿校正。評估比較了 Breeze、large-v3-turbo（q5_0、q8_0）、kotoba-whisper v2.0、macOS 內建，結果：large-v3-turbo q5_0 在會議資料的錯誤率最低（合成會議 4.5%、國會聽證 9.2%／7.7%，加上校正後 2.0%／8.8%／6.5%、關鍵詞全對）；Breeze 會把片假名外來語寫成英文；kotoba-whisper 在 whisper.cpp 上會亂輸出、整句漏掉；macOS 內建朗讀最好但會議資料最差。設定裡選了「macOS 內建」的話，日文會議也用 macOS 內建。
- `TranscriptionOptions.engine` 的 `breeze` 意思改成「whisper.cpp」，模型依會議語言選（`ModelStore.transcription(for:)`）；評估時可以用 `model` 指定模型檔。
- `ModelStore` 改成管多個模型（`breeze`、`japanese`、`languageDetection`），各自下載、暫停、刪除，固定 commit、SHA-256 驗證、可以續傳。
- 只有第一次轉錄日文會議時才下載：`JobRunner` 只取模型已經下載的語言的轉錄工作；有日文會議在排隊、日文模型還沒下載過，就自動開始下載（使用者暫停過或下載失敗就不自動重來）。會議頁顯示「等日文轉錄模型下載完成就會開始轉錄」。
- 首次使用引導：預設會議語言是日文時，下載日文模型而不是 Breeze。設定 → 模型與儲存空間多一列日文模型。
- 提示詞：「以下は仕事の会議の録音です。用語：…」加詞彙表，150 字以內（跟中文一樣）。
- 幻覺過濾加「ご視聴ありがとうございました」「ご視聴いただきありがとうございました」「チャンネル登録」。

### 18.3 即時逐字稿

用 SpeechAnalyzer ja_JP，第一次用時沿用 `ensureAssets` 下載系統的日文辨識資源。

### 18.4 整理、校正、範本

- 會議紀錄提示詞、逐字稿校正提示詞各加一份日文版（跟英文版一樣，規則相同、輸出日文，不只是加一句「用日文輸出」，見 §16.5）。
- 五個內建範本的日文版（名稱、說明、自訂段落的標題和指示）。

### 18.5 匯出

`ExportText` 加日文：日付、参加者、話者、長さ、要約、決定事項、ToDo、担当、期限等。HTML（PDF、Word 的來源）的 `lang="ja"`，內文用 Hiragino Sans，標題用 Hiragino Mincho。

### 18.6 字形（D20）

現在整個 app 套 `.chineseGlyphs()`，漢字一律用繁體中文字形。改成：介面維持繁中字形；會議內容依會議語言設定 `typesettingLanguage`，日文會議用 `ja`。

- 會議頁把會議語言放進環境值 `meetingLanguage`；只有內容本身套 `.meetingText()`：逐字稿每一段、校正建議、會議紀錄的條目和待辦、待確認的題目、筆記、錄音中的即時逐字稿、編輯時的輸入框。介面的說明文字、按鈕不套（一開始整個分頁都套，介面說明的逗號也變成日文的位置，改成只套內容）
- 會議列表的標題、搜尋結果的片段用 `.contentGlyphs(language)`
- 會議標題、段落標題、議題標題原本用宋體-繁（漢字是中文寫法），日文會議改用ヒラギノ明朝（`.contentTitleFont`）

### 18.7 評估

- `asr-score.swift`（只在 `lang` 是 `ja` 時生效，中文、英文的分數不變）：
  - 平假名、片假名、長音符號也當成一個字
  - NFKC 正規化：全形英數字轉半形、半形片假名轉全形（會議錄的 ＡＩ 跟辨識結果的 AI 要算一樣）
  - 漢字數字轉阿拉伯數字（會議錄寫「二〇二五年」「十五分」，辨識結果多半寫「2025年」「15分」）；兩邊都轉，所以「一般」變成「1般」也不影響
  - 去語助詞的分數：先用規則刪掉「えー」「えっと」「あのー」「まあ」「うーん」等（日文的語助詞是好幾個假名，不能一個字一個字比）。單獨的「あの」「え」也可能是一般的詞，不刪
  - 日文不轉簡體
- 測試資料：
  - FLEURS 日文測試集（CC BY 4.0，朗讀維基百科句子）：基本辨識率
  - 合成的日文會議（macOS 的 Kyoko、Eddy 等日文語音，講稿夾片假名、英文專有名詞、人名，附關鍵詞）：專有名詞、英日夾雜
  - 真實會議：日本眾議院內閣委員會 2025-04-16 AI 法案專家聽證裡的兩段問答（約 15 分鐘、10 分鐘，各 6 位說話者）。音訊從眾議院網路審議直播站的 HLS 用 ffmpeg 只抓那兩段，正確答案用國會會議錄 API 裡對應的發言，去掉「○三木委員」這類開頭和〔拍手〕等註記（主要依據）
- 中英回歸：3.2 的評估資料轉錄輸出要完全相同。

### 18.8 自動偵測主要語言（R16，D21）

- 開始錄音後收集有聲音的片段（`LanguageDetector.Collector`）：每 0.1 秒一段，比背景噪音大 2.5 倍（至少 -54 dBFS）才算；背景噪音的估計遇到更小的就降、平常慢慢往上爬。湊滿 10 秒就偵測；錄了 45 秒還湊不滿（小聲、遠距收音）但有 5 秒以上也先偵測。一開始用固定的 -40 dBFS 門檻，遠距收音的會議要等兩三百秒，改成跟著噪音調整。
- 用 whisper.cpp 的語言偵測（`whisper_lang_auto_detect`），只取中文、英文、日文三個機率、加總成 1。
- 模型用 Whisper base q5_1（60 MB）。Breeze 會把英文判成中文，不能用。設定開著自動偵測時，啟動 app、首次使用引導、打開開關時下載；還沒下載好的那次錄音不偵測。
- 機率最高的語言跟目前不同、而且 0.7 以上才切換：會議語言跟著改（資料庫）、即時逐字稿用新語言重新開始。錄音畫面顯示「聽起來是日文，即時逐字稿和會議語言已經改成日文」和「改回中文」。
- 錄音的混音同時送給即時逐字稿和偵測（`MixTap`）。即時逐字稿重新開始時：新的辨識從 0 秒算，時間點要加上接上音訊時已經錄了多久（每個辨識各自記）；被換掉的辨識收尾送來的是用錯的語言聽的，不加進逐字稿。之前已經出現的即時逐字稿保留，錄完會正式轉錄。
- 只偵測一次；按「改回」之後不再自動切換。設定 → 錄音可以關掉。

## 19. 更多整理工具（3.4，R17）

### 19.1 共同介面（D22）

```swift
@MainActor protocol LLMBackend {
    /// 回傳符合 format.schema 的 JSON
    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data
    /// 可用的模型，給設定頁選；不支援或失敗回空陣列
    func listModels() async -> [String]
}
```

- `LLMProvider`（enum）保留：它是存在 UserDefaults（`llmProvider`）和資料庫（`notes_by`）的代號，也負責介面上的名稱、要不要 key、預設模型。`provider.backend(model:)` 產生對應的實作。
- 每種整理工具一個型別、一個檔案，放在 `Notes/Backends/`：`ClaudeCLI`、`CodexCLI`、`AnthropicAPI`、`OpenAIAPI`、`GeminiAPI`、`OpenAICompatibleAPI`。送出 HTTP、讀錯誤訊息的共用部分放在 `APIRequest`。
- HTTP 429 的訊息提到 credit、quota、billing 時是帳戶沒有額度（OpenAI 實測：「You have no credits remaining」），提示「帳戶沒有額度，要到服務的網站加值」，不要寫「請稍後再試」；其他 429 才是用量限制。
- `LLMClient.generate`、`generateJSON`、`listModels` 的呼叫方式不變，呼叫的地方（JobRunner、逐字稿校正、設定頁）不用改。
- 原本四種的程式碼原封搬過去，只換位置（R17.7）。
- 不做外掛或動態註冊：整理工具的清單還是 enum 的 case。加一種整理工具＝一個新檔案＋一個 case＋設定頁的欄位。

### 19.2 Gemini API（R17.1、D23）

| 項目 | 內容 |
|---|---|
| 端點 | `POST https://generativelanguage.googleapis.com/v1beta/interactions`，header `x-goog-api-key` |
| 內容 | `model`、`system_instruction`、`input`（提示詞）、`response_format: {type: "text", mime_type: "application/json", schema}`、`store: false` |
| 結果 | `status` 是 `completed`：把 `steps` 裡 `type == "model_output"` 的 `content[]`（`type == "text"`）的 `text` 接起來。`incomplete`：錯誤「回應不完整（可能超過長度上限）」。其他狀態：錯誤，附上狀態 |
| 錯誤 | 錯誤回應可能包在陣列裡（`[{"error": …}]`，實測 Interactions 是這樣），兩種格式都要讀；key 錯誤是 HTTP 400 加上 `API key not valid`，要跟 401 一樣提示 key 錯誤 |
| 模型清單 | `GET /v1beta/models?pageSize=1000`，取名稱是 `models/gemini-…`、`supportedGenerationMethods` 有 `generateContent` 的，去掉 `models/`。圖片、語音、機器人等模型也有 `generateContent`，所以再排除名稱有 embedding、tts、image、live、audio、banana、robotics、computer-use、transcribe、customtools、omni 的（2026-10 實際清單 47 個，留下 16 個） |
| 預設模型 | `gemini-3.8-flash`（2026-10 官方建議的一般用途模型） |
| key | 鑰匙圈 account `gemini` |

### 19.3 OpenAI 相容 API（R17.2、R17.4、D24）

| 項目 | 內容 |
|---|---|
| 端點 | `POST <網址>/chat/completions`；有 key 才加 `Authorization: Bearer <key>` |
| 內容 | `model`、`messages`（`system`、`user`）、`response_format: {type: "json_schema", json_schema: {name, schema, strict: true}}` |
| 退回 | HTTP 400 或 422，而且錯誤訊息提到 `response_format`、`json_schema` 或 `schema`：改成 `response_format: {type: "json_object"}`，系統提示詞後面加上 schema 和「只輸出符合這個 JSON schema 的 JSON 物件」，再送一次 |
| 結果 | `choices[0].message.content`；`finish_reason` 是 `length`：錯誤「回應不完整（可能超過長度上限）」。內容前後有 ```` ```json ```` 圍欄或其他文字時，取第一個 `{` 到最後一個 `}` |
| 格式不對 | 回應不是 JSON，或解不開成會議紀錄、逐字稿校正的格式時，錯誤訊息加上「這個模型可能不支援 JSON 輸出，可以換一個模型試試」 |
| 模型清單 | `GET <網址>/models` 的 `data[].id` |
| 設定 | 網址、模型存在 UserDefaults（`openaiCompatibleBaseURL`、`openaiCompatibleModel`）；key 存鑰匙圈 account `openaiCompatible`，可以不填 |
| 常用網址 | Groq `https://api.groq.com/openai/v1`、OpenRouter `https://openrouter.ai/api/v1`、Ollama `http://localhost:11434/v1`、LM Studio `http://localhost:1234/v1` |

- 網址只接受 http、https；去掉結尾的 `/`，使用者貼了完整的 `…/chat/completions` 也去掉那一段。
- 模型必填，沒有預設：沒填時錯誤「還沒有填模型名稱」。
- 逾時一樣 20 分鐘（本機模型可能很慢）。
- 隱私說明（R17.5）：主機是 `localhost`、`127.0.0.1`、`::1` 時寫「送到這台 Mac 上的服務，不會離開電腦」；其他寫「送到 <主機>」。用 http 而且不是本機時，加上「用 http 傳送沒有加密」。

### 19.4 設定頁、引導、環境檢查（R17.5、R17.6）

- 整理工具選單多「Gemini API」「OpenAI 相容 API」。
- Gemini：跟 Anthropic、OpenAI 一樣，有 API key、模型（可以從清單選）、測試。
- OpenAI 相容 API：網址欄位，旁邊的「常用」選單可以填入常用網址；API key（說明本機服務可以不填）；模型（「選擇」列出 `/models`）；測試。
- 首次使用引導：選單多這兩項。Gemini 跟其他 API 一樣在引導裡填 key；OpenAI 相容 API 要填的比較多，引導裡只提示「網址、模型到『設定 → 整理工具』填寫」。
- 環境檢查多「Gemini API key」「OpenAI 相容 API」（有沒有填網址和模型，有的話顯示主機）。
- 「由 xx 整理」顯示「Gemini API」「OpenAI 相容 API」。

### 19.5 驗證（R17.7）

- DEBUG 加 `shiyinctl llmurl <anthropic|openai|gemini|openaiCompatible> <網址>`：把 API 的網址改到本機的假伺服器（只在 DEBUG 建置，不存設定；OpenAI 相容 API 是取代設定裡的網址）。
- DEBUG 加 `shiyinctl llmkey <account> <key>`、`llmprovider <整理工具> [模型]`：測試用的 key、整理工具、模型只放在記憶體。不能用 `setkey` 和 `defaults write`：鑰匙圈的項目綁定建立它的 app 簽章，ad-hoc 重建或使用者自己的拾音去讀都會跳出系統的存取確認；整理工具的設定跟使用者的拾音共用。
- `shiyinctl listmodels <整理工具> <結果.json>`：設定頁「選擇」用的模型清單。
- `shiyinctl preview <settings|onboarding [步驟]>`：設定視窗要 app 在前景才打得開，會搶走使用者的畫面；這個指令把設定頁放進桌面層的無邊框視窗（被所有視窗蓋住；放在螢幕外的視窗截不到），用 `screencapture -l` 截圖，不切換前景。
- 假伺服器 `scripts/dev/fake-llm-server.py`：記下每個請求（路徑、header、body，key 只記前幾個字），依路徑回固定的成功回應；可以設定讓 `json_schema` 回 400，測退回 JSON 模式。
- 先在 3.3 的程式加上 `llmurl`，送一次 Anthropic、OpenAI 的整理和逐字稿校正，記下請求；改成共同介面後再送一次，比對網址、header 和解析後的 JSON。Swift 字典轉 JSON 的鍵順序每次執行可能不同，所以不比位元組。
- Claude CLI、Codex CLI 各實際整理一次。

## 20. 穩定性修正（3.4.1，R18）

### 20.1 外部程式提早結束（R18.1）

- `AppDelegate.applicationWillFinishLaunching` 一開始 `signal(SIGPIPE, SIG_IGN)`。整理時提示詞從 stdin 送給 claude、codex，一小時會議約 60～100 KB，超過 pipe 的緩衝，寫入會等子程序讀；子程序先結束時，寫入收到 SIGPIPE，預設是結束整個程序。忽略之後寫入只會失敗（EPIPE），`Subprocess` 原本就忽略寫入的錯誤，照常讀子程序的結束代碼和錯誤訊息。
- `Subprocess` 的寫入端另外設 `F_SETNOSIGPIPE`，不靠全域設定也安全。

### 20.2 錄音中換裝置（R18.2，D25）

- `AudioSource` 多兩個成員：`onInterruption`（音源發現裝置變了，任何執行緒都可能呼叫）和 `restart()`（預設是 `stop()` 再 `start()`）。
- 麥克風：`start()` 時監聽這個 engine 的 `AVAudioEngineConfigurationChange`（接上或拔掉耳機、藍牙換協定、取樣率改變時，AVAudioEngine 會自己停下來）。重新開始時重讀輸入格式、建新的 `StreamConverter`；設定裡指定的輸入裝置不在了，就用系統預設。
- 系統聲音：`start()` 時監聽系統的預設輸出裝置（`kAudioHardwarePropertyDefaultOutputDevice`），換了就依序清掉 tap 和聚合裝置，用新的輸出裝置重建。`stop()` 時移除監聽。
- `Recorder` 收到 `onInterruption` 後，在主執行緒等 0.3 秒（裝置切換時通知會連續來好幾次）再 `restart()` 那個音源。
- 看門狗：`RecordingWriter` 記錄每個聲道最後一次收到音訊的時間（暫停時音源照樣送資料，所以暫停不影響）。`tick` 發現某個音源超過 3 秒沒送資料：錄音畫面的提示列顯示「麥克風沒有送出聲音，正在重新連接…」（系統聲音是「電腦聲音的擷取中斷了，正在重新連接…」），每 5 秒試一次 `restart()`；收到資料後提示消失。`restart()` 失敗就顯示原因，錄音不停：另一個音源照常錄，雙聲道時這段會補靜音。
- DEBUG：`FileSource` 可以讓某個聲道停止送資料幾秒（模擬裝置停了；時間照樣往前，那段資料丟掉），`restart()` 從停下的位置繼續送，不會提早結束停止。`shiyinctl recordstall <秒數|off> [聲道]`、`recordinterrupt [聲道]`（連續送 3 次通知，驗證只重新開始一次）用來驗證看門狗和重新連接。模擬的檔案送完後不再送資料是正常的，看門狗不檢查。真的裝置要使用者實測。

### 20.3 寫檔失敗、磁碟空間（R18.3）

- `RecordingWriter` 寫入失敗時記下錯誤（原本就有），另外提供 `writeFailed`。`tick` 看到就呼叫 `stop()`，並設定 `failureMessage`：「錄音寫不進磁碟（可能是空間不足），已經停止錄音。停止前錄到的部分有保留，會照常轉錄。」`stop()` 原本就會保留已經寫入的檔案並排轉錄。
- 開始錄音時看錄音資料夾所在的磁碟剩多少：`volumeAvailableCapacityForImportantUsage`（含可以清掉的空間）和 `volumeAvailableCapacity` 取大的（前者在磁碟映像、外接磁碟會回傳 0）。不到 1 GB 就提醒「磁碟只剩 N，大約只能錄 M 分鐘」（雙聲道每小時約 230 MB、單聲道約 115 MB），照樣開始錄。錄音中每 30 秒看一次，不到 300 MB 提醒一次。
- 錄音結束後（`AppModel.recordingFinished`）資料庫寫入失敗（磁碟滿時常常一起發生）：把錄音檔搬回 `recording/`（檔名照舊，帶會議 id），下次開啟由復原流程接回這場會議（§20.6），不會留下找不到的檔案；會議改了一半也一樣改回去。

### 20.4 筆記存檔（R18.4）

- 筆記編輯器（`RecordingView.swift` 的 `UserNotesEditor`，錄音畫面和筆記分頁共用）存檔改呼叫 `AppModel.saveUserNotes(id, text)`：寫資料庫後，目前顯示的是同一場會議時，只更新 `detail.meeting.userNotes`，不整份重新讀取。編輯器重新出現時讀到的 `initial` 就是最新的內容。

### 20.5 意外結束的工作（R18.5，D26）

- migration `v6`：`jobs` 加 `crashes INTEGER NOT NULL DEFAULT 0`。
- 啟動時（`requeueRunning`）：還是 `running` 的工作表示上次執行時拾音意外結束，`crashes + 1`；加完是 2 以上就標成 `failed`，錯誤寫「執行這個工作時拾音意外結束了 2 次，已經停止自動重試。可以按『重試』再試一次。」，不到 2 次才改回排隊。
- 正常結束拾音（`AppDelegate` 確認後的 `terminateAll`）：先把執行中的工作改回排隊（不加 `crashes`），再停止它們。
- 「重試」會排一個新的工作，`crashes` 從 0 開始。

### 20.6 復原錄音（R18.6）

- `Importer.importFile(move: true)`：搬過去之後任何一步失敗，就把檔案搬回原處再刪會議，不刪檔案。
- `recoverRecordings` 接回原本的會議時也一樣：資料庫更新失敗就把檔案搬回 `recording/`，下次開啟再試。會議已經改了一半（`audio_file` 寫進去、排工作失敗）就改回原本的內容，不要指向搬走的檔案。
- 空會議的清理改成直接查資料庫：`source = 'recording'`、`audio_file = ''`、不在垃圾桶的會議，排除 `recording/` 裡還有錄音檔的會議 id（這次沒復原成功的）和正在錄的那一場。
- DEBUG：啟動參數 `-debugFailEnqueue YES` 讓排工作失敗，用來驗證失敗時不會刪掉錄音。

### 20.7 檢查 API key 有沒有存（R18.7）

- `Keychain.exists(account)`：查詢帶 `kSecReturnAttributes`、不帶 `kSecReturnData`。只讀屬性不用解密，換了簽章也不會跳出存取確認。
- 環境檢查、設定頁的「已儲存」、首次使用引導改用 `exists`；只有送出請求和列出模型時才 `get`。
- DEBUG 版預設完全不讀真的鑰匙圈（`get`、`exists` 只看 `llmkey` 放在記憶體的 key）：每次重建簽章都不同，讀使用者存的 key 會跳出確認視窗，主執行緒會停在那裡等。要測鑰匙圈時加啟動參數 `-debugRealKeychain YES`。2026-10-19 驗證 3.4.1 時，DEBUG 版啟動時的環境檢查在使用者的畫面上跳出了 27 次確認。

### 20.8 log 不寫錯誤內容（R18.8）

- `Error.logSummary`：`LLMError` 只寫種類（`notInstalled(claude)`、`notLoggedIn(codex)`、`missingKey(Gemini)`、`failed`、`badOutput`），其他錯誤寫 domain 和代碼。
- `JobRunner` 的「工作失敗」「逐字稿校正失敗」兩處改用 `logSummary`。完整的錯誤訊息照樣存在 `jobs.error`、顯示在畫面上（那是使用者自己的資料庫）。

### 20.9 匯出的會議（R18.9）

- `ExportSheet` 打開時記下會議 id；`AppModel.export(..., meetingId:)` 依 id 從資料庫重新讀這場會議的資料。會議不存在時丟出錯誤「這場會議已經被刪除，沒辦法匯出」。
- 對話框的標題寫出會議名稱（「匯出「<標題>」」），使用者看得出要匯出的是哪一場；提示（有沒有會議紀錄、有沒有錄音）和建議的檔名也用這場會議的資料。

### 20.10 封包匯入（R18.10）

- `manifest.audio` 只接受單純的檔名（等於 `lastPathComponent`、不是空的、不以 `.` 開頭），而且解壓後是一般檔案、不是符號連結；不符合就丟出「封包裡的錄音檔不正確」。副檔名用 `Importer.safeExtension`。
- `transcript.json` 存在但讀不出來：丟出「transcript.json 讀不出來」。檔案不存在才當成沒有逐字稿。

### 20.11 搜尋索引（R18.11，D27）

- 搜尋表的 rowid 改成可以推算：逐字稿的段落用段落的 id；標題、會議紀錄、筆記用 `2^50 + 會議 id × 4 + 種類`（標題 1、會議紀錄 2、筆記 3），不會跟段落的 id 重疊。新增時指定 rowid，修改、刪除都用 rowid，不再用 `kind`、`ref` 找（FTS5 只能用 MATCH 或 rowid 找列，用其他欄位每次都掃整張表）。
- 實測（sqlite3 3.51，10 萬列）：用 `kind`、`ref` 刪一列 13 ms，用 rowid 不到 1 ms；一次刪掉 1,000 段 4 ms。
- migration `v6` 重建搜尋索引（`rebuildSearch` 改成指定 rowid）。
- `kind`、`ref` 欄位保留，搜尋結果照樣用它們。
- `undoNotes` 復原上一版時，標題的索引也要更新。

### 20.12 結束時等工作停下來（R18.12）

- 轉錄中結束拾音時，`terminateAll` 送出取消後 app 馬上 `exit`，這時 whisper.cpp 還在 GPU 上算（context 在 `whisper_full` 回來後才釋放），`exit` 的靜態解構呼叫 `ggml_metal_device_free`，裡面 `ggml_abort`，app 以 SIGABRT 結束（2026-10-19 的當機報告）。
- `WhisperContexts`：whisper context 建立時加一、`whisper_free` 後減一（轉錄、語言偵測都算），執行緒安全。
- `applicationShouldTerminate` 確認之後：停止錄音、`terminateAll`（送出取消）；還有 whisper context 沒釋放就回 `.terminateLater`，用 run loop 的 Timer（common modes）每 0.1 秒看一次，都釋放了（最多 10 秒）再 `NSApp.reply(toApplicationShouldTerminate: true)`。
- 不能用 MainActor 的 Task 等：從主佇列的工作裡呼叫 `NSApp.terminate`（例如 DEBUG 的 `quit`、選單列的按鈕）時，AppKit 在原地跑 run loop 等回覆，主佇列不會再執行別的工作，兩邊互相等。Timer 是 run loop 的來源，不受影響；whisper 的 context 在自己的執行緒上釋放，也不需要主佇列。
- whisper 的取消是在計算中途由 `abort_callback` 檢查，實測停下來要多久寫在驗證結果。

## 14. 決策紀錄

| # | 決策 | 不採用的方案與原因 |
|---|---|---|
| D1 | 即時用 SpeechAnalyzer，正式用 whisper.cpp + Breeze | 只用 SpeechAnalyzer：專有名詞錯得多（實驗 CER 5.8%、關鍵詞 1/6）。只用 whisper.cpp：即時延遲約 4.5 秒，而且會跟正式轉錄搶 GPU。WhisperKit：第一次要編譯 5 分鐘，速度只有 3 倍速，GPU 模式的輸出還會壞掉。mlx-whisper：要 Python |
| D2 | 移除 Python 子程序 | 給別人用的 app 不能要求安裝 Homebrew 和 Python |
| D3 | 正式轉錄用 Breeze q8_0 | fp16（3.1 GB）：實驗看不出明顯的準確度差異，卻多了一倍的下載量和記憶體 |
| D4 | whisper.cpp 在程式內執行 | XPC 服務：可以隔離崩潰，但複雜度大增；錄音有 WAVRepair、工作會重跑，崩潰的代價有限 |
| D5 | 自簽憑證 | ad-hoc 簽章：每次更新後 TCC 權限都要重新允許（R10.7） |
| D6 | 不上 App Store | 沙盒不允許執行 claude、codex CLI |
| D7 | PDF、docx 用系統元件產生 | 第三方套件：多一個依賴，系統元件就夠用 |
| D8 | 說話者辨識用 FluidAudio | pyannote（Python）：違反 D2；sherpa-onnx：要另外整合 C++ 和 ONNX Runtime |
| D9 | 每一條紀錄都帶時間點，由 LLM 填 | 在 app 內用文字比對去找時間點：摘要是改寫過的文字，比對不準 |
| D12 | 翻譯表的鍵用程式裡的中文原文 | 鍵改成英文代號（`settings.recording.title`）：650 個呼叫點全部要改，程式也比較難讀；改成英文原文：中文介面反而要另外翻，而且中文才是主要語言 |
| D13 | 會議語言只分中文、英文，由使用者選 | 自動偵測語言：錄音開始時還沒有內容可以判斷，中英夾雜的會議也容易誤判 |
| D14 | 自己寫 l10n.py 抽字串、合併 catalog | `xcstringstool extract`：看不出型別，插值變成 `%arg`，跟執行時 SwiftUI 查的鍵對不上；`sync` 又不接受編譯器產生的 stringsdata。Xcode 專案：專案是 Swift Package，用指令建置 |
| D15 | 匯出的文字在程式裡寫中英兩組，不查 catalog | 指定 `en.lproj` 的 Bundle 查表：catalog 只有英文翻譯，中文要另外處理，而且匯出用的字串會跟介面字串混在一起，改介面時容易誤改匯出格式 |
| D16 | 線上會議分聲道轉錄後再依時間合併 | 照舊混成一軌：雙方講話重疊時互相干擾，說話者辨識也要從混音裡分；分聲道的代價是轉兩次，加上 VAD 後增加的時間有限 |
| D17 | 逐字稿校正只做成建議，使用者接受才改 | 直接改：模型可能把正確的專有名詞改錯，而且使用者看不出哪裡被改過；逐字稿是會議紀錄的依據，原文不能在使用者不知道的情況下變動 |
| D18 | 不做轉錄前的偵測說話片段（VAD） | whisper.cpp 內建 VAD：評估時漏字大增（AISHELL 30.5% → 38.4%～47.8%），只有乾淨的合成錄音變好；要解決的「安靜時憑空產生文字」現有的事後過濾已經擋掉大部分 |
| D19 | 日文會議的正式轉錄引擎評估後決定，中英文維持 Breeze | 全部改用一個多語言模型：中文會議的專有名詞會變差（Breeze 針對台灣中文、中英夾雜調過）；直接選 kotoba-whisper：還沒在會議錄音上實測，macOS 內建加上逐字稿校正也可能就夠 |
| D20 | 會議內容依會議語言設定字形，介面維持繁中字形 | 全部改成日文字形：中文會議的漢字會變成日文寫法；不處理：日文會議的漢字顯示成繁中寫法，日本使用者會覺得不對 |
| D21 | 自動偵測只在開頭偵測一次、只比中英日三種、要超過門檻才切換 | 一直偵測、每句切換：即時辨識同時只能用一種語言，頻繁重新開始會漏字；開頭寒暄很短容易判斷錯，所以要累積夠多說話內容 |
| D11 | 正式轉錄可以改用 macOS 內建辨識（預設仍是 Breeze） | 只用 Breeze：真人中文會議（AISHELL-4，遠距麥克風陣列、6 人、38 分鐘）上，Breeze 的 CER 29.1%、macOS 內建 25.2%，內建還快 160 倍、不用下載 1.7 GB 模型。只用內建：合成的中英夾雜會議上，內建的英文專有名詞幾乎都錯（關鍵詞 1/6，Breeze 加詞彙表 5/6），台灣公司的會議以這種情況為主，所以預設還是 Breeze |
| D22 | 整理工具做成共同介面（`LLMBackend`），enum 保留當代號 | 繼續在 enum 加 case、在 `LLMClient` 加 switch：到六種之後每個 switch 都要改，OpenAI 相容 API 還有網址這種只屬於它的設定，switch 會越來越長、各家的程式混在同一個檔案。做成外掛或動態註冊：整理工具只有六種，還要配合設定頁、引導、隱私說明，外掛的彈性用不到。代價：原本四種的程式碼要搬家，要確認請求沒變（§19.5） |
| D23 | Gemini 用 Interactions API，一律 `store: false` | generateContent：官方 2026-06 起列為舊版（仍支援），新模型和新功能只在 Interactions 推出。Interactions 預設會在 Google 保存請求（付費 55 天、免費 1 天），會議內容不該留在那裡，所以關掉。Gemini 的 OpenAI 相容端點：使用者可以自己用「OpenAI 相容 API」接，但 issue 的回覆答應 Gemini 有自己的選項，填 key 就能用 |
| D24 | OpenAI 相容 API 用 Chat Completions，JSON schema 不支援時退回 JSON 模式 | Responses API（OpenAI 官方那一種用的）：Groq、Ollama、LM Studio 等相容服務主要支援 Chat Completions。只用 JSON 模式：支援 JSON schema 的模型就少了格式保證。只用 JSON schema：Groq 只有少數模型支援，其他模型會回 400 |
| D25 | 錄音中裝置變了就自動重新接上，錄音不停 | 停止錄音並提示：會議還在進行，使用者可能沒注意到，後半場就沒錄到。只提示、不重接：使用者在開會中很難處理。代價：換裝置的那一兩秒沒錄到（雙聲道時補靜音） |
| D26 | 同一個工作意外結束 2 次才標成失敗，正常結束不算 | 1 次就失敗：拾音意外結束可能跟這個工作無關（例如錄音當掉），重跑一次通常會成功。一直重跑：每次開 app 都會再當一次，還可能帶走正在錄的會議 |
| D27 | 搜尋表的 rowid 用可推算的值，刪改都用 rowid | 只把 `saveTranscript` 改成一句 `DELETE … WHERE meeting_id = ? AND kind = 'segment'`：一次掃整張表還是會隨資料變多變慢，而且全文取代、全部接受校正仍是一段掃一次。另外建一張對照表：多一張表要維持同步，rowid 可以直接算出來就不需要 |
| D10 | 自己做簡單的更新器（JSON＋SHA-256＋designated requirement 檢查） | Sparkle 2：要在鑰匙圈產生並保管 EdDSA 私鑰、用 generate_appcast 產生 appcast，對只在公司內部發布的 app 來說太重。取捨：還沒改用自簽憑證（ad-hoc）時，designated requirement 只檢查 identifier，安全性靠 https 和更新網址的主機；改用自簽憑證後，新版必須是同一張憑證簽的 |

沿用 v2 的決策：資料放 Application Support（v2 D3）、GRDB（v2 D5）、會議紀錄存 JSON 加版本（v2 D6）、DEBUG 控制通道（v2 §13）。

## 15. 需要先做的實驗

| # | 內容 | 通過標準 |
|---|---|---|
| T-A1 | 用真實會議錄音驗證 SpeechAnalyzer 和 whisper.cpp + Breeze | whisper.cpp + Breeze 的正式逐字稿，人工抽查 5 分鐘不超過 10 處錯字；即時逐字稿讀得懂內容 |
| T-A2 | 系統聲音擷取（process tap） | 錄 YouTube 或 Meet 播出的聲音 5 分鐘：沒有斷音、跟麥克風的時間差在 100 ms 以內；拒絕權限時有明確的錯誤 |
| T-A3 | FluidAudio 說話者辨識 | 2～4 人的錄音，說話者標錯的時間不超過 15%；1 小時的錄音 3 分鐘內處理完 |
| T-A4 | whisper.cpp XCFramework 嵌進 app | 在 app 內轉錄測試音檔，結果和 whisper-cli 一致；取消要在 1 秒內停下來；記憶體峰值在 3 GB 以內 |
| T-A5 | 自簽憑證與 DMG | 在另一個使用者帳號下載 DMG 安裝：照安裝說明可以打開；更新版本後麥克風權限還在 |
