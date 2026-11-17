# 拾音 Shiyin Windows 版：設計

> 狀態：4.0.0 起發布。對應 `requirements.md` 第 22 節（R26～R28）。
> Mac 版的設計在 `design.md`，本文件以「M§n」指那邊的章節。行為跟 Mac 相同的部分只寫「同 M§n」，這裡只寫 Windows 不同的地方。
> 套件版本在 `windows/Directory.Packages.props` 釘死。

## 1. 架構

```
Shiyin.exe（WinUI 3，單一程序，不需要 Python、ffmpeg、系統管理員權限，也不用另外裝 .NET）
 ├─ Capture        WASAPI 擷取（麥克風）＋ WASAPI loopback（電腦聲音，預設輸出裝置，D129）
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
| 電腦聲音（R1.2、R54） | 預設輸出裝置的 WASAPI loopback（endpoint loopback），換預設裝置時重開 | 原本用 process loopback 排除拾音自己（對應 Mac 的「全域 tap 排除自己」），新版 Teams 的聲音收到的全是 0（D129） |
| 即時逐字稿（R2） | sherpa-onnx 1.13（Apache-2.0）：Silero VAD ＋ SenseVoice-Small int8；中文用 OpenccNetLib（MIT，s2twp）轉台灣繁體 | Windows 沒有可用的內建離線中文辨識（D44）；T-W5 的結果見 §7、D61 |
| 快速辨識（R4.1、R20.8） | 同一個 SenseVoice 模型讀整個檔案 | 取代 macOS 內建（D45） |
| 正式轉錄（R4） | whisper.cpp b5454（v1.9.5、commit `d1be6fd`，跟 `Package.swift` 同一版）自己建：x64 CPU 各種指令集，AMD 的電腦另外下載 Vulkan 版（D127）；NVIDIA 另外下載官方的 CUDA 版（D64）；ARM64 一個 CPU 版本（clang，§8）。加一層小的 C shim，C# 用 P/Invoke | 結果跟 Mac 一致（R28.4，D46、D56），T-W3 的結果見 §8 |
| 語言偵測（R16） | 同上的 whisper ＋ Whisper base（同 M§18.8） | 同一個模型和門檻；要中英日三個機率，Whisper.net 拿不到 |
| 說話者辨識、聲紋（R5、R19.3） | sherpa-onnx 的說話者分段（pyannote segmentation-3.0，6 MB）切段落＋聲紋嵌入（3D-Speaker ERes2Net base，512 維，40 MB），自己分群 | 有 .NET 套件、跑在 CPU（D47、D58） |
| 解碼、壓縮 m4a | Media Foundation（NAudio 的 `MediaFoundationReader`、`MediaFoundationEncoder`）；AIFF 用 NAudio 的 `AiffFileReader` | 系統內建，不用 ffmpeg（R4.5）；解得開的格式見 §8 |
| 播放（R8.2） | `Windows.Media.Playback.MediaPlayer` 的 `PlaybackRate` | 變速時有沒有保持音高，要在有音效裝置的電腦確認（tasks 的「播放」）；會變調的話換成 WASAPI 加自己的變速 |
| 資料庫 | Microsoft.Data.Sqlite 10 ＋ SQLitePCLRaw 2.1（自帶 SQLite，含 FTS5、有 win-arm64），Dapper | trigram 要 SQLite 3.34 以上，不能用系統的 |
| API key | 認證管理員（CredWriteW／CredReadW，CsWin32 產生呼叫） | 對應鑰匙圈（D49） |
| 設定 | `SHIYIN_HOME\settings.json` | 對應 UserDefaults（D48） |
| 通知 | Windows 本身的 ToastNotificationManager（§12.2） | 有按鈕、點了帶參數打開拾音（D62） |
| 系統匣 | WinUIEx 2.9（MIT，TrayIcon；檔案總管重開後自動恢復）＋自己做的小視窗 | H.NotifyIcon 的新版還在 beta |
| docx | DocumentFormat.OpenXml 3.5（MIT），直接從會議紀錄的資料產生 | Windows 沒有 NSAttributedString 的 docx 輸出 |
| PDF | WebView2 把跟 Mac 同一份匯出的 HTML 印成 PDF | Windows 11 內建 WebView2 執行環境 |
| 安裝、更新 | Velopack 1.2（MIT；1.2.158 被標為不要用）＋ 自己的更新金鑰（ECDSA P-256） | 不用系統管理員、有差異更新（D50、§14） |
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
├── Shiyin.Windows.slnx
├── global.json                 固定 .NET SDK 10.0.x
├── Directory.Packages.props    所有套件的版本
├── Directory.Build.props       ArtifactsPath：建置輸出放到 repo 外（Mac 是 ~/Library/Caches/ShiyinBuild-win）
├── src/Shiyin.Core/  Shiyin.Data/  Shiyin.LLM/  Shiyin.Engines/  Shiyin.Eval/
├── src/Shiyin.Platform.Windows/  Shiyin.App/  Shiyin.Ctl/  Shiyin.Services/
├── tests/Shiyin.Tests/         xUnit：標準答案、資料庫、整理工具、服務（Mac、CI 都跑）；畫面用虛擬機的 UI Automation 驗（§15.4）
└── native/whisper/             CMake 腳本（CMakeLists.txt）、shim（shiyin_whisper.c、.h）
shared/golden/                  Mac 產生的標準答案（§4）
scripts/windows/                build-whisper.ps1、pack.ps1、update-key.cs、make-third-party-licenses.py；vm/ 驗證用虛擬機的工具（§15.2）
```

建置輸出不能放在專案裡（桌面有 iCloud 同步）。

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

Mac 的 DEBUG 控制通道加 `shiyinctl dump-golden <fixtures.json> <輸出資料夾>`（`Debug/GoldenDump.swift`，只在 DEBUG 版，R26.5）。它用 Mac 的程式算出結果寫進 `shared/golden/expected/`；資料庫在暫存資料夾另開一個，不碰使用者的資料。跑兩次結果相同。輸出：

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
| `documents/` | 參考文件（M§39.7）：長度分配和組好的區塊、整理文字、檔名、封包裡的路徑、pptx 的 Target、`shared/golden/documents/` 測試檔讀出來的文字 |
| `llm/mac-requests.jsonl` | Mac 的 DEBUG 版接假伺服器（`scripts/dev/fake-llm-server.py`）實際送出的四種 API 請求；Windows 送同樣的提示詞，body 要解析後相同 |

### 4.2 Windows 比對

`tests/` 讀 `fixtures.json`，用 C# 產生同樣的東西，跟 `shared/golden/` 比對：

- **逐位元組**：提示詞、schema 的文字、範本、md、txt、srt、vtt、HTML、`schema.sql`。匯出一律 UTF-8 無 BOM、換行 `\n`，跟 Mac 相同。
  - `shared/golden/**` 在 `.gitattributes` 設成 `-text`：Windows 上的 git（`core.autocrlf`）取出時會把換行改成 CRLF，逐位元組比對全部失敗。
- **比解析後的內容**：JSON 檔（Mac 的 JSONEncoder 輸出 `"key" : value`、鍵有排序、不跳脫 `/`，System.Text.Json 不同）和送給整理工具的請求 body（M§19.5 本來就不比位元組）。
- **容許誤差**：低信心的壓縮比（zlib）兩邊的壓縮器輸出大小不同，允許 ±0.05；測試資料避開門檻 2.4 附近。說話者分得準不準的凝聚度是浮點數的加總，順序不同，比到小數 6 位。
- **Mac 每次結果不同的地方**（Mac 的行為不改，R26.5）：
  - 沒有 `sortedKeys` 的 JSONEncoder 每次啟動鍵的順序不同：範本的定義（`TemplateDefinition.jsonString()`，所以 Mac 每次啟動都會覆寫內建範本，沒有害處）、說話者的復原資料。標準答案先重新排序；Windows 寫的時候用宣告的順序，讀的時候只看內容
  - 字典的順序不固定：一段逐字稿跟兩位說話者重疊一樣多時，Mac 選哪一位不一定（`Diarizer.assignNumbering`、重新辨識的名字沿用）。Windows 一律選編號小的；測試資料避開剛好一樣的情況
- **Swift 的字串規則**（不照做提示詞就會差一個字）：長度、截斷用字形叢集；排序用 Unicode 純量值（不是 UTF-16）；`rounded()` 是四捨五入；正規表示式的 `\d` 是 `Character.isNumber`，包括「一」「十」「兩」「萬」這些 Unihan 有數值的漢字（「說話者 一」也算預設名稱）。都放在 `Shiyin.Core/Text/SwiftText.cs`
- **.shiyin 雙向匯入**：Mac 匯出的封包 Windows 匯入、Windows 匯出的 Mac 匯入，內容完整（R28.3）。這是最能抓到格式差異的測試。

### 4.3 以後 Mac 改了提示詞或匯出

Mac 改完重跑 `dump-golden`、commit 新的 `shared/golden/`，Windows 的測試就會失敗，照著改 C#。CI 每次推送都跑，所以不會漏掉。

## 5. 資料庫

- Microsoft.Data.Sqlite ＋ SQLitePCLRaw 的 e_sqlite3（版本 ≥ 3.34、有 FTS5 trigram）。連線設定照 GRDB：WAL、`foreign_keys=ON`、busy timeout；一條寫入連線＋讀取連線（對應 DatabasePool）。
- migration v1～v9 的 SQL 從 `Sources/Shiyin/Store/AppDatabase.swift:51-243` 照抄。也建立 GRDB 用的 `grdb_migrations` 表、寫入同樣的識別字，**Mac 的資料庫檔案可以直接在 Windows 打開**（用使用者的真實資料測 migration、搜尋時用得到）。以後 Mac 加 v10，Windows 用同樣的識別字加。
- 遷移前先備份到 `backups\shiyin-<時間>-before-<vN>.sqlite`（同 Mac 的程式）。
- 日期、JSON 欄位、FTS rowid 的算法（D27）跟 Mac 相同；聲紋嵌入是 Float32 little-endian 的 BLOB。
- 聲紋的模型跟 Mac 不同（§9）：比對時長度不符的聲紋略過，不改 schema。

## 6. 錄音

- **麥克風**：WASAPI 共用模式、事件驅動，用 Console／Multimedia 角色的端點，**不用 Communications**（Windows 會把其他聲音自動降低 80%）。裝置用端點 ID 記在設定。換裝置、拔耳機：NAudio 3.2 起的 `MMDeviceEnumerator.CreateNotificationClient()` 事件（3.0 起 `IMMNotificationClient` 不公開了），收到就重新開（D25）。
- **電腦聲音**（R54、D129）：預設輸出裝置（Render、Console 角色）的 endpoint loopback，跟麥克風同一套：先請系統轉成 16 kHz 單聲道，不接受時用裝置的格式自己轉。
  - 換預設輸出裝置（`DefaultDeviceChanged` 的 Render、Console）、正在錄的裝置被停用或拔掉時重新開始，接著錄新的預設裝置（同 D25 的 0.3 秒合併）。
  - 沒有任何輸出裝置（例如虛擬機、沒接喇叭的桌機）：不算開始失敗，音源照時間補靜音，等 `DefaultDeviceChanged` 出現新的預設裝置再重開。
  - 不排除拾音自己：錄音中在拾音裡播放錄音會錄進去（R54.3）。process loopback 錄不到新版 Teams（Windows 給全是 0 的資料、不回報錯誤），見 D129。
  - Teams 的聲音不在預設輸出裝置上時錄不到：Teams 的喇叭指定了別的裝置，或藍牙耳機通話時聲音改送到「免持」裝置（預設的通訊裝置，Communications 角色）。2026-10-29 issue #2 的回報者回覆：Windows 預設的輸出和 Teams 的喇叭是同一台 USB 會議揚聲器（Jabra Speak 710），R54 就錄得到；失敗的那幾場是「沒有辨識到語音」（只錄到靜音），不是模型載入失敗。所以「也錄 Teams 指定的裝置」和「顯示卡載入模型失敗時改用 CPU」先不做，有人回報再做（requirements 第 37 節）。
- **沒有聲音在播時，電腦聲音完全不送資料**（連靜音都不送）。所以：
  - 寫檔時電腦聲音那一聲道依時間（QPC）補靜音，不是等它送資料。
  - 3 秒 watchdog（M§20.2）只看麥克風；電腦聲音沒有資料不算中斷。
  - 「電腦聲音一直沒聲音」的提醒（M§26）把沒有資料當成靜音計時。
- **轉成 16 kHz 單聲道**：先請系統直接給 16 kHz 單聲道浮點（`AUTOCONVERTPCM`＋`SRC_DEFAULT_QUALITY`）；麥克風不接受時用裝置的格式，每個來源一個重新取樣器（NAudio 的 `WdlResampler`，純 C#，在 `Services/Capture/StreamResampler.cs`），狀態跨 buffer 保留（對應 `StreamConverter`）。NAudio 3.3 版本釘死，升級時重跑轉錄的比對。
- **電腦聲音補靜音**：音源自己依時間（Stopwatch）補：100 毫秒沒有資料、而且送出的量落後時間超過 0.1 秒時，補到落後 50 毫秒。寫檔的對齊規則因此跟 Mac 完全相同（`RecordingWriter` 照搬）。
- **寫檔**：自己寫 RIFF，格式、雙聲道對齊、補靜音的規則跟 `Capture/RecordingWriter.swift` 相同；每幾秒更新一次標頭，意外結束後用 WAVRepair（照搬）修。暫停時丟掉樣本。
  - 跟 Mac 不同：復原時錄音檔只有 44 bytes 的標頭（錄音開始一兩秒內就當掉，資料還在緩衝區）就刪掉，空的會議一起清掉。Mac 會一直留著、每次開啟都再失敗一次（Mac 版之後再改）
- **音量、收音提醒、磁碟空間**：算法和門檻同 M§26（`Recorder.checkLevels`、1 GB／300 MB），放在 Core，用標準答案比對。音量條的畫法見 §11.2。
- **防止睡眠**（R1.9）：`PowerCreateRequest`＋`PowerSetRequest(PowerRequestSystemRequired)`，整個程序有效；錄音和工作結束時清掉。`powercfg /requests` 看得到。
- **麥克風權限**（R27 R1.8）：開啟時回 `E_ACCESSDENIED` 就顯示「拾音沒有麥克風權限」，按「打開設定」打開 `ms-settings:privacy-microphone`（固定的網址）。Windows 之後如果對桌面 app 也個別詢問，照同樣的流程。
- **開始失敗、中途出問題**：同 Mac 的「錄音的提醒」，Recorder 記下原因（`FailureMessage`、`PermissionDenied`），主視窗顯示一次就清掉。原因要保留原本的說明（例如「找不到可以用的麥克風」），不要換成錯誤代碼。

### 6.3 忘了停止錄音（R48，M§35）

- `Recorder` 記 `LastSpeechAt`，每秒算沒人說話多久：即時逐字稿在跑時，VAD 偵測到說話或出現新的字就算有人說話；沒有即時逐字稿時照 M§35.1 看聲音。暫停時不計時。
- 15 分鐘提醒（錄音列、系統匣的小視窗、Windows 通知，按鈕「停止錄音」「繼續錄」）、30 分鐘自動停止，自動停止的轉錄工作帶 `until`，照 M§35.2、M§35.3。設定 `idleStop` 同 Mac；DEBUG 用 `--set idleRemindSeconds=…`、`--set idleStopSeconds=…`。

## 7. 即時逐字稿（R2、R27 R2.1）

- 混音（左右平均，同 Mac 的 MixTap）先經過自動增益，再送進 Silero VAD（門檻 0.35，D61）；VAD 判定在說話時，約每 0.4 秒把這一句目前的聲音整句丟給 SenseVoice 重新辨識，結果當暫定文字；VAD 判定一句結束時（一句最長約 15 秒，超過就切），最後一次的結果當定稿，存成 `kind='live'` 的段落（同 M§5.3）。
- SenseVoice 的語言跟著會議語言（zh、en、ja）；開 ITN（標點、數字）；拿掉情緒、事件標籤。
- 中文輸出用 OpenccNetLib 的 s2twp 轉成台灣繁體（字典已內建）。
- 在背景執行緒、低於一般的優先順序跑，不能拖到錄音寫檔（R25.2）。
- **自動增益**（D61）：遠距收音的會議很小聲（AISHELL-4 的音量中位數 −46 dBFS），VAD 只抓到兩成的時間。看最近 30 秒的音量（每 0.1 秒一格，第 90 百分位當說話的音量），放大到約 −22 dBFS，最多 24 dB，不縮小，每秒更新；只影響送進 VAD 和辨識的聲音，錄音檔不變。快速辨識也一樣。
- T-W5（Mac 上，D61 的設定，各語言的前 5 分鐘照錄音的速度餵）：定稿延遲中位數 0.50～0.58 秒、第 90 百分位 0.68～0.83 秒；AISHELL-4（中文、遠距）錯字率 18.3%、AMI（英文）WER 33.8%，日文國會聽證有假名；中文轉出來是台灣繁體。
- 自動偵測語言（R16）：照 M§18.8，用 whisper base；切換語言時即時辨識換語言，時間偏移照舊。

## 8. 正式轉錄

> Mac 4.1 起設定裡可選 Qwen3-ASR，預設維持 Breeze（M§32、R43；使用者 2026-10-24 決定）。Windows 4.1 同樣可選（§8.4、D95）：llama.cpp 官方預建的 `llama-server.exe` 當子程序跑，分段、重試、防線照 M§32.4，簡轉繁 `Shiyin.Core/Text/ChineseScript.cs`（標準答案 `util/s2tw.json`）。

- **whisper**：`native/whisper/` 的 CMake 腳本從 commit `d1be6fd` 建置，CI 建好（`build-whisper.ps1` 在建置記錄印出每個 DLL 的 SHA-256）放進安裝檔，不從別處下載。C/C++ 執行階段靜態連結（`CMAKE_MSVC_RUNTIME_LIBRARY`），沒裝 VC++ 可轉散發套件的電腦也載得起來；`build-whisper.ps1` 檢查 DLL 沒有匯入 VC++ 的 DLL（sherpa-onnx、onnxruntime 的 DLL 本來就是靜態連結）。
  - x64：`GGML_BACKEND_DL`＋`GGML_CPU_ALL_VARIANTS`（執行時依 CPU 指令集選）。Vulkan 版另外建、AMD 的電腦才下載（不放進安裝檔，見下面的「AMD 顯示卡加速」、D127）。
  - ARM64：只建一個 CPU 版本，指定最低等級 `armv8.2-a+dotprod+fp16`（Snapdragon 7c、8cx、X 都有；`GGML_CPU_ARM_ARCH`），用 clang 建（同 whisper.cpp 官方的 Windows ARM64 版）。ggml 在 Windows ARM 不支援建多個版本（`GGML_CPU_ALL_VARIANTS`：「Unsupported ARM target OS: Windows」）；用 MSVC 建 ARM64 一定要依建置的機器偵測 CPU 功能（`GGML_NATIVE`），GitHub 的 ARM 機器是伺服器 CPU，偵測出的指令（SVE 等）一般的 Snapdragon 筆電沒有。官方用 armv8.7-a，舊的 Snapdragon 可能碰到不支援的指令；只建新指令集（i8mm）的版本也一樣。取捨：Snapdragon X 用不到 i8mm，稍慢。GPU（OpenCL-Adreno）還是實驗性，先不用。
  - Mac 也建一份 dylib，`Shiyin.Engines` 的測試在 Mac 上就能跑。官方 zip 裡的 `whisper-cli.exe` 拿來交叉比對結果。
