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

專案依「能不能在 Mac 上測」分開。上面五個是 `net10.0`，在這台 Mac 上就能 `dotnet test`，大部分邏輯、甚至轉錄和說話者辨識的評估都不用開虛擬機：

| 專案 | 目標 | 內容 | 在 Mac 上能跑 |
|---|---|---|---|
| `Shiyin.Core` | net10.0（只用 BCL） | 資料模型、MeetingLanguage、時間格式、提示詞和 schema、範本、匯出（txt、md、srt、vtt、html）、封包的 JSON、匯入的解析、說話者對應、合併和復原、聲紋比對規則、分不準的判斷、收音提醒、語言偵測的累積、whisper 的提示詞和接縫、版本比較、翻譯查表 | 能 |
| `Shiyin.Data` | net10.0 | migration v1～v9、FTS rowid、搜尋、工作佇列、校正、草稿 | 能 |
| `Shiyin.LLM` | net10.0 | 六種整理工具、重試、退回 JSON 模式；「找外部程式」是介面，Windows 的實作在 Platform | 能 |
| `Shiyin.Engines` | net10.0 | whisper（P/Invoke shim）、SenseVoice、VAD、說話者辨識、聲紋嵌入 | 能（osx-arm64 的原生檔） |
| `Shiyin.Eval` | net10.0 命令列 | 對應 Mac 的 `transcribe-eval`、`voiceprint-eval`、`detect-eval`，輸出格式相同 | 能 |
| `Shiyin.Platform.Windows` | net10.0-windows10.0.22621 | WASAPI、Media Foundation、認證管理員、快捷鍵、偵測會議、防止睡眠、通知、找 CLI、檔案關聯、更新驗證 | 不能 |
| `Shiyin.App` | WinUI 3（不打包、自帶 Windows App SDK） | 畫面、ViewModel、系統匣、PDF、DEBUG 控制通道 | 不能 |
| `Shiyin.Ctl` | net10.0 | `shiyinctl.exe` | — |
| `Shiyin.Agent` | net10.0-windows | 驗證用的桌面代理（§15.3），不放進安裝檔 | 不能 |

`Shiyin.Core`、`Data`、`LLM` 照 Mac 的 Swift 檔案一個對一個翻（`Model/`、`Notes/`、`Store/`、`IO/`、`Transcribe/TranscriptionPipeline.swift`…），方便以後對照修改。用使用者的錄音評估時用 `Shiyin.Eval` 在 Mac 上跑，錄音不用複製進虛擬機。

## 2. 技術選型

| 需求 | 選擇 | 依據 |
|---|---|---|
| 語言、介面（R26.3） | C# .NET 10（LTS，支援到 2028-11）＋ WinUI 3：Windows App SDK 2.5，**只參考用到的元件套件**（WinUI、Foundation、InteractiveExperiences） | 使用者決定（D42）。整包的 Windows App SDK 會帶進 Windows ML 的 `onnxruntime.dll`，跟 sherpa-onnx 的同名檔衝突 |
| 麥克風（R1.1） | NAudio 3.3（MIT，`NAudio.Wasapi`）；不行就用 CsWin32 自己包 WASAPI | 3.x 是 2026-08 才出的新大版，版本釘死 |
| 電腦聲音（R1.2） | WASAPI process loopback，`EXCLUDE_TARGET_PROCESS_TREE` 排除拾音自己（NAudio 3 的 `WithProcessLoopback`） | 對應 Mac 的「全域 tap 排除自己」；需要 build 20348 以上，Windows 11 都有 |
| 即時逐字稿（R2） | sherpa-onnx 1.13（Apache-2.0）：Silero VAD ＋ SenseVoice-Small int8；中文用 OpenccNetLib（MIT，s2twp）轉台灣繁體 | Windows 沒有可用的內建離線中文辨識（D44），待 T-W5 |
| 快速辨識（R4.1、R20.8） | 同一個 SenseVoice 模型讀整個檔案 | 取代 macOS 內建（D45） |
| 正式轉錄（R4） | whisper.cpp b5454（v1.9.5、commit `d1be6fd`，跟 `Package.swift` 同一版）自己建：x64 CPU 各種指令集＋Vulkan；ARM64 CPU 各種指令集。加一層小的 C shim，C# 用 P/Invoke | 結果要跟 Mac 一致（R28.4，D46、D56），待 T-W3 |
| 語言偵測（R16） | 同上的 whisper ＋ Whisper base（同 M§18.8） | 同一個模型和門檻；要中英日三個機率，Whisper.net 拿不到 |
| 說話者辨識、聲紋（R5、R19.3） | sherpa-onnx 的說話者分段（pyannote segmentation-3.0）＋聲紋嵌入（WeSpeaker ResNet34，256 維） | 有 .NET 套件、跑在 CPU（D47），待 T-W4 |
| 解碼、壓縮 m4a | Media Foundation（NAudio 的 `MediaFoundationReader`、`MediaFoundationEncoder`）；AIFF 用 NAudio 的 `AiffFileReader` | 系統內建，不用 ffmpeg（R4.5），待 T-W9 |
| 播放（R8.2） | `Windows.Media.Playback.MediaPlayer` 的 `PlaybackRate` | 變速時有沒有保持音高待 T-W9 |
| 資料庫 | Microsoft.Data.Sqlite 10 ＋ SQLitePCLRaw 2.1（自帶 SQLite，含 FTS5、有 win-arm64），Dapper | trigram 要 SQLite 3.34 以上，不能用系統的 |
| API key | 認證管理員（CredWriteW／CredReadW，CsWin32 產生呼叫） | 對應鑰匙圈（D49） |
| 設定 | `SHIYIN_HOME\settings.json` | 對應 UserDefaults（D48） |
| 通知 | Windows App SDK 的 AppNotification | 有按鈕、點了帶參數打開拾音 |
| 系統匣 | WinUIEx 2.9（MIT，TrayIcon；檔案總管重開後自動恢復）＋自己做的小視窗 | H.NotifyIcon 的新版還在 beta |
| docx | DocumentFormat.OpenXml 3.5（MIT），直接從會議紀錄的資料產生 | Windows 沒有 NSAttributedString 的 docx 輸出 |
| PDF | WebView2 把跟 Mac 同一份匯出的 HTML 印成 PDF | Windows 11 內建 WebView2 執行環境 |
| 安裝、更新 | Velopack 1.2（MIT；1.2.158 被標為不要用）＋ 自己的更新金鑰（ECDSA P-256） | 不用系統管理員、有差異更新（D50），待 T-W7 |
| UI 自動化（驗證） | FlaUI 5（UI Automation）、Windows.Graphics.Capture | §15.3 |

