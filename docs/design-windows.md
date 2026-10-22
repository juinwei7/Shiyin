# 拾音 Shiyin Windows 版：設計

> 狀態：草稿（2026-10-20）。對應 `requirements.md` 第 22 節（R26～R28）。
> Mac 版的設計在 `design.md`，本文件以「M§n」指那邊的章節。行為跟 Mac 相同的部分只寫「同 M§n」，這裡只寫 Windows 不同的地方。
> 標「待 T-W#」的選擇要等 §17 的實驗做完才定案；做完後把結果寫進對應的章節和決策紀錄，拿掉標記。
> 套件版本是 2026-10-20 查到的最新版，W1.2 釘死前再確認一次。

## 1. 架構

```
Shiyin.exe（WinUI 3，單一程序，不需要 Python、ffmpeg、系統管理員權限，也不用另外裝 .NET）
 ├─ Capture        WASAPI 擷取（麥克風）＋ WASAPI process loopback（電腦聲音，排除拾音自己和它開的程式）
 │    ├─► RecordingWriter   16 kHz WAV，左聲道麥克風、右聲道電腦聲音（同 M§5.2），WAVRepair
 │    └─► LiveTranscriber   sherpa-onnx：Silero VAD ＋ SenseVoice → 即時逐字稿（暫定＋定稿）
 ├─ JobRunner      三條線（同 M§21.2）
 │    ├─ 轉錄線：解碼（Media Foundation）→ whisper.dll + Breeze → 說話者辨識（sherpa-onnx）＋聲紋比對 → 壓縮成 m4a
 │    ├─ 整理線：整理、校稿、草稿紀錄（LLMBackend，同 M§19）
 │    └─ 輔助線：逐字稿校正、猜說話者
 ├─ ModelStore     下載、續傳、驗證模型
 ├─ AppDatabase    Microsoft.Data.Sqlite（WAL）＋ FTS5 trigram
 ├─ Exporter / Importer
 └─ 視窗           主視窗、首次使用引導、使用說明、回報問題、問所有會議、設定、系統匣小視窗、偵測會議的小視窗
```

專案依「能不能在 Mac 上測」分開。上面六個是 `net10.0`，在這台 Mac 上就能 `dotnet test`，大部分邏輯、甚至轉錄和說話者辨識的評估都不用開虛擬機：

| 專案 | 目標 | 內容 | 在 Mac 上能跑 |
|---|---|---|---|
| `Shiyin.Core` | net10.0（只用 BCL） | 資料模型、MeetingLanguage、時間格式、提示詞和 schema、範本、匯出（txt、md、srt、vtt、html）、封包的 JSON、匯入的解析、說話者對應、合併和復原、聲紋比對規則、分不準的判斷、收音提醒、語言偵測的累積、whisper 的提示詞和接縫、版本比較、翻譯查表 | 能 |
| `Shiyin.Data` | net10.0 | migration v1～v9、FTS rowid、搜尋、工作佇列、校正、草稿 | 能 |
| `Shiyin.LLM` | net10.0 | 六種整理工具、重試、退回 JSON 模式；「找外部程式」是介面，Windows 的實作在 Platform | 能 |
| `Shiyin.Engines` | net10.0 | whisper（P/Invoke shim）、SenseVoice、VAD、說話者辨識、聲紋嵌入 | 能（osx-arm64 的原生檔） |
| `Shiyin.Services` | net10.0 | 不碰畫面的 app 邏輯：設定、模型下載、匯入、封包、轉錄流程、工作佇列、錄音（寫檔、看門狗、收音提醒、即時逐字稿、自動偵測語言、模擬錄音）、會議的操作（對應 Mac 的 `AppModel`、`JobRunner`、`Recorder`）。平台的部分（WASAPI、Media Foundation、睡眠、docx、PDF）是介面 | 能（用 WAV、模擬的音源、假的引擎） |
| `Shiyin.Eval` | net10.0 命令列 | 對應 Mac 的 `transcribe-eval`、`voiceprint-eval`、`detect-eval`，輸出格式相同 | 能 |
| `Shiyin.Platform.Windows` | net10.0-windows10.0.22621 | WASAPI、Media Foundation、認證管理員、快捷鍵、偵測會議、防止睡眠、通知、找 CLI、檔案關聯、更新驗證 | 不能 |
| `Shiyin.App` | WinUI 3（不打包、自帶 Windows App SDK） | 畫面、ViewModel、系統匣、PDF、DEBUG 控制通道 | 不能 |
| `Shiyin.Ctl` | net10.0 | `shiyinctl.exe` | — |

`Shiyin.Core`、`Data`、`LLM` 照 Mac 的 Swift 檔案一個對一個翻（`Model/`、`Notes/`、`Store/`、`IO/`、`Transcribe/TranscriptionPipeline.swift`…），方便以後對照修改。用使用者的錄音評估時用 `Shiyin.Eval` 在 Mac 上跑，錄音不用複製進虛擬機。

## 2. 技術選型

| 需求 | 選擇 | 依據 |
|---|---|---|
| 語言、介面（R26.3） | C# .NET 10（LTS，支援到 2028-11）＋ WinUI 3：Windows App SDK 2.5，**只參考用到的元件套件**（WinUI、Foundation、InteractiveExperiences） | 使用者決定（D42）。整包的 Windows App SDK 會帶進 Windows ML 的 `onnxruntime.dll`，跟 sherpa-onnx 的同名檔衝突 |
| 麥克風（R1.1） | NAudio 3.3（MIT，`NAudio.Wasapi`）；不行就用 CsWin32 自己包 WASAPI | 3.x 是 2026-08 才出的新大版，版本釘死 |
| 電腦聲音（R1.2） | WASAPI process loopback，`EXCLUDE_TARGET_PROCESS_TREE` 排除拾音自己（NAudio 3 的 `WithProcessLoopback`） | 對應 Mac 的「全域 tap 排除自己」；需要 build 20348 以上，Windows 11 都有 |
| 即時逐字稿（R2） | sherpa-onnx 1.13（Apache-2.0）：Silero VAD ＋ SenseVoice-Small int8；中文用 OpenccNetLib（MIT，s2twp）轉台灣繁體 | Windows 沒有可用的內建離線中文辨識（D44）；T-W5 的結果見 §7、D61 |
| 快速辨識（R4.1、R20.8） | 同一個 SenseVoice 模型讀整個檔案 | 取代 macOS 內建（D45） |
| 正式轉錄（R4） | whisper.cpp b5454（v1.9.5、commit `d1be6fd`，跟 `Package.swift` 同一版）自己建：x64 CPU 各種指令集＋Vulkan；ARM64 CPU 各種指令集。加一層小的 C shim，C# 用 P/Invoke | 結果要跟 Mac 一致（R28.4，D46、D56），待 T-W3 |
| 語言偵測（R16） | 同上的 whisper ＋ Whisper base（同 M§18.8） | 同一個模型和門檻；要中英日三個機率，Whisper.net 拿不到 |
| 說話者辨識、聲紋（R5、R19.3） | sherpa-onnx 的說話者分段（pyannote segmentation-3.0，6 MB）切段落＋聲紋嵌入（3D-Speaker ERes2Net base，512 維，40 MB），自己分群 | 有 .NET 套件、跑在 CPU（D47、D58） |
| 解碼、壓縮 m4a | Media Foundation（NAudio 的 `MediaFoundationReader`、`MediaFoundationEncoder`）；AIFF 用 NAudio 的 `AiffFileReader` | 系統內建，不用 ffmpeg（R4.5）；解得開的格式見 §8 |
| 播放（R8.2） | `Windows.Media.Playback.MediaPlayer` 的 `PlaybackRate` | 變速時有沒有保持音高待 T-W9 |
| 資料庫 | Microsoft.Data.Sqlite 10 ＋ SQLitePCLRaw 2.1（自帶 SQLite，含 FTS5、有 win-arm64），Dapper | trigram 要 SQLite 3.34 以上，不能用系統的 |
| API key | 認證管理員（CredWriteW／CredReadW，CsWin32 產生呼叫） | 對應鑰匙圈（D49） |
| 設定 | `SHIYIN_HOME\settings.json` | 對應 UserDefaults（D48） |
| 通知 | Windows App SDK 的 AppNotification | 有按鈕、點了帶參數打開拾音 |
| 系統匣 | WinUIEx 2.9（MIT，TrayIcon；檔案總管重開後自動恢復）＋自己做的小視窗 | H.NotifyIcon 的新版還在 beta |
| docx | DocumentFormat.OpenXml 3.5（MIT），直接從會議紀錄的資料產生 | Windows 沒有 NSAttributedString 的 docx 輸出 |
| PDF | WebView2 把跟 Mac 同一份匯出的 HTML 印成 PDF | Windows 11 內建 WebView2 執行環境 |
| 安裝、更新 | Velopack 1.2（MIT；1.2.158 被標為不要用）＋ 自己的更新金鑰（ECDSA P-256） | 不用系統管理員、有差異更新（D50）；虛擬機驗過安裝、更新、解除安裝（§14） |
| UI 自動化（驗證） | Windows 內建的 UIAutomationClient（PowerShell）；截圖在 Mac 上截 UTM 的視窗 | §15.4 |

授權寫進第三方授權（R27 R13.1）：`scripts/windows/make-third-party-licenses.py` 依 app 實際用到的套件（`dotnet list package --include-transitive`）組出 `windows/src/Shiyin.App/ThirdPartyLicenses.txt`，「關於拾音」和安裝資料夾的 `Third-Party Licenses.txt` 都是這份。做法同 Mac（M§12.1）：條文取套件附的檔案，或套件記的原始碼 commit 上的檔案，原文不改；套件清單多了腳本不認得的套件就停下來。要特別標示的（2026-10-21 查過）：
- 快速辨識的模型是原版 SenseVoice-Small（2024-07-17，sherpa-onnx 轉成 int8，D61），FunASR Model License 不是 OSI 授權，附原文
- 3D-Speaker 的聲紋模型：ModelScope 的模型頁標 Apache License 2.0
- Windows App SDK、WebView2 是 Microsoft 的授權條款（可以跟 app 一起散布，不是開源授權），照附
- whisper 的 DLL 靜態連結 C/C++ 執行階段（§8），不用附 VC++ 可轉散發套件；OpenMP 關掉了，也不用 `libomp140.aarch64.dll`
- 模型不在安裝檔裡（第一次用時從 Hugging Face、GitHub 下載），一樣列出來源和授權

## 3. 檔案配置

**原始碼**：

```
windows/
├── Shiyin.Windows.sln
├── global.json                 固定 .NET SDK 10.0.x
├── Directory.Packages.props    所有套件的版本
├── Directory.Build.props       ArtifactsPath：建置輸出放到 repo 外（Mac 是 ~/Library/Caches/ShiyinBuild-win）
├── src/Shiyin.Core/  Shiyin.Data/  Shiyin.LLM/  Shiyin.Engines/  Shiyin.Eval/
├── src/Shiyin.Platform.Windows/  Shiyin.App/  Shiyin.Ctl/  Shiyin.Services/
├── tests/Shiyin.Tests/         xUnit：標準答案、資料庫、整理工具、服務（Mac、CI 都跑）；畫面用虛擬機的 UI Automation 驗（§15.4）
└── native/whisper/             CMake 腳本、shim、各平台建出來的檔案的 SHA-256
shared/golden/                  Mac 產生的標準答案（§4）
scripts/windows/                build-whisper.ps1、pack.ps1、update-key.cs、make-third-party-licenses.py；vm/ 驗證用虛擬機的工具（§15.2）
```

建置輸出不能放在專案裡（桌面有 iCloud 同步，同 CLAUDE.md）。