- **NVIDIA 顯示卡加速**（R27 R4.6，D64）：x64 有 NVIDIA 顯示卡時（顯示卡驅動的登錄裡 `MatchingDeviceId` 有 `VEN_10DE`），第一次啟動自動下載 whisper.cpp 官方同一版（b5454）的 `whisper-bin-win-cuda-12.4.0-x64.zip`（685 MB，固定網址和 SHA-256，`ModelStore.Model.GpuCuda`），驗證後只解出 whisper、ggml、ggml-base、ggml-cpu-*、ggml-cuda、cudart、cublas、cublasLt（約 1.1 GB）到 `models\gpu-cuda12\`，刪掉壓縮檔。自動下載只做一次（設定的 `gpuPackOffered`），使用者刪掉後不再下載。
  - 載入：整套 whisper 都用元件資料夾裡的（同一次建置的 ggml 才能搭配 ggml-cuda），不跟安裝資料夾的 CPU 版混用。第一次用到 whisper 之前，把安裝資料夾的 `shiyin_whisper.dll` 和 VC++ 執行階段（msvcp140、vcruntime140、vcruntime140_1、vcomp140）複製過去（不一樣才複製，app 更新後 shim 也跟著換），`SetDllDirectory` 加上元件資料夾，再從那裡載入 shim。ggml 用 `LoadLibraryW` 載入 `ggml-cuda.dll`，它要的 cudart、cublas 只會照一般的順序找（安裝資料夾、系統），所以一定要加 `SetDllDirectory`。
  - whisper 已經從安裝資料夾載入（這次開啟已經轉錄過）時換不了，下載好時提示重新開啟拾音。驅動太舊、沒有 CUDA 裝置時 ggml-cuda 不會有裝置，whisper 自己用 CPU。
  - 指定第幾張卡（企業伺服器用，M§36.6、D125）：shim 的 `shiyin_init_device(路徑, use_gpu, flash_attn, gpu_device)`，`shiyin_init` 等於 gpu_device 0；`WhisperControl.GpuDevice` 是 null 時用 `shiyin_init`（app 一律如此）。Qwen 的子程序設 `CUDA_VISIBLE_DEVICES`、`CUDA_DEVICE_ORDER=PCI_BUS_ID`（`QwenServer.Options.Device`）。
  - shim 的 `shiyin_gpu_device` 回傳 whisper 會用的顯示卡（第一個 GPU 裝置的名稱和說明），寫進 log 和回報問題。
  - 元件靠安裝檔裡的 VC++ 執行階段：CI 改成靜態連結執行階段的 whisper 之後，安裝檔還是要帶這四個 DLL（x64 約 0.9 MB）。
- **AMD 顯示卡加速**（R27 R4.7，D127）：x64、顯示卡驅動的登錄裡 `MatchingDeviceId` 有 `VEN_1002`（AMD，含 Radeon 內顯）、而且沒有 NVIDIA 時，第一次啟動自動下載一次拾音自己建的 Vulkan 版 whisper（`ModelStore.Model.GpuVulkan`）。同時有 NVIDIA 和 AMD（AMD 內顯＋NVIDIA 獨顯的筆電）時用 CUDA，不下載 Vulkan。
  - 建置：`windows-gpu-pack.yml`（手動執行）在 windows-latest 裝固定版本的 Vulkan SDK，`build-whisper.ps1 -Arch x64 -Vulkan` 從同一個 commit `d1be6fd`、同一份 CMake 設定建（`GGML_BACKEND_DL`＋`GGML_CPU_ALL_VARIANTS`＋`GGML_VULKAN`）；只打包 whisper、ggml、ggml-base、ggml-cpu-*、ggml-vulkan 成 `whisper-vulkan-x64.zip`（shim 不放：跟 CUDA 一樣用安裝資料夾的那一份），上傳到 GitHub 的 prerelease `win-gpu-vulkan-b5454`。官網（website.md §7）和 Windows 的檢查更新（D94）都跳過 prerelease，Mac 的更新網址是 latest，所以這個 release 不會被當成新版。app 固定網址、大小、SHA-256；whisper 升版才重建。2026-10-29 建的：下載 19.8 MB、解開 59.2 MB（`ggml-vulkan.dll` 45.6 MB）；zip 裡另外有 `parakeet.dll`（whisper 沒有用到），不解出來。
  - CI 沒有顯示卡：裝 Mesa 的 lavapipe（軟體實作的 Vulkan，固定版本），用 Vulkan 版跑一次語言偵測，跟 CPU 版判斷出同一個語言、中英日三個機率差 ≤ 0.03，確認建出來的 ggml-vulkan 算得對。Vulkan 用 fp16 算、CPU 用 fp32，本來就有一點差（Mac 的 Metal、NVIDIA 的 CUDA 也是 fp16；2026-10-29 lavapipe 英文 0.985、CPU 0.998）；算錯的話會判斷錯語言或機率亂掉。lavapipe 要登記到登錄（GitHub 的機器是系統管理員權限，Vulkan 載入器不看 `VK_DRIVER_FILES`）、`GGML_VK_VISIBLE_DEVICES=0`（ggml-vulkan 預設只用獨顯、內顯，lavapipe 是 CPU 類型），Vulkan 的載入器（`vulkan-1.dll`）從 LunarG 的執行階段拿（使用者的電腦是顯示卡驅動附的）。真的 AMD 顯示卡的速度要使用者的電腦量。
  - 解到 `models\gpu-vulkan\`，載入同 CUDA（複製 shim、`SetDllDirectory`、`WhisperEngine.NativeDirectory`；`GpuAcceleration` 依 `Pack` 處理兩種）。Vulkan 的執行階段（`vulkan-1.dll`）是顯示卡驅動附的，不另外帶；驅動太舊、沒有 Vulkan 裝置時 ggml-vulkan 沒有裝置，whisper 用 CPU。
  - Intel 內顯也用這一包，但要先通過檢查才用顯示卡（見下面的「Intel 顯示卡加速」、R4.8、D138）。
  - 自動下載只做一次（設定的 `gpuVulkanOffered`），使用者刪掉後不再下載；轉錄用企業伺服器時不下載（R50.7）。回報問題的「whisper 用的顯示卡」是 ggml 的裝置名稱（例如 `Vulkan0 (AMD Radeon 780M Graphics)`）。
- **Intel 顯示卡加速**（R27 R4.8，D138）：x64、顯示卡驅動的登錄裡 `MatchingDeviceId` 有 `VEN_8086`（Intel）、沒有 NVIDIA 和 AMD 時，`GpuAcceleration.PackFor(x64, nvidia, amd, intel)` 也是 `Pack.Vulkan`，`CheckFirst = true`。下載同 AMD（`ModelStore.Model.GpuVulkan`、同一個 `gpuVulkanOffered`、企業伺服器時不下載）。ggml-vulkan 在 Intel 內顯算得對不對、快不快沒有在實機量過，靠第一次正式轉錄時的檢查決定。
  - 檢查的狀態：設定 `gpuVulkanCheck`：沒有值（還沒檢查）、`running`（檢查中）、`gpu`（通過）、`cpu`（沒通過）。`GpuVulkan` 下載好（`ModelStore.Installed`）時清掉，重新下載就重新檢查。
  - 啟動（`GpuAcceleration.Start`）：`running` 改成 `cpu`（上次檢查到一半拾音當掉，例如顯示卡驅動出錯，log `gpu check: crashed last time`）；`cpu` 不載入 Vulkan 版（從安裝資料夾載入 CPU 版，跟沒有元件一樣）；其他照 AMD 載入 Vulkan 版，`WhisperEngine.UseGpu = 狀態是 gpu`。所以通過之前語言偵測、轉錄都用 Vulkan 版裡的 CPU 後端（同一次建置的 `ggml-cpu-*`）。
  - 第一次載入（`GpuAcceleration.ProbeFirstLoad`，AppHost 在 `Gpu.Start()` 之後、`Controller.Start()` 之前呼叫）：還沒檢查、這次要從 Vulkan 版載入時，先寫 `running`、讀一次 `WhisperEngine.GpuDevice`（shim 一載入就 `ggml_backend_load_all`，ggml-vulkan 會建立 Vulkan instance、列出裝置，`use_gpu=0` 也一樣），讀完清回還沒檢查。驅動在這裡當掉時下次開啟就是 `cpu`，不會之後每次語言偵測、轉錄都當掉；丟出例外（例如少了 DLL）記 `cpu`。只有還沒檢查時才多這一步，在 UI 執行緒，約 1 秒內。
  - 什麼時候檢查（`SpeechEngines.WhisperFile`、`WhisperSamples` → `GpuCheckFlow.Run`）：`CheckFirst`、whisper 從 Vulkan 版載入（`WhisperEngine.NativeDirectory`、`LoadedDirectory` 是元件資料夾）、還沒檢查。whisper 的顯示卡是空的（驅動沒有 Vulkan 裝置）直接記 `cpu`。Qwen 不檢查。
  - 怎麼檢查：從錄音開頭讀最多 180 秒（分聲道轉錄用已經解碼好的那一聲道），每 5 秒一格找平方和最大的 30 秒（`GpuCheckRules.LoudestWindow`）。`WhisperEngine.TimedTranscribe` 用同一個模型、提示詞、語言、參數（beam 5…）開一個 context 轉這 30 秒、計時（不含載入）：**CPU 先**（電腦還沒熱起來時量 CPU，對顯示卡偏保守），再用顯示卡；顯示卡那次先用開頭 2 秒暖機一次、不計時（ggml-vulkan 第一次用到才編譯 pipeline）。讀過的取樣用 `ReplayStream` 接回串流前面，正式轉錄照樣從頭，`CountingStream` 的長度不變。開始前先寫 `running`。
  - 判斷（`GpuCheckRules.Decide`）：CPU 的文字去掉空白少於 10 個字 → 判斷不了，清回還沒檢查（下一場再試）；兩邊的相似度（`QwenRules.Similarity`，difflib 的 ratio）< 0.8 → `cpu`；顯示卡的時間 > CPU 的 0.8 倍 → `cpu`（只快一點點時寧可用 CPU）；其他 → `gpu`。CPU 那次出錯（例如模型壞了）→ 不是顯示卡的問題，清回還沒檢查、照原樣丟出（這一場照一般的轉錄失敗處理）；顯示卡那次出錯（載入失敗、轉錄失敗、驅動丟出的其他例外）→ `cpu`，這一場接著用 CPU 轉；取消 → 清回還沒檢查、照樣取消。
  - 結束拾音時：工作被取消，檢查停得下來就清回還沒檢查；超過 `StopWork` 的等待（顯示卡卡住）留下 `running`，下次開啟當成沒通過。
  - 結果：`gpu` → `UseGpu = true`，這一場接著用顯示卡；`cpu` → `UseGpu = false`，這一場用 Vulkan 版的 CPU 後端，下次開啟改從安裝資料夾載入。log：`gpu check: gpu|cpu|inconclusive similarity=… gpu=…s cpu=…s`（沒有文字內容）。檢查時進度停在 0，這一場多花約 1～2 分鐘（30 秒用 CPU 轉一次，加上顯示卡的暖機和轉錄）。
  - Qwen 的 Vulkan 版只給 AMD：`QwenRuntime`、`OfferQwenGpu`、模型清單都看 `CheckFirst`。
  - 還沒處理：Intel 內顯＋AMD 獨顯的電腦照 AMD（不檢查），ggml-vulkan 用第一個裝置，可能是 Intel 內顯（R4.7 就這樣）。
  - 畫面：模型清單的名稱改「AMD、Intel 顯示卡加速（Vulkan）」；Intel 的說明寫檢查的規則和結果（還沒檢查、用顯示卡、用 CPU）；下載好要重新開啟的提示 Intel 另外一句。回報問題的 whisper 欄：Intel 還沒通過時寫「CPU（Intel 顯示卡還沒檢查）」或「CPU（Intel 顯示卡沒通過檢查）」。
  - DEBUG：`--set debugIntel=YES` 當成有 Intel 顯示卡（同 `debugAmd`）。
  - 驗證：Mac 上的測試（判斷規則、啟動時的狀態、第一次載入、檢查流程用假的轉錄：CPU 先、兩邊各種失敗、取消、分聲道、`ReplayStream`）；`shiyin-eval gpu-check` 用 Mac 的 dylib（Metal 對 CPU）跑一段真的錄音。真的 Intel 電腦（tasks W-INTEL）：發布後看統計裡 Intel 的轉錄倍速、回報問題的 whisper 欄。
- **shim**（D56）：約 100 行的 C，匯出 `shiyin_whisper_full(ctx, const ShiyinParams*, 進度 callback, 取消 callback, …)`，在 C 這邊填好 `whisper_full_params`。不在 C# 複製這個 struct：它用傳值的方式傳、C 的 `bool` 是 1 byte、裡面有巢狀 struct 和 callback，很容易對錯而且不會報錯。其他簡單的函式（`whisper_full_n_segments`、`whisper_full_get_segment_*`、`whisper_full_get_token_p`、`whisper_pcm_to_mel`、`whisper_lang_auto_detect`、`whisper_lang_id`…）直接 P/Invoke，跟 `WhisperEngine.swift`、`LanguageDetector.swift` 用的一樣。
- **參數**同 Mac：beam 5、best_of 5、執行緒數 clamp(核心數 − 2, 2, 8)、`suppress_blank`、有顯卡時用 GPU。只有 CPU 時要不要改（beam、執行緒）等 CI 量完速度再定（tasks 的「速度和記憶體」），改了寫進決策紀錄、設定頁顯示。
- **分段、提示詞、接縫、過濾、低信心**：同 M§21.16、M§6.2，程式在 Core，用 §4 的 `pipeline/` 比對。
- **快速辨識**（R4.1、R20.8）：SenseVoice＋VAD 讀整個檔案。Breeze 還沒下載好時自動用（同 D35），轉完提示「可以用 Breeze 重新轉錄」。錯字率（D61 的設定）：合成會議 9.2%（Mac 的內建辨識 9.2%、Breeze 6.0%）、AISHELL-4 26.4%（Mac 的內建辨識約 25%）、AMI 英文 WER 39.0%（約 26%）、日文 15.5～28.1%（日文的 Whisper large-v3-turbo 4.5～9.2%）。38 分鐘的錄音 14 秒轉完（M3 Pro）。快速辨識的模型還沒下載好時，轉錄線先不接工作、自動開始下載（Mac 的內建辨識不用下載）。
- **跟 Mac 比對（Mac 上，T-W3）**：C# 經 shim 呼叫同一版 whisper.cpp（Mac 的 dylib、Metal），餵一樣的取樣時結果跟 Mac 逐段相同；m4a 解碼器不同造成的取樣差異會讓少數段落不同（合成會議錯字率 5.2% → 6.0%），Windows 用 Media Foundation 解碼後要再比一次。CI x64、ARM64 的 CPU 倍速見 tasks 的「速度和記憶體」。
- **快速辨識的模型怎麼來**（R27 R20.8）：不附在安裝檔，第一次使用時下載（`ModelStore.FastRecognition`：SenseVoice 和 VAD，約 240 MB，比 Breeze 小很多）。首次使用引導一打開就在背景依序下載快速辨識、說話者辨識、正式轉錄的模型（R44.6，`OtherWindows.Onboarding`）；快速辨識的模型下載好就有即時逐字稿。開始錄音時還沒下載好的話，這場沒有即時逐字稿、模型在背景接著下載（`AppHost.MakeLive`），錄完照常轉錄。
- **解碼**：Media Foundation 的 SourceReader 依序讀、轉成 16 kHz 單聲道浮點（同 D34 的串流讀法）。Media Foundation 不支援 CAF、AIFF：AIFF 用 `AiffFileReader`，CAF 不支援（不在匯入的格式清單裡）。T-W9：m4a、mp3、aac、flac、aiff、aif、wma、mp4、mov、m4v、wmv、webm 都解得開；ogg、opus 是 Ogg 容器，要「Web 媒體延伸模組」（一般的 Windows 11 有預裝），沒有時錯誤是 `MF_E_UNSUPPORTED_BYTESTREAM_TYPE`，畫面說明可以先轉檔或去 Microsoft Store 裝（不顯示內部的檔名）。Windows 的 N 版沒有 Media Foundation，要裝 Media Feature Pack（寫進系統需求）。
- **結束拾音**時等 whisper 收尾最多 10 秒（R18.12）。


### 8.4 Qwen3-ASR（R43.9，D95）

- **為什麼是子程序**：whisper（b5454）和 llama.cpp（b11840）各帶一套 `ggml.dll`、`ggml-base.dll`、`ggml-cpu*.dll`。Windows 的載入器處理隱含匯入時只認模組名稱——同名的 DLL 已經載入就直接用它，不看路徑——兩個版本的 ggml 沒辦法在同一個程序裡並存（Mac 的問題是 Swift 不能同時 import 兩份 `ggml.h`，用 C 薄層解；Windows 的問題在載入器，薄層解不了）。所以 Qwen 用 llama.cpp 官方 Windows 版自帶的 `llama-server.exe`：獨立程序、HTTP 在 127.0.0.1 的隨機埠；原生程式當掉不會拖垮拾音，也不用自己建 C 程式（官方有 cpu x64／arm64、cuda 12.4 x64）。
- **模型和執行檔**（`ModelStore.Model.Qwen`，三個檔一起下載、一起算裝好、一起刪）：`qwen3-asr-1.7b-q8_0.gguf`（2.17 GB）、`qwen3-asr-1.7b-mmproj-q8_0.gguf`（356 MB）同 Mac（固定 commit、SHA-256，檔名相同，模型檔可以直接從 Mac 複製）；`llama-b11840-bin-win-cpu-<x64|arm64>.zip`（24／13 MB，固定 SHA-256）只解出 `llama-server.exe`、`llama-server-impl.dll`、`llama-common.dll`、`llama.dll`、`mtmd.dll`、`ggml*.dll`、`libomp.dll` 到 `models\qwen-runtime\`。
- **NVIDIA**（x64）：`Model.QwenCuda`：`llama-b11840-bin-win-cuda-12.4-x64.zip`（272 MB）解到 `qwen-runtime-cuda\`（含 `ggml-cuda.dll` 578 MB，整套 ggml 用同一次建置的）；cudart、cublas 用 whisper 的 `gpu-cuda12\` 那一份（子程序的 `PATH` 加上那個資料夾），所以 GpuCuda 也裝好才用 CUDA 版，不然用 CPU 版。有 NVIDIA 顯示卡而且選了 Qwen（或 Qwen 已經裝好）時自動下載一次（設定 `qwenCudaOffered`），使用者刪掉後不再下載。
- **AMD**（x64，R4.7、D127）：`Model.QwenVulkan`：`llama-b11840-bin-win-vulkan-x64.zip`（37 MB）解到 `qwen-runtime-vulkan\`（`ggml-vulkan.dll` 41.5 MB 加上 CPU 版那些檔案，大小跟 CPU 版一樣），不用另外的執行階段。條件同 whisper 的 Vulkan（有 AMD、沒有 NVIDIA），選了 Qwen（或 Qwen 已經裝好）時自動下載一次（設定 `qwenVulkanOffered`），不需要 whisper 的 Vulkan 元件；`-ngl 999`。Intel 同 whisper 先不用。
- **每次轉錄**（`Shiyin.Engines/Qwen/QwenServer.cs`）：啟動 llama-server（`-m`、`--mmproj`、`-c 4096 -b 2048 -ub 512`、`-t` 同 whisper 的執行緒數、`-ngl 0`（CPU）或 `999`（CUDA、Vulkan）、`--host 127.0.0.1 --port <先找到的空埠> -np 1 --no-webui --no-warmup --jinja`），等 `/health` 回 200（最多 5 分鐘，程序先結束就把 stderr 的最後幾行寫進 log），逐段 `POST /v1/chat/completions`：system = 詞彙表（空的也送，跟 Mac 的薄層一樣）、user = `input_audio`（這一段 16 kHz 16-bit WAV 的 base64）、assistant 先填 `language <Chinese|English|Japanese><asr_text>`（llama-server 預設接著寫下去）；`temperature`、`seed`、`repeat_penalty`、`repeat_last_n`、`max_tokens` 同 Mac 的薄層，`top_k 0`、`top_p 1`、`min_p 0` 讓取樣鏈只剩懲罰和溫度；回應的 `finish_reason` 是 `length` 就是到達 max_tokens。轉完結束子程序（同 Mac 每次載入、釋放）。Windows 用 Job Object（`KILL_ON_JOB_CLOSE`）綁住子程序，拾音當掉子程序也跟著結束；取消是取消 HTTP 請求、結束程序。Mac 原型（`qwen-eval.py`）走的就是這條 HTTP 路，評估結果跟 Mac app 的薄層一致。
- **規則**：分段、重試、幻覺防線、縮寫併回去在 `Shiyin.Core/Transcribe/QwenRules.cs`，流程在 `Shiyin.Engines/Qwen/QwenTranscriber.cs`，照 M§32.4 一字不差（能量用 float 相乘再加進 double，跟 Swift 一樣）；說話者先辨識再分段，段落的說話者用 `SpeakerRules.AssignNumbering` 加短句再確認（§9）。分段用 Mac `shiyinctl qwenchunks` 倒出的 turns 餵 `shiyin-eval qwen-chunks` 比對切點（§4 的標準答案沒有這一項：要真的錄音和說話者片段）。
- **設定**：「正式轉錄」三選一（Breeze、Qwen3-ASR、快速辨識），存 `transcriptionEngine=qwen`（同 Mac）；Qwen 沒下載好、Breeze 在時先用 Breeze（R43.7），兩個都沒有才用快速辨識；日文一律 large-v3-turbo。設定的模型清單多 Qwen（和 NVIDIA 的 QwenCuda）。匿名統計 `engines.qwen`（後台 2026-10-25 已加欄）。
- **評估**：`shiyin-eval transcribe-eval` 的設定 `"engine": "qwen"`（tuning 同 Mac：`qwenChunkSeconds`、`qwenRetry`、`qwenTurns`、`qwenGuard`、`qwenCutsAny`、`qwenSplitMixed`），說話者辨識用 `SHIYIN_DIARIZATION_MODELS`；Mac 上 `SHIYIN_QWEN_SERVER` 指到 macOS 版的 llama-server（原型工具 `~/Library/Caches/ShiyinSpike/qwen3asr/llama/llama-b11840/llama-server`）、`SHIYIN_MODELS` 指到有兩個 GGUF 的資料夾，同一套 C# 流程就能在 Mac 上跑、跟 Mac app 比。`shiyin-eval qwen-chunks <錄音.wav> <turns.json|-> <輸出.json>` 只做分段。

## 9. 說話者辨識與聲紋

sherpa-onnx 的說話者分段是凝聚式分群（`FastClusteringConfig` 只有「剛好 N 群」或門檻），**沒有 PLDA、VBx、「至少 N 人」，也不回傳每個人的聲紋**。用它原本的門檻分群（D47 的 A 方案），中文會議（AISHELL-4）不管門檻怎麼調都會把 6 個人併成 1～3 人（整場錄音的聲紋都很像）。所以改成（D58）：

1. **sherpa-onnx 只用來切段落**：固定分成 30 群（分得很細，切出的段落才不會跨兩個人；錄音很短時不超過每 10 秒一群），pyannote 的窗每次移 0.5（sherpa-onnx 預設 0.1，快五倍、結果差不多）。
2. **每段切成約 3 秒的小段**，各算一次聲紋（3D-Speaker ERes2Net base，中文 3D-Speaker 資料集訓練，512 維，40 MB）。不到 0.5 秒的不算。
3. **重新分群**（`Core/Transcribe/SpeakerClustering.cs`）：先減掉整場的平均聲紋再比 cosine；凝聚式分群，群中心是依長度加權的和，最像的兩群低於 0.25 就停，有「至少 N 人」時合併到剩 N 群也停。說話總長不到 30 秒（而且不超過總長的 5%，短的會議不會把話少的人併掉）的群，每個小段各自併到最像的大群，但至少留 N 群。最後看「不減平均」的群中心（但不少於「至少 N 人」）：只有一個人說話的錄音，減掉平均後剩下的是雜訊，會被拆成好幾群。兩步：最像的兩群在 0.7 以上就合併，重複到沒有；剩下的群每一對都在 0.6 以上，就是只有一個人，全部合併。每段取它小段裡長度最多的群；沒有小段的短段落跟著 sherpa 的同一位最常被分到的群。
   - 門檻怎麼定的：分群後的群（各 20 秒以上）不減平均的中心相似度，不同的人最高 0.58（AMI ES2004c 的陣列麥克風，遠距收音時房間的共同成分重），同一個人被拆開的群最低 0.63（AMI 剪出的 5 段單人錄音、合成會議）。只用一個 0.55 的門檻時，陣列麥克風那場的兩位女性被併成一位、聲紋帶錯名字，所以分兩步
   - 結果：單人錄音 6 段都是 1 位；AMI、AISHELL-4、聲紋比對都跟沒有這一步時一樣，只有 AMI TS3003a 從 4 位變 2 位（另外兩位只說了 7 秒、6 秒）
   - 分不準的提醒（第 5 點）：一個窗裡 sherpa 的編號聲紋（不減平均）在 0.6 以上的先當成同一個聲音再算碰撞，不然單人錄音的碰撞率很高、會誤報。單人錄音 0、正常分群 0.18～0.32、故意併成一人 0.42～1，門檻 0.37 不變
4. **每位說話者的聲紋**：最長的幾段（加起來到 60 秒）的小段聲紋依長度加權平均（Mac 是最長的幾段各算一次）。
5. **分不準的提醒**（D39）：每 10 秒一窗，窗裡每個 sherpa 的編號（分得很細）說了 1.5 秒以上的，用那幾個小段的聲紋平均當「片段的說話者」，算法照 `Diarizer.quality`（凝聚度、只分出一群時看碰撞率）。

結果（Mac M3 Pro、5 個執行緒；Mac 的 FluidAudio 在括號裡）：AMI ES2004a（英文，17.5 分鐘）4 人、單人說話的時間裡標對 94.9%（94.6%）、50 秒；AISHELL-4（中文，38 分鐘）6 人、96.6%（96.7%）、104 秒；AMI 9 場有 7 場剛好 4 人（ES2004b 多 1 人、ES2002a 少 1 人；Mac 9 場都對）。

- 門檻 0.2～0.35、最小群 20～45 秒在這三組資料上都差不多，取中間。sherpa 內部改用 CAM++ 只快 30%，AISHELL-4 會多分 1 人；兩段都用 CAM++ 時 AISHELL-4 掉到 87.6%。
- **聲紋比對門檻 0.6**（Mac 0.65）：AMI ES2004a 學、b～d 和遠距麥克風比對，同一人最低 0.632（遠距麥克風）、最高 0.916；別組 4 場的陌生人跟名單裡任何人最高 0.535。間距比 Mac 小（兩邊各約 0.03～0.07），使用者實際用了再看。
- **聲紋分兩段**（R33、M§21.4、D91）：同 Mac，很有把握才直接帶入、中間的改成待確認的說話者題目。Windows 的模型分數比較低，門檻另外定：相似度 ≥ **0.78** 而且比第二像的人高 **0.15** 以上才直接帶入，0.6～0.78 改成問。用 `vp-two.json`（AMI 9 場）模擬：只用第一場學時 17 位直接帶入 10、問 7、帶錯 0；聲紋越存越多時直接帶入 16、問 1。0.75 時第一次也是問 7，取 0.78 多留一點給中間的。`DatabaseTests.SameAsMac` 比標準答案時換成 Mac 的 0.65、0.88、0.15。
- **分不準的提示門檻 0.37**（Mac 0.4）：AMI 9 場正常分群的分數 0.21～0.33、AISHELL-4 0.19；故意併成 1 人時 0.34～0.48（9 場有 8 場超過 0.37）、併成 2 人時 0.30～0.40。
- **很短的一句**（R34、M§6.3.2、D93）：分好說話者之後，0.5～4 秒的句子各自用同一個聲紋模型（ERes2Net）算一次聲紋，跟每位說話者的聲紋（第 4 點）比，比另一位像 **0.25** 以上就改成那一位（`Core/Transcribe/SpeakerRecheck.cs`，接在 `TranscriptionPipeline.Speakers` 和 `JobRunner.Rediarize` 的 `AssignNumbering` 後面；使用者改過的句子不動；算不出來就照原本的結果）。Mac 的第 1 步（FluidAudio 不到 1 秒就不算聲紋的設定）Windows 沒有：每個 0.5 秒以上的段落本來就各算聲紋，沒重疊的 2 秒內句子原本就標對 59.5%（Mac 改之後 59.8%）。
  - 評估：`Shiyin.Eval diarize-eval` 的設定加 `embedSegments` 輸出每句的聲紋（離線試門檻），加 `recheck` 照 app 的規則跑（`margin` 換門檻）。逐字稿用標準答案的每一句（同 M§6.3.2），「完整逐字稿」用 Mac 的 Breeze 逐字稿。結果：AMI 9 場沒重疊、2 秒內的句子 59.5% → 61.4%（改錯 0），含重疊 42.8% → 47.1%；AISHELL-4 含重疊 73.3% → 77.5%；完整逐字稿單人說話的時間裡標對 AMI 94.9% → 95.5%、AISHELL-4 96.6% → 97.1%（人數不變）。
  - 門檻怎麼定的：照 Mac 用長句（3 秒以上、最長的 20 句）的平均、差距 0.15，AMI 沒重疊的句子改錯 5：Windows 的模型只聽得到這一句，0.5～1 秒的聲紋很散。改用每位說話者的聲紋，0.25～0.3 都是改錯 0，0.24 以下開始改錯。先減掉整場的平均再比反而比較差。句長上限 2～10 秒結果差不多，照 Mac 用 4 秒。
  - 時間：約是說話者辨識的 10～20%（M3 Pro，AISHELL-4 38 分鐘多 11.6 秒）；不用另外下載模型。
- 合併、改句子的說話者、復原、重新辨識的邏輯同 M§23、M§24（在 Core、Services）。

## 10. 整理工具

- 六種後端照 `Notes/Backends/` 翻成 C#（HttpClient），重試、取消、錯誤分類同 M§19、M§21。
- **找 claude、codex**（T-W6）：claude 先找原生安裝的 `%USERPROFILE%\.local\bin\claude.exe`、PATH 裡的 `claude.exe`，再找 npm 的；codex 找 npm 套件裡真正的執行檔。**不能透過 `.cmd` 執行**：cmd.exe 的命令列只有 8191 個字元、跳脫規則不同，`--system-prompt`、`--json-schema` 會被截斷或解讀錯，還有參數注入的風險（BatBadBut 那一類）。找不到真正的執行檔時用 `node.exe` 加 js 的路徑。
  - npm 的全域資料夾：官方安裝檔是 `%APPDATA%\npm`；zip 版的 Node、nvm、scoop、Volta 在別的地方，所以也找 PATH 裡有 `claude.cmd`、`codex.cmd` 的資料夾（只拿來定位套件，不執行 .cmd）。
  - 套件的樣子（2026-10-21，claude-code 2.1.306、codex 最新版）：claude-code 的 npm 套件直接帶 `bin\claude.exe`（已經沒有 `cli.js`，舊版才用 node 跑 cli.js）；codex 的執行檔在平台子套件 `@openai/codex-win32-<arm64|x64>/vendor/<平台>/bin/codex.exe`，旁邊的 `codex-path`（有 rg.exe）加進子程序的 PATH（同 codex.js）。
  - 長度：逐字稿和提示詞走標準輸入（claude 的 `-p`、codex 的 `exec -`），命令列只放系統提示詞和 JSON schema（標準答案裡最長的約 2,400 字和 1,800 字），遠低於 Windows 命令列 32,767 字元的上限。
- 參數一律用 `ProcessStartInfo.ArgumentList`（同 Mac 的規則）；子程序的 PATH 用 `;` 接上找到的資料夾；工作目錄是 `tmp` 下的空資料夾。
- 子程序放進 Job Object（`KILL_ON_JOB_CLOSE`）：拾音當掉時子程序一起結束；逾時時整棵程序樹一起結束。
- 登入：用固定參數開 Windows Terminal（沒有就用 `cmd.exe /k`）執行 `claude` 或 `codex login`，參數裡不放任何使用者內容。
- **還沒設定整理工具**（R35、M§19.6、D95）：同 Mac。`AppSettings.DetectCli` 由 App 用 `WindowsToolLocator` 找 claude、codex（要掃過 PATH 的每個資料夾，快取 30 秒）；Services、測試裡沒給就是找不到。DEBUG 加 `--set debugNoCli=YES` 當成找不到。
- **Codex 桌面版附的 codex**（R55、M§37、D128）：npm、PATH 都找不到 codex 時才找（`WindowsToolLocator.CodexDesktop`，結果留 30 秒，不記住路徑）。實際的樣子是從 openai/codex 的 issue 查到的（#40700、#40867、#42645、#54698，2026-08～10 的 26.820～26.930 版；虛擬機沒有 Microsoft Store，裝不了）：
  - 套件：Microsoft Store 的 MSIX `OpenAI.Codex`，package family `OpenAI.Codex_2p2nqsd0c76g0`，裝在 `C:\Program Files\WindowsApps\OpenAI.Codex_<版本>_x64__2p2nqsd0c76g0\`。安裝資料夾用 `Platform/Packages.cs`（kernel32 的 `GetPackagesByPackageFamily` 加 `GetPackagePathByFullName`，LibraryImport）查：`WindowsApps` 一般使用者列不出內容，不能用掃資料夾的方式找；資料夾名稱有版本號，所以每次重新查。2026-10-29 在虛擬機用控制通道的 `packages Microsoft.AAD.BrokerPlugin_cw5n1h2txyewy` 驗過：查得到完整名稱、`C:\Windows\SystemApps\…` 的位置和 `Application` 的 Id；沒裝的 `OpenAI.Codex_2p2nqsd0c76g0` 是空的。
  - 執行檔：套件裡是 `app\resources\codex.exe`（旁邊有 `codex-command-runner.exe`、`codex-code-mode-host.exe`），沒有 Mac 那種說明檔。但**直接執行 WindowsApps 裡的那份，有些電腦會拒絕存取**；桌面版自己是先複製到 `%LOCALAPPDATA%\OpenAI\Codex\bin\<16 位的雜湊>\codex.exe`（每個版本一個資料夾，`.staging-…` 是複製到一半的）再執行。所以順序是：
    1. `%LOCALAPPDATA%\OpenAI\Codex\bin\` 底下最新建立的雜湊資料夾裡的 `codex.exe`（套件還在才找：移除桌面版之後當成沒有安裝，R55.4）
    2. 還沒複製過（裝了但沒開過桌面版）：`Shiyin.LLM/DesktopApps.cs` 的 `CodexPackage.Find(<安裝資料夾>\app\resources)`（跨平台，Mac 上的 `dotnet test` 測得到）：先找跟 Mac 一樣的說明檔 `codex-cli\codex-package.json`（以後的版本可能會有；規則同 M§37.1：`layoutVersion` 1；`entrypoint`、`pathDir` 是相對路徑，不接受絕對路徑、磁碟代號、`..`，展開後要在 `codex-cli` 裡面；**`entrypoint` 一定要是 .exe**：.cmd、.ps1 要經過 cmd.exe 或 PowerShell，參數會被改寫）。有說明檔但不對就當作沒有；沒有說明檔時用 `resources\codex.exe`。不對的原因記 log（同一個原因只記一次）。
  - 執行不了（`Process.Start` 丟 `Win32Exception`，例如拒絕存取）：`CodexCli` 把它變成「沒辦法執行 Codex 附的 codex（原因）。可以另外安裝 Codex CLI（npm install -g @openai/codex）再試。」（R55.4）；查登入狀態失敗時照舊當作看不出來。
  - app 的名稱、打開 app：`Packages.Manifest` 讀套件資料夾的 `AppxManifest.xml`，名稱用 `Properties/DisplayName`（是 `ms-resource:` 時用「Codex」），第一個 `Application` 的 `Id` 組成 `<package family>!<Id>`。找到的 `ToolCommand` 多 `DesktopApp`（名稱）、`AppUserModelId`；`CliLogin.Open` 遇到它們就 `Shell.OpenPackagedApp`（`explorer.exe shell:AppsFolder\<id>`，`ArgumentList`，不經過 cmd.exe），提示「拾音：正在打開 Codex。在那裡登入之後，回到拾音按「重新檢查」。」，不開終端機。
  - 訊息：`DesktopApps.CodexAppName`（App 啟動時接成 `Tools.Find("codex")?.DesktopApp`）給 `LLMException.NotLoggedIn("codex")`、`SummarizerProblem` 的訊息和按鈕、失敗橫幅的「打開 Codex 登入」、`LLMException.IsLoginMessage`（同 M§37.2：標準答案用的 `IsLoginProblem` 不改）。設定頁、引導的文字同 Mac（「使用 Codex 附的 codex（這台電腦沒有另外安裝 Codex CLI）」「Codex（使用 Codex 附的 codex）」「打開 Codex 登入」）；Windows 沒有環境檢查的清單。
- **Claude 桌面版**（R56、M§37.3）：`WindowsToolLocator.HasClaudeDesktop`（接到 `DesktopApps.HasClaude`，留 30 秒）看 MSIX 套件 `Claude_pzs8sxrjxfjjc`（Store 和官網的安裝檔現在都是 MSIX），或舊版安裝在 `%LOCALAPPDATA%\AnthropicClaude\claude.exe`。提示在 `Shiyin.App/ClaudeCodeHint.cs`：指令是 PowerShell 的 `irm https://claude.ai/install.ps1 | iex`（「打開 PowerShell，貼上這一行安裝：」，Consolas 字型、可以選取）、「複製」（翻譯表裡的「複製」是 Duplicate，這裡用 `L.English ? "Copy" : "複製"`）、「安裝說明」、「重新檢查」（清掉桌面版的快取、重畫）。原生安裝放在 `%USERPROFILE%\.local\bin\claude.exe`，本來就找得到。出現的地方、訊息同 Mac。
- DEBUG：控制通道的 `packages <package family>`（`Packages.ByFamily`、`Manifest` 的結果）、`tools refresh`（清掉桌面版的快取）；`--set debugOnlyDesktopCli=YES`（不看使用者自己裝的 claude、codex，只找桌面版）、`--set debugCodexApp=<資料夾>`（把這個資料夾當成 Codex 桌面版的安裝資料夾，不問系統）、`--set debugClaudeDesktop=yes|no`；`--set debugNoCli=YES` 照舊連桌面版都找不到。`state` 多 `tools`：`claude`、`codex`（`path`、`desktopApp`、`appId`、`pathEnv`）、`claudeDesktop`。

### 10.1 整理工具設定好才用、失敗說清楚怎麼修（R45～R47、M§34）

- 同 Mac：`AppSettings.SummarizerProblem`（`NotChosen`、`Off`、`NotInstalled`、`NotLoggedIn`、`MissingKey`、`Incomplete`、`Paused`），自動排程改判斷 `SummarizerReady`；登入狀態用 `CliLogin` 的快取（app 啟動、視窗回到前景超過 60 秒、設定頁、引導時查），查不到當作準備好；key 用認證管理員有沒有這一筆（不讀出來）。
- 引導的整理工具這一步、設定視窗的整理工具頁、會議頁的提示和失敗橫幅照 M§34.3、M§34.4；沒安裝的 CLI 在下拉選單裡 `IsEnabled = false`。
- `LLMError` 多 `Rejected`、`Limited`、`Unreachable`、`NotConfigured`；暫停只在記憶體，指紋是 provider、模型、OpenAI 相容 API 的網址、認證管理員的寫入次數。
- 統計的 `errorDetail` 同 M§34.5（`UsageStats.cs`），測試比對 Mac 產生的 JSON 鍵。
- 「其他」的種類（R86、M§61.1）：`LLMException.Cause`（`FailureCause`，`Key()` 是 Mac 的 rawValue），`LLMException.Failed(訊息, 種類)`；CLI 的結束代碼負數（NTSTATUS，被系統結束）或大於 128 是 `CliSignal`。統計的 `summarizeOther` 同 Mac（只送大於 0 的）；`LLMTests.FailureCausesFromApi`、`FailureCausesFromCli`、`UsageStatsTests.SummarizeOtherCauses`。
- 額度用完（R62、M§40）：`LLMException.IsLimitProblem` 的說法、`LLMException.CliLimited` 的兩行訊息同 Mac；`ServicesTests` 列出各種說法，`LLMTests` 用假的 claude、codex 腳本測。

## 11. 介面

### 11.1 視窗對照

| Mac | Windows |
|---|---|
| 主視窗（NavigationSplitView：側欄、搜尋、會議內容） | 主視窗：左邊會議清單（含資料夾、搜尋框、垃圾桶、匯入），右邊會議內容 |
| 會議頁的分頁（紀錄、逐字稿、提問，R44.1） | `SelectorBar`，Ctrl+1～3；待確認是紀錄頂部的 `Expander`、我的筆記是逐字稿頂部的 `Expander`（§11.6） |
| 錄音中的畫面、播放列、時間軸 | 同樣的排版；時間軸用 Canvas 放矩形、三角形、圓點 |
| Settings（左邊側欄十一項，R108、M§82） | 獨立的設定視窗，左邊 `NavigationView` 十一項分四組、上方搜尋（§11.41）；「一般」頁多一個介面語言（R26.4、D57） |
| 引導、使用說明、回報問題、問所有會議 | 各自的視窗 |
| MenuBarExtra | 系統匣圖示（WinUIEx 的 TrayIcon）：左鍵打開主視窗，右鍵選單有錄音狀態、暫停、停止、進行中的工作、打開、說明、設定、結束。Windows 11 預設把新圖示收進「^」，引導的最後一步說明 |
| 偵測會議的浮動視窗（NSPanel） | 右下角、最上層、不搶焦點的小視窗（`WS_EX_NOACTIVATE`），30 秒收起 |
| sheet、alert、popover | `ContentDialog`、`Flyout` |
| 關掉主視窗 | 隱藏到系統匣，錄音和工作繼續（R1.7）；從系統匣或選單「結束」時，有工作在跑要確認（同 Mac） |

快捷鍵：Mac 的 ⌘ 換成 Ctrl（Ctrl+O 匯入、Ctrl+R 錄音、Ctrl+Shift+P 暫停、Ctrl+B 標記重點、Ctrl+E 匯出、Ctrl+F 搜尋逐字稿、Ctrl+1～3 分頁、空白鍵播放、Ctrl+Delete 丟到垃圾桶）。

**畫面全部用 C# 建，不用 XAML 檔**（D59）：`Shiyin.App/Ui/Ui.cs` 集中顏色、字型和常用元件的寫法；資源只加 `XamlControlsResources`，主題色設成青瓷綠。這樣 Mac 上也能編譯檢查（XAML 編譯器是 Windows 的程式）。

### 11.2 效能（同 R20.3、R25）

- 一直在變的東西（音量條、計時、播放位置）不經過綁定通知整個畫面：音量條用 Composition 的 visual 自己定時讀音量（對應 D40）；計時只更新那一個 TextBlock。
- 逐字稿列表用 `ListView` 的虛擬化，`ContainerContentChanging` 時才建出那一段的畫面（不用 DataTemplate 和綁定），播放、搜尋時只重畫看得到的幾段。
- 服務的事件在背景執行緒觸發，`AppHost.Post` 切回 UI 執行緒；資料改了 0.15 秒內合併成一次重讀（`MainState`）。錄音中的變化（計時、即時文字、提示）只更新錄音的畫面，不重讀資料庫；工作的進度每秒更新一次，只改列表每一列的狀態文字、進度條和會議頁狀態列的文字、進度條：會議沒有增減、標題日期資料夾沒變時不重建列表（整個重建會一直閃），狀態列的樣子（狀態、階段、在跑的工作）沒變時不換掉（不然「取消」按鈕每秒被換掉一次）。
- 量法同 Mac：DEBUG 的 `metrics` 回報 UI 執行緒最長卡住多久（每 10 毫秒一次的計時器，`metrics reset` 歸零）。T-W10（虛擬機）：2,100 段、2.1 小時的逐字稿只建出看得到的 54 列；一頁一頁捲最長卡 82 ms、一行一行 51 ms、跳著捲 122 ms（Mac 播放跟隨的標準是 85 ms）。播放跟隨要有音效裝置，虛擬機量不到。
- 錄音中（`record-sim` 1 倍速、即時逐字稿開著）CPU 約一個核心的 31%（虛擬機 4 核；SenseVoice 在程序裡跑，Mac 的 SpeechAnalyzer 在系統的程序裡，不能直接比）。

### 11.2.1 WinUI 的寫法要注意（虛擬機驗證時踩到的）

- 同一個元件不能放進第二個父元件（0x800F1000，UI 執行緒的例外會讓程式結束）：一直留著的元件（輸入框、音量條）重畫時先從舊的父元件拿下來，或一開始建好只切換顯示。
- `TextBox` 要多行時，`AcceptsReturn` 一定要在 `Text` 之前設：單行的 TextBox 設 Text 時只留第一行（範本的說明、回報問題的內容曾經被截掉）。TextBox 把換行存成 `\r`，存進資料庫或提示詞前換成 `\n`。
- 閱讀的欄位（紀錄、提問、編輯會議紀錄、使用說明）用 `ReadingColumn`（同 Mac：填滿可用的寬度、最寬 680 或 760、靠左）。不能用 StackPanel 設 `MaxWidth` 加靠左：靠左時寬度是內容要的寬度，會換行的文字算出來很窄，整欄擠成一半。文字後面接時間點用 `Ui.Inline`（文字一欄、時間照內容寬），不能兩欄都給比例寬度（會各分一半）；放在橫的 StackPanel 裡的文字不會換行，長的會被切掉。
- **要宣告支援高 DPI**（`src/Shiyin.App/app.manifest` 的 PerMonitorV2，csproj 的 `ApplicationManifest`）：WinUI 的專案範本會帶這個檔案，純 C# 建的專案沒有，app 會是 DPI unaware，縮放不是 100% 時 Windows 把整個視窗的點陣圖拉大、字會糊（大部分筆電預設 125%～150%）。Windows App SDK 建置時會把元件註冊合併進這個 manifest。視窗大小的程式本來就是照 `GetDpiForWindow` 換算的邏輯像素。
- 逐字稿每一段的時間點跳到段落真正的開始（`Ui.Time` 的 `at`）：顯示的時間去掉了小數，24.4 秒的段落顯示 00:24，照顯示的時間跳到 24.0 秒，播放位置的高亮會落在上一段。會議紀錄裡的時間點（整理工具寫的字串）照舊。Mac 也是照顯示的時間跳（tasks 的「Mac 的小問題」）。
- 粗體用 `FontWeights.Bold`（`Ui.Text(bold: true)`），不要用 SemiBold：中文、日文沒有 SemiBold 的字重，Windows 會換成明體的假粗體，跟其他文字不一樣。ContentDialog 內建的標題也是 SemiBold，標題要用 `Ui.DialogTitle`。
- 文字加按鈕的一列（狀態列、範本的說明、錄音列）用 `Ui.Bar`：長的文字或會被截的東西放中間那欄（左欄寬度跟著內容、不會換行），按鈕放右邊照內容寬。全部放在橫的 StackPanel 裡時，英文的按鈕比較寬，會被擠出視窗。放不下時整個藏起來、不要切一半的（錄音列的兩個音量條）用 `FitRow`。逐字稿的搜尋列是 Grid：搜尋框最寬 300、可以縮。
- `TextHighlighter` 要給 `Foreground`：預設是黑色，深色模式下標起來的字看不見。
- 視窗的最小尺寸同 Mac（主視窗 960×620、設定 720×500（§11.41）、其他 520×440，`WindowPlacement.MinimumSize`，不超過螢幕的工作區）。
- 介面巡檢的做法：虛擬機用 `ctl.sh open settings|guide|report|ask|onboarding`、`uia.sh` 按分頁和選單、`shot.sh` 截圖，淺色、深色（改 `AppsUseLightTheme` 再廣播 `ImmersiveColorSet`，app 不用重開）、中文、英文（`--set interfaceLanguage=en`）、最小視窗（`SetWindowPos` 拉到比最小還小）各走一次；測試資料夾裡有各種狀態的會議（整理失敗、猜到名字、校正建議、分不準的提醒、待確認、日文）。
- 可以選取的 `TextBlock` 有自己的右鍵選單（拷貝、全選），會蓋掉外層的 `ContextFlyout`：文字上也要設同一個選單。
- 快捷鍵掛在整個視窗時設 `KeyboardAcceleratorPlacementMode = Hidden`，不然滑鼠停在視窗裡會跳出快捷鍵提示。
- 無障礙名稱（螢幕閱讀器、UI Automation）：只有圖示的按鈕、沒有標題的輸入框和下拉選單、`ListViewItem`（內容是好幾個文字元件時，名稱會是空的或資料的型別名稱）都要設 `AutomationProperties.Name`。新的畫面用虛擬機的 `uia.sh dump` 檢查。
- 錄音開始時先接好每個聲道的 `OnSamples`，全部接好再 `Start()`：模擬錄音的第二個聲道是第一個的鏡像，第一個一開始就送資料，第二個還沒接上的話開頭那幾段會丟掉、兩個聲道對不齊（CI 的 macOS 機器比較慢時 `RecorderTests` 失敗）。真的錄音（WASAPI）順序沒有差別。
- `ReleasePointerCapture` 會馬上（同步）觸發 `PointerCaptureLost`：在 `PointerReleased` 裡放掉捕捉之前，先把要用的拖曳狀態記下來（2026-11-17 虛擬機：編輯會議在波形上拖曳選取，`PointerCaptureLost` 先把「拖曳中」清掉，放開時變成點一下）。
- 服務要在 UI 執行緒建、在 UI 執行緒關（MediaPlayer、全域快捷鍵）；結束時背景只做停止錄音和工作，`AppHost.Post` 不再更新畫面，回到 UI 執行緒再關播放器和資料庫（錄音中按「結束」曾經卡死）。

### 11.3 外觀、字型

- 顏色（celadon、signal、說話者 8 色）在 `Ui/Ui.cs`，每個顏色有淺色、深色兩個值，跟著視窗的主題換（Mac 是 `Views/Theme.swift`）。
- 視窗的底色：淺色白、深色 #202020（Windows 11 的 SolidBackgroundFillColorBase），每個視窗的最外層用 `Ui.WindowBackground` 設、跟著深淺色換。沒設時深色模式是純黑。主視窗的側欄淺色 #F7F7F7、深色 #2B2B2B（跟內容區分得出來，同 Mac 的側欄）。對比主題（高對比）開著時兩種都換成系統的視窗底色（`UISettings.UIElementColor(Window)`）。
- 對比主題（高對比，設定 → 協助工具 → 對比佈景主題；2026-11-16 在虛擬機用「夜空」「沙漠」巡檢）：畫面只用系統色（視窗底、文字、反白／反白的字、超連結、停用的字、按鈕），做法如下。
  - `Application.HighContrastAdjustment = None`（`App` 的建構式）：預設 Auto 時 WinUI 把不是系統色的字換成文字的顏色、後面墊一塊視窗底色的底板，選到的列（反白的底）、強調色的按鈕上每段字都是一塊黑底（「夜空」）。關掉之後顏色由拾音自己負責。
  - `Ui` 的顏色在對比主題時直接給系統色（`Ui.SystemColor`／`SystemBrush`，`UISettings.UIElementColor`）：`Celadon`、`AccentText`、`AccentFill` 是反白的顏色（同 WinUI 的強調色在對比主題的做法）；`Signal`、`Danger`、說話者 8 色、`Primary`／`Secondary`／`Tertiary`、`Divider` 是文字的顏色（琥珀、紅靠圖形和文字分辨）；`CardBackground` 是視窗底色；`Subtle`（淡淡的底）透明，畫圖形沒亮的部分改用 `Ui.Track`（停用的字的顏色；音量條、引導的步驟、播放列的底線）；青瓷綠實心上面的打勾、數字用 `Ui.OnCeladon`（反白的字）。
  - 淡淡上色的一塊（建議條、狀態條、待確認展開的那一塊、提問的回答、錄自己聲音的那段文字）：`Ui.Tint` 在對比主題時透明，`Ui.Tinted(border, 底色)` 改成沒有底色、一圈文字顏色的細框（同 Windows 設定的卡片在對比主題的樣子）。逐字稿目前播放、跳過來的段落改成一圈框（播放的是反白的顏色），標起來的字（搜尋、校正建議）是反白的底配反白的字。
  - 清單的列、超連結按鈕、整列是按鈕的會議列裡的字和圖示不自己給顏色：`Ui.FollowForeground(內容)` 在對比主題時把 `TextBlock`、圖示的 `Foreground` 清掉，跟著列或按鈕的前景色（平常是按鈕的字或文字的顏色，選到、滑過去是反白的字，超連結是超連結的顏色）；跳過控制項（「⋯」這些按鈕自己處理）和有自己底色的一小塊（頭像、資料夾齒輪的小圓）。會議列的狀態文字（失敗、待確認）每秒更新時也不上色。用在側欄的導覽、會議、人物、搜尋結果的列，會議頁上方和逐字稿的說話者，人物頁的會議列和待辦表的會議連結、常參加的人。
  - 頭像：視窗底色的圓、文字顏色的字和外圈（「我」的外圈 2）；底色不透明，放在選到的列上也看得清楚。
  - `Ui.Borderless`（沒有框的按鈕：會議設定、文件、鉛筆、合併到、移除這個人、篩選的漏斗和 ✕、「⋯」）在對比主題時照平台的按鈕（按鈕色的字和框，滑過去反白）：沒有框的按鈕看起來跟一般的字一樣，原本覆寫的滑過底色（`Subtle`）和字（反白的顏色）也不是成對的系統色。紅字的按鈕（移除這個人…）也是按鈕色。待確認的標記「？」同樣不上琥珀色。時間點（`Ui.Time`）、提問的出處連結不給青瓷綠，照平台的超連結顏色。彈窗、編輯畫面右上角的 ✕（`Sheet.Header`）平常往右上多出去一點對齊，對比主題時不移：按鈕有框，多出去的那段被欄位切掉、框缺一角（2026-11-17「水生」）。
  - 開著 app 開關對比主題、換一個對比主題（`ActualTheme` 不變）：`Ui.OnContrastChanged(元件, 動作)` 聽 `UISettings.ColorValuesChanged`（不在 UI 執行緒，排回元件的 `DispatcherQueue`），`Ui.ContrastKey`（沒開時空字串，開著時是用到的系統色；每次重讀對比主題開了沒，`Ui.HighContrast` 平常用記住的值，建一頁要問上千次）變了才做：主視窗重新給顏色（`restyle`）、重建列表、重畫；列表、人物頁「內容一樣時不重建」的比對加上 `Ui.ThemeKey`（深淺色＋`ContrastKey`）。`AccessibilitySettings.HighContrastChanged` 在沒打包的桌面 app 訂閱時丟 0x80070490（2026-11-15 虛擬機啟動就當掉），不用。已知：從一個對比主題直接換成另一個時，平台的 `SelectorBar`（紀錄／逐字稿／提問、條列／心智圖）選取的那一條底線留著前一個主題的反白顏色，重開拾音才換（一般→對比、對比→一般會換）；設定、引導這些另外開的視窗開著時換主題，自己的顏色要重開視窗才換（底色會換）。
  - 驗證：虛擬機用 `Start-Process` 開 `C:\Windows\Resources\Ease of Access Themes\hc2.theme`（夜空）、`hcwhite.theme`（沙漠）套用（`hc1` 黃昏、`hcblack` 水生），等 `CurrentTheme` 換好再關掉跳出來的設定視窗；改回原本的主題是同樣開 `%LOCALAPPDATA%\Microsoft\Windows\Themes\Custom.theme`，`HKCU\Control Panel\Accessibility\HighContrast` 的「High Contrast Scheme」、`Themes` 的 `LastHighContrastTheme` 會記成剛剛用過的對比主題，測前 `reg export`、測完改回（原本是「High Contrast Black」、`hcblack.theme`）；剛開機時第一次開 `.theme` 可能等不到套用（設定 app 還在啟動），再開一次。停虛擬機見 §15.2（不要強制斷電）。
