# 拾音 Shiyin：設計

> 對應 `requirements.md` 的需求編號（R#、N#）。實作中如果偏離本文件，要先改本文件再改程式。
> 沿用第二版的部分會標「沿用 v2 §n」，第二版的設計用 `git show b621ed6:docs/archive/v2/design.md` 看。文中的任務編號（T-…）的驗證紀錄用 `git show b621ed6:docs/tasks.md` 看。

## 1. 架構

```
Shiyin.app（SwiftUI，單一程序，不依賴 Python、Homebrew、ffmpeg）
 ├─ Capture        AVAudioEngine（麥克風）＋ Core Audio process tap（系統聲音）
 │    ├─► RecordingWriter   16 kHz WAV，每個來源一個聲道，意外結束也能修復（沿用 v2 的 WAVRepair）
 │    └─► LiveTranscriber   SpeechAnalyzer zh_TW → 即時逐字稿（暫定＋定稿）
 ├─ JobRunner      三條線（3.5 起，§21.2）
 │    ├─ 轉錄線：解碼（AVFoundation）→ whisper.cpp + Breeze → 說話者辨識（FluidAudio）＋聲紋比對 → 壓縮成 m4a
 │    ├─ 整理線：整理、校稿、草稿紀錄（LLMBackend，§19）
 │    └─ 輔助線：逐字稿校正、猜說話者
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
| 說話者辨識（R5） | FluidAudio（Swift，Apache-2.0，pyannote 模型在 ANE 上跑） | 見 D8 |
| 系統聲音（R1.2） | Core Audio process tap（`AudioHardwareCreateProcessTap`）＋ aggregate device | macOS 14.4 以上的正式 API |
| 解碼匯入的檔案 | AVFoundation（AVAudioFile、AVAssetReader） | 取代 ffmpeg |
| 整理（R6） | CLI（Process）＋ API（URLSession） | CLI 沿用 v2 §6.2；API 讓沒有 CLI 的人也能用 |
| 資料庫 | GRDB 7，沿用 v2 的 schema 並遷移 | 加上 FTS5 trigram 做中文全文搜尋 |
| 匯出 PDF、docx | 先產生 HTML，再用 WKWebView 轉 PDF；docx 用 `NSAttributedString`（officeOpenXML） | 都是系統內建 |
| 封包 | 資料夾加 `manifest.json`，用 `/usr/bin/ditto` 壓成 zip，副檔名 `.shiyin` | 系統內建工具 |
| 簽章 | 固定的自簽憑證（`scripts/make-signing-cert.sh` 只要建一次） | 沒有開發者帳號；用固定身分簽章，更新後 TCC 權限才會保留（R10.7） |
| 更新（R10.6） | 自己做的更新器：JSON＋SHA-256＋簽章檢查（D10，§12） | 更新檔放在 GitHub Releases（Q1、R13.2） |

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

  Info.plist 加上 `NSAudioCaptureUsageDescription`。
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
- 段落的平均 token 機率太低，或壓縮比太高時，標成低信心（沿用 v2 的門檻概念）。
- 在程式內的背景 Task 執行，不另外開子程序（D4）。

### 6.2.1 macOS 內建辨識（R4.1、D11）

- 設定 →「錄音」的「正式轉錄」可以選「Breeze ASR 25（建議）」或「macOS 內建」，存在 `transcriptionEngine`（`breeze` / `system`）。
- 選 macOS 內建時：解碼後的 16 kHz 單聲道寫成暫存 WAV，交給 `SpeechAnalyzer.start(inputAudioFile:finishAfterFile:)`（`SpeechTranscriber` zh_TW、`audioTimeRange`）。每個定稿結果就是一段逐字稿，時間取 `result.range`；進度用結果的結束時間除以總長度。系統還沒有 zh_TW 模型時先下載（跟即時逐字稿共用）。
- 不需要 Breeze 模型，所以這種轉錄工作不必等模型下載完。
- 說話者辨識、壓縮、整理都照舊。沒有詞彙提示，也沒有低信心標示。

### 6.3 說話者辨識

- 轉錄完成後，用 FluidAudio 的離線辨識（offline diarizer）處理同一份 16 kHz 音訊，得到「說話者、開始、結束」的時間區段。
- 每段逐字稿標上跟它重疊最多的說話者，並在 `speakers` 建立「說話者 1…n」。
- 有雙聲道時（P1）：如果某個說話者的聲音幾乎都在麥克風聲道，就預設命名為「我」。

#### 6.3.2 很短的一句（R34、D92，2026-10-22）

使用者回報：訪談裡約 1 秒的一句「就只有這件事而已」被標成下一位說話者。看說話者辨識的結果，這裡的換人點放早了 1.3 秒，句子落在下一位的時段裡。分兩步改：

1. **說話者辨識的設定**（`Diarizer.config`）：FluidAudio 的離線辨識預設一個人在一個 10 秒窗裡說不到 1 秒就不算聲紋（`embedding.minSegmentDurationSeconds`），這種片段沒有人認領，重組時被判給第 0 群（FluidAudio 的說明：「整段話被默默併進旁邊那個人」）。改成 0.3 秒，並打開 `zeroVoteReembed`（沒人認領的片段重新算聲紋，判給最像的人）。
2. **每句再比一次**（`SpeakerRecheck`）：分好說話者之後，0.5～4 秒的句子各自算一次聲紋（FluidAudio 的 wespeaker_v2，帶著前後文：中心對著這一句的 10 秒窗、遮罩只標這一句），跟每位說話者的聲音（他 3 秒以上、最長的 20 句的平均）比，比另一位像 0.15 以上就改成那一位。模型約 15 MB（wespeaker_v2 8 MB、FluidAudio 一起帶的 pyannote_segmentation 6 MB），跟說話者辨識的模型一起準備（`Diarizer.prepare`），放在同一個 `fluidaudio/speaker-diarization`（FluidAudio 下載到指定資料夾的上一層加上這個名稱），下載不了就略過這一步。使用者改過說話者的句子（R23.2）不動。模型的三個欄位共用第一個欄位的聲音，所以一次算一句（17 分鐘的錄音約 8 秒，M3 Pro）。

評估（Mac DEBUG 版 `voiceprint-eval` 加上 `minSegment`、`zeroVote`、`step` 設定，`segment-embed` 算每句的聲紋，離線比對）。用標準答案的每一句，看說話者辨識判給誰；「沒重疊」是前後 0.2 秒內沒有別人在說話的句子（一個人講、另一個人插一句「嗯」的情況拾音一次只標一個人，R34.2）。兩步都做之後：沒重疊、2 秒內的句子標對 AMI 9 場 24.3% → 59.8%（改錯 0 句）、AISHELL-4 91.3% → 95.7%；含重疊的全部句子 AMI 37.2% → 47.3%、AISHELL-4 71.8% → 78.3%；日文合成會議（全部）90.0% → 100%。分出的人數、聲紋比對、分不準的提醒、說話者辨識的時間都不變。再比一次的門檻 0.1 改錯變多，0.2～0.3 改對的少一些。

使用者的那段訪談（17 分鐘、404 段）：改設定後沒有變（這裡不是短片段沒有聲紋，是換人點放早了）；每句再比一次改了 6 段，「就只有這件事而已」改成蔡康永（相似度 0.61 比 0.32）。

### 6.4 壓縮

轉錄完成後排一筆 `compress` 工作：用 `AVAssetExportSession`（AppleM4A）把 WAV 轉成 m4a，大約每小時 30 MB。成功後更新 `audio_file` 並刪除 WAV（R9.4，可以在設定關掉）。

## 7. 模型管理（R10.3）

- `ModelStore` 的模型清單（寫在程式裡）：

  | 模型 | 來源 | 大小 | 驗證方式 |
  |---|---|---|---|
  | Breeze ASR 25 q8_0 | HF `shdennlin/breeze-asr-25-ggml`，固定 commit | 1.66 GB | SHA-256 |
  | FluidAudio 說話者辨識模型 | FluidAudio 指定的 HF repo | — | SHA-256 |

- 用 URLSession 下載：暫停時保存 resumeData，斷線後接續；完成後驗證 SHA-256，通過才搬到 `models/`。
- 設定裡的「模型與儲存空間」頁可以看到每個模型的狀態，也可以重新下載或刪除。

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
  "reviewItems":  [{"time": "…", "question": "…", "yesNo": false}]
}
```

- JSON schema 沿用 v2 §6.1 的嚴格模式寫法：所有欄位都 required，不知道的值用空字串。
- 讀取時相容 v1 格式：遇到字串陣列，就轉成 `{"text": 字串, "time": ""}`。
- `reviewItems` 的 `yesNo`：能用是或否回答的題目（§27，R32）。
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
- 贊助連結（R13.3）：`Credits.html` 最上面一行是 Ko-fi 的連結（中文「覺得拾音好用的話，可以請我喝杯咖啡。」、英文「Enjoying Shiyin? Buy me a coffee.」），所以分成兩份放在 `zh-Hant.lproj`、`en.lproj`，系統依介面語言挑。`Resources/` 底下不能再放一份：系統找資源時先找沒有分語言的那份。連結用系統的文字元件，點了用預設瀏覽器打開，不在 app 裡開網頁。
- `make-dmg.sh` 在 DMG 放一份 `Third-Party Licenses.txt`。
- 更新網址：GitHub 的 repository variable `SHIYIN_UPDATE_URL` 設成 `https://github.com/juinwei7/Shiyin/releases/latest/download/shiyin-update.json`，CI 建置時寫進 app。`releases/latest` 永遠指向最新的 Release，每次發布不用改網址。

## 13. 從第二版遷移（R10.5）

- 資料庫：先備份，再執行 migration `v2`（§4）。會議紀錄轉成 v2 格式；錄音、逐字稿、詞彙表照舊。
- 第二版已經轉錄好的會議不重新轉錄，也沒有說話者資訊。會議頁可以選「重新轉錄」，用新引擎重轉並辨識說話者。
- 偵測到第二版的 `venv/`、`models/breeze-asr-25/` 時，提示「可以刪除，釋放約 4.2 GB」，使用者確認後才刪。

## 16. 多語言（3.1，R12）

### 16.1 字串

- 用 String Catalog：`Resources/Localizable.xcstrings`，來源語言是繁體中文，**鍵就是程式裡的中文原文**，另外填英文翻譯。好處是呼叫點不用改寫，程式讀起來跟原本一樣（D12）。
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
- 會議頁的標題區：範本、會議語言兩個選單放在標題下方資訊列（日期、長度、說話者）的右邊，標題獨佔一整行（範本選單放在標題右邊的話，英文標題在 960 寬的視窗裡兩行也放不下、被截斷）。資訊列加選單放不下時（說話者多、英文），選單改到下一行（`ViewThatFits`），說話者名字不截斷。

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

`.github/README.md`（英文，讓 GitHub 首頁顯示英文；README.md 是中文，最上面有語言連結）；`scripts/install-guide.en.html`，DMG 裡同時放「安裝說明.pdf」和「Install Guide.pdf」。中英文共用一個 DMG，所以磁碟名稱（Shiyin 3.1.0）和「應用程式」捷徑（Applications）改用不分語言的英文。

### 16.8 驗證

用 `-AppleLanguages "(en)"` 啟動 DEBUG 版，所有畫面截圖檢查；英文會議用 AMI 片段跑模擬錄音的全流程；中文用既有的測試流程回歸。

## 17. 轉錄準確度（3.2，R14）

### 17.1 評估工具（R14.1）

- DEBUG 指令 `shiyinctl transcribe-eval <錄音> <設定.json> <輸出.json>`：用 app 真正的轉錄流程（不建立會議、不寫資料庫）跑一次，輸出每段的時間、聲道、說話者、文字。設定可以指定引擎、語言、詞彙表、分聲道、回音處理，方便同一段錄音換設定重跑。
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

- 每項改進前後的數字和耗時寫在 commit 訊息，結論寫進這裡對應的章節。

### 17.2 偵測說話片段（VAD，R14.3）：評估後不採用

whisper.cpp 內建的 VAD 會把小聲、遠處的說話當成安靜跳過，漏字大增（D18）。程式、模型、設定開關都已移除。

### 17.3 分聲道轉錄（R14.2）

- 只在會議是雙聲道（`meetings.channels = 'both'`）時啟用；匯入的檔案和單聲道錄音照舊。
- `AudioDecoder.decodeChannels`：解碼時保留聲道（現在的 `decode` 會混成單聲道）。壓縮後的 m4a 也是雙聲道，重新轉錄時一樣能分開。
- 回音處理：轉錄後比對，麥克風聲道的段落如果跟電腦聲音聲道的段落時間重疊一半以上、文字相似（最長共同子序列 ÷ 較短的長度 > 0.6），就刪掉。
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

- 只有評估後採用的改進才在設定裡放開關，預設值依評估結果決定（R14 驗收），寫進這一節。目前：偵測說話片段不採用；分聲道等真實會議；逐字稿校正的開關放在「整理工具」分頁（它用的是整理工具）。
- 開關全部關閉時，轉錄流程跟 3.1 完全一樣（R14.6）。

## 18. 日文（3.3，R15、R16）

### 18.1 會議語言