**資料**（`%LOCALAPPDATA%\Shiyin`，環境變數 `SHIYIN_HOME` 可以改；預設的 ACL 只有這個使用者讀得到）：

```
shiyin.sqlite
settings.json                    設定（§12.8）
audio\<會議id>.m4a｜.wav
audio\recording\<會議id>-<uuid>.wav   錄音中的檔案
models\breeze-asr-25-q8_0.bin     跟 Mac 同一份網址和 SHA-256（M§7）
models\ggml-large-v3-turbo-q5_0.bin
models\ggml-base-q5_1.bin
models\sensevoice\                快速辨識、即時逐字稿
models\vad\
models\diarization\               說話者分段、聲紋嵌入
backups\  tmp\  logs\
```

**安裝位置**跟資料分開：Velopack 預設裝在 `%LOCALAPPDATA%\<套件代號>`，解除安裝時刪掉整個資料夾。套件代號用 `ShiyinApp`（裝在 `%LOCALAPPDATA%\ShiyinApp`），不能跟資料資料夾同名（R27：解除安裝不刪錄音和資料庫）。

## 4. 跟 Mac 一致：標準答案（R28）

### 4.1 Mac 產生標準答案

輸入是手寫的 `shared/golden/fixtures.json`：三場測試會議（中文、英文、日文：有說話者名字、低信心段落、重點標記、使用者筆記、詞彙表、完整的會議紀錄、待辦、自訂段落、待確認）、各種邊界的小案例（時間格式、檔名、匯入、模型回傳的檢查），以及一串資料庫操作（`db`：匯入、搜尋、聲紋、合併、復原、校正、工作佇列…，每一步後倒出資料表）。

Mac 的 DEBUG 控制通道加 `shiyinctl dump-golden <fixtures.json> <輸出資料夾>`（`Debug/GoldenDump.swift`，只在 DEBUG 版，R26.5）。它用 Mac 的程式算出結果寫進 `shared/golden/expected/`；資料庫在暫存資料夾另開一個，不碰使用者的資料。跑兩次結果相同（W1.1 驗證時跑了三次）。輸出：

| 檔案 | 內容 |
|---|---|
| `prompts/<語言>/` | 組好的提示詞：整理（五個內建範本、自訂範本）、修訂、逐字稿校正（全部、一批）、猜說話者、對一場會議提問、問所有會議、抽關鍵詞的 system 和 user。草稿用的是整理同一份提示詞 |
| `schemas/` | JSON schema、Anthropic 用的工具名稱和說明 |
| `templates/` | 內建範本（中英日）、段落名稱、範本定義的寬鬆解碼 |
| `whisper/` | whisper 的 initial prompt（詞彙表）、下一段的提示詞（前一段結尾）、幻覺字串、壓縮比 |
| `export/<語言>/` | 會議紀錄的 md、純文字、HTML；逐字稿的 txt、md、HTML（時間、說話者四種組合）、srt、vtt |
| `package/<語言>/` | .shiyin 裡的 manifest.json、meeting.json、transcript.json（內容，不比 zip 的位元組） |
| `pipeline/` | 合成訊號的分段點、語言偵測收集的結果、接縫和回音的比對、說話者編號、分不準的判斷、重新辨識的名字沿用 |
| `db/` | `schema.sql`（跑完 migration v1～v9 後 `sqlite_master` 的 SQL）、每一步資料庫操作後的資料表和回傳值（含搜尋、詞彙建議、聲紋比對） |
| `util/` | 時間、日期、檔名、匯入、模型回傳的檢查等小工具的結果 |
| `llm/mac-requests.jsonl` | Mac 的 DEBUG 版接假伺服器（`scripts/dev/fake-llm-server.py`）實際送出的四種 API 請求；Windows 送同樣的提示詞，body 要解析後相同 |

### 4.2 Windows 比對

`tests/` 讀 `fixtures.json`，用 C# 產生同樣的東西，跟 `shared/golden/` 比對：

- **逐位元組**：提示詞、schema 的文字、範本、md、txt、srt、vtt、HTML、`schema.sql`。匯出一律 UTF-8 無 BOM、換行 `\n`，跟 Mac 相同。
  - `shared/golden/**` 在 `.gitattributes` 設成 `-text`：Windows 上的 git（`core.autocrlf`）取出時會把換行改成 CRLF，逐位元組比對全部失敗（2026-10-22 第一次跑 CI 時，x64、ARM64 各 13 個失敗）。
- **比解析後的內容**：JSON 檔（Mac 的 JSONEncoder 輸出 `"key" : value`、鍵有排序、不跳脫 `/`，System.Text.Json 不同）和送給整理工具的請求 body（M§19.5 本來就不比位元組）。
- **容許誤差**：低信心的壓縮比（zlib）兩邊的壓縮器輸出大小不同，允許 ±0.05；測試資料避開門檻 2.4 附近。說話者分得準不準的凝聚度是浮點數的加總，順序不同，比到小數 6 位。
- **Mac 每次結果不同的地方**（W1.1 跑三次比對時發現，Mac 的行為不改，R26.5）：
  - 沒有 `sortedKeys` 的 JSONEncoder 每次啟動鍵的順序不同：範本的定義（`TemplateDefinition.jsonString()`，所以 Mac 每次啟動都會覆寫內建範本，沒有害處）、說話者的復原資料。標準答案先重新排序；Windows 寫的時候用宣告的順序，讀的時候只看內容
  - 字典的順序不固定：一段逐字稿跟兩位說話者重疊一樣多時，Mac 選哪一位不一定（`Diarizer.assignNumbering`、重新辨識的名字沿用）。Windows 一律選編號小的；測試資料避開剛好一樣的情況
- **Swift 的字串規則**（不照做提示詞就會差一個字）：長度、截斷用字形叢集；排序用 Unicode 純量值（不是 UTF-16）；`rounded()` 是四捨五入；正規表示式的 `\d` 是 `Character.isNumber`，包括「一」「十」「兩」「萬」這些 Unihan 有數值的漢字（「說話者 一」也算預設名稱）。都放在 `Shiyin.Core/Text/SwiftText.cs`
- **.shiyin 雙向匯入**：Mac 匯出的封包 Windows 匯入、Windows 匯出的 Mac 匯入，內容完整（R28.3）。這是最能抓到格式差異的測試。

### 4.3 以後 Mac 改了提示詞或匯出

Mac 改完重跑 `dump-golden`、commit 新的 `shared/golden/`，Windows 的測試就會失敗，照著改 C#。CI 每次推送都跑，所以不會漏掉。這條規則寫進 CLAUDE.md。

## 5. 資料庫

- Microsoft.Data.Sqlite ＋ SQLitePCLRaw 的 e_sqlite3（W1 確認版本 ≥ 3.34、有 FTS5 trigram）。連線設定照 GRDB：WAL、`foreign_keys=ON`、busy timeout；一條寫入連線＋讀取連線（對應 DatabasePool）。
- migration v1～v9 的 SQL 從 `Sources/Shiyin/Store/AppDatabase.swift:51-243` 照抄。也建立 GRDB 用的 `grdb_migrations` 表、寫入同樣的識別字，**Mac 的資料庫檔案可以直接在 Windows 打開**（用使用者的真實資料測 migration、搜尋時用得到）。以後 Mac 加 v10，Windows 用同樣的識別字加。
- 遷移前先備份到 `backups\shiyin-<時間>-before-<vN>.sqlite`（同 Mac 的程式）。
- 日期、JSON 欄位、FTS rowid 的算法（D27）跟 Mac 相同；聲紋嵌入是 Float32 little-endian 的 BLOB。
- 聲紋的模型跟 Mac 不同（§9）：比對時長度不符的聲紋略過，不改 schema。

## 6. 錄音

- **麥克風**：WASAPI 共用模式、事件驅動，用 Console／Multimedia 角色的端點，**不用 Communications**（Windows 會把其他聲音自動降低 80%）。裝置用端點 ID 記在設定。換裝置、拔耳機：NAudio 3.2 起的 `MMDeviceEnumerator.CreateNotificationClient()` 事件（3.0 起 `IMMNotificationClient` 不公開了），收到就重新開（D25）。
- **電腦聲音**：process loopback 排除拾音的程序樹（claude CLI、WebView2 這些子程序也一起排除）。格式自己指定（沒有 GetMixFormat）：48 kHz 浮點雙聲道，再降混、重新取樣。輸出裝置換了要不要重開待 T-W2。
- **沒有聲音在播時，電腦聲音完全不送資料**（連靜音都不送）。所以：
  - 寫檔時電腦聲音那一聲道依時間（QPC）補靜音，不是等它送資料。
  - 3 秒 watchdog（M§20.2）只看麥克風；電腦聲音沒有資料不算中斷。
  - 「電腦聲音一直沒聲音」的提醒（M§26）把沒有資料當成靜音計時。
- **轉成 16 kHz 單聲道**：先請系統直接給 16 kHz 單聲道浮點（`AUTOCONVERTPCM`＋`SRC_DEFAULT_QUALITY`）；麥克風不接受時用裝置的格式，每個來源一個重新取樣器（NAudio 的 `WdlResampler`，純 C#，在 `Services/Capture/StreamResampler.cs`），狀態跨 buffer 保留（對應 `StreamConverter`）。NAudio 3.3 版本釘死，升級時重跑轉錄的比對。
- **電腦聲音補靜音**：音源自己依時間（Stopwatch）補：100 毫秒沒有資料、而且送出的量落後時間超過 0.1 秒時，補到落後 50 毫秒。寫檔的對齊規則因此跟 Mac 完全相同（`RecordingWriter` 照搬）。
- **寫檔**：自己寫 RIFF，格式、雙聲道對齊、補靜音的規則跟 `Capture/RecordingWriter.swift` 相同；每幾秒更新一次標頭，意外結束後用 WAVRepair（照搬）修。暫停時丟掉樣本。
  - 跟 Mac 不同：復原時錄音檔只有 44 bytes 的標頭（錄音開始一兩秒內就當掉，資料還在緩衝區）就刪掉，空的會議一起清掉。Mac 會一直留著、每次開啟都再失敗一次（2026-10-21 在虛擬機看到；Mac 版之後再改）
- **音量、收音提醒、磁碟空間**：算法和門檻同 M§26（`Recorder.checkLevels`、1 GB／300 MB），放在 Core，用標準答案比對。音量條的畫法見 §11.2。
- **防止睡眠**（R1.9）：`PowerCreateRequest`＋`PowerSetRequest(PowerRequestSystemRequired)`，整個程序有效；錄音和工作結束時清掉。`powercfg /requests` 看得到。
- **麥克風權限**（R27 R1.8）：開啟時回 `E_ACCESSDENIED` 就顯示「拾音沒有麥克風權限」，按「打開設定」打開 `ms-settings:privacy-microphone`（固定的網址）。Windows 之後如果對桌面 app 也個別詢問，照同樣的流程。
- **開始失敗、中途出問題**：同 Mac 的「錄音的提醒」，Recorder 記下原因（`FailureMessage`、`PermissionDenied`），主視窗顯示一次就清掉。原因要保留原本的說明（例如「找不到可以用的麥克風」），不要換成錯誤代碼。2026-10-21 在沒有音效裝置的虛擬機發現：按「錄音」畫面完全沒有反應（原因沒有顯示）。

## 7. 即時逐字稿（R2、R27 R2.1）