授權寫進第三方授權（R27）。要特別標示的：SenseVoice 的權重是 FunASR Model License（不是 OSI 授權）、WeSpeaker 模型是 CC-BY-4.0、ARM64 的 `libomp140.aarch64.dll` 要對照 Visual Studio 的可轉散發清單。

## 3. 檔案配置

**原始碼**：

```
windows/
├── Shiyin.Windows.sln
├── global.json                 固定 .NET SDK 10.0.x
├── Directory.Packages.props    所有套件的版本
├── Directory.Build.props       ArtifactsPath：建置輸出放到 repo 外（Mac 是 ~/Library/Caches/ShiyinBuild-win）
├── src/Shiyin.Core/  Shiyin.Data/  Shiyin.LLM/  Shiyin.Engines/  Shiyin.Eval/
├── src/Shiyin.Platform.Windows/  Shiyin.App/  Shiyin.Ctl/  Shiyin.Agent/
├── tests/                      xUnit：Core、Data、LLM、Engines（Mac、CI 都跑）；UITests（FlaUI，虛擬機的桌面跑）
└── native/whisper/             CMake 腳本、shim、各平台建出來的檔案的 SHA-256
shared/golden/                  Mac 產生的標準答案（§4）
scripts/windows/                build.ps1、pack.ps1、make-update-key.ps1
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
- **轉成 16 kHz 單聲道**：每個來源一個重新取樣器，狀態跨 buffer 保留（對應 `StreamConverter`）。NAudio 的 `WdlResamplingSampleProvider` 下一版預設會改，版本釘死，升級時重跑標準答案。
- **寫檔**：自己寫 RIFF，格式、雙聲道對齊、補靜音的規則跟 `Capture/RecordingWriter.swift` 相同；每幾秒更新一次標頭，意外結束後用 WAVRepair（照搬）修。暫停時丟掉樣本。
- **音量、收音提醒、磁碟空間**：算法和門檻同 M§26（`Recorder.checkLevels`、1 GB／300 MB），放在 Core，用標準答案比對。音量條的畫法見 §11.2。
- **防止睡眠**（R1.9）：`PowerCreateRequest`＋`PowerSetRequest(PowerRequestSystemRequired)`，整個程序有效；錄音和工作結束時清掉。`powercfg /requests` 看得到。
- **麥克風權限**（R27 R1.8）：開啟時回 `E_ACCESSDENIED` 就顯示說明，按鈕打開 `ms-settings:privacy-microphone`。Windows 之後如果對桌面 app 也個別詢問，照同樣的流程。

## 7. 即時逐字稿（R2、R27 R2.1）

- 混音（左右平均，同 Mac 的 MixTap）送進 Silero VAD；VAD 判定在說話時，約每 0.4 秒把這一句目前的聲音整句丟給 SenseVoice 重新辨識，結果當暫定文字；VAD 判定一句結束時（一句最長約 15 秒，超過就切），最後一次的結果當定稿，存成 `kind='live'` 的段落（同 M§5.3）。
- SenseVoice 的語言跟著會議語言（zh、en、ja）；開 ITN（標點、數字）；拿掉情緒、事件標籤。
- 中文輸出用 OpenccNetLib 的 s2twp 轉成台灣繁體（字典已內建）。
- 在背景執行緒、低於一般的優先順序跑，不能拖到錄音寫檔（R25.2）。
- 自動偵測語言（R16）：照 M§18.8，用 whisper base；切換語言時即時辨識換語言，時間偏移照舊。

## 8. 正式轉錄

- **whisper**：`native/whisper/` 的 CMake 腳本從 commit `d1be6fd` 建置，CI 建好、記下 SHA-256 放進安裝檔，不從別處下載。
  - x64：`GGML_BACKEND_DL`＋`GGML_CPU_ALL_VARIANTS`（執行時依 CPU 指令集選），加 `ggml-vulkan.dll`（官方的 Windows 檔案沒有 Vulkan，要自己建）。
  - ARM64：CPU 也建多個版本。只建一個新指令集（i8mm）的版本，舊的 Snapdragon 會碰到不支援的指令而當掉。GPU（OpenCL-Adreno）還是實驗性，先不用。
  - Mac 也建一份 dylib，`Shiyin.Engines` 的測試在 Mac 上就能跑。官方 zip 裡的 `whisper-cli.exe` 拿來交叉比對結果。
- **shim**（D56）：約 100 行的 C，匯出 `shiyin_whisper_full(ctx, const ShiyinParams*, 進度 callback, 取消 callback, …)`，在 C 這邊填好 `whisper_full_params`。不在 C# 複製這個 struct：它用傳值的方式傳、C 的 `bool` 是 1 byte、裡面有巢狀 struct 和 callback，很容易對錯而且不會報錯。其他簡單的函式（`whisper_full_n_segments`、`whisper_full_get_segment_*`、`whisper_full_get_token_p`、`whisper_pcm_to_mel`、`whisper_lang_auto_detect`、`whisper_lang_id`…）直接 P/Invoke，跟 `WhisperEngine.swift`、`LanguageDetector.swift` 用的一樣。
- **參數**同 Mac：beam 5、best_of 5、執行緒數 clamp(核心數 − 2, 2, 8)、`suppress_blank`、有顯卡時用 GPU。只有 CPU 時要不要改（beam、執行緒）待 T-W3，改了寫進決策紀錄、設定頁顯示。
- **分段、提示詞、接縫、過濾、低信心**：同 M§21.16、M§6.2，程式在 Core，用 §4 的 `pipeline/` 比對。
- **快速辨識**（R4.1、R20.8）：SenseVoice＋VAD 讀整個檔案。Breeze 還沒下載好時自動用（同 D35），轉完提示「可以用 Breeze 重新轉錄」。
- **快速辨識的模型怎麼來**（R27 R20.8）：首次使用引導的「模型」步驟先下載快速辨識和 VAD（約 230 MB，數字 T-W5 確認，比 Breeze 小很多），下載好就能錄音，Breeze 接著在背景下載。要不要直接附在安裝檔，T-W7 量過安裝檔和差異更新的大小再決定。
- **解碼**：Media Foundation 的 SourceReader 依序讀、轉成 16 kHz 單聲道浮點（同 D34 的串流讀法）。Media Foundation 不支援 CAF、AIFF：AIFF 用 `AiffFileReader`，CAF 待 T-W9（解不開就從 R3.1 拿掉並說明）。Windows 的 N 版沒有 Media Foundation，要裝 Media Feature Pack（寫進系統需求）。
- **結束拾音**時等 whisper 收尾最多 10 秒（R18.12）。

## 9. 說話者辨識與聲紋

sherpa-onnx 的說話者分段是凝聚式分群（`FastClusteringConfig` 只有「剛好 N 群」或門檻），**沒有 PLDA、VBx、「至少 N 人」，也不回傳每個人的聲紋**。這些要自己補：

- **「大約幾個人」**（`minSpeakers`，D37）：先用門檻分群；分出的人數少於 N 時，用 `SetConfig` 改成 N 群再分一次（不用重新載入模型）。
- **每位說話者的聲紋**：取這個人最長的幾段，用 `SpeakerEmbeddingExtractor` 算嵌入再平均。
- **分不準的提醒**（D39）：每 10 秒一窗算嵌入，跟所屬群中心的 cosine 算凝聚度，算法照 `Diarizer.quality`。
- **兩個方案**（待 T-W4）：A 用 sherpa 原本的分群；DER 明顯比 Mac 差時做 B：參考 pyannote、FluidAudio 用 C# 寫 VBx。
- **門檻重新調**：聲紋比對（Mac 0.65）、分不準（Mac 0.4）。用 AMI 9 場照 Mac M8 的方法（`scripts/dev/voiceprint-eval.py` 讀倒出來的嵌入）定，誤認 ≤ 2%（D29）。
- 合併、改句子的說話者、復原、重新辨識的邏輯同 M§23、M§24（在 Core）。

## 10. 整理工具

- 六種後端照 `Notes/Backends/` 翻成 C#（HttpClient），重試、取消、錯誤分類同 M§19、M§21。
- **找 claude、codex**（待 T-W6）：claude 先找原生安裝的 `%USERPROFILE%\.local\bin\claude.exe`，再找 npm 的；codex 找 npm 套件裡真正的執行檔。**不能透過 `.cmd` 執行**：cmd.exe 的命令列只有 8191 個字元、跳脫規則不同，`--system-prompt`、`--json-schema` 會被截斷或解讀錯，還有參數注入的風險（BatBadBut 那一類）。找不到真正的執行檔時用 `node.exe` 加 js 的路徑。
- 參數一律用 `ProcessStartInfo.ArgumentList`（同 CLAUDE.md 的規則）；子程序的 PATH 用 `;` 接上找到的資料夾；工作目錄是 `tmp` 下的空資料夾。
- 子程序放進 Job Object（`KILL_ON_JOB_CLOSE`）：拾音當掉時子程序一起結束；逾時時整棵程序樹一起結束。
- 登入：用固定參數開 Windows Terminal（沒有就用 `cmd.exe /k`）執行 `claude` 或 `codex login`，參數裡不放任何使用者內容。

## 11. 介面

### 11.1 視窗對照

| Mac | Windows |
|---|---|
| 主視窗（NavigationSplitView：側欄、搜尋、會議內容） | 主視窗：左邊會議清單（含資料夾、搜尋框、垃圾桶、匯入），右邊會議內容 |
| 會議頁的分頁（摘要、詳細、逐字稿、待確認、筆記、提問） | `SelectorBar`，Ctrl+1～6 |
| 錄音中的畫面、播放列、時間軸 | 同樣的排版；時間軸用 Win2D 或 Composition 畫 |
| Settings（7 個分頁） | 獨立的設定視窗，左邊 `NavigationView` 7 頁；「錄音」頁多一個介面語言（R26.4） |
| 引導、使用說明、回報問題、問所有會議 | 各自的視窗 |
| MenuBarExtra | 系統匣圖示＋無邊框、置頂、失焦就收起的小視窗，用 `Shell_NotifyIconGetRect` 定位。Windows 11 預設把新圖示收進「^」，第一次啟動時提示釘到工作列 |
| 偵測會議的浮動視窗（NSPanel） | 右下角、最上層、不搶焦點的小視窗（`WS_EX_NOACTIVATE`），30 秒收起 |
| sheet、alert、popover | `ContentDialog`、`Flyout` |
| 關掉主視窗 | 隱藏到系統匣，錄音和工作繼續（R1.7）；從系統匣或選單「結束」時，有工作在跑要確認（同 Mac） |

快捷鍵：Mac 的 ⌘ 換成 Ctrl（Ctrl+O 匯入、Ctrl+R 錄音、Ctrl+Shift+P 暫停、Ctrl+B 標記重點、Ctrl+E 匯出、Ctrl+F 搜尋逐字稿、空白鍵播放、Delete 丟到垃圾桶、F1 使用說明）。

### 11.2 效能（同 R20.3、R25）

- 一直在變的東西（音量條、計時、播放位置）不經過綁定通知整個畫面：音量條用 Composition 的 visual 自己定時讀音量（對應 D40）；計時只更新那一個 TextBlock。
- 逐字稿列表用 `ItemsRepeater` 虛擬化，播放時只更新換段的那兩列。
- 量法同 Mac：DEBUG 的 `metrics` 回報 UI 執行緒最長卡住多久、即時文字更新幾次；2,086 段的會議捲動和跟著播放（T-W10）。

### 11.3 外觀、字型

- 顏色（celadon、signal、說話者 8 色）從 `Views/Theme.swift` 搬到 XAML 的 ThemeDictionaries（淺色、深色）。
- 介面字型用系統預設（Segoe UI Variable、微軟正黑體）。會議標題的明體：Mac 用宋體，Windows 用新細明體（沒有時退回微軟正黑體粗體），W2 截圖比較後定。
- 會議內容依會議語言設定 `Language`（ja-JP 時日文字形，對應 D20）。
- 匯出（PDF、docx）的字型：中文 Microsoft JhengHei／PMingLiU、日文 Yu Gothic／Yu Mincho、英文 Segoe UI／Georgia（Mac 是 New York）。

### 11.4 介面文字（R26.4）

- 翻譯表 `src/Shiyin.App/Strings/Localizable.xcstrings`：跟 Mac 同樣的格式、同樣用中文當鍵（D12）。**不跟 Mac 共用同一個檔案**：Mac 的 `l10n.py extract` 會刪掉 Swift 沒用到的條目。
- `scripts/dev/l10n.py` 加 Windows 的模式：從 .cs、.xaml 抽字串、`check`（缺英文、沒查表的中文）、`sync`（Mac 的表裡已經有的英文直接帶進來）。
- C# 用 `L("中文")`、`L("已匯入 %lld 個檔案", n)`：鍵的寫法照 xcstrings（`%@`、`%lld`、`%1$@`、複數），才能沿用 Mac 的英文；XAML 用 markup extension `{l:Str Key=中文}`。
- 介面語言：預設跟著 Windows 的顯示語言（zh-Hant／zh-TW／zh-HK 是中文，其他英文）；Windows 不能替不打包的桌面 app 個別設定語言，所以設定裡另外可以選（R26.4）。DEBUG 可以用啟動參數 `--lang en`。
- 依會議語言的文字（匯出、預設說話者名稱）同 M§16（`MeetingLanguage.text`），在 Core。

## 12. 系統整合

### 12.1 單一執行個體、開檔

- `AppInstance.FindOrRegisterForKey`：第二次啟動把參數（要開的檔案）轉給已經開著的拾音再結束。
- 安裝時在 `HKCU\Software\Classes` 註冊 `.shiyin` 的預設程式、音訊和影片的「開啟檔案」清單（Velopack 安裝、解除安裝的 hook）。

### 12.2 通知（R11.2、R19.4）

- AppNotification：先掛 `NotificationInvoked` 再 `Register()`；完成、失敗、偵測會議（按鈕「開始錄音」「這次不用」「不要再提醒這個 app」；「繼續錄」「停止並轉錄」）；點了用參數打開那場會議。解除安裝時 `UnregisterAll()`。
- 用 `AppNotificationManager.Setting` 看通知有沒有被關掉，關掉時偵測會議改用 §11.1 的小視窗（同 Mac 的退路）。
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
- **docx**：DocumentFormat.OpenXml 直接用會議紀錄的資料產生（標題、項目符號、待辦表格、短欄位縮窄同 Mac 的 `fitShortColumns`），字型見 §11.3。不從 HTML 轉、不用 altChunk。
- **PDF**：WebView2 打開標準答案裡同一份 HTML（字型換成 §11.3 的 Windows 字型），`PrintToPdfAsync`，A4、邊界 56 pt。
- docx、PDF 不能跟 Mac 逐位元組比（Mac 是 NSAttributedString、NSTextView 排的，見 M§21.13）：比抽出來的文字、表格結構、頁數。Mac 上用 PDFKit 的小腳本抽 PDF 的文字。
- **m4a**：Media Foundation 的 AAC 編碼器只接受 44.1 或 48 kHz、96～192 kbps，16 kHz 的錄音要先升頻，每小時約 43 MB（Mac 約 10～30 MB）。寫進 R27 的 R9.4。
- **wav**：16 kHz 單聲道。
- **.shiyin**：`System.IO.Compression`，zip 根目錄放 manifest.json、meeting.json、transcript.json、audio.*（同 `IO/Package.swift`）。匯入時檢查路徑（不能有 `..`、絕對路徑、符號連結，R18.10）。

## 14. 安裝、簽章、更新、發布

### 14.1 安裝檔（R27 R10.1）

- Velopack（`vpk pack`）：`Shiyin-<版本>-win-x64-Setup.exe`、`Shiyin-<版本>-win-arm64-Setup.exe`，各自一個更新頻道（`win-x64`、`win-arm64`）。裝在 `%LOCALAPPDATA%\ShiyinApp`、不用系統管理員，自帶 .NET 和 Windows App SDK。開始功能表捷徑的名稱依 Windows 語言（R27 R12.3）。
- 安裝說明（中、英）放在官網和 Release 說明：
  - SmartScreen「Windows 已保護您的電腦」→「其他資訊」→「仍要執行」
  - **Smart App Control** 開著時沒有簽章的 app 會被直接擋下、沒有「仍要執行」可以按（多半是新安裝的 Windows 11 才開著）。說明要寫清楚：可以關掉 Smart App Control（2026-04 起關掉後可以再打開，逐步推出），或等有正式簽章的版本
- 正式簽章：申請 SignPath Foundation（開源專案免費，repo 是 MIT）。申請要用使用者的名義送出，等使用者決定（tasks W8.2）。申請下來前發布的版本沒有 Authenticode 簽章。付費憑證、Azure Artifact Signing（個人帳號只限美國、加拿大）不做。

### 14.2 更新（R27 R10.6、R10.7，D50，待 T-W7）

- Velopack 只檢查自己 feed 裡的 SHA-256，不驗是誰發的。所以另外加一把**更新金鑰**（ECDSA P-256）：
  - `scripts/windows/make-update-key.ps1` 建一次，私鑰放 GitHub secrets（要使用者同意，同 Mac 的 `export-signing-cert.sh`）
  - CI 打包後簽「版本、架構、每個套件檔的 SHA-256」，簽章檔跟 Release 一起上傳
  - app 內建公鑰；下載新版後、套用前驗證簽章、版本比目前新、套件的 SHA-256 相符，不符就刪掉下載的檔案、拒絕更新
  - 等於 Mac 的「新版要跟目前的 app 同一個簽章身分」（M§12）
- 有 Authenticode 簽章（SignPath）之後，加檢查「新版的簽署者跟目前的相同」。
- 檢查時機、錄音或工作中不能更新、資料庫自動遷移同 M§12。Windows 的麥克風權限跟著「桌面 app」整體，不綁 app 的版本，更新後不用重新允許（R10.7，T-W7 確認）。沒有更新金鑰的 DEBUG 版不檢查（測試用的金鑰另外建）。

### 14.3 GitHub Releases（D51）

- Windows 用自己的 tag：`win-vX.Y.Z`（Mac 的工作流程只認 `v*`，不會被觸發）。
- 版號跟 Mac 的功能版本對齊：第一版 3.6.0 表示功能跟 Mac 3.6 相同（D52）；之後只修 Windows 的問題時加第三碼。
- **Windows 的 release 一律用 `--latest=false`**：Mac 的更新網址是 `releases/latest/download/shiyin-update.json`，Windows 的 release 變成 latest 時，Mac 的 app 內更新會找不到檔案（R27 R13.2）。之後 Mac 發新版時照樣變成 latest。
- Windows 的更新來源（Velopack 讀 GitHub 的 release 清單）要能跳過沒有 Windows 檔案的 Mac release，待 T-W7；不行的話用固定網址的 feed（SimpleWebSource）。
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

- **Parallels Desktop**（VMware Fusion 的 ARM 版 Windows 社群回報沒有音效驅動，錄音測不了）。Pro 版有 `prlctl exec <vm> --current-user`，可以直接在登入的桌面執行程式；Standard 版沒有，用 §15.4 的桌面代理（用哪一版由使用者決定）。
- Windows 11 ARM64，6 核、8 GB；設定自動登入、不鎖定螢幕（鎖定時截不到圖）。
- 裝 .NET 10 SDK、Visual Studio Build Tools（C++、ARM64 和 x64 的工具組）、Git、OpenSSH Server。從 Mac 用 `ssh` 建置、跑測試。repo 在虛擬機裡自己 clone（不用共用資料夾）；模型從 Mac 的 `models` 複製過去。
- 不用 Coherence 模式（Windows 的視窗會混進 Mac 的桌面）。虛擬機的桌面不是使用者在用的桌面，必要時可以在虛擬機**裡面**送按鍵，但優先用 UI Automation。
- Mac 只有 18 GB：虛擬機 8 GB 加上轉錄時約 3 GB。使用者在用拾音錄會議時（Mac 的 `audio/recording/` 有檔案）不開虛擬機、不跑重的工作。

### 15.3 控制通道

- DEBUG 版開 named pipe `\\.\pipe\shiyin-debug`，只接受同一個使用者、拒絕遠端連線；`shiyinctl.exe <指令> <參數…>`，結果寫成 JSON 檔（同 Mac）。named pipe 不分工作階段，從 SSH 也連得到桌面上的拾音。
- 指令名稱、輸出格式跟 Mac 的 `Debug/DebugControl.swift` 相同（`state`、`select`、`import`、`record-sim`、`transcribe-eval`、`voiceprint-eval`、`detect-eval`、`metrics`、`llmurl`、`llmkey`、`llmprovider`、`setupdateurl`、`hotkey`、`purge`、`quit`…），Mac 的測試方法和腳本可以直接用：例如 Windows 的 `transcribe-eval` 輸出拿回 Mac，用 `scripts/dev/asr-score.swift` 算錯字率（R28.4）。

### 15.4 真的畫面：桌面代理

從 SSH 啟動的程式不會出現在登入的桌面，截圖、UI Automation 也碰不到桌面。所以另外做 `Shiyin.Agent`，用排程工作（「只在使用者登入時執行」）或 `prlctl exec --current-user` 在桌面啟動，開另一個 named pipe 收指令：

- `launch`：在桌面啟動 DEBUG 版拾音（等於 Mac 的 `open -g`）
- `uia-dump`、`uia-press <視窗> <名稱>`、`uia-focus`、`uia-value`：用 FlaUI 列出元件、按按鈕、設焦點、設值（等於 Mac 用 AX 按按鈕）
- `screenshot <視窗>`：Windows.Graphics.Capture（或 `PrintWindow(PW_RENDERFULLCONTENT)`）只截拾音的視窗
- 測按鍵時用 `PostMessage` 只送給拾音的視窗

可行性待 T-W1。

## 16. 決策紀錄（接著 design.md 的 D41）

| # | 決策 | 不採用的方案與原因 |
|---|---|---|
| D42 | Windows 版用 C# .NET＋WinUI 3 另外寫，Mac 版不動 | Swift on Windows：可以搬 Model、Store、Notes 的程式，但沒有 SwiftUI，介面要用 swift-winrt 接 WinUI，工具鏈和 GRDB 在 Windows 都不成熟。Tauri、Electron 兩邊共用：等於 Mac 3.6 整個重寫，原生的體驗和已經驗收的東西都要重來。WPF：成熟但外觀是舊的 Windows；WinUI 3 是現在的原生元件。取捨：兩份程式要各自維護，靠 §4 的標準答案保持一致（使用者 2026-10-20 決定） |
| D43 | 兩邊一致靠 Mac 產生的標準答案比對，不共用提示詞檔案 | 把提示詞搬到共用的文字檔：要改 Mac 3.6 讀提示詞的方式，而且組合提示詞的邏輯兩邊還是各寫一份，終究要比對組出來的結果。從 Swift 原始碼自動轉成 C#：插值和條件太多，轉不穩 |
| D44 | 即時逐字稿用 sherpa-onnx 的 SenseVoice＋VAD，一直重新辨識正在講的那句當暫定文字（待 T-W5） | Windows 內建的語音辨識：舊的 Windows.Media.SpeechRecognition 連續聽寫要連網或品質差；新的 Windows AI 語音辨識是實驗性、一定要打包成 MSIX、只有英文。串流的 zipformer、paraformer：輸出簡體、沒有日文。whisper 小模型邊錄邊辨識：延遲大、CPU 吃重，還會跟正式轉錄搶資源（同 D1）。雲端語音服務：違反第 4 節（轉錄不連網） |
| D45 | 「macOS 內建」換成「快速辨識」（SenseVoice） | 拿掉這個選項：第一場會議要等 1.7 GB 下載（D35 的理由）；即時逐字稿本來就要這個模型，不用多下載 |
| D46 | whisper.cpp 自己從 Mac 同一版建，P/Invoke | Whisper.net：包的 whisper.cpp 比較舊、同一段錄音結果會跟 Mac 不一樣（R28.4）；語言偵測只給最高的那一個，拿不到中英日三個機率（R16 的門檻要用）；Vulkan 版只有 x64。執行 whisper-cli 子程序：進度、取消、token 機率都要從文字輸出解析 |
| D47 | 說話者辨識用 sherpa-onnx，先用它的分群，不夠再自己寫 VBx（待 T-W4） | 把 FluidAudio 的流程（pyannote community-1＋VBx）整個搬到 ONNX：community-1 沒有官方 ONNX 版，要自己轉模型。pyannote（Python）：違反 D2。取捨：模型和 Mac 不同，聲紋不能共用、門檻要重新調（R28.6）；「至少 N 人」、聲紋、分不準的判斷要自己補（§9） |
| D48 | 資料放 `%LOCALAPPDATA%\Shiyin`，設定放在裡面的 settings.json | 登錄（Registry）：換 `SHIYIN_HOME` 時設定還是同一份，測試會改到正式的設定（Mac 的 UserDefaults 就踩過，見 CLAUDE.md）。`%APPDATA%`（漫遊）：錄音和模型很大，不該跟著帳號漫遊 |
| D49 | API key 存在認證管理員 | DPAPI 加密後存檔案：N6 規定 key 不寫進檔案。WinRT 的 PasswordVault：底層也是認證管理員，不打包的 app 用起來比較麻煩 |
| D50 | 用 Velopack 安裝和更新，加上自己的更新金鑰（ECDSA）驗證發布者 | MSIX：自簽的 MSIX 要先用系統管理員把憑證裝進「受信任的人」，一般使用者裝不起來。Inno Setup 加自己寫更新器：要自己處理替換、重開、解除安裝，Velopack 都有，還有差異更新。只靠自簽的 Authenticode 比對簽署者：做得到（`CERT_E_UNTRUSTEDROOT` 表示簽章完整只是不受信任），但對 SmartScreen、Smart App Control 沒有幫助，還要多保管一張憑證；更新金鑰的驗證只要幾行標準的密碼學程式。Microsoft Store：要開發者帳號和審核，之後再說 |
| D51 | Windows 的 release 用 `win-v*` tag，不設成 latest | 同一個 release 放兩邊的檔案、版號一樣：兩邊一定要同時發布，但 Windows 修自己的問題、Mac 加新功能的時間不會一樣。Windows 的 release 標成 prerelease：官網、Velopack 預設都會略過它，使用者看不到也更新不到。另外開一個 repo：星星、issue、官網的資料來源都要分開。取捨：官網的 `releases.mjs` 要改成認兩種 tag，而且要在第一個 Windows release 之前改好 |
| D52 | Windows 的版號對齊 Mac 的功能版本，第一版 3.6.0 | 從 1.0.0 開始：使用者看不出 Windows 版有哪些 Mac 的功能 |
| D53 | 驗證用桌面代理＋named pipe＋UI Automation | 在使用者的電腦上送鍵盤事件：CLAUDE.md 禁止。WinAppDriver：微軟已經不維護。從 SSH 直接開 app：不會出現在桌面，截不到圖 |
| D54 | 複製富文字只放純文字和 HTML | 再放 RTF：要自己從 HTML 產生 RTF；Windows 上常用的貼上目標都吃 HTML |
| D55 | 偵測會議讀隱私設定的麥克風使用紀錄（ConsentStore）（待 T-W8） | audio session 清單（IAudioSessionManager2）：要一個一個裝置列，瀏覽器和 WebView2 用另一個程序收音，容易漏。取捨：ConsentStore 沒有官方文件，Windows 改版時可能變；它是系統「麥克風使用中」指示的資料來源，改了也比較容易發現 |
| D56 | whisper 外面包一層小的 C shim | 在 C# 宣告整個 `whisper_full_params`：傳值、1 byte 的 bool、巢狀 struct 和 callback，對錯了不會報錯、只會結果怪；每次升級 whisper.cpp 都要重新對一次 struct |
| D57 | 介面語言在設定裡可以另外選 | 只跟著 Windows：Windows 沒有替不打包的桌面 app 個別設定語言的地方（Mac 有），英文 Windows 上想用中文介面的人就沒辦法 |

## 17. 需要先做的實驗

結果寫在對應的 commit 訊息，選擇定了就改上面的章節和決策紀錄。

| # | 內容 | 通過標準 |
|---|---|---|
| T-W1 | 虛擬機和自動化：從 Mac 用 SSH 建置一個 WinUI 3 的小 app；桌面代理（或 `prlctl exec --current-user`）在登入的桌面啟動它、截圖、用 FlaUI 按按鈕；named pipe 讀狀態 | 全部不碰虛擬機的鍵盤滑鼠就能做到；截圖看得到 WinUI 的內容（不是黑的）；輸出資料夾沒有兩份 `onnxruntime.dll` |
| T-W2 | 錄音：命令列原型同時錄麥克風和電腦聲音 5 分鐘（Edge 播 YouTube，中間停 30 秒；原型自己播一段正弦波） | 沒有斷音、停播的 30 秒補成靜音、兩個聲道時間差 100 ms 以內、正弦波沒錄進去；換預設裝置後自動接回；`powercfg /requests` 看得到 |
| T-W3 | whisper：建 x64（CPU 各版本＋Vulkan）、ARM64（CPU 各版本）、Mac 的 dylib，跑 Breeze | 同一段錄音跟 Mac 的 `transcribe-eval` 錯字率差 ≤ 0.5 個百分點（R28.4 的上限是 1）；語言偵測跟 Mac 判斷相同；取消 1 秒內停；記憶體峰值 ≤ 3 GB；量出虛擬機、CI x64、CI ARM64 的倍速，決定只有 CPU 時的參數和 N2、N4 的數字 |
| T-W4 | 說話者辨識：`Shiyin.Eval` 在 Mac 上跑 AMI 9 場、AISHELL-4、使用者的麻將錄音，A、B 兩個方案 | 說話者標錯的時間 ≤ 15%（同 T-A3）；AMI 自動判斷出 4 人、AISHELL-4 6 人、麻將給「至少 4 人」時分出 ≥ 4 位；選出門檻（聲紋誤認 ≤ 2%）；1 小時的錄音 3 分鐘內處理完（CI x64） |
| T-W5 | 即時逐字稿：`record-sim` 跑中、英、日的評估錄音 | 暫定文字 1 秒內、定稿 5 秒內；錯字率跟 Mac 的 SpeechAnalyzer 比；轉出來是台灣繁體；虛擬機的 CPU 用量 |
| T-W6 | claude、codex CLI：原生安裝和 npm 安裝各試一次 | 找得到真正的執行檔（不經過 cmd.exe）；10,000 字以上的中文 `--system-prompt` 和 JSON schema 傳進去不會被截斷；能判斷登入狀態、開終端機登入（登入要使用者同意） |
| T-W7 | 安裝和更新：本機伺服器模擬 GitHub Releases（Mac 和 Windows 的 release 混在一起） | 從 Edge 下載的 Setup.exe（有網路來源標記）被 SmartScreen 擋下時照說明可以裝、不用系統管理員；Smart App Control 開著時的畫面記下來寫進安裝說明；正常、SHA 不符、更新金鑰不符、同版本四種更新檔的結果都對；更新後麥克風權限還在；解除安裝不刪資料；更新來源跳過 Mac 的 release |
| T-W8 | 偵測會議：新版 Teams、Zoom、Chrome 打開麥克風 | 5 秒內偵測到是哪個 app；停用 10 秒內偵測到；ConsentStore 跟 audio session 清單比對 |
| T-W9 | 媒體：解碼 m4a、mp3、wav、aac、flac、mp4、mov、wma、aiff、caf；AAC 編碼的檔案大小；變速播放的音高；WebView2 印中文、日文 PDF；docx 用 Word 打開 | 列出解得開的格式，寫進 R27 的 R3.1；解不開的有清楚的錯誤；1.5 倍速時聲音不變調（不行就換成 WASAPI 加自己的變速） |
| T-W10 | 長會議畫面：2,086 段的會議捲動、跟著播放 | `metrics` 的 UI 執行緒最長卡住時間跟 Mac 的 R20.3 同一個標準 |