- `MeetingLanguage` 加 `ja`：`speechLocale` 是 ja_JP、`whisperCode` 是 ja、名稱「日本語」。
- 依會議語言的文字用 `text(zh:en:ja:)`，日文必填，編譯器會抓出漏掉的地方。依介面語言的 `ui(zh:en:)` 維持兩種（沒有日文介面）。
- 預設會議語言（`systemDefault`）：跟著介面語言；macOS 的第一個語言是日文時（介面是英文）預設日文。
- 封包：`meeting.json` 的 `language` 可以是 `ja`；舊版讀到不認得的語言，照現有的寫法當成 `zh`。

### 18.2 正式轉錄引擎（D19）

- 中文、英文會議維持 Breeze（R15.6）。
- 日文會議用 **Whisper large-v3-turbo q5_0**（574 MB，whisper.cpp 官方轉檔，MIT），加上詞彙表提示和逐字稿校正：評估時在會議資料的錯誤率最低（合成會議 4.5%、國會聽證 9.2%／7.7%）；Breeze 會把片假名外來語寫成英文，kotoba-whisper v2.0 在 whisper.cpp 上會亂輸出、整句漏掉，macOS 內建在會議資料最差。設定裡選了「macOS 內建」的話，日文會議也用 macOS 內建。
- `TranscriptionOptions.engine` 的 `breeze` 意思改成「whisper.cpp」，模型依會議語言選（`ModelStore.transcription(for:)`）；評估時可以用 `model` 指定模型檔。
- `ModelStore` 改成管多個模型（`breeze`、`japanese`、`languageDetection`），各自下載、暫停、刪除，固定 commit、SHA-256 驗證、可以續傳。
- 只有第一次轉錄日文會議時才下載：有日文會議在排隊、日文模型還沒下載過，就自動開始下載（使用者暫停過或下載失敗就不自動重來）。下載好之前的日文會議先用 macOS 內建辨識轉錄（3.5 起，§21.17）。
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

介面維持繁中字形（`.chineseGlyphs()`）；會議內容依會議語言設定 `typesettingLanguage`，日文會議用 `ja`。

- 會議頁把會議語言放進環境值 `meetingLanguage`；只有內容本身套 `.meetingText()`：逐字稿每一段、校正建議、會議紀錄的條目和待辦、待確認的題目、筆記、錄音中的即時逐字稿、編輯時的輸入框。介面的說明文字、按鈕不套（整個分頁都套的話，介面說明的逗號也會變成日文的位置）
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
| 模型清單 | `GET /v1beta/models?pageSize=1000`，取名稱是 `models/gemini-…`、`supportedGenerationMethods` 有 `generateContent` 的，去掉 `models/`。圖片、語音、機器人等模型也有 `generateContent`，所以再排除名稱有 embedding、tts、image、live、audio、banana、robotics、computer-use、transcribe、customtools、omni 的 |
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

### 19.6 還沒設定整理工具（R35、D95，2026-10-22）

- `AppSettings.provider`：設定裡選過就用選的；沒選過（`llmProvider` 沒有值或不是認得的值）時，找得到 claude 就用 claude，再找 codex，都沒有就是 `.none`。每次讀都重新找（Mac 只看幾個固定資料夾有沒有執行檔），之後裝了 CLI 就自動用上。`providerChosen`：設定裡有認得的值；`needsProviderSetup`：沒選過而且結果是 `.none`。
- 用到整理工具的地方原本就用 `provider != .none` 判斷（自動整理、逐字稿校正、猜說話者、草稿紀錄），沒有 CLI 時都不排，不會「整理失敗」。
- 文字分兩種：`needsProviderSetup` 時說「還沒設定整理工具」，使用者選了「不整理」才說「整理工具設定為『不整理』」。會議紀錄的位置（`NotesPlaceholder`）多一個「設定整理工具」按鈕，打開設定（整理工具是第一個分頁）；整理、依回答修正、提問被擋下的訊息也分開。
- 引導、設定頁的選單：沒選過時顯示自動選到的那一個；都找不到 CLI 時多一個「之後再設定（先不整理）」，選它等於清掉設定。只在選單改了才寫進設定。找不到 CLI 時，兩個 CLI 選項寫「沒有安裝（給已經在用 Claude Code／Codex 的人）」；引導原本「沒有 claude 但有 codex 時改選 codex」的寫入拿掉（自動選就會選到 codex）。
- 填 API key 的整理工具旁邊放「取得 API key」連結：Anthropic `https://console.anthropic.com/settings/keys`、OpenAI `https://platform.openai.com/api-keys`、Gemini `https://aistudio.google.com/apikey`。OpenAI 相容 API 各家不同，不放。
- 選了 claude、codex 卻找不到（R35.4）：「這台電腦找不到 claude。沒有在用 Claude Code 的話，請到設定 → 整理工具改用 API（填 API key）或選「不整理」」。
- DEBUG：啟動參數 `-debugNoCLI YES` 當成找不到 claude、codex（在裝了 CLI 的電腦上測）；`-llmProvider ""` 當成沒選過（設定跟使用者的拾音共用，不能刪使用者的設定）。

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
- DEBUG：`shiyinctl notestype <文字>` 在畫面上的筆記輸入框（TextEditor 背後的 NSTextView）結尾用 `insertText` 打字，跟鍵盤打字一樣經過 SwiftUI 的綁定和自動存檔；`notesvalue <輸出.json>` 讀出輸入框目前顯示的內容。螢幕上鎖、視窗在別的桌面時也能用。

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
- DEBUG 版預設完全不讀真的鑰匙圈（`get`、`exists` 只看 `llmkey` 放在記憶體的 key）：每次重建簽章都不同，讀使用者存的 key 會跳出確認視窗，主執行緒會停在那裡等。要測鑰匙圈時加啟動參數 `-debugRealKeychain YES`。

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
- whisper 的取消是在計算中途由 `abort_callback` 檢查。

## 21. 錄完就有紀錄、認得說話者、可以提問（3.5，R19、R20）

### 21.1 資料模型（migration `v7`）

```sql
ALTER TABLE meetings ADD COLUMN notes_draft INTEGER NOT NULL DEFAULT 0;        -- 1：會議紀錄是用即時逐字稿整理的草稿（§21.3）
ALTER TABLE meetings ADD COLUMN transcribed_with TEXT NOT NULL DEFAULT '';     -- breeze / japanese / system，空字串是 3.5 以前（§21.17）
ALTER TABLE jobs ADD COLUMN error_kind TEXT;                                    -- Error.logSummary，回報問題用（§21.9）

ALTER TABLE speakers ADD COLUMN name_source TEXT NOT NULL DEFAULT 'default';    -- default / manual / voiceprint / suggestion
ALTER TABLE speakers ADD COLUMN person_id INTEGER REFERENCES people(id) ON DELETE SET NULL;
ALTER TABLE speakers ADD COLUMN embedding BLOB;                                 -- 這場會議裡這位說話者的聲紋（Float32×256）
ALTER TABLE speakers ADD COLUMN speech_seconds REAL NOT NULL DEFAULT 0;         -- 說了多久，太短的不比對、不學
ALTER TABLE speakers ADD COLUMN audio_source TEXT NOT NULL DEFAULT 'mix';       -- mic / system / mix（分聲道時是哪一聲道）
ALTER TABLE speakers ADD COLUMN suggested_name TEXT;                            -- AI 猜的名字（§21.5）
ALTER TABLE speakers ADD COLUMN suggestion_reason TEXT;
ALTER TABLE speakers ADD COLUMN suggestion_time TEXT;

CREATE TABLE people (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                     created_at TEXT NOT NULL);
CREATE TABLE voiceprints (id INTEGER PRIMARY KEY AUTOINCREMENT,
                          person_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE,
                          embedding BLOB NOT NULL, audio_source TEXT NOT NULL,
                          meeting_id INTEGER REFERENCES meetings(id) ON DELETE SET NULL, speaker_idx INTEGER,
                          created_at TEXT NOT NULL);
CREATE TABLE questions (id INTEGER PRIMARY KEY AUTOINCREMENT,
                        meeting_id INTEGER REFERENCES meetings(id) ON DELETE CASCADE,   -- NULL：跨會議
                        scope TEXT NOT NULL DEFAULT '', question TEXT NOT NULL, answer TEXT NOT NULL,  -- answer 是 JSON（§21.8）
                        provider TEXT NOT NULL, created_at TEXT NOT NULL);
CREATE TABLE drafts (meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
                     key TEXT NOT NULL, text TEXT NOT NULL, updated_at TEXT NOT NULL,
                     PRIMARY KEY (meeting_id, key));                             -- §21.15
```

- `SpeakerName` 加上新欄位。`saveTranscript` 重建說話者時保留改過的名字（沿用），新的欄位跟著轉錄結果寫入。
- 聲紋用 `Data` 存 Float32 little endian。`people.name` 不分大小寫唯一：同一個名字就是同一個人。
- 匯出的 .shiyin 封包不帶 `embedding`、`person_id`、`suggested_*`（`MeetingJSON` 不變）。

### 21.2 工作線（R20.1）

- 工作線從兩條變三條：`transcribe`（轉錄、壓縮）、`llm`（整理、校稿、草稿）、`assist`（逐字稿校正、猜說話者）。`JobKind.lane` 對應，`claimNext` 依線挑工作，`JobRunner` 每條線一個迴圈。
- 轉錄完排工作的順序不變（壓縮、校正、整理），但校正和整理在不同線上，會同時開始。整理讀的是 `final` 段落的原文（沿用），還沒接受的校正本來就不會進會議紀錄，所以內容跟以前一樣。
- 畫面上的「執行中」取同一場會議的工作時，`llm` 線優先（使用者在等的是會議紀錄）。

### 21.3 草稿紀錄（R19.1，D28）

- 停止錄音後，等即時辨識收尾完（`Recorder.onLiveFinished`，停止時還沒定稿的文字這時才存進去），再判斷：錄音 ≥ 3 分鐘、即時逐字稿（`segments` 裡 `kind = 'live'`）合計至少 100 個字、會自動整理、整理工具不是「不整理」、會議有錄音檔，就排一個 `summarize`，payload `{"draft":true}`，在 `llm` 線上跟轉錄同時跑。不用段數當門檻：即時辨識在講話沒停頓時會把很長一段當成一段。
- 草稿工作：開始時如果已經有 `final` 段落就不做（短會議轉錄比較快）。用 `kind = 'live'` 的段落組 `NotesContext`（沒有說話者），提示詞跟正式整理完全相同。存檔前再檢查一次：已經有 `final` 段落或會議紀錄不是空的就丟掉草稿。存成 `notes_draft = 1`，`saveNotes` 不留版本（原本是空的）。
- 正式整理存檔時 `notes_draft = 0`。原本是草稿而且使用者沒改過：不留版本（草稿沒有保存價值）。改過（手動編輯會把 `notes_draft` 改成 2）：照舊把舊的存進版本紀錄，原因寫「草稿（改過）」，畫面提示「正式版已經更新，你改過的草稿在版本紀錄裡」。
- 畫面：`notes_draft != 0` 時會議紀錄上方顯示「草稿：用錄音中的即時逐字稿整理，正式轉錄完成後會自動更新」。時間點照樣可以點（即時逐字稿的時間就是錄音檔的時間，暫停的部分本來就不在檔案裡）。
- 通知：草稿完成發「會議紀錄草稿好了」；之後的正式版不再發通知（同一場會議發過草稿通知），失敗照樣發。
- 校稿（回答待確認）在草稿上也可以做，送出的是草稿的內容；正式版完成後待確認會重新產生。
- DEBUG：啟動參數 `-debugDraftMinimumSeconds 30` 把「錄音 ≥ 3 分鐘」的門檻改短，驗證時不用真的錄 3 分鐘。

### 21.4 聲紋（R19.3，D29）

**取得**：`Diarizer.diarize` 回傳每段說話的編號，另外回傳每位說話者的聲紋：FluidAudio 離線辨識結果的 `speakerDatabase`（speakerId → 256 維），跟著 `Diarizer.assign` 的重新編號對應到最後的說話者編號；分聲道時每個聲道各自算，加上編號位移。同時算每位說話者說話的總秒數。`TranscriptionResult` 多 `voiceprints: [Int: [Float]]`、`speechSeconds: [Int: Double]`、`audioSources: [Int: String]`。

**比對**（轉錄存檔時，在 `JobRunner.transcribe` 裡，本機運算）：
- 功能打開（`AppSettings.voiceprints`，預設開）、說話者說話 ≥ 8 秒、有聲紋，才比對。
- 相似度用 cosine。每位認識的人取跟他所有聲紋相似度的最大值。高於門檻 `T` 的才算候選；依相似度由高到低分配，一個人在一場會議只對到一位說話者（分聲道時，同一個人不會同時在兩個聲道）。
- 對到的說話者分兩種（3.6.1 起，R33、D91）：相似度 ≥ `A` 而且比這位說話者第二像的人高出 `M` 以上，名字設成那個人，`name_source = 'voiceprint'`、`person_id`；其他的不帶入，存成說話者的建議（`suggested_name` 是那個人，`suggestion_reason` 是會議語言的「聲音跟之前的 X 很像」，沒有時間點），在待確認裡問（§27）。已經手動改過名字的說話者（重新轉錄時保留的）不覆蓋。
- `T`、`A`、`M` 用 AMI 語料和使用者的資料決定（§21.4.1），寫進程式的常數，不開放設定：`T` = 0.65、`A` = 0.88、`M` = 0.15。