- 混音（左右平均，同 Mac 的 MixTap）先經過自動增益，再送進 Silero VAD（門檻 0.35，D61）；VAD 判定在說話時，約每 0.4 秒把這一句目前的聲音整句丟給 SenseVoice 重新辨識，結果當暫定文字；VAD 判定一句結束時（一句最長約 15 秒，超過就切），最後一次的結果當定稿，存成 `kind='live'` 的段落（同 M§5.3）。
- SenseVoice 的語言跟著會議語言（zh、en、ja）；開 ITN（標點、數字）；拿掉情緒、事件標籤。
- 中文輸出用 OpenccNetLib 的 s2twp 轉成台灣繁體（字典已內建）。
- 在背景執行緒、低於一般的優先順序跑，不能拖到錄音寫檔（R25.2）。
- **自動增益**（D61）：遠距收音的會議很小聲（AISHELL-4 的音量中位數 −46 dBFS），VAD 只抓到兩成的時間。看最近 30 秒的音量（每 0.1 秒一格，第 90 百分位當說話的音量），放大到約 −22 dBFS，最多 24 dB，不縮小，每秒更新；只影響送進 VAD 和辨識的聲音，錄音檔不變。快速辨識也一樣。
- T-W5（Mac 上，2026-10-21）：合成會議（91 秒）照錄音的速度餵，定稿延遲中位數 0.62 秒、第 90 百分位 0.79 秒，暫定文字更新 148 次；轉出來是台灣繁體。換成 D61 的設定後，各語言的前 5 分鐘照錄音的速度餵：AISHELL-4（中文、遠距）錯字率 18.3%、延遲中位數 0.58 秒／第 90 百分位 0.83 秒；AMI（英文）WER 33.8%（原本 56.9%）、0.52／0.76 秒；日文國會聽證有假名了（原本只剩漢字）、0.50／0.68 秒。虛擬機的 CPU 用量待 W0.1。
- 自動偵測語言（R16）：照 M§18.8，用 whisper base；切換語言時即時辨識換語言，時間偏移照舊。

## 8. 正式轉錄

- **whisper**：`native/whisper/` 的 CMake 腳本從 commit `d1be6fd` 建置，CI 建好、記下 SHA-256 放進安裝檔，不從別處下載。C/C++ 執行階段靜態連結（`CMAKE_MSVC_RUNTIME_LIBRARY`），沒裝 VC++ 可轉散發套件的電腦也載得起來；`build-whisper.ps1` 檢查 DLL 沒有匯入 VC++ 的 DLL（sherpa-onnx、onnxruntime 的 DLL 本來就是靜態連結，2026-10-21 看過匯入表）。
  - x64：`GGML_BACKEND_DL`＋`GGML_CPU_ALL_VARIANTS`（執行時依 CPU 指令集選），加 `ggml-vulkan.dll`（官方的 Windows 檔案沒有 Vulkan，要自己建）。
  - ARM64：CPU 也建多個版本。只建一個新指令集（i8mm）的版本，舊的 Snapdragon 會碰到不支援的指令而當掉。GPU（OpenCL-Adreno）還是實驗性，先不用。
  - Mac 也建一份 dylib，`Shiyin.Engines` 的測試在 Mac 上就能跑。官方 zip 裡的 `whisper-cli.exe` 拿來交叉比對結果。