- 介面字型用系統預設（Segoe UI Variable、微軟正黑體）。會議標題的明體：Mac 用宋體，Windows 中文、英文用新細明體（`PMingLiU`，沒有時退回 `Microsoft JhengHei UI`），日文用 `Yu Mincho`，都是粗體（`Ui.TitleFont`、`Ui.Heading`）。
- 會議內容依會議語言設定 `Language`（ja-JP 時日文字形，對應 D20）。
- 匯出（PDF、docx）的字型：中文 Microsoft JhengHei／PMingLiU、日文 Yu Gothic／Yu Mincho、英文 Segoe UI／Georgia（Mac 是 New York）。

### 11.4 介面文字（R26.4）

- 翻譯表兩份，app 都內嵌：**Mac 的 `Resources/Localizable.xcstrings` 直接拿來用**（同樣用中文當鍵，D12），再加上 Windows 才有的字串 `src/Shiyin.App/Strings/Windows.xcstrings`（同一個鍵以 Windows 的為準）。Mac 的檔案不動（R26.5）；Mac 的 `l10n.py extract` 刪掉 Swift 沒用到的條目時，Windows 用到的鍵會變成「缺英文」，`windows-check` 會抓到，補進 Windows 的表。
- `scripts/dev/l10n.py windows-check`：抽出 `L.T("…")`、`L.F("…")` 的鍵，列出兩份表都沒有英文的條目和沒查表的介面中文（`// l10n-skip` 跳過）；`windows-apply <翻譯.json>` 寫進 Windows 的表。CI 的 macOS 那一台每次都跑。
- C# 用 `L.T("中文")`、`L.F("已匯入 %lld 個檔案", n)`：鍵的寫法照 xcstrings（`%@`、`%lld`、`%1$@`、`%%`），英文的單複數照 xcstrings 的 variations（看第一個整數），才能沿用 Mac 的英文。
- 介面語言：預設跟著 Windows 的顯示語言（中文是中文，其他英文）；Windows 不能替不打包的桌面 app 個別設定語言，所以設定的「進階」裡另外可以選，重開後生效（R26.4、D57）。DEBUG 可以用 `--set interfaceLanguage=en`。
- 依會議語言的文字（匯出、預設說話者名稱）同 M§16（`MeetingLanguage.text`），在 Core。

### 11.5 待確認就地回答、收音提醒送通知（R41、R42，M§31）

- 對應規則、「是」當成確認、全部略過、統計欄位同 Mac（M§31.1～31.4）。對應在 `AppController` 載入會議時算（`MeetingDetail.ReviewLinks`）；畫面在 `NotesTabs.cs`：`ItemList`、`ActionItemsTable`、`TopicsTab` 的每一條後面加標記（`Ui.Inline` 多放一個按鈕），點了在那一條下面展開題目。題目列從 `ReviewTab` 抽成共用的 `ReviewQuestionRow`，回答的狀態搬到 `ReviewDraft`，兩邊共用同一份草稿（資料庫的 drafts，M§21.15）。
- `AppController.SubmitReview` 把「是」跟略過一起移除（版本原因同 Mac），只有修改的題目排 `revise`。標準答案的 `db` 操作加「確認、略過」一步，`DatabaseTests` 比對移除後的紀錄和版本原因要跟 Mac 相同。
- 收音提醒（R42）：`Recorder.HintAppeared`（提示第一次出現，每場每種一次）→ 主視窗不是前景或沒開時送 Windows 通知（§12.2 的 `Notifier.LevelHint`，點了打開那場會議）。系統匣只有一個圖示（錄音中也只改提示文字），所以警示狀態是提示文字加「收音提醒」、右鍵選單多一行提示和「知道了」（R42.2 的「圖示變警示」在 Windows 是這樣對應）。提示的文字在 `LevelHints.Text`，錄音畫面、系統匣、通知共用。驗證：`ctl.sh state` 的 `lastToast`（通知被關掉時也記）、`recorder.hint`。

### 11.6 減法：三個分頁、五個動作、四頁設定、三步引導（R44，M§33）

- `DetailTab` 剩 Summary（「紀錄」）、Transcript、Ask；`NotesTabs` 的摘要和議題合成一頁；`ReviewTab` 的內容變成紀錄頂部可展開的一條（`ReviewBanner`，有待確認才顯示）；我的筆記收進逐字稿頂部的 Expander。控制通道 `tab` 接受舊名稱當別名。
- `MoreMenu` 分組同 M§33.2；復原上一版放編輯列、復原說話者的調整放改名視窗和右鍵選單（有東西可復原才顯示）。
- 引導三步，分法同 M§33.4（Windows 沒有 CLI 時「你平常用哪個 AI？」的第二段只列 API）。設定原本是四頁（M§33.3），R108 改成左邊導覽十一項（§11.41）。
- 標準答案不變；介面文字跑 `l10n.py windows-check`。

### 11.7 麥克風看得到、換得到，文字大小，匯出（R58～R60，M§38）

- 麥克風的資訊（R58）：`Services.Capture.MicrophoneDevice`（`Id`、`Name`、`IsBluetooth`、`IsBuiltIn`）。Platform 的 `Microphones.All()`（Capture、Active 的端點）、`Microphones.Default()`（Capture、Console 角色的預設，同 §6）。每次要用時才列（打開下拉選單的 `Opening`、設定頁建畫面時），不另外常駐監聽：下拉選單打開時就是當下的裝置。
- 藍牙、內建的判斷看端點的屬性（`MMDevice.Properties`）：`PKEY_Device_EnumeratorName`（`{a45c254e-df1c-4efd-8020-67d146a850e0}`,24）是 `BTHENUM`、`BTHHFENUM`、`BTHLE`、`BTHLEDEVICE`（`BTH` 開頭）算藍牙；`PKEY_AudioEndpoint_FormFactor`（`{1da5d803-d492-4edd-8c23-e0c0ffee7f0e}`,0）是 `Microphone`（4）、而且 enumerator 不是 USB、藍牙、`ROOT`（虛擬的音效驅動程式）算內建。讀不到屬性就當成都不是（不提醒，比誤報好）。虛擬機沒有音效裝置，判斷方法要在真的電腦上確認（MT6 的 `[~]`）。
- 內建麥克風：有麥克風陣列（`PKEY_AudioEndpoint_JackSubType` 是 KSNODETYPE_MICROPHONE_ARRAY、PROCESSING_MICROPHONE_ARRAY）時只有陣列算內建，耳機孔的麥克風（也是 HDAUDIO、外型可能是麥克風）不算；沒有陣列時照上面的規則（Mac 4.2.3 插著有線耳機時，「改用」按鈕指到耳機孔的麥克風，M§38.1）。要真的電腦確認。
- `WasapiSource`：麥克風的端點 ID 改成可以改的 `DeviceId`；開始（含重新開始）時記下實際開的端點 `Current`（指定的不在時是系統預設）。`IMicrophoneSource`（`IAudioSource` 加 `DeviceId`、`Current`）讓 Recorder 拿得到。
- `Recorder`（Core，用標準的單元測試測）：`MicDevice`（`Begin`、`RestartSource(0)` 之後更新；模擬錄音時照設定從 `Hooks.Microphones` 找，找不到用 `Hooks.DefaultMicrophone`）、`MicHint`（條件同 M§38.4）、`MicHintAppeared`（第一次出現，App 照 R42 送通知）、`SwitchMicrophone(id)`（寫 `inputDeviceUID`、改 `DeviceId`、`RestartSource(0)`，模擬錄音時 FileSource 從停下的位置接著送）、`DismissMicHint()`。`Hooks` 加 `Microphones`、`DefaultMicrophone`（預設值是空的，原本的測試不用改）。
- 錄音按鈕的下拉選單（R58.1）：來源、語言之間加子選單「麥克風：<目前的選擇>」，裡面是「系統預設（<名稱>）」、每一支、存的 ID 不在時的「之前選的麥克風（沒有接上，會用系統預設）」；子選單在 `MenuFlyout.Opening` 才建，接上、拔掉耳機後打開就是新的清單。錄音來源是「只錄電腦聲音」時不放。
- 錄音列（R58.2）：麥克風音量條後面加 `DropDownButton`（名稱，最寬 180、超過截斷），選單同上，選了 `SwitchMicrophone`。`FitRow` 放不下時整個藏起來（音量條還在）。系統匣的右鍵選單在錄音中多一行「麥克風：<名稱>」。
- 藍牙的提示列、通知、系統匣：同 §11.5 的收音提醒（提示列的「改用 <內建>」「知道了」、`Notifier.MicHint`、系統匣的提示文字和右鍵選單）。
- 文字大小（R59）：設定 `contentTextSize`（−1～5，同 Mac），`TextSizes.Scales` 在 Services。`Ui.ContentScale`（啟動和設定改了時設）、`Ui.C(size)` 乘上倍率；會議內容的元件（紀錄、待確認的題目、逐字稿的段落和欄寬、提問、錄音中的即時逐字稿、我的筆記、紀錄的編輯器）建的時候用 `Ui.C`，側欄、按鈕、提示列、搜尋列不用。設定改了：主視窗整個重畫、逐字稿重綁看得到的那幾段、錄音中的即時逐字稿重建。快捷鍵在主視窗的 `PreviewKeyDown` 處理（不用 `KeyboardAccelerator`：游標在輸入框時 TextBox 把 `Ctrl+-` 當成「插入選擇性連字號」吃掉，加速鍵收不到，2026-11-02 虛擬機發現；PreviewKeyDown 比輸入框先收到，處理完設 Handled，輸入框也不會多一個連字號）：`Ctrl+=`、`Ctrl+Shift+=`（加號）、數字鍵盤的 `Ctrl++` 放大，`Ctrl+-`、數字鍵盤的 `Ctrl+-` 縮小，`Ctrl+0`、數字鍵盤的 `Ctrl+0` 實際大小；Windows 沒有選單列，按了在下方提示「會議內容的文字大小：130%」。設定頁「進階」加「顯示」一組（介面語言之後）。
- 匯出（R60）：會議頁右上角「更多」左邊加 `SplitButton`（分享圖示加「匯出」，按下打開匯出對話框，箭頭是複製會議紀錄的三種格式）；`MoreMenu` 剩重新處理、移到資料夾、移到垃圾桶。條件同「更多」（不在垃圾桶、不是錄音中的那場）。
- DEBUG 控制通道（M§38.7）：`fakemic <名稱> <bluetooth 0|1> [內建的名稱]`（假的麥克風只在記憶體，當成系統預設，排在真的裝置前面；`fakemic off` 清掉）、`micswitch <id|default>`、`michint dismiss`、`textsize <−1～5>`；`state` 的 `recorder` 多 `micDevice`、`micHint`，最外層多 `textSize`。

### 11.8 會議的參考文件（R61，M§39）

- **Core**（跟 Mac 逐字相同，標準答案比對）：`Model/Records.cs` 的 `DocumentKind`、`MeetingDocument`（頁的文字用 `SwiftJsonWriter` 寫成跟 Mac 一樣的 JSON）；`Notes/DocumentPrompt.cs`（`DocumentText`、長度分配、區塊、截斷的說明、整理和提問的規則；字數用 `SwiftText.Count`、截斷用 `SwiftText.Prefix`）；`NotesPrompt.System(language, documents)`、`NotesContext.Documents`、`AskPrompt.MeetingSystem(l, documents)`、`MeetingPrompt(…, documents)`、`Validate(…, documentCitations)`。
- **讀出文字**：`Core/IO/DocumentReader.cs`：種類、拖放算不算文件、整理文字（M§39.2 的規則）、純文字的編碼、docx／pptx（`System.IO.Compression.ZipArchive` 只讀要的那幾個檔，一個檔解出來超過 64 MB 就停；`XmlReader` 不解析 DTD，規則同 M§39.2，只看本地名稱；pptx 的 Target 同 `OfficeText.resolve`）、`DocumentError`（`Code` 同 Mac，訊息用 Mac 的同一句中文查表）。
- **PDF**：PdfPig（`PdfPig` 0.1.16，Apache-2.0，純 C#，Mac 上的測試也跑得到）在 Services 的 `Documents.cs`：每頁用 `ContentOrderTextExtractor` 取文字，康熙部首（U+2F00～U+2FD5）換回一般的漢字（macOS 用蘋方輸出的 PDF，字型的對照表把「音」「一」對到部首，PdfPig 照表取出「拾⾳」，2026-11-02 虛擬機發現；Mac 的 PDFKit 會換掉）；要密碼的 PDF（`PdfDocumentEncryptedException`）是 `locked`；打不開是 `unreadable`。文字少於 20 字的頁、圖片交給 `IDocumentOcr`（Platform）；沒有實作（Mac 上的測試）時當成辨識不出文字。
- **文字辨識**（Platform 的 `WindowsOcr`）：`Windows.Media.Ocr.OcrEngine`，語言依會議語言（中文 `zh-Hant-TW`→`zh-TW`→`zh-Hans-CN`、英文 `en-US`、日文 `ja-JP`），都沒有就用 `TryCreateFromUserProfileLanguages`。PDF 的頁用 `Windows.Data.Pdf` 畫成長邊 2000 px（同 Mac），圖片用 `BitmapDecoder`（照 EXIF 方向），超過 `OcrEngine.MaxImageDimension` 先縮小。每一行把 `Words` 接起來：前後兩個字都不是中日文、不是數字接數字（或接 `/ . : , - %`）、而且兩個 word 的框之間的空隙超過字高的四分之一才加空格（Windows 的中文辨識一個字一個 word，直接用 `Line.Text` 會變成「會 議 議 程」，數字也會變成「1 2 / 1 5」）。辨識的品質比 Mac 的 Vision 差一點（「一、」的「一」常常漏掉）。這台電腦沒有可以用的辨識語言時，掃描的頁和圖片是空的，整份讀不到字時說明「「<檔名>」裡讀不到文字。這台電腦沒有<語言>的文字辨識：到「設定 → 時間與語言 → 語言與地區」加入<語言>再試。」（Windows 才有的字串）。
- **資料庫**：migration `v10` 的 SQL 從 Mac 照抄（`MigrationSql.V10`）；`AppDatabase` 建構時多 `documentsRoot`（同 Mac：null 時不碰檔案），`Documents`、`Document`、`AddDocument`、`DeleteDocument`、`DeleteMeeting` 先讀出文件的 `file` 再刪資料夾。`Paths.DocumentsDir`（`SHIYIN_HOME\documents`）；檔名照 Mac 的 `safeName`，另外把 Windows 檔名不能用的字元（`<>:"/\\|?*`、控制字元）換成 `-`（標準答案的檔名都沒有這些字元），`file` 是 `<id>/<檔名>`（資料庫裡用 `/`，組路徑時用 `Path.Combine` 拆開）。
- **Services**：`AppController.AddDocuments(paths, meetingId)`（依序讀、同一份不重複加、讀取中記在 `ReadingDocuments`、完成後 `DocumentsChanged`）、`RemoveDocument`、`DismissDocumentsChanged`、`Resummarize` 清掉提示；`MeetingDetail.Documents`、`DocumentUse`；`JobRunner` 整理時帶文件；`Asking` 單場提問帶文件；`MeetingPackage` 匯出、匯入 `documents.json`（路徑檢查同 Mac 的 `isSafeDocumentPath`；Mac 的封包裡檔名有 Windows 不能用的字元時，存檔時照上面換掉）。
- **畫面**：會議標題下資訊列的 `DropDownButton`（迴紋針圖示，文字同 Mac：「加入文件」「N 份文件」「正在讀取文件…」），選單每一份是子選單（打開、移除），最後「加入文件…」用 `FileOpenPicker`（多選，pdf、docx、pptx、txt、md、png、jpg、jpeg、heic）。打開用 `Shell` 的預設程式（`ShellExecute`，參數不經過 cmd.exe）。拖放：`RequestImport` 先挑出文件加到選取中的會議（同 M§39.4）。紀錄分頁頂部的提示列「文件有變動，會議紀錄還沒用上。」（不用、重新整理會議紀錄）。提問的出處 `Time` 是空的時候不放時間按鈕，放迴紋針圖示。使用說明照 Mac 加一段、改隱私那一條（「macOS 的文字辨識」改成「Windows 的文字辨識」，Windows 才有的字串）。
- **DEBUG 控制通道**：`adddoc <會議 id> <檔案>…`、`drop <檔案>…`、`removedoc <文件 id>`、`docread <檔案> <zh|en|ja> <輸出.json>`、`docbanner <會議 id> dismiss|resummarize`；`state` 多 `documents`、`readingDocuments`、`documentsChanged`（同 M§39.7）。
- **標準答案**：`documents/`（分配、整理文字、檔名、路徑、pptx 的 Target、`shared/golden/documents/` 測試檔讀出來的結果）、提示詞的 `*-documents`、`db/`、`package/` 的 `documents.json`。PDF、文字辨識兩邊引擎不同，不比標準答案：Windows 的測試用 Mac 上也跑得到的 PdfPig 讀測試用的 PDF，檢查每頁的文字有沒有讀到。

### 11.9 更多會議語言（R63～R65，M§41）

- 語言檔 `Resources/languages.json` 內嵌在 Shiyin.Core（`LogicalName="languages.json"`，同 opencc 的字表），`LanguageProfiles` 讀一次。`MeetingLanguage` 從 enum 改成以代碼為身分的 `readonly struct`（`default` 等於中文，原本 enum 的預設值就是中文）：`MeetingLanguage.Zh／En／Ja` 這些靜態欄位留著，`switch` 的常數比對都改成問 `Profile`。擴充方法照舊（`Code`、`Parse`、`Text`、`Name`、`UiName`、`WhisperCode`、`DefaultSpeaker`），加上 `Prompt`、`Format`、`Base`、`UsesTurbo`，意思同 Mac（M§41.1）。
- 跟 Mac 不同的地方：
  - 即時逐字稿、快速辨識是 SenseVoice（§7）：語言用 `live.windows`（粵語 `yue`、韓文 `ko`），輸出照 `live.windowsConvert` 轉（中文 `s2twp`、粵語 `s2tw`）再套 `scriptFixes`。粵語的 Qwen 還沒下載好時用快速辨識（Mac 是 macOS 內建）。
  - 匯出：HTML 的字型讀 `fonts.htmlWindows`（`ExportFonts.Mac`／`Windows` 改成從語言檔挑哪一組），Word 讀 `fonts.docx`；標題字型 `fonts.titleWindows`、內容的語言 `fonts.contentWindows`。
  - 文字辨識讀 `ocr.windows`，`windowsUserProfile` 是 true 的語言（英文）找不到時用使用者的語言設定。
  - 星期幾：中文、英文、日文照舊用寫死的表（跟 Mac 的系統格式相同）；一般語言用 `locale` 的 `CultureInfo`（韓文「목요일」，跟 Mac 一樣）。
  - 預設會議語言：英文介面而且 Windows 的語言是語言檔裡中文、英文以外的語言時用那個語言（`TwoLetterISOLanguageName` 比對代碼）。
- 標準答案：`util/languages.json`（每種語言推出來的名稱、說話者、提示詞、長度、星期、字串表、簡轉繁的修正）由 `UtilTests.LanguageProfiles` 逐項比；粵語、韓文的測試會議走跟中文、英文、日文同樣的提示詞、匯出、封包測試。
- `sync.sh` 同步到虛擬機時要帶 `Resources/languages.json`（漏了 Shiyin.Core 建置不過）。

### 11.10 心智圖（R67，M§42）

- 樹、排版、SVG 在 Shiyin.Core（`Notes/MindMap.cs`），跟 Mac 一字一句同一套規則，標準答案 `mindmap/*.json`、`*.svg` 逐字比。
- 畫面：紀錄分頁的編輯列左邊加「條列｜心智圖」切換（設定的 `notesView`）。心智圖是 `Canvas`：連線用 `Path`（`BezierSegment`），節點是 `Border`＋`TextBlock`（每行一個 `Run`、`LineBreak`，字型照會議語言的 `fonts.titleWindows`（根）、`contentWindows`（第一層用內文字型的 `Bold`：新細明體、Batang 沒有粗體，縮小時系統加粗的點陣字會糊掉；`SemiBold` 會被換成新細明體）；`Border` 的框線畫在內距裡面，第一層的內距要扣掉框線的 1.5，不然文字放不下），時間是可以按的連結（播放）。提示條、編輯列和心智圖整頁一起上下捲（同 Mac），心智圖比畫面寬時左右捲；整張比畫面寬時用 `Viewbox` 縮小（最小 0.6，乘上文字大小；不用 `ZoomFactor`：放在上下捲的頁面裡高度會照沒縮放的算）。
- 匯出：SVG 直接寫 `MindMap.Svg`；PDF 用現有的 WebView2 列印（`Pdf.Print`），HTML 裡只放那張 SVG，紙張設成跟圖一樣大（加邊）、一頁；PNG 用 `RenderTargetBitmap` 畫匯出模式的 `Canvas`（放在視窗裡一個看不到的 `Popup`，畫完拿掉），2 倍。右上角「匯出」選單的「複製心智圖（圖片）」用同一張 PNG 放進剪貼簿（`DataPackage.SetBitmap`）。

### 11.11 議題時間軸（R68，M§43）

- 段落用 Shiyin.Core 的 `MindMap.TopicRanges`（同 Mac，標準答案 `util/topic-ranges.json`）。
- 畫面：`PlayerBar` 的時間軸上面多一條議題帶（Grid 裡一段一個 `Border`＋`TextBlock`，`TextTrimming` 截短），寬度、時間比例同時間軸；正在播放的那段底色變深、字變粗（`Bold`，不用 `SemiBold`：中文會被換成新細明體的假粗體）；點了從那個議題開始播放；`ToolTip` 是「議題名稱　開始–結束」，`AutomationProperties.Name` 同樣的字。

### 11.12 讓 AI 工具讀取會議：本地 MCP（R69，M§44）

- 另外一個命令列程式 `shiyin-mcp.exe`（`src/Shiyin.Mcp`，只有入口，參考 Shiyin.Services），跟 `Shiyin.exe` 放在同一個資料夾（`pack.ps1` 發布到同一個 self-contained 的資料夾，執行環境共用、只多兩個小檔）。不像 Mac 用同一個執行檔：WinUI 的 exe 是視窗程式，啟動時有單一執行個體的轉送、Velopack 的 hook，stdio 也不是它的用法。
- 協定、工具、回傳的文字同 Mac：`Shiyin.Services` 的 `Mcp/McpTools.cs`、`Mcp/McpServer.cs`（要讀 settings.json、企業帳號，所以放 Services；標準答案 `mcp/calls.json`、`mcp/tools.json` 逐字比）。資料庫用 `AppDatabase.OpenReadOnly`（`Mode=ReadOnly`，不跑 migration；`grdb_migrations` 跟這版認得的比，同 Mac）；沒有 `-wal`、`-shm` 時（SQLite 14）改用 `Mode=ReadWrite`（不是 GRDB，不會建、刪暫時的表）。工具參數的數字看 JSON 的種類（`JsonValueKind.Number`），解析來的、程式建的都讀得到。
- 開關是 `settings.json` 的 `mcpEnabled`（每次 `tools/call` 重讀檔案）；`--set 鍵=值` 只對那次執行有效（測試用）。企業伺服器的 `mcp: false` 同 Mac：`EnterpriseInfo`、`EnterpriseAccount` 的 `Mcp`，`EnterpriseServer.McpBlocked(settings)` 直接讀存著的帳號。log 寫進 `logs\shiyin-<日期>.log`（同 app，只寫工具名稱、筆數、花多久）。
- 路徑：安裝版是 `%LOCALAPPDATA%\ShiyinApp\current\shiyin-mcp.exe`（Velopack 更新後路徑不變），不需要 Mac 的啟動檔。`SHIYIN_HOME` 是用環境變數指定的（DEBUG、示範資料）時，登記時一起帶（Claude Code `-e SHIYIN_HOME=…`，要放在名稱後面：`-e` 可以接好幾個值，放前面會把名稱也吃掉；Codex `--env SHIYIN_HOME=…`；Claude Desktop 的 `env`）。
- 加到 Claude Code、Codex（`Mcp/McpSetup.cs`）：`IToolLocator` 找到的 claude、codex（真正的 exe，或 node 加套件的 js；不會經過 cmd.exe），`ProcessRunner`（`ArgumentList`）；先 `mcp remove` 再 `mcp add`。Claude Desktop：寫法同 Mac（先備份成 `.bak`、只改 `mcpServers.shiyin`，Windows 的 `JsonObject` 保留其他鍵的順序）；設定檔的位置：MSIX 版（Store、官網的安裝檔都是 MSIX，`Claude_pzs8sxrjxfjjc`）的 AppData 被虛擬化，只讀 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json`（寫到 `%APPDATA%\Claude` 它讀不到，連它自己的「Edit Config」都開錯檔，anthropics/claude-code#26073、#29100）。偵測到 MSIX 套件（`Packages.ByFamily`；裝了還沒開過、資料夾還不在也算）或那個資料夾在就寫那裡（資料夾不在就建），都沒有才寫 `%APPDATA%\Claude\claude_desktop_config.json`。有沒有裝看 `WindowsToolLocator.HasClaudeDesktop()`。
- 設定的「AI 工具」那一頁（原本在進階，§11.41）同 Mac：開關、三個按鈕、「複製」下拉選單（英文是 Copy，用 `MeetingLanguages.Ui`；翻譯表的「複製」是 Duplicate）。DEBUG：`SHIYIN_MCP_SERVER` 指到 `dotnet build` 出來的 shiyin-mcp（DEBUG 的 app 資料夾裡沒有）、`SHIYIN_CLAUDE_DESKTOP_CONFIG` 指到別的設定檔。
- DEBUG 控制通道：`open settings <頁面>` 打開設定的那一頁（§11.41）、`openlink <網址>` 等於點了那個 shiyin: 網址。
- 會議連結：`NotificationLinks.Parse` 也認得 `shiyin://open?meeting=…&t=…`（`open` 是主機名稱、路徑是空的；通知用的 `shiyin:open?…` 照舊），`Link.Open` 多了 `Seconds`。有時間點時 `AppHost.ShowMeetingAt`：打開那場、切到逐字稿、從那裡開始（同「問所有會議」點出處）；沒有時同點通知（選到那場，分頁不變）。會議已經永久刪掉了（舊的連結、舊的通知）：打開主視窗、提示「找不到這場會議，可能已經刪掉了」（同 Mac），不選那個 id。
- 驗證：`dotnet test`（標準答案、協定、開關、唯讀、企業伺服器的 `mcp`、`McpSetup` 的參數和設定檔）；Mac 上 `shiyin-mcp.dll` 用 stdin 送協定訊息、跟 Mac 的 `Shiyin --mcp` 讀同一份資料庫逐字比；虛擬機裡 Claude Code 沒有登入時只驗設定檔寫對。

### 11.13 把一句拆成兩句（R70，M§46）

- `AppDatabase.SplitSegment`：同 Mac（UTF-16 的位置、時間的比例、快照的 `split`、校正建議移到後半句、復原時接回），標準答案 `db/35～38` 逐欄比。
- 逐字稿的右鍵選單在「這句是誰說的」上面加「拆成兩句…」；按了打開 `ContentDialog`（先收起已經開著的）：唯讀的 `TextBox`（`SelectionStart`）、預覽的前後半句、「後半句是」`ComboBox`、「拆開」「取消」。「拆開」照 R70.2 啟用。
- DEBUG 控制通道（對目前選取的會議）：`splitsegment <段落 id> <位置> <說話者|new>`、`splitmode <段落 id>`（打開對話框）、`splitcaret <位置>`（對話框的游標）、`undospeaker`。

### 11.14 中文逐字稿、會議紀錄用簡體（R71，M§47）

- `ChineseScript.ToSimplified`：同 Mac，字表內嵌同一份 `Resources/opencc/*.tsv`（多 `TSCharacters`、`TSPhrases`、`TWVariantsRevPhrases`），標準答案 `util/tw2s.json` 逐字比。
- `settings.json` 的 `chineseScript`；轉的地方同 Mac 的表（正式轉錄、即時逐字稿、整理、校正、猜說話者、匯出）。設定頁「錄音與轉錄」同一個位置。

### 11.15 簡體中文的介面（R72，M§48）

- 介面語言：`AppHost.InterfaceLanguage(setting)` 回傳 `zh`／`zh-Hans`／`en`（取代 `UsesEnglish`）；設定「進階」的選單：跟著 Windows、繁體中文、简体中文、English（選單裡的語言名稱用各自的寫法）。
- `L.Use(table, plurals, simplified)`：簡體時讀兩份 xcstrings 的 `zh-Hans`，查不到的鍵 `ChineseScript.ToSimplifiedUI`；`L.Simplified`。用語表 `Resources/l10n/zh-Hans-terms.tsv` 內嵌在 Shiyin.Core（同 opencc 的字表）。
- 字型：視窗的根元素（`Ui.WindowBackground`）、對話框（`Ui.InterfaceLanguage`）的 `Language = "zh-CN"`（簡體介面）；`Ui.ContentLanguage(language)` 對中文會議看「中文的字」。
- 執行時轉（`L.Ui`）：`MeetingLanguages.Ui`、語言名稱、內建範本的名稱和說明（設定裡的範本內容只顯示）、會議長度、心智圖和播放條的段落名稱、更新說明、存起來的失敗原因。`MeetingLanguages.SimplifiedChineseContent`（AppHost 接到設定）：中文會議用簡體時新增的預設說話者是「说话者 N」；`AppController.MindMapLayout(b, settings)` 匯出的心智圖段落名稱同匯出轉。
- `sync.sh` 帶 `Resources/l10n`。
- 驗證：`dotnet test`（`util/ui-hans.json`）；虛擬機 `--set interfaceLanguage=zh-Hans` 截圖主視窗、設定四頁、引導、對話框。

### 11.16 雙聲道的錄音兩耳都聽得到（R73，M§49）

- 混音：`Shiyin.Services.Capture.ChannelMix.Mix(ReadOnlySpan<float> interleaved, int channels, Span<float> mono)`：2 個聲道時 m = (L + R) × 0.7071、超過 ±1 截掉（同 M§49.1）；其他聲道數取平均。單元測試在 `UtilTests`。
- 播放：`Player.Load(path, mixChannels)`，`MainState` 傳 `meeting.Channels == "both"`。要混音時 `MediaPlayer.Source` 是 `MediaStreamSource`（`MixedAudioSource`）：NAudio 讀原檔（wav 用 `WaveFileReader`，其他用 `MediaFoundationReader`），PCM 16-bit 單聲道、原檔的取樣率，`SampleRequested` 每次讀 0.1 秒混好送出，`Starting` 時跳到要求的位置（`CurrentTime`）。`MediaPlayer.MediaFailed` 時改回直接播原檔（兩耳分開，至少聽得到）。
- 匯出（`MediaFoundationCompressor.Export(…, mixChannels)`）：`both` 的會議即使原檔是同一種格式也重新轉。m4a：讀原檔 → 混成單聲道（`ChannelMix.ToMono`）→ 照舊升頻到 48 kHz、AAC；wav 照舊解碼成 16 kHz 單聲道（原本原檔是 wav 時直接複製）。`.shiyin` 照舊。
- 驗證：`dotnet test`（混音）；虛擬機沒有音效裝置，`MediaPlayer` 播不出來（§15），播放要真的 Windows 電腦；匯出 m4a 在虛擬機上做、讀回來看兩邊一樣。

### 11.17 錄音中麥克風一直重新連接（R74，M§50）

- `Recorder` 的重新連接節奏同 M§50.1（`restarts`、`Delay(n)`、看門狗在重新連接完先等 5 秒、提示）。Windows 的重新連接本來就在背景（thread pool），沒有 M§50.2、§50.3 的問題。
- 驗證：`RecorderTests` 用會一直送通知、開始收音很慢的假音源（`IAudioSource`）。

### 11.18 錄不到電腦聲音時說清楚（R75.3、R75.4，M§51.2）

- `Shiyin.Platform.Windows.AudioOutputs.Playing()`：`MMDeviceEnumerator` 列出 `DataFlow.Render`、`DeviceState.Active` 的每個裝置，`AudioSessionManager.Sessions` 裡 `State == AudioSessionStateActive`、`AudioMeterInformation.MasterPeakValue > 0` 的工作階段，排除 `IsSystemSoundsSession` 和拾音自己的程序；回傳 `PlayingApp(ProcessId, Name, DeviceId, DeviceName, IsDefault)`，`IsDefault` 比對 `GetDefaultAudioEndpoint(Render, Console)`（錄音用的那一個，§6）。名稱：§12.4 的會議 app、瀏覽器用固定的名稱，其他用 `Process.MainModule.FileVersionInfo.FileDescription`，讀不到用程序名稱。COM 例外一律當成沒有。
- `Recorder.PlayingApps`（`Func<IReadOnlyList<PlayingApp>>?`，AppHost 接上）、`LevelHint.SystemElsewhere`、`LevelHint.SystemMissing`，規則同 M§51.2；文字在 `LevelHints.Text(hint, Recorder)`（要用記下來的 app、裝置名稱）。
- 錄音畫面的提示列、系統匣、通知照 §11.5。
- 驗證：`RecorderTests`（假的音源、假的 `PlayingApps`：在別的裝置、在預設裝置、沒有 app、聲音恢復）；虛擬機沒有音效裝置，`Playing()` 只能確認不丟例外、回傳空的。

### 11.19 分享畫面時藏起拾音的視窗（R77，M§53）

- `WindowCapture.Apply(Window, bool hide)`：`SetWindowDisplayAffinity(hwnd, hide ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE)`（user32，`WDA_EXCLUDEFROMCAPTURE = 0x11`）。每個 `Window` 在建立時（`AppHost` 開視窗的地方）套用；設定改了時對所有開著的視窗套用。系統匣的小視窗也是。
- 設定 `hideFromScreenSharing`（settings.json），「進階 → 顯示」同 Mac 的開關和說明。
- 驗證：虛擬機截圖（`shot.sh`，用 `Graphics.CopyFromScreen`／BitBlt 時藏起來的視窗是黑的或看不到），開、關各截一次。

### 11.20 錄一段自己的聲音（R80，M§56）

- 資料同 M§56.1（`voiceprints.audio_source = 'enroll'` 那一份的人就是「我」；每人 30 份的上限不刪它）。聲紋用 Windows 自己的說話者辨識（§9）算，跟 Mac 不共用（R28.6）。
- 錄音用 `WasapiSource`（麥克風，設定裡選的那支），規則同 M§56.2；畫面在設定「說話者與詞彙」，`ContentDialog`（同時只能一個，先收起其他的）。
- DEBUG：控制通道 `enrollvoice <音檔> <名字>`、`enrolldelete`，state 的 `selfPerson`。

### 11.21 錄音島（R81.9，M§57）