**學習**：
- 說話者被取名（手動改名、套用建議），名字不是預設的「說話者 N」：找或建立同名的 `people`，`person_id` 指過去；說話 ≥ 8 秒而且有聲紋，就存一份 `voiceprints`（記下會議和說話者編號）。跟這個人已有的某份聲紋相似度 > 0.95 就不重複存。每人最多 30 份，超過刪最舊的。
- 改正自動帶入的名字（改成別人）：刪掉這場會議這位說話者之前存的聲紋（如果有），再照上一條存到新的人。按「不是他」：名字回到預設，`person_id` 清掉，`name_source = 'manual'`（之後重新比對也不再自動帶入這場）。
- 名字改回預設：刪掉這場會議這位說話者存的聲紋。

**設定**（新的「說話者」分頁）：開關「記住說話者的聲音，之後的會議自動帶入名字」；認識的人列表（名字、幾份聲紋、最近出現的會議），可以改名（`people.name` 和所有對到的說話者一起改）、刪除某人（聲紋刪掉，已經帶入的名字留著）、全部刪除。說明文字：聲紋只存在這台 Mac。

**畫面**：會議頁的說話者名字，自動帶入的後面有小小的「自動」；點名字的選單：改名、不是他（自動的才有）、只看他說的（沿用）。

#### 21.4.1 門檻與驗證資料

- AMI 的 ES2004a～d 是同一組 4 個人的 4 場會議（Mix-Headset，CC BY 4.0）。用 a 學（4 個人各取一次名字），b、c、d 比對；再用別組人的會議（例如 ES2005a、IS1000a）比對同一份名單，算誤認率。
- 評估工具：DEBUG 指令 `voiceprint-eval <設定.json> <輸出.json>`，對每場會議跑說話者辨識、算聲紋，輸出每位說話者跟每個人的相似度和 AMI 標註的真實身分（用說話時間重疊對應），用 Python 算出各門檻的正確率和誤認率。
- 標準：誤認（帶入錯的名字）≤ 2%，在這個前提下自動帶入的比例越高越好。
- 結果（2026-10-20，`scripts/dev/voiceprint-eval.py`）：ES2004a～d、ES2004c 的遠距麥克風（Array1-01）都分出 4 位、對到 4 個人；同一個人跟第一場的自己相似度 0.81～0.97（遠距麥克風 0.81～0.84），同組不同人最高 0.416，別組 4 場 13 位陌生人跟名單裡任何人最高 0.384。門檻 0.45～0.80 時，只用第一場學到的聲紋就能自動帶入 16/16、帶錯 0、陌生人被帶入 0。門檻取 **0.65**：離兩邊都有 0.16 以上的距離。語料是英文、耳麥和會議室麥克風；中文會議、筆電麥克風、線上會議壓縮過的聲音還沒測。
- 2026-10-22（R33）：使用者回報聲紋帶錯人。使用者的資料庫裡不同的人之間 0.63（同一支訪談的兩個人）、0.77（兩位同事），還有一位沒取名的說話者跟使用者自己像到 0.85，都遠高於 AMI 的 0.42，所以改成兩段：≥ `A` 而且跟第二像的人差 ≥ `M` 才直接帶入，`T`～`A` 改成問。AMI 9 場模擬：只用第一場學到的聲紋時 16 位裡直接帶入 8、問 8、帶錯 0；聲紋越存越多時直接帶入 15、問 1。`A` 取 0.88（0.85 會讓使用者資料裡 0.85 那位直接帶入）。`M` 擋住兩位同事那種兩個人都很像的情況，AMI 裡不影響（最像和第二像都差 0.4 以上）。

### 21.5 AI 猜說話者（R19.2，D30）

- 新的工作種類 `nameSpeakers`（`assist` 線）。轉錄完、有說話者、而且有預設名稱（沒手動改、沒被聲紋對到）的說話者時，排在校正後面。不擋整理。
- 提示詞（新的，中英日各一份，`Notes/SpeakerNaming.swift`）：會議日期、參加者、認識的人的名字（名單，給模型參考寫法）、詞彙表、逐字稿（`[時間] 說話者 N：內容`）。要模型只在有明確根據時才猜（被叫名字後回應、自我介紹、主持人點名），沒把握就不列。輸出 `{"speakers":[{"speaker":3,"name":"藍心","reason":"…","time":"12:30"}]}`。
- 存進 `speakers.suggested_name`、`suggestion_reason`、`suggestion_time`，只寫還是預設名稱的說話者。
- 畫面：說話者列有建議時顯示「猜到 2 位說話者的名字」，點開列出「說話者 3 → 藍心？」、根據（時間可以點）、「套用」「忽略」，以及「全部套用」。
- 套用 = 改名（`name_source = 'suggestion'`，會學聲紋）。忽略 = 清掉建議。

### 21.6 改名帶到會議紀錄

- 說話者改名（手動、套用建議、聲紋帶入以外的情況）後，會議紀錄裡的舊名字換成新名字：舊名字是預設名稱時用數字邊界（「說話者 1」不會換到「說話者 10」），否則整個字串取代。用 `MeetingNotes.replacing`，存成新版本，原因「說話者改名」。
- 逐字稿存的是編號，顯示時查名字，不用改。

### 21.7 偵測會議（R19.4，D31）

- `MeetingDetector`（`Services/`）：每 2 秒讀一次系統的 `kAudioHardwarePropertyProcessObjectList`，每個程序讀 `kAudioProcessPropertyBundleID`、`kAudioProcessPropertyIsRunningInput`（35 個程序左右，開銷很小；比監聽每個程序的變化簡單，程序來來去去也不用管理監聽）。不需要任何權限（2026-10-19 實測：從終端機讀得到，沒有跳出詢問）。bundle ID 用前綴比對：瀏覽器、Teams 常由 helper 程序開麥克風（例如 `com.google.Chrome.helper`）。
- 只看已知的會議 app：Zoom `us.zoom.xos`、Teams `com.microsoft.teams2`／`com.microsoft.teams`、Webex `Cisco-Systems.Spark`／`com.cisco.webexmeetingsapp`、FaceTime `com.apple.FaceTime`、Slack `com.tinyspeck.slackmacgap`、LINE `jp.naver.line.mac`、Discord `com.hnc.Discord`；瀏覽器（Chrome、Safari、Edge、Arc、Firefox、Brave）當成「可能在開 Google Meet」。系統程序（例如 `com.apple.CoreSpeech` 的語音喚醒）和拾音自己不算。
- 開始：某個會議 app 持續用麥克風 5 秒，拾音沒在錄，而且這個 app 這次的通話還沒提醒過 → 提醒「Zoom 正在開會，要開始錄音嗎？」，按鈕「開始錄音」「這次不用」「不要再提醒這個 app」（按鈕裡放 app 名稱時，名稱長的會被截掉）。
- 結束：拾音正在錄，而錄音期間用過麥克風的會議 app 都停止用麥克風 10 秒 → 提醒「會議好像結束了，要停止錄音嗎？」，按鈕「停止並轉錄」「繼續錄」。
- 提醒的方式：通知權限是允許時用通知（帶按鈕，`UNNotificationCategory`）；沒有權限時用一個不搶焦點的小浮動視窗（`NSPanel`，`.nonactivatingPanel`，右上角，30 秒後自己收起）。
- 設定（錄音分頁）：開關「偵測到線上會議時提醒錄音」（預設開）、不要再提醒的 app 列表（可以移除）。
- DEBUG：`shiyinctl meetingapp <bundleID> start|stop` 模擬某個 app 開始、停止用麥克風；啟動參數 `-debugQuietPrompts YES` 時浮動視窗不顯示（驗證時不要跳到使用者的畫面上），提醒記在 `meetingprompts`，用 `meetingaction <start|later|mute|stop|continue>` 按按鈕。

### 21.8 對會議提問（R19.6，D32）

- 單場：會議頁新增「提問」分頁。送出的內容：會議基本資料、會議紀錄（JSON）、逐字稿（`[時間] 說話者：內容`）、這場會議最近 3 組問答（追問用）、問題。輸出 `{"answer":"…","citations":[{"time":"12:30","quote":"…"}]}`。回答用問題的語言。
- 跨會議：工具列「提問」按鈕打開「問所有會議」視窗，範圍可選「所有會議／目前的資料夾／最近 30 天」。兩步：
  1. 請整理工具從問題抽出 3～8 個搜尋關鍵詞（含同義詞、中英寫法），輸出 `{"keywords":[…]}`。
  2. 每個關鍵詞用現有的全文搜尋找段落，依命中數和日期挑最多 8 場會議；每場帶會議紀錄的摘要和決議、命中段落和前後各一段，總長上限 40,000 字。請整理工具回答，輸出 `{"answer":"…","citations":[{"meetingId":12,"time":"12:30","quote":"…"}]}`。找不到相關會議就直接說找不到，不呼叫第二步。
- 問答存在 `questions`；`answer` 存 JSON。回答裡的時間點、會議都可以點。
- 用設定的整理工具（`LLMClient.generateJSON`，新的 `OutputFormat`）。CLI 可以結束子程序、API 可以取消 Task（§21.14）。不排進工作佇列（使用者在等），同時只問一題。
- 提示詞新寫（`Notes/AskPrompt.swift`），要求只根據提供的內容回答、每個重點附時間點、找不到就說沒提到。

### 21.9 回報問題（R19.7，D33）

- 「說明」選單「回報問題…」打開對話框，上面是要附上的資訊（唯讀文字），下面三個按鈕：「在 GitHub 回報」「複製」「取消」。
- 內容：版本和 build、macOS 版本、晶片、記憶體、介面語言、轉錄引擎、說話者辨識和聲紋開關、整理工具種類（不含模型、網址、key）、最近 5 個失敗的工作（種類、`error_kind`、日期）。`error_kind` 在工作失敗時寫入（`Error.logSummary`），不讀 `jobs.error`（可能含內容）。
- 「在 GitHub 回報」打開 `<issues 網址>/new?title=&body=<內容>`。網址在 Info.plist `ShiyinIssuesURL`（預設 `https://github.com/juinwei7/Shiyin/issues`），fork 的人可以改。

### 21.10 全域快捷鍵標記重點（R19.5）

- `GlobalHotKey` 改成可以註冊多組：1 是錄音（⌃⌥⌘R，沿用），2 是標記重點（⌃⌥⌘B）。事件處理用 `GetEventParameter` 取出 `EventHotKeyID` 分派。
- 標記重點只在錄音中註冊，停止就取消，不佔用按鍵。按下：在目前的錄音時間加重點，播放系統音效「Tink」，選單列圖示換成星號 1 秒。
- 設定（錄音分頁）：開關「錄音中按 ⌃⌥⌘B 標記重點」（預設開）。DEBUG：`shiyinctl hotkey bookmark`。

### 21.11 一次處理同一個詞的校正（R20.2）

- 待確認的校正依（原文、建議）分組。逐字稿上方的校正列改成列出組：「把『A』改成『B』（5 處）」「全部接受」「全部忽略」，展開看每一處（沿用原本的單筆操作）。
- `AppDatabase.acceptCorrections(ids)`、`rejectCorrections(ids)`：一個 transaction 裡改段落文字、更新搜尋索引。

### 21.12 長會議的介面（R20.3）

- 逐字稿分頁：`MeetingDetail` 載入時先算好每段的校正（字典）、段落開始時間的陣列。播放跟隨改用二分搜尋找目前的段落；跟播放時間有關的部分（目前段落的高亮、自動捲動）拆成獨立的小 view，播放時間更新時只重畫它，整份列表不重新計算。
- 錄音畫面：即時逐字稿同理，只有新增的段落會更新。
- `AppModel.perform` 依操作只重讀需要的部分：`reload(.detail(parts))` 只讀這場會議的說話者、重點、段落、校正其中幾樣，`reload(.list)` 只讀會議列表。改名、標記重點、改一段文字、接受校正不再重讀所有會議、詞彙表、範本。
- 驗證用 DEBUG 計數：逐字稿列表每秒重算幾次、每個操作讀了哪些表。

### 21.13 匯入匯出在背景（R20.4）

- 匯入：複製或搬移檔案、讀時長、解壓封包（`ditto`）在背景的 Task 做，完成後回主執行緒寫資料庫、更新列表。側邊欄下方顯示「正在匯入 N 個檔案…」。
- 匯出：寫錄音（本來就是 async）、打包封包、逐字稿的文字檔在背景。PDF 和 Word 原本用 `NSAttributedString(html:)` 同步轉換 HTML，只能在主執行緒，長會議的逐字稿會卡住畫面；改用 WebKit 的 `NSAttributedString.loadFromHTML`（非同步，轉換在 WebKit 裡做）。實測 2,086 段的逐字稿：同步轉換卡 665 ms，非同步最長卡 102 ms，產生的 Word 檔解開後逐位元組相同。匯出對話框按下後顯示「正在匯出…」，完成或失敗都有提示。

### 21.14 API 可以取消、自動重試（R20.5）