- **NVIDIA 顯示卡加速**（R27 R4.6，D64）：x64 有 NVIDIA 顯示卡時（顯示卡驅動的登錄裡 `MatchingDeviceId` 有 `VEN_10DE`），第一次啟動自動下載 whisper.cpp 官方同一版（b5454）的 `whisper-bin-win-cuda-12.4.0-x64.zip`（685 MB，固定網址和 SHA-256，`ModelStore.Model.GpuCuda`），驗證後只解出 whisper、ggml、ggml-base、ggml-cpu-*、ggml-cuda、cudart、cublas、cublasLt（約 1.1 GB）到 `models\gpu-cuda12\`，刪掉壓縮檔。自動下載只做一次（設定的 `gpuPackOffered`），使用者刪掉後不再下載。
  - 載入：整套 whisper 都用元件資料夾裡的（同一次建置的 ggml 才能搭配 ggml-cuda），不跟安裝資料夾的 CPU 版混用。第一次用到 whisper 之前，把安裝資料夾的 `shiyin_whisper.dll` 和 VC++ 執行階段（msvcp140、vcruntime140、vcruntime140_1、vcomp140）複製過去（不一樣才複製，app 更新後 shim 也跟著換），`SetDllDirectory` 加上元件資料夾，再從那裡載入 shim。ggml 用 `LoadLibraryW` 載入 `ggml-cuda.dll`，它要的 cudart、cublas 只會照一般的順序找（安裝資料夾、系統），所以一定要加 `SetDllDirectory`。
  - whisper 已經從安裝資料夾載入（這次開啟已經轉錄過）時換不了，下載好時提示重新開啟拾音。驅動太舊、沒有 CUDA 裝置時 ggml-cuda 不會有裝置，whisper 自己用 CPU。
  - shim 的 `shiyin_gpu_device` 回傳 whisper 會用的顯示卡（第一個 GPU 裝置的名稱和說明），寫進 log 和回報問題。
  - 元件靠安裝檔裡的 VC++ 執行階段：CI 改成靜態連結執行階段的 whisper 之後，安裝檔還是要帶這四個 DLL（x64 約 0.9 MB）。
- **shim**（D56）：約 100 行的 C，匯出 `shiyin_whisper_full(ctx, const ShiyinParams*, 進度 callback, 取消 callback, …)`，在 C 這邊填好 `whisper_full_params`。不在 C# 複製這個 struct：它用傳值的方式傳、C 的 `bool` 是 1 byte、裡面有巢狀 struct 和 callback，很容易對錯而且不會報錯。其他簡單的函式（`whisper_full_n_segments`、`whisper_full_get_segment_*`、`whisper_full_get_token_p`、`whisper_pcm_to_mel`、`whisper_lang_auto_detect`、`whisper_lang_id`…）直接 P/Invoke，跟 `WhisperEngine.swift`、`LanguageDetector.swift` 用的一樣。
- **參數**同 Mac：beam 5、best_of 5、執行緒數 clamp(核心數 − 2, 2, 8)、`suppress_blank`、有顯卡時用 GPU。只有 CPU 時要不要改（beam、執行緒）待 T-W3，改了寫進決策紀錄、設定頁顯示。
- **分段、提示詞、接縫、過濾、低信心**：同 M§21.16、M§6.2，程式在 Core，用 §4 的 `pipeline/` 比對。
- **快速辨識**（R4.1、R20.8）：SenseVoice＋VAD 讀整個檔案。Breeze 還沒下載好時自動用（同 D35），轉完提示「可以用 Breeze 重新轉錄」。錯字率（D61 的設定，2026-10-21）：合成會議 9.2%（Mac 的內建辨識 9.2%、Breeze 6.0%）、AISHELL-4 26.4%（Mac 的內建辨識約 25%）、AMI 英文 WER 39.0%（約 26%）、日文合成會議 16.8%、國會聽證 21.7%／28.1%、FLEURS 15.5%（日文的 Whisper large-v3-turbo 是 4.5%、9.2%／7.7%）。91 秒的錄音 2.4 秒、38 分鐘 14 秒轉完（M3 Pro）。快速辨識的模型還沒下載好時，轉錄線先不接工作、自動開始下載（Mac 的內建辨識不用下載）。
- **T-W3 的 Mac 部分**（2026-10-21）：C# 經 shim 呼叫同一版 whisper.cpp（Mac 的 dylib、Metal），餵一樣的取樣時結果跟 Mac 逐段相同（國會聽證 190/190 段、AMI 198/198 段）；m4a 解碼器不同造成的取樣差異會讓少數段落不同（合成會議 27 段裡 2 段，錯字率 5.2% → 6.0%），Windows 用 Media Foundation 解碼後要再比一次。速度：AMI 17.5 分鐘 98 秒、國會聽證 15.3 分鐘 51 秒（M3 Pro、Metal）。虛擬機、CI 的 CPU 倍速待 W0.4。
- **快速辨識的模型怎麼來**（R27 R20.8）：首次使用引導的「模型」步驟先下載快速辨識和 VAD（約 240 MB，比 Breeze 小很多），下載好就能錄音，Breeze 接著在背景下載。要不要直接附在安裝檔，T-W7 量過安裝檔和差異更新的大小再決定。
- **解碼**：Media Foundation 的 SourceReader 依序讀、轉成 16 kHz 單聲道浮點（同 D34 的串流讀法）。Media Foundation 不支援 CAF、AIFF：AIFF 用 `AiffFileReader`，CAF 不支援（不在匯入的格式清單裡）。T-W9（2026-10-21，UUP 做的 Windows 11 映像檔）：m4a、mp3、aac、flac、aiff、aif、wma、mp4、mov、m4v、wmv、webm 都解得開，同一段 91 秒的錄音每種都轉出同樣的 10 段；ogg、opus 是 Ogg 容器，要「Web 媒體延伸模組」（一般的 Windows 11 有預裝），沒有時錯誤是 `MF_E_UNSUPPORTED_BYTESTREAM_TYPE`，畫面說明可以先轉檔或去 Microsoft Store 裝（不顯示內部的檔名）。日文、中文的 PDF（WebView2）字型正常。Windows 的 N 版沒有 Media Foundation，要裝 Media Feature Pack（寫進系統需求）。
- **結束拾音**時等 whisper 收尾最多 10 秒（R18.12）。

## 9. 說話者辨識與聲紋

sherpa-onnx 的說話者分段是凝聚式分群（`FastClusteringConfig` 只有「剛好 N 群」或門檻），**沒有 PLDA、VBx、「至少 N 人」，也不回傳每個人的聲紋**。用它原本的門檻分群（D47 的 A 方案），中文會議（AISHELL-4）不管門檻怎麼調都會把 6 個人併成 1～3 人：整場錄音的聲紋都很像（同一個房間、同一支麥克風），同一人、不同人的相似度分不開。所以改成（D58）：

1. **sherpa-onnx 只用來切段落**：固定分成 30 群（分得很細，切出的段落才不會跨兩個人；錄音很短時不超過每 10 秒一群），pyannote 的窗每次移 0.5（sherpa-onnx 預設 0.1，快五倍、結果差不多）。
2. **每段切成約 3 秒的小段**，各算一次聲紋（3D-Speaker ERes2Net base，中文 3D-Speaker 資料集訓練，512 維，40 MB）。不到 0.5 秒的不算。
3. **重新分群**（`Core/Transcribe/SpeakerClustering.cs`）：先減掉整場的平均聲紋再比 cosine；凝聚式分群，群中心是依長度加權的和，最像的兩群低於 0.25 就停，有「至少 N 人」時合併到剩 N 群也停。說話總長不到 30 秒（而且不超過總長的 5%，短的會議不會把話少的人併掉）的群，每個小段各自併到最像的大群，但至少留 N 群。最後看「不減平均」的群中心（但不少於「至少 N 人」）：只有一個人說話的錄音，減掉平均後剩下的是雜訊，會被拆成好幾群（2026-10-21 在虛擬機轉錄合成會議分出 6 人才發現）。兩步：最像的兩群在 0.7 以上就合併，重複到沒有；剩下的群每一對都在 0.6 以上，就是只有一個人，全部合併。每段取它小段裡長度最多的群；沒有小段的短段落跟著 sherpa 的同一位最常被分到的群。
   - 門檻怎麼定的：分群後的群（各 20 秒以上）不減平均的中心相似度，不同的人最高 0.58（AMI ES2004c 的陣列麥克風，遠距收音時房間的共同成分重），同一個人被拆開的群最低 0.63（AMI 剪出的 5 段單人錄音、合成會議）。只用一個 0.55 的門檻時，陣列麥克風那場的兩位女性被併成一位、聲紋帶錯名字，所以分兩步；相鄰兩群 0.65～0.75、全部 0.58～0.6 的結果都一樣
   - 結果：單人錄音 6 段都是 1 位；AMI 混音 4 人 94.9%、AISHELL-4 6 人 96.6%（跟沒有這一步時一樣）；AMI 9 場只有 TS3003a 從 4 位變 2 位（原本把說最多的 A 拆成三群，另外兩位只說了 7 秒、6 秒），陣列麥克風那場維持 4 位；聲紋比對 17 位帶入 16 位、帶錯 0、陌生人 0（同之前）
   - 分不準的提醒（第 5 點）：一個窗裡 sherpa 的編號聲紋（不減平均）在 0.6 以上的先當成同一個聲音再算碰撞，不然單人錄音的碰撞率很高、會誤報。單人錄音 0；AMI 9 場正常分群 0.19～0.32、AISHELL-4 0.18；故意併成一人 0.42～1，門檻 0.37 不變
4. **每位說話者的聲紋**：最長的幾段（加起來到 60 秒）的小段聲紋依長度加權平均（Mac 是最長的幾段各算一次）。
5. **分不準的提醒**（D39）：每 10 秒一窗，窗裡每個 sherpa 的編號（分得很細）說了 1.5 秒以上的，用那幾個小段的聲紋平均當「片段的說話者」，算法照 `Diarizer.quality`（凝聚度、只分出一群時看碰撞率）。

結果（Mac M3 Pro、5 個執行緒，2026-10-21；Mac 的 FluidAudio 在括號裡）：

| 錄音 | 人數 | 單人說話的時間裡標對 | 處理時間 |
|---|---|---|---|
| AMI ES2004a（英文，17.5 分鐘） | 4／4（4） | 94.9%（94.6%） | 50 秒 |
| AISHELL-4（中文，38 分鐘） | 6／6（6） | 96.6%（96.7%） | 104 秒 |
| AMI 9 場（`ami-vp`） | 7 場剛好 4 人，ES2004b 多 1 人（多的那位 81 秒、一半是別人），ES2002a 少 1 人（只說 45 秒的那位） | 逐 0.1 秒跟標註比平均 88%（Mac 只看人數，9 場都對） | 12～144 秒 |

- 門檻 0.2～0.35、最小群 20～45 秒在這三組資料上都差不多，取中間。sherpa 內部改用 CAM++ 只快 30%，AISHELL-4 會多分 1 人；兩段都用 CAM++ 時 AISHELL-4 掉到 87.6%。
- **聲紋比對門檻 0.6**（Mac 0.65）：AMI ES2004a 學、b～d 和遠距麥克風比對，同一人最低 0.632（遠距麥克風）、最高 0.916；別組 4 場的陌生人跟名單裡任何人最高 0.535。`voiceprint-eval.py`：0.55～0.70 自動帶入 16/17（沒帶入的是一半是別人的那一群）、帶錯 0、陌生人被帶入 0。間距比 Mac 小（兩邊各約 0.03～0.07），使用者實際用了再看。
- **分不準的提示門檻 0.37**（Mac 0.4）：AMI 9 場正常分群的分數 0.21～0.33、AISHELL-4 0.19；故意併成 1 人時 0.34～0.48（9 場有 8 場超過 0.37）、併成 2 人時 0.30～0.40。
- 使用者的麻將錄音（手機遠距收音、洗牌聲）還沒試：在 Windows 版拿到使用者同意後跑（T-W4 的最後一項）。
- 合併、改句子的說話者、復原、重新辨識的邏輯同 M§23、M§24（在 Core、Services）。

## 10. 整理工具

- 六種後端照 `Notes/Backends/` 翻成 C#（HttpClient），重試、取消、錯誤分類同 M§19、M§21。
- **找 claude、codex**（T-W6）：claude 先找原生安裝的 `%USERPROFILE%\.local\bin\claude.exe`、PATH 裡的 `claude.exe`，再找 npm 的；codex 找 npm 套件裡真正的執行檔。**不能透過 `.cmd` 執行**：cmd.exe 的命令列只有 8191 個字元、跳脫規則不同，`--system-prompt`、`--json-schema` 會被截斷或解讀錯，還有參數注入的風險（BatBadBut 那一類）。找不到真正的執行檔時用 `node.exe` 加 js 的路徑。
  - npm 的全域資料夾：官方安裝檔是 `%APPDATA%\npm`；zip 版的 Node、nvm、scoop、Volta 在別的地方，所以也找 PATH 裡有 `claude.cmd`、`codex.cmd` 的資料夾（只拿來定位套件，不執行 .cmd）。
  - 套件的樣子（2026-10-21 虛擬機，claude-code 2.1.306、codex 最新版）：claude-code 的 npm 套件直接帶 `bin\claude.exe`（已經沒有 `cli.js`，舊版才用 node 跑 cli.js）；codex 的執行檔在平台子套件 `@openai/codex-win32-<arm64|x64>/vendor/<平台>/bin/codex.exe`，旁邊的 `codex-path`（有 rg.exe）加進子程序的 PATH（同 codex.js）。
  - 長度：逐字稿和提示詞走標準輸入（claude 的 `-p`、codex 的 `exec -`），命令列只放系統提示詞和 JSON schema（標準答案裡最長的約 2,400 字和 1,800 字），遠低於 Windows 命令列 32,767 字元的上限。
  - 虛擬機驗過：找到兩個 npm 版、沒登入時設定頁顯示「沒有登入」、「測試整理工具」的錯誤訊息（「…還沒登入或登入已過期…（終端機指令：claude auth login／codex login）」），「登入 Claude」開出終端機跑登入流程。真的整理一次要使用者登入。
- 參數一律用 `ProcessStartInfo.ArgumentList`（同 CLAUDE.md 的規則）；子程序的 PATH 用 `;` 接上找到的資料夾；工作目錄是 `tmp` 下的空資料夾。
- 子程序放進 Job Object（`KILL_ON_JOB_CLOSE`）：拾音當掉時子程序一起結束；逾時時整棵程序樹一起結束。
- 登入：用固定參數開 Windows Terminal（沒有就用 `cmd.exe /k`）執行 `claude` 或 `codex login`，參數裡不放任何使用者內容。

## 11. 介面

### 11.1 視窗對照

| Mac | Windows |
|---|---|
| 主視窗（NavigationSplitView：側欄、搜尋、會議內容） | 主視窗：左邊會議清單（含資料夾、搜尋框、垃圾桶、匯入），右邊會議內容 |
| 會議頁的分頁（摘要、詳細、逐字稿、待確認、筆記、提問） | `SelectorBar`，Ctrl+1～6 |
| 錄音中的畫面、播放列、時間軸 | 同樣的排版；時間軸用 Canvas 放矩形、三角形、圓點 |
| Settings（7 個分頁） | 獨立的設定視窗，上方 `SelectorBar` 7 頁；「進階」頁多一個介面語言（R26.4、D57） |
| 引導、使用說明、回報問題、問所有會議 | 各自的視窗 |
| MenuBarExtra | 系統匣圖示（WinUIEx 的 TrayIcon）：左鍵打開主視窗，右鍵選單有錄音狀態、暫停、停止、進行中的工作、打開、說明、設定、結束。Windows 11 預設把新圖示收進「^」，引導的最後一步說明 |
| 偵測會議的浮動視窗（NSPanel） | 右下角、最上層、不搶焦點的小視窗（`WS_EX_NOACTIVATE`），30 秒收起 |
| sheet、alert、popover | `ContentDialog`、`Flyout` |
| 關掉主視窗 | 隱藏到系統匣，錄音和工作繼續（R1.7）；從系統匣或選單「結束」時，有工作在跑要確認（同 Mac） |

快捷鍵：Mac 的 ⌘ 換成 Ctrl（Ctrl+O 匯入、Ctrl+R 錄音、Ctrl+Shift+P 暫停、Ctrl+B 標記重點、Ctrl+E 匯出、Ctrl+F 搜尋逐字稿、Ctrl+1～6 分頁、空白鍵播放、Ctrl+Delete 丟到垃圾桶）。

**畫面全部用 C# 建，不用 XAML 檔**（D59）：`Shiyin.App/Ui/Ui.cs` 集中顏色、字型和常用元件的寫法；資源只加 `XamlControlsResources`，主題色設成青瓷綠。這樣 Mac 上也能編譯檢查（XAML 編譯器是 Windows 的程式）。

### 11.2 效能（同 R20.3、R25）

- 一直在變的東西（音量條、計時、播放位置）不經過綁定通知整個畫面：音量條用 Composition 的 visual 自己定時讀音量（對應 D40）；計時只更新那一個 TextBlock。
- 逐字稿列表用 `ListView` 的虛擬化，`ContainerContentChanging` 時才建出那一段的畫面（不用 DataTemplate 和綁定），播放、搜尋時只重畫看得到的幾段。
- 服務的事件在背景執行緒觸發，`AppHost.Post` 切回 UI 執行緒；資料改了 0.15 秒內合併成一次重讀（`MainState`）。錄音中的變化（計時、即時文字、提示）只更新錄音的畫面，不重讀資料庫；工作的進度每秒更新一次，只改列表每一列的狀態文字、進度條和會議頁狀態列的文字、進度條：會議沒有增減、標題日期資料夾沒變時不重建列表（整個重建會一直閃，2026-10-21 使用者回報），狀態列的樣子（狀態、階段、在跑的工作）沒變時不換掉（不然「取消」按鈕每秒被換掉一次）。
- 量法同 Mac：DEBUG 的 `metrics` 回報 UI 執行緒最長卡住多久（每 10 毫秒一次的計時器，`metrics reset` 歸零）。T-W10（2026-10-21，虛擬機）：匯入 2,100 段、2.1 小時的逐字稿，只建出看得到的 54 列；一頁一頁捲 61 次最長卡 82 ms、一行一行捲 51 ms、跳著捲到 51 個位置 122 ms（Mac 播放跟隨的標準是 85 ms）。播放跟隨要有音效裝置，虛擬機量不到。
- 錄音中（`record-sim` 1 倍速、即時逐字稿開著）CPU 約一個核心的 31%（虛擬機 4 核；SenseVoice 在程序裡跑，Mac 的 SpeechAnalyzer 在系統的程序裡，不能直接比）。

### 11.2.1 WinUI 的寫法要注意（虛擬機驗證時踩到的）

- 同一個元件不能放進第二個父元件（0x800F1000，UI 執行緒的例外會讓程式結束）：一直留著的元件（輸入框、音量條）重畫時先從舊的父元件拿下來，或一開始建好只切換顯示。
- `TextBox` 要多行時，`AcceptsReturn` 一定要在 `Text` 之前設：單行的 TextBox 設 Text 時只留第一行（範本的說明、回報問題的內容曾經被截掉）。TextBox 把換行存成 `\r`，存進資料庫或提示詞前換成 `\n`。
- `Ui.Bar` 的左欄寬度跟著內容、不會換行：會換行的長文字放中間那欄。
- 閱讀的欄位（摘要、詳細、待確認、提問、筆記、編輯會議紀錄、使用說明）用 `ReadingColumn`（同 Mac：填滿可用的寬度、最寬 680 或 760、靠左）。不能用 StackPanel 設 `MaxWidth` 加靠左：靠左時寬度是內容要的寬度，會換行的文字算出來很窄，整欄擠成一半（2026-10-21 朋友的電腦上看到）。文字後面接時間點用 `Ui.Inline`（文字一欄、時間照內容寬），不能兩欄都給比例寬度（會各分一半）；放在橫的 StackPanel 裡的文字不會換行，長的會被切掉。
- **要宣告支援高 DPI**（`src/Shiyin.App/app.manifest` 的 PerMonitorV2，csproj 的 `ApplicationManifest`）：WinUI 的專案範本會帶這個檔案，純 C# 建的專案沒有，app 會是 DPI unaware，縮放不是 100% 時 Windows 把整個視窗的點陣圖拉大、字會糊（大部分筆電預設 125%～150%；2026-10-22 在虛擬機 150% 時看到，`GetWindowDpiAwarenessContext` 是 0）。Windows App SDK 建置時會把元件註冊合併進這個 manifest。視窗大小的程式本來就是照 `GetDpiForWindow` 換算的邏輯像素。
- 逐字稿每一段的時間點跳到段落真正的開始（`Ui.Time` 的 `at`）：顯示的時間去掉了小數，24.4 秒的段落顯示 00:24，照顯示的時間跳到 24.0 秒，播放位置的高亮會落在上一段。會議紀錄裡的時間點（整理工具寫的字串）照舊。Mac 也是照顯示的時間跳（tasks 的「Mac 的小問題」）。
- 粗體用 `FontWeights.Bold`（`Ui.Text(bold: true)`），不要用 SemiBold：中文、日文沒有 SemiBold 的字重，Windows 會換成明體的假粗體，跟其他文字不一樣。ContentDialog 內建的標題也是 SemiBold，標題要用 `Ui.DialogTitle`（2026-10-22 介面巡檢）。
- 文字加按鈕的一列（狀態列、範本的說明、錄音列）用 `Ui.Bar`：長的文字或會被截的東西放中間那欄，按鈕放右邊照內容寬。全部放在橫的 StackPanel 裡時，英文的按鈕比較寬，會被擠出視窗。放不下時整個藏起來、不要切一半的（錄音列的兩個音量條）用 `FitRow`。逐字稿的搜尋列是 Grid：搜尋框最寬 300、可以縮。
- `TextHighlighter` 要給 `Foreground`：預設是黑色，深色模式下標起來的字看不見。
- 視窗的最小尺寸同 Mac（主視窗 960×620、設定 600×480、其他 520×440，`WindowPlacement.MinimumSize`，不超過螢幕的工作區）。
- 介面巡檢的做法：虛擬機用 `ctl.sh open settings|guide|report|ask|onboarding`、`uia.sh` 按分頁和選單、`shot.sh` 截圖，淺色、深色（改 `AppsUseLightTheme` 再廣播 `ImmersiveColorSet`，app 不用重開）、中文、英文（`--set interfaceLanguage=en`）、最小視窗（`SetWindowPos` 拉到比最小還小）各走一次；測試資料夾裡有各種狀態的會議（整理失敗、猜到名字、校正建議、分不準的提醒、待確認、日文）。螢幕縮放 125%、150% 還沒試（虛擬機要登出才換得了）。
- 可以選取的 `TextBlock` 有自己的右鍵選單（拷貝、全選），會蓋掉外層的 `ContextFlyout`：文字上也要設同一個選單。
- 快捷鍵掛在整個視窗時設 `KeyboardAcceleratorPlacementMode = Hidden`，不然滑鼠停在視窗裡會跳出快捷鍵提示。
- 無障礙名稱（螢幕閱讀器、UI Automation）：只有圖示的按鈕、沒有標題的輸入框和下拉選單、`ListViewItem`（內容是好幾個文字元件時，名稱會是空的或資料的型別名稱）都要設 `AutomationProperties.Name`。新的畫面用虛擬機的 `uia.sh dump` 檢查。
- 錄音開始時先接好每個聲道的 `OnSamples`，全部接好再 `Start()`：模擬錄音的第二個聲道是第一個的鏡像，第一個一開始就送資料，第二個還沒接上的話開頭那幾段會丟掉、兩個聲道對不齊（CI 的 macOS 機器比較慢時 `RecorderTests` 失敗）。真的錄音（WASAPI）順序沒有差別。
- 服務要在 UI 執行緒建、在 UI 執行緒關（MediaPlayer、全域快捷鍵）；結束時背景只做停止錄音和工作，`AppHost.Post` 不再更新畫面，回到 UI 執行緒再關播放器和資料庫（錄音中按「結束」曾經卡死）。

### 11.3 外觀、字型

- 顏色（celadon、signal、說話者 8 色）從 `Views/Theme.swift` 搬到 XAML 的 ThemeDictionaries（淺色、深色）。
- 視窗的底色：淺色白、深色 #202020（Windows 11 的 SolidBackgroundFillColorBase），每個視窗的最外層用 `Ui.WindowBackground` 設、跟著深淺色換。沒設時深色模式是純黑。主視窗的側欄淺色 #F7F7F7、深色 #2B2B2B（跟內容區分得出來，同 Mac 的側欄）。
- 介面字型用系統預設（Segoe UI Variable、微軟正黑體）。會議標題的明體：Mac 用宋體，Windows 用新細明體（沒有時退回微軟正黑體粗體），W2 截圖比較後定。
- 會議內容依會議語言設定 `Language`（ja-JP 時日文字形，對應 D20）。
- 匯出（PDF、docx）的字型：中文 Microsoft JhengHei／PMingLiU、日文 Yu Gothic／Yu Mincho、英文 Segoe UI／Georgia（Mac 是 New York）。

### 11.4 介面文字（R26.4）

- 翻譯表兩份，app 都內嵌：**Mac 的 `Resources/Localizable.xcstrings` 直接拿來用**（同樣用中文當鍵，D12），再加上 Windows 才有的字串 `src/Shiyin.App/Strings/Windows.xcstrings`（同一個鍵以 Windows 的為準）。Mac 的檔案不動（R26.5）；Mac 的 `l10n.py extract` 刪掉 Swift 沒用到的條目時，Windows 用到的鍵會變成「缺英文」，`windows-check` 會抓到，補進 Windows 的表。
- `scripts/dev/l10n.py windows-check`：抽出 `L.T("…")`、`L.F("…")` 的鍵，列出兩份表都沒有英文的條目和沒查表的介面中文（`// l10n-skip` 跳過）；`windows-apply <翻譯.json>` 寫進 Windows 的表。CI 的 macOS 那一台每次都跑。
- C# 用 `L.T("中文")`、`L.F("已匯入 %lld 個檔案", n)`：鍵的寫法照 xcstrings（`%@`、`%lld`、`%1$@`、`%%`），英文的單複數照 xcstrings 的 variations（看第一個整數），才能沿用 Mac 的英文。
- 介面語言：預設跟著 Windows 的顯示語言（中文是中文，其他英文）；Windows 不能替不打包的桌面 app 個別設定語言，所以設定的「進階」裡另外可以選，重開後生效（R26.4、D57）。DEBUG 可以用 `--set interfaceLanguage=en`。
- 依會議語言的文字（匯出、預設說話者名稱）同 M§16（`MeetingLanguage.text`），在 Core。