- `IslandWindow`（`Shiyin.App`）：WinUI 的 `Window`，`OverlappedPresenter` 拿掉標題列和邊框、不能縮放，`IsAlwaysOnTop`；`WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`（不出現在工作列、按了不搶焦點），`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 一律套用（不看 R77 的設定）；Windows 11 的圓角（`DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`）。
- 位置：前景視窗所在的螢幕（`DisplayArea.GetFromPoint`，用 `GetForegroundWindow` 的矩形中心），工作區上緣正中間往下 6 px；收起時是 160×32（邏輯像素）的膠囊，展開時 440 寬、高度照內容量，重新 `MoveAndResize`。
- 內容、出現的時機、展開 10 秒後收回、偵測會議的提醒、島打開時錄音中的提醒不送通知，規則同 M§57.2；資料從 `Recorder`（`Hint`、`MicHint`、`IdlePrompt`、`DeviceWarning`）和 `MeetingDetector` 的提醒來。偵測會議原本的 `MeetingPromptWindow` 只在島關掉時用。
- 滑過去展開：`PointerEntered`／`PointerExited`（移出 0.3 秒後收回）。滑過去的畫面在提醒那一句下面也放提醒的按鈕（「知道了」「繼續錄」…）：展開的提醒一滑過去就換成控制的畫面，不放的話提醒的按鈕永遠按不到（M§57.2 的 Mac 也是這樣，要 Mac 一起改）。即時逐字稿那一句只改文字、不重建（重建時按到一半的按鈕會按不到）。
- `OverlappedPresenter.SetBorderAndTitleBar(false, false)` 之後視窗還留著 `WS_DLGFRAME`（四周 3 px 的框，client 比視窗小 6 px）：第一次出現前改成 `WS_POPUP`、`SWP_FRAMECHANGED`；`DWMWA_BORDER_COLOR` 設成不畫。
- 「打開拾音」：打開錄音中的會議後 `SetForegroundWindow(主視窗)`（島按了不搶焦點，只靠 `Activate` 時主視窗不會到前面；剛收到點擊的是拾音，系統允許）。
- 偵測會議的提醒左上角是青瓷綠的麥克風（Mac 是 waveform）。
- 設定 `recordingIsland`（settings.json，預設 true），在「錄音與轉錄 → 提醒與快捷鍵」，說明同 Mac 但不提瀏海。
- DEBUG：控制通道 `island hover on|off`、`meetingaction <later|mute|start|continue|stop>`（按島上偵測會議的按鈕，同 Mac）、state 的 `island`（`enabled`、`visible`、`mode`、`frame`（實際像素）、`hintKey`、`prompt`、`actions`）；`--set debugIslandCapture=YES` 讓截圖拍得到（同 Mac）。
- 虛擬機驗證：`shot.sh`（UTM 拍虛擬機的畫面輸出）一定拍得到島，要看拍不拍得到用 insession 裡的 `CopyFromScreen`。滑過去要用 `mouse_event` 的相對移動（真的輸入），`SetCursorPos` 不會觸發 WinUI 的 `PointerEntered`。虛擬機沒有顯示卡加速，DWM 的圓角看不到（主視窗也是直角）。

### 11.22 人有臉、會議頁上方、人物頁（R83～R85，M§58～§60）

- 頭像：`Ui.Avatar(name, color, size, isMe)`（`Border` 圓角＝直徑的一半、釉色 18% 的底、粗體的字；取字的規則、顏色同 M§58，在 Core 的 `Avatars`：`Initials`、`PersonIndex`（同樣的 FNV-1a）、`Palette`，`Ui.Speaker`、`Ui.Person` 都用它）。尺寸照 Mac 的程式：逐字稿、待辦 18（跟著文字大小 `Ui.C`）、會議頁上方 20（M§58.2 寫 22，Mac 的程式是 20）、側欄 26、人物頁 56。用在會議頁上方、逐字稿、待辦的負責人、人物頁、側欄。標準答案 `util/avatars.json`（Mac 的 `PersonAvatar.initials`、`Color.person`、`Color.speaker` 在淺色、深色外觀下解出的 sRGB）。
- 會議頁上方：同 M§59，`MeetingSettingsMenu` 是一個 `DropDownButton`＋`MenuFlyout`（三段用 `MenuFlyoutSeparator` 和停用的標題項目隔開）。「會議設定」和文件選單沒有框（2026-11-15 照 Mac 改）：平台的 `DropDownButton` 加輕量的樣式覆寫 `Ui.Borderless`（不換 ControlTemplate，範本讀的 `Button*` 主題資源換成 subtle 那一組：平常透明、滑過去 `SubtleFillColorSecondaryBrush`，文字 `AccentTextFillColorPrimaryBrush`）。第二列是 `LeadingTrailing`（`Ui.cs`，同 Mac 的 `ViewThatFits`）：放得下時選單靠右同一列，放不下時換到下一行。資訊列本身是 `FlowPanel`（同一行裡上下置中）：資料夾、日期、長度、與會者整串太長時換行（跟 Mac 不同：Mac 的 HStack 把字縮成省略號，Windows 的橫排 `StackPanel` 會直接切掉最後一位；2026-11-16 英文巡檢，當時的定期會議＋四位英文名字和「自動」在一般的視窗寬度就超出）。
- 側欄（2026-11-15 照 M§60.2 改，拿掉 `SelectorBar`）：搜尋框下面一個平台的 `ListView`（`scopes`，同 Outlook、檔案總管的資料夾窗格；不自訂 ControlTemplate）：「所有會議（數量）」「人物（數量）」，有資料夾時一個不能選的「資料夾」標題項目、每個資料夾（數量、右鍵選單）。選到的那一列是平台的選取樣子（強調色＝青瓷綠）。下面一行是下面那一段的標題（所有會議、資料夾名稱或人物），再下面是會議或人物的 `ListView`。點所有會議、資料夾：`MainState.ShowMeetings(folder)`（回到會議模式）；點人物：`SetSidebar(People)`。垃圾桶裡、搜尋中整組不顯示。名字、數量沒變時只換選取、不重建。微軟的對照表裡 2～3 種模式是 `SelectorBar`，但這裡的三種項目（所有會議、人物、資料夾）是同一層的導覽目的地、數量不固定，照資料夾窗格的做法比較對。
- 人物頁：資料用 `AppDatabase.PeopleOverview`、`PersonMeetings`、`PersonActionItems`（同 M§60.1 的 SQL，標準答案 `db/43～48`；同一天的人照名字排用介面語言的排序，兩邊的語系不一定一樣，標準答案避開同一天）；右邊的 `PersonPage` 同 M§60.2（沒有「加到提醒事項」）。上面是不跟著捲動的標題列（同會議頁：左邊頭像 52、名字、摘要，右上角「更多」）；「我」只留細的外圈（最多 2，`AccentFillColorDefaultBrush`）和名字旁的灰字「我」。「更多」是 `DropDownButton`＋`MenuFlyout`（同會議頁，無障礙名稱「更多」）：「改名…」（`Flyout` 接在「更多」下面，名字選好）、「合併到」（子選單）、分隔線、「刪除聲音特徵…」（`ContentDialog`：主要按鈕「刪除」、預設按鈕是取消、標題和內文都寫出是哪個人）。內容跟標題列同樣的左右邊距、整欄寬，負責的待辦那張表（事項那一欄撐滿）右邊跟「更多」對齊（Windows 沒有「加到提醒事項」，對齊的是「更多」）。主視窗每次資料改了都重畫，人物頁內容一樣時留著原本的元件（轉錄的進度一直更新時捲動位置才不會跳回最上面）。「會議」那一段的英文用 Meetings（「會議」這個鍵的英文是單數的 Meeting）。視窗標題照舊是「拾音」（會議頁也是），人名在頁面的標題列。設定「說話者與詞彙」的認識的人下面加一行「也可以在側欄的「人物」管理」（R85.5）。
- 名字旁的鉛筆（R85.6、M§60.2 最後一條，2026-11-16）：名字右邊一顆平台的 `Button`＋`Ui.Borderless`（平常透明、滑過去才有底色，字和圖示是強調色的文字＝青瓷綠，同 Mac 的 borderless、celadon），內容是 `SymbolIcon(Symbol.Edit)`；`ToolTip` 和無障礙名稱都是「改名」。按了打開同一個改名的 `Flyout`（「更多 → 改名…」那一個），`ShowAt` 給位置：鉛筆是 `BottomEdgeAlignedLeft`（接在鉛筆下面），「更多」照舊 `BottomEdgeAlignedRight`；取消、儲存、Enter、改成已經有的名字時合併過去，都同「更多 → 改名…」。
- DEBUG：控制通道 `sidebar people|meetings`、`person <id>`，state 的 `sidebarMode`、`selectedPerson`、`peopleOverview`（同 Mac）。
- 移除一個人（R85.7、M§60.2，2026-11-16）：人物頁「更多」和側欄人物列的右鍵選單（`MenuFlyout`，打開時才建）用同一份 `PersonPage.MenuItems`：「改名…」（`SelectPerson` 後 `RequestRename`：那個人的頁面畫好、鉛筆 `Loaded` 之後打開鉛筆下面的改名 `Flyout`；原本「更多 → 改名…」接在「更多」下面，跟著 Mac 改成都打開鉛筆的那一個）、「合併到 ▸」、分隔線、「移除這個人…」（`Symbol.Delete`）。「更多」的 `ToolTip` 改成「改名、跟另一個名字合併（同一個人被記成兩個名字時）、移除這個人」。人物列表選到一個人時按 Delete 也是移除（`list.KeyDown`）：選的人換了只換列表的選取、不重建（原本 `ListSignature` 含「選了誰」，換人時整個重建，焦點離開列表，Delete 收不到；2026-11-16 虛擬機）。確認在主視窗（`MainWindow.ConfirmRemovePerson`，`ContentDialog`，同時只開一個）：標題「移除「X」？」、說明「會從「人物」拿掉，也會刪掉他的聲音特徵。會議裡已經帶入的名字會留著，之後的會議不會再自動認出他。」、主要按鈕「移除」、預設按鈕是取消；`DeletePerson`（同原本的刪除聲音特徵：`speakers.person_id` 變 null，會議裡的名字留著），移除的是正在看的人時回到沒選人。設定頁「認識的人」照舊叫刪除聲音特徵。四個新的鍵 Mac 的翻譯表（9c713dc）已經有，這個分支從那之前開的，英文照 Mac 的寫進 `Windows.xcstrings`（合併後兩邊相同）。DEBUG：`personremove <id>`（打開確認）、`personrenameui <id>`（同 Mac）；state 加 `confirmRemovePerson`（同 Mac）、`personRenameOpen`（改名的輸入框開著沒；Mac 沒有）。

### 11.23 會議多選、清空垃圾桶、刪除一句、分割會議（R88～R90，M§62～§64）

- **多選**（M§62）：會議列表（所有會議、資料夾、垃圾桶）的 `ListView` 用 `SelectionMode = Extended`（Ctrl 點選加入或取消、Shift 點選一次選一段、Shift＋方向鍵、Ctrl＋A，同檔案總管、Outlook）；搜尋結果、人物列表照舊 `Single`。
  - `MainState.Selection`（目前這場：會議頁、播放）和多選 `MultiSelection` 並存，規則同 M§62：列表選了兩場以上時存進多選、`Selection` 不變；只剩一場時清掉多選、選那一場；程式選了不在多選裡的會議（還原、錄音停止後跳到那場、點通知）時清掉多選；換資料夾、進出垃圾桶、搜尋時清掉；重讀列表時拿掉已經不在列表裡的（移到垃圾桶、永久刪除之後），剩不到兩場就清掉。
  - 選了兩場以上：右邊是「已選取 N 場會議」（排法同沒選會議時的置中畫面：多選的圖示、標題、說明「按住 Ctrl 點選可以加入或取消，按住 Shift 點選一次選一段。」（Windows 才有的字串）、按鈕）：`DropDownButton`「移到資料夾」（`MenuFlyout`，項目同右鍵的「移到資料夾」）、「移到垃圾桶…」；垃圾桶裡是「還原」「永久刪除…」。「移到資料夾」的項目同 Mac 的 `FolderMenu`：都在同一個資料夾時不列那個資料夾、最後是「移出「名稱」」；在不同資料夾時列全部、最後是「移出資料夾」。
  - 右鍵選單：每一列的 `MenuFlyout` 在 `Opening` 時才建：在多選裡的列是全部（「把 N 場會議移到垃圾桶…」要確認），不在多選裡只有那一列（同 M§62；WinUI 的右鍵不會改選取，同 SwiftUI 的 `contextMenu(forSelectionType:)`）。
  - 鍵盤：Ctrl+Delete（整個視窗，§11.1）對多選的全部，確認寫場數；會議列表有焦點時按 Delete 也一樣，在垃圾桶裡是「永久刪除…」（同檔案總管的資源回收筒；Mac 的 ⌘⌫ 在垃圾桶裡沒有作用）。
  - 確認對話框（`ContentDialog`，同時只能開一個，`Dialogs.Show` 排隊）寫場數：「把 N 場會議移到垃圾桶？」（可以還原，主要按鈕是預設）；「永久刪除 N 場會議？」不能還原，預設按鈕改成「取消」（WinUI 的準則；原本一場的也一起改）。
  - `AppController.MoveToFolder`、`MoveToTrash`、`Restore`、`DeletePermanently` 收多場（訊息同 M§62），一場的照舊；正在錄音的那場跳過、說明原因（R88.2）。`Restore` 多場時發 `RevealMany`：回到會議列表、選取那幾場。永久刪除前發 `ReleasingAudio`（主視窗正在播的那場先停止，放掉檔案）。
- **清空垃圾桶**（R88.3）：垃圾桶裡，列表上方「垃圾桶裡的會議 30 天後會永久刪除」那一行右邊加「清空垃圾桶…」（`Button`，垃圾桶是空的時停用）。這一行放在 `ListView` 外面（原本是列表裡停用的項目：停用的 `ListViewItem` 裡的按鈕按不了）。`ContentDialog`：標題「永久刪除垃圾桶裡的 N 場會議？」、內文「會議紀錄、逐字稿和錄音都會刪掉，沒辦法還原。」、主要按鈕「清空垃圾桶」、預設按鈕是「取消」。`AppController.EmptyTrash()`。
- **刪除一句**（M§63、D171）：`AppDatabase.DeleteSegment`、`UndoSpeakerEdit` 同 Mac。快照多記 `deleted`，`segment`、`corrections` 的鍵照 Mac 的 Codable（段落：`id`、`meetingId`、`seq`、`start`、`end`、`text`、`lowConfidence`、`speaker`（沒有就不寫）、`kind`、`channel`（沒有就不寫）、`speakerManual`；校正建議：`id`、`meetingId`、`segmentId`、`original`、`suggestion`、`reason`、`status`、`createdAt`），同一份資料庫在 Mac、Windows 打開都能復原對方刪的句子。復原時校正建議的 id 已經有人用（`AUTOINCREMENT`，正常不會發生）就用新的 id，不讓整個復原失敗。
  - 逐字稿的右鍵選單：說話者那一組（拆成兩句…、這句是誰說的、這段連續的話…）後面加「從這裡分成兩場…」（第一句沒有）、「刪除這句」（`Symbol.Delete`）、「復原：…」（`Symbol.Undo`，從「有說話者才顯示」的那一組移出來，沒辨識說話者的會議也有）。不用確認（D171）。
  - `AppController.DeleteSegment`：擋下來的條件同 `SpeakerEditProblem`（垃圾桶裡、正在處理），訊息「正在處理這場會議，完成後才能修改逐字稿」；復原的說明「刪除一句「前 12 個字…」」（字數用 `SwiftText`，同 Mac 的字形叢集）。
- **分割會議**（M§64、D180）：
  - 確認：`Dialogs.Confirm`，標題「從 mm:ss 這句開始分成另一場會議？」、內文同 Mac（最後一句是「30 天內可以從「更多」還原。」）、主要按鈕「分成兩場」。
  - `AppController.SplitMeeting` 走編輯會議的套用（§11.40、M§81.6）：片段是整場（會議長度、錄音長度、最後一句的結尾取大的）、切點是那一句的開始（四捨五入到 0.01 秒），所以一樣能還原。錄音、資料庫、換檔、擋下來的條件、排重新整理、通知都同 §11.40；原本的 `IAudioSplitter`、`MediaFoundationSplitter`、`SplittingMeeting` 拿掉（`AppDatabase.SplitMeeting` 改成交易內也能呼叫，編輯的套用接著用）。
- **DEBUG 控制通道**（同 Mac 的名稱；會議 id 都寫在參數裡，不看目前選的）：`selectmany <id,id,…>`（等於 Ctrl 點選）、`trashmany`、`restoremany`、`purgemany <id,id,…>`（不經過確認）、`showtrash 0|1`、`confirmemptytrash`（打開確認對話框）、`emptytrash`（不經過確認）、`deletesegment <會議 id> <段落 id>`、`splitmeeting <會議 id> <段落 id>`（不經過確認）、`splitconfirm <會議 id> <段落 id>`（打開確認對話框，等於右鍵「從這裡分成兩場…」）。`state` 加 `multiSelected`、`showTrash`、`trashCount`、`confirmDelete`、`deleteTargets`、`confirmPurge`、`confirmEmptyTrash`、`splitMeetingRequest`、`splittingMeeting`（對話框開著時是要處理的那幾場、那一句；`splittingMeeting` 現在是正在套用編輯的那場，同 Mac）。
- 虛擬機驗證：`uia.ps1` 加 `rclick`（真的滑鼠右鍵點那個元件，打開 `ContextFlyout`，可以接著點選單的選項）、`lclick`（真的左鍵點，`DropDownButton` 用 UIA 的 Invoke 打不開選單時）、`ctrlclick`（按住 Ctrl 點，多選）、`key`（按一個鍵，例如 Delete）；虛擬機裡的輸入不會送到 Mac。要點：送鍵盤之前用 `AttachThreadInput`＋`SetForegroundWindow` 把拾音拉到前面（排程工作開的 PowerShell 會搶到前景，Ctrl 送不到拾音）；打開選單和點選項要在同一個腳本裡（下一個 PowerShell 開起來會搶焦點，選單被關掉）；in-session 的腳本丟出錯誤時只看得到 TIMEOUT。

### 11.24 心智圖放大縮小（R91，M§65）

- 倍數同 Mac：`(zoom ?? fit) × 文字大小`。`fit` 照舊（§11.10：比畫面寬時縮到放得下，最小 0.6、最大 1，在左右捲的 `ScrollViewer` 的 `SizeChanged` 算）；`zoom` 是使用者選的倍數（null 是預設），放在 `NotesTabs` 的欄位：主視窗每次資料改了都整頁重畫，放在元件上會被清掉。縮放時只改 `Viewbox` 的 `Width`、`Height`、百分比的字和按鈕的停用，不整頁重畫（同 §11.10，不用 `ZoomFactor`）。
- 心智圖上方靠右一排三個按鈕（Mac 是 borderless）：平台的 `Button` 加 `Ui.Borderless` 的輕量樣式覆寫（平常透明、滑過去 `SubtleFillColorSecondaryBrush`，不換 ControlTemplate），字和圖示用次要文字的顏色（同 Mac 的 `.secondary`；「會議設定」那種強調色的字是選單，這裡不是）：「縮小」（Segoe Fluent Icons 的 ZoomOut `E71F`）、目前的百分比（最小寬 44；按了回到預設，`ToolTip`「回到符合寬度」）、「放大」（ZoomIn `E8A3`）。一次 ±25%，對齊到 25% 的格子（預設的倍數不在格子上時，第一下就對齊），範圍 25%～300%，到頭時那一顆停用。百分比是倍數本身，不乘文字大小（同 Mac）。無障礙名稱：「縮小」「符合寬度」「放大」（同 Mac），按鈕的 `ToolTip` 是「縮小」「回到符合寬度」「放大」。
- Ctrl＋滾輪（Mac 是觸控板兩指開合，R91.2）：接在整個心智圖頁（上下捲的 `ScrollViewer`）上，不只心智圖本身（跟 Mac 不同：縮到 25% 時圖只有一小塊，2026-11-16 在虛擬機上只接在圖上時，滑鼠很難剛好在圖上）。用 `AddHandler(PointerWheelChangedEvent, …, handledEventsToo: true)`（滾輪事件可能已經被 `ScrollViewer` 標成處理過），按著 Ctrl 時累積 `MouseWheelDelta`，每 120（滑鼠滾輪的一格）±25%（同按鈕，對齊格子），`Handled = true`（整頁不會跟著上下捲）。精確式觸控板的捏合送給 app 的也是 Ctrl＋滾輪（一次的量比較小，累積到 120 才動一格）。
- 上下捲（R91.3：比畫面高時跟著整頁上下捲）：左右捲的 `ScrollViewer` 不能上下捲時，會把一般的滾輪拿去左右捲，放大後圖比畫面寬、滑鼠在圖上就捲不動整頁（2026-11-16 虛擬機）。所以 `Viewbox` 先接住滾輪：一般的滾輪標成處理過、自己捲整頁（`ChangeView`，一格 100，同瀏覽器）；Shift＋滾輪、橫向的滾輪（`IsHorizontalMouseWheel`）照舊交給左右捲；Ctrl 的標成處理過（左右捲的不要動），縮放照樣收得到（handledEventsToo）。觸控板的兩指捲動走 DirectManipulation，不經過這裡（上下的會串到整頁）。
- 回到預設（R91.3）：換一場會議、切到逐字稿或提問（`DetailView.Show`）、切回條列、按「編輯」（`NotesTabs.Page`）。匯出、複製的 PNG、SVG、PDF 用 `MindMapView.Build`，不經過縮放（同 Mac）。
- DEBUG：`state` 加 `mindMapZoom`（使用者選的倍數，null 是預設）、`mindMapScale`（畫面上用的倍數，不含文字大小）。`ps/wheel.ps1` 加第 6 個參數 `ctrl`（按住 Ctrl 捲）。

### 11.25 依人物整理的重點（R92，M§66）

2026-11-16 第一版（febacfe）把「每個人的重點」放在條列的摘要下面，匯出、心智圖、MCP 的說明也加上這一段；使用者看了 Mac 的第一版後說明要的是紀錄分頁的切換多一類「人物」、原本的摘要不用動，跟著 Mac 08758c9 改成下面這樣。

- Core 同 Mac：`MeetingNotes.People`（`Person(Name, Points)`）；解碼同 Mac 的寬鬆規則（陣列裡有不是物件的，整個 `people` 當作沒有；`name`、`points` 型別不對當作空的）；`People` 是空的時 `ToJson` 不寫（同 Mac 的 `encode`：以前的紀錄存起來、放進提示詞一字不差）。`MappingText`（改說話者名字、合併、全文取代、簡體）、`PlainText`（搜尋，MCP 的搜尋節錄也找得到）、`IsEmpty`、`JsonSchema`、`TemplateDefinition.BuiltinSectionIds`（`summary` 後面）、`SectionLabel`、內建範本「一般會議」（中英日）、`NotesPrompt`（中英日的規則、一般語言的 Output language、校稿說明）、`ReviewLinks`（`NoteSection.Person`，排序值 6）照 Mac 加上。`NotesExporter`（Markdown、純文字、HTML、Word）、`MindMap.Tree`、MCP 工具的說明跟 R92 之前一樣（R92.1）。標準答案（Mac 重新產生的）逐字比。
- 切換：編輯列左邊的 `SelectorBar` 多一個「人物」（`notesView = "people"`；`ToolTip`「把會議紀錄切換成條列、心智圖或依人物整理」）。條列、心智圖不變。切到人物時心智圖的縮放回到預設（同切回條列，§11.24）。
- 人物（`NotesTabs.PeopleNotes`，同 Mac 的 `PeopleNotes`）：上面照舊是提示條、編輯列，下面每位說話者一張（張和張間距 30，上面多 10）：
  - 一列 `Ui.Split`：左邊頭像（`Ui.Avatar` 30），右邊名字（`Ui.Heading` 18）和「說了 N 分鐘（P%）」（12、次要文字；`TimeFormat.Duration(秒, 介面語言)`，簡體介面經過 `L.Ui`），都跟著文字大小（`Ui.C`）。頭像的顏色：名字對到這場的說話者（`MeetingDetail.SpeakerIdx`：不分大小寫、去掉頭尾空白，同 Mac）時用說話者的顏色，是「我」時有外圈；對不到時照名字算（`Ui.Person`，同人物頁）。
  - 下面是重點（同其他段落的 `Items`：時間點按了播放、待確認的標記 `NoteItemRef.Person(人, 條)`、就地展開的卡片）。
  - 有負責的待辦時：「負責的待辦」（13、次要文字，上面多 4）＋`Items`：「事項（期限 日期）」（`L.F("%@（期限 %@）")`，沒有期限只寫事項）、時間點；待確認的標記連到 `NoteItemRef.ActionItems(原本的位置)`。
  - 計算放在 Services 的 `MeetingDetail.PeopleCards`（單元測試測得到）：有重點的人才有一張，順序同紀錄；`SpokenSeconds` 是正式逐字稿（`kind = final`）每位說話者 `end − start` 的總和，秒數和百分比（佔全部說話者）照 Swift 的 `rounded()`（`SwiftText.Round`）；對不到說話者、全部都是 0 時不顯示那一行；`Owns`：負責人包含去掉頭尾空白的名字（不分大小寫，同人物頁的 `PersonActionItems`，R85.3）。
  - 沒有 people 時：說話者不到兩位寫「這場會議只有一位說話者（或沒有辨識說話者），沒有依人物整理的重點。」；兩位以上寫「這場的會議紀錄還沒有每個人的重點。」和按鈕「產生每個人的重點」（平台的 `Button`，內容是 `Symbol.People`＋字，同 Mac 的 person.2；無障礙名稱同字；`ToolTip`「用整理工具依說話者整理每個人的重點；摘要、決議、待辦這些段落不會變」；這場正在處理時停用）。說明的字跟著文字大小，按鈕不跟（同其他按鈕，R59.2；Mac 的整段用 `contentFont`）。
- 只補上（R92.4）：`AppController.GeneratePeopleNotes`：擋下來的條件和訊息同「重新整理」（`ReadyForAction`、整理工具是「不整理」、整理工具還沒準備好）；排 `Summarize`，payload `JobRunner.Encode(new SummarizePayload(People: true))` = `{"draft":false,"people":true}`。`people` 是 false 時不寫：草稿的 `{"draft":true}`、自動排的 `{"draft":false}` 跟以前一字不差。`JobRunner.Summarize(peopleOnly)`：沒有紀錄時不做；範本的說明、額外指示照舊，段落只有 `people`、沒有自訂段落；簡體照舊整理完才轉；結果讀現在的紀錄（整理期間被改過也不會蓋掉）只換掉 `People`，`SaveNotes(by: null, 「補上每個人的重點」, NotesSource.Edit)`（整理工具、標題不變，舊的那一版可以「復原上一版」），不發通知。
- 編輯器（`NotesEditor`）：打開時紀錄有 people 才顯示「每個人的重點」（`showsPeople`，刪光了也留著，可以再新增），放在自訂段落後面（同 Mac；原本的編輯器不變）：同議題的編輯器，每個人一列名字欄（`TextBox`，標題字型、14，提示文字「名字」）＋右邊「刪除這個人的重點」（`Ui.IconButton(Symbol.Delete)`），下面是重點的 `ItemsEditor`；最後「新增一個人」（`Ui.Link`，同「新增議題」）。名字欄的字級照議題標題欄的 14（Mac 是 16）。
- DEBUG：`notesview list|mindmap|people`；`peoplenotes <會議 id>`（等於按「產生每個人的重點」，同 Mac）；`state` 加 `peopleCards`（選取中的會議每一張的 `index`、`name`、`speaker`、`seconds`、`percent`、`points`（幾條）、`owned`（負責的待辦原本的位置）；Mac 沒有）。`reviewexpand`、`reviewsubmit` 的段落 `person <第幾個人> <第幾條>`（同 Mac）；`editnotes <會議 id> <會議紀錄 JSON 或 JSON 檔的路徑>`（同 Mac 的 `editnotes`，存成新的一版，同按「完成」；Windows 從 SSH 傳中文的 JSON 不方便，所以也收檔案路徑）。

### 11.26 企業伺服器連不上時自動改在這台電腦轉錄（R93，M§67）

- `ServerTranscriber.Transcribe` 記「連續連不上」的開始時間（同 M§67）：`EnterpriseFailure.Unreachable`（連線被拒、逾時）、`Server` 的 `server_error`（5xx）記下第一次的時間；查狀態成功（`Status`，建工作、上傳成功後的下一圈都會查）就清掉；伺服器忙（`busy`）、登入失效（`IsSetupProblem`）也清掉（照舊等）。連續 `AutoLocalAfter`（預設 `DefaultAutoLocalAfter`，10 分鐘）就刪掉伺服器上的工作、清掉 payload 的 `remoteId`，丟 `SwitchToLocalException(automatic: true)`。快到時 `WaitForServer` 只等到那時候（無條件進位到秒、至少 1 秒，同 Mac）。
- `JobRunner.Transcribe` 照「改在這台電腦轉錄」的路（R50.5）改本機；`Automatic` 時發 `ServerFallback(meetingId)`，`AppHost` 交給 `Notifier.ServerFallback`：標題「企業伺服器連不上，已改在這台電腦轉錄」、內文是會議標題，點了打開那場（`NotificationLinks.OpenMeeting`）。轉完照常有「轉錄完成」。
- 等多久：`JobRunner.ServerAutoLocalAfter`（init）；`AppHost` 在 DEBUG 讀 `--set serverAutoLocalSeconds=20`（同 Mac 的 `-serverAutoLocalSeconds`），正式版固定 10 分鐘（R93 不做「等多久可以改」）。
- DEBUG 模擬「登入了但連不上」：`--set debugEnterpriseUnreachable=YES`（`EnterpriseServer.DebugUnreachable`，同 Mac 的 `-debugEnterpriseUnreachable`）：`Account` 是假的帳號（`debug@example.invalid`）、`IsSignedIn`、`SavedUrl` 是 `http://127.0.0.1:9`、`AccessToken` 回 `debug`；只在記憶體，不寫 settings.json、不讀認證管理員。轉錄要交給伺服器再加 `--set transcriptionEngine=server`（`--set` 都只在這次執行有效）。
- 測試：`EnterpriseTests.UnreachableTooLongSwitchesToThisComputerAutomatically`（500 `server_error`、連線被拒都會自動改，`Automatic`；使用者按的不是）、`BusyServerKeepsWaiting`（伺服器忙不算，好了照樣在伺服器轉完）、`JobRunnerReportsAutomaticFallback`。

### 11.27 詞彙表：一次加好幾個、搜尋、批次刪除（R94，M§68）

- Core 的 `GlossaryInput.Split`（同 Mac，標準答案 `util/glossary-split.json`）：換行（`\n`、`\r`；WinUI 的 `TextBox` 把換行存成 `\r`）、逗號（半形、全形）、頓號、分號（半形、全形）分開，去頭尾空白、拿掉空的和重複的（小寫後比，留第一個）。
- `AppDatabase.AddTerms`（`INSERT OR IGNORE`，回傳實際加了幾個；`term` 是 UNIQUE，一字不差的才算已經有，同 Mac）、`DeleteTerms`。`AppController.AddTerms(text)`：說明同 Mac（一個詞而且已經有時「「X」已經在詞彙表裡了」；好幾個時「已加入 N 個詞彙」或「已加入 N 個詞彙，M 個已經有了」；一個新詞不說），走 `Notice`（主視窗下方的提示），也回傳同一句。
- 設定「說話者與詞彙」（`SettingsWindow.Glossary`）：
  - 新增：`TextBox`（`Header`「新增詞彙」、提示字「產品名、人名；可以一次貼上好幾個」、`AcceptsReturn`、自動換行、最高 140（約 4 行，再多就捲動））＋「新增」（空的時候停用，對齊輸入框底部）。`AcceptsReturn` 是為了貼上好幾行（單行的 `TextBox` 貼上時只留第一行）；Enter 送出、Shift+Enter 換行（`PreviewKeyDown`，同 Mac 的 Return 送出）；按 Enter 加完焦點回到輸入框，可以接著打。跟 Mac 不同：加完的說明也寫在輸入框下面（12、次要文字，顯示一次）：設定視窗常蓋在主視窗上，看不到主視窗的提示。
  - 超過 10 個詞時「搜尋詞彙」（`TextBox`，`Header`「搜尋詞彙」、提示字「輸入要找的字」；`Contains(…, OrdinalIgnoreCase)`，同 Mac 的 caseInsensitive）。打字只重畫列表那一塊（整頁重畫時焦點會跑掉）；詞彙減到 10 個以下時清掉搜尋字（Mac 會留著看不到的搜尋字，列表還是篩過的）。
  - 列表上面一列：左邊 `HyperlinkButton`「全選」（搜尋時「全選搜到的 N 個」；列出來的都勾了變「取消勾選」），右邊「刪除勾選的 N 個…」（平台的 `Button`，有勾才出現）。每個詞一列：`CheckBox`（內容是那個詞，同 Mac 的 checkbox 樣式的 Toggle）＋右邊刪除這一個（`Ui.IconButton(Symbol.Delete)`，照舊）。勾選只換上面那一列的字，勾選框不重建（焦點不會跑掉）；勾了之後被單獨刪掉的詞不算在 N 裡（同 Mac）。搜不到時「沒有含「X」的詞彙」。勾選、搜尋字在整頁重畫（新增、刪除一個）時留著，存在 `SettingsWindow` 的欄位。
  - 確認：`ContentDialog`（設定視窗的 `XamlRoot`），標題「刪除勾選的 N 個詞彙？」（Mac 的確認也只有標題）、主要按鈕「刪除」、預設按鈕是「取消」（沒辦法還原，WinUI 準則，同 §11.23）；開著時不會再開第二個（`confirmingTerms`）。刪完清掉勾選。
- DEBUG：控制通道 `addterm <文字>`（同 Mac：`\n` 當換行，走 `AddTerms`，回傳 `message`）；state 加 `glossary`（自己的詞，照加入的順序）。

### 11.28 會議結束時倒數自動停止錄音（R95，M§69）

- 設定 `autoStopOnMeetingEnd`（settings.json，預設 true）：「錄音與轉錄 → 提醒與快捷鍵」的 `ToggleSwitch`「會議結束時自動停止錄音」，放在「偵測到線上會議時提醒錄音」下面，偵測會議關掉時停用（`IsEnabled`，切偵測的開關時跟著變）。偵測會議的說明換成 Mac 的新句子，從「很久沒人說話」那一組後面搬到這兩個開關下面（「不提醒：X」接在說明後面）。
- `MeetingEndCountdown`（Services，同 Mac 的 `startMeetingEndCountdown`；`AppHost.MeetingEnd`）：`Start()` 記錄音中的那場和期限（`Seconds`，預設 60），`System.Threading.Timer` 每秒 `Tick`：錄音停了或換了一場就收起（`Ended`）；到期限時收起，還在錄同一場、`MeetingDetector.MeetingAppActive`（有會議 app 在用麥克風，同 Mac 的 `meetingAppActive`）是 false 就停止。停止的動作 `AppHost` 給：先拿會議標題、`Recorder.Stop()`（同自動停止的錄音，從計時器的執行緒呼叫）、`Notifier.MeetingEndStopped`（「會議結束，已自動停止錄音」，內文是會議標題，點了打開那場）。`Cancel()` 給「繼續錄」「停止並轉錄」。`Remaining(now)` 無條件進位（同 Mac 的 `TimelineView`）。單元測試 `ServicesTests.MeetingEndCountdownStopsOnlyTheSameRecording` 直接呼叫 `Tick`。
- `App.ShowMeetingPrompt`：`Stop` 的提醒、設定開著時先 `MeetingEnd.Start()`，再交給島、通知或小視窗（同 §11.21 的分流）：
  - 錄音島：`PromptState` 多 `Deadline`；訊息跟著島的 0.5 秒計時器換成「會議好像結束了，N 秒後自動停止錄音」，只改那個 `TextBlock` 的字、不重建（重建時按到一半的「繼續錄」會按不到，同即時逐字稿那一句）；期限後 2 秒才過期（倒數收起時 `Ended` 會先收）。「繼續錄」「停止並轉錄」都 `Cancel()`。
  - 通知（島關掉）：內文「60 秒後自動停止錄音並轉錄」；通知按鈕的 `shiyin:` 網址 `continue`、`stop` 都 `Cancel()`。
  - 小視窗 `MeetingPromptWindow`（島關掉、通知也關掉）：寫同一句、不倒數，30 秒後照舊收起（同 Mac 的浮動視窗）；兩個按鈕都 `Cancel()`。
  - `Ended`：島上、小視窗開著的「會議好像結束了」收起來。
  - 設定關掉時照舊只問、30 秒後收起來（R95.2）。
- DEBUG：`--set meetingEndCountdownSeconds=8`（同 Mac 的 `-meetingEndCountdownSeconds`）；state 加 `meetingEndCountdown`（剩幾秒，沒在倒數是 -1；同 Mac 的 `meetingprompts` 的 `countdown`）、`island.promptText`（島上現在寫的字）。模擬：`record-sim` 錄音中 `meetingapp zoom.exe on`、過幾秒 `meetingapp zoom.exe off`（停 10 秒才算結束），`meetingaction continue|stop` 等於按島上的按鈕。

### 11.29 去掉贅字的逐字稿（R96，M§70）

- 資料：migration v11（`MigrationSql.V11`，SQL 同 Mac，標準答案 `db/schema.sql`）：`clean_lines`、觸發器 `clean_lines_text`。`AppDatabase.CleanLines`、`SaveCleanLines`（這場原本的整份換掉、段落已經不在的跳過）、`LastJob(id, kind)`（輔助工作也算）。Windows 的連線一直開著外鍵（`ForeignKeys = true`），重新轉錄（`SaveTranscript` 刪段落）、刪除一句的 `ON DELETE CASCADE` 會跟著刪；拆句、編輯、接受或還原校正、用詞修正改了 `text` 時觸發器刪掉那一句（只改 `seq` 的 UPDATE 不會觸發）。
- 工作：`JobKind.Clean`（`clean`；輔助線、`UsesSummarizer`、`IsAuxiliary`：不發通知、不改會議的狀態、列表照舊是完成）。Core 的 `TranscriptCleanup`（提示詞中英日、粵語接 `transcriptNote.correction`、schema、`Format`、每次 120 句、`Validate`，同 Mac；字數用 `SwiftText.Count`（字形叢集）；標準答案 `prompts/<語言>/clean-system.txt`、`clean-prompt.txt`、`schemas/clean.json`、`formats.json` 的 clean、`util/validate-clean.json`）。`JobRunner.Clean`：沒選整理工具時失敗（「還沒設定整理工具，請到設定選擇 Claude、Codex 或 API」），每一句都存（改過的用改過的、中文會議用簡體時轉簡體；沒改的存原文；整句贅字存空字串）；失敗照一般的工作記原因。系統匣的工作寫「整理中：…」（同 Mac 選單列的 default）。
- `AppController.GenerateCleanTranscript(id)`：擋法和訊息同「產生每個人的重點」（`ReadyForAction`、整理工具是「不整理」、還沒準備好），已經在排或在跑的不重複排；`CleaningProgress(id)`（在不在產生、進度）。`MeetingDetail.CleanLines`、`CleanFailure`（最後一次 clean 失敗的原因，可能是空字串）。
- 畫面（`TranscriptTab`）：搜尋列在說話者篩選後面加平台的 `SelectorBar`「原文｜精簡」（同紀錄分頁的「條列｜心智圖｜人物」；無障礙名稱「逐字稿」、`ToolTip` 同 Mac 的 help；沒有逐字稿時停用；存在 settings.json 的 `transcriptView`）。
  - 精簡、有精簡版：每一句顯示 `CleanLines[id] ?? 原文`，不標校正建議、不畫搜尋的底色（都對原文）；空字串的句子不列（說話者篩選照舊）。播放跟隨、點時間點、搜尋（找原文、捲到那一句）、用詞修正、右鍵的「拷貝」（原文，同 Mac）、匯出照舊用原文；列的無障礙名稱用畫面上的字。
  - 精簡、還沒有精簡版：列表的 `Header` 放說明（次要文字、跟著文字大小，最寬 760）、上次失敗時「上次產生精簡版失敗：原因」（琥珀色、`L.Ui`、可以選取）、按鈕「產生精簡版」（失敗過是「再試一次」；`SymbolIcon(Symbol.Filter)`＋字，Mac 是 text.badge.minus；`ToolTip`「會用整理工具的額度；長的會議要幾分鐘」；這場在處理時停用；按鈕不跟文字大小，R59.2）。產生中是 `ProgressRing`＋「正在產生精簡版 N%」（排隊、第一批還沒回來是「正在產生精簡版…」）：主視窗每秒的 `DetailView.UpdateStatus` 呼叫 `TranscriptTab.UpdateClean`，樣子變了才重建那一塊；做完、失敗時 `JobRunner.Changed` 整份重讀。
  - 編輯一句之後：主視窗本來就整份重讀，觸發器刪掉的那一句回到原文（M§70 的「只重讀幾句」是 Mac 才有的）。
