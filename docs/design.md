# 拾音 Shiyin 3.0：設計

> 狀態：已確認（2026-10-15，第三版；使用者以「全部做好再找我」授權開工）
> 對應 `requirements.md` 的需求編號（R#、N#）。實作中如果偏離本文件，要先改本文件再改程式。
> 第二版的設計保留在 `docs/archive/v2/design.md`。沿用的部分會標「沿用 v2 §n」。

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
├── Notes/        LLMProvider（Claude CLI、Codex CLI、Anthropic API、OpenAI API）、NotesPrompt、Templates
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

- API key 存在鑰匙圈：service 是 `com.weiwei.shiyin.apikey`，account 是 `anthropic` 或 `openai`。
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

- `scripts/make-signing-cert.sh`：在登入鑰匙圈建立一張自簽的程式簽章憑證，名稱是「Shiyin Self-Signed」，只要做一次。之後 `build-app.sh` 都用這張憑證簽章，designated requirement 才會固定，TCC 權限在更新後也會保留（R10.7）。
- `scripts/make-dmg.sh`：用 hdiutil 建立 DMG，裡面有 Shiyin.app、「應用程式」資料夾的捷徑、`安裝說明.pdf`。
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
- 全域快捷鍵（R11.3）：用 Carbon 的 `RegisterEventHotKey` 以獨佔模式（`kEventHotKeyExclusive`）註冊 ⌃⌥⌘R（不需要輔助使用權限）。不加獨佔的話，Carbon 允許不同的 app 註冊同一組按鍵，偵測不到衝突，按下時跟 ⌘R 一樣：沒在錄就開始，在錄就停止。設定 →「錄音」可以關掉；註冊失敗（例如被別的 app 佔用）時在設定裡顯示原因

## 13. 從第二版遷移（R10.5）

- 資料庫：先備份，再執行 migration `v2`（§4）。會議紀錄轉成 v2 格式；錄音、逐字稿、詞彙表照舊。
- 第二版已經轉錄好的會議不重新轉錄，也沒有說話者資訊。會議頁可以選「重新轉錄」，用新引擎重轉並辨識說話者。
- 偵測到第二版的 `venv/`、`models/breeze-asr-25/` 時，提示「可以刪除，釋放約 4.2 GB」，使用者確認後才刪。

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
| D11 | 正式轉錄可以改用 macOS 內建辨識（預設仍是 Breeze） | 只用 Breeze：真人中文會議（AISHELL-4，遠距麥克風陣列、6 人、38 分鐘）上，Breeze 的 CER 29.1%、macOS 內建 25.2%，內建還快 160 倍、不用下載 1.7 GB 模型。只用內建：合成的中英夾雜會議上，內建的英文專有名詞幾乎都錯（關鍵詞 1/6，Breeze 加詞彙表 5/6），台灣公司的會議以這種情況為主，所以預設還是 Breeze |
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