## 12. 系統整合

### 12.1 單一執行個體、開檔

- `AppInstance.FindOrRegisterForKey`：第二次啟動把參數（要開的檔案）轉給已經開著的拾音再結束。
- 安裝時在 `HKCU\Software\Classes` 註冊 `.shiyin` 的預設程式、音訊和影片的「開啟檔案」清單（Velopack 安裝、解除安裝的 hook）。

### 12.2 通知（R11.2、R19.4）

- 用 Windows 本身的 `ToastNotificationManager`（D62），不用 Windows App SDK 的 AppNotification：AppNotification 依賴 Windows App SDK 的 Singleton 套件，自帶部署、沒打包的 app 註冊時就失敗（`Register()` 回 0x8007007E，2026-10-21 在虛擬機看到）。
- 通知用的 AppUserModelID `Weiwei.Shiyin` 登錄在 `HKCU\Software\Classes\AppUserModelId\Weiwei.Shiyin`（`DisplayName` 依 Windows 語言是「拾音」或「Shiyin」、`IconUri` 是安裝資料夾裡的 `Assets\Shiyin.png`），每次啟動都寫一次（路徑變了也跟著改）；不改程序本身的 AppUserModelID（工作列跟 Velopack 的捷徑同一組）。
- 內容：完成、失敗、偵測會議（按鈕「開始錄音」「不要再提醒這個 app」；「停止並轉錄」「繼續錄」）。點通知、按按鈕都是 `shiyin:` 網址（`HKCU\Software\Classes\shiyin`，`URL Protocol`），Windows 會用網址當參數開拾音，第二個拾音把參數轉給已經開著的那個（§12.1）後結束；拾音沒開時就直接開起來處理。
  - `shiyin:open?meeting=<id>`：打開那場會議。
  - `shiyin:prompt?action=start|mute|stop|continue&app=<id>&token=<權杖>`：權杖是每次啟動隨機產生、只放記憶體，對不上時（網頁裡的連結、上次啟動留下的舊通知）只打開主視窗，不開始錄音、不停止錄音。
- 看通知有沒有被關掉：先讀 `ToastNotifier.Setting`；它丟出例外時（剛登錄 AppUserModelID 的那次啟動會）改看 Windows 存使用者設定的登錄（`PushNotifications` 的 `ToastEnabled`、`Notifications\Settings\Weiwei.Shiyin` 的 `Enabled`）。關掉時偵測會議改用 §11.1 的小視窗（同 Mac 的退路）。測試時要從「設定 → 系統 → 通知」關，直接改登錄不會馬上生效（通知平台有自己的狀態）。
- 虛擬機驗過（2026-10-21）：轉錄完成、整理完成、整理失敗、偵測會議的通知都進通知中心；偵測會議通知的按鈕網址（從通知紀錄讀出來交給 Windows 開，跟按按鈕走同一條路）把 Zoom 設成不提醒；權杖不對的網址不會開始錄音；通知關掉時改跳小視窗（「這次不用」「不要再提醒這個 app」「開始錄音」，會議結束時「停止並轉錄」）。
- 解除安裝時（Velopack 的 hook）刪掉 AppUserModelID、`shiyin:` 的登錄，清掉通知中心裡拾音的通知。
- exe 的路徑要固定：Velopack 的 `current\` 資料夾每次更新都是同一個路徑。

### 12.3 全域快捷鍵（R11.3、R19.5）

`RegisterHotKey`：Ctrl+Alt+Shift+R 開始或停止錄音、Ctrl+Alt+Shift+B 標記重點（只在錄音中註冊）；`MOD_NOREPEAT`。不用 Win 鍵（Win+Alt+R 是 Xbox Game Bar 的錄影，Win 的組合多半被系統佔用）。被別的 app 佔走時回 `ERROR_HOTKEY_ALREADY_REGISTERED`，設定頁顯示原因（等於 Mac 的獨佔註冊）。

### 12.4 偵測會議（R19.4，D55，待 T-W8）

- 讀 `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone`：封裝的 app 用套件家族名稱當子鍵（新版 Teams 是 `MSTeams_8wekyb3d8bbwe`），桌面 app 在 `NonPackaged\` 下、路徑的 `\` 換成 `#`（例如 `C:#…#Zoom.exe`）；`LastUsedTimeStop = 0` 表示正在用。這是 Windows「麥克風使用中」指示的資料來源，涵蓋封裝的新版 Teams。
- 照 Mac 每 2 秒讀一次（或用 `RegNotifyChangeKeyValue` 監聽）。
- 備案：麥克風端點的 audio session 清單（`IAudioSessionManager2`）。瀏覽器和 WebView2 用另一個程序收音，容易漏，只在 DEBUG 交叉比對。
- app 清單：執行檔名稱或套件家族名稱（Zoom.exe、新版 Teams、Teams.exe、Webex、slack.exe、LINE.exe、Discord.exe；chrome.exe、msedge.exe、firefox.exe、brave.exe、Arc），確切名稱 T-W8 確認。
- 時間規則同 M§21.7：用麥克風 5 秒提醒開始、停 10 秒提醒停止；不要再提醒的清單存在設定。

### 12.5 API key（R27 R21）

- 認證管理員的一般認證，目標名稱 `com.weiwei.shiyin.apikey/<anthropic|openai|gemini|openaiCompatible>`，`CRED_PERSIST_LOCAL_MACHINE`（不跟著帳號漫遊到其他電腦）。
- 同一個 Windows 使用者的程式都讀得到，**不會跳出存取確認**，所以 R21.2、R21.3 處理的情況在 Windows 不存在。照樣只在用到時讀、記在記憶體；存、刪時一起更新；存不進去時說明原因。
- 不寫進檔案、資料庫、log（N6）。