- DEBUG：`cleantranscript <會議 id>`（等於按「產生精簡版」）、`transcriptview original|clean`（同 Mac）；state 加 `transcriptView`、`cleanLines`（選取的會議有幾句精簡版，0 是還沒產生）、`cleanEmpty`（幾句是空字串）、`cleanFailure`、`cleaning`（產生中的進度；Mac 沒有）。

### 11.30 會議列表的排序、分組、篩選（R97，M§71）

- 規則放在 Services 的 `MeetingList`（同 Mac 的 `MeetingListOptions.swift`，單元測試 `OrganizeTests.MeetingListSortsGroupsAndFilters`）：`MeetingSort`（dateDesc、dateAsc、title、duration）、`MeetingGrouping`（none、month、folder；以前存的 series 讀成不分組，§11.32）存在 settings.json 的 `meetingSort`、`meetingGrouping`；`MeetingFilter`（needsReview、myActionItems、personId、templateId）只在 `MainState`（記憶體），換篩選時取消多選。標題照 `NaturalComparer`（Core，原本在 Services 的資料夾排序：數字照大小、不分大小寫、照目前的語系，同 Mac 的 `localizedStandardCompare`）；長度一樣的、沒有長度的照原本的順序（穩定排序）。月份的標題「2026年11月」「November 2026」（介面語言，簡體經過 `L.Ui`）。`AppDatabase.MeetingPeople()`（`speakers.person_id`）在 `MainState.LoadLists` 跟列表一起讀。
- 列表（`MainWindow`）：側欄導覽下面原本的「所有會議」那一行換成標題列 `sectionHeader`（`Ui.Bar`）：左邊「所有會議」（或資料夾的名稱），分組時接「・依月份」，放不下時省略號；右邊「顯示方式」按鈕（平台的 `Button`＋`Flyout`，漏斗 `E71C`；無障礙名稱「顯示方式」、`ToolTip`「排序、分組、篩選會議」）。沒篩選時沒有框、灰色（`Ui.Borderless(…, Secondary)`），篩選中換成 `AccentButtonStyle`（實心青瓷綠、白色的圖示，同 Mac 的 fill、celadon）。人物模式沒有這個按鈕。標題列、篩選列、垃圾桶那一行在建構時就做好、不重建：換深淺色時（和啟動時 `Ui.SetTheme` 之後）重新給顏色（`MainWindow.restyle`）；灰色是 app 主題資源當時的那一份、青瓷綠看 `Ui.SetTheme`，不重給的話深色開 app 時漏斗是淺色的深綠、開著換成淺色時標題和 ✕ 是看不見的淺灰（2026-11-16 深色巡檢）。
- 選單（`MenuFlyout`，打開時才建，`BottomEdgeAlignedRight`）：三段用 `MenuFlyoutSeparator` 和停用的標題項目隔開（同會議頁的「會議設定」，§11.22）：「排序方式」四個 `RadioMenuFlyoutItem`（`GroupName` sort）；「分組方式」`RadioMenuFlyoutItem`（看某個資料夾時沒有「依資料夾」，打勾的是實際用的）；「篩選」：`ToggleMenuFlyoutItem`「有待確認的」「有我的待辦的」（沒有「我」時不出現），`MenuFlyoutSubItem`「有誰參加 ▸」「範本 ▸」（裡面是「不限」、分隔線、人物（我的話「名字（我）」）或範本的 `RadioMenuFlyoutItem`）；篩選中最後「清除篩選」。
- 篩選中：標題列下面一列 `filterBar`（漏斗、「篩選：有待確認、王小明 有參加」最多兩行、✕ 按鈕「清除篩選」）；篩選後沒有會議時再下面「沒有符合篩選的會議」和 `HyperlinkButton`「清除篩選」。跟 Mac 不同：都放在 `ListView` 外面（停用的 `ListViewItem` 裡的按鈕按不了，§11.23 的垃圾桶說明也是這樣）。
- 分組：每組一段，標題是不能選的 `ListViewItem`（12、粗體、次要文字，同搜尋結果的會議標題），下面是那一組的會議。多選（§11.23）照舊，Shift 點選一次選一段時跨過標題。列表的樣子（`ListSignature`）加上分組、排序：換了才整個重建。
- 垃圾桶：只能排序（R97.4）。Windows 沒有選單列（Mac 在選單列的「顯示方式」），垃圾桶上方「垃圾桶裡的會議 30 天後會永久刪除」那一行，「清空垃圾桶…」左邊多一個同樣的「顯示方式」按鈕，選單只有「排序方式」那一段（`ToolTip`「排序方式」）。
- DEBUG：`listsort <dateDesc|dateAsc|title|duration>`、`listgroup <none|month|folder>`、`listfilter <review|mine|person <id>|template <id>|clear>`（同 Mac，review、mine 是切換）、`reload`（直接改資料庫之後重讀）；state 加 `list`（`sort`、`group`、`filter`、`filterSummary`、`myName`、`ids`、`groups`，同 Mac）。

### 11.31 資料夾自動歸類（R98，M§72）

- 資料：migration v12（`MigrationSql.V12`，跟 §11.32 一起，SQL 同 Mac，標準答案 `db/schema.sql`、`db/*.json` 的 `folder_decided`、`series_id`）。`meetings` 整列寫入多了 `folder_decided`（`MeetingColumns`；`series_id` v14 起不寫，§11.32）。`folder_rules` 的 `keywords`、`people` 是 JSON 陣列，寫法同 GRDB 的 `JSONEncoder`（緊湊、斜線跳脫 `\/`），讀的時候兩種都收。`AppDatabase.FolderRules.cs`（原本叫 `AppDatabase.Series.cs`）：`FolderRules`、`SaveFolderRule`（`ON CONFLICT(folder) DO UPDATE`，條件都空時刪掉）、`MeetingsMatching`、`SetFolderDecided`、`AutoFile`（符合好幾個時照 `NaturalComparer` 排第一的）；`SetFolder` 一起設 `folder_decided = 1`；`RenameFolder` 時條件跟著改名，刪除資料夾（或新名稱已經有條件）時條件一起刪（同 Mac）。
- 規則在 Core 的 `Model/FolderRules.cs`（同 Mac 的 `Store/FolderRules.swift`）：`FolderRule.Matches`（標題包含任一個詞用 `OrdinalIgnoreCase`）、`FolderSuggester.Best`（資料夾的順序由呼叫的人給：`NaturalComparer`）；標準答案 `util/folder-rule-match.json`、`util/folder-suggest.json`。共同的詞（`MeetingSimilarity.CommonWord`，標準答案 `util/common-word.json`）照 Swift 的 Character 算字數（`SwiftText.Characters`），頭尾去掉的字元用 `Rune` 的空白、標點、符號，字母是 L、M 類（同 `CharacterSet.letters`）；完整單字的檢查（`IsWholeWord`）只看 ASCII 的字母、數字。
- 側欄：有條件的資料夾一直列著（沒有會議時 0，`Organizing.Folders`），圖示是資料夾的 `SymbolIcon` 右下角疊一個小的青瓷綠齒輪（`E713` 11 號，放在側欄底色的 13×13 小圓上，不跟資料夾的線疊在一起；Mac 是 folder.badge.gearshape，Segoe Fluent 沒有現成的），`ToolTip` 是條件（`Organizing.RuleSummary`：「自動加入：標題包含 週會、sync；陳家豪、王小明 都參加；範本：一對一」）。資料夾的右鍵：「自動加入的條件…」（有條件時「編輯自動加入的條件…」）、分隔線、重新命名…、刪除資料夾。「移到資料夾」的選單（右鍵、多選）也列出只有條件的空資料夾（同 Mac 的 `folders`）。
- 條件的編輯（`Dialogs.FolderRule`，`ContentDialog`，同 Mac 的 `FolderRuleSheet`）：標題「「X」自動加入的條件」；說明；`TextBox`「標題包含」（提示字「例如：週會、sync（任一個詞）」，用 `GlossaryInput.Split` 分）；「這些人都參加」：人物的 `CheckBox`（我的話「名字（我）」，`ScrollViewer` 最高 160）；`ComboBox`「用這個範本」（不限＋…）；`CheckBox`「現在不在資料夾裡的會議也套用（N 場）」：每改一個條件就重算 N（`MeetingsMatching`），0 場時停用。按鈕：主要「儲存」（條件都空時等於移除）、次要「移除條件」（已經有條件時；Mac 的 destructive 按鈕在左邊，`ContentDialog` 的次要按鈕在中間）、關閉「取消」。套用的會議設 `folder_decided = 1`，提示「已把 N 場會議放進「X」」。
- 自動放：`JobRunner.Transcribe` 在聲紋比對之後 `AutoFile`，`Summarize` 存好正式的紀錄後再 `AutoFile` 一次（草稿、只補每個人的重點不放）；放了就發 `JobRunner.Organized(會議, 資料夾)`，`AppController.Organized` 提示「「標題」已放進「資料夾」」（主視窗下方的提示，同 Mac 的 noticeMessage）。
- 建議（R98.3）：`Organizing.Suggestion`（不在資料夾、沒決定過、有紀錄、不在錄音中、不在垃圾桶），紀錄分頁頂端、待確認條的上面（文件有變動的提示下面）一條：資料夾加號的圖示（`E8F4`）、「這場很像「客戶」裡的會議：陳家豪、Bob 都參加（2 場）」（最多兩行）、「不用」「放進去」（強調色）；底色是淡淡的青瓷綠（淺色 8%、深色 14%，同 Mac 的 celadon 0.08）；提示列不跟著文字大小（R59.2）。
- DEBUG：`folderrule <資料夾> <JSON|JSON 檔的路徑|-> [apply]`（同 Mac；從 SSH 傳中文不方便時給檔案路徑，同 §11.25 的 `editnotes`）、`editfolderrule <資料夾>`（打開條件的編輯）、`autofile <會議 id>`（回傳 `folder`）、`foldersuggest <會議 id>`（Mac 寫到檔案，Windows 直接回傳 `folder`、`count`、`reason`、`text`）、`foldersuggestion accept|dismiss <會議 id>`；state 的 `folderRules`、`folderRuleDialog`（編輯開著時是哪個資料夾；Mac 沒有）。

### 11.32 定期會議（R99，M§73；2026-11-17 拿掉，D177）

- 使用者：「不需要定期會議這概念了」「做成一般資料夾概念即可」。同 Mac（df5ef9a）整個拿掉，原本的做法在 git 歷史（`git show f133362:docs/design-windows.md` 的 §11.32）。同一群人固定開的會用資料夾放，要自動放進去用資料夾的自動加入條件（§11.31：標題、人物、範本）。
- 拿掉的：Core 的 `MeetingSeries`、`MeetingTime`、`SeriesCandidate`、`SeriesDetector`（找可能的定期會議、自動加入的判斷、名稱和頻率的文字）、`PreviousMeeting`、`SeriesTasks`；`MeetingNotes.FollowUps`（`FollowUp`、`FollowUpStatus`）、`NotesContext.Previous`、`NotesPrompt.PreviousBlock`、匯出的「上次的待辦」；`FolderRule.SeriesId`、`Meeting.SeriesId`；Data 的 `Series`、`CreateSeries`、`RenameSeries`、`DeleteSeries`、`SetSeries`、`SeriesMeetings`、`PreviousSeriesMeeting`、`AutoJoinSeries`、`RejectSeries`、`SeriesOpenItems`；Services 的 `MeetingGrouping.Series`（以前存的 `series` 讀成不分組）、`Organizing.SeriesInputs`／`CommonSeries`／`Regulars`／`JoinSuggestion`、`SeriesJoinSuggestion`、`RenameTarget.Series`、`SidebarCollapse.SeriesKey`、`AppController` 的定期會議動作、轉錄完的自動加入、整理時接上一場；App 的側欄「發現 N 組可能的定期會議」和定期會議的列（↻ 標記）、確認彈窗（`Dialogs.SeriesSuggestions`）、系列頁（`Views/SeriesPage`；裡面的 `FlowPanel` 會議頁的資訊列還在用，搬到 `Ui/Ui.cs`）、資訊列的「↻ 名稱」、紀錄分頁的建議加入和「上次的待辦」、右鍵的「加到定期會議 ▸」「移出「X」」、資料夾條件的「屬於定期會議」、拖到定期會議、使用說明那兩條；DEBUG 的 `seriesnew`、`seriesdeleteui`、`seriessuggest`、`seriessuggestion`、`seriescandidates`、`seriescreate`、`seriesreject`、`seriesjoin`、`seriesfilter`、`seriesrename`、`seriesdelete`、`autojoin`、`seriessheet`、`seriesopenitems`、`previousmeeting`，state 的 `series`、`seriesCandidates`、`seriesFilter`、`seriesDialog`、`confirmDeleteSeries`、`folderRules` 的 `seriesId`。
- 留著的（資料夾的建議 §11.31 用）：`MeetingSimilarity`（同 Mac 改名：`SamePeople`、`Core`、`CommonWord` 和它用的 `IsWholeWord`、`WithoutNumbers`、`Clean`、`IsSignificant`）；Core 的 `Series.cs` 改名 `FolderRules.cs`，Data 的 `AppDatabase.Series.cs` 改名 `AppDatabase.FolderRules.cs`（條件、自動放、`MeetingPeople`）。
- migration v14（同 Mac 的 v14）：`RunMigration` 先把每個 `series`（`ORDER BY id`，名稱頭尾的空白去掉，空的跳過）換成同名資料夾：`INSERT OR IGNORE INTO folders`（已經有就合在一起）、`series_id` 是它而 `folder` 是空的會議設成這個名字、`folder_decided = 1`（已經在別的資料夾的不動）；再跑 `MigrationSql.V14`：`folder_rules.series_id` 都設 null、`keywords`、`people` 都是 `'[]'` 且 `template_id` 是 null 的刪掉、`meetings.series_id` 都設 null、清空 `series` 和 `series_rejected`。表、欄位留著不用（拿掉有外鍵的欄位要重建整張 `meetings`，不值得）；會議整列寫入（`MeetingColumns`）不再寫 `series_id`、存條件時一律寫 null。換掉的名稱記在 `AppDatabase.MigratedSeries`（id → 名稱，只有跑 v14 的那一次有），`AppHost` 開資料庫後交給 `SidebarCollapse.ReplaceSeriesKeys`：settings.json 的 `sidebarExpanded`、`sidebarOrder` 裡的 `series:<id>` 換成 `folder:<名稱>`，換完跟原本的重複時留前面的、找不到的拿掉（展開的照 Ordinal 排序存，排序的照原本的順序；同 Mac 的 `AppDatabase.replaceSeriesKeys`，Mac 在 migration 裡直接改 UserDefaults，Windows 的 Data 碰不到設定所以交給 Services）。
- 會議紀錄的 `followUps`：以前存的照樣留在資料庫的 JSON 裡，解碼時略過、`ToJson` 不寫，重新整理或編輯存回去時就沒了；整理工具還回 `followUps` 也不存。`JsonSchema`、提示詞、匯出跟 4.8.1 一樣（加上 people）。
- 標準答案（Mac 重跑）：拿掉 `util/series-*`、`meeting-time`、`followups-validate`、`export/*/notes-followups.*`、`prompts/*/summarize-followups.txt`；`series-common-word` 改名 `util/common-word.json`；`folder-rule-match` 沒有 `seriesId`（6 個）；`schemas/notes.json` 沒有 `followUps`；`llm/mac-requests.jsonl` 重錄。
- 測試：`OrganizeTests.CommonWordMatchesMac`、`FolderRulesAndSuggestionsMatchMac`、`MigrationV14TurnsSeriesIntoFolders`（暫存的資料庫退回 v13 塞定期會議、拒絕的組、條件，重開後換成資料夾、條件、series 清空、`MigratedSeries`，再開一次是空的；settings.json 的鍵換掉）、`OldFollowUpsAreDropped`；`ServicesTests.SummarizeFilesIntoFolderWithoutFollowUps`（提示詞沒有上一場、整理工具回的 followUps 不存、整理完照條件放進資料夾發 `Organized`）；側欄、拖曳、重新命名、列表的測試拿掉定期會議的部分。
- 驗證：虛擬機用舊版建的有定期會議的資料（或 SQL 塞一筆 series）開新版：側欄是同名資料夾、會議在裡面（已經在別的資料夾的不動），展開、順序照舊；右鍵、顯示方式的分組、資料夾條件都沒有定期會議。

### 11.33 每一種東西都能直接改、刪（R100，M§74）

- 三條路、同一份選單（同 M§74）：右鍵（`ContextFlyout`）、列右邊的「⋯」、頁面的「更多」都呼叫同一個填選單的函式，打開時才建（人、資料夾可能改了）：側欄的會議 `FillRowMenu`（一場時第一項「重新命名…」＋分隔線，再來移到資料夾、移到垃圾桶；垃圾桶裡還原、永久刪除…）、資料夾 `FillFolderMenu`（自動加入的條件…、重新命名…、分隔線、刪除資料夾…；R101 起重新命名在條件前面，§11.34；R101 加過的最上面「只看這個資料夾」fa12d60 拿掉）、人物 `PersonPage.MenuItems`（重新命名…、合併到 ▸、分隔線、移除這個人…）、人物頁的會議列（打開這場會議）。
- 「⋯」（`Ui/RowMore`，同 Mac 的 `RowMenuButton`）：`Ui.Borderless` 的按鈕、`E712`（More）、次要文字的顏色，`ToolTip` 和無障礙名稱「更多動作」；`Flyout` 是同一份選單。滑鼠進出那一列（`PointerEntered`／`PointerExited`）、那一列選到（`SelectorItem.IsSelectedProperty` 的變化）或選單開著時顯示，沒顯示時透明、不能按、不能 Tab（照樣佔位，列的寬度不跳）。資料夾的「⋯」跟數量疊在同一格，顯示時數量透明（同 Mac 的 `FolderRow`）。會議列、人物列放在最右邊一欄；人物頁的會議列放在列右邊的內距裡；紀錄的條目接在時間點、待確認的標記後面（同 `Ui.Inline` 的 trailing，短的時候緊接在文字後面）。
- 鍵盤（側欄）：照選到、亮起來的東西（`MainWindow.SidebarRename`、`SidebarDelete`，同 Mac 的 `sidebarReturn`、`sidebarDelete`）。重新命名是 Enter 或 F2（Windows 的慣例，檔案總管、Outlook 的資料夾都是 F2；Mac 是 Return）。會議列表（`list.KeyDown`）：人物模式選到人 → 重新命名／移除；選到一場會議（不是多選）→ 重新命名（R102 起都是重新命名的對話框，§11.35）；多選時 Delete 一起移到垃圾桶；沒選會議而在看資料夾 → 重新命名或刪除它；垃圾桶裡 Delete 是永久刪除。側欄上面的資料夾那幾列有焦點時照那一列，不是右邊選的會議（Windows 的側欄是兩個清單，Mac 是一個）；R101 起那幾列點了是在原地展開（§11.34），按鍵在那一列自己處理：F2 重新命名、Delete 刪除，Enter 是展開收起。Ctrl＋Delete 照舊是整個視窗的移到垃圾桶。
- 會議的重新命名（`RenameMeetingUi`）：原本是選那場、打開「編輯」的 `ContentDialog`（標題、日期、與會者）；R102 起是重新命名的對話框（§11.35）。
- 刪除資料夾的確認改到主視窗（`ConfirmDeleteFolder`，`Dialogs.Confirm`；R106 起是 §11.39 的確認，紅底、Enter 是確認）：「刪除資料夾「X」？」說明「裡面的 N 場會議會留著，移到「不在資料夾」。自動加入的條件也會刪掉。」（沒有會議時只有後半句）、按鈕「刪除資料夾」。原本資料夾右鍵的「刪除…（會議會保留）」直接刪，改成先確認（同 Mac）。正在看那個資料夾時刪掉後回到所有會議。
- 人物頁：名字旁的鉛筆 `ToolTip` 改「重新命名」；摘要下面一排 `Ui.Borderless`：「合併到」（`DropDownButton`，`Symbol.People`，沒有別人時不放，`ToolTip`「同一個人被記成兩個名字時，合併成一個」）、「移除這個人…」（`Symbol.Delete`、`Ui.Danger` 的紅字）。右上角的「更多」照舊。
- 按鈕吃掉右鍵的問題（Mac a8a59e8）：WinUI 的右鍵選單是 `ContextFlyout`，掛在按鈕本身就出得來；人物頁的會議列、說話者的名字都掛在按鈕上，側欄的資料夾列是 `ListViewItem`（不是按鈕），掛在整列。
- 會議頁上方的說話者：沒有自動帶入的名字改成 `HyperlinkButton`（頭像＋名字），點了打開重新命名的 `Flyout`（名字選好、Enter 儲存，`RenameSpeaker`），`ToolTip`「重新命名「X」，整場的逐字稿和會議紀錄都會跟著改」；右鍵：重新命名…、合併到 ▸（其他說話者，`MergeSpeaker`）。自動帶入的名字點了照舊是「是別人／不是他」的選單，右鍵同上。
- 紀錄的條目（條列的摘要、決議、待辦的事項、未決問題、議題的要點、自訂段落；人物的重點）：不開文字選取（同 Mac a8a59e8：`IsTextSelectionEnabled` 開著時，字上的右鍵是系統的文字選單、按兩下只選到一個詞，自己的選單和就地改都出不來），要拷貝用右鍵的「拷貝」；整行（含行尾空白）都收得到右鍵、按兩下：列的 `Panel` 背景設成透明（沒有背景的 `Panel` 空白處點不到），待辦的事項外面再包一層撐滿那一欄的 `Grid`。問答照舊可以選取。按兩下就地改（`AddHandler(DoubleTappedEvent, …, handledEventsToo: true)`）；改的時候是 `TextBox`（Enter 儲存、Esc 取消、「取消」「儲存」，下面小字「Enter 儲存、Esc 取消」，Mac 寫 Return）。右鍵：拷貝、從這裡播放（有時間點、有錄音時）、分隔線、編輯這一條、刪除這一條（沒辦法改時停用：垃圾桶裡、錄這場、正在處理、開著編輯器）。存檔走 `AppController.EditNoteItem`／`DeleteNoteItem`（Core 的 `NoteItemEdits.TextAt`、`WithText`、`Removing`，同 Mac 的 `text(at:)`、`setText`、`remove`；`SaveEditedNotes` 留一個版本）。刪除不確認，提示「已刪除一條紀錄，可以按紀錄分頁「編輯」旁的「復原上一版」還原」（Windows 才有的句子：Windows 沒有選單列，復原在「編輯」旁邊）。主視窗每次資料改了都整頁重畫，正在改的那一條記在 `NotesTabs`（換一場會議不算）。
- 重點標記（逐字稿「我的筆記」那一列的「重點標記 N」）：每個標記一個子選單：跳到這裡、重新命名…（R102 起是重新命名的對話框，說明「mm:ss 的標記」，§11.35）、分隔線、刪除。`AppDatabase.RenameBookmark`。
- 按 Enter 存檔、輸入框被拿掉時，焦點落到視窗的第一個元件（錄音按鈕）；`SplitButton` 放開 Enter、空白鍵就執行（不管在哪裡按下），放開的 Enter 就開始錄音（2026-11-16 虛擬機上就地改紀錄時發生）。錄音、匯出這兩個 `SplitButton` 用 `Ui.IgnoreStrayKeyUp`：`PreviewKeyDown` 記下按下的鍵，放開的 Enter、空白鍵沒有在這個按鈕上按下過就標成處理過。逐字稿的就地改等原本按 Enter 存檔的地方也一起受惠。
- 設定：詞彙每一列加鉛筆（「修改「X」」）、按兩下詞改字（兩下點擊各切換一次勾選，剛好切回原本的；`CheckBox` 自己處理指標、不發 `DoubleTapped`，掛在字上或 `handledEventsToo` 都收不到，改成用 `PointerPressed`／`PointerReleased`（`handledEventsToo`）看兩次按下的間隔 ≤ 500 毫秒，第二次放開後才進入修改）、整列（背景透明）右鍵重新命名…、刪除；改的時候是 `TextBox`（Enter 儲存、Esc 取消）。`AppDatabase.RenameTerm`（同 Mac：改成已經有的詞時合併、刪掉這一個）。範本列表右鍵：複製、設為預設範本（已經是預設時停用）、刪除（自訂的才有）。名字放在撐滿的那一欄、太長時省略號（英文「General meeting (copy)」會把「Default」擠出列表）；複製內建範本（右鍵和下面的按鈕）用畫面上的內容，簡體介面複製出簡體（同 Mac「複製」按鈕的 draft；Mac 的右鍵「複製」直接拿繁體的定義，2026-11-16 回報）。認識的人：「改名」改叫「重新命名」，垃圾桶的 `ToolTip`「移除「X」（會議裡的名字留著）」、移除前確認（用詞同人物頁，預設按鈕是取消）、右鍵重新命名…、移除這個人…。
- 使用說明（`OtherWindows` 的「找會議、播放」）加兩條（原本三條，定期會議那一條 D177 拿掉）：顯示方式和自動加入的條件、直接改刪（Windows 寫「漏斗按鈕」「Enter 或 F2」「按兩下」「紀錄分頁「編輯」旁的「復原上一版」」）；人物那一條改成同 Mac 的新說法。
- 介面文字：Mac 的鍵在 c2e2be0 才加，這個分支從那之前開的，英文照 Mac 的寫進 `Windows.xcstrings`（合併後兩邊相同）；Windows 才有的：「Enter 儲存、Esc 取消」、「已刪除一條紀錄，…「復原上一版」還原」和使用說明的四條。
- DEBUG（同 Mac 的名稱）：`renamemeetingui <id>`、`folderdeleteui <名稱>`（打開確認）、`sidebarkey return|delete`（等於在會議列表按 Enter、Delete，回傳 `handled`）、`noteitem editui|edit|delete|cancel <section> <i> [<j>] [文字]`、`bookmarkrename <id> <名稱>`、`bookmarkrenameui <id>`、`termrename <id> <詞>`；state 加 `confirmDeleteFolder`、`editingNoteItem`、`bookmarks`（選取中的會議的標記；Mac 沒有）。
- 測試：`AppTests.EditAndDeleteThingsInPlace`（就地改：待辦只改事項、時間點不變、留版本、空的和沒改的不存；刪除一條的提示、復原上一版拿回來；標記重新命名；詞改字、改成已經有的詞時合併）。

### 11.34 側欄的段落、資料夾可以收合，側欄和選單的樣子，下面只放其他會議（R101，M§75、D175）

- 狀態：Services 的 `SidebarCollapse`（同 Mac 的 `Store/SidebarCollapse.swift`）讀寫 settings.json 的 `sidebarCollapsed`（收起來的段落：`folders`、`meetings`、`people`、`group:<分組 id>`，分組 id 同 Mac 的 `month-2026-11`、`folder-客戶`）和 `sidebarExpanded`（在原地展開的列：`folder:<名稱>`；以前的 `series:<id>` 在 migration v14 換成同名資料夾的鍵，§11.32），字串陣列、照 Ordinal 排序，值真的變了才寫。資料夾改名、刪除資料夾時由 `AppController.RenameFolder`／`DeleteFolder` 跟著改鍵（Mac 在 AppModel；Windows 的這幾個動作都在 AppController，對話框、確認、控制通道都走這裡）。主視窗聽 `Settings.Changed` 的這兩個鍵重畫（控制通道改的也一樣）。
- 用了哪些元件：Windows 的側欄是兩個 `ListView`（上面的導覽 `scopes`：所有會議、人物、資料夾那一段；下面的會議列表 `list`），中間是標題列（§11.30）。**不用 `TreeView`、`Expander`**：TreeView 的子節點是可以選的節點，選到展開的那場就把目前的範圍（所有會議／資料夾）的選取換掉，跟「下面的列表不換」衝突；Expander 不能放進 ListView 的一列，上下鍵也不能從一段走到下一段。所以照 Mac 自己畫，放在原本的清單裡。ListViewItem 本來就有滑過時一塊圓角的淡底、跟選取那一塊一樣大（R101.4 要的樣子，Mac 要自己畫 `sidebarHover`），不用另外做。
- `Ui/SidebarItem`（`ListViewItem` 的子類別）：段落標題、資料夾的列、展開的會議、「顯示全部 N 場」、分組的標題都是這個。上下鍵走得到、滑過有底色，但點了不會變成選取：左鍵、觸控按下時不交給 ListViewItem（`OnPointerPressed`／`OnPointerReleased` 自己處理、自己拿焦點，從列裡的按鈕來的、右鍵照舊）——交給它的話會先選到這一列，清單把選取還原時焦點跳回選到的那一列、整個清單捲走（2026-11-16 虛擬機：點下面的資料夾展開的會議，上面捲回最上面）；上下鍵選到時清單馬上把選取還原（`scopes`；`list` 是 Extended，拿掉被選到的標題，只選到標題時還原原本選的會議，Shift 點選跨過標題時照舊選那一段）。動作只在點一下（`Tapped`，`handledEventsToo`；從「⋯」來的不算）、Enter／空白鍵（`PreviewKeyDown`，焦點在這一列本身時）、UI Automation 的 Invoke 時做：自己的 AutomationPeer（`ListViewItemAutomationPeer` 加 `IInvokeProvider`，可以收合的多 `IExpandCollapseProvider`；ListView 的項目 peer 把不認得的 pattern 交給容器的 peer，虛擬機上 `uia.sh click` 用 Invoke 按得到）。原本停用的標題項目（資料夾、分組的每一組）換成這個：停用的項目不能點、不能有焦點。
- 段落標題（資料夾、分組的每一組）：標題（14 號、不加粗、次要文字的顏色：跟列一樣大的灰字）後面一個小箭頭（Segoe Fluent 的 `E70D` 展開／`E76C` 收起，10 號，第三層文字的顏色），收起來時一直顯示，展開時只在滑過、有鍵盤焦點時出現（opacity，位置留著；R101.1）；滑過去沒有底色（`ListViewItemBackgroundPointerOver`／`Pressed` 換成透明；對比主題時照平台，不然反白的字落在透明的底上看不見）。點標題、Enter、空白鍵切換；收起時那一段只剩標題。無障礙名稱是標題，`ItemStatus`「已展開」「已收起」，`HeadingLevel` 2，`ToolTip`「收起這一段」「展開這一段」。
- 會議列表的標題列（`sectionHeader`）：左邊的名稱換成沒有框的按鈕（`Ui.Borderless`、次要文字，滑過去沒有底色、只出現箭頭，對比主題時照平台的按鈕）：名稱、後面同樣的箭頭（`Ui.Inline`：短的時候箭頭緊接在字後面，長的時候名稱省略號、箭頭留著），點了切換 `meetings`（人物模式是「人物」、`people`）；右邊的「顯示方式」不動。Windows 的兩個是分開的按鈕，沒有 Mac 段落標題併成一個元件、選單打不開的問題（M§75），所以標題是一般的按鈕（名稱是標題、`ItemStatus`）。收起時篩選列、篩選後空的提示、列表（人物模式是人物列表）都不顯示；垃圾桶、搜尋結果不收合。
- 「資料夾」一段（R101.3）：資料夾（照拖曳排的順序，§11.36）；R104 起一直顯示，標題右邊「+」（§11.37）。
- 資料夾的列（`FolderRow`，R101.2）：點整列（Enter、空白鍵、UI Automation 的 Invoke）是在原地展開、收起，不換下面的列表；沒有箭頭，圖示就是狀態：收起時關著的資料夾（Segoe Fluent 的 Folder `E8B7`）、展開時打開的資料夾（FolderOpen `E838`），20 號（跟所有會議、人物的 `SymbolIcon` 同寬，圖示照舊對齊）。有自動加入條件的資料夾右下角一個小齒輪（`E713` 11 號，M§72），標記放在側欄底色的 13×13 小圓上、青瓷綠（墊底色，資料夾的線不會穿過標記；Mac 是挖空。已知：滑過、選到時列的底色跟小圓的底色不一樣，看得出一個小圓），關著、打開兩種圖示都有。無障礙：名稱「客戶」，`ItemStatus`「已展開、8 場」（右邊不放場數，§11.34 的單色），ExpandCollapse。只看某個資料夾（「顯示全部」）時那一列照舊是選到的樣子：`SyncScopeSelection` 照 `FolderFilter` 選它（程式選的不還原）。鍵盤：F2 重新命名、Delete 刪除（先確認，R100）、→ 展開、← 收起（TreeView 的習慣）；Enter 是展開收起（這一列是按鈕；原本 §11.33 的 Enter 重新命名改成只有 F2）。資料夾的選單沒有「只看這個資料夾」（fa12d60 拿掉，使用者：「不用只看這個」），只看一個資料夾從展開後的「顯示全部」。
- 展開的內容：那個資料夾最近 5 場（`SidebarCollapse.PeekRows`：記憶體裡的會議列表，資料庫照 `meeting_date DESC, id DESC` 讀出來，不含垃圾桶），每場一列：前面留圖示的空位（名稱跟資料夾名稱對齊）、標題 13 號一行（太長省略號），`ToolTip` 是「日期・標題」，無障礙名稱是標題。點了 `MainState.OpenFromSidebar`：人物模式換回會議、選那場，下面的列表不換；人物模式換回會議時上面的選取換成「所有會議」，清單會把焦點帶過去，所以焦點留回這一列（接著按 Enter 才是重新命名這場）。選到的那場：一塊灰底、字照常（85e6998 起，同下面列表選到的樣子；原本青瓷綠粗體）——不能用平台的選取：上面清單的選取是目前的範圍，同一場也在下面的列表，兩邊都亮起來會像多選（同 Mac 的理由），所以用 `SidebarItem.Backplate` 自己畫（下面「單色」）。對比主題時字不自己給顏色（跟著列的前景色：滑過去是反白的底，反白顏色的字會看不見），改成粗體加一圈反白顏色的細框（同逐字稿目前的段落，§11.3）。選到的那場換了只改字的樣子、不重建（`SyncScopeSelection`）。右鍵、「⋯」同會議列表（`FillRowMenu`，只這一場、不管多選，同 Mac 的 `MeetingMenuItems(ids: [id])`）。鍵盤：焦點在展開的會議時 Enter／F2：選到的那場重新命名（同會議列表），還沒選到的打開它；Delete：選到的那場移到垃圾桶（先確認），還沒選到的不處理。超過 5 場時最後一列「顯示全部 N 場」（次要文字）：下面的列表只看這個資料夾（`ShowMeetings`）。沒有會議時（空的資料夾：有自動加入條件的、新增的，§11.37）一列次要文字的「沒有會議」（停用的項目，不能選、不能有焦點）。
- 下面的列表（R101.6，`SidebarCollapse.ShowsOthersOnly`、`MainState.ListShowsOthersOnly`）：不在垃圾桶、沒看某個資料夾、沒篩選、不是依資料夾分組（存的分組方式），而且至少有一場會議在資料夾裡時，`FilteredMeetings` 只留不在資料夾的會議，標題「其他會議」；空的時候一行次要文字的「不在資料夾裡的會議會列在這裡」（停用的項目）。其他情況照舊（標題「所有會議」或資料夾的名稱）。`SelectedMeetings`、多選、Ctrl+Delete 都照 `FilteredMeetings`：從展開的會議打開資料夾裡的一場時它不在下面的列表，鍵盤由那一列自己處理（上一條）。
- 選單（R101.4）：側欄和物件的選單（會議 `FillRowMenu`、移到資料夾、資料夾、人物、人物頁裡的會議、紀錄的條目、重點標記、設定的詞彙和認識的人）每一項有圖示（`MenuFlyoutItem.Icon`，跟字同色）：重新命名 `Symbol.Edit`、移到資料夾 `Folder`、移出 `Remove`、新資料夾 `NewFolder`、自動加入的條件 `Setting`、打開（打開這場會議）Segoe Fluent 的 OpenInNewWindow `E8A7`、還原 `Undo`、拷貝 `Copy`、從這裡播放 `Play`、合併到 `People`、刪除 `Delete`、移除這個人 BlockContact `E8F8`；資料夾名稱、人名這些選項不放圖示。刪除、移除這類（移到垃圾桶、永久刪除、刪除資料夾、移除這個人、刪除這一條、刪除這句、重點標記和詞彙和範本的刪除、清除問答紀錄）用 `Ui.DangerItem`：紅字紅圖示（`Ui.Danger`，`MenuFlyoutItemForeground`／`PointerOver`／`Pressed` 一起換，滑過去也是紅的）；本來沒有圖示的選單（提問的「⋯」）只有紅字。對比主題時不上紅色，照平台的顏色（§11.3：滑過去是反白的底）。「⋯」本來就是沒有圈的三個點（`E712`）。
- 收合、展開會重建清單（樣子的比對加上收合的狀態、展開的會議的標題、「其他會議」）；重建前記下兩個清單捲到哪裡，排好版後捲回來（不然展開下面的資料夾時那一列被捲走）；用鍵盤收合時焦點回到同一列（每一列的 `AutomationId`：`section:folders`、`scope:folder:客戶`、`peek:folder:客戶:42`、`group:month-2026-11`…，清單重建後找回來 `Focus`）。
- 上面的導覽（`scopes`）最高是側欄的 60%，超過時自己捲動：原本放在 Auto 高度的列、不能捲，資料夾多或展開了好幾個時會把下面的會議列表擠掉、超出的部分被切掉。
- 程式帶你去某場會議（`MainState.Reveal`：錄音開始、會議連結、通知、問所有會議的出處、控制通道的 `select`；搜尋結果點進去）時 `ExpandToShow`：在下面的列表就展開 `meetings` 和那一組；在資料夾裡（下面的列表沒有）就展開 `folders` 和那個資料夾（同 Mac）。
- 顏色照 §11.3：標題、箭頭、資料夾圖示是文字的顏色（對比主題時 `FollowForeground` 清掉，跟著列的前景色）；小標記的圓底是側欄的底色（對比主題時是視窗底色，`FollowForeground` 跳過有底色的一小塊）。
- 單色（85e6998，R101.4 使用者：「你可以學一下嗎？他這美觀多了」，同 M§75「單色」）：`Ui/SidebarLook`。
  - 圖示跟字同色：資料夾的小標記（齒輪）不再是青瓷綠，跟著列的字。
  - 選到的列一塊灰色圓角底、字照常，滑過一塊更淡的灰：`SidebarLook.Row` 在建列時（範本套上之前）覆寫這一列的資源：`ListViewItemBackgroundPointerOver`／`Pressed` 是字色 6%，`ListViewItemBackgroundSelected`／`SelectedPointerOver`／`SelectedPressed` 是 11%，`ListViewItemSelectionIndicator*Brush` 透明（拿掉左邊那條強調色的線），`CornerRadius` 8（同 Mac 的 `sidebarHover(selected:)`）。用在所有會議、人物、資料夾、展開的會議、「顯示全部」、下面的會議、人物、搜尋結果。「所有會議」選到也不畫（`showSelected: false`；人物模式時「人物」亮）。
  - 不在清單選取裡的列要自己畫底（展開的會議選到時的灰底、拖東西進來時的青瓷綠，§11.36）：`SidebarItem.Backplate(brush)`。平常的狀態畫的是 `ListViewItem.Background`，滑過、選到的狀態是 `ListViewItemPresenter` 的 `PointerOverBackground`、`SelectedBackground`、`SelectedPointerOverBackground`，兩邊都要換，換完切一次視覺狀態（`GoToState` 到另一個再回來）才會重畫。踩到的（2026-11-17 虛擬機）：只改 presenter 的 `Background` 畫面沒變；只改 `ListViewItem.Background` 的話滑過時（拖曳經過也算滑過）被滑過的灰蓋掉；`ClearValue` 拿掉之後畫面停在原本的顏色，要切一次狀態。範本套上之後才有 presenter，`Loaded` 時套一次、再排一個低優先順序的再套一次。
  - 右邊不放場數，滑過或選到時「⋯」；無障礙的值（`ItemStatus`）唸得到：資料夾「已展開、4 場」，所有會議「47 場」，人物「2 人」。
  - 段落標題、會議列表的標題（「其他會議」）14 號灰字、不加粗（上面「段落標題」）。
  - 段落標題右邊的小按鈕（7fb4224，使用者：「你可不可以學一下他的＋，目前的有夠醜」）：`SidebarLook.HeaderIcon`：細線的圖示 15 號（Segoe Fluent 的字形只有一種粗細）、次要文字的顏色、26×24 的點擊範圍、圓角 6，滑過變成文字的顏色加字色 8% 的底，右邊留 6 跟列的「⋯」對齊。「資料夾」的「+」（`E710`）和會議列表、垃圾桶的「顯示方式」都用它；「顯示方式」從有圈的漏斗（`E71C`）改成 Equalizer（`E9E9`，橫的三條滑桿，同 Mac 的 `slider.horizontal.3`），篩選中是強調色的字（原本是實心的強調色按鈕）。
  - 對比主題照平台的系統色（不覆寫；「所有會議」選到時底透明、字是文字的顏色），小按鈕照 `Ui.Borderless`。
