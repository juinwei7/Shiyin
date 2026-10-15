# 拾音 Shiyin：設計

> 狀態：已確認（2026-10-15，第二版：改成 macOS 原生 app）
> 對應 `requirements.md` 的需求編號（R#、N#）。實作中如果偏離本文件，要先改本文件再改程式。

## 1. 架構

```
Shiyin.app（SwiftUI，單一程序）
 ├─ AppModel（@MainActor @Observable）       介面狀態：會議列表、選取中的會議、錄音、進度
 ├─ AppDatabase（GRDB，SQLite WAL）          會議、逐字稿、會議紀錄版本、工作、詞彙表
 ├─ JobRunner                                兩條線各跑一個 Task 迴圈，互不等待
 │    ├─ 轉錄線 ─► Process：venv/bin/python transcribe_job.py（mlx-whisper + Breeze ASR 25）
 │    └─ 整理線 ─► Process：claude -p ／ codex exec
 ├─ Recorder（AVAudioRecorder）              App 內錄音（R11）
 ├─ Player（AVPlayer）                       播放、時間點跳播（R4.2）
 ├─ Notifier（UserNotifications）            系統通知（R12）
 ├─ AppDelegate                              Dock 拖放、打開方式、結束前確認、通知點擊
 └─ Scenes：主視窗、設定視窗、MenuBarExtra（R13）
```

| 項目 | 選擇 |
|---|---|
| 語言、介面 | Swift 6.3、SwiftUI，部署目標 macOS 26 |
| 資料庫 | GRDB 7（Swift Package），SQLite WAL 模式 |
| 建置 | Swift Package（`Package.swift`）加上 `scripts/build-app.sh`，組成 `.app` 後用 ad-hoc 簽章 |
| 轉錄 | Python 3.11 venv，裝 mlx-whisper 0.4.3，模型是 Breeze ASR 25（MLX 格式） |
| 設定 | `UserDefaults`（`@AppStorage`） |
| Log | `os.Logger`，subsystem 是 `com.weiwei.shiyin`。用 `log stream --predicate 'subsystem == "com.weiwei.shiyin"'` 查看，不寫逐字稿內容（N2） |

## 2. 檔案配置

**原始碼**（`~/Desktop/Shiyin`，會被 iCloud 同步，所以只放程式碼）：

```
Shiyin/
├── CLAUDE.md
├── README.md
├── docs/                      requirements.md、design.md、tasks.md
├── Package.swift
├── Sources/Shiyin/
│   ├── App/                   ShiyinApp（Scenes）、AppDelegate
│   ├── Model/                 Meeting、Segment、Job、NotesVersion、GlossaryTerm、MeetingNotes
│   ├── Store/                 AppDatabase（schema、migration、查詢）、AppModel
│   ├── Services/              Paths、JobRunner、Transcriber、LLMClient、NotesPrompt、Importer、
│   │                          Recorder、WAVRepair、Player、Notifier、EnvironmentCheck、MarkdownExporter
│   └── Views/                 Sidebar、MeetingDetail、SummaryTab、TopicsTab、TranscriptTab、ReviewTab、
│                              TimelineBar、RecordingControls、ImportSheet、SetupView、SettingsView、MenuBarContent
├── Resources/
│   ├── Info.plist
│   ├── AppIcon.icns           由 scripts/make-icon.swift 產生
│   └── python/                transcribe_job.py、setup.sh（打包進 app 的 Contents/Resources/python）
└── scripts/
    ├── build-app.sh           swift build → build/Shiyin.app → 簽章；加 --install 就裝到 ~/Applications
    └── make-icon.swift
```

**資料**（`~/Library/Application Support/Shiyin`，權限 700，不會被 iCloud 同步）：

```
Shiyin/
├── venv/                      Python 環境，約 1.2 GB
├── models/breeze-asr-25/      轉錄模型，約 3 GB（config.json、weights.safetensors）
├── audio/                     錄音檔，命名為 <會議id>.<副檔名>
│   └── recording/             錄音中的檔案；app 正常結束後這裡應該是空的
├── shiyin.sqlite
├── tmp/                       送給 LLM 的暫存資料夾，用完就刪
└── logs/setup.log             安裝腳本的輸出
```