- API 的整理工具：`JobRunner` 把呼叫包在一個 Task 裡，`cancelAction` 取消這個 Task；`URLSession` 跟著取消。取消的工作照原本「已取消」處理，不算失敗、不發通知。
- `APIRequest.send` 重試一次：`URLError` 的逾時、連線中斷、沒有網路、連不上主機、找不到主機，或 HTTP 429、500、502、503、504。等待時間：有 `Retry-After` 就照它（最多 30 秒），沒有就 5 秒。其他錯誤不重試。
- DEBUG：假伺服器 `fake-llm-server.py` 加「第一次回 503、第二次正常」和「延遲 30 秒」兩種模式。

### 21.15 草稿保留（R20.6）

- `drafts` 表，key：`review:<待確認的編號>`、`segment:<段落 id>`。打字停 0.8 秒存一次，送出或取消時刪掉。
- 待確認分頁、逐字稿單段編輯：出現時讀回草稿（逐字稿的話直接回到編輯狀態）；消失、換會議時立刻存。`applicationWillTerminate` 前送出通知讓畫面上的編輯器存檔。
- 草稿是會議內容，跟會議一起存在資料庫，會議刪除時一起刪。

### 21.16 長錄音分段轉錄（R20.7，D34）

- 先量：DEBUG 指令量轉錄各階段的記憶體（`phys_footprint`）：解碼、whisper、說話者辨識。whisper.cpp 會先把整段音訊算成 mel 頻譜（large-v3 是 128×每秒 100 格的 Float32，4 小時約 740 MB），而且會議越長越大。
- 分段：whisper 每 10 分鐘一段送進去，分段點選在預定位置前後 10 秒內最安靜的 0.5 秒（每 0.05 秒一個窗，平方和最小的窗的中間）；最後一段不短於 2 分鐘（剩下的併進前一段），所以 12 分鐘以內的錄音不分段。模型只載入一次，每段各呼叫一次 `whisper_full`，直接指到原本陣列裡的位置（不複製）。
  - 提示詞：基本提示詞（含詞彙表）加上前一段最後的文字（中文、日文 120 字，英文 400 個字元），跟 whisper 自己連續轉錄時一樣（whisper 只取最後 224 個 token）。
  - 時間點加上這一段的起點，不超過這一段的結尾（最後一個 30 秒的窗會補零）。
  - 接縫：下一段開頭 3 秒內、跟前一段最後一句幾乎一樣的段落（最長共同子序列 ÷ 較長那句 > 0.8，至少 4 個字）去掉，是提示詞被念出來。
  - 錄音不先整段解碼：whisper 從檔案依序讀，每次讀到「一段＋2 分鐘」就決定這一段切在哪裡、送進去，記憶體裡最多只有約 12 分鐘的聲音（16 kHz Float32，約 46 MB）。說話者辨識仍用整段（要一致的說話者編號），轉錄完才解碼整段。切出來的位置跟整段解碼後再切完全相同。
  - 實測（M3 Pro、4 小時模擬錄音、Breeze、含說話者辨識）：記憶體峰值不分段 4,878 MB，分段、從檔案依序讀 2,852 MB（少 42%；分段但先解碼整段只少 25%）。轉錄時間不變（約 25 分鐘）。
  - 錯字率：分段後 whisper 每段各自依那一段最大聲的地方正規化頻譜，辨識結果整段都會有些不同（不只接縫）。AISHELL-4 30.5% → 29.4%、AMI 36.7% → 35.6%、國會聽證 9.2% → 9.0%、FLEURS 日文 10.9% → 11.4%、合成會議不變（短於 12 分鐘不分段）；接縫前後 60 秒的錯字率跟不分段差不多，沒有重複的段落。
  - 分聲道（還在評估）、macOS 內建辨識照舊先解碼整段：分聲道的 whisper 仍分段送進去；macOS 內建辨識本來就是串流處理。
  - 評估：`TranscriptionOptions.chunkSeconds`（預設 600，0 不分段）；`transcribe-eval` 的輸出多了 `seams`（分段的位置）和 `memory`（各階段的記憶體峰值）。
- 驗證：評估工具在 AISHELL-4（38 分鐘，會切成 4 段）和合成會議上，錯字率跟不分段相差 ≤ 0.5 個百分點；4 小時的模擬錄音量記憶體峰值。

### 21.17 第一次使用不用等下載（R20.8，D35）

- `JobRunner` 挑轉錄工作時不再等模型：轉錄引擎是 Breeze、但這個語言的模型還沒下載好，就用 macOS 內建辨識轉這一場（`TranscriptionOptions.current` 決定，沿用 `SystemTranscriber`），`meetings.transcribed_with = 'system'`。有這種會議在排隊、模型又還沒開始下載（狀態是「沒有」，不是暫停或失敗）時自動開始下載，跟日文模型一樣（§18.2）。使用者在設定選了 macOS 內建辨識的不受影響。
- 轉錄的時候寫入 `transcribed_with`（breeze、japanese、system）；匯入的逐字稿不寫。逐字稿分頁：`transcribed_with = 'system'`、設定的引擎是 Breeze、這個語言的模型已經下載好、沒有在轉錄或整理時，上方顯示「這場會議是在模型下載好之前，用 macOS 內建辨識轉錄的。可以用 Breeze 重新轉錄，通常比較準。」（日文會議是「日文轉錄模型」）和「重新轉錄」按鈕（跟「更多 → 重新轉錄」相同）。
- 首次使用引導的下載步驟說明改成「下載完成前會先用 macOS 內建辨識」；完成步驟的提示改成「會先用 macOS 內建辨識轉錄」。

## 22. 3.5 之後的修正：鑰匙圈、工具列（R21）

### 22.1 讀 API key（R21.1～R21.4，D36）

- `Keychain.read(account) throws -> String?`：沒有這個項目回傳 nil；`errSecAuthFailed`、`errSecUserCanceled`、`errSecInteractionNotAllowed`、`errSecNoAccessForItem`（使用者按「拒絕」、關掉確認視窗、不能跳視窗）丟出 `Keychain.AccessError.denied`，其他錯誤丟出 `.failed(OSStatus)`。錯誤訊息：
  - denied：「macOS 沒有讓拾音讀取鑰匙圈裡的 API key。再試一次時，系統詢問就輸入這台 Mac 的登入密碼（不是 API key），按「永遠允許」；或到設定 → 整理工具重新貼上 key。」
  - failed：「讀取鑰匙圈裡的 API key 失敗（錯誤 N）」
- 讀到的 key 記在記憶體（`OSAllocatedUnfairLock` 保護的字典），之後同一個 account 不再查鑰匙圈。`set` 成功時換成新的，`delete` 時拿掉；被拒絕、失敗不記。DEBUG 的 `llmkey` 照舊優先。
- DEBUG：沒加 `-debugRealKeychain` 時，`set`、`delete` 也只改記憶體裡的 key（以前會寫進、刪掉使用者真的鑰匙圈）。`shiyinctl keychainsim <account> denied|duplicate|-` 模擬鑰匙圈回「拒絕」「已經有舊的」，`keychainreads` 讀實際查鑰匙圈的次數，`settingsaction pickmodel|savekey <key>` 等於在設定頁按「選擇」「儲存 key」。
- `Keychain.get` 保留（`try? read`），只給不需要分辨原因的地方用。送出請求的地方（四個 API 的 `generateJSON`、OpenAI 相容 API 的請求和模型清單）改用 `try read`：讀不到就丟出上面的錯誤，OpenAI 相容 API 不會改成不帶 key 送出。
- `Keychain.set(account, value) throws`：先刪舊的再新增（照舊）。新增回傳 `errSecDuplicateItem`（舊的刪不掉，通常是舊版拾音存的、macOS 不讓這一版刪）丟出：「鑰匙圈裡已經有舊的 key，macOS 沒有讓拾音取代它。請打開「鑰匙圈存取」，刪除「拾音 API key（account）」後再存一次。」其他錯誤：「沒辦法把 API key 存進鑰匙圈（錯誤 N）」。設定頁、引導在輸入框下面用紅字顯示，輸入框的內容留著。
- 設定頁：`load()` 不再取模型清單。模型欄旁邊一律有「選擇」按鈕（API 整理工具有存 key、OpenAI 相容 API 有網址時）；按下去打開彈出視窗，清單還沒取過就先取（「正在取得模型清單…」），取完列出來，點一個就填進模型欄。取之前先 `Keychain.read`：被拒絕就在彈出視窗裡顯示錯誤，不再呼叫 `listModels`（不然會再問一次）。取不到清單：「取得不到模型清單，可以直接輸入模型名稱。」存好 key、改了網址時清掉舊清單；存好 key 時先在背景取（記憶體裡已經有新的 key，不會查鑰匙圈）。

### 22.2 工具列（R21.5）

- 側邊欄的工具列只放：錄音（和旁邊的錄音選項箭頭）、提問（問所有會議）、側邊欄開關。「匯入」移到側邊欄最下面那一列：左邊是垃圾桶，右邊是「匯入」（`square.and.arrow.down`，文字按鈕、跟垃圾桶同樣的樣式），看垃圾桶時不顯示。
- 錄音中側邊欄的工具列是「● 經過時間」、停止、提問、側邊欄開關；放不下時錄音中拿掉提問（跟原本拿掉匯入一樣）。
- 驗證：DEBUG 版、視窗 960 和 1180 寬、沒在錄音和模擬錄音中，截圖確認工具列沒有「>>」，會議頁的「更多」看得到。

## 23. 重新辨識說話者（R22，D37）

### 23.1 為什麼不改預設門檻

FluidAudio 離線辨識的分群門檻是 0.6（單位化後聲紋的歐氏距離，越大越容易併成同一人）。使用者的麻將錄音（手機遠距收音、洗牌聲）預設只分出 2 人，門檻調到 0.4 也只有 3 人，`minSpeakers = 4` 才分出 4 人；AMI 9 場用預設值都分對，調低門檻反而會把一般會議的人拆開，所以改成讓使用者填人數。

### 23.2 資料

- migration v8：`meetings.min_speakers INTEGER`（NULL 是自動判斷）。`Meeting.minSpeakers: Int?`。
- `Diarizer.diarize(samples:minSpeakers:)`：有人數時用 `OfflineDiarizerConfig.Clustering.community` 加上 `minSpeakers`（只在自動判斷的人數比較少時才作用，人更多時照樣分出來）。沒有人數時照舊用預設的 manager。
- 轉錄（R22.3）：`TranscriptionOptions.minSpeakers` 從會議帶進來，重新轉錄也用同一個人數；評估 JSON 也可以給。

### 23.3 工作

`JobKind.rediarize`：在轉錄線（不跟轉錄同時跑），算輔助工作（不改會議的狀態、不發通知），會議頁上方的輔助工作列顯示「正在重新辨識說話者…」和「取消」。

1. 解碼錄音，用這場的 `min_speakers` 跑說話者辨識。
2. 正式逐字稿（kind = final）照轉錄時同樣的規則（`Diarizer.assignNumbering`）重新標說話者。段落的文字、校正、重點、會議紀錄都不動。
3. 名字沿用（R22.2）：新的說話者 n 的段落（依秒數算）有 60% 以上原本是同一位取過名字的說話者 o（名字來源不是預設），而且 o 原本的段落分到 n 的最多，n 就沿用 o 的名字、名字來源、連到的人。
4. 一個交易裡：更新段落的說話者；刪掉這場會議存過的聲紋（舊的分群可能把兩個人混在一起）；重建這場的說話者（沒沿用的用預設名稱、清掉建議，聲紋、說話秒數、收音照新的結果）；清掉沒有聲紋也沒連到說話者的人。
5. 沿用名字而且連到人的，用新的聲紋重新學（`learnVoiceprint`）；聲紋開著時，其他預設名稱的說話者跟認識的人比對（`matchVoiceprints`）。
6. 整理工具不是「不整理」、還有預設名稱的說話者時，排「猜說話者」。
7. 完成後如果使用者正在看這場，提示「已重新辨識說話者：分出 N 位。名字請再確認一下；會議紀錄要更新的話，請重新整理會議紀錄。」

### 23.4 介面

- 會議頁「更多」選單：「重新轉錄（含說話者辨識）」下面加「重新辨識說話者…」，打開一個小視窗：
  - 標題「重新辨識說話者」；說明「只重新分辨誰在說話，逐字稿的文字不會變。人多、吵雜或手機遠距收音時，告訴拾音大約幾個人會分得比較準。」
  - 「大約幾個人」：自動判斷、2～12 人（預設是這場記住的人數，沒有就自動判斷）
  - 「取消」「開始」
- 不能做的情況（R22.4）：沒有錄音、沒有正式逐字稿、正在轉錄或整理、分聲道轉錄（有段落的 channel 不是空的）、已經在重新辨識。跟「更多」選單其他項目一樣不預先停用（工具列的選單建立一次就不會更新），按下時檢查，不能做就說明原因。
- DEBUG：`rediarize <會議 id> <人數|auto>`（跟按「開始」同一個函式）。

## 24. 說話者的修正、分不準時提醒（3.6，R23，D38、D39）

### 24.1 資料（migration v9）