- 使用說明（「找會議、播放」）三條同 Mac 的新說法（資料夾點一下展開、展開後點「顯示全部」只看這個資料夾（fa12d60 起，原本是右鍵的「只看這個資料夾」）、其他會議；「側欄太長時可以收起來」），整理那一條 Windows 照舊寫「漏斗按鈕」、沒有選單列。
- DEBUG（同 Mac）：`sidebarsection <folders|meetings|people|group:…> on|off`（on 是展開）、`sidebarexpand <folder:名稱> on|off`、`folder <會議 id> <資料夾|->`（移到資料夾，同 Mac）；state 的 `sidebarCollapsed`、`sidebarExpanded`（陣列）；`list.ids` 是「其他會議」時只有其他會議。
- 測試：`OrganizeTests.SidebarCollapseKeys`（鍵、預設展開／收起、值沒變不寫、資料夾改名、展開的會議的順序、ExpandToShow 兩種情況、其他會議的條件）、`AppTests.SidebarKeysFollowFolderRenameAndDelete`（資料夾改名鍵跟著改、刪除資料夾時拿掉、段落的收合不受影響）。

### 11.35 重新命名的對話框（R102，M§76）

- 狀態、規則在 Services（`Renaming.cs`，同 Mac 的 `Store/RenameTarget.swift`）：`RenameTarget`（record：`Meeting(id)`、`Folder(名稱)`、`NewFolder(會議 id)`、`Person(id)`、`Bookmark(id)`；`Key` 同 Mac 的 `"\(self)"`：`meeting(42)`、`folder("客戶")`、`newFolder([1, 2])`）；`Renaming.CanSave`（名字空白、沒改不能存，新資料夾有字就能建立）、`Renaming.Conflict`（資料夾同名、人物同名大小寫不分時是合併）；`AppController.CurrentName`（從資料庫讀：沒取過標題的會議是檔名；人物、標記不靠畫面上的列表，Mac 驗收時人物列表沒打開過、輸入框是空的）、`CommitRename`（走原本的動作）。
- 對話框 `Dialogs.Rename`（`RenameDialog.cs`）。樣子同 M§76 的 `RenameSheet`（R102.1）：
  - 內容寬 392（`ContentDialog` 自己的內距 24）：上面一列是大標題（`Ui.DialogTitle`，22 號粗體：重新命名會議／資料夾／這個人／標記、新資料夾）和右上角的 ✕（`SidebarLook.HeaderIcon` 的 `E711`，`ToolTip` 和無障礙名稱「取消」（R106 起「關閉」、不能 Tab，§11.39），等於取消），標題下面一行灰字說明（14 號、`Ui.Secondary`，文字同 Mac：會議「保持簡短、容易辨識。日期和與會者在會議頁的「編輯」改。」、資料夾「資料夾裡的會議都會跟著改。」、新資料夾「例如專案名稱或客戶名稱。這場會議會移進新資料夾。」（好幾場時「這 N 場會議」）、「+」的新資料夾「例如專案名稱或客戶名稱。」、人物「所有會議裡的這個人都會跟著改。」、標記「12:34 的標記」）；再來是輸入框；右下兩顆圓角按鈕「取消」「儲存」（新資料夾「建立並移入」、「+」是「建立」）。
  - 輸入框（`RenameBox`）：細框、沒有強調色的焦點線：覆寫 `TextControlBorderBrush`（字色 25%）、`PointerOver`（40%）、`Focused`（75%），`TextControlBorderThemeThickness` 1、`Focused` 1.5（原本是底下一條 2 的強調色），底色字色 4%，圓角 10、字 15、內距 12×8。`PlaceholderText` 和無障礙名稱：資料夾名稱、名字、名稱。
  - 按鈕（`PillButton`，同 Mac 的 `SheetPillButtonStyle`）：圓角 10、高 34、最寬 68 起、左右 18。主要按鈕是字的顏色當底、視窗底色的字（淺色黑底白字、深色白底黑字），滑過 85%、按下 75%、停用 35%（覆寫 `ButtonBackground*`、`ButtonForeground*`、`ButtonBorderBrush*`）；取消是字色 6% 的底加 18% 的細框。
  - 為什麼不用 `ContentDialog` 內建的標題和按鈕：內建的兩顆按鈕撐滿一整排、主要按鈕在左邊、底下一條灰色的按鈕區，樣式只能整組換。所以標題、按鈕都放在內容裡，`PrimaryButtonText`、`CloseButtonText` 不設；按鈕區（範本裡的 `CommandSpace`，沒有按鈕時照樣是一條灰色的空白）在 `Opened` 時從視覺樹找出來收起。Enter（輸入框的 `KeyDown`）存、Esc（內容的 `KeyDown`）和 ✕、取消是 `Hide`，存了沒有自己記（`ShowAsync` 的結果都是 None）。
  - 名字空白、沒改時主要按鈕停用（Enter 也不會存）；輸入時說明即時換成合併的提醒：「已經有「X」，儲存後會合併成一個。」（從會議的選單建的新資料夾「…，會議會移進那個資料夾。」；「+」的新資料夾「已經有「X」。」而且「建立」停用，§11.37）。
  - 焦點：`ContentDialog` 打開後會把焦點給第一個能按的元件（右上角的 ✕，2026-11-17 虛擬機：F2 打開後焦點在 ✕、名字沒選起來），所以在 `Opened` 之後排一個低優先順序的工作讓輸入框拿到焦點、全選。關掉後焦點回到原本那一列（`MainWindow.RenameAndRefocus`：`ContentDialog` 自己還原時落到視窗的第一個元件、錄音按鈕；存了之後清單可能重建，等下一次重畫、排好版再找：原本的元件還在清單裡就用它，不然是清單選到的那一列、側欄同一個 AutomationId 的列）。這樣接著按 F2、Delete、方向鍵都對。
  - 對比主題：輸入框、按鈕都不覆寫（照平台的系統色；主要按鈕是 `AccentButtonStyle`）。
  - 同時只開一個（`Dialogs.RenamingTarget` 有值時其他重新命名不理；`ContentDialog` 同時只能一個，§11.2.1）。
- 會議列表的 Enter：`ListView` 自己會把列上的 Enter 標成處理過（2026-11-16 虛擬機：F2 打得開重新命名、Enter 沒反應，§11.33 寫的 Enter 一直沒作用），所以 `list` 的按鍵改用 `AddHandler(KeyDownEvent, …, handledEventsToo: true)`，只管焦點在列本身時（列裡的「⋯」按 Enter 是打開選單）。
- 儲存：會議 `UpdateInfo`（只改標題，日期、與會者照舊；改了會鎖住標題，整理時不再取名）、資料夾 `RenameFolder`（正在看那個資料夾時先換篩選，重讀時才不會回到所有會議；展開、排序的鍵跟著改）、新資料夾 `MoveToFolder`（「+」的空資料夾是 `CreateFolder`，§11.37）、人物 `RenamePerson`（同名時合併；人物模式時選改完的那個人）、標記 `RenameBookmark`。
- 用到的地方（原本各自的 `TextBox` 對話框、人物頁的 `Flyout` 都換成這一個）：會議（側欄右鍵、「⋯」、列表的 Enter／F2、展開的會議的 Enter／F2；§11.33 原本是打開會議頁的「編輯」，Mac 原本也是）、資料夾（右鍵、「⋯」、那一列的 F2、看資料夾時列表的 Enter／F2）、新資料夾（「移到資料夾 ▸ 新資料夾…」、多選的「移到資料夾」）、人物（人物頁名字旁的鉛筆、「更多」、側欄右鍵、Enter／F2）、重點標記（「重點標記 N」每個標記的子選單）。會議頁的「編輯」（標題、日期、與會者一起改）照舊。設定「認識的人」的重新命名照舊（設定是另一個視窗；Mac 也是在設定頁就地改）。
- DEBUG：`renameui <meeting|folder|person|bookmark|newfolder> <id、名稱或會議 id,…>`（同 Mac）；`renamemeetingui`、`personrenameui`、`bookmarkrenameui` 照舊、打開同一個對話框；state 的 `renameTarget`（沒開是空字串；取代 `renameMeetingRequest`、`renamingBookmark`、`personRenameOpen`，同 Mac）。
- 測試：`OrganizeTests.RenameRules`（能不能存、合併的名字、鍵）、`AppTests.RenameDialogReadsAndCommits`（目前的名字、會議沒改不存和鎖住標題、新資料夾、資料夾改名排序的鍵、人物合併、標記、空白不存）。

### 11.36 拖曳（R103，M§77）

- 拖的東西：會議（`DataPackage` 的自訂格式 `com.weiwei.shiyin.meeting-ids`，逗號分開的 id）、資料夾（`com.weiwei.shiyin.sidebar-item`，鍵 `folder:名稱`，同 `sidebarExpanded`）、檔案總管的檔案（`StorageItems`）。沒有放純文字：拖到記事本、檔案總管不會變成一段字。拖的內容同時記在主視窗（`draggingMeetings`、`draggingItem`）：`DragOver` 要知道是不是拖到自己上面（排序），`DataPackageView` 的資料只能非同步讀；放下、拖曳結束（`DropCompleted`、`DragItemsCompleted`）時清掉。是不是自己拖的看兩樣都有（記下的內容和 `DataView.Contains(格式)`）。
- 拖的來源：
  - 下面的會議列表：`ListView.CanDragItems`（只在會議列表；垃圾桶、搜尋結果、人物列表不能拖）、`DragItemsStarting`：拖的是選到的列時 ListView 本身就帶整個多選（R103.1），只有一場時再照多選補齊（`SidebarDrag.DragIds`，同 Mac 的 `dragIDs`）；沒有會議（分組的標題）就取消。項目直接是 `ListViewItem` 時 `e.Items` 裡是它的 `Content`（同 `scopes` 的 `ClickedItem`），要從 `list.Items` 找回那一列拿 `Tag`（2026-11-17 虛擬機：一開始照 `ListViewItem` 找，找不到就取消，會議一直拖不動）。可以拖曳時清單的選取在放開時才決定（按住 Ctrl 點選多選，Ctrl 要按到放開之後）。
  - 上面的資料夾、展開的會議：是 `SidebarItem`，左鍵按下不交給 ListViewItem（§11.34：交給它會選到這一列、清單捲走），ListView 自己的拖曳不會開始（同 Mac：List 裡沒有 tag 的列不能拖）。`SidebarItem.DragData` 有值時，按住左鍵移動超過 4（系統的拖曳門檻 SM_CXDRAG）就 `StartDragAsync`，`DragStarting` 填資料；沒移動就是原本的點一下（`Tapped`：展開、打開）。拖的圖是系統用那一列的樣子產生的（Mac 是名稱的圓角標籤）。
  - 不用 `CanReorderItems`：ListView 自己的重新排序會把段落標題、展開的會議、所有會議、人物都一起排，也只能排、不能「放進去」；排序只在「資料夾」那一段，自己算位置。
- 放的地方（`AllowDrop`；`DragOver` 設 `AcceptedOperation` 和說明文字 `DragUIOverride.Caption`，處理了就 `Handled`，不然往上到整個視窗的「拖檔案進來匯入」，R3.1）：
  - 資料夾列：會議 → `MoveToFolder`（說明「移到「客戶」」）；檔案 → 匯入到這個資料夾（`MainWindow.ImportInto`，同 Mac 的 `importDropped`：參考文件類的不收，全部是參考文件時說「參考文件請拖到會議頁，會加進那場會議」；逐字稿、封包直接匯入到那裡，錄音、影片跳原本的匯入對話框、資料夾先設好；說明「匯入到「客戶」」，`Copy`）；別的資料夾 → 排序。
  - 會議列表的標題列（`sectionHeader` 整條，`AppController.DropOnListHeader`，同 Mac 的 `dropOnListHeader`）：在看資料夾＝移進那個資料夾、「其他會議」＝移出資料夾（說明「移出資料夾」；都不在資料夾裡時「這場會議沒有在任何資料夾裡」）。看所有會議、人物、垃圾桶時不收（`SidebarDrag.HeaderAccepts`）。
  - 側欄下面的「垃圾桶（N）」：會議 → `MoveToTrash`（不確認，同 Mac 的 ⌘⌫ 和 Windows 右鍵的一場；可以還原，提示照舊）。
  - 拖到自己上面（排序）不能放。
- 回饋（R103.4）：會議、檔案拖到資料夾上時，那一列整塊青瓷綠 18% 的底（`SidebarItem.Backplate`，§11.34 的單色：拖曳經過時列是滑過的狀態，只改 `ListViewItem.Background` 會被滑過的灰蓋掉），圖示換成打開的資料夾；排序時看放開的位置在那一列的上半或下半，在上緣或下緣一條 2 的青瓷綠線（列的內容外面一層 `Grid`，`VerticalContentAlignment = Stretch`，線用負的邊距伸到整列；上緣的線常被系統拖曳的圖蓋住，下緣的看得到）。拖到列表的標題、垃圾桶時字換成青瓷綠。拖曳的說明文字（`DragUIOverride.Caption`）：「移到「客戶」」「匯入到「客戶」」「移出資料夾」「移到垃圾桶」（Windows 才有：檔案總管也是這樣說，Mac 沒有說明文字）；排序時不放說明和圖示。對比主題：淡淡的底看不出來（`Ui.Tint` 是透明的），改成一圈 2 的反白顏色的框（`ListViewItem.BorderBrush`），線、字是反白的顏色；拖曳經過的列本來就是平台滑過的反白底，看得出放在哪裡。
- 同時只亮一個地方（`ShowDropRow`、`ClearDropTargets`）：列的 `DragLeave` 不一定來（2026-11-17 虛擬機：拖過去經過的資料夾一直是青瓷綠、排序的線留在經過的列），所以記下亮著的那一列，換到別列、標題、垃圾桶時把前一個收掉；整個視窗的 `DragOver`（拖到沒有收東西的地方）、`DragLeave`、放下、拖曳結束時全部收掉。
- 排序（`SidebarDrag`，Services，同 Mac 的 `sidebarFolderItems`、`moveSidebarItem`）：settings.json 的 `sidebarOrder`（字串陣列，照順序、不排序；鍵同 `sidebarExpanded`）；`Sorted`：有在 `sidebarOrder` 的照它，沒有的（新的）排在後面、照名稱。放開時把拖的那個移到目標的前面或後面，整段的順序寫回（值沒變不寫）。資料夾改名（改成已經有的資料夾、合併時拿掉舊的，留那個的位置）、刪除資料夾時 `AppController` 跟著改。主視窗聽 `Settings.Changed` 的 `sidebarOrder` 重畫（樣子的比對加上順序）。
- 鍵盤、輔助使用：Mac 的 VoiceOver 有「往上移」「往下移」的自訂動作，UI Automation 沒有對應的東西，所以資料夾列有焦點時 Alt+↑／Alt+↓ 往上、往下移一格（焦點留在那一列；到頭了不動），無障礙說明（`HelpText`）是「按 Alt+↑、Alt+↓ 可以往上移、往下移」，螢幕閱讀器會念。
- DEBUG（同 Mac）：`importinto <資料夾> <檔案>`（等於從檔案總管把檔案拖到資料夾）；state 的 `sidebarOrder`（「資料夾」那一段實際的順序）、`showImportSheet`（匯入的對話框開著沒）。拖曳只能用真的滑鼠：`uia.sh drag <視窗> <來源> <目標> [top|center|bottom] [停留秒數]`（`mouse_event` 按下、絕對座標的移動（`SetCursorPos` 只搬游標，WinUI 的指標事件收不到）、先慢慢移過拖曳的門檻、分段移到目標、停一下（每 0.3 秒動 1 像素讓 `DragOver` 一直來）、放開；停留時可以 `shot.sh` 截拖曳中的樣子；清單裡沒捲到的項目先捲進畫面，還是沒有位置就不拖，不然會按在桌面的角落）。檔案總管的檔案也是同一個指令：來源寫「視窗標題::檔名」（`ShiyinDrop*::a.srt`），先把檔案總管拉到前面，拾音的側欄要露在外面。`uia.sh focus <視窗> <名稱>` 用 UI Automation 給鍵盤焦點（測 Alt+↑↓、F2），`uia.sh focused <視窗>` 看焦點在哪，`key` 多了 `alt`。
- 測試：`OrganizeTests.SidebarOrderMovesAndFollowsRename`（新的排最後、前面後面、往上往下移到頭了不動、改名換鍵和合併、刪除、拖的會議、標題收不收）、`AppTests.DropOnListHeaderMovesMeetings`。

### 11.37 新增空的資料夾（R104，M§78、D176）

- 資料：Shiyin.Data 的 migration v13（`folders` 表）、`CreateFolder`、`FolderNames`，`SetFolder`、`RenameFolder` 跟著改（fa12d60 跟 Mac 一起做，標準答案的 schema 一致）。
- 側欄的資料夾是資料夾表、會議的資料夾、自動加入條件三者的聯集（`Organizing.Folders(meetings, rules, names)`、`MainState.Folders`；`FolderNames` 跟列表一起讀），沒有會議的是 0 場；會議都移走了資料夾還在，要自己刪除（刪除時資料夾表那一列也拿掉）。正在看的資料夾不見了（刪除、改名）才回到所有會議。「移到資料夾」的選單、新資料夾的同名檢查、拖曳排序都照這個聯集。
- 「資料夾」那一段一直顯示；標題右邊「+」（`SidebarLook.HeaderIcon` 的 `E710`，`ToolTip`、無障礙名稱「新資料夾」；點按鈕不會收合，`SidebarItem` 不管從按鈕來的點擊）打開 §11.35 的對話框（`RenameTarget.NewFolder([])`）：標題「新資料夾」、說明「例如專案名稱或客戶名稱。」、主要按鈕「建立」；名字跟已經有的資料夾一樣時說明「已經有「X」。」、「建立」停用（`Renaming.CanSave` 的 conflict）。建立走 `AppController.CreateFolder`（同名的不建，提示「已建立資料夾「X」」），新的資料夾排在「資料夾」那一段的最後（§11.36 的排序）。沒有任何資料夾時，段落裡一行次要文字「按 + 新增資料夾」（停用的項目）。Windows 沒有選單列（Mac 的「檔案 → 新資料夾…」⇧⌘N 沒有對應）。
- DEBUG（同 Mac）：`foldernew <名稱>`；state 的 `folders`（名稱、場數，同 Mac）。
- 測試：`OrganizeTests.OrganizingHelpers`（資料夾表的名字也列出來、不重複）、`OrganizeTests.RenameRules`（「+」同名不能建）、`AppTests.RenameDialogReadsAndCommits`（「+」建立、提示、同名不建、會議都移走了還在）。

### 11.38 選單的樣子（R105，M§79）

- 用什麼做（評估後）：**平台的 `MenuFlyout` 加輕量的樣式覆寫**（`Ui/MenuLook`），不像 Mac 自己畫面板。Mac 自己畫是因為 macOS 的 NSMenu 換不了樣子（圓角、列高、底色、圖示顏色都固定）；WinUI 的 MenuFlyout 本來就是圓角面板、Segoe Fluent 的細線圖示（`Icon`）、右邊灰色的快捷鍵提示（`KeyboardAcceleratorTextOverride`，範本裡的 `KeyboardAcceleratorTextBlock`）、子選單的「›」（`E974`）、滑過一塊圓角底，子選單開在右邊（滑鼠移過去的延遲照系統的選單延遲），↑↓→←、Enter、Esc、點外面和切到別的 app 就關、UI Automation（`uia.sh rclick` 照樣按得到）、對比主題都由平台處理；差的只是尺寸和顏色，都有主題資源或屬性可以換（`MenuFlyoutPresenterStyle`、項目的 `Resources`、`CornerRadius`、`MinHeight`），不用換 ControlTemplate。自己用 `Popup` 畫的話鍵盤、焦點、無障礙、對比主題、螢幕邊緣、子選單的斜移都要自己做（winui-design 的「自己做元件」要全部做到），沒有必要。
- 樣子（數字照 M§79）：
  - 面板：`MenuFlyoutPresenterStyle`（`MenuLook.Presenter`，照深淺色快取：Style 用過之後不能改）圓角 14、不透明的底（淺色白；深色 #323232：Mac 是 white 0.17＝#2B2B2B，但 Windows 的側欄就是 #2B2B2B，開在側欄上只剩細框分得出來；平台是壓克力）、字色 10% 的細框、內距 1,3（加上每一項平台的外距 4,2，四邊各 5，同 Mac 的 padding 5）、最窄 200。陰影照平台。子選單的面板 WinUI 會套同一個 Style（MenuFlyoutSubItem 把 MenuFlyout 的 MenuFlyoutPresenterStyle 帶給子選單的面板）。
  - 每一項（`MenuFlyoutItem`、`MenuFlyoutSubItem`）：`MinHeight` 36（減掉平台上下各 2 的外距，滑過的底 32；平台用滑鼠打開時約 29）、圓角 8、滑過字色 9% 的底（按下 12%、子選單打開時 9%），鍵盤選到的是平台的焦點框（圓角跟著是 8）。字 14、圖示 16（平台的 Viewbox）照平台。快捷鍵提示、「›」是次要文字的顏色（紅色那一項的快捷鍵也是灰的，同 Mac）。左右的內距照平台（滑鼠打開時 11，範本的 NarrowPadding 動畫寫死的 StaticResource，覆寫不到；Mac 是 9）。
  - 分隔線：字色 10%、上下 4、左右縮進 8（`Padding` 8,4；平台是 -4,1，伸到項目外面）。
  - 刪除、移除這類照 §11.34 的 `Ui.DangerItem`（紅字紅圖示，滑過也是紅的）。
- 右邊的快捷鍵提示（`MenuLook.Hint`）：只是提示、不註冊快捷鍵（ContextFlyout 裡的 KeyboardAccelerator 在選單關著時也會生效，每一列都註冊一次會搶同一個鍵；同 Mac 不註冊的理由）。寫 Windows 的鍵（§11.33 的側欄鍵盤）：重新命名…「F2」（會議、資料夾、人物；會議、人物 Enter 也可以，但 F2 是 Windows 的慣例）、移到垃圾桶／把 N 場會議移到垃圾桶…／永久刪除…／刪除資料夾…／移除這個人…「Delete」。Mac 只標會議的重新命名、移到垃圾桶和人物的重新命名（↩、⌘⌫）；Windows 的資料夾列也有 F2、Delete，一起標。人物頁右上角的「更多」（同一份項目，`PersonPage.MenuItems(sidebar: false)`）不標：焦點不在側欄時 F2、Delete 沒作用。
- 用在哪裡（R105.1）：`RowMore.Button`／`RowMore.Context`（側欄的資料夾、展開的會議、人物，人物頁裡的會議，會議紀錄的每一條）和會議列表的右鍵（`MainWindow.RowMenu`）：打開時（`Opening`）填完內容後 `MenuLook.Apply`，建的時候也先套一次（面板在第一次打開前就建好）。工具列和頁面的「更多」、逐字稿的右鍵、錄音按鈕、顯示方式、設定的選單照舊是平台的樣子（R105.2）。
- 「⋯」打開的位置：`BottomEdgeAlignedLeft`（選單左上角對齊「⋯」的左下，同 Mac；原本 BottomEdgeAlignedRight），放不下時平台會換邊。右鍵照平台開在游標的位置。
- 對比主題：不覆寫（`MenuLook.Apply` 直接回傳），照平台的系統色（§11.3：滑過去是反白的底，自己的灰色會看不見）。
- 驗證：虛擬機 `uia.sh menukeys <視窗> <列> <鍵…> [right|more]`（真的滑鼠右鍵或點列右邊的「⋯」打開，同一個程序接著按鍵：下一個 PowerShell 會搶到焦點、選單被關掉），截圖（深色、淺色、高對比）。

### 11.39 彈窗、確認、就地編輯的共用樣子（R106，M§80）

- 元件在 `Ui/Sheet.cs`（同 Mac 的 `Views/SheetStyle.swift`），從 §11.35 的重新命名對話框抽出來（重新命名的樣子不變）：
  - `Sheet.Header(標題, 關閉, 說明, 右邊的小字)`：大標題（22 號粗體）、標題下面一行灰字說明（`Sheet.Subtitle`，14 號、次要文字、會換行）、標題列右邊的小字（「第 1 組，共 2 組」，13 號次要文字）、右上角 ✕（`SidebarLook.HeaderIcon` 的 `E711`，`ToolTip` 和無障礙名稱「關閉」，同 Mac；§11.35 原本是「取消」）。✕ 不能 Tab：`ContentDialog` 打開時會把焦點給第一個能 Tab 的元件（§11.35 踩過：焦點在 ✕），同 Mac 的 ✕ 沒有焦點，鍵盤用 Esc。
  - `Sheet.Field(TextBox, compact)`：§11.35 的 `RenameBox` 搬過來：字色 4% 的底、25% 的細框、滑過 40%、有焦點 75% 1.5（覆寫 `TextControlBorderBrush*`、`TextControlBorderThemeThickness*`；原本是底下一條 2 的強調色），圓角 10、字 15、內距 12×8。compact（就地編輯）圓角 8、內距 9×5，字照原本（會議內容跟著文字大小）。多行的、唯讀的也一樣。`Sheet.TextField(字, placeholder)` 建一個新的（placeholder 也是無障礙名稱）。
  - `Sheet.Combo(ComboBox)`：選單式的欄位（同 Mac 的 `SheetMenuField`）：同一種框（`ComboBoxBackground*`、`ComboBoxBorderBrush*`、`ComboBoxBorderThemeThickness`），圓角 10、字 15、高 38、撐滿欄寬；有鍵盤焦點時左邊那一小條（範本的 `Pill`，原本強調色）換成字色 75%。下拉的清單照平台。
  - `Sheet.Date(CalendarDatePicker)`：同一種框（`CalendarDatePicker*`），右邊的日曆圖示、打開的月曆照平台（Mac 要自己把沒有框的日期欄位放進框裡，Windows 的 CalendarDatePicker 本來就是一個框）。
  - `Sheet.Label`、`Sheet.Labeled(標籤, 欄位)`：欄位上面的灰色小標籤（13 號、次要文字，間距 6；欄位沒有無障礙名稱時用標籤的字）。原本用控制項的 `Header`（14 號主色字）都拿掉。`Sheet.Pair`：兩個欄位排一列、各一半（同 Mac 的 HStack 兩欄）。`Sheet.Box`：同一種框的一塊（勾選清單、好幾個檔時「依各檔案的檔名或修改日期」）。
  - `Sheet.Pill(字, Kind, compact)`：§11.35 的 `PillButton` 搬過來，多一種刪除類：`Primary` 字色當底、視窗底色的字（淺色黑底白字、深色白底黑字），`Destructive` 紅底白字（#C42B1C，Windows 的 critical 紅，深淺色同一個：`Ui.Danger` 深色模式是淡紅，配白字看不清；白字對比 5.9:1），兩種都是滑過 85%、按下 75%、停用 35%；`Secondary` 字色 6% 的底加 18% 的細框。圓角 10、高 34、最窄 68、左右 18；compact（就地編輯）圓角 8、高 26、最窄 52、左右 12、字 13。`Sheet.DangerText`：次要按鈕紅字（資料夾條件的「移除條件」）。`Sheet.Buttons(左邊, 右邊…)`：按鈕列，左邊一顆（「分享…」「移除條件」）、其他靠右，間距 10。
  - 對比主題：框、按鈕都不覆寫（照平台的系統色；主要、刪除類是 `AccentButtonStyle`），`Sheet.Box` 是一圈文字顏色的細框（§11.3）。
- `SheetDialog`：照這個樣子的 `ContentDialog`（§11.35 的做法搬過來）：內建的標題、按鈕都不用，內容寬 392（Mac 的 sheetChrome 440 減掉 ContentDialog 兩邊的 24）或 432（Mac 480），按鈕區（`CommandSpace`）在 `Opened` 時收起來。`ContentDialogMinHeight` 改 0（平台最小 184，一行說明的確認底下會多一塊空白）。
  - 深淺色：`RequestedTheme` 照主視窗（`Ui.IsDark`）。開著拾音時換了 Windows 的深淺色，ContentDialog 照樣是開 app 時的顏色（2026-11-17 虛擬機：換成深色後對話框是淺色的底、字是照深色給的白字）。對比主題時不設。
  - 焦點：打開後給 `Focus`（輸入框全選，`SelectOnFocus` 是 false 時不選）：排一個低優先順序的工作，350 毫秒後還不在它上面（使用者還沒按鍵）再給一次；而且 `Focus` 的 `TabIndex` 設 0。ContentDialog 打開後自己會把焦點給「第一個」能 Tab 的元件，有時候比排的工作晚（2026-11-17 虛擬機：清空垃圾桶的確認焦點落在「取消」，Enter 變成取消）。
  - 焦點留在對話框裡（`FocusManager.GettingFocus`）：選單裡的項目打開對話框時，選單晚一點關掉、把焦點還給對話框後面的「⋯」（2026-11-17 虛擬機：之後的 Enter、Esc 都送到側欄，對話框關不掉）。焦點要跑到主視窗裡的元件時，原本在對話框裡就留在原地，不然給 `Focus`／`Primary`；下拉選單、月曆在對話框自己的 popup 裡，不管。
  - Enter（ContentDialog 的 `PreviewKeyDown`，往下傳的時候就接住）：焦點不在按鈕（勾選框不算）、選單式的欄位、日期、多行的輸入框上時做 `Submit`；`Primary` 停用時不做（同 Mac 的 `.defaultAction` 停用時按 Return 沒反應）。不能等內容的 `KeyDown`：ContentDialog 自己也收 Enter（沒有內建按鈕時關掉、結果是 None），剛打開、視窗剛拿回前景、焦點還沒進到內容裡時按 Enter 就被它關掉了（2026-11-17 虛擬機：刪除資料夾的確認按 Enter 沒刪）。
  - Esc（內容的 `KeyDown`）、✕：`Escape`，沒給是關掉。`ContentDialog` 自己也會收 Esc（焦點不在內容裡時）：`Closing` 不是自己關的就取消、改做 `Escape`（目前沒有用到；D177 拿掉的可能的定期會議用過：Esc 是「略過」，不是關掉）。
  - 同時只開一個（`Dialogs.Show` 的 Gate，§11.2.1）。開著時 `Dialogs.OpenSheet` 是標題（DEBUG 的 state `sheet`）。
  - 標題列自己排兩欄（不用 `Ui.Bar`：左邊空的那一欄照樣多一段間距，標題會比欄位往右縮 12）。
- 確認（R106.2）：`Dialogs.Confirm(標題, 說明, 確認的字, destructive)` 改成 SheetDialog：標題、說明、✕，右下「取消」（次要）、確認（刪除類紅底，其他主要按鈕），寬 392、間距 22。打開時焦點在確認的按鈕（`Programmatic`：Enter 直接按它；Tab 到「取消」再 Enter 是取消）。**改了**：原本刪除類的預設按鈕是「取消」（不小心按 Enter 不會刪），R106.2 改成 Enter 是確認、Esc 是取消（同 Mac 的 `.defaultAction`）。
  - 刪除類（紅底）：刪除資料夾、移除這個人、移到垃圾桶（原本不是刪除類，同 Mac 改成紅底）、永久刪除、清空垃圾桶。分成兩場不是刪除類（原本是，同 Mac 改成主要按鈕；「分開後沒辦法合併回來」寫在說明）。麥克風權限的「打開設定」也走這裡（主要按鈕）。
  - Windows 的確認本來就是各自 `await`（`MainWindow.ConfirmTrash`、`ConfirmPurge`…），不用 Mac 的 `activeConfirmation`（Mac 是 SwiftUI 一個 `.sheet(item:)` 從狀態組出來）；DEBUG 的狀態（`deleteTargets`、`confirmPurge`、`confirmDeleteFolder`…）照舊。
  - 錯誤、錄音的提醒（`Dialogs.Alert`）照舊是系統的對話框（同 Mac 的 `.alert`）；設定視窗、更新、結束拾音的對話框照舊（R106 沒列）。
- 其他彈窗（R106.1、R106.3，同 Mac 的版面）：
  - 匯入（寬 432）：「匯入錄音」（好幾個「匯入 N 個錄音檔」）、檔名（灰字，前面 `E8D6` 的聲音圖示）、標題（一個檔時）、會議日期｜大約幾個人、與會者、範本｜會議語言，「取消」「匯入並開始轉錄」。
  - 匯出（寬 432）：「匯出「X」」、項目｜格式、包含時間點和包含說話者同一列（逐字稿時）、說明（還沒有會議紀錄時琥珀色）；左下「分享…」（次要；原本是 ContentDialog 的第二顆按鈕），右下「取消」「存檔…」。打開時焦點在「存檔…」（欄位都是選單式的，選單有焦點時 Enter 是打開它）。
  - 重新辨識說話者：說明放在標題下面、大約幾個人（寬 220）、灰字的提醒，「取消」「開始」。打開時焦點在「開始」（同匯出）。
  - 拆成兩句（Windows 才有的對話框，Mac 是就地拆）：說明「在文字裡點一下要拆開的地方」、唯讀的文字放進同一種框（有焦點時框變亮，打開時焦點在文字上、不全選）、預覽、後半句是，「取消」「拆開」（沒點在中間時停用）。
  - 編輯會議資訊（R106.3）：標題改「編輯會議資訊」（原本「編輯」）、說明「標題、日期、與會者。」，標題（打開時全選）、會議日期、與會者，「取消」「儲存」。Windows 本來就是對話框（Mac 原本是小視窗）。
  - 用詞修正（寬 432）：說明、錯誤寫法｜正確寫法（打開時錯誤寫法全選）、加進詞彙表、結果（青瓷綠，取代之後才出現），「取消」（取代過後是「完成」）「全部取代」（錯誤寫法空白或跟正確寫法一樣時停用；原本按了沒反應）。
  - 資料夾的自動加入條件（寬 432）：說明、標題包含、這些人都參加（同一種框裡的勾選清單，最高 140）、用這個範本、套用；左下「移除條件」（紅字的次要按鈕，有條件時；原本是第二顆按鈕），右下「取消」「儲存」。
  - 同一個輸入框換到新的容器（D177 拿掉的可能的定期會議，換一組時用過）：先從舊的那一欄拿下來，它還掛在舊的欄裡、`Parent` 卻是 null，要自己記著那一欄；直接放進新的欄 WinUI 丟 0x800F1000（2026-11-17 虛擬機：略過第一組後第二組只剩標題）。
  - 重新命名（§11.35）改用共用的元件（`SheetDialog`、`Sheet.TextField`、`Sheet.Pill`），樣子、行為不變，✕ 的名稱改「關閉」。