`Paths` 統一管理上述路徑。環境變數 `SHIYIN_HOME` 可以改根目錄，測試時用。

## 3. 資料模型

```sql
CREATE TABLE meetings (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  title            TEXT NOT NULL DEFAULT '',
  title_locked     INTEGER NOT NULL DEFAULT 0,  -- 使用者自己填過標題，整理時就不覆蓋
  meeting_date     TEXT NOT NULL,               -- yyyy-MM-dd
  participants     TEXT NOT NULL DEFAULT '',    -- 逗號分隔
  source_filename  TEXT NOT NULL,
  audio_file       TEXT NOT NULL,               -- audio/ 底下的檔名
  duration_seconds INTEGER,
  notes            TEXT,                        -- 目前版本的會議紀錄 JSON（見第 6 節）
  notes_by         TEXT,                        -- claude / codex
  created_at       TEXT NOT NULL,
  updated_at       TEXT NOT NULL
);

CREATE TABLE segments (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id     INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  seq            INTEGER NOT NULL,
  start          REAL NOT NULL,
  end            REAL NOT NULL,
  text           TEXT NOT NULL,
  low_confidence INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE notes_versions (                  -- R6.3：存「被取代掉的那一版」
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  notes      TEXT NOT NULL,
  reason     TEXT NOT NULL,                     -- 例如：重新整理、校稿、取代「A」→「B」
  created_at TEXT NOT NULL
);

CREATE TABLE jobs (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  meeting_id  INTEGER NOT NULL REFERENCES meetings(id) ON DELETE CASCADE,
  kind        TEXT NOT NULL,                    -- transcribe / summarize / revise
  status      TEXT NOT NULL,                    -- queued / running / done / failed / cancelled
  payload     TEXT,                             -- revise 的答案 JSON
  error       TEXT,
  created_at  TEXT NOT NULL,
  started_at  TEXT,
  finished_at TEXT
);

CREATE TABLE glossary (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  term       TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL
);
```

- 寫入 `meetings.notes` 時，在同一個交易裡把舊值存進 `notes_versions`。
- 復原上一版：取出最新一筆版本寫回 `meetings.notes`，再刪掉那筆版本。
- 轉錄進度只放在記憶體（`AppModel.progress[jobId]`），不寫進資料庫：重開 app 時工作會從頭重跑，存進度沒有意義。
- 會議狀態（R7.1）不另外存欄位，由「該會議最新一筆工作」加上 `notes.reviewItems` 的題數推算：
  1. 有排隊中或執行中的工作：顯示排隊中、轉錄中 n%、整理中
  2. 最新一筆工作失敗：失敗
  3. 還有待確認題目：待確認 n 題
  4. 其他：完成

  錄音中的那一場還沒建立會議，由 `AppModel` 在列表最上面另外加一列顯示。

## 4. 工作佇列（R2.2、R2.6～R2.8、R3.1、N5）

- `JobRunner` 有兩條線，各跑一個 Task 迴圈：
  - 轉錄線：處理 `transcribe`
  - 整理線：處理 `summarize`、`revise`
- 迴圈流程：
  1. 在資料庫交易裡把最舊一筆 `queued` 改成 `running`
  2. 執行工作
  3. 寫回 `done` 或 `failed`
  4. 沒有工作時，等待新工作的通知（`AsyncStream`），最多等 2 秒
- 執行中的工作會持有 `ProcessInfo.beginActivity(.idleSystemSleepDisabled)`，避免系統進入閒置睡眠（N5）。
- 取消（R2.7）：
  - 排隊中的工作：直接改成 `cancelled`
  - 執行中的工作：對子程序送 `terminate()`，3 秒內還沒結束就送 `SIGKILL`，再改成 `cancelled`
- app 啟動時，把所有 `running` 的工作改回 `queued`（R2.8）。同時清空 `tmp/`，並用 `pkill -f <transcribe_job.py 路徑>` 結束上次 app 異常結束時留下的轉錄子程序，避免兩個轉錄同時佔用 GPU。
- 轉錄成功後，如果開了自動整理、而且整理工具不是「不整理」，就自動排一筆 `summarize`。
- 工作完成或失敗時，呼叫 `Notifier`（R12.1）。
- 刪除會議時，先取消這場會議的所有工作，再刪資料和錄音檔。