- `segments.speaker_manual INTEGER NOT NULL DEFAULT 0`：使用者改過說話者的句子（R23.2）。
- `meetings.speaker_check REAL`：說話者辨識的自我檢查分數（§24.5），NULL 是沒算過（3.6 以前轉錄的會議）。
- `meetings.speaker_hint_dismissed INTEGER NOT NULL DEFAULT 0`：按過「不用了」。重新轉錄、重新辨識說話者時重設。
- `speaker_undo(meeting_id PRIMARY KEY, snapshot TEXT, label TEXT, created_at TEXT)`：最近一次合併或改句子之前的樣子（JSON：正式逐字稿每段的說話者和 `speaker_manual`、這場的 speakers 列；合併換了會議紀錄的名字時，加上換之前、換之後的會議紀錄）。label 是「更多」選單復原時顯示的說明。
- 復原資料什麼時候清掉：重新轉錄（`saveTranscript`）、重新辨識說話者（`reassignSpeakers`）、改名（`renameSpeaker`）、「不是他」。改名之後還能復原的話，會把名字一起換回去，跟已經換過名字的會議紀錄對不上。

### 24.2 合併說話者（R23.1）

- 說話者的改名小視窗，左下角加「合併到…」選單（這場其他的說話者）。選了就合併，不再確認（可以復原）。
- 資料（一個交易）：
  1. 會議紀錄裡來源的名字換成目標的名字（跟改名同一個規則，§21.6），有改到就存一版（原因「合併說話者」）。
  2. 存復原資料。
  3. 來源的段落都改成目標；目標的說話秒數加上來源的；聲紋依說話秒數加權平均後單位化（只有一邊有就用那一邊）。
  4. 刪掉來源的 speakers 列和這場它存過的聲紋，清掉沒有聲紋也沒連到說話者的人。
- 目標的名字是使用者取的或套用建議的（manual、suggestion）：用合併後的聲紋重新學（`learnVoiceprint`）。
- 提示「已把「A」合併到「B」。要復原的話，從「更多 → 復原說話者的調整」。」
- 目標保留自己的名字和編號；其他說話者的編號不重排（會議紀錄、匯出裡的「說話者 3」不會變成別人）。

### 24.3 改句子的說話者（R23.2，D38）

- 逐字稿每段的右鍵選單加：
  - 「這句是誰說的」：子選單列出這場其他的說話者，分隔線後是「新的說話者」。
  - 「這段連續的話是誰說的（N 句）」：只在前後相鄰的段落是同一位說話者時出現。連續是看逐字稿的順序（`seq`），不看時間間隔。
  - 只有正式逐字稿、有說話者的會議才有。
- 資料：段落改成選的說話者、`speaker_manual = 1`。「新的說話者」用這場最大的編號 +1、預設名稱（會議語言）、沒有聲紋。改完沒有段落的說話者拿掉（連同這場的聲紋）。
- 不重算聲紋和說話秒數（要再跑一次聲紋模型，R23 不做）；會議紀錄不動（句子層級的改動，會議紀錄裡的名字還是對的人）。
- 重新辨識說話者時（§23.3 第 2 步之後）：
  - 名字沿用只看沒改過的段落。
  - 改過的段落不照新的分群：原本的說話者 o 沒改過的段落最多分到哪一位新的說話者 n，改過的段落就給 n。
  - o 沒有沒改過的段落時（例如「新的說話者」只有改過的句子），另外新增一位說話者，使用者取過的名字沿用，預設名稱就用新的編號。
- 轉錄、重新辨識說話者在跑的時候不能改（完成時會整份寫回），說明「正在處理這場會議，完成後才能改說話者」。

### 24.4 復原

- 「更多」選單「重新辨識說話者…」下面加「復原說話者的調整」。跟其他項目一樣按下時才檢查：沒有可以復原的就說明「沒有可以復原的說話者調整（改名、重新辨識說話者之後就不能復原之前的調整）」。
- 復原：段落的說話者和 `speaker_manual`、這場的 speakers 列換回快照（快照裡連到的人已經不在了就不連）。合併時換過名字的會議紀錄，如果現在還是換之後的樣子、最新的版本是換之前的，就拿掉那一版換回去；之後又改過會議紀錄的話不動。
- 聲紋不還原：合併時刪掉的聲紋，下次改名或重新辨識會再學。
- 只留最近一次（R23 不做多層復原）。

### 24.5 分不準時提醒（R23.3，D39）

FluidAudio 的離線辨識把錄音切成 10 秒的片段（每 2 秒一個），先在每個片段裡分出最多 3 位說話者、各算一個聲紋，再把所有片段的聲紋分群（`exposeChunkEmbeddings` 可以拿到每個片段的聲紋和它被分到哪一群）。

- 「碰撞率」（同一個片段裡分出兩位、卻被分到同一群的比例）：分群時同一個片段的不同說話者會被強制分到不同群，只要群數 ≥ 2 就不會碰撞（麻將只分出 2 群也是 0%），所以只在只分出 1 群時有用。
- 「凝聚度」（每個片段的聲紋跟它那一群平均聲紋的 cosine，所有片段平均）：不同的人被併在同一群時，群裡的聲紋散得開，凝聚度變低。15 段錄音（自動判斷）：AMI 6 場（含遠距陣列麥克風）0.80～0.87、AISHELL-4（遠距收音）0.79、使用者的週會、研發週會、FonTicket 0.71～0.79；麻將（6～8 人只分出 2 群）0.48。
- 使用者的排隊取號、幼貓認養（對話）只分出 1 群、碰撞率 100%，不是誤報：用「至少 2 人」重新辨識，多出來的那位說的是另一個人的話，預設設定把兩個人併成一位。麻將用「至少 6 人」分出 6 位後凝聚度也只有 0.55：吵雜、遠距收音本來就散，靠「選過人數就不提示」處理。
- 分數（`Diarizer.Quality.mergeRisk`，越大越可能把人併在一起）：
  - 片段不到 30 個（大約 1 分鐘的說話）：0，不判斷。
  - 只分出 1 群、至少 10 個片段分得出兩位、碰撞率 ≥ 50%：1。
  - 其他：1 − 凝聚度。
- 門檻 0.4（凝聚度低於 0.6）：一般會議最低 0.71（使用者的週會），麻將 0.48。分數存在 `speaker_check`，門檻寫在 `Diarizer.Quality.hintThreshold`，之後換門檻不用重算。
- 顯示：逐字稿分頁搜尋列下方（跟「用 Breeze 重新轉錄」同樣式）：「說話者可能分得不夠：有些不同的人可能被當成同一位。人多、吵雜或手機放在桌上錄音時常發生，告訴拾音大約幾個人會分得比較準。」按鈕「不用了」「重新辨識說話者…」（打開 §23.4 的小視窗）。
- 不顯示：使用者選過人數（`min_speakers` 不是 NULL）、按過「不用了」、正在轉錄或整理、分聲道轉錄、沒有正式逐字稿。
- 自我檢查在轉錄和重新辨識說話者時算，跟說話者辨識同一次（不另外跑模型）。

### 24.6 DEBUG

- `mergespeaker <會議 id> <來源> <目標>`、`reassign <會議 id> <說話者|new> <段落 id…>`、`undospeaker <會議 id>`：跟按選單同一個函式（會議要是選取中的那場）。
- `voiceprint-eval` 的設定可以加 `dump`：倒出每個片段的聲紋（`.f32`）和群、片段、片段裡的說話者（`.json`），用來離線試判斷方法。
- state 加 `speakerCheck`、`showsSpeakerHint`、`speakerUndo`。


## 25. 錄音中的畫面（R25，D40）

### 25.1 調查

- 使用者用 3.5.0 錄 71 分鐘的會議，錄到 3:53 畫面就停了：主執行緒 100% CPU，一個多小時都沒恢復，錄音照常寫檔。用同一場錄音模擬錄音重現不出來。
- 主執行緒幾乎都在整個視窗重新排版（`NSHostingView.layout` → 重算視窗的最小尺寸 → 把整個畫面量一遍）：SwiftUI 裡任何一個 view 變了，整個視窗都要走一次。同一段錄音的 CPU：3.5.0 是 59%；關掉即時逐字稿 39%；再讓音量條不動 10%。音量條說話時每秒變十幾次，是最大的來源；即時文字每秒約 3 次。
- 推測：主執行緒本來就接近滿載，其他程式搶 CPU 或排版陷入循環時，就再也追不上。即時逐字稿用「最底下的標記出現、消失」改狀態，是在排版途中改，有讓排版一直重來的風險。

### 25.2 改法

- 音量條（D40）：`LevelMeter`，AppKit 的 view，自己每 0.08 秒讀 Recorder 的音量，亮的格數變了才改圖層的顏色；最後三格琥珀色，跟原本的 `VUMeter` 一樣。Recorder 的 `micLevel`、`systemLevel` 不讓 SwiftUI 觀察。DEBUG 截圖（ImageRenderer 畫不出 AppKit 的 view）時還是用 `VUMeter`。
- 計時：`elapsedSeconds` 整秒變了才更新；錄音畫面、工具列、選單列的計時用 `RecorderClock`，在自己的 body 裡讀。精確的 `elapsed`（筆記的時間點、標記重點）不讓 SwiftUI 觀察。
- 其他每 0.05 秒設一次的觀察屬性（`systemSilent`）只在值變了才設：`@Observable` 設成同樣的值也會讓畫面重算。
- 跟著最新（R2.2）：改看捲動的位置（`onScrollGeometryChange`）。往上捲（內容沒有變短，排除定稿時文字變短被往下推）就不跟；回到底部就跟。滾輪、觸控板、拖捲軸都一樣。按「回到最新」捲到底部並繼續跟。

### 25.3 結果

- 同一段錄音，即時逐字稿開著：CPU 59% → 22%（DEBUG 版，量 120 秒）。模擬錄音 30 分鐘一直有回應。
- DEBUG：`livescroll <0～1>` 把即時逐字稿捲到某個位置（等於拖捲軸）；`metrics` 的 `volatileUpdate` 是即時文字更新的次數。

## 26. 錄音前就減少錯誤（3.7，R24，D41）

### 26.1 收音提醒（R24.1、R24.3，D41）

- 量法：麥克風那一聲道，每 100 毫秒算一次音量（RMS，dBFS）。每秒看最近 30 秒：第 90 百分位當「說話的音量」，第 10 百分位當「背景」。在 `RecordingWriter` 寫檔的佇列上算（已經有每段的 RMS），Recorder 每秒讀一次結果。電腦聲音不看（它有自己的「超過 15 秒沒有收到系統聲音」）。
- 評估（`afconvert` 轉成 16 kHz，Python 照同樣的算法，最多 30 分鐘）：使用者的 8 場會議（筆電、手機）、AMI、AISHELL-4 的背景都不會持續超過 −36 dBFS，麻將（洗牌聲）持續 72 秒、加了粉紅雜音的整段都超過；說話 < −50 dBFS 一般錄音最長持續 30 秒，調小 20、26 dB 的版本 490 秒以上。使用者的會議說話和背景只差 8 dB 左右，跟加雜音的版本（6 dB）很接近，所以不用「說話和背景差多少」判斷吵不吵，只看背景本身多大聲、持續多久。
- 規則（同一種提示一場錄音只出現一次；暫停時不判斷、重新計時）：
  - 沒有收到聲音：麥克風的聲音完全是 0 超過 10 秒 →「麥克風沒有收到聲音：可能被靜音、被其他 app 佔用，或選錯了麥克風。可以在設定的「錄音」換一個。」
  - 背景太吵：背景 > −36 dBFS 持續 45 秒 →「背景有點吵，說話者可能分不準。錄完可以用「重新辨識說話者」告訴拾音大約幾個人。」
  - 聲音太小：說話的音量 < −50 dBFS、而且比背景大 4 dB 以上（有人在說話，不是完全安靜）持續 60 秒 →「聲音有點小：手機或筆電放近說話的人一點。」一般錄音最長 30 秒（AMI 一段安靜的時間），調小 20 dB 的版本 260 秒以上。
- 顯示：錄音畫面控制列下方的提示列（跟自動偵測語言同樣的樣式），右邊「知道了」。關掉的那一種這場錄音不再出現。
- DEBUG：state 的 `recorder.levelCheck`（說話、背景、各條件持續的秒數、目前的提示）；`levelhint dismiss` 等於按「知道了」。

### 26.2 先填人數（R24.2）

- 匯入對話框加「大約幾個人」（自動判斷、2～12 人），跟範本、會議語言放在一起；`ImportOptions.minSpeakers` 建立會議時存進 `min_speakers`，轉錄時就用（§23.2）。一次匯入多個檔案時每一場都用同一個人數。
- 錄音中：會議標題右邊（範本、會議語言旁）加「人數」選單：自動判斷、2～12 人，只在錄這場會議時出現。選了就存進 `min_speakers`，錄完轉錄時用。
- DEBUG：`import minSpeakers=6 <檔案>`、`minspeakers <會議 id> <人數|auto>`。

## 27. 待確認的是非題（R32，D90）