### 12.6 剪貼簿、分享、存檔對話框（R7.3、R7.5）

- 複製富文字：剪貼簿同時放純文字和 HTML（Word、Outlook、Notion 都吃 HTML）；不放 RTF（D54）。
- 分享（R7.5）：`IDataTransferManagerInterop.ShowShareUIForWindow` 打開 Windows 的分享視窗。
- 開檔、存檔：WinUI 的 FileOpenPicker／FileSavePicker（用視窗代號初始化）。

### 12.7 回報問題（R19.7）

Windows 版本和 build（`RtlGetVersion`）、CPU 名稱和架構（登錄的 ProcessorNameString）、記憶體、顯示卡（DXGI 的介面卡名稱）、whisper 用的是 CPU 還是 Vulkan，其他同 M§21.9。

### 12.8 設定

`SHIYIN_HOME\settings.json`，鍵名跟 Mac 的 UserDefaults 相同（`llmProvider`、`transcriptionEngine`…）。寫入時先寫暫存檔再取代，避免寫到一半。DEBUG 可以用啟動參數覆蓋單一設定（`--set llmProvider=none`），只在這次執行有效，同 Mac 的 `-llmProvider none`。

### 12.9 log

`logs\shiyin-<日期>.log`，保留 7 天；不寫逐字稿和會議紀錄的內容、不寫 key（同 CLAUDE.md 的規則）。

## 13. 匯出與封包

- md、txt、srt、vtt、HTML：翻 `IO/Exporters.swift` 的文字產生部分，跟標準答案逐位元組相同（§4）。
- **docx**：DocumentFormat.OpenXml，把 Core 匯出的 HTML（標籤固定：h1～h3、p、ul/li、table、b、span.time）轉成 Word 原生的標題、項目符號、表格（`Services/Export/DocxWriter.cs`），字型見 §11.3；待辦表格的負責人、期限欄不拆行（同 Mac 的 `fitShortColumns`）。HTML 只是中間格式，Word 裡沒有 HTML（不用 altChunk）；用 OpenXml 的 schema 檢查零錯誤。
- **PDF**：WebView2 打開標準答案裡同一份 HTML（字型換成 §11.3 的 Windows 字型），`PrintToPdfAsync`，A4、邊界 56 pt。
- docx、PDF 不能跟 Mac 逐位元組比（Mac 是 NSAttributedString、NSTextView 排的，見 M§21.13）：比抽出來的文字、表格結構、頁數。Mac 上用 PDFKit 的小腳本抽 PDF 的文字。
- **docx 跟 Mac 比過**（2026-10-21）：同一個 .shiyin（標準答案的中、英、日會議）分別匯入 Mac 3.6 的 DEBUG 版和 Windows 的程式，匯出會議紀錄、逐字稿的 Word，抽出每一段的文字比：逐字稿三種語言、日文會議紀錄完全相同；中文、英文會議紀錄的文字和順序相同，差在兩個地方，都是 Mac 的 NSAttributedString 的寫法，Windows 不跟：
  - 待辦表格：Mac 的 Word 沒有表格（`w:tbl`），每一格變成一段；Windows 是真的表格
  - 沒有內容的主題（`<ul></ul>`）：Mac 多一行只有項目符號的空白項目；Windows 不輸出
- **m4a**：Media Foundation 的 AAC 編碼器只接受 44.1 或 48 kHz、96～192 kbps，16 kHz 的錄音要先升頻，每小時約 43 MB（Mac 約 10～30 MB）。寫進 R27 的 R9.4。
- **wav**：16 kHz 單聲道。
- **.shiyin**：`System.IO.Compression`，zip 根目錄放 manifest.json、meeting.json、transcript.json、audio.*（同 `IO/Package.swift`）。匯入時檢查路徑（不能有 `..`、絕對路徑、符號連結，R18.10）。

## 14. 安裝、簽章、更新、發布

### 14.1 安裝檔（R27 R10.1）

- Velopack（`vpk pack`，`scripts/windows/pack.ps1`）：`Shiyin-<版本>-win-x64-Setup.exe`、`Shiyin-<版本>-win-arm64-Setup.exe`，各自一個更新頻道（`win-x64`、`win-arm64`）。裝在 `%LOCALAPPDATA%\ShiyinApp`、不用系統管理員，自帶 .NET 和 Windows App SDK。發行者是 `juinwei7`（同 LICENSE）。
- 名稱依介面語言（R27 R12.3，D63）：中文是「拾音」，其他是「Shiyin」（設定裡選的介面語言，沒選時跟著 Windows）。
  - 開始功能表、桌面的捷徑由 app 的安裝 hook 自己建（`Shortcuts.cs`，打包時 `--shortcuts None`），帶 Velopack 的 AppUserModelID `velopack.ShiyinApp`；更新 hook 和每次啟動時，換了語言就把現有的捷徑改名（使用者刪掉的不建回來）；解除安裝時兩種名稱都刪。
  - 「設定 → 應用程式」裡的名稱：Velopack 在安裝 hook 之後才寫登錄，所以在每次啟動時改（裝好後第一次打開就對）。檔案類型（.shiyin）的名稱也在啟動時跟著換。
  - 安裝程式自己的視窗標題固定是「拾音」（Velopack 不能依語言換，只顯示幾秒）。
- 打包要帶 `Shiyin.pri`（app 的資源表，少了它 XAML 一載入就當掉）：不打包的 WinUI app 要開 `EnableMsixTooling`，publish 才會帶它（只在 Windows 上開，Mac 的編譯檢查不產生 .pri）。`pack.ps1` 檢查 `Shiyin.pri`、`Third-Party Licenses.txt`、只有一份 `onnxruntime.dll`。
- `scripts/windows/*.ps1` 存成有 BOM 的 UTF-8：Windows 內建的 PowerShell 5.1 把沒有 BOM 的檔案當成系統的編碼（中文 Windows 是 Big5），中文字串會壞掉、整個腳本解析失敗。
- 解除安裝不刪 `%LOCALAPPDATA%\Shiyin`（錄音、資料庫、模型、設定），拿掉檔案關聯、`shiyin:` 網址、通知的登錄。Velopack 會在移除後幾秒才刪掉安裝資料夾，移除後馬上重裝的話，剛裝好的檔案可能被刪掉（測試時移除後等 20 秒）。
- 安裝說明（中、英）放在官網和 Release 說明：
  - SmartScreen「Windows 已保護您的電腦」→「其他資訊」→「仍要執行」
  - **Smart App Control** 開著時沒有簽章的 app 會被直接擋下、沒有「仍要執行」可以按（多半是新安裝的 Windows 11 才開著）。說明要寫清楚：可以關掉 Smart App Control（2026-04 起關掉後可以再打開，逐步推出），或等有正式簽章的版本
- 正式簽章：申請 SignPath Foundation（開源專案免費，repo 是 MIT）。申請要用使用者的名義送出，等使用者決定（tasks W8.2）。申請下來前發布的版本沒有 Authenticode 簽章。付費憑證、Azure Artifact Signing（個人帳號只限美國、加拿大）不做。

### 14.2 更新（R27 R10.6、R10.7，D50）

- Velopack 只檢查自己 feed 裡的 SHA-256，不驗是誰發的。所以另外加一把**更新金鑰**（ECDSA P-256）：
  - `scripts/windows/update-key.cs keygen` 建一次，私鑰放 GitHub secrets（要使用者同意，同 Mac 的 `export-signing-cert.sh`），公鑰貼進 `AppHost.cs` 的 `UpdateKey.Public`
  - CI 打包後簽「版本、架構、每個套件檔的 SHA-256」，簽章檔跟 Release 一起上傳
  - app 內建公鑰；下載新版後、套用前驗證簽章、版本比目前新、套件的 SHA-256 相符，不符就刪掉下載的檔案、拒絕更新
  - 等於 Mac 的「新版要跟目前的 app 同一個簽章身分」（M§12）
- 有 Authenticode 簽章（SignPath）之後，加檢查「新版的簽署者跟目前的相同」。
- 檢查時機、錄音或工作中不能更新、資料庫自動遷移同 M§12。Windows 的麥克風權限跟著「桌面 app」整體，不綁 app 的版本，更新後不用重新允許（R10.7，要在有麥克風的電腦上確認）。沒有更新金鑰的 DEBUG 版不檢查。
- 測試更新：`pack.ps1 -UpdateTest` 建的版本（編譯時加 `UPDATE_TEST`，發布的版本不會有）改從環境變數 `SHIYIN_UPDATE_SOURCE`（Velopack 的 SimpleWebSource，更新說明也從這裡讀）、`SHIYIN_UPDATE_PUBLIC_KEY` 讀更新來源和公鑰。用測試金鑰打包兩個版本，Mac 上開本機伺服器、`ssh -R` 讓虛擬機連進來。2026-10-21 在虛擬機驗過：另一把金鑰簽的更新說明被拒絕（「新版的簽章跟目前的拾音不符」）、下載的檔案刪掉；正確的簽章從 3.6.0 更新到 3.6.1、自己重開，資料、設定、捷徑的名稱都在。

### 14.3 GitHub Releases（D51）

- Windows 用自己的 tag：`win-vX.Y.Z`（Mac 的工作流程只認 `v*`，不會被觸發）。
- 版號跟 Mac 的功能版本對齊：第一版 3.6.0 表示功能跟 Mac 3.6 相同（D52）；之後只修 Windows 的問題時加第三碼。
- **Windows 的 release 一律用 `--latest=false`**：Mac 的更新網址是 `releases/latest/download/shiyin-update.json`，Windows 的 release 變成 latest 時，Mac 的 app 內更新會找不到檔案（R27 R13.2）。之後 Mac 發新版時照樣變成 latest。
- Windows 的更新來源（Velopack 的 GithubSource 讀 GitHub 的 release 清單）要能跳過沒有 Windows 檔案的 Mac release：2026-10-21 用 `-UpdateTest` 的版本讀真的 repo（只有 Mac 的 release），顯示「已是最新版本」、沒有錯誤。Mac 和 Windows 的 release 混在一起時有沒有挑到 Windows 的，第一個 Windows release 時再確認；不行的話改用固定網址的 feed（SimpleWebSource）。
- **官網要先改再發第一個 Windows release**（§14.4）：現在的 `releases.mjs` 遇到不是 `vX.Y.Z` 的 release 會建置失敗。
- 新的工作流程 `.github/workflows/release-windows.yml`：推 `win-v*` 時在 `windows-latest`（x64）、`windows-11-arm`（ARM64）建置，跑測試、whisper 從固定 commit 建並比對 SHA、`vpk pack`、簽更新金鑰、建立 Release（不設成 latest）。手動執行只產出 artifact（同 Mac）。
- Release 說明的格式同 CLAUDE.md 的規定，標題改成「拾音 Windows 3.6.0：…」「Shiyin for Windows 3.6.0: …」。

### 14.4 官網

`site/src/releases.mjs` 現在只認 `vX.Y.Z` 的 tag、最新的 release 一定要有 DMG（不然建置失敗）。改成依 tag 分兩組：`v*` 是 Mac（規則不變）、`win-v*` 是 Windows（要有兩個 Setup.exe）；下載區、`/download/windows`、更新紀錄、下載次數分開算；常見問題「只有 Mac 版」的文字改掉（`site/src/content.mjs` 三種語言）。改之前先改 `docs/website.md` §7，用 `node site/build.mjs --releases <檔案>` 拿兩種 release 混在一起的 JSON 試建置。