### 4.1 轉錄子程序（Resources/python/transcribe_job.py）

```
venv/bin/python transcribe_job.py <錄音> <結果.json> --model <模型資料夾> --prompt <提示詞>
環境變數：PATH 前面加 /opt/homebrew/bin:/usr/local/bin（mlx-whisper 讀檔要用 ffmpeg；從 Finder 開的 app，PATH 只有系統目錄），HF_HUB_OFFLINE=1
```

轉錄放在子程序執行：每場轉完 GPU 記憶體就會釋放；取消時可以直接結束程序；模型崩潰也不會拖垮 app。

stdout 每行輸出一個 JSON 事件，Swift 端逐行解析：

```
{"event":"loaded","duration":542.1}
{"event":"progress","done":12000,"total":54210}      每秒最多一次
{"event":"done","segments":87}
```

- 進度：mlx-whisper 用 `tqdm.tqdm(total=content_frames)` 回報進度。在 import `mlx_whisper` 之前把 `tqdm.tqdm` 換成子類別，在 `update()` 裡輸出進度事件。
- 預估剩餘時間：Swift 端用「已經過時間 ÷ 目前進度」推算。
- 段落過濾沿用原本轉錄工具的 transcribe.py（R2.5）：
  - 丟掉 `no_speech_prob > 0.6` 且 `avg_logprob < -1.0` 的段落
  - 40 字以下的段落如果含有幻覺字串，也丟掉
  - `avg_logprob < -1.0` 或 `compression_ratio > 2.4` 的段落標成低信心
- 提示詞：`以下是繁體中文的工作會議錄音，會夾雜英文專有名詞。詞彙：A、B、C。`，總長度不超過 150 字（R2.4）。
- 模型參數給本機資料夾路徑。已確認 mlx-whisper 原始碼：路徑存在時直接讀檔，不呼叫 `snapshot_download`。
- 子程序的 stderr 只保留最後 2000 字，失敗時當作錯誤訊息。

## 5. 安裝與首次設定（R10）

`Resources/python/setup.sh` 可以重複執行，每一步都先檢查是否已完成：

1. 確認是 Apple Silicon 機器，並且有 `ffmpeg`
2. 建立 `SHIYIN_HOME` 和子資料夾，權限 700
3. 找 Python 3.10 以上的版本（依序試 `python3.11`、`python3`），建立 venv，`pip install mlx-whisper==0.4.3`
4. 準備模型：
   - 已存在：跳過
   - 原本轉錄工具的 Breeze 模型資料夾存在：先 `cp -c`（APFS clone）到 `.partial` 資料夾，完成後再改名；clone 失敗就改用一般複製
   - 都沒有：從 Hugging Face 下載 `schsu/breeze-asr-25-mlx`
5. 最後用 venv 的 Python 執行 `import mlx_whisper`，確認環境可用

App 端：

- `EnvironmentCheck` 檢查 ffmpeg、venv、模型、claude、codex 是否可用。
- 必要項目（ffmpeg、venv、模型）有缺時，主視窗改顯示 `SetupView`：
  - 列出檢查結果
  - 「開始安裝」按鈕：用 `Process` 執行 setup.sh，即時顯示輸出，並同時寫進 `logs/setup.log`
  - 安裝完成後重新檢查
- 設定視窗的「環境」分頁也有同樣的檢查結果和「重新安裝」按鈕。

## 6. 會議紀錄格式與整理

### 6.1 JSON schema

這份 schema 同時給 Claude 的 `--json-schema` 和 Codex 的 `--output-schema` 使用。Codex 要求嚴格模式：每一層都要 `additionalProperties:false`，所有欄位都要列在 required，不知道的值用空字串。