- 就地編輯（R106.4）：
  - 逐字稿改句子：同一種框（不是 compact 的框，字照會議內容的文字大小，同 Mac 的 `.sheet`），「取消」「儲存」compact。
  - 說話者改名的小視窗（逐字稿點說話者、會議頁上方的說話者）：標題「說話者名字」18 號粗體、輸入框（`Sheet.TextField`）、「不是他」「合併到…」「復原：…」次要、「儲存」主要（同 Mac 的小視窗用一般大小的按鈕），寬 320。
  - 紀錄的條目、待確認的回答（「是誰？」「正確的是…」「你的回答」）和「全部略過」「收合」「送出」、編輯會議紀錄（每個欄位、標題 18 號粗體、「取消」「完成」）：compact。
  - 提問的輸入列（這場會議、所有會議共用 `AskTab.InputBar`）：同一種框（一般大小）、「提問」主要按鈕。
- 頁面的提示條、空白頁的按鈕照舊（R106.5）。
- DEBUG：`dialogui <export|rediarize|editinfo|replace|split|trash|purge> <會議 id[,…]> [段落 id 或錯誤寫法]`（同 Mac 的 `exportui`、`preview editinfo|replace|rediarize`；確認的其他幾種照舊用 `folderdeleteui`、`personremove`、`confirmemptytrash`、`splitconfirm`，資料夾條件 `editfolderrule`、匯入 `importinto`、重新命名 `renameui`），state 的 `sheet`（開著的對話框的標題，沒開是空字串）。
- 驗證：虛擬機 `uia.sh` 打開每一種、截圖（深色、淺色、高對比），按 Enter／Esc（`uia.sh key`）、✕（`uia.sh click … 關閉`），看 state 的 `sheet` 和對應的狀態；從右鍵選單的項目打開（`uia.sh rclick … <項目>`、`menukeys … 28,0D`）再按 Enter／Esc；開著拾音時換深淺色再打開。

### 11.40 編輯會議：裁切、刪掉一段、分成兩場、合併兩場（R107，M§81、D178～D183）

- **共用的邏輯**跟 Mac 同一套，標準答案比對（§4）：
  - `Shiyin.Core/Model/EditPlan.cs`（片段、`Map`、`Snap`、`MapSentence`、`Source`、`Split`、`Keeping`、`RemapUserNotes`）、`MeetingNotes.MappingTimes`：`util/edit-plan.json`。
  - `Shiyin.Data/AppDatabase.Edits.cs`：migration v15（`meeting_edits`）、`ApplyEdit`、`RestoreEdit`、快照、合併時說話者的對法（人物 → 名字 → 聲紋，門檻是 `VoiceprintThreshold`：Windows 的聲紋模型不同，§9）、搜尋索引重建、30 天：`db/49～61`、`schema.sql`。比對時 `meeting_edits.snapshot` 寫成 `<snapshot>`、`edited_at` 是時間欄位（同 Mac 的 dumpTable）。
  - 快照的 JSON 照 Mac 的 `EditSnapshot`（Codable 的鍵、沒有值的選填欄位不寫、Data 是 base64、斜線跳脫）：同一份資料庫在 Mac、Windows 都能還原對方編輯的會議。
- **錄音**（`IAudioEditor`：Windows 是 Platform 的 `MediaFoundationAudioEditor`，測試用 Services 的 `AudioEdit.WavOnly`）：
  - 只有一場、錄音是 WAV：`AudioEdit.ComposeWav` 照位元組接（`fmt ` 區段原樣，`data` 照片段的 `floor(秒 × 取樣率)` 切，格式、聲道、位元數不變，逐個取樣相同；其他區段不帶）。同原本的 WAV 分割（§11.23）。片段超出錄音的部分當成沒有（同 Mac 的 composeWAV）。
  - 其他（m4a、匯入的 mp3、影片，或合併）：Media Foundation 解碼（`MediaFoundationPcmReader`，同 `MediaFoundationDecoder.OpenReader`），`PlanPcmStream` 照片段依序讀：取樣率取高的（NAudio 的 WdlResampler 換）、聲道取多的（最多 2，少的複製過去），沒有錄音、錄音比片段短的地方補安靜，長度一定是每段 `round(長度 × 取樣率)` 的和；壓成 AAC 的 m4a：取樣率不是 44.1／48 kHz 時升到 48 kHz（同壓縮、分割），位元率照來源的平均取 96～192 kbps 最接近的（WAV 的來源算 96）。檔名換成 `<id>.m4a`（D172）。
  - 測試（Mac 上）的 `WavOnly` 合併時寫成 16 位元的 WAV（Media Foundation 只有 Windows 有），其他格式說不支援。
  - 換檔：先發 `ReleasingAudio`（主視窗停止播放），第一次編輯時原本的錄音搬到 `audio\edits\<id>.<副檔名>`，之後的編輯舊的直接刪；新的搬回 `audio\<id>.<副檔名>`。`MediaPlayer` 不一定馬上放掉檔案：搬、刪都遇到 IOException 每 0.2 秒重試、最多 2 秒（同 §11.23）。換好發 `AudioReplaced`（主視窗重新載入播放器）。合併時複製參考文件的原始檔（`documents\<新 id>\<檔名>`）。
  - 還原：刪掉現在的錄音、從 `audio\edits\` 搬回來，編輯後加進來的參考文件連檔案刪掉。
  - 30 天：`AppController.PurgeExpiredEdits`，啟動時（清垃圾桶之後）和永久刪除會議之後跑：最後編輯超過 30 天的刪掉快照和錄音，`audio\edits\` 裡沒人用的檔一起刪。
- **擋下來、訊息、排重新整理**同 M§81.5（`AppController.EditProblem`、`ApplyingEdit`）：正在錄音、有排隊或進行中的工作（`HasActiveJobs` 或 `RunningJob`）、在垃圾桶裡、另一場正在套用時說原因；會議不見了不說。
- **編輯畫面是主視窗上的覆蓋層**（`Views/MeetingEditView.cs`、`Views/EditTimeline.cs`），不是 ContentDialog，也不是另一個視窗：
  - ContentDialog 同時只能開一個（§11.2.1），編輯畫面裡還要開套用、放棄的確認和選會議的彈窗；ContentDialog 開在覆蓋層上面，照常用 `Dialogs.Confirm`、`SheetDialog`。
  - 另一個視窗：WinUI 3 沒有附屬在主視窗上的模態視窗（`OverlappedPresenter.IsModal` 要自己用 Win32 設擁有者），主視窗還能點，會有 D183 說的衝突（同一場會議在主視窗被改名、移到垃圾桶、開始整理）；深淺色、文字大小、截圖都要另外處理。覆蓋層跟 Mac 的 sheet 一樣：蓋住整個主視窗，這段時間這場會議不會有其他操作。
  - 做法：`MainWindow` 的內容改成外層 Grid（原本的 root ＋ `editorLayer`）。覆蓋層是半透明的黑（淺色 35%、深色 55%），吃掉滑鼠；中間的卡片圓角 12、內距 24、細框和 `ThemeShadow`，淺色白、深色 #2B2B2B，大小跟著視窗（寬 720～1100、高 520～900，上下留 24、左右 48，視窗小的時候留 16 的邊）。卡片裡是一個 `ScrollViewer`（`MeetingEditView.Host`）：卡片夠高時跟沒有捲動一樣（內容的高度＝卡片），太矮時內容撐到逐字稿至少留 112、整張卡片上下捲，「取消」「套用…」不會被切掉（150% 縮放的 1080p 筆電視窗內容只有 640 左右高；2026-11-17 虛擬機 150%：逐字稿剩 0、下面的按鈕被切掉一半）。內容的高度在 `LayoutUpdated` 算（逐字稿以外每一列的 `DesiredSize` 加上 112，值變了才設），逐字稿照樣虛擬化。對比主題時卡片一圈字的顏色的框（12% 的灰框在變暗的主視窗上看不出邊）。`TabFocusNavigation = Cycle`：Tab 不會跑到後面的主視窗。開著時主視窗的快捷鍵（`root.KeyboardAccelerators`：Ctrl+Delete、Ctrl+R…）停用；root 的空白鍵播放、文字大小的按鍵在 root 底下，覆蓋層收不到，不用另外擋。打開時停止主視窗的播放、存好逐字稿的草稿，焦點給時間軸。
  - **開著時不重建會議頁**：`MainWindow.Refresh` 照樣更新側欄，右邊的會議頁先不重建（`detailStale`），關掉時補一次。覆蓋層底下重建的分頁列（`SelectorBar`：紀錄／逐字稿／提問、原文／精簡）會畫到覆蓋層上面、跟著每次重建跳來跳去（2026-11-17 虛擬機：下載模型時每個進度都 `MarkDirty` → 重讀會議 → 重建會議頁，使用者看到分頁列在時間軸上閃；拿掉卡片的 `ThemeShadow`、`Translation` 照樣會，不是陰影造成的）。文字大小改了也是關掉時才重建。
  - 開著時換深淺色、對比主題：用同一份編輯狀態（`MeetingEditor`：把手、刪掉的段落、選取、切點、合併、縮放、讀好的波形、選了的句子）重建畫面（`MeetingEditView.Rebuilt`），焦點給時間軸。畫面的顏色是建的時候決定的（平台的按鈕、`Sheet.Pill` 在對比主題另有一套），只重畫時間軸的話說明文字、按鈕會留在舊的顏色。讀到一半的波形由新的畫面接手（`peaksCancel` 共用）。
  - 排法同 Mac：標題「編輯會議」＋灰字（「標題・原本 58:10」，合併時加「（合併「X」00:59）」）、✕；時間軸；播放列（試聽、播放頭的時間、說明、說話者的圖例最多 4 位、縮放）；動作列（`Sheet.Pill` compact：設成開頭、設成結尾、刪掉選取的範圍、在這裡分成兩場／拿掉切點、合併另一場…／取消合併，放不下時換行）；逐字稿；摘要和「取消」「套用…」。
- **時間軸**（`EditTimeline`）：`ScrollViewer`（只有左右）裡一條 Canvas。上面一列標籤（開頭、結尾青瓷綠，切點琥珀色；合併時多一行每一場的名字），中間 132 高的軌道，下面刻度（間隔至少 70 點一格）。
  - 軌道由下往上：議題帶、波形、標記重點的菱形、裁掉的兩端（變暗）、刪掉的段落（變暗、斜線、虛線邊）、選取（藍色，淺色 #007AFF、深色 #0A84FF：把手是青瓷綠）、合併的分隔線、切點（琥珀色虛線）；再上面一層是按得到的東西：刪掉的段落中間的「刪掉 03:35・恢復」（窄的時候「↶ 恢復」或只有圖示）、切點的剪刀（按一下拿掉）、選取的長度、開頭和結尾的把手、播放頭。
  - 波形依說話者著色：每 3 點一條，同一個說話者、留不留同一組的長條放在一個 `Path`（`GeometryGroup`），只畫看得到的那一段前後各多一個畫面（2 小時的會議放大 40 倍有幾萬點寬，全部畫成元件太慢），捲動超出時再畫。峰值：`IAudioDecoder` 讀成 16 kHz 單聲道，每 0.05 秒一個（Mac 讀 8 kHz），背景讀，讀好前顯示「正在讀取錄音…」和句子的色塊。
  - 把手、播放頭在一層不重建的 Canvas 裡只搬位置（拖曳中重畫時拿下來再放回去，滑鼠的捕捉和焦點會不見）。其他東西（把手、刪掉的段落、選取、切點、縮放、合併）變了才整個重畫，試聽中每 0.05 秒只搬播放頭。
  - 把手、時間軸的表面是 `Adjustable`（`ContentControl` 加自己的 AutomationPeer，UI Automation 是 Slider 類型，名稱「開頭」「結尾」「播放位置」）：RangeValue 是秒數（UIA 的 SetValue 也能調，虛擬機驗證用），Value 是時間的字「00:12」（朗讀程式唸這個，不然唸「12.4」這種秒數；SetValue 吃「01:30」或秒數），說明（HelpText）是「00:12，剩下 02:15」「00:30／03:00」。唸出來的字變了才發 `PropertyChanged`（Value、RangeValue），按 ← → 時朗讀程式唸新的時間。刪掉的段落的按鈕名稱「刪掉的段落 12:00 到 18:50，按一下恢復」，切點的剪刀「切點 02:00，按一下拿掉」。把手的游標是左右箭頭，兩邊各多 4 點也抓得到。
  - 焦點：時間軸的表面拿到焦點時外面一圈框（另一層，不佔位置、不重畫）：用鍵盤（Tab 進來、按了 ← →）是 2 點字的顏色，滑鼠點、打開時程式給的焦點是 1 點淡灰（滑鼠用的人不用一直看到粗框；原本一律 1 點 35% 的灰，鍵盤用的人看不太出來）。把手拿到焦點時外框從 1 點 30% 的黑變成 2 點字的顏色。
  - 對比主題（2026-11-17 虛擬機「水生」「沙漠」巡檢，§11.3）：時間軸是視窗底色加一圈字的顏色的框；裁掉、刪掉的地方不蓋半透明的一層（系統色疊出來的就不是系統色），那裡的波形、議題、標記改用停用的字的顏色，刪掉的段落是停用的字的斜線、字的顏色的虛線邊；議題帶沒有底色、一圈框；選取沒有底色、2 點反白顏色的框，「選取 00:15」是反白的底配反白的字；把手是反白的顏色、握把是反白的字、焦點時外框字的顏色；剪刀的圓是字的顏色、圖示是視窗底色；「恢復」照平台的按鈕（原本自己的白底配上對比主題的白字，看不到字）；合併時每場的名字是 `Ui.Tinted` 的框；試聽鈕照平台的按鈕（有框）。
  - 滑鼠：在波形上拖曳（超過 4 點）選一段，點一下移播放頭、逐字稿捲到那一句；把手拖曳。縮放：播放列的放大鏡和滑桿（1～40 倍，對數），在時間軸上按住 Ctrl 滾滾輪（以游標為中心；Mac 是觸控板捏合，Windows 的精確式觸控板捏合也是送 Ctrl＋滾輪）；不用 Ctrl＋、Ctrl－（整個 app 的文字大小）。縮放後播放頭留在畫面中間，試聽中、放大時播放頭跟著捲。
- **鍵盤**（R107.9）：焦點在時間軸上 ← → 移播放頭 1 秒（Shift 10 秒）、Delete／Backspace 刪掉選取的範圍；把手上 ← → 0.1 秒（Shift 1 秒）。整個編輯畫面（`PreviewKeyDown`）：空白鍵試聽、Enter 套用、Esc 取消，焦點在按鈕上時空白鍵、Enter 照按鈕自己的（Esc 照樣是取消），在輸入框時不攔。逐字稿選了句子時 Delete 刪掉那幾句（都刪掉了就恢復；Windows 才有，清單選好幾項按 Delete 是 Windows 的習慣）。
- **逐字稿**：`ListView`（虛擬化，同 §11.2 的寫法），`SelectionMode = Extended`：Ctrl、Shift 點選是 Windows 的多選（Mac 是 ⌘、⇧），點一下（沒按 Ctrl、Shift）從那句播放。每一列：時間（青瓷綠）、說話者（顏色點、名字）、文字（最多兩行；刪掉、裁掉的變灰、刪除線），滑過或選到的那列右邊一顆 compact 按鈕（刪掉／恢復／從這句開始／到這句結束），播放到的那句淡淡的底，試聽中跟著捲。右鍵（打開時才建，對象是選取的那幾句，同 Mac）：從這裡播放、刪掉這句／這 N 句、恢復這句／這 N 句、從這句開始（設成開頭）、到這句結束（設成結尾）、從這句分成兩場。列的無障礙名稱是時間、說話者、文字，刪掉的加「（刪掉）」。說明改寫「按住 Ctrl、Shift 選好幾句」（Windows 才有的字串）。列的範本（`DataTemplate` 的 `ContentControl`）`IsTabStop=False`：不然 Tab 在列和下一個按鈕中間多停一個看不到、沒有名字的地方（UI Automation 只讀到視窗的 InputSite；主視窗逐字稿分頁的範本同樣改掉）。刪掉句子後（按鈕、右鍵、Delete）編輯狀態清掉選取（同 Mac），清單的選取也一起拿掉，不然看起來還選著、再按 Delete 沒反應。對比主題：列裡的字跟著列的前景色（`Ui.FollowForeground`，選到的列是反白的底配反白的字；原本青瓷綠的時間在反白的底上看不見），說話者的點不畫（都是字的顏色），播放到的那句是一圈反白顏色的框。動作列「設成開頭」「設成結尾」的直線用 `PathIcon`，跟著按鈕的字色（停用、對比主題）。
- **試聽**（`EditPreview`、Platform 的 `EditPreviewSource`）：Mac 用 AVMutableComposition 把片段接成一條；Windows 用 `MediaStreamSource`（同 `MixedAudioSource`）＋`PlanPcmStream` 依序讀片段，交給 `MediaPlayer`。混成單聲道、取樣率取高的（最高 48 kHz）、每次 0.1 秒 16 位元 PCM；時間是新時間軸上的秒數，0.05 秒一次照 `EditPlan.Source` 換回時間軸的播放頭。播放頭在刪掉、裁掉的地方時從下一個留下的地方開始（`PreviewStart`，同 Mac）。改了把手、刪掉的段落、切點、合併時先停止；移播放頭時從新的位置接著播。沒有音效裝置時 `MediaPlayer` 丟例外就不播（同 `Player`）。DEBUG 的 `--set debugMutePreview=YES` 不出聲、不用 `MediaPlayer`，照真的時間走（虛擬機沒有音效裝置，`MediaPlayer` 不會前進；同 Mac 的 `-debugMutePreview`）。
- **合併另一場**（`Dialogs.MergePicker`，`SheetDialog` 寬 472＝Mac 的 520 減兩邊）：標題、說明、搜尋欄位（打開時焦點在這裡）、`ListView`（高 300）分「前後三天」「更早或更晚」兩段（段落標題是停用的項目），每一場是標題、「日期・長度・資料夾」，選到的那場右邊打勾，會議語言不同時多一行琥珀色的「兩場的會議語言不同，合併後照這場的語言整理」；「取消」「合併」（沒選時停用），Enter、雙擊也是合併。選了之後時間軸接上另一場（同 Mac：這場的裁切變成刪掉的段落、把手在整條的兩端），「取消合併」回到只有這場。
- **確認**：`Dialogs.Confirm` 的說明是「・」開頭的好幾行時畫成條列（點一欄、文字一欄，換行對齊在點的後面，同 Mac 的 ConfirmSheet）。套用（「套用這些改動？」，主要按鈕「套用」）、放棄（「放棄這些改動？」，紅底「放棄」）、還原（「還原成編輯前的樣子？」，主要按鈕「還原」）。
- **入口**（R107.1）：會議列表的右鍵和「⋯」（`MainWindow.EditMenuItems`）：「重新命名…」下面「編輯會議…」（剪刀），編輯過的會議再多「還原成編輯前的樣子…」，右邊灰字「還有 N 天」（`MenuLook.Hint`）。會議頁的「更多」：「重新處理」下面同樣兩項。**跟 Mac 不同**：Mac 工具列的選單建立一次就不更新，所以「還原」一直在、沒編輯過時按了說明；Windows 的「更多」每次打開時重建（`Opening`），沒編輯過的不列「還原」（同設計稿）。
- **會議頁上方**：`ApplyingEdit` 是這場時狀態列是轉圈和「正在套用編輯…」（`StatusKey` 加這個狀態，換成別的時才重建）。
- **DEBUG 控制通道**（同 Mac 的名稱，§15.3）：`editmeeting <會議 id> keep=<開始>-<結束>[,…] [split=<秒>] [merge=<會議 id>[:<開始>-<結束>,…]]`（不經過畫面和確認）、`restoreedit <會議 id>`、`confirmrestoreedit <會議 id>`（打開還原的確認）、`editage <會議 id> <天數>`（把最後編輯的時間改早，接著清一次過期的）、`openedit <會議 id>`、`editorstate start= end= del= sel= cut= playhead= zoom= merge= lines= play apply cancel`（直接改打開的編輯畫面；`apply`、`cancel` 等於按「套用…」「取消」，會打開確認）。`state` 加 `applyingEdit`、`editingMeeting`、`editor`（把手、刪掉的段落、選取、切點、片段、試聽的時間、波形讀好了沒）、`meetingEdit`（選取中的會議的快照：錄音、剩幾天、分出去、合併進來的會議）、`mergePickerOpen`、`confirmRestoreEdit`。
- 驗證：Mac 上 `dotnet test`（標準答案；`MeetingEditTests` 的裁切加刪段、合併、裁切加分成兩場的套用和還原逐欄比對、錄音逐個取樣比對、30 天清除、擋下來、`PlanPcmStream` 的換取樣率和補安靜、編輯畫面的狀態和說明文字）；虛擬機用控制通道、UI Automation、真的滑鼠和鍵盤（`uia.sh`）操作，截圖淺色、深色、英文。`uia.ps1` 多了 `dragat <視窗> <名稱> <橫向的位置 0～1> <往右幾個像素> [ctrl|shift]`（真的滑鼠在元件裡按下、橫著拖、放開；0 是點一下：拖把手、在波形上選一段、點波形移播放頭）、`range <視窗> <名稱> [值]`（讀、設 RangeValue）、`key` 的 `shift`。2026-11-17 輔助使用巡檢：150%（桌面 1352×908，主視窗最小時的卡片）、對比主題「水生」「沙漠」、只用鍵盤（Tab 一圈、空白鍵、← →、Shift、Delete、Enter、Esc，逐字稿 ↓、Shift+↓、Delete）、UI Automation 的名稱、值和說明；開著時換對比主題、深淺色；下載模型時打開編輯畫面、試聽、拖曳選取，連拍確認分頁列不會跑到上面。
- 跟 Mac 不同、還沒在真的 Windows 電腦上確認的：試聽真的出聲音時跳過刪掉的段落聽起來對不對（虛擬機沒有音效裝置，用 `debugMutePreview` 的時鐘驗播放頭）；精確式觸控板的捏合（虛擬機只送得出 Ctrl＋滾輪）；朗讀程式真的唸出把手、播放位置（UI Automation 讀得到名稱、時間、說明，但虛擬機裡用 UIAutomationClient 聽不到 PropertyChanged，連平台的 Slider 也聽不到，按 ← → 時唸不唸新的時間要真的開朗讀程式聽）；分頁列畫到覆蓋層上面在真的顯示卡上會不會發生（虛擬機是軟體繪圖；不重建會議頁之後兩邊都不會再觸發）。時間軸的縮放滑桿唸的是對數的值（平台的 Slider），沒改。

### 11.41 設定：左邊導覽十一項（R108.7，M§82、D184）

- **版面**：`NavigationView`（`PaneDisplayMode = Left`，拿掉收合鈕、上一頁、內建的「設定」，`OpenPaneLength` 248），十一項分四組，組和組之間 `NavigationViewItemSeparator`（Mac 是組和組之間空一點）。右邊：上面是那一頁的名稱（24 號粗體，不跟著捲，同 Windows 設定），下面捲動的內容（左右 28 的邊，靠上：只有一張卡片的頁面不會被拉到視窗底）。代號、順序、分組、名稱、搜尋的關鍵字在 `Shiyin.Services/SettingsPages.cs`（測試讀 Mac 的 `SettingsView.swift` 比對代號和分組）。
- **每一頁**（R108.6：原本四頁的段落照搬，項目、存的鍵、說明文字不變；只有一段的頁面不再放段落標題，同 Mac）：

| 頁（代號） | 內容（原本在哪） |
|---|---|
| 整理工具（provider） | 原「整理工具」去掉範本 |
| 範本（templates） | 原「整理工具」的範本 |
| 錄音（recording） | 錄音來源、提醒與快捷鍵（原「錄音與轉錄」） |
| 轉錄（transcription） | 轉錄（正式轉錄、說話者辨識、即時逐字稿）、語言（原「錄音與轉錄」，順序同 Mac） |
| 模型與空間（models） | 模型清單（原「錄音與轉錄 → 轉錄」，含顯示卡加速的元件）、儲存空間（原「進階」） |
| 說話者（speakers）、詞彙表（glossary） | 原「說話者與詞彙」的兩段 |
| 一般（general） | 介面（介面語言）、顯示、其他（重新顯示首次使用引導、重建搜尋索引）、匿名使用統計（原「進階」） |
| AI 工具（ai）、企業伺服器（enterprise） | 原「進階」的那一段 |
| 檢查與更新（status） | 更新（目前版本、檢查更新）、其他（回報問題…、關於拾音）（原「進階」） |

- **圖示**：Segoe Fluent Icons 的字（整理工具 E8BD Message、範本 E8A5 Document、錄音 E720 Microphone、轉錄 E9D9 Diagnostic、模型與空間 EDA2 HardDrive、說話者 E716 People、詞彙表 E82D Dictionary、一般 E713 Setting、AI 工具 EA86 Puzzle、企業伺服器 E825 Bank、檢查與更新 E895 Sync），上 Mac 那一項的顏色，**沒有底色方塊**：Windows 11 的設定是彩色的圖示加文字，WinUI 的 `NavigationViewItem` 本來也是一個圖示加文字；Mac 的彩色方塊是 macOS 系統設定的樣子。淺色調深、深色調亮，在導覽的底色上對比 3.8:1 以上（Mac 的橘、青、綠直接放在淺色底上不到 3:1）。對比主題時不上色，跟著項目的文字顏色。開著設定時換深淺色、對比主題：圖示重新上色、這一頁重畫（原本設定開著時換主題，自己的顏色要重開視窗才換）。
- **底色**：導覽是主視窗側欄的顏色（`Ui.SidebarBackground`，淺色 #F7F7F7、深色 #2B2B2B），右邊是 NavigationView 自己的內容層（左上角圓角、細框），卡片照舊。
- **搜尋**（R108.4）：`NavigationView.AutoSuggestBox`（導覽上方，無障礙名稱「搜尋設定」，沒有建議清單）。打字時只留名稱或關鍵字對得上的項目（`SettingsPages.Find`，不分大小寫），分隔線只留在兩組看得到的項目中間；只剩一項時直接選它；Enter 選第一項。關鍵字同 Mac，只列 Windows 有的項目：Mac 才有的系統錄音權限、說話者辨識的模型、環境檢查不列；Windows 才有的介面語言、顯示卡加速、回報問題、關於拾音加上。選到的那一項被篩掉、再出現時，NavigationView 的選取條不見了（2026-11-17 虛擬機），排好之後重新選一次。
- **鍵盤**：導覽 `SelectionFollowsFocus`：↑ ↓ 換頁（同 Windows 設定、Mac 的側欄；預設只移焦點，要再按 Enter 或空白鍵）。打開設定時焦點在搜尋框，Tab 到選到的那一項、再 Tab 到那一頁的內容。打開時、從別的地方指定頁面時，導覽捲到看得到選到的那一項（`StartBringIntoView`：150% 縮放、視窗矮的時候「檢查與更新」在捲動範圍外）。導覽每一項的 UI Automation 名稱是頁名。
- **對比主題**（2026-11-17「水生」「沙漠」十一頁巡檢）：範本清單選到的那一列「預設」跟著列的前景色（`Ui.FollowForeground`；次要文字的顏色在反白的底上看不見），其他照 §11.3。
- **記住頁面**（R108.5）：`settings.json` 的 `settingsPage`（鍵、值同 Mac 的 UserDefaults），換頁就記（值變了才寫）；存的不是十一頁的代號時是整理工具。
- **視窗**：900×660（Mac 820×620），最小 720×500（同 Mac）、可以拉大。比 Mac 大：Windows 的下拉選單、開關的標題在控制項上面、字 14（Mac 13），同樣的內容要比較高；導覽 248 比 Mac 的側欄 220 寬（上面有搜尋框）。最小的時候範本的編輯欄很窄（範本清單固定 200 寬），照樣能用。
- **跟 Mac 不同的地方**：
  - 一般多了「介面」（介面語言，D57）；檢查與更新沒有環境檢查、更新網址（Windows 本來就沒有），多了回報問題…、關於拾音（Windows 沒有選單列，原本放在進階的「其他」）。轉錄沒有「說話者辨識的模型」（Windows 沒有 Nemotron）；模型與空間沒有「第二版留下的檔案」。
  - 從別的地方打開設定時指定頁面（`App.ShowSettings(頁面)`）：會議頁的「設定整理工具」「打開設定」打開整理工具（公司的伺服器沒登入時打開企業伺服器，`SummarizerProblem.SettingsPage`）；企業伺服器的加入連結、瀏覽器登入完回來打開企業伺服器（原本是進階）。設定會記住上次的頁面，不指定的話會停在別頁。Mac 的 `openSettings()` 不能指定頁面，目前停在上次那頁。
  - 說明文字、使用說明裡的舊頁名（「設定 → 錄音與轉錄」「進階 → 企業伺服器」）照 R108.6 不改，同 Mac。
- **DEBUG**：`open settings [頁面]`（代號、0～10、4.8 以前的 `names`／`advanced`／`storage` 當別名；沒給頁面時停在上次那頁）、`closewindow settings`、`settingssearch [文字]`（等於在搜尋框打字，沒給文字是清掉；回傳留下的頁面）；state 的 `settingsPage`（開著時目前的頁面）、`settingsSearch`、`settingsVisible`（導覽看得到的頁面）、`settingsPageStored`（settings.json 記住的）。
- 驗證：Mac 上 `dotnet test`（`SettingsPagesTests`：代號和分組同 Mac、搜尋「麥克風」「API key」「快捷鍵」各只剩一頁、英文的名稱和搜尋、關鍵字都是設定視窗裡的字、舊頁名、記住的頁面寫進 settings.json）；虛擬機用 UI Automation 和真的滑鼠點導覽換頁、`uia.sh set 設定 '搜尋設定#1' <字>` 搜尋（同名的「搜尋設定」有三個元件，`#1` 是輸入框）、`uia.sh close` 關掉再按主視窗的齒輪、重開 app 停在上次那頁，`snapwin.ps1 place` 拉大、縮到比最小還小，淺色、深色、英文截圖。2026-11-17 輔助使用巡檢：150%、對比主題「水生」（十一頁）「沙漠」、只用鍵盤（搜尋框 Tab 到導覽、↑ ↓ 換頁、Tab 進內容、搜尋「轉錄」按 Enter 選第一項）、導覽的 UI Automation 名稱；停在「檢查與更新」關掉重開，頁面不會被 ↑ ↓ 換頁改掉、導覽捲到那一項。

## 12. 系統整合

### 12.1 單一執行個體、開檔

- `AppInstance.FindOrRegisterForKey`：第二次啟動把參數（要開的檔案）轉給已經開著的拾音再結束。
- 安裝時在 `HKCU\Software\Classes` 註冊 `.shiyin` 的預設程式、音訊和影片的「開啟檔案」清單（Velopack 安裝、解除安裝的 hook）。

### 12.2 通知（R11.2、R19.4）

- 用 Windows 本身的 `ToastNotificationManager`（D62），不用 Windows App SDK 的 AppNotification：AppNotification 依賴 Windows App SDK 的 Singleton 套件，自帶部署、沒打包的 app 註冊時就失敗（`Register()` 回 0x8007007E）。
- 通知用的 AppUserModelID `Weiwei.Shiyin` 登錄在 `HKCU\Software\Classes\AppUserModelId\Weiwei.Shiyin`（`DisplayName` 依 Windows 語言是「拾音」或「Shiyin」、`IconUri` 是安裝資料夾裡的 `Assets\Shiyin.png`），每次啟動都寫一次（路徑變了也跟著改）；不改程序本身的 AppUserModelID（工作列跟 Velopack 的捷徑同一組）。
- 內容：完成、失敗、偵測會議（按鈕「開始錄音」「不要再提醒這個 app」；「停止並轉錄」「繼續錄」）。點通知、按按鈕都是 `shiyin:` 網址（`HKCU\Software\Classes\shiyin`，`URL Protocol`），Windows 會用網址當參數開拾音，第二個拾音把參數轉給已經開著的那個（§12.1）後結束；拾音沒開時就直接開起來處理。
  - `shiyin:open?meeting=<id>`：打開那場會議。
  - `shiyin:prompt?action=start|mute|stop|continue&app=<id>&token=<權杖>`：權杖是每次啟動隨機產生、只放記憶體，對不上時（網頁裡的連結、上次啟動留下的舊通知）只打開主視窗，不開始錄音、不停止錄音。
- 看通知有沒有被關掉：先讀 `ToastNotifier.Setting`；它丟出例外時（剛登錄 AppUserModelID 的那次啟動會）改看 Windows 存使用者設定的登錄（`PushNotifications` 的 `ToastEnabled`、`Notifications\Settings\Weiwei.Shiyin` 的 `Enabled`）。關掉時偵測會議改用 §11.1 的小視窗（同 Mac 的退路）。測試時要從「設定 → 系統 → 通知」關，直接改登錄不會馬上生效（通知平台有自己的狀態）。
- 解除安裝時（Velopack 的 hook）刪掉 AppUserModelID、`shiyin:` 的登錄，清掉通知中心裡拾音的通知。
- exe 的路徑要固定：Velopack 的 `current\` 資料夾每次更新都是同一個路徑。

### 12.3 全域快捷鍵（R11.3、R19.5）

`RegisterHotKey`：Ctrl+Alt+Shift+R 開始或停止錄音、Ctrl+Alt+Shift+B 標記重點（只在錄音中註冊）；`MOD_NOREPEAT`。不用 Win 鍵（Win+Alt+R 是 Xbox Game Bar 的錄影，Win 的組合多半被系統佔用）。被別的 app 佔走時回 `ERROR_HOTKEY_ALREADY_REGISTERED`，設定頁顯示原因（等於 Mac 的獨佔註冊）。

### 12.4 偵測會議（R19.4，D55）

- 讀 `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone`：封裝的 app 用套件家族名稱當子鍵（新版 Teams 是 `MSTeams_8wekyb3d8bbwe`），桌面 app 在 `NonPackaged\` 下、路徑的 `\` 換成 `#`（例如 `C:#…#Zoom.exe`）；`LastUsedTimeStop = 0` 表示正在用。這是 Windows「麥克風使用中」指示的資料來源，涵蓋封裝的新版 Teams。
- 照 Mac 每 2 秒讀一次（`Shiyin.Platform.Windows/MicrophoneUsage.cs`，計時器在 `Services/MeetingDetector.cs`）；拾音自己不算。
- app 清單（`MeetingDetector.MeetingApps`、`Browsers`，小寫比對；封裝的 app 比對套件家族名稱的前綴）：zoom.exe、新版 Teams（`msteams_8wekyb3d8bbwe`、ms-teams.exe）、舊版 teams.exe、Webex（ciscocollabhost.exe、webex.exe、atmgr.exe）、slack.exe、line.exe、discord.exe；瀏覽器 chrome.exe、msedge.exe、firefox.exe、brave.exe、Arc（`thebrowsercompany.arc`）、opera.exe、vivaldi.exe，顯示成「Chrome（可能是 Google Meet）」。真的電腦上還沒確認（tasks 的「偵測會議」）。
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

`Shiyin.Platform.Windows/SystemInfo.cs`：Windows 版本和 build（登錄 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` 的 CurrentBuild、UBR、EditionID、DisplayVersion，build 22000 以上是 Windows 11；加上系統架構）、CPU 名稱（登錄的 ProcessorNameString）、記憶體（`GlobalMemoryStatusEx`）、顯示卡（顯示卡驅動的登錄 `DriverDesc`，不用 DXGI）、whisper 用的顯示卡（shim 的 `shiyin_gpu_device`：ggml 第一個 GPU 裝置的名稱和說明，例如 `CUDA0 (NVIDIA GeForce RTX 3060)`、`Vulkan0 (AMD Radeon 780M Graphics)`；沒有就是「CPU」），其他同 M§21.9。

最近失敗的工作的錯誤種類（`jobs.error_kind`，`ErrorSummary.LogSummary`，R54.4）：整理工具是 `LLMError.<種類>`（同 Mac）；轉錄是 `WhisperError.modelMissing`、`loadFailed`、`failed(<whisper 的錯誤代碼>)`、`cancelled`、`empty`、`qwenFailed(<HTTP 狀態>)`；Windows 系統元件的錯誤（`ExternalException`：COM、Media Foundation、Win32）寫型別和 HRESULT（`COMException 0xC00D36C4`）；其他寫型別名稱。都不含訊息（訊息可能有逐字稿）。Mac 是 `<domain> <code>`，不改（R26.5）。

### 12.8 設定

`SHIYIN_HOME\settings.json`，鍵名跟 Mac 的 UserDefaults 相同（`llmProvider`、`transcriptionEngine`…）。寫入時先寫暫存檔再取代，避免寫到一半。DEBUG 可以用啟動參數覆蓋單一設定（`--set llmProvider=none`），只在這次執行有效，同 Mac 的 `-llmProvider none`。

### 12.9 log

`logs\shiyin-<日期>.log`，保留 7 天；不寫逐字稿和會議紀錄的內容、不寫 key（同 Mac 的規則）。

### 12.10 匿名使用統計（R40.7，M§30）

累計什麼、什麼時候送、送出的格式、`usage.json` 的內容和保留規則都同 M§30.1～30.4。不同的地方：

- 程式：`Shiyin.Services/UsageStats.cs`（有鎖，任何執行緒都能呼叫）。AppHost 建一個，交給 `JobRunner`、`AppController`、`AskService` 的 `Usage` 屬性和 `Importer.ImportFile` 的參數；沒給（測試）就不累計。掛的位置照 M§30.1 的表：`JobRunner.Execute`（工作結束）、`JobRunner.Transcribe`、`AppController.RecordingFinished`、`Importer.ImportFile`、`AskService` 存問答之後。
- 開關：`settings.json` 的 `usageStats`（預設開，同 Mac 的鍵名）。
- 統計網址：打包時寫進 app。`pack.ps1` 讀環境變數 `SHIYIN_USAGE_URL`，用 `"-p:ShiyinUsageUrl=…"` 傳給 `dotnet publish`，`Shiyin.App.csproj` 把它寫成組件的 `AssemblyMetadata("ShiyinUsageURL", …)`；`release-windows.yml` 的打包步驟帶 GitHub 的 repository variable（跟 Mac 同一個）。沒帶就是不送統計的版本。DEBUG 版不讀組件裡的網址，只在控制通道的 `usageurl` 設在記憶體時累計和送（同 M§30.7）。
- `app.os`：`Windows 11 24H2 (26100.4652)`（名稱、DisplayVersion、build.UBR，不跟著介面語言）；`app.chip`：登錄的 ProcessorNameString（同 §12.7）；`app.uiLanguage`：拾音實際用的介面語言，`zh-Hant` 或 `en`（同 Mac 的寫法）。`app` 的字串超過 64 字就截斷（後台只收 64 字以內，被拒的那一天就丟了；Snapdragon 的 CPU 名稱接近這個長度）。
- 送出：用 AppHost 的 HttpClient，每個請求 20 秒逾時；啟動 60 秒後、之後每小時（同 M§30.3）。
- 設定 → 進階的最下面（「其他」之後）一張卡片：「匿名使用統計」和「詳細…」；按下去是對話框（ContentDialog）：開關、說明、「重設匿名 ID」、「完成」（不顯示 JSON）。使用說明「資料與隱私」加一條。文字和行為同 M§30.5，只在有統計網址時顯示（DEBUG 一律顯示）。
- DEBUG 控制通道：`usageurl <網址|off>`、`usage <輸出.json>`、`usageage <天數>`、`usagesend`，同 Mac 的 shiyinctl。虛擬機裡驗證時，假後台（`scripts/dev/fake-usage-server.py`）開在 Mac 上，用 `ssh -R` 讓虛擬機連 `http://127.0.0.1:<埠>`（同 §14.2 測更新）。