## 15. 驗證環境與 DEBUG 控制通道

### 15.1 四個驗證的地方

| 地方 | 用來驗證 |
|---|---|
| Mac：`dotnet test`、`Shiyin.Eval` | 標準答案、資料庫、整理工具（假伺服器）、轉錄和說話者辨識的評估（用 Mac 上的評估資料和使用者的錄音） |
| 虛擬機（Windows 11 ARM64） | 畫面、錄音、系統整合、安裝、x64 版（模擬執行，只看功能） |
| CI：`windows-latest`（x64）、`windows-11-arm`（ARM64），公開 repo 免費 | 每次推送跑測試；真的 x64 CPU 的轉錄速度；無聲安裝、更新；Vulkan 用軟體實作（lavapipe）確認結果正確 |
| 使用者的 Windows 電腦（`[~]`） | 真的顯示卡、筆電麥克風、藍牙、Teams／Zoom 會議、SmartScreen 和 Smart App Control 的實際體驗 |

評估資料在 Mac 的 `~/Library/Caches/ShiyinSpike/`：`eval32/`（AMI、日文、合成會議）、`aishell4/`、`Datasets/ami_public_1.6.2`（AMI 的標註）。

### 15.2 虛擬機

工具在 `scripts/windows/vm/`；金鑰、IP、密碼、Windows 的 ISO 放在 `$SHIYIN_VM`（預設 `~/Library/Caches/ShiyinBuild-win/vm`），不進 repo。

- **UTM**（免費，2026-10-21 使用者決定；原本規劃的 Parallels 要付費）。Windows 11 ARM64 專業版 zh-TW，4 核、4 GB（6 GB 時 Mac 的 swap 到 16 GB，調低），共用網路（192.168.64.x）。虛擬機的磁碟檔最多約 25 GB；`diskguard.sh` 在背景跑，Mac 剩不到 10 GB 就停掉虛擬機。
- 建置環境：
  - Windows 的 ISO：微軟下載頁擋自動下載，用 UUP dump 的 convert 腳本在 Mac 上做（26100.1、不含更新）。
  - 全自動安裝：`make-payload.sh` 做第二張光碟（`payload/autounattend.xml`：略過 TPM 檢查、本機帳號 `shiyin`、自動登入；`payload/shiyin-setup.ps1`：virtio 驅動程式、OpenSSH Server 和金鑰、不睡眠不鎖定、關掉自動更新、長路徑、原始碼資料夾不即時掃毒）。UTM 用 AppleScript 建虛擬機（`make new virtual machine`，再逐項 `update configuration`）。
  - 裝在虛擬機裡：.NET 10 SDK（`~\.dotnet`）、`dotnet-dump`、WebView2 執行階段（UUP 做的映像檔沒有）、VC++ 執行階段 ARM64（官方 whisper.cpp 的 DLL 要）。沒有 Visual Studio Build Tools：whisper 用官方 b5454 的 Windows ARM64 版（`GGML_BACKEND_DL`，§8）放在 `C:\xfer\whisper-arm64`，`shiyin_whisper.dll` 在 Mac 上用 zig 交叉編譯（`aarch64-windows-gnu`）。正式發布的 DLL 由 CI 用 `build-whisper.ps1` 建（靜態連結執行階段）。
  - 當掉時：WER LocalDumps 把 `Shiyin.exe` 的 dump 寫到 `C:\dumps`，用 `dotnet-dump analyze` 看（`clrstack -all`、`pe`）。DEBUG 版另外把每個例外記在 `%TEMP%\shiyin-exceptions.log`（含呼叫堆疊）。
- 從 Mac 操作：`sync.sh` 把工作目錄（git 追蹤和還沒加的檔案）同步到 `C:\src`（不用共用資料夾：桌面有 iCloud）；`ssh.sh`、`run.sh` 在 SSH 的工作階段跑 PowerShell（建置、測試）；`ps/rebuild.ps1` 重建 DEBUG 版；`ctl.sh` 送控制通道的指令。Mac 上的本機伺服器（假的整理工具、更新檔）用 `ssh -R <埠>:127.0.0.1:<埠>` 讓虛擬機從 127.0.0.1 連過來。
- 虛擬機**沒有音效裝置**：WASAPI 的錄音、換裝置、播放的聲音都測不到，錄音用 `record-sim`；真的麥克風、電腦聲音要使用者的電腦（W0.3）。
- 磁碟：虛擬機裡刪檔案不會讓 Mac 上的磁碟檔變小，刪完要 `Optimize-Volume -DriveLetter C -ReTrim`。
- 使用者在用拾音錄會議時（Mac 的 `audio/recording/` 有檔案）不開虛擬機、不跑重的工作。UTM 的視窗會出現在使用者的螢幕上（不會搶前景）。

### 15.3 控制通道

- DEBUG 版開 named pipe `\\.\pipe\shiyin-debug`（`Debug/ControlPipe.cs`）；`shiyinctl.exe <指令> <參數…>`（`Shiyin.Ctl`）把結果的 JSON 印到標準輸出（Mac 是寫成檔案；從 SSH 用時直接讀輸出比較方便）。named pipe 不分工作階段，從 SSH 也連得到桌面上的拾音。
- 指令：`state`、`select`、`tab`、`open`、`closewindow`、`quit`、`record-sim`、`recordstop`、`recordpause`、`recordstall`、`recordinterrupt`、`recordlang`、`import`、`export`、`purge`、`rediarize`、`language`、`llmprovider`、`llmkey`、`llmurl`、`modelurl`、`model`、`hotkey`、`meetingapp`、`metrics`。名稱和參數跟 Mac 的 `Debug/DebugControl.swift` 相同（例如 `export <項目> <格式> <路徑> [times] [speakers] [會議 id]`、`import [lang=ja] <檔案…>`），Mac 的測試方法可以直接照用。
- 評估（`transcribe-eval`、`voiceprint-eval`、`detect-eval`、`diarize-eval`、`fast-eval`、`live-eval`）不在 app 裡，用 `Shiyin.Eval` 命令列在 Mac 或 Windows 上跑，輸出格式跟 Mac 相同，例如用 `scripts/dev/asr-score.swift` 算錯字率（R28.4）。

### 15.4 真的畫面：排程工作和 UI Automation

從 SSH 啟動的程式不會出現在登入的桌面，UI Automation 也碰不到桌面。所以用排程工作在桌面的工作階段執行 PowerShell（`ps/in-session.ps1`：註冊「互動式登入」的工作、等它跑完、讀回輸出）。原本規劃的 `Shiyin.Agent` 不用做了。

- `insession.sh`：把標準輸入的 PowerShell 放到桌面跑，例如 `ps/start-app.ps1` 在桌面開 DEBUG 版（資料在 `C:\ShiyinTest`）。
- `uia.sh <動作> <視窗> <名稱>`（`ps/uia.ps1`，Windows 內建的 UIAutomationClient）：`windows`、`dump`、`click`（Invoke／Toggle／選取／展開）、`open`（會打開選單的按鈕，交給另一個程序按，不然會等到選單關掉）、`menu`、`choose`（下拉選單）、`set`（輸入框）、`close`。同名的元件能按的排前面，`名稱#2` 指定第幾個。
- `shot.sh <檔案>`：在 Mac 上用 `screencapture -l` 截 UTM 的視窗（整個虛擬機的畫面）。
- UI Automation 做不到時（開著選單、存檔對話框時 UI Automation 會卡住）：用 UTM 的 AppleScript `input scan code` 送按鍵，只會送進虛擬機，不會到 Mac 前景的 app。
- 系統的視窗（SmartScreen、系統匣）不屬於拾音：直接從桌面的根元件找（例如系統匣在 `Shell_TrayWnd`，隱藏的圖示先按「顯示隱藏的圖示」）。

## 16. 決策紀錄（接著 design.md 的 D41）