```json
{
  "type": "object", "additionalProperties": false,
  "required": ["title", "summary", "topics", "decisions", "actionItems", "openQuestions", "reviewItems"],
  "properties": {
    "title":   {"type": "string"},
    "summary": {"type": "array", "items": {"type": "string"}},
    "topics":  {"type": "array", "items": {"type": "object", "additionalProperties": false,
                "required": ["title", "start", "points"],
                "properties": {"title": {"type": "string"}, "start": {"type": "string"},
                               "points": {"type": "array", "items": {"type": "string"}}}}},
    "decisions":     {"type": "array", "items": {"type": "string"}},
    "actionItems":   {"type": "array", "items": {"type": "object", "additionalProperties": false,
                      "required": ["owner", "task", "due"],
                      "properties": {"owner": {"type": "string"}, "task": {"type": "string"}, "due": {"type": "string"}}}},
    "openQuestions": {"type": "array", "items": {"type": "string"}},
    "reviewItems":   {"type": "array", "items": {"type": "object", "additionalProperties": false,
                      "required": ["time", "question"],
                      "properties": {"time": {"type": "string"}, "question": {"type": "string"}}}}
  }
}
```

時間點格式是 `mm:ss`，超過一小時用 `h:mm:ss`，跟逐字稿一致，Swift 端解析成秒數。

### 6.2 呼叫方式（LLMClient）

以下指令都在 2026-10-15 實測過，回傳的 JSON 符合 schema。兩個 CLI 都是原生執行檔，不需要 node。

**Claude**：工作目錄設成 `tmp/` 底下新建的空資料夾，這樣不會讀到任何專案的 CLAUDE.md。

```
claude -p --output-format json --tools "" --no-session-persistence --strict-mcp-config
       --system-prompt <系統提示詞> --json-schema <schema> [--model <模型>]
stdin  ＝ 使用者提示詞
結果   ＝ 輸出 JSON 裡的 structured_output；is_error 為 true 時，取 result 當錯誤訊息
```

不能加 `--bare`：加了就不讀 OAuth 登入資訊，訂閱帳號會驗證失敗。

**Codex**：

```
codex exec --skip-git-repo-check --ephemeral --sandbox read-only --color never
           --output-schema schema.json -o out.json [-m <模型>] -
stdin  ＝ 系統提示詞 + 使用者提示詞（Codex 沒有獨立的系統提示詞參數）
結果   ＝ out.json
```

**共通處理**：
- 執行檔依序找 `/opt/homebrew/bin/<名稱>`、`~/.local/bin/<名稱>`、`/usr/local/bin/<名稱>`。從 Finder 開的 app 拿不到使用者 shell 的 PATH，所以這裡用固定路徑。
- 子程序的 PATH 要補上述目錄。
- 逾時 20 分鐘。
- 暫存資料夾在 `defer` 裡刪除（N2）。
- 失敗時只取 stderr 最後 500 字。
- 待驗證：從 app 啟動的 `claude` 子程序能不能讀到鑰匙圈裡的 OAuth 憑證（T6 驗證）。

### 6.3 提示詞（NotesPrompt）

**系統提示詞**（整理和校稿共用），把 meeting-notes skill 的規則寫死在裡面（R3.3）：
- 角色：會議紀錄整理者，用繁體中文、台灣用語。
- 只根據逐字稿寫，不補充逐字稿裡沒有的資訊。
- 語音辨識常把英文術語、人名、縮寫聽錯。可以依上下文和詞彙表推斷，但推斷出來的專有名詞、人名一定要列進 `reviewItems`。
- 負責人只填明確提到的人。期限依會議日期換算成 `yyyy-MM-dd`（提示詞會附上會議日期和星期幾），不明確就留空並列成題目。
- `reviewItems` 最多 8 題，相近的問題要合併，只問會影響紀錄正確性的：專有名詞、人名、負責人、期限、方案內容。
- `topics` 依議題分段，`start` 填這個議題開始的時間點。
- `title`：提示詞裡有現有標題就原樣回傳，沒有就產生一個 20 字以內的標題。

**整理（summarize）的使用者提示詞**：
```
會議日期：2026-10-13（星期一）
現有標題：<空白或使用者填的標題>
與會者：<逗號分隔，或「未提供」>
詞彙表（正確寫法）：A、B、C
逐字稿（[時間] 內容，標 (?) 的是辨識信心低的段落）：
[00:00] ……
[00:12] (?) ……
```

**校稿（revise）的使用者提示詞**：在整理用的提示詞後面再加上
- 目前的會議紀錄 JSON
- 使用者對待確認題目的回答（題目、時間點、答案）
- 指示：依回答修正會議紀錄；已回答的題目從 `reviewItems` 移除；沒回答的題目原樣保留；除非答案本身帶出新的疑點，否則不要新增題目