- 結構化輸出：`reviewItems` 每題加 `yesNo`（boolean，跟其他欄位一樣 required）。整理的提示詞規則 6 後面加一句（中、英、日三份各自的寫法）：能用是或否回答的題目寫成是非題、`yesNo` 填 true；要使用者說出答案的填 false。依回答修正（校稿）的提示詞不用改：沒回答的題目照原樣保留，`yesNo` 跟著留著。
- 存檔：`yesNo` 是 false 時不寫進 JSON，以前的會議紀錄、沒有是非題的會議紀錄存起來跟以前一字不差；讀取時沒有這個欄位就是 false。
- 畫面（待確認分頁，Mac 和 iPhone 同一份 `ReviewTab`）：是非題在題目下面放分段的「是」「不是」（還沒選時兩個都不亮）和「略過」；選了「不是」才出現輸入框「正確的是…（可以不填）」。不是是非題的照舊是「你的回答」輸入框。
- 回答的文字：依會議語言，是 → 「是」「Yes」「はい」；不是 → 「不是」「No」「いいえ」；不是而且有填答案 → 「不是，答案」「No, 答案」「いいえ、答案」。這段文字就是存起來的草稿（§21.15）和送給校稿的回答；讀回草稿時照這個格式認回按鈕的狀態，認不出來的（例如舊的草稿）照舊顯示成輸入框。
- 頁首的說明在有是非題時改成「按『是』『不是』或直接回答就好」。使用說明的待確認那一段也提到按鈕。
- 跨平台：Windows 照同樣的規則改 `MeetingNotes`、提示詞、待確認頁；iPhone 共用 Mac 的程式。結構化輸出和提示詞在標準答案裡，三邊不一致時 Windows 的測試、iPhone 的 `golden.sh` 會失敗。

**說話者題目**（R33）：
- 待確認分頁最上面列出說話者的建議（`speakerSuggestions`：聲紋沒把握的、AI 猜的，§21.4、§21.5），每位一題：「『說話者 3』是 weiwei 嗎？」，下面一行是原因（AI 猜的有時間點）。分段的「是」「不是」和「略過」。選「不是」時出現「是…」選單（這位說話者最像的 3 位認識的人，不含建議的那位）和輸入框「是誰？（可以不填）」。
- 送出：先把整理工具要處理的題目送出（題目文字可能有說話者的名字，改名後會變），再處理說話者題目：「是」= 套用建議（`renameSpeaker`，`source = 'suggestion'`，學聲紋、改會議紀錄裡的名字）；「不是」而且選了或打了名字 = 改成那個名字（`source = 'manual'`）；「不是」沒填、略過 = 清掉建議。送出鈕和題數算進說話者題目。
- 草稿（§21.15）：`review-speaker:<說話者編號>`，文字格式同是非題的回答（「是」「不是，名字」，依會議語言）。
- 題數：會議清單的「待確認 N 題」、分頁的數字 = 會議紀錄的待確認 + 說話者建議（還是預設名稱、有 `suggested_name` 的說話者）。
- AI 猜說話者（§21.5）不覆蓋已經有建議的說話者（聲紋的建議優先：有聲音當根據）。
- 會議頁的「猜到 N 位說話者的名字」提示照舊（說話者建議的另一個入口），文字改成不提「從對話內容」。

**一步改正**（R33.4）：點「自動」的名字，選單是「是別人：」下面列這位說話者最像的 3 位認識的人（`voiceprintCandidates`：跟每個人所有聲紋的最高相似度，由高到低，不含目前的名字）、「其他人…（到逐字稿改名）」，以及「不是X（不知道是誰）」。選了某人 = `renameSpeaker`（`source = 'manual'`）：刪掉這位說話者在這場存的聲紋，存到選的人，會議紀錄裡的名字一起換。最像的名單在載入會議時跟說話者一起算（`MeetingDetail.speakerCandidates`），不在畫面更新時重算。

## 30. 匿名使用統計（R40，D100）

使用者想知道活躍度和處理了多少語音（requirements 第 30 節）。每天一份匯總，送到使用者自己開發的後台；不含任何會議內容。

### 30.1 累計什麼、掛在哪裡

`Services/UsageStats.swift` 的 `UsageStats.shared`（有鎖，任何執行緒都能呼叫）。累計到「今天」（本機時區的日期）那一份：

| 欄位 | 什麼時候加 | 掛在哪裡 |
|---|---|---|
| `meetingsRecorded`、`recordedSeconds` | 錄音存檔成功（有聲音、進了轉錄佇列） | `AppModel` 錄音結束的流程 |
| `meetingsImported` | 匯入音訊或影片成功 | `Importer.importFile` |
| `importedSeconds` | 匯入的會議轉錄完成，加上它的長度 | `JobRunner.transcribe`（`meeting.source == "import"`） |
| `transcribedSeconds`、`transcribeWallSeconds` | 轉錄完成：語音長度、從開始到完成的實際秒數（含說話者辨識） | `JobRunner.transcribe` |
| `engines.breeze`／`builtin`／`whisperJa` | 轉錄完成，依 `transcribedWith`（breeze／system／japanese）加語音秒數。`builtin` 是系統內建或快速辨識：Mac 是 macOS 內建辨識，Windows 是快速辨識（SenseVoice），報表依平台顯示名稱 | 同上 |
| `meetingLanguages.zh`／`en`／`ja` | 轉錄完成，依會議語言加一場 | 同上 |
| `diarizationRuns` | 轉錄時有做說話者辨識、重新辨識說話者完成 | `JobRunner.transcribe`、`rediarize` |
| `summarizer.claudeCode`／`codex`／`api`／`local` | 整理（含草稿、回答待確認後的修正）成功；Anthropic、OpenAI、Gemini、遠端的 OpenAI 相容 API 算 `api`，網址是 localhost、127.0.0.1 的 OpenAI 相容 API 算 `local` | `JobRunner.execute` |
| `questionsAsked` | 提問（單場或所有會議）得到回答 | `Asking` |
| `errors.recording`／`transcribe`／`diarize`／`summarize` | 錄音結束時有錯誤；轉錄、重新辨識、整理（含修正）的工作失敗 | `AppModel`、`JobRunner.execute` |
| `review.answered`／`confirmed`／`skipped` | 待確認送出成功（R41.7，§31.3）：打字或「不是」的題數、「是」的題數、略過的題數 | `AppModel.submitReview` |

校正、猜說話者、壓縮不算（是整理和轉錄的輔助工作）。

### 30.2 存在哪裡

`SHIYIN_HOME/usage.json`（不放 UserDefaults：DEBUG 版跟使用者的拾音共用 UserDefaults，換 `SHIYIN_HOME` 測試時數字才不會混在一起）：

```json
{ "installId": "隨機 UUID", "days": { "2026-10-22": { …30.1 的欄位… } }, "sent": ["2026-10-21"] }
```

- 安裝 ID 第一次累計時用 `UUID()` 產生；「重設匿名 ID」換一個新的。
- 每次累計後寫檔（原子寫入）；檔案讀不懂就當作沒有、重新開始。
- 關掉開關、或沒有統計網址時：不累計，`days` 清空。
- `days` 只留最近 4 天；`sent` 只留最近 7 天，用來確保同一天不送兩次。

### 30.3 什麼時候送

- 啟動 60 秒後檢查一次，之後每小時檢查一次（跟 app 內更新一樣，`Updater.scheduleAutomaticChecks`）。
- 要送的是**今天以前、最近 3 天內、還沒送過**的日子，一天一個請求，從舊到新。有開 app 但沒有用量的日子也有一份（打開 app 時就建立當天的空白匯總），才算得出活躍天數；沒開 app 的日子沒有資料。
- 回 2xx：記進 `sent`、刪掉那一天。回 4xx（429 除外）：格式被拒，刪掉那一天、不再送，log 記一筆。網路錯誤、5xx、429：留著，下一小時再試；超過 3 天就刪掉。
- 同時只有一個請求；逾時 20 秒；只用 https（localhost、127.0.0.1 例外給測試用，跟更新一樣）。
- 送統計不在主執行緒做事、失敗不顯示任何訊息。

### 30.4 送出的格式（給後台）

```
POST <統計網址>
Content-Type: application/json
User-Agent: Shiyin/<版本>

{
  "schema": 1,
  "installId": "2f7c3a52-…",
  "date": "2026-10-22",
  "app": { "platform": "mac", "version": "3.7.0", "os": "macOS 26.1", "chip": "Apple M3 Pro", "uiLanguage": "zh-Hant" },
  "usage": {
    "meetingsRecorded": 2, "recordedSeconds": 4210,
    "meetingsImported": 1, "importedSeconds": 1800,
    "transcribedSeconds": 6010, "transcribeWallSeconds": 520,
    "engines": { "breeze": 5800, "builtin": 210, "whisperJa": 0 },
    "meetingLanguages": { "zh": 2, "en": 1, "ja": 0 },
    "diarizationRuns": 3,
    "summarizer": { "claudeCode": 3, "codex": 0, "api": 0, "local": 0 },
    "questionsAsked": 2,
    "review": { "answered": 1, "confirmed": 3, "skipped": 1 }
  },
  "errors": { "recording": 0, "transcribe": 0, "diarize": 0, "summarize": 1 }
}
```

- 秒數都是整數；沒有發生的欄位是 0（欄位一定都在）。`app` 是**送出當下**的環境（不是那一天的），一天之內換版本不另外分。
- `platform` 是 `mac` 或 `windows`（2026-10-22 加，分平台看報表用，不用從 `os` 字串猜）。`os` 用 `ProcessInfo.operatingSystemVersion`（例如 `macOS 26.1`）；`chip` 用 `sysctl machdep.cpu.brand_string`；`uiLanguage` 是 app 實際用的介面語言（`Bundle.main.preferredLocalizations.first`）。
- 後台收到回 `204`（或任何 2xx）。用 `installId` + `date` 去重：同一筆重送（例如回應在路上掉了）要覆蓋，不要重複算。
- 後台不要存 IP（或只用來擋濫用、不跟 `installId` 一起存）。`schema` 以後改格式時加 1，後台可以依版本解析。
- `review`（2026-10-23 加，R41.7）：待確認的回答、確認、略過的題數。只加字典不改 `schema`：後台對不認得的字典忽略、少的鍵當 0，所以舊 app 和舊後台互相都能收。

### 30.5 設定頁、使用說明

- 設定 → 進階的**最下面**（環境檢查、其他按鈕之後）只放一行：「匿名使用統計」和「詳細…」按鈕（2026-10-22 使用者要求藏深一點，原本是一整段開關和說明）。按「詳細…」打開小視窗「匿名使用統計」：
  - 開關「分享匿名使用統計」（`usageStats`，預設開）；關掉時清掉還沒送的資料、打開時建立今天的匯總（同之前）
  - 說明：「每天送一份匿名的使用統計給拾音的開發者：版本、系統、錄音和轉錄了幾分鐘、用了哪些功能、失敗幾次。不會送會議標題、逐字稿、會議紀錄、人名、聲紋或任何檔案。」
  - 不顯示送出的 JSON（2026-10-22 使用者決定拿掉原本的「查看會送出的內容…」；收什麼寫在說明和 README，DEBUG 用 `shiyinctl usage` 看）
  - 底部左邊「重設匿名 ID」（按了在旁邊顯示「已重設匿名 ID」），右邊「完成」
- 使用說明「資料與隱私」加一條：「每天送一份匿名的使用統計（版本、系統、錄音和轉錄的分鐘數、用了哪些功能、失敗次數），不含任何會議內容。在設定 → 進階可以關掉。」
- 這兩處只在 app 有統計網址時顯示（DEBUG 版一律顯示，方便驗證）：沒有網址的版本本來就不送，不寫會送。

### 30.6 統計網址（建置時寫進 app）

- 跟更新網址一樣（§12）：`SHIYIN_USAGE_URL=https://…` 讓 `build-app.sh` 寫進 Info.plist 的 `ShiyinUsageURL`；`make-dmg.sh`、`release.yml` 一起帶（GitHub 的 repository variable `SHIYIN_USAGE_URL`）。
- 設定裡不能改（R40 不做）。沒有設 variable 的 release 就是不送統計的版本。

### 30.7 DEBUG 版、驗證

- DEBUG 版忽略 Info.plist 的統計網址；只在 `shiyinctl usageurl <網址>` 設在記憶體時累計和送（`usageurl off` 取消）。
- `shiyinctl usage` 回傳目前的 `usage.json` 和下一份要送的內容；`usageage <天數>` 把今天的匯總改成幾天前（模擬時間過去）；`usagesend` 馬上跑一次送出的檢查。
- 假後台：`scripts/dev/fake-usage-server.py <埠> <紀錄檔>`，把收到的每個請求（標頭、內容）寫進紀錄檔；可以指定回應碼（測 4xx、5xx）。

### 30.8 隱私說明的新寫法（發布有統計網址的那一版時一起改）