| # | 決策 | 不採用的方案與原因 |
|---|---|---|
| D42 | Windows 版用 C# .NET＋WinUI 3 另外寫，Mac 版不動 | Swift on Windows：可以搬 Model、Store、Notes 的程式，但沒有 SwiftUI，介面要用 swift-winrt 接 WinUI，工具鏈和 GRDB 在 Windows 都不成熟。Tauri、Electron 兩邊共用：等於 Mac 3.6 整個重寫，原生的體驗和已經驗收的東西都要重來。WPF：成熟但外觀是舊的 Windows；WinUI 3 是現在的原生元件。取捨：兩份程式要各自維護，靠 §4 的標準答案保持一致（使用者 2026-10-20 決定） |
| D43 | 兩邊一致靠 Mac 產生的標準答案比對，不共用提示詞檔案 | 把提示詞搬到共用的文字檔：要改 Mac 3.6 讀提示詞的方式，而且組合提示詞的邏輯兩邊還是各寫一份，終究要比對組出來的結果。從 Swift 原始碼自動轉成 C#：插值和條件太多，轉不穩 |
| D44 | 即時逐字稿用 sherpa-onnx 的 SenseVoice＋VAD，一直重新辨識正在講的那句當暫定文字（待 T-W5） | Windows 內建的語音辨識：舊的 Windows.Media.SpeechRecognition 連續聽寫要連網或品質差；新的 Windows AI 語音辨識是實驗性、一定要打包成 MSIX、只有英文。串流的 zipformer、paraformer：輸出簡體、沒有日文。whisper 小模型邊錄邊辨識：延遲大、CPU 吃重，還會跟正式轉錄搶資源（同 D1）。雲端語音服務：違反第 4 節（轉錄不連網） |
| D45 | 「macOS 內建」換成「快速辨識」（SenseVoice） | 拿掉這個選項：第一場會議要等 1.7 GB 下載（D35 的理由）；即時逐字稿本來就要這個模型，不用多下載 |
| D46 | whisper.cpp 自己從 Mac 同一版建，P/Invoke | Whisper.net：包的 whisper.cpp 比較舊、同一段錄音結果會跟 Mac 不一樣（R28.4）；語言偵測只給最高的那一個，拿不到中英日三個機率（R16 的門檻要用）；Vulkan 版只有 x64。執行 whisper-cli 子程序：進度、取消、token 機率都要從文字輸出解析 |
| D47 | 說話者辨識用 sherpa-onnx（分群改成自己做，見 D58） | 把 FluidAudio 的流程（pyannote community-1＋VBx）整個搬到 ONNX：community-1 沒有官方 ONNX 版，要自己轉模型。pyannote（Python）：違反 D2。取捨：模型和 Mac 不同，聲紋不能共用、門檻要重新調（R28.6）；「至少 N 人」、聲紋、分不準的判斷要自己補（§9） |
| D48 | 資料放 `%LOCALAPPDATA%\Shiyin`，設定放在裡面的 settings.json | 登錄（Registry）：換 `SHIYIN_HOME` 時設定還是同一份，測試會改到正式的設定（Mac 的 UserDefaults 就踩過，見 CLAUDE.md）。`%APPDATA%`（漫遊）：錄音和模型很大，不該跟著帳號漫遊 |
| D49 | API key 存在認證管理員 | DPAPI 加密後存檔案：N6 規定 key 不寫進檔案。WinRT 的 PasswordVault：底層也是認證管理員，不打包的 app 用起來比較麻煩 |
| D50 | 用 Velopack 安裝和更新，加上自己的更新金鑰（ECDSA）驗證發布者 | MSIX：自簽的 MSIX 要先用系統管理員把憑證裝進「受信任的人」，一般使用者裝不起來。Inno Setup 加自己寫更新器：要自己處理替換、重開、解除安裝，Velopack 都有，還有差異更新。只靠自簽的 Authenticode 比對簽署者：做得到（`CERT_E_UNTRUSTEDROOT` 表示簽章完整只是不受信任），但對 SmartScreen、Smart App Control 沒有幫助，還要多保管一張憑證；更新金鑰的驗證只要幾行標準的密碼學程式。Microsoft Store：要開發者帳號和審核，之後再說 |
| D51 | Windows 的 release 用 `win-v*` tag，不設成 latest | 同一個 release 放兩邊的檔案、版號一樣：兩邊一定要同時發布，但 Windows 修自己的問題、Mac 加新功能的時間不會一樣。Windows 的 release 標成 prerelease：官網、Velopack 預設都會略過它，使用者看不到也更新不到。另外開一個 repo：星星、issue、官網的資料來源都要分開。取捨：官網的 `releases.mjs` 要改成認兩種 tag，而且要在第一個 Windows release 之前改好 |
| D52 | Windows 的版號對齊 Mac 的功能版本，第一版 3.6.0 | 從 1.0.0 開始：使用者看不出 Windows 版有哪些 Mac 的功能 |
| D53 | 驗證用排程工作在桌面跑 PowerShell 的 UI Automation，加上 named pipe 的控制通道（§15.4；原本規劃的桌面代理 app 用不到） | 在使用者的電腦上送鍵盤事件：CLAUDE.md 禁止。WinAppDriver：微軟已經不維護。從 SSH 直接開 app：不會出現在桌面，截不到圖 |
| D54 | 複製富文字只放純文字和 HTML | 再放 RTF：要自己從 HTML 產生 RTF；Windows 上常用的貼上目標都吃 HTML |
| D55 | 偵測會議讀隱私設定的麥克風使用紀錄（ConsentStore）（待 T-W8） | audio session 清單（IAudioSessionManager2）：要一個一個裝置列，瀏覽器和 WebView2 用另一個程序收音，容易漏。取捨：ConsentStore 沒有官方文件，Windows 改版時可能變；它是系統「麥克風使用中」指示的資料來源，改了也比較容易發現 |
| D56 | whisper 外面包一層小的 C shim | 在 C# 宣告整個 `whisper_full_params`：傳值、1 byte 的 bool、巢狀 struct 和 callback，對錯了不會報錯、只會結果怪；每次升級 whisper.cpp 都要重新對一次 struct |
| D57 | 介面語言在設定裡可以另外選 | 只跟著 Windows：Windows 沒有替不打包的桌面 app 個別設定語言的地方（Mac 有），英文 Windows 上想用中文介面的人就沒辦法 |
| D58 | sherpa-onnx 只切段落（固定 30 群），每 3 秒的小段用 3D-Speaker 算聲紋，減掉整場平均後自己做凝聚式分群（§9） | sherpa-onnx 原本的門檻分群：中文會議（AISHELL-4）把 6 人併成 1～3 人，門檻怎麼調都不行（整場的聲紋太像）。WeSpeaker（英文 VoxCeleb）的聲紋：減掉平均後 AMI 還好，AISHELL-4 同一人、不同人的相似度重疊太多（第 10 百分位 0.03 對第 90 百分位 0.33；3D-Speaker 是 0.17 對 0.09）。VBx：要 PLDA 模型、參數多，這個做法在 AMI、AISHELL-4 已經跟 Mac 一樣好。取捨：聲紋模型比 WeSpeaker 大（40 MB 對 26 MB）；聲紋比對、分不準的門檻跟 Mac 不同（0.6、0.37） |
| D59 | WinUI 的畫面全部用 C# 建，不用 XAML 檔 | XAML：排版看得比較清楚，但 XAML 編譯器（XamlCompiler.exe）只能在 Windows 跑，Mac 上連編譯檢查都不行，每改一次畫面都要進虛擬機。取捨：程式比較長、沒有設計工具的預覽；用 `Ui.cs` 的小工具把常用的寫法縮短，畫面一律在虛擬機截圖確認（W2） |
| D62 | 通知用 Windows 本身的 ToastNotificationManager＋登錄的 AppUserModelID，按鈕用 `shiyin:` 網址加權杖（§12.2） | Windows App SDK 的 AppNotification：自帶部署、沒打包的 app 不能用（依賴 Singleton 套件）。不自帶 Windows App SDK、改用框架套件：要另外裝執行階段，安裝檔要系統管理員或多一步。COM 啟動器（INotificationActivationCallback）：按按鈕時不用開第二個程序，但要登錄 CLSID、實作 COM 伺服器，程式多很多。取捨：每按一次按鈕會短暫多開一個程序（轉送完就結束）；網址可能被網頁觸發，所以會改變狀態的動作要權杖 |
| D61 | 快速辨識、即時逐字稿用原版 SenseVoice-Small（2024-07-17），VAD 門檻 0.35，送進 VAD 前自動增益（§7） | 2025-09-09 版（原本選的，比較新）是用粵語語料微調過的：日文的假名幾乎都不見（國會聽證、FLEURS 錯字率約 80%，原版 16～28%），英文 AMI WER 60%（原版 40%），只有中文合成會議稍好（8.8% 對 9.2%）。VAD 用預設門檻 0.5、不放大：遠距收音的 AISHELL-4 只抓到兩成的時間，錯字率 84%；門檻 0.35 加自動增益是 26%，其他錄音也沒有變差（門檻 0.35 單獨讓 FLEURS 從 19.9% 降到 15.8%）。固定放大 15～20 dB 對 AISHELL-4 也有用，但大聲的錄音（國會聽證的第 90 百分位已經 −20 dBFS）再放大會超過 0 dBFS 被削掉。取捨：自動增益在很安靜的房間會把背景放大（最多 24 dB），靠 VAD 擋；音量變化很大的會議，增益要幾秒才跟上 |
| D63 | 開始功能表、桌面的捷徑由 app 的安裝 hook 自己建，名稱依介面語言（§14.1） | 用 Velopack 建的捷徑：名稱是打包時固定的 `--packTitle`，英文 Windows 也是「拾音」。名稱寫成「拾音 Shiyin」：兩種語言都找得到，但「應用程式」清單、捷徑都多一串字。`desktop.ini` 的 LocalizedFileNames 依語言顯示：要在 exe 放多語言的字串資源（MUI），還要改開始功能表資料夾裡共用的 `desktop.ini`。取捨：建立、改名、刪除捷徑要自己處理（`Shortcuts.cs`）；安裝程式自己的視窗標題還是固定的「拾音」 |
| D64 | NVIDIA 顯示卡加速由 app 另外下載 whisper.cpp 官方的 CUDA 版（R27 R4.6、§8） | 直接放進安裝檔：x64 的安裝檔從約 100 MB 變成約 750 MB（ggml-cuda 570 MB、cuBLASLt 474 MB），沒有 NVIDIA 顯示卡的人也要下載。官方的 CUDA 11.8 版：壓縮檔沒有附 cuBLAS，要另外從 NVIDIA 下載。自己建 Vulkan 版（設計原本的做法，所有顯示卡都能用）：官方沒有 Windows 的 Vulkan 檔案，要 CI 裝 Vulkan SDK 建，還沒做；做好之後 AMD、Intel 的顯示卡用 Vulkan，NVIDIA 再比較 CUDA 和 Vulkan 的速度決定要不要留這個下載。取捨：下載 685 MB、佔 1.1 GB；只支援 NVIDIA；CUDA 12.4 要 527 版以後的驅動（太舊時用 CPU）；要第一次用 whisper 之前下載好才用得上，不然要重新開啟 |
| D60 | 不碰畫面的 app 邏輯放在 `Shiyin.Services`（net10.0），平台的部分是介面 | 寫在 App 的 ViewModel 裡：Mac 上測不了，錄音、工作佇列、匯入、更新這些最容易出錯的流程都要進虛擬機才能測。取捨：多一層介面（`IAudioDecoder`、`IAudioSource`、`IExportPlatform`、`IUpdatePlatform`…）；換來在 Mac 上用模擬的音源、假的引擎和本機伺服器跑完整的流程 |

## 17. 需要先做的實驗

結果寫在對應的 commit 訊息，選擇定了就改上面的章節和決策紀錄。

| # | 內容 | 通過標準 |
|---|---|---|
| T-W1 | 虛擬機和自動化：從 Mac 用 SSH 建置一個 WinUI 3 的小 app；排程工作在登入的桌面啟動它、截圖、用 UI Automation 按按鈕；named pipe 讀狀態 | 全部不碰虛擬機的鍵盤滑鼠就能做到；截圖看得到 WinUI 的內容（不是黑的）；輸出資料夾沒有兩份 `onnxruntime.dll` |
| T-W2 | 錄音：命令列原型同時錄麥克風和電腦聲音 5 分鐘（Edge 播 YouTube，中間停 30 秒；原型自己播一段正弦波） | 沒有斷音、停播的 30 秒補成靜音、兩個聲道時間差 100 ms 以內、正弦波沒錄進去；換預設裝置後自動接回；`powercfg /requests` 看得到 |
| T-W3 | whisper：建 x64（CPU 各版本＋Vulkan）、ARM64（CPU 各版本）、Mac 的 dylib，跑 Breeze | 同一段錄音跟 Mac 的 `transcribe-eval` 錯字率差 ≤ 0.5 個百分點（R28.4 的上限是 1）；語言偵測跟 Mac 判斷相同；取消 1 秒內停；記憶體峰值 ≤ 3 GB；量出虛擬機、CI x64、CI ARM64 的倍速，決定只有 CPU 時的參數和 N2、N4 的數字 |
| T-W4 | 說話者辨識：`Shiyin.Eval` 在 Mac 上跑 AMI 9 場、AISHELL-4、使用者的麻將錄音，A、B 兩個方案 | 說話者標錯的時間 ≤ 15%（同 T-A3）；AMI 自動判斷出 4 人、AISHELL-4 6 人、麻將給「至少 4 人」時分出 ≥ 4 位；選出門檻（聲紋誤認 ≤ 2%）；1 小時的錄音 3 分鐘內處理完（CI x64） |
| T-W5 | 即時逐字稿：`record-sim` 跑中、英、日的評估錄音 | 暫定文字 1 秒內、定稿 5 秒內；錯字率跟 Mac 的 SpeechAnalyzer 比；轉出來是台灣繁體；虛擬機的 CPU 用量 |
| T-W6 | claude、codex CLI：原生安裝和 npm 安裝各試一次 | 找得到真正的執行檔（不經過 cmd.exe）；10,000 字以上的中文 `--system-prompt` 和 JSON schema 傳進去不會被截斷；能判斷登入狀態、開終端機登入（登入要使用者同意） |
| T-W7 | 安裝和更新：本機伺服器模擬 GitHub Releases（Mac 和 Windows 的 release 混在一起） | 從 Edge 下載的 Setup.exe（有網路來源標記）被 SmartScreen 擋下時照說明可以裝、不用系統管理員；Smart App Control 開著時的畫面記下來寫進安裝說明；正常、SHA 不符、更新金鑰不符、同版本四種更新檔的結果都對；更新後麥克風權限還在；解除安裝不刪資料；更新來源跳過 Mac 的 release |
| T-W8 | 偵測會議：新版 Teams、Zoom、Chrome 打開麥克風 | 5 秒內偵測到是哪個 app；停用 10 秒內偵測到；ConsentStore 跟 audio session 清單比對 |
| T-W9 | 媒體：解碼 m4a、mp3、wav、aac、flac、mp4、mov、wma、aiff、caf；AAC 編碼的檔案大小；變速播放的音高；WebView2 印中文、日文 PDF；docx 用 Word 打開 | 列出解得開的格式，寫進 R27 的 R3.1；解不開的有清楚的錯誤；1.5 倍速時聲音不變調（不行就換成 WASAPI 加自己的變速） |
| T-W10 | 長會議畫面：2,086 段的會議捲動、跟著播放 | `metrics` 的 UI 執行緒最長卡住時間跟 Mac 的 R20.3 同一個標準 |