### 6.4 全文取代（R4.5）

- 逐字稿的每一段做「區分大小寫的單純字串取代」。
- 會議紀錄 JSON 的每個字串值也做同樣的取代；有實際改到才存版本，原因寫「取代「A」→「B」」。
- 回報結果：改了幾段逐字稿、會議紀錄有沒有被改到。
- 勾選「加入詞彙表」時，只加正確的寫法。錯誤的寫法不存，避免 meeting-notes skill 提過的誤替換問題。

### 6.5 Markdown 匯出（R7.4）

```markdown
# 標題
- 日期：2026-10-13（星期一）
- 與會者：…
- 長度：9 分鐘

## 摘要
## 決議
## 待辦
| 負責人 | 事項 | 期限 |
## 未決問題
## 詳細
### [03:12] 議題標題
## 待確認
- [03:40] 題目
```

匯出方式有兩種：用 `NSSavePanel` 存成檔案，或複製到剪貼簿。

## 7. 匯入（R1）

`Importer` 統一處理四個匯入入口：

| 入口 | 實作 | 對話框 |
|---|---|---|
| 拖進視窗 | `.dropDestination(for: URL.self)` | 有（ImportSheet） |
| 工具列按鈕、⌘O | `fileImporter`，可以多選，類型是 `.audio`、`.movie`、`.audiovisualContent` | 有 |
| 拖到 Dock 圖示、Finder 的打開方式 | `AppDelegate.application(_:open:)`；Info.plist 的 `CFBundleDocumentTypes` 宣告上面這幾種類型，角色是 Viewer，順位是 Alternate | 沒有（R1.3） |

匯入流程：
1. 推算日期（R1.4）
2. 在交易裡建立會議，取得會議 id
3. 用 `FileManager.copyItem` 複製到 `audio/<id>.<副檔名>`（同一個 APFS 磁碟區時會自動用 clone）。副檔名只保留英數字，沒有的話用 `bin`
4. 排一筆轉錄工作

步驟 3 如果失敗，就刪掉剛建立的會議。

## 8. App 內錄音（R11）

- 使用 `AVAudioRecorder`，格式是 Linear PCM、16 kHz、單聲道、16 bit 的 WAV。Whisper 本來就是用 16 kHz，所以音質不會有損失。一小時約 115 MB。
- 錄音中的檔案寫在 `audio/recording/<UUID>.wav`。停止後，建立會議（日期是今天、標題空白、`source_filename` 是「錄音 2026-10-15 14:03」），把檔案搬到 `audio/<id>.wav`，再排一筆轉錄工作。
- 音量表：`isMeteringEnabled`，每秒更新 20 次 `averagePower` 和 `peakPower`。
- 防止 app 意外結束時遺失錄音（R11.5）：WAV 的檔頭大小在錄音結束時才會寫入，但音訊資料會一直寫進檔案。啟動時如果在 `audio/recording/` 發現殘留的檔案，就交給 `WAVRepair` 處理：
  1. 逐一解析 chunk，找到 `data` chunk 的位置
  2. 用實際檔案大小改寫 RIFF 大小和 data 大小
  3. 用一般的匯入流程建立會議，標題設為「復原的錄音」
- 麥克風權限：
  - Info.plist 加上 `NSMicrophoneUsageDescription`
  - 開始錄音前檢查 `AVAudioApplication.recordPermission`。被拒絕時顯示說明，並附上「打開系統設定」按鈕（`x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone`）
- 錄音中會持有 `beginActivity(.idleSystemSleepDisabled)`（N5）。
- 結束 app（R11.7）：`applicationShouldTerminate` 先跳 `NSAlert` 確認；確認後呼叫 `stop()` 把檔頭寫好，接著走正常的停止流程，再結束 app。

## 9. 介面

### 9.1 Scenes