### 12.11 企業伺服器（R49～R52，M§36）

- API、流程、錯誤分類、隱私說明同 M§36。程式：`Shiyin.Services/EnterpriseServer.cs`（info、登入、換 token、HTTP、`ISecretStore`）、`Shiyin.Services/ServerTranscriber.cs`（寫 16-bit WAV、建工作、續傳、等結果、解析結果）、`Shiyin.LLM` 的 `EnterpriseApi`（包 OpenAI 相容那一個，key 換成 access token；LLM 專案不能參照 Services，用 `IEnterpriseAccess` 介面拿 token）。都在 net10.0，Mac 上 `dotnet test`（`EnterpriseTests` 用測試裡的假伺服器）。
- HTTP：企業伺服器的請求用自己的 HttpClient（`EnterpriseServer.NoRedirectClient()`，`AppHost.EnterpriseHttp`），`AllowAutoRedirect = false`，收到 3xx 當成錯誤（M§36.2、D126）；整理（`EnterpriseApi`）用同一個（`IEnterpriseAccess.Http`）。`AppHost.Http`（下載模型、更新）照舊跟著轉址。伺服器的回應型別不對（例如 `apiVersion` 是字串）不能讓 `GetValue` 丟出 `InvalidOperationException`：`FetchInfo`、換 token 當成「不是拾音的企業伺服器」，錯誤的 `kind`、`message` 不是字串就當成沒有。
- 登入：設定頁按「登入」→ `BeginSignIn` 檢查網址 → `Launcher.LaunchUriAsync` 用預設瀏覽器打開 `/login`，`redirect_uri` 是 `shiyin://auth`。回來的網址走已經登錄的 `shiyin:` 網址（§12.2）：第二個拾音把網址轉給開著的那個（§12.1），`App.OpenLinks` 看到 `shiyin://auth` 就交給 `AppHost.CompleteEnterpriseSignIn`（不是通知的網址）。`state` 只放記憶體、10 分鐘有效，對不上就忽略。登入完套用預設（轉錄、整理都用企業伺服器，R49.3）、打開設定的「進階」、顯示「已登入企業伺服器」。
- token：認證管理員 `com.weiwei.shiyin.apikey/enterpriseServer`（同 §12.5，DEBUG 沒加 debugRealKeychain 時只放記憶體），網址、帳號存 `settings.json` 的 `enterpriseServerURL`、`enterpriseServerAccount`。啟動參數 `--set` 覆蓋的值登出時只清記憶體（`AppSettings` 本來就這樣）。
- 說話者辨識也交給伺服器（D122）：建工作時 `diarize` 照設定；結果的 `speakers`、`voices`（伺服器跟 Windows 同一個 ERes2Net 模型）、`diarizationQuality` 直接存進資料庫、比對聲紋，跟本機轉錄一樣。這台電腦不用說話者辨識的模型。
- 選企業伺服器時：`JobRunner.PrepareTranscription` 不等模型、不自動下載（R50.7）；有 NVIDIA 顯示卡也不自動下載加速元件（`GpuAcceleration.Start` 看到 `transcriptionEngine=server` 就跳過，`gpuPackOffered` 不設，之後換回本機引擎時下次開啟再判斷）。快速辨識的模型照樣下載（即時逐字稿要用）。按「改在這台電腦轉錄」時才開始下載 Breeze、說話者辨識的模型，還沒好就先用快速辨識（D35）。
- 畫面：設定的「進階」最上面一組「企業伺服器」（同 M§36.3）；「錄音與轉錄」的正式轉錄多「企業伺服器（公司名稱）」；整理工具的選單在登入了、公司有提供整理時多「公司（<公司名稱>）」（M§36.3、D124；伺服器自己的錯誤只顯示伺服器的訊息，R51.6、`OpenAICompatibleApi.PlainServerErrors`），登入只在進階；選了沒有模型欄、key 欄，只顯示公司設定的模型（不只一個時是下拉選單「公司的預設」＋後台設定的名稱）。啟動、打開整理工具頁時 `EnterpriseServer.RefreshInfo` 重新讀伺服器的資訊。會議頁的狀態列：伺服器負責的那段時間有「改在這台電腦轉錄」，在等伺服器時只顯示原因（不顯示百分比）。
- 加入連結（R49.7）：`shiyin://join?server=…` 跟 `shiyin://auth` 一樣從 `App.OpenLinks` 進來（`EnterpriseServer.IsJoin`），打開設定的「進階」，`SettingsWindow.ConfirmJoin` 讀 info 後用對話框問「加入 <主機> 的企業伺服器？」（`Uri.IdnHost`：國際化網域顯示成 punycode），內文寫伺服器自稱的名稱，加入就照設定頁的「登入」（瀏覽器 → `shiyin://auth`）；登入的是別台時不先登出，`CompleteSignIn` 成功才換掉舊的（M§36.2）。`ConfirmJoin`、設定頁的「登入」「登出」是 `async void`：接住所有例外（log 只寫型別）、顯示訊息，加入連結再怎麼怪都不能讓 app 當掉；`AppHost.CompleteEnterpriseSignIn` 也一樣。公司詞彙表（R53）：`CompanyGlossary` 存在 `SHIYIN_HOME\enterprise-glossary.json`，`EnterpriseServer.RefreshInfo` 和登入完成時一起更新、登出時刪；`JobRunner` 的四個地方用合併後的詞；「說話者與詞彙」頁多一段「公司的詞彙」。
- DEBUG 控制通道：`enterprise login <網址> <信箱>`（伺服器開發用的登入，不經過瀏覽器，登入後套用預設）、`enterprise logout`、`enterprise expire`、`enterprise local <會議 id>`；`state` 的 `enterprise`、`transcriptionEngine`、`provider` 和轉錄的 `server`。虛擬機裡驗證時，伺服器開在 Mac 上，用 `ssh -R` 讓虛擬機連 `http://127.0.0.1:<埠>`（同 §14.2）。

## 13. 匯出與封包

- md、txt、srt、vtt、HTML：翻 `IO/Exporters.swift` 的文字產生部分，跟標準答案逐位元組相同（§4）。
- **docx**：DocumentFormat.OpenXml，把 Core 匯出的 HTML（標籤固定：h1～h3、p、ul/li、table、b、span.time）轉成 Word 原生的標題、項目符號、表格（`Services/Export/DocxWriter.cs`），字型見 §11.3；待辦表格的負責人、期限欄不拆行（同 Mac 的 `fitShortColumns`）。HTML 只是中間格式，Word 裡沒有 HTML（不用 altChunk）；用 OpenXml 的 schema 檢查零錯誤。
- **PDF**：WebView2 打開標準答案裡同一份 HTML（字型換成 §11.3 的 Windows 字型），`PrintToPdfAsync`，A4、邊界 56 pt。
- docx、PDF 不能跟 Mac 逐位元組比（Mac 是 NSAttributedString、NSTextView 排的，見 M§21.13）：比抽出來的文字、表格結構、頁數。Mac 上用 PDFKit 的小腳本抽 PDF 的文字。
- **docx 跟 Mac 的差別**（同一個 .shiyin 匯出的 Word 抽出每一段的文字比）：文字和順序相同，會議紀錄差在兩個地方，都是 Mac 的 NSAttributedString 的寫法，Windows 不跟：
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

- 詢問更新的視窗只列更新內容的前 4 項、其餘寫「還有 N 項」（4.1 起）：`Shiyin.Core/Text/ReleaseNotes.cs` 跟 Mac 的 `ReleaseNotes.swift` 同一套取段落、取條目的規則，標準答案 `util/release-notes.json`。
- Velopack 只檢查自己 feed 裡的 SHA-256，不驗是誰發的。所以另外加一把**更新金鑰**（ECDSA P-256）：
  - `scripts/windows/update-key.cs keygen` 建一次，私鑰放 GitHub secrets（要使用者同意，同 Mac 的 `export-signing-cert.sh`），公鑰貼進 `AppHost.cs` 的 `UpdateKey.Public`
  - 2026-10-22 使用者建好了：私鑰在 GitHub secrets 的 `SHIYIN_UPDATE_KEY`，備份在使用者的登入鑰匙圈（服務 `com.weiwei.shiyin.update-key`、帳號 `SHIYIN_UPDATE_KEY`，內容是 PEM 的 base64）。兩邊都弄丟就簽不了新版，已經安裝的人只能自己下載安裝檔
  - CI 打包後簽「版本、架構、每個套件檔的 SHA-256」，簽章檔跟 Release 一起上傳
  - app 內建公鑰；下載新版後、套用前驗證簽章、版本比目前新、套件的 SHA-256 相符，不符就刪掉下載的檔案、拒絕更新
  - 等於 Mac 的「新版要跟目前的 app 同一個簽章身分」（M§12）
- 有 Authenticode 簽章（SignPath）之後，加檢查「新版的簽署者跟目前的相同」。
- 下載的進度（4.7.1，R10.6、M§12）：Velopack 的下載回呼給百分比（`State.Downloading(Percent)`），沒有位元組數，所以只寫百分比。主視窗側欄底部（同模型下載）「正在下載更新 45%」加進度條；設定「進階」的更新那一行寫「正在下載更新 45%…」加進度條。下載中結束拾音（`App.Quit`）先問「更新還在下載，要結束拾音嗎？」。設定頁下載中只換百分比、不整行重畫（重畫會讓焦點掉到下面的開關）；按了「下載並更新到」那顆按鈕消失時，焦點移到這一行的進度條。在設定頁開始下載時，主視窗還開著的詢問（`App.AskToUpdate` 的 ContentDialog）收起來：同時只能開一個 ContentDialog，開著的話結束時的確認出不來（`_ = Quit()` 的例外被吞掉，什麼都沒發生；2026-11-11 在虛擬機上看到）。下載中再按不會重下載一次（`Updater.Install` 看到 Downloading、Installing 直接回來）
- 檢查時機、錄音或工作中不能更新、資料庫自動遷移同 M§12。Windows 的麥克風權限跟著「桌面 app」整體，不綁 app 的版本，更新後不用重新允許（R10.7，要在有麥克風的電腦上確認）。沒有更新金鑰的 DEBUG 版不檢查。
- 測試更新：`pack.ps1 -UpdateTest` 建的版本（編譯時加 `UPDATE_TEST`，發布的版本不會有）改從環境變數 `SHIYIN_UPDATE_SOURCE`（Velopack 的 SimpleWebSource，更新說明也從這裡讀）、`SHIYIN_UPDATE_PUBLIC_KEY` 讀更新來源和公鑰。用測試金鑰打包兩個版本，Mac 上開本機伺服器、`ssh -R` 讓虛擬機連進來。

### 14.3 GitHub Releases（D51）

- Windows 用自己的 tag：`win-vX.Y.Z`（Mac 的工作流程只認 `v*`，不會被觸發）。
- 版號跟 Mac 的功能版本對齊：第一版 4.0.0（`win-v4.0.0`）跟 Mac 4.0.0 一起發布、功能相同（D52）；之後只修 Windows 的問題時加第三碼。
- **Windows 的 release 一律用 `--latest=false`**：Mac 的更新網址是 `releases/latest/download/shiyin-update.json`，Windows 的 release 變成 latest 時，Mac 的 app 內更新會找不到檔案（R27 R13.2）。之後 Mac 發新版時照樣變成 latest。
- Windows 的更新來源（Velopack 的 GithubSource 讀 GitHub 的 release 清單）要能跳過沒有 Windows 檔案的 Mac release：只有 Mac 的 release 時顯示「已是最新版本」、沒有錯誤。
- **release 清單讀 100 筆**（D94）：Velopack 的 GithubSource 只讀最新的 10 筆（`releases?per_page=10&page=1`）。Windows 使用者一段時間沒開、這期間 Mac 發了 10 版以上，Windows 的新版就不在這 10 筆裡，顯示「已是最新版本」，要等下一個 Windows 版發布才看得到。`Services/WindowsReleaseSource.cs` 繼承 GithubSource、只換掉讀清單的那一步：讀 100 筆（GitHub 一次最多），只留有 Velopack 更新說明（`releases.*.json`）的最新 10 個，找新版、下載照 Velopack 原本的做法。測試：12 個 Mac 版之後才有 1 個 Windows 版的清單找得到（`ServicesTests.ReleaseSourceFindsWindowsBehindManyMacReleases`）。
- 新的工作流程 `.github/workflows/release-windows.yml`：推 `win-v*` 時在 `windows-latest`（x64）、`windows-11-arm`（ARM64）建置，跑測試、whisper 從固定 commit 建（檢查沒有匯入 VC++ 的 DLL、印出每個 DLL 的 SHA-256）、`vpk pack`、簽更新金鑰、建立 Release（不設成 latest）。手動執行只產出 artifact（同 Mac）。
- Release 說明的格式同 Mac（docs/website.md §7），標題改成「拾音 Windows X.Y.Z：…」「Shiyin for Windows X.Y.Z: …」。

### 14.4 官網

`site/src/releases.mjs` 依 tag 分兩組（`docs/website.md` §7）：`v*` 是 Mac（規則不變，最新的 release 一定要有 DMG）、`win-v*` 是 Windows（要有兩個 Setup.exe）；下載區、`/download/windows`、更新紀錄、下載次數分開算。試建置用 `node site/build.mjs --releases <檔案>` 拿兩種 release 混在一起的 JSON。

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
- 停虛擬機用 `utmctl stop --request "Shiyin Windows"`（請 Windows 自己關機）：`utmctl stop` 預設是強制斷電，還沒寫回磁碟的登錄檔、檔案的改動會不見（2026-11-16 對比主題巡檢：改回原本的佈景主題、刪掉測試資料夾後強制斷電，重開機又是「夜空」、資料夾還在）。
- 使用者在用拾音錄會議時（Mac 的 `audio/recording/` 有檔案）不開虛擬機、不跑重的工作。UTM 的視窗會出現在使用者的螢幕上（不會搶前景）。

### 15.3 控制通道

- DEBUG 版開 named pipe `\\.\pipe\shiyin-debug`（`Debug/ControlPipe.cs`）；`shiyinctl.exe <指令> <參數…>`（`Shiyin.Ctl`）把結果的 JSON 印到標準輸出（Mac 是寫成檔案；從 SSH 用時直接讀輸出比較方便）。named pipe 不分工作階段，從 SSH 也連得到桌面上的拾音。
- 指令：`state`、`select`、`tab`、`open`（`open settings [頁面]`，§11.41）、`closewindow`（`main`、`settings`）、`settingssearch`、`quit`、`record-sim`、`recordstop`、`recordpause`、`recordstall`、`recordinterrupt`、`recordlang`、`import`、`export`、`purge`、`rediarize`、`language`、`llmprovider`、`llmkey`、`llmurl`、`modelurl`、`model`、`hotkey`、`meetingapp`、`metrics`。名稱和參數跟 Mac 的 `Debug/DebugControl.swift` 相同（例如 `export <項目> <格式> <路徑> [times] [speakers] [會議 id]`、`import [lang=ja] <檔案…>`），Mac 的測試方法可以直接照用。
- 評估（`transcribe-eval`、`voiceprint-eval`、`detect-eval`、`diarize-eval`、`fast-eval`、`live-eval`）不在 app 裡，用 `Shiyin.Eval` 命令列在 Mac 或 Windows 上跑，輸出格式跟 Mac 相同，例如用 `scripts/dev/asr-score.swift` 算錯字率（R28.4）。

### 15.4 真的畫面：排程工作和 UI Automation

從 SSH 啟動的程式不會出現在登入的桌面，UI Automation 也碰不到桌面。所以用排程工作在桌面的工作階段執行 PowerShell（`ps/in-session.ps1`：註冊「互動式登入」的工作、等它跑完、讀回輸出）。

- `insession.sh`：把標準輸入的 PowerShell 放到桌面跑，例如 `ps/start-app.ps1` 在桌面開 DEBUG 版（資料在 `C:\ShiyinTest`）。
- `uia.sh <動作> <視窗> <名稱>`（`ps/uia.ps1`，Windows 內建的 UIAutomationClient）：`windows`、`dump`、`click`（Invoke／Toggle／選取／展開）、`open`（會打開選單的按鈕，交給另一個程序按，不然會等到選單關掉）、`menu`、`choose`（下拉選單）、`set`（輸入框）、`close`、`menukeys`（真的滑鼠打開右鍵或「⋯」的選單、同一個程序接著按鍵，§11.38）。同名的元件能按的排前面，`名稱#2` 指定第幾個。拖曳用 `drag`（真的滑鼠，也能從檔案總管拖檔案進來，§11.36）、`dragat`（元件裡橫著拖，§11.40），鍵盤焦點用 `focus`、`focused`，RangeValue 用 `range`。SplitButton 的箭頭、DropDownButton、子選單用 `ps/expand.ps1 <視窗> <名稱> [項目] [子選單的項目或 ?]`（ExpandCollapsePattern；`uia.ps1` 的 `click`、`menu` 用 Invoke，會按到 SplitButton 主要的動作），放在 `insession.sh` 裡跑。
- `shot.sh <檔案>`：在 Mac 上用 `screencapture -l` 截 UTM 的視窗（整個虛擬機的畫面）。
- UI Automation 做不到時（開著選單、存檔對話框時 UI Automation 會卡住）：用 UTM 的 AppleScript `input scan code` 送按鍵，只會送進虛擬機，不會到 Mac 前景的 app。
- 系統的視窗（SmartScreen、系統匣）不屬於拾音：直接從桌面的根元件找（例如系統匣在 `Shell_TrayWnd`，隱藏的圖示先按「顯示隱藏的圖示」）。

## 16. 決策紀錄（接著 design.md 的 D41）

| # | 決策 | 不採用的方案與原因 |
|---|---|---|
| D42 | Windows 版用 C# .NET＋WinUI 3 另外寫，Mac 版不動 | Swift on Windows：可以搬 Model、Store、Notes 的程式，但沒有 SwiftUI，介面要用 swift-winrt 接 WinUI，工具鏈和 GRDB 在 Windows 都不成熟。Tauri、Electron 兩邊共用：等於 Mac 3.6 整個重寫，原生的體驗和已經驗收的東西都要重來。WPF：成熟但外觀是舊的 Windows；WinUI 3 是現在的原生元件。取捨：兩份程式要各自維護，靠 §4 的標準答案保持一致（使用者 2026-10-20 決定） |
| D43 | 兩邊一致靠 Mac 產生的標準答案比對，不共用提示詞檔案 | 把提示詞搬到共用的文字檔：要改 Mac 3.6 讀提示詞的方式，而且組合提示詞的邏輯兩邊還是各寫一份，終究要比對組出來的結果。從 Swift 原始碼自動轉成 C#：插值和條件太多，轉不穩 |
| D44 | 即時逐字稿用 sherpa-onnx 的 SenseVoice＋VAD，一直重新辨識正在講的那句當暫定文字（T-W5 的結果見 §7） | Windows 內建的語音辨識：舊的 Windows.Media.SpeechRecognition 連續聽寫要連網或品質差；新的 Windows AI 語音辨識是實驗性、一定要打包成 MSIX、只有英文。串流的 zipformer、paraformer：輸出簡體、沒有日文。whisper 小模型邊錄邊辨識：延遲大、CPU 吃重，還會跟正式轉錄搶資源（同 D1）。雲端語音服務：違反第 4 節（轉錄不連網） |
| D45 | 「macOS 內建」換成「快速辨識」（SenseVoice） | 拿掉這個選項：第一場會議要等 1.7 GB 下載（D35 的理由）；即時逐字稿本來就要這個模型，不用多下載 |
| D46 | whisper.cpp 自己從 Mac 同一版建，P/Invoke | Whisper.net：包的 whisper.cpp 比較舊、同一段錄音結果會跟 Mac 不一樣（R28.4）；語言偵測只給最高的那一個，拿不到中英日三個機率（R16 的門檻要用）；Vulkan 版只有 x64。執行 whisper-cli 子程序：進度、取消、token 機率都要從文字輸出解析 |
| D47 | 說話者辨識用 sherpa-onnx（分群改成自己做，見 D58） | 把 FluidAudio 的流程（pyannote community-1＋VBx）整個搬到 ONNX：community-1 沒有官方 ONNX 版，要自己轉模型。pyannote（Python）：違反 D2。取捨：模型和 Mac 不同，聲紋不能共用、門檻要重新調（R28.6）；「至少 N 人」、聲紋、分不準的判斷要自己補（§9） |
| D48 | 資料放 `%LOCALAPPDATA%\Shiyin`，設定放在裡面的 settings.json | 登錄（Registry）：換 `SHIYIN_HOME` 時設定還是同一份，測試會改到正式的設定（Mac 的 UserDefaults 就踩過）。`%APPDATA%`（漫遊）：錄音和模型很大，不該跟著帳號漫遊 |
| D49 | API key 存在認證管理員 | DPAPI 加密後存檔案：N6 規定 key 不寫進檔案。WinRT 的 PasswordVault：底層也是認證管理員，不打包的 app 用起來比較麻煩 |
| D50 | 用 Velopack 安裝和更新，加上自己的更新金鑰（ECDSA）驗證發布者 | MSIX：自簽的 MSIX 要先用系統管理員把憑證裝進「受信任的人」，一般使用者裝不起來。Inno Setup 加自己寫更新器：要自己處理替換、重開、解除安裝，Velopack 都有，還有差異更新。只靠自簽的 Authenticode 比對簽署者：做得到（`CERT_E_UNTRUSTEDROOT` 表示簽章完整只是不受信任），但對 SmartScreen、Smart App Control 沒有幫助，還要多保管一張憑證；更新金鑰的驗證只要幾行標準的密碼學程式。Microsoft Store：要開發者帳號和審核，之後再說 |
| D51 | Windows 的 release 用 `win-v*` tag，不設成 latest | 同一個 release 放兩邊的檔案、版號一樣：兩邊一定要同時發布，但 Windows 修自己的問題、Mac 加新功能的時間不會一樣。Windows 的 release 標成 prerelease：官網、Velopack 預設都會略過它，使用者看不到也更新不到。另外開一個 repo：星星、issue、官網的資料來源都要分開。取捨：官網的 `releases.mjs` 要改成認兩種 tag，而且要在第一個 Windows release 之前改好 |
| D52 | Windows 的版號對齊 Mac 的功能版本，第一版 4.0.0（跟 Mac 一起發布） | 從 1.0.0 開始：使用者看不出 Windows 版有哪些 Mac 的功能 |
| D53 | 驗證用排程工作在桌面跑 PowerShell 的 UI Automation，加上 named pipe 的控制通道（§15.4；原本規劃的桌面代理 app 用不到） | 在使用者的電腦上送鍵盤事件：會送進使用者正在用的 app，不行。WinAppDriver：微軟已經不維護。從 SSH 直接開 app：不會出現在桌面，截不到圖 |
| D54 | 複製富文字只放純文字和 HTML | 再放 RTF：要自己從 HTML 產生 RTF；Windows 上常用的貼上目標都吃 HTML |
| D55 | 偵測會議讀隱私設定的麥克風使用紀錄（ConsentStore） | audio session 清單（IAudioSessionManager2）：要一個一個裝置列，瀏覽器和 WebView2 用另一個程序收音，容易漏。取捨：ConsentStore 沒有官方文件，Windows 改版時可能變；它是系統「麥克風使用中」指示的資料來源，改了也比較容易發現 |
| D56 | whisper 外面包一層小的 C shim | 在 C# 宣告整個 `whisper_full_params`：傳值、1 byte 的 bool、巢狀 struct 和 callback，對錯了不會報錯、只會結果怪；每次升級 whisper.cpp 都要重新對一次 struct |
| D57 | 介面語言在設定裡可以另外選 | 只跟著 Windows：Windows 沒有替不打包的桌面 app 個別設定語言的地方（Mac 有），英文 Windows 上想用中文介面的人就沒辦法 |
| D58 | sherpa-onnx 只切段落（固定 30 群），每 3 秒的小段用 3D-Speaker 算聲紋，減掉整場平均後自己做凝聚式分群（§9） | sherpa-onnx 原本的門檻分群：中文會議（AISHELL-4）把 6 人併成 1～3 人，門檻怎麼調都不行（整場的聲紋太像）。WeSpeaker（英文 VoxCeleb）的聲紋：減掉平均後 AMI 還好，AISHELL-4 同一人、不同人的相似度重疊太多（第 10 百分位 0.03 對第 90 百分位 0.33；3D-Speaker 是 0.17 對 0.09）。VBx：要 PLDA 模型、參數多，這個做法在 AMI、AISHELL-4 已經跟 Mac 一樣好。取捨：聲紋模型比 WeSpeaker 大（40 MB 對 26 MB）；聲紋比對、分不準的門檻跟 Mac 不同（0.6、0.37） |
| D59 | WinUI 的畫面全部用 C# 建，不用 XAML 檔 | XAML：排版看得比較清楚，但 XAML 編譯器（XamlCompiler.exe）只能在 Windows 跑，Mac 上連編譯檢查都不行，每改一次畫面都要進虛擬機。取捨：程式比較長、沒有設計工具的預覽；用 `Ui.cs` 的小工具把常用的寫法縮短，畫面一律在虛擬機截圖確認 |
| D60 | 不碰畫面的 app 邏輯放在 `Shiyin.Services`（net10.0），平台的部分是介面 | 寫在 App 的 ViewModel 裡：Mac 上測不了，錄音、工作佇列、匯入、更新這些最容易出錯的流程都要進虛擬機才能測。取捨：多一層介面（`IAudioDecoder`、`IAudioSource`、`IExportPlatform`、`IUpdatePlatform`…）；換來在 Mac 上用模擬的音源、假的引擎和本機伺服器跑完整的流程 |
| D61 | 快速辨識、即時逐字稿用原版 SenseVoice-Small（2024-07-17），VAD 門檻 0.35，送進 VAD 前自動增益（§7） | 2025-09-09 版（原本選的，比較新）是用粵語語料微調過的：日文的假名幾乎都不見（國會聽證、FLEURS 錯字率約 80%，原版 16～28%），英文 AMI WER 60%（原版 40%），只有中文合成會議稍好（8.8% 對 9.2%）。VAD 用預設門檻 0.5、不放大：遠距收音的 AISHELL-4 只抓到兩成的時間，錯字率 84%；門檻 0.35 加自動增益是 26%，其他錄音也沒有變差（門檻 0.35 單獨讓 FLEURS 從 19.9% 降到 15.8%）。固定放大 15～20 dB 對 AISHELL-4 也有用，但大聲的錄音（國會聽證的第 90 百分位已經 −20 dBFS）再放大會超過 0 dBFS 被削掉。取捨：自動增益在很安靜的房間會把背景放大（最多 24 dB），靠 VAD 擋；音量變化很大的會議，增益要幾秒才跟上 |
| D62 | 通知用 Windows 本身的 ToastNotificationManager＋登錄的 AppUserModelID，按鈕用 `shiyin:` 網址加權杖（§12.2） | Windows App SDK 的 AppNotification：自帶部署、沒打包的 app 不能用（依賴 Singleton 套件）。不自帶 Windows App SDK、改用框架套件：要另外裝執行階段，安裝檔要系統管理員或多一步。COM 啟動器（INotificationActivationCallback）：按按鈕時不用開第二個程序，但要登錄 CLSID、實作 COM 伺服器，程式多很多。取捨：每按一次按鈕會短暫多開一個程序（轉送完就結束）；網址可能被網頁觸發，所以會改變狀態的動作要權杖 |
| D63 | 開始功能表、桌面的捷徑由 app 的安裝 hook 自己建，名稱依介面語言（§14.1） | 用 Velopack 建的捷徑：名稱是打包時固定的 `--packTitle`，英文 Windows 也是「拾音」。名稱寫成「拾音 Shiyin」：兩種語言都找得到，但「應用程式」清單、捷徑都多一串字。`desktop.ini` 的 LocalizedFileNames 依語言顯示：要在 exe 放多語言的字串資源（MUI），還要改開始功能表資料夾裡共用的 `desktop.ini`。取捨：建立、改名、刪除捷徑要自己處理（`Shortcuts.cs`）；安裝程式自己的視窗標題還是固定的「拾音」 |
| D64 | NVIDIA 顯示卡加速由 app 另外下載 whisper.cpp 官方的 CUDA 版（R27 R4.6、§8） | 直接放進安裝檔：x64 的安裝檔從約 100 MB 變成約 750 MB（ggml-cuda 570 MB、cuBLASLt 474 MB），沒有 NVIDIA 顯示卡的人也要下載。官方的 CUDA 11.8 版：壓縮檔沒有附 cuBLAS，要另外從 NVIDIA 下載。自己建 Vulkan 版（設計原本的做法，所有顯示卡都能用）：官方沒有 Windows 的 Vulkan 檔案，要 CI 裝 Vulkan SDK 建，還沒做；做好之後 AMD、Intel 的顯示卡用 Vulkan，NVIDIA 再比較 CUDA 和 Vulkan 的速度決定要不要留這個下載。取捨：下載 685 MB、佔 1.1 GB；只支援 NVIDIA；CUDA 12.4 要 527 版以後的驅動（太舊時用 CPU）；要第一次用 whisper 之前下載好才用得上，不然要重新開啟。後續：AMD 的顯示卡改用拾音自己建的 Vulkan 版（D127），Intel 還沒開，NVIDIA 照舊用 CUDA |
| D93 | Windows 的短句再確認用說話者辨識的同一個聲紋模型，跟每位說話者的聲紋比，像另一位 0.25 以上才改（§9；Mac 用另一個帶前後文的模型、跟長句的平均比、0.15，D92） | 照搬 Mac 的長句平均、0.15：Windows 的模型只聽得到這一句，AMI 沒重疊的句子改錯 5 句，使用者的訪談有 4 句改得跟 Mac 相反。像 Mac 帶前後文算：sherpa-onnx 的聲紋模型沒有遮罩的輸入（Mac 的 wespeaker_v2 有），只能把前後的聲音一起算，會混進別人的聲音（沒有試）。不做：完整逐字稿還是有進步（AMI 94.9% → 95.5%、AISHELL-4 96.6% → 97.1%），也沒有變差。取捨：使用者回報的那一句在 Windows 改不回來（兩位的分數只差 0.09）；說話者辨識多約 10～20% 的時間 |
| D94 | Windows 找新版時自己讀 GitHub 的 release 清單（100 筆），其他照 Velopack 的 GithubSource（§14.3） | 照用 GithubSource：只讀最新的 10 筆，Mac 連發 10 版後 Windows 使用者看不到新版。固定網址的 feed（SimpleWebSource）：每次發布要另外更新一個固定的 release 或網站上的檔案，官網的 release 清單也要跳過它，多一個會出錯的地方。分 Mac、Windows 兩個 repo：星星、issue、下載數分散（D51）。取捨：Mac 在一次 Windows 發布之間發了 100 版以上才會再漏，到時候改成往下翻頁 |
| D95 | Windows 的 Qwen3-ASR 用 llama.cpp 官方的 `llama-server.exe` 當子程序（HTTP），不做 shim DLL（§8.4） | 同程序載入 llama 的 DLL：跟 whisper 同名的 ggml DLL，載入器只認名稱，兩個版本不能並存；side-by-side manifest 可以但複雜、出錯時只會靜默用錯版本。自己建 llama.cpp 把 DLL 改名：每次升級要重建 cpu／cuda 兩種後端兩種架構，CI 時間和出錯的地方都多。用跟 llama 同版的 ggml 重建 whisper：whisper 固定在 d1be6fd 才跟 Mac 結果一致（R28.4）。取捨：多一個程序、每次轉錄多幾秒啟動（Mac 也是每次載入模型）、音訊要 base64 走 localhost（15 秒一段約 0.6 MB，可忽略）、要自己管子程序的生命週期（Job Object） |
| D127 | AMD 顯示卡用 Vulkan：拾音自己在 CI 建 whisper 的 Vulkan 版、放在 prerelease，app 偵測到 AMD 才下載；Qwen 用 llama.cpp 官方的 Vulkan 版（R4.7、§8、§8.4；2026-10-29 使用者要求） | 放進 x64 的安裝檔（§8 原本的寫法）：沒有 AMD 的人也要下載，使用者要「只有部分電腦要的元件讓 app 自己下載」。ROCm／HIP：Windows 的 HIP SDK 只支援部分獨顯，Radeon 內顯（拾音的使用者裡有 780M、860M）多半不支援，元件也大很多。DirectML、ONNX：whisper.cpp 沒有，要換引擎，結果跟 Mac 不一致（R28.4）。直接用 llama.cpp 官方的 ggml-vulkan.dll：ggml 版本不同（b11840 對 whisper 的 d1be6fd），不能混用。Intel 也一起開：還沒量過內顯會不會比 CPU 快。取捨：要在 CI 裝 Vulkan SDK、自己放檔案；沒有顯示卡的 CI 只能用 lavapipe 確認算得對，真的速度要使用者的 AMD 電腦 |
| D129 | Windows 的電腦聲音改錄預設輸出裝置（endpoint loopback），跟著預設裝置換（R54、§6；2026-10-28 GitHub issue #2） | 照舊用 process loopback 排除拾音自己：新版 Teams（ms-teams.exe）的聲音收到的全是 0，開始、收音都回報成功（排除和只錄某個程序兩種模式都一樣；Microsoft 的 ApplicationLoopback 範例 microsoft/Windows-classic-samples#414、OBS 的應用程式音訊擷取一樣）。process loopback 為主，偵測到「自己收到靜音、輸出裝置卻有聲音」才改錄整個裝置：Teams 跟別的聲音同時在播時偵測不到，還要一直讀每個裝置的音量、切換時兩邊銜接。process loopback 和 endpoint loopback 一起錄再混：別的 app 的聲音兩邊都有，會疊兩次。取捨：錄音中在拾音裡播放錄音會錄進去（Mac 排除自己，R54.3）；Teams 的喇叭指定了不是預設的裝置時錄不到。後續：issue #2 的回報者 Windows 預設的輸出和 Teams 的喇叭是同一台裝置，R54 就錄得到（2026-10-29 回覆）；「也錄 Teams 指定的裝置」先不做，有人回報再做（§6） |
| D138 | Intel 內顯不等實機量過就開，第一次正式轉錄時用同一段錄音讓顯示卡和 CPU 各轉一次，文字幾乎一樣、顯示卡又比較快才用（R4.8、§8；2026-11-04 使用者決定不等實機） | 等實機量過再開：手邊沒有 Intel 的 Windows 筆電，Intel 是 Windows 使用者的 3/4，統計裡多數 Intel 筆電轉錄比錄音還慢。直接全開：ggml-vulkan 在某些 Intel 內顯、驅動上算錯或比 CPU 慢時，使用者看不出來，只會覺得逐字稿變差或變慢。只比語言偵測的機率（CI 的 lavapipe 那樣）：快，但只跑到編碼器和一步解碼，解碼器算錯看不出來，也量不到真的轉錄速度。換 OpenVINO：whisper.cpp 的 OpenVINO 只加速編碼器、要另外轉模型，結果跟 Mac 對不上（R28.4）。取捨：每台 Intel 電腦第一次正式轉錄多花 1～2 分鐘，還沒檢查時啟動多約 1 秒（先載入一次 Vulkan 版，載入就當掉時才不會每次都當）；顯示卡要快到 CPU 的 8 成以內才用（偏保守，只快一點點時寧可用 CPU）；判斷不了時下一場再試 |
| D172 | Windows 分割會議時，WAV 直接切位元組，其他格式都重新壓成 m4a（AAC），匯入的 mp3、mp4 這類檔案分割後原本那場也換成 m4a（§11.23） | 照 Mac 用原本的格式寫回去：Media Foundation 沒有通用的「照原檔設定寫回去」，mp3、flac、mp4（影片）要各找編碼器，有些 Windows（N 版、沒裝延伸模組）沒有；Mac 的 AVAudioFile 也寫不了 mp3。不重新壓縮、照時間切 m4a 的封包：AAC 的音框有前後相依（priming），切點附近會有雜音或少一小段，還要自己寫 MP4 容器。WAV 也走 Media Foundation：要解碼再寫，格式（浮點、24 位元、WAVE_FORMAT_EXTENSIBLE）可能變，還比較慢。取捨：重新壓縮會再損失一點音質（位元率照原檔取最接近的）；mp4 影片分割後只剩聲音（拾音本來就只播聲音）；切的時間跟錄音的長度成正比（一小時的 m4a 約十幾秒，期間 `SplittingMeeting` 擋住重複分割） |