- README（中文，「資料與隱私」表格加一列；「整理工具選『不整理』｜完全離線」改成「…，並關掉匿名使用統計」）：`匿名使用統計`｜每天送一份給拾音的開發者：版本、系統、錄音和轉錄的分鐘數、用了哪些功能、失敗次數，加上一個隨機的匿名 ID。不含會議標題、逐字稿、會議紀錄、人名、聲紋或任何檔案。設定 → 進階可以關掉。
- README（英文；“Don’t summarize” 那一列同樣改成 “… with usage statistics turned off”）：`Anonymous usage statistics` | Once a day, Shiyin sends its developer an anonymous summary: version, system, minutes recorded and transcribed, which features were used, and how many tasks failed, with a random anonymous ID. It never includes meeting titles, transcripts, notes, names, voiceprints or files. You can turn it off in Settings → Advanced.
- 官網（首頁的隱私區塊，中英日；Mac 首頁和 Windows 頁都加，`content.mjs` 的 `privacy.usage`）：整理工具那一段後面加一句：「另外，每天會送一份匿名的使用統計給開發者（例如版本、用了幾分鐘；不含任何會議內容），在設定 → 進階可以關掉。」英文：「Shiyin also sends its developer a daily anonymous usage summary (such as the version and minutes used, never any meeting content). You can turn it off in Settings → Advanced.」日文：「また、匿名の利用統計（バージョンや利用時間など。会議の内容は含みません）を 1 日 1 回開発者に送ります。Settings → Advanced でオフにできます。」（日文版的介面名稱照 app 的英文介面寫）
- requirements 第 4 節已經改好（2026-10-22）。
- 版本說明：不寫（2026-10-22 使用者決定；原本規劃 `v4.0.0`、`win-v4.0.0` 各加一條「新增匿名使用統計…」）。收什麼寫在使用說明、README、官網的隱私說明和設定的「詳細…」

### 30.9 Windows 版

Windows 版做一樣的功能（R40.7，跟 4.0.0 一起發）：同樣的格式（`app.platform` 是 `windows`、`app.os` 寫 `Windows 11 …`、`chip` 寫 CPU 名稱；`engines.builtin` 是快速辨識 SenseVoice 轉錄的秒數）、同樣的時機和保留規則，資料檔一樣放 `SHIYIN_HOME/usage.json`，設定放 `settings.json`。跟 Mac 不同的地方（統計網址怎麼寫進 app、程式放哪裡）見 design-windows §12.10。

### 30.10 後台：Cloudflare Workers＋D1（D101）

- 程式：`workers/usage/`（`worker.js` 純 JavaScript、不用套件；`schema.sql`；`wrangler.toml`）。網址 `https://usage.weibuilds.com/v1/usage`（Workers 的自訂網域，跟官網分開：官網維持純靜態，兩邊部署、出問題都不互相影響）。
- `POST /v1/usage`：
  - 只收 `Content-Type: application/json`、內容 8 KB 以內，其他回 400／413／415
  - 檢查 §30.4 的格式：`schema` 是 1；`installId` 是 UUID；`date` 是今天前 7 天到明天（UTC）之間；`app.platform` 是 `mac` 或 `windows`；`app` 的其他四個欄位是 64 字以內的字串；`usage`、`errors` 的數字是 0～1,000,000 的整數；字典只認得 §30.4 列的鍵（多的忽略、少的當 0）。不對就回 400（app 收到 4xx 會丟掉那一天，不再重送）
  - 寫進 D1：`installId`＋`date` 是主鍵，重送就覆蓋（`INSERT … ON CONFLICT DO UPDATE`），所以同一台同一天只算一次
  - 成功回 204
- `GET /v1/health`：回 200，部署後檢查用。其他路徑 404。
- 不存 IP、不存 User-Agent 以外的標頭（User-Agent 也不存，版本已經在內容裡）。Cloudflare 本身的請求紀錄不在我們的資料庫。
- 防濫用：內容檢查加上 Workers 的頻率限制（同一個 IP 每分鐘 30 次，超過回 429；`wrangler.toml` 的 `ratelimits`）。程式開源、網址公開，沒辦法藏密鑰，只能這樣擋。
- 資料表：

  ```sql
  CREATE TABLE usage_daily (
    install_id TEXT NOT NULL, date TEXT NOT NULL,
    platform TEXT NOT NULL, app_version TEXT, os TEXT, chip TEXT, ui_language TEXT,
    meetings_recorded INTEGER, recorded_seconds INTEGER, meetings_imported INTEGER, imported_seconds INTEGER,
    transcribed_seconds INTEGER, transcribe_wall_seconds INTEGER,
    engine_breeze INTEGER, engine_builtin INTEGER, engine_whisper_ja INTEGER,
    lang_zh INTEGER, lang_en INTEGER, lang_ja INTEGER, diarization_runs INTEGER,
    summarizer_claude_code INTEGER, summarizer_codex INTEGER, summarizer_api INTEGER, summarizer_local INTEGER,
    questions_asked INTEGER,
    review_answered INTEGER, review_confirmed INTEGER, review_skipped INTEGER,   -- 2026-10-23 加（R41.7）
    errors_recording INTEGER, errors_transcribe INTEGER, errors_diarize INTEGER, errors_summarize INTEGER,
    received_at TEXT NOT NULL,
    PRIMARY KEY (install_id, date)
  );
  ```

  已經建好的資料庫要加欄位時，另外放一個 `migrate-<名稱>.sql`（`ALTER TABLE … ADD COLUMN … NOT NULL DEFAULT 0`）用 `d1 execute --remote` 跑一次；`schema.sql` 同時改，新建的資料庫才一致。

  欄位攤平成一欄一個數字，用 SQL 直接加總。例如每日活躍 `SELECT date, COUNT(*) FROM usage_daily GROUP BY date`；總轉錄時數 `SELECT SUM(transcribed_seconds) / 3600.0 FROM usage_daily`。
- 部署：`npx wrangler@4 d1 create shiyin-usage`、`d1 execute --remote --file schema.sql`、`deploy`。token 要有 Workers Scripts、D1 的編輯權限，自訂網域另外要 weibuilds.com 的 Workers Routes、DNS 編輯權限；token 只在部署時用環境變數帶，不寫進任何檔案。
- 本機測試：`npx wrangler@4 dev --local`（本機的 D1，`--persist-to` 放暫存資料夾），DEBUG 版用 `shiyinctl usageurl http://127.0.0.1:8787/v1/usage` 指過去。

## 31. 待確認就地回答、收音提醒送通知（R41、R42，D102～D104）

### 31.1 題目對應到紀錄（R41.1，D102）

- 載入會議時算一次（跟 `MeetingDetail.speakerCandidates` 一樣，不在畫面更新時重算）：`MeetingDetail.reviewLinks: [NoteItemRef: [Int]]`。`NoteItemRef` 指一條紀錄：段落（summary、decisions、actionItems、openQuestions、某個 topic 的 point、某個 custom 段落的 item）加位置。值是題號（`reviewItems` 的 index，也就是畫面上的順序）。
- 規則：題目的 `time` 和條目的 `time` 都用 `TimeFormat.seconds` 轉成秒，空的或轉不出來的不對應。先找同一秒的條目，有幾條標幾條（同一個時間點的條目通常在講同一件事，例如摘要和決議各一條）；沒有的話找時間差最小、差距 ≤ 10 秒的一條（2026-10-24 從 20 秒收緊：示範和標準答案裡對得對的都在 5 秒內，差 4 秒就已經有對錯的例子，再遠只會更不準）。議題的標題（`topics[].start`）不算條目，只有要點算。
- 一條可以對到多題。對不到的題目只在待確認分頁。
- 題目的 index 在送出、重新整理、復原後都會變（§21.15 說過），`reviewLinks` 跟著 `MeetingDetail` 重新載入就重算。

### 31.2 畫面（R41.2、R41.3）

- `ItemList`、`ActionItemsTable`、`TopicsTab` 的每一條，在時間點之後加 `ReviewMarker`：圓形琥珀色（`Color.signal`，跟側欄的「待確認 N 題」同色）的「？」，對到多題時顯示「？2」。`.help` 寫「整理時有 N 題不確定的地方，點了就能回答」。
- 點了在那一條下面展開 `InlineReviewCard`：列出對應的題目，每題用 `ReviewQuestionRow`（從 `ReviewTab.reviewRow` 抽出來，兩邊共用：題號、時間點、題目、是非或輸入框、略過），右下「送出」（文字同待確認分頁的 `submitTitle`）和「收合」。同時只展開一條，點別條就收合前一條。
- 回答的狀態從 `ReviewTab` 的 `@State` 搬到 `ReviewDraft`（`@Observable`，掛在 `AppModel` 上，換會議就換一份）：`answers`、`dismissed`、`speakerAnswers`、`speakerSkipped`、存草稿的 debounce 都在這裡。`ReviewTab` 和 `InlineReviewCard` 觀察同一份，兩邊看到的回答一致（R41.2），存草稿的 key 不變（§21.15）。
- 題目換了（送出一部分、依回答修正、重新整理、復原上一版）時，不再整份清掉：草稿本來就是照題目文字存的，還在的題目把回答照文字接回來，不在的題目刪掉草稿（2026-10-24 改，配合卡片只送一部分）。
- 送出：待確認分頁的「送出」送全部；卡片的「送出」只送這一條對到的題目（`submitReviewDraft(only:)`，說話者題目不算），按鈕的題數也只算這幾題。送出後題目從紀錄移除，`MeetingDetail` 重新載入，標記跟著消失，其他題目的回答留著。
- 編輯模式（`NotesEditor`）、匯出（`Exporters`）不畫標記；DEBUG 的 snapshot 要畫（驗證排版用）。草稿紀錄（`notesDraft`）照樣標；垃圾桶裡的會議不標。

### 31.3 「是」不送整理工具（R41.4，D103）

- `submitReview` 把回答分三類：**確認**（`YesNoAnswer` 認出 `isYes == true` 而且沒有 correction）、**略過**（dismissed，加上「不是」沒填答案的：有問題但不知道對的是什麼，模型拿到也只能刪掉或留著，2026-10-24 加）、**修改**（其他）。確認和略過一起從 `reviewItems` 移除：`dismissReviewItems` 擴充成 `resolveReviewItems(id, confirmed:, dismissed:)`，版本原因分別寫「確認待確認」「略過待確認」，兩種都有時一個版本、原因「確認、略過待確認」。只有修改的題目進 `revise` 工作，payload 格式不變。
- 校稿的提示詞不變，送出去的 answers 不再包含「是」。以前「是」也會送去校稿，校稿收到「是」只會原樣保留，結果相同。
- 整理工具是 `.none` 或還沒設定時，只擋修改類（現在的 guard 改成看修改的題數）；確認和略過照樣能送。
- 統計（R41.7）：`UsageStats.Counters` 加 `review = ["answered": 0, "confirmed": 0, "skipped": 0]`，`submitReview` 成功後依三類加。§30.1、§30.4 跟著補。後台（§30.10）`usage_daily` 加 `review_answered`、`review_confirmed`、`review_skipped` 三欄（`ALTER TABLE … ADD COLUMN … NOT NULL DEFAULT 0`），worker 的 `USAGE_DICTS` 加 `review`；舊的 worker 遇到不認得的字典會忽略（§30.4「多的鍵忽略」），`schema` 維持 1。

### 31.4 待確認分頁（R41.5）

- 「略過」改成 `.borderless` 的按鈕；按了那題收成一行（灰字、劃掉，題號和時間點留著）加「取消略過」。
- 頁首說明的右邊加「全部略過」：把沒有回答的題目（含說話者題目）都標成略過，已經有回答的不動；送出鈕的文字變成「略過 N 題」。
- 送出鈕本來就固定在底部（`ReadingColumn` 是 ScrollView，footer 在它外面），不用改。

### 31.5 收音提醒送通知（R42，D104）

- `Recorder.levelHint` 從 nil 變成某個值時（§26.1 的規則不變），`AppModel` 觀察到就：
  - `NSApp.isActive == false` 或主視窗沒開時，`Notifier.levelHint(hint, meetingId:)` 送通知：標題「收音提醒」，內文同 `RecordingView.levelHintText`，類別 `LEVEL_HINT`，點了 `reveal(meetingId)`。`Recorder` 記 `notifiedLevelHints: Set<LevelHint>`，每場每種一次，錄音開始時清掉。
  - 不管前景與否，`AppModel.menuBarSymbol` 在 `levelHint != nil` 時用 `mic.badge.exclamationmark`，提示消失就換回錄音中的圖示。`MenuBarContent` 在錄音控制下面顯示同一句提示（琥珀色）和「知道了」（`dismissLevelHint`）。