- `Window("拾音", id: "main")`：單一主視窗。關掉視窗 app 不會結束：`applicationShouldTerminateAfterLastWindowClosed` 回傳 false。
- `Settings`：設定視窗，有三個分頁：一般、詞彙表、環境。
- `MenuBarExtra`，用 `.window` 樣式。圖示依狀態切換：
  - 閒置：`waveform`
  - 錄音中：`record.circle.fill`
  - 工作中：`waveform.circle.fill`（`waveform.badge.ellipsis` 在 macOS 26 不存在）

  選單內容：錄音開始或停止（錄音中顯示經過時間和音量）、目前工作與進度條、排隊數量、「打開拾音」、「結束拾音」。

### 9.2 主視窗版面

```
┌──────────────────┬───────────────────────────────────────────────────┐
│ [錄音] [匯入]     │ 新品上市時程討論                            [編輯]  │
│ 🔍 篩選會議        │ 2026-10-13 星期一   9 分鐘   王小明                  │
│ ──────────────── │ ┌ 轉錄中 42%，約剩 3 分鐘                 [取消] ┐ │ ← 有工作或失敗時才出現
│ ● 錄音中 03:21    │ 摘要 │ 詳細 │ 逐字稿 │ 待確認 3                      │
│ 新品上市時程…      │ ───────────────────────────────────────────────── │
│ 10/13 完成        │                                                   │
│ 季度目標規劃…      │   分頁內容（閱讀寬度上限約 680 pt）                  │
│ 08/27 待確認 3    │                                                   │
│                  │                                                   │
├──────────────────┴───────────────────────────────────────────────────┤
│ ▶  03:12  ▁▁▁▁▂▁▁▁█▁▁▁▁▁▁▂▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁  09:03      │ ← TimelineBar
└───────────────────────────────────────────────────────────────────────┘
```

- `NavigationSplitView`：
  - 側欄：會議列表，`.searchable` 依標題篩選
  - 內容：MeetingDetail
- 分頁用工具列的 segmented `Picker` 切換，⌘1～⌘4 對應四個分頁。
- 標題、日期、與會者用「編輯」popover 修改（R7.2）。
- 「更多」選單：重新整理、復原上一版、匯出 Markdown、複製 Markdown、刪除會議。
- 空狀態用 `ContentUnavailableView`：「把錄音檔拖進來，或按「錄音」開始。」
- 環境不齊全時，主視窗內容整個換成 SetupView。

### 9.3 視覺方向

主題是「收音設備」。大部分畫面都用系統原生樣式，保持安靜，只在兩個地方表現個性：

1. **錄音時間軸**（TimelineBar，用 `Canvas` 繪製）：把整場會議畫成一條帶子，每段逐字稿是一格；低信心段落是琥珀色（類比 VU 表的警示區）；待確認題目在對應位置有標記；目前播放位置是一條直線。點擊或拖曳就能跳播。
2. **錄音音量表**：錄音時在工具列和選單列顯示，模仿 VU 表的分段 LED，滿格的地方顯示琥珀色。

| Token | 淺色 | 深色 | 用途 |
|---|---|---|---|
| AccentColor | `#2D6A5D` | `#6DB2A1` | 主要動作、目前播放的段落、列表選取（青瓷綠）。編譯成 `Assets.car`（`Resources/Assets.xcassets`，build-app.sh 用 actool），Info.plist 設 `NSAccentColorName`；系統「強調色」設成多色時才會套用，使用者指定其他顏色時以使用者為準 |
| Signal | `#B97F06` | `#E2AD3B` | 低信心、待確認、進行中（VU 琥珀） |
| 錄音紅 | 系統 `.red` | 系統 `.red` | 只用在錄音狀態 |

- 字體：
  - 會議標題和會議紀錄的段落標題用宋體-繁（`Songti TC`），讓紀錄讀起來像一份文件
  - 其他文字用系統字體
  - 時間點加上 `.monospacedDigit()`
- 背景、分隔線、選取狀態都用系統預設，自動跟著淺色、深色模式切換。
- 動態效果只用在回應使用者操作，例如播放段落的高亮；系統設定「減少動態效果」時就關掉。

### 9.4 鍵盤

| 快捷鍵 | 動作 |
|---|---|
| ⌘O | 匯入 |
| ⌘R | 開始或停止錄音 |
| 空白鍵 | 播放或暫停（游標在輸入框時不觸發） |
| ⌘1～⌘4 | 切換分頁 |
| ⌘F | 搜尋逐字稿（在逐字稿分頁） |
| ⌘⌫ | 刪除會議（會先確認） |

### 9.5 使用說明（R14）

- 獨立視窗 `Window("拾音使用說明", id: "guide")`，內容是 `GuideView`，寬度約 600 pt，可以捲動。
- 自動打開：`MainView` 出現時，如果 `@AppStorage("hasSeenGuide")` 是 false，就先設成 true，再打開說明。只會自動打開一次。
- 入口：
  - 「輔助說明」選單（系統的 Help 選單，繁中 macOS 的名稱）取代預設項目：「拾音使用說明」（⌘?）
  - 沒有會議時的空白畫面：多一個「看使用說明」按鈕
  - 選單列：「使用說明」
- 版面：
  - 宋體標題，加上一句說明 app 在做什麼
  - 六個步驟，因為實際使用本來就有順序，所以用編號；每一步一個 SF Symbol 和兩三句說明
  - 接著是「設定」「資料與隱私」「系統需求」三段，最後是快捷鍵表
  - 系統需求（R14.5）：
    - Apple Silicon（M1 以上）、macOS 26 以上
    - 記憶體 16 GB 以上：轉錄時峰值約 7.5 GB，8 GB 的 Mac 不建議
    - 磁碟約 4.5 GB（Python 環境 1.3 GB、模型 2.9 GB），另外加錄音：App 內錄音每小時約 115 MB
    - 先安裝 Homebrew 的 ffmpeg、Python 3.10～3.13；要整理的話，還需要已登入的 claude 或 codex
    - 網路：安裝時需要（下載套件；本機沒有模型時另外下載約 3 GB）、整理時需要；轉錄不需要
- 文字一律引用介面上的實際名稱，例如「錄音」「匯入」「待確認」「送出」「略過」「更多」「用詞修正…」「整理工具」。

## 10. 系統通知（R12）

- 第一次有工作完成時，先請求 `UNUserNotificationCenter` 授權。
- 通知內容：
  - 「轉錄完成：<標題或檔名>」
  - 「會議紀錄整理好了：<標題>，有 n 題待確認」
  - 「轉錄失敗」或「整理失敗：<原因前 60 字>」
- 通知的 `userInfo` 帶 `meetingId`。點通知時，代理方法會打開主視窗並選取那場會議。
- 沒有授權就不發通知，其他功能不受影響（R12.3）。

## 11. 建置與簽章（R10.1）

`scripts/build-app.sh [--install]`：

0. 先結束從建置路徑啟動的拾音（SIGTERM，10 秒後 SIGKILL）：重建後的 bundle 會被 LaunchServices 當成另一個 app，兩個程序會共用資料庫同時跑工作
1. `swift build -c release --arch arm64 --scratch-path ~/Library/Caches/ShiyinBuild`（建置暫存有幾百 MB，不放在會被 iCloud 同步的專案資料夾）
2. 組成 `~/Library/Caches/ShiyinBuild/app/Shiyin.app`（專案裡的 `build` 是指過去的捷徑；放在 iCloud 資料夾裡的話，iCloud 加上的延伸屬性會讓 codesign 失敗）：
   - `Contents/MacOS/Shiyin`
   - `Contents/Info.plist`
   - `Contents/Resources/AppIcon.icns`
   - `Contents/Resources/python/*`
3. `codesign --force --sign - --identifier com.weiwei.shiyin`
4. 加上 `--install` 時：先結束正在執行的拾音，再把 app 複製到 `~/Applications/Shiyin.app`

Info.plist 的重點欄位：

| 欄位 | 值 |
|---|---|
| `CFBundleIdentifier` | `com.weiwei.shiyin` |
| `LSMinimumSystemVersion` | 26.0 |
| `NSMicrophoneUsageDescription` | 會議錄音需要使用麥克風 |
| `CFBundleDocumentTypes` | 見第 7 節 |
| `CFBundleIconFile` | AppIcon |

開發時從程式碼找資源：`Bundle.main` 找不到 `python/transcribe_job.py` 時，改用環境變數 `SHIYIN_RESOURCES` 指定的路徑。用 `swift run` 執行時就是靠這個。

不開 App Sandbox：app 要執行 Python、claude、codex，也要讀原本轉錄工具的模型資料夾。只在自己的電腦上使用，所以不需要沙盒。