- 通知權限沒給時只有選單列的變化（跟其他通知一樣，R11.2）。
- Windows：design-windows §11.5。

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
| D10 | 自己做簡單的更新器（JSON＋SHA-256＋designated requirement 檢查） | Sparkle 2：要在鑰匙圈產生並保管 EdDSA 私鑰、用 generate_appcast 產生 appcast，對只在公司內部發布的 app 來說太重。取捨：還沒改用自簽憑證（ad-hoc）時，designated requirement 只檢查 identifier，安全性靠 https 和更新網址的主機；改用自簽憑證後，新版必須是同一張憑證簽的 |
| D11 | 正式轉錄可以改用 macOS 內建辨識（預設仍是 Breeze） | 只用 Breeze：真人中文會議（AISHELL-4，遠距麥克風陣列、6 人、38 分鐘）上，Breeze 的 CER 29.1%、macOS 內建 25.2%，內建還快 160 倍、不用下載 1.7 GB 模型。只用內建：合成的中英夾雜會議上，內建的英文專有名詞幾乎都錯（關鍵詞 1/6，Breeze 加詞彙表 5/6），台灣公司的會議以這種情況為主，所以預設還是 Breeze |
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
| D22 | 整理工具做成共同介面（`LLMBackend`），enum 保留當代號 | 繼續在 enum 加 case、在 `LLMClient` 加 switch：到六種之後每個 switch 都要改，OpenAI 相容 API 還有網址這種只屬於它的設定，switch 會越來越長、各家的程式混在同一個檔案。做成外掛或動態註冊：整理工具只有六種，還要配合設定頁、引導、隱私說明，外掛的彈性用不到。代價：原本四種的程式碼要搬家，要確認請求沒變（§19.5） |
| D23 | Gemini 用 Interactions API，一律 `store: false` | generateContent：官方 2026-06 起列為舊版（仍支援），新模型和新功能只在 Interactions 推出。Interactions 預設會在 Google 保存請求（付費 55 天、免費 1 天），會議內容不該留在那裡，所以關掉。Gemini 的 OpenAI 相容端點：使用者可以自己用「OpenAI 相容 API」接，但 issue 的回覆答應 Gemini 有自己的選項，填 key 就能用 |
| D24 | OpenAI 相容 API 用 Chat Completions，JSON schema 不支援時退回 JSON 模式 | Responses API（OpenAI 官方那一種用的）：Groq、Ollama、LM Studio 等相容服務主要支援 Chat Completions。只用 JSON 模式：支援 JSON schema 的模型就少了格式保證。只用 JSON schema：Groq 只有少數模型支援，其他模型會回 400 |
| D25 | 錄音中裝置變了就自動重新接上，錄音不停 | 停止錄音並提示：會議還在進行，使用者可能沒注意到，後半場就沒錄到。只提示、不重接：使用者在開會中很難處理。代價：換裝置的那一兩秒沒錄到（雙聲道時補靜音） |
| D26 | 同一個工作意外結束 2 次才標成失敗，正常結束不算 | 1 次就失敗：拾音意外結束可能跟這個工作無關（例如錄音當掉），重跑一次通常會成功。一直重跑：每次開 app 都會再當一次，還可能帶走正在錄的會議 |
| D27 | 搜尋表的 rowid 用可推算的值，刪改都用 rowid | 只把 `saveTranscript` 改成一句 `DELETE … WHERE meeting_id = ? AND kind = 'segment'`：一次掃整張表還是會隨資料變多變慢，而且全文取代、全部接受校正仍是一段掃一次。另外建一張對照表：多一張表要維持同步，rowid 可以直接算出來就不需要 |
| D28 | 停止後先用即時逐字稿整理一份草稿，正式版完成後取代 | 等正式轉錄（現在）：長會議要等 20 幾分鐘。邊錄邊正式轉錄：whisper 跟即時辨識搶 GPU（D1），而且要大改轉錄流程；草稿用現成的即時逐字稿和同一份提示詞，成本最低。代價：即時逐字稿的錯字比較多、沒有說話者，所以一定要標明是草稿 |
| D29 | 聲紋對到就自動帶入名字，標「自動」可以改正 | 先列出來讓使用者確認：開會的人固定時每週都要按一次，跟沒做差不多。取捨：誤認的代價比沒認出來大，所以門檻要調到誤認 ≤ 2%（§21.4.1），寧可少帶入 |
| D30 | AI 猜說話者只當建議，在整理之後跑 | 直接套用：模型猜錯的名字會進到會議紀錄。放進整理的提示詞一起做：要改整理的提示詞和輸出格式（中文提示詞不變的原則），也會拖慢整理。代價：多一次呼叫整理工具 |
| D31 | 偵測會議用 Core Audio 的程序物件，只提醒不自動錄 | 看視窗標題或 Accessibility：要輔助使用權限，而且各家介面常改。自動開始錄音：沒經過與會者同意就錄音有禮儀和法律的問題 |
| D32 | 跨會議提問先請整理工具抽關鍵詞，再用全文搜尋挑片段 | 把所有會議都送出去：會議一多就超過模型的上限，也送出太多不相關的內容。本機語意搜尋（embedding 模型）：要再下載模型、建向量索引；全文搜尋已經有了。代價：多一次呼叫，問法跟逐字稿用詞差很多時可能找不到 |
| D33 | 回報問題只打開瀏覽器的 GitHub 頁面 | app 直接上傳：要有伺服器或 token，也違反「不回傳任何資料」；使用者在瀏覽器裡看得到、改得到要送出的內容 |
| D34 | 長錄音的 whisper 每 10 分鐘一段、從檔案依序讀，說話者辨識仍用整段（轉錄完才解碼整段） | 全部分段（含說話者辨識）：不同段的說話者編號對不起來。不分段：記憶體隨會議長度增加，4 小時要 4 GB 以上。分段但先解碼整段：整段聲音（4 小時 921 MB）在轉錄時一直佔著，只少 25%。代價：要辨識說話者時多解碼一次（4 小時的 WAV 約幾秒） |
| D35 | 模型還沒下載好時，自動改用 macOS 內建辨識轉錄那一場 | 等下載完：第一場會議要等 1.7 GB 下載，新使用者的第一印象很差。直接把設定改成內建：使用者之後不會知道 Breeze 比較準 |
| D36 | 讀到的 API key 在拾音開著時記在記憶體，不再查鑰匙圈 | 每次送出請求都查鑰匙圈（3.5 以前）：使用者只按「允許」時，一場會議的整理、校正、猜說話者、提問會各跳一次確認。改存到檔案或資料庫：違反 N6。取捨：key 在記憶體裡的時間變長（本來送出請求時就會在記憶體裡）；結束拾音就沒了 |
| D37 | 分不出人時讓使用者填「大約幾個人」（`minSpeakers`），可以只重新辨識說話者；名字依重疊比例沿用 | 調低預設門檻：一般會議會把同一個人拆成兩個。用「剛好 N 人」（`numSpeakers`）：使用者常常只知道大概，實際更多人時會被硬塞成 N 人。重新辨識後名字全部清掉：使用者取過的名字都要重取；全部照舊的編號：新舊分群對不起來，名字會掛錯人 |
| D38 | 改過說話者的句子標成「手動」，重新辨識時跟著原本那位說話者對應到的新說話者 | 重新辨識時照新的分群蓋掉：使用者改的會白改。把改過的句子當成分群的限制條件：FluidAudio 不支援，要自己改分群演算法。改過就不讓重新辨識：使用者要先選要哪一個 |
| D39 | 用片段聲紋的凝聚度判斷可能把人併在一起，只提示、不自動重新辨識 | 碰撞率：分群時同一片段的說話者會被強制分開，群數 ≥ 2 時一直是 0%（麻將也是）。看分出的人數：拾音不知道實際幾個人。說話秒數不平均：一般會議也常常一人主講。自動用更多人重新辨識：一般會議會把一個人拆成兩個；提示讓使用者決定 |
| D40 | 錄音時的音量條用 AppKit 的 view 畫，自己定時讀音量 | SwiftUI 的音量條只在格數變的時候更新：說話時還是每秒十幾次，每次整個視窗重新排版（CPU 51%）。`TimelineView`、`Canvas`：一樣經過 SwiftUI 的更新。降低更新頻率：音量條看起來會頓。取捨：這一小塊不是 SwiftUI，截圖工具畫不出來（DEBUG 截圖時換回 SwiftUI 版） |
| D41 | 收音提醒只看音量本身（背景多大聲、說話多小聲）持續多久，不看說話和背景差多少 | 用說話和背景的差（訊噪比）判斷吵：使用者正常的會議只差 8 dB，跟加了雜音的版本（6 dB）分不開，會一直誤報。用即時逐字稿的信心或字數：辨識不出來的原因很多（口音、語言），不一定是收音。取捨：麻將那種洗牌聲只在背景大聲的那一段抓得到，不吵的時候分錯人提醒不了（錄完的「說話者可能分得不夠」補這一塊） |
| D90 | 是非題由整理工具在結構化輸出標出（`yesNo`），不由 app 從題目文字判斷（D42～D88 是 Windows 版、iPhone 版的編號，D89 是 R14.8） | app 看題目結尾（「嗎？」、「?」）判斷：中文、日文是非題的問法很多（「是不是」「有沒有」「でしょうか」），「負責人是誰？」又不是是非題，容易判錯；整理工具出題時就知道是不是在問「X 是不是 Y」。代價：結構化輸出多一個欄位，三個平台的標準答案要重新產生；以前整理的會議紀錄沒有這個標記，重新整理才有按鈕 |
| D91 | 聲紋分兩段：很有把握才直接帶入，中間的改成待確認的說話者題目（取代 D29 的「對到就帶入」） | 照舊一個門檻、調高：沒那麼像的真的同一個人就認不出來，使用者每場都要自己取名。照舊直接帶入：使用者的資料裡不同的人可以像到 0.77，帶錯的名字會進到待辦的負責人。全部都問：D29 說的每週都要按一次。取捨：第一次遇到的人比較常被問（AMI 16 位問 8 位），回答「是」會存聲紋，之後越來越少問（16 位問 1 位） |
| D92 | 很短的一句：說話者辨識的設定改成 0.3 秒就算聲紋、重新嵌入沒人認領的片段；再用另一個聲紋模型把 0.5～4 秒的句子跟每位說話者比一次，像另一位 0.15 以上才改 | 只改設定：使用者那段訪談沒有改善（換人點放早，不是短片段沒聲紋）。每句都重新分群：等於另做一次說話者辨識，人數、名字對應會變。改成「可能分錯人」的提示讓使用者改：短句很多，一句一句問很煩；評估裡門檻 0.15 時沒重疊的句子改錯 0 句。代價：多下載約 15 MB 的模型；一小時的會議多約 30 秒；兩個人同時講話的地方偶爾改錯（AISHELL-4 改對 42、改錯 9；AMI 改對 227、改錯 23） |
| D95 | 沒選過整理工具時自動用找得到的 CLI，都沒有就先不整理、提示去設定（取代「預設 claude CLI」，§19.6） | 照舊預設 claude CLI、只把錯誤訊息寫清楚：沒有 CLI 的人每場會議都「整理失敗」。預設改成某個 API：還是要先申請 key，沒填之前一樣失敗。第一次使用時一定要選：多一個擋路的步驟，只要逐字稿的人也得選。取捨：沒設定的人要看到引導或會議頁的提示才會去設定 |
| D100 | 匿名使用統計：每天一份匯總、預設開啟（告知＋可以關；2026-10-22 起開關收在設定 → 進階最下面的「詳細…」裡、不顯示送出的 JSON）、送到使用者自己的後台、沒有統計網址就完全不送 | 完全選擇加入：多數人不會去開，數字太少看不出活躍度。預設開啟但不說：違反之前「不收集」的公開承諾，程式開源也藏不住。逐筆事件（每次點擊）：資料多、比較像追蹤，用量的匯總就夠回答「有多少人在用、處理多少語音」。第三方服務（TelemetryDeck、Aptabase）：資料在別人那裡、要多一個套件；使用者想自己做後台。不用安裝 ID：算不出活躍天數和留存。取捨：歐盟的規定要求在裝置上存識別碼前先取得同意，目前是「告知＋可關」的常見做法，歐洲使用者變多時再改成先問；編號跳到 D100，避開 windows 分支正在用的 D42 起 |
| D101 | 匿名使用統計的後台用 Cloudflare Workers＋D1，網址是自己網域的子網域 | 自己租主機：要顧作業系統更新、備份、停機，一天幾百、幾千筆用不到。Supabase、Firebase：好用，但資料在第三方，隱私說明要多寫一家公司。PostHog 自架：很吃資源。放在官網的網域（Pages Functions）：官網現在是純靜態、CSP 很嚴，統計另外一個 Worker 互不影響，以後換後台只要換網址。取捨：Cloudflare 看得到請求的 IP（我們的資料庫不存）；D1 免費額度（每天 10 萬次寫入）以每台每天一筆來算夠用很久 |
| D102 | 待確認的題目靠時間點對應到紀錄的條目，不改提示詞（§31.1） | 讓整理工具多輸出「對應到哪一條」：三個平台的 schema、提示詞、標準答案都要重產，模型填的對應也不一定準。app 用文字比對：紀錄是改寫過的文字（D9 同樣的理由）。代價：時間填錯的題目對不到，只在待確認分頁 |
| D103 | 是非題回答「是」當成確認，跟略過一樣直接移除，不送整理工具（§31.3） | 照舊全部送校稿：校稿收到「是」只會原樣保留，使用者白等一次、多一次費用。由 app 改紀錄：「不是＋答案」要改文字，還是要模型。代價：「是」不再經過模型，模型也不會順便改別的地方（本來就不該改） |
| D104 | 收音提醒用通知和選單列圖示提醒，不自動停止錄音或換麥克風（§31.5） | 自動換麥克風：R18.2 已經處理裝置消失的情況；靜音、選錯麥克風是使用者的設定，app 猜不準。只在錄音畫面提示（3.7）：R24.3 的那次人在 Zoom 的視窗，沒看到。每次提示都通知：每場每種一次就夠，多了會煩 |

沿用 v2 的決策：資料放 Application Support（v2 D3）、GRDB（v2 D5）、會議紀錄存 JSON 加版本（v2 D6）、DEBUG 控制通道（v2 §13）。