## 12. 安全（N1）

- app 不開任何網路連接埠。
- 所有外部程式都用 `Process` 搭配參數陣列執行，不經過 `/bin/sh -c`。只有 setup.sh 是用 `/bin/bash <腳本路徑>` 執行，腳本本身打包在 app 裡。
- 錄音檔命名固定為 `<會議 id>.<英數字副檔名>`，避免路徑穿越。
- 送給 LLM 的資料只有逐字稿文字、詞彙表、會議基本資料。

## 13. 驗證工具（只在 DEBUG 建置）

螢幕鎖定或沒人操作時，沒辦法點畫面驗證功能。所以 DEBUG 建置多了一個控制通道：

- `Sources/Shiyin/Debug/DebugControl.swift` 整段包在 `#if DEBUG` 裡，release 建置不會包含。
- 它監聽 `DistributedNotificationCenter` 的 `com.weiwei.shiyin.debug` 通知，object 是用 `\u{1F}` 分隔的指令。
- 支援的指令：選會議、切分頁、取消、重試、重新整理、復原、校稿、取代、改段落、錄音開始和停止、跳播、安裝、匯出、複製、`state`（把狀態寫成 JSON）、`snapshot`（用 ImageRenderer 把會議內容畫成 PNG；AppKit 元件不會出現在圖上）。
- `scripts/dev/shiyinctl.swift` 是送指令的小工具；`scripts/build-app.sh --debug` 會建置 DEBUG 版。

## 14. 決策紀錄

| # | 決策 | 不採用的方案與原因 |
|---|---|---|
| D1 | SwiftUI 原生介面＋Python 轉錄子程序（使用者選擇） | 本機網頁 app：這是第一版的設計，使用者後來改要原生 app。whisper.cpp 全 Swift：要先轉換模型格式並驗證品質，風險高，留待之後評估。Apple SpeechAnalyzer：辨識品質未知，也沒辦法用詞彙表當提示詞。WKWebView 外殼：操作起來不像原生 app |
| D2 | 整理用本機的 `claude` 或 `codex` CLI（使用者決定） | 本機 LLM：18 GB RAM 只能跑 8B～14B 的模型，品質差，尤其不會主動找出自己不確定的地方 |
| D3 | 資料、venv、模型都放在 `~/Library/Application Support/Shiyin` | 放在專案資料夾：桌面有開 iCloud 同步，錄音會被上傳 |
| D4 | 轉錄放在子程序 | 在 app 程序內轉錄：GPU 記憶體不會釋放、沒辦法取消，崩潰會連 app 一起掛掉 |
| D5 | 用 GRDB | SwiftData：背景寫入的資料什麼時候反映到介面比較難掌握，也不好直接用 sqlite3 檢查資料。代價是建置時要從 GitHub 下載套件 |
| D6 | 會議紀錄存成結構化 JSON，另外存版本 | 存成 Markdown：沒辦法做時間點跳播和待辦表格，校稿時也很難精準修改 |
| D7 | 模型用 APFS clone 從原本的轉錄工具複製過來 | 直接引用那個路徑：兩個工具會綁在一起。一般複製：磁碟只剩 41 GB，不想多佔 3 GB |
| D8 | 用 Swift Package 搭配建置腳本組出 .app | Xcode 專案：這台沒有 xcodegen，手寫 `.pbxproj` 很容易出錯。`Package.swift` 一樣可以用 Xcode 打開編輯 |
| D9 | 錄音存成 16 kHz WAV，下次啟動時修復檔頭 | m4a：沒有正常結束的話，整個檔案都讀不了（R11.5）。代價是一小時約 115 MB |
| D10 | ad-hoc 簽章 | 開發者憑證：這台沒有。代價是每次重新建置後，系統會再問一次麥克風權限 |
| D11 | 設定存在 UserDefaults | settings.json：原生 app 用 `@AppStorage` 最直接 |

## 15. 已確認事項（2026-10-15）

- 外部會議系統上的舊會議不匯入。
- 第一版不做會議紀錄的手動編輯。
- 改做原生 app，轉錄沿用 Python。系統通知、App 內錄音、拖到 Dock 匯入、選單列圖示，這四項第一版都要做。
