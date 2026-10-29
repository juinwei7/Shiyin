# CLAUDE.md

拾音 Shiyin 是 macOS 原生的會議紀錄 app（SwiftUI，macOS 26）。目前是 4.2.1（4.1 加上三個分頁的減法、待確認就地回答、收音提醒送通知、可選的 Qwen3-ASR 引擎，Windows 同步；4.1.1 整理工具設定好才用、失敗說清楚怎麼修；4.1.2 忘了停止錄音時提醒、自動停止；4.2 可以連公司的企業伺服器（在公司的伺服器轉錄、整理，公司詞彙表、加入連結）；4.2.1 企業伺服器的安全強化；第三版加上英文版、開源準備、轉錄準確度、日文、更多整理工具、穩定性修正；3.5 加上草稿紀錄、聲紋、猜說話者、對會議提問、偵測會議；3.6 加上重新辨識說話者、合併和改句子的說話者、分不準的提醒、收音提醒，修正錄音中畫面卡住；4.0 加上 Windows 版、待確認的是非題、聲紋沒把握時先問、短句的說話者、沒有 claude／codex 時先不整理）：

- 錄音：AVAudioEngine 收麥克風、Core Audio process tap 收電腦聲音，寫成雙聲道 WAV（`Sources/Shiyin/Capture/`）
- 即時逐字稿：系統的 SpeechAnalyzer（zh_TW、en_US 或 ja_JP，依會議語言）
- 正式轉錄：預設 whisper.cpp（官方 XCFramework）+ Breeze ASR 25 GGML q8_0（聯發科），可以改用 macOS 內建（SpeechAnalyzer 讀檔），模型還沒下載好時也先用內建；4.1 起設定裡可選 Qwen3-ASR 1.7B（阿里巴巴開源；llama.cpp 官方 XCFramework，經 C 薄層 `Sources/QwenShim/` 進 Swift；輸出簡體，`ChineseScript` 用 `Resources/opencc/` 的 OpenCC 字表轉台灣繁體；先辨識說話者再分段，每段 ≤ 15 秒，`Transcribe/QwenEngine.swift`；design §32，使用者決定預設維持 Breeze）；日文會議用 Whisper large-v3-turbo q5_0；whisper 從檔案依序讀、每 10 分鐘一段（design §21.16）；說話者辨識：FluidAudio（`Sources/Shiyin/Transcribe/`），聲紋比對在 `Store/Voiceprints.swift`
- 整理：claude CLI、codex CLI、Anthropic API、OpenAI API、Gemini API、OpenAI 相容 API，共同介面 `LLMBackend`（`Sources/Shiyin/Notes/`，各家在 `Notes/Backends/`，design §19）
- 資料庫：GRDB（SQLite、FTS5 全文搜尋）
- 多語言：介面繁中、英文（跟著 macOS 語言）；每場會議另外有會議語言（中文／英文／日文），決定轉錄、整理、匯出用哪個語言（design §16、§18）；錄音開始後用 Whisper base 自動偵測（§18.8）
- 發布：自簽憑證或 ad-hoc 簽章的 DMG（沒有 Apple 開發者帳號）

不需要 Python、Homebrew、ffmpeg。選型的理由在 design.md 的決策紀錄（D#）。已經刪掉的舊文件（第二版的規格 `docs/archive/v2/`、選型實驗 `docs/spikes/`、3.4.0 以前完整的 `docs/tasks.md`）用 `git show b621ed6:<路徑>` 看。

**Windows 版**（2026-10-20 起開發，4.0.0 跟 Mac 一起發布）：C# .NET 10＋WinUI 3，程式放 `windows/`，功能對齊 Mac 4.2.1；需求在 requirements 第 22 節（R26～R28），設計在 `docs/design-windows.md`，任務在 tasks.md 的「Windows 版」。見下面的「Windows 版」一節。

## 開發流程：SDD（規格驅動）

本專案先寫規格再寫程式，規格放在 `docs/`：

| 文件 | 內容 |
|---|---|
| `docs/requirements.md` | 要做什麼：需求編號 R#、N#（已發布版本的驗收結果不留在文件裡，在 git 歷史） |
| `docs/design.md` | 怎麼做：架構、資料模型、提示詞、介面、決策紀錄 D# |
| `docs/design-windows.md` | Windows 版怎麼做：跟 Mac 不同的地方、標準答案、驗證環境、決策紀錄 D42 起 |
| `docs/tasks.md` | 還沒完成的任務，每項都有驗證方式；做完就刪掉 |

開發時的規則：
- 開工前先讀 requirements、design、tasks（做 Windows 版時加上 design-windows），從 `tasks.md` 的任務接著做。
- 每完成一項任務，照它的「驗證」實際跑過一次，從 `tasks.md` 刪掉，跑了什麼、結果如何寫在 commit 訊息。`[~]` 表示程式完成，但有部分驗證要等使用者（權限、真實錄音、API key），只寫還差什麼。
- docs 只留還用得到的：做完的任務、用不到的文件直接刪掉，不另外留紀錄檔（都在 git 歷史裡）。
- 程式要偏離 `design.md` 時，先改 `design.md`（重要的取捨寫進決策紀錄 D#），再改程式，commit 訊息寫原因。
- 有新需求時，先寫進 `requirements.md`，再改設計、加任務。不要直接寫程式。

## 指令

```bash
scripts/build-app.sh             # 建置 release 版到 build/Shiyin.app（實際在 ~/Library/Caches/ShiyinBuild/app）
scripts/build-app.sh --install   # 建置並安裝到 ~/Applications
scripts/build-app.sh --debug     # DEBUG 版：含驗證用控制通道（指令見 Sources/Shiyin/Debug/DebugControl.swift）
scripts/make-dmg.sh              # release 建置並產生 DMG（dist/Shiyin-<版本>.dmg，含中英文安裝說明 PDF）
scripts/dev/l10n.py check        # 抽出介面字串合併進翻譯表，列出缺英文的條目和沒查表的中文（有問題時 exit 1）
scripts/dev/make-third-party-licenses.sh   # 更新套件版本後重跑：產生 Resources/ThirdPartyLicenses.txt（「關於拾音」和 DMG 附的授權條款）
scripts/dev/l10n.py apply <翻譯.json>   # 寫入英文翻譯：{"中文鍵": "English"}，複數用 {"one": …, "other": …}
scripts/make-signing-cert.sh     # 建立自簽憑證；加 --trust 信任它（要使用者輸入登入密碼）
scripts/export-signing-cert.sh --to-github <owner/repo>   # 把自簽憑證設成 GitHub Actions 的 secrets（私鑰會離開電腦，要使用者同意）
# CI：.github/workflows/release.yml，推 v* tag 就建置並發布 Release；手動執行只產出 artifact
node site/build.mjs              # 產生官網到 site/dist（讀 GitHub Releases，不用裝套件）；node site/serve.mjs 本機預覽 http://localhost:4321/
site/make-images.sh              # docs/images 的截圖或圖示換過後跑：產生官網用的 AVIF、favicon、分享預覽圖，連同 site/public 一起 commit
# 官網 CI：.github/workflows/site.yml，推到 main 改到 site/ 或 docs/images/、發布新版之後，部署到 Cloudflare Pages
swiftc scripts/dev/shiyinctl.swift -o ~/Library/Caches/ShiyinBuild/shiyinctl
~/Library/Caches/ShiyinBuild/shiyinctl state /tmp/s.json      # 讀狀態；其他指令見 Sources/Shiyin/Debug/DebugControl.swift
open -g build/Shiyin.app
/usr/bin/log stream --predicate 'subsystem == "com.weiwei.shiyin"'   # 看 log
sqlite3 ~/Library/Application\ Support/Shiyin/shiyin.sqlite # 看資料
```

建置暫存放在 `~/Library/Caches/ShiyinBuild`，專案裡的 `build/`、`dist/` 是指過去的捷徑（桌面有 iCloud 同步，不要把大檔案放進專案）。SwiftPM 要加 `--disable-keychain`，腳本已經處理。

## 重要限制

- 錄音、逐字稿、資料庫、模型一律放在 `SHIYIN_HOME`（預設 `~/Library/Application Support/Shiyin`），不要放進專案資料夾。
- API key 只能存在鑰匙圈（service `com.weiwei.shiyin.apikey`），不能寫進檔案、資料庫或 log。
- 呼叫外部程式一律用 `Process` 搭配參數陣列，不要組 shell 字串。
- log 不寫逐字稿和會議紀錄的內容。
- 從 Finder 開的 app 拿不到使用者 shell 的 PATH。要執行 claude、codex 時，用固定路徑，或在子程序的環境變數裡補上 PATH。
- 回覆和文件一律用繁體中文（台灣用語）。

## Windows 版

- Mac 版的行為不能因為 Windows 版改變；為了比對兩邊而加的 Mac 程式只能在 DEBUG 版（R26.5）。
- 兩邊一致靠標準答案（design-windows §4）：Mac 改了提示詞、範本、匯出、資料庫 migration、轉錄流程的規則，要重跑 Mac DEBUG 版的 `dump-golden`、commit `shared/golden/`，Windows 的程式跟著改到測試通過。
- 錄音、資料庫、模型放 `SHIYIN_HOME`（Windows 預設 `%LOCALAPPDATA%\Shiyin`）；設定是裡面的 `settings.json`；API key 只能存在認證管理員（`com.weiwei.shiyin.apikey/<帳號>`）。安裝位置（`%LOCALAPPDATA%\ShiyinApp`）跟資料分開。
- 外部程式一律用 `ProcessStartInfo.ArgumentList`，**不能經過 cmd.exe**（npm 的 `.cmd`）：參數會被截斷、改寫。
- 建置輸出放到 repo 外（`Directory.Build.props` 的 ArtifactsPath），不要放進專案。
- 虛擬機（UTM，Windows 11 ARM64，design-windows §15.2）：工具在 `scripts/windows/vm/`（`sync.sh` 同步原始碼到 `C:\src`、`ssh.sh`／`run.sh` 建置和測試、`ctl.sh` 控制通道、`insession.sh`／`uia.sh` 在桌面開 app 和按按鈕、`shot.sh` 截圖），金鑰、IP 在 `~/Library/Caches/ShiyinBuild-win/vm`。使用者在用拾音錄會議時（Mac 的 `audio/recording/` 有檔案）不開、不跑重的工作；`diskguard.sh` 在背景跑（Mac 剩不到 10 GB 就停掉虛擬機），虛擬機裡刪了大檔案後跑 `Optimize-Volume -DriveLetter C -ReTrim` 才會還空間給 Mac。從 SSH 開的 app 不會出現在桌面，要用 `insession.sh`（§15.4）。虛擬機沒有音效裝置，錄音用 `record-sim`。 開虛擬機用 `utmctl start "Shiyin Windows"`（`~/Applications/UTM.app/Contents/MacOS/utmctl`）；UTM 開很久之後 AppleScript 會沒回應（`start` 卡住、-1712 逾時），虛擬機沒在跑的話 `kill` UTM 再 `open -g -a UTM` 就好；開機後約 3 分鐘 SSH 才連得上。2026-10-28 UTM 在背景跑約 5 分鐘就被 macOS 的自動結束關掉兩次（log 有「would be terminated by TAL」、launchd 回報 SIGTERM），虛擬機跟著斷電；要不要關掉 UTM 的自動結束由使用者決定。虛擬機沒有瀏覽器：測企業伺服器的登入用 `ctl enterprise pending` 拿登入頁網址，在 Mac 走完登入後用 `insession.sh` 的 `Start-Process '<shiyin://auth…>'` 交回 app；`uia.sh show <視窗> <名稱>` 把元件捲進畫面再截圖。
- 有中文的 `.ps1` 存成有 BOM 的 UTF-8：Windows 內建的 PowerShell 5.1 會把沒有 BOM 的檔案當成 Big5，整個腳本解析失敗。
- PowerShell 傳給外部程式的 `-D名稱=$變數`、`--名稱=$變數` 要整串加雙引號（`"-DFOO=$x"`）：「-」開頭的參數裡的變數不會展開，程式拿到的是 `$x` 這串字（2026-10-22 CI 建 whisper 時，x64 因此去找 Vulkan）。
- 測安裝檔（design-windows §14）：`pack.ps1` 打包、`Setup.exe --silent` 安裝、`%LOCALAPPDATA%\ShiyinApp\Update.exe --uninstall --silent` 移除；移除後等 20 秒再重裝（Velopack 會延後刪安裝資料夾，會把剛裝好的刪掉）。測 app 內更新用 `pack.ps1 -UpdateTest`（§14.2）。安裝版跟 DEBUG 版共用通知的 AppUserModelID，移除安裝版會把通知中心裡 DEBUG 版的通知一起清掉。
- 官網的 Windows 版截圖：docs/website.md §16.4（虛擬機調成 150%、示範資料 `C:\ShiyinDemo`、`C:\ShiyinDemoEN`、`scripts/windows/vm/ps` 的 snapwin、clickname、dpi、display、setnotes），截完改回 100%、1024×768。截圖程式要宣告支援高 DPI，不然座標會被換算；PowerShell 的變數不分大小寫（`$h` 會蓋掉 `$H`）。
- Windows 的 release 用 `win-vX.Y.Z` tag，一律不設成 latest（Mac 的更新網址用 latest）；第一個 Windows release 之前官網要先改好（design-windows §14.3、§14.4）。
- .NET 10 SDK 裝在 `~/.dotnet`（使用者的資料夾，沒有加進 PATH）。在 `windows/` 下跑：`~/.dotnet/dotnet test tests/Shiyin.Tests`（這版 SDK 不認 `--project`）（標準答案、資料庫、整理工具，Mac 上就能跑；建置輸出在 `~/Library/Caches/ShiyinBuild-win`）。
- 重新產生標準答案：建 Mac 的 DEBUG 版，用暫存的 `SHIYIN_HOME` 啟動（`open -g -n --env SHIYIN_HOME=… … --args -ApplePersistenceIgnoreState YES -llmProvider none`），`shiyinctl dump-golden "$PWD/shared/golden/fixtures.json" <資料夾>`，等 `<資料夾>/result.json` 出現（`"ok": true`），刪掉 result.json 後換掉 `shared/golden/expected/`。跑兩次比對要完全相同。
- Mac 上只能編譯檢查 app（`dotnet build Shiyin.Windows.slnx`，App、Platform 用 `EnableWindowsTargeting`，不自帶 Windows App SDK、不產生 .pri，建出來的東西不能用）；真的建置、打包在 Windows：`scripts/windows/build-whisper.ps1 -Arch x64|arm64`（whisper.cpp 固定在 d1be6fde）、`scripts/windows/pack.ps1 -Version … -Arch … -Whisper …`（自帶 .NET 和 Windows App SDK、vpk pack、安裝檔改成 `Shiyin-<版本>-win-<架構>-Setup.exe`、簽更新說明）。
- 第三方授權：更新 Windows 的套件、whisper.cpp 之後跑 `DOTNET=~/.dotnet/dotnet python3 scripts/windows/make-third-party-licenses.py`，commit 產生的 `windows/src/Shiyin.App/ThirdPartyLicenses.txt`（app 用到沒列過的套件時會停下來，要在腳本裡加上授權從哪裡來）。
- 更新金鑰：`~/.dotnet/dotnet run scripts/windows/update-key.cs -- keygen|manifest|sign|verify`；私鑰放 GitHub secrets 的 `SHIYIN_UPDATE_KEY` 要使用者同意，公鑰貼進 `AppHost.cs` 的 `UpdateKey.Public`（空的時候不檢查更新）。
- 介面文字：改過 C# 的介面文字一定要跑 `python3 scripts/dev/l10n.py windows-check`，缺的英文用 `windows-apply <翻譯.json>` 寫進 `windows/src/Shiyin.App/Strings/Windows.xcstrings`（Mac 的翻譯表不動）。不會顯示在介面上的中文行尾加 `// l10n-skip`。
- Qwen3-ASR（design-windows §8.4）：Windows 用 llama.cpp 官方的 `llama-server.exe` 當子程序，不是 shim（跟 whisper 同名的 ggml DLL 不能同進程）。Mac 上跑 Windows 的 C# 流程：`SHIYIN_QWEN_SERVER=~/Library/Caches/ShiyinSpike/qwen3asr/llama/llama-b11840/llama-server SHIYIN_MODELS=~/Library/Caches/ShiyinSpike/qwen3asr/models SHIYIN_DIARIZATION_MODELS=~/Library/Caches/ShiyinBuild-win/models … shiyin-eval.dll transcribe-eval <wav> <設定 {"engine":"qwen",…}> <輸出>`，結果可以跟 Mac app 的 `shiyinctl transcribe-eval` 逐段比（2026-10-25 合成會議 7/7 相同）；`qwen-chunks <wav> <turns.json|-> <輸出>` 只比分段。GPU 一次一個：llama-server 跟 DEBUG app 不要同時跑。虛擬機的 `C:\ShiyinDemo\models` 已經放了兩個 GGUF 和 `qwen-runtime\`（ARM64 CPU 版），測試用的家目錄用 junction 就看得到；`sync.sh` 會帶 `Resources/opencc`（Core 內嵌的簡轉繁字表，漏了 Qwen 的中文會失敗）。
- 評估（Mac 上）：`SHIYIN_NATIVE_DIR=~/Library/Caches/ShiyinBuild-win/whisper-build-mac ~/.dotnet/dotnet ~/Library/Caches/ShiyinBuild-win/bin/Shiyin.Eval/debug/shiyin-eval.dll transcribe-eval|detect-eval|diarize-eval|fast-eval|live-eval …`；說話者辨識的模型放 `~/Library/Caches/ShiyinBuild-win/models`（`SHIYIN_DIARIZATION_MODELS`、`SHIYIN_SPEECH_MODELS`），`diarize-eval` 的逐字稿給 `-` 時只看分出幾個人和每人的聲紋（AMI 9 場的聲紋比對用 `scripts/dev/voiceprint-eval.py`）；設定加 `embedSegments` 輸出每句的聲紋、加 `recheck` 照 app 的規則做短句再確認（R34，design-windows §9）。
- 測試在 Mac 上不能碰使用者的資料：`ShiyinPaths.FromEnvironment()` 在 Windows 以外一定要有 `SHIYIN_HOME`（.NET 在 Mac 的 LocalApplicationData 就是 Mac 版拾音的資料夾）；測試一律用暫存資料夾（`TempHome`）。
- DEBUG 版的控制通道是 named pipe `shiyin-debug`，用 `shiyinctl.exe`（`windows/src/Shiyin.Ctl`）送指令：`state`、`select`、`tab <summary|transcript|ask>`（舊名稱 topics、review、notes 是別名，review、notes 會展開頂部的那一條）、`record-sim <檔案> <倍速> <1=雙聲道>`、`recordstop`、`llmprovider`、`llmkey`（只放記憶體，DEBUG 預設不讀真的認證管理員）、`llmurl`、`modelurl`、`hotkey`、`meetingapp <app> on|off`、`open settings|guide|report|ask|onboarding`（不是 `settings`；`closewindow` 只能關 main，其他視窗用 `uia.sh close`）、待確認的 `reviewexpand <section> <位置>`／`reviewsubmit`／`reviewskipall`／`review <答案 JSON> <略過>`（都對目前選取的會議；Mac 的同名指令多一個會議 id）…（`Shiyin.App/Debug/ControlPipe.cs`）。測 NVIDIA 顯示卡加速（design-windows §8）：x64 DEBUG 版要 `--self-contained` 建（虛擬機只有 ARM64 的 .NET），加 `--set debugNvidia=YES`（AMD 是 `debugAmd=YES`）啟動，用全新的 `SHIYIN_HOME`；x64 的 whisper DLL 從 GitHub Release 的 `ShiyinApp-<版本>-win-x64-full.nupkg` 的 `lib/app/` 取。

## 介面文字（3.1 起）

- 程式裡照樣寫中文；SwiftUI 的字面值會自動查 `Resources/Localizable.xcstrings`，其他地方（錯誤、提示、通知、狀態文字）要包 `String(localized:)`。鍵就是中文原文。
- 改過介面文字一定要跑 `scripts/dev/l10n.py check`，再把新條目的英文用 `apply` 寫進去。不會顯示在介面上的中文行尾加 `// l10n-skip`。
- 同一句中文在不同地方要不同的英文時，用 `MeetingLanguage.ui(zh:en:)`；依會議語言（不是介面語言）的文字用 `meeting.meetingLanguage.text(zh:en:)`，例如匯出、預設說話者名稱。
- 中文提示詞、中文匯出格式要跟 3.0 一字不差，改英文版時不要動到中文那一份。

## 發布新版的說明文字

官網的版本說明直接讀 GitHub Release（`docs/website.md` §7），格式不對時官網建置失敗、不會更新。tag 的說明文字（`git tag -a vX.Y.Z -F 說明.txt`，會變成 Release 的內容）一律寫成：

```
拾音 3.3.0：日文會議、自動偵測會議語言
- 一行一條，用「- 」開頭
- 寫使用者看得到的改變，跟 app 用同樣的詞

Shiyin 3.3.0: Japanese meetings and automatic language detection
- One item per line, starting with "- "
- What users will notice, in the app's own words
```

中文一段、空一行、英文一段；標題的版本號要跟 tag 一致，「：副標」可以省略。發布前可以先用 `node site/build.mjs --releases <檔案>` 拿一份跟 GitHub API 同樣格式的 JSON 試建置。

## 驗證時要注意

- 使用者可能正在用電腦：
  - 開 app 一律用 `open -g`（不搶前景）。
  - **絕對不要送按鍵**（System Events 的 `keystroke`、`key code`）：按鍵會送到最前面的 app，曾經把 ⌘W 送進使用者的 Chrome。關視窗用 `shiyinctl closewindow <id>`，其他操作也優先用 shiyinctl。
  - 不要點會跳出選單或系統面板的按鈕（分享、存檔對話框），會出現在使用者的畫面上。
  - 不要用 AppleScript 叫 app 結束：有工作在跑時會跳出確認對話框。建置腳本會自己處理結束。
- 截圖：
  - `shiyinctl snapshot` 用 ImageRenderer，不會跳出視窗，但畫不出 AppKit 元件（TextField、Picker、ScrollView 會變成黃色佔位框），適合看排版、顏色。
  - 要看真正的畫面，用 `screencapture -x -o -l <視窗編號>` 只擷取拾音的視窗（視窗編號用 CGWindowListCopyWindowInfo 依 PID 找）。螢幕鎖定時拍不到。
- 從這個終端機（Claude Code）用 `open` 開 app，app 會繼承 Claude Code session 的環境變數，子程序的 `claude` 會沿用這個 session 的登入。要模擬使用者的電腦（例如沒登入）時，用 `env -i HOME=… PATH=/usr/bin:/bin SHIYIN_HOME=… <app>/Contents/MacOS/Shiyin &` 啟動。
- 英文介面：直接執行 `build/Shiyin.app/Contents/MacOS/Shiyin -AppleLanguages "(en)"`（要用示範資料時前面加 `SHIYIN_HOME=…`）；中文介面就是系統語言。改會議語言用 `shiyinctl language <id> zh|en`。
- 還沒完成首次使用引導的話，每次啟動都會打開引導視窗，測試時記得用 `shiyinctl closewindow onboarding` 關掉。
- 編輯器、對話框的按鈕（完成、取消、儲存）要在真的 app 上用 AX 按過，不能只用 shiyinctl 直接存資料：3.2.0 的會議紀錄編輯器一按「完成」就閃退，就是因為當時只用 `shiyinctl editnotes` 驗證。
- 拾音的視窗在別的桌面（Space）時，AX 列不出它的視窗。要先用 System Events `set frontmost`，等 AX 看得到視窗再操作；使用者在用電腦時會被切回去，先跟使用者說一聲。
- 測試資料會寫進使用者真正的資料庫。測完用 `shiyinctl purge <id>` 刪掉自己建立的會議，改過的設定用 `defaults delete com.weiwei.shiyin <key>` 還原。
- 要用使用者的真實資料測（例如 migration）時，另外建一個 `SHIYIN_HOME`：資料庫用 `sqlite3 <正式資料庫> ".backup <複本>"` 複製（app 開著也安全）；`audio/recording/`（錄音中的暫存檔）不要複製，複本裡錄音中的會議（`source='recording'`、`audio_file=''`）刪掉；DEBUG 版加 `-llmProvider none -autoSummarize NO -correctTranscript NO` 啟動。2026-10-19 曾經複製到使用者正在錄的會議，DEBUG 版把它當成當掉的錄音復原、轉錄，還用使用者的 Claude 整理了一次。
- 設定（UserDefaults `com.weiwei.shiyin`）跟使用者正式安裝的 app 共用，換 `SHIYIN_HOME` 也一樣。測完要 `defaults read com.weiwei.shiyin` 檢查有沒有留下測試改的值（曾經把 `defaultMeetingLanguage` 留成 en，使用者升級後新會議會預設英文）。
- 說話者辨識不能用 `say` 合成的聲音測（會判成同一人），要用真人錄音，例如 AMI 會議語料（`~/Library/Caches/ShiyinSpike/eval32/ami-vp/`；標註在 `~/Library/Caches/ShiyinSpike/Datasets/ami_public_1.6.2`，AISHELL-4 在 `~/Library/Caches/ShiyinSpike/aishell4/`）。
- 說話者分不出人（3.5.1）：`shiyinctl voiceprint-eval` 的設定每一項可以加 `threshold`、`minSpeakers`、`numSpeakers`（字串），同一段錄音換設定比較分出幾個人；`shiyinctl rediarize <id> <人數|auto>` 只重新辨識說話者。
- 轉錄的記憶體、分段（3.5）：`shiyinctl transcribe-eval` 的輸出有各階段的記憶體峰值（`memory`）和分段位置（`seams`）；設定 JSON 加 `"chunkSeconds": 0` 不分段。量記憶體要每次重開 app（同一個 app 連續跑，前一次留下的記憶體會算進去）。
- 模擬錄音（`shiyinctl record-sim <檔案> <倍速> <1=雙聲道>`）的雙聲道是同一個讀檔迴圈同時送兩個聲道。不要改成兩個獨立的音源：高倍速時兩邊速度不同，寫檔時會補靜音，檔案變長，那是測試工具的假象。
- 只想確認能不能編譯時，用 `swift build -c debug --arch arm64 --disable-keychain --scratch-path ~/Library/Caches/ShiyinBuild`，不會動到正在跑的 app；`build-app.sh` 會先結束 app（有長時間測試在跑時不要執行）。
- 測 app 內更新：`scripts/dev/make-update-fixtures.sh <資料夾>` 產生正常、SHA 不符、簽章不符、同版本四種更新檔，在那個資料夾跑 `python3 -m http.server 18765 --bind 127.0.0.1`，再用 `shiyinctl setupdateurl http://127.0.0.1:18765/good.json`、`checkupdate`、`installupdate`。
- 全域快捷鍵不能用送按鍵測（見上面），用 `shiyinctl hotkey` 模擬按下。
- 日文（3.3）：`scripts/dev/make-eval-data-ja.py` 產生日文評估資料（合成會議、國會聽證、FLEURS）。`shiyinctl import lang=ja <檔案>` 用日文匯入（不用改共用的預設語言）；`shiyinctl detect-eval <音檔> <開始秒數> <輸出.json>` 用 app 的語言偵測跑一段；`shiyinctl recordlang <zh|en|ja>` 錄音中換語言（等於按「改回」）；`shiyinctl model start|pause|delete japanese|languageDetection` 管其他模型。
- 設定頁是 `Form`，`shiyinctl snapshot` 畫不出來（一片空白）。 要測按鍵或右鍵時，用 `CGEvent.postToPid(拾音的 pid)` 只送給拾音（不經過最前面的 app）；輸入框和清單的焦點用 AX 的 `AXFocused` 設，不要靠點擊。座標點擊（`click at`）會點到當下最上層的視窗，點之前一定要確認拾音在最前面。測模型下載出錯用 `shiyinctl modelurl <模型> <網址>` 指到本機伺服器。要看真的畫面：把 app 拉到前景（System Events 的 `set frontmost`），用 AX 點 app 自己的選單和按鈕（設定的分頁是 `button "錄音與轉錄" of toolbar 1`；4.1 起設定只有整理工具、錄音與轉錄、說話者與詞彙、進階四頁，design §33.3），再 `screencapture -l <視窗編號>`；螢幕上鎖時截不到。選單裡的選項用 AX 點會寫進共用的 UserDefaults，測完要 `defaults delete`。
- 示範資料夾的 `models` 是指到正式資料夾（`~/Library/Application Support/Shiyin/models`）的連結：在示範資料夾下載的模型會存進正式資料夾；設定頁的「模型」大小在示範資料夾會顯示 0（不跟著連結算）。
- `AVAudioFile` 關檔後 `length` 會變 0，不能用它判斷錄了多久（曾經因此刪掉整場錄音）。
- zsh 內建的 `log` 會蓋掉系統指令，要用 `/usr/bin/log`；zsh 不會依 IFS 切字串；`rm -f *.wav` 沒有符合的檔案時會報錯。
- bash 腳本裡，變數後面緊接全形字時要寫成 `${VAR}`，否則全形字的位元組會被吃進變數名稱。
- 用腳本做大段替換後，檢查檔案的行數和結尾：曾經因為替換腳本漏接後半段，把整個檔案截斷。
- 整理工具（3.4）：測 API 用假伺服器 `scripts/dev/fake-llm-server.py` 加上 `shiyinctl llmurl`；key、整理工具、模型用只放記憶體的 `llmkey`、`llmprovider`。DEBUG 版（3.4.1 起）預設不讀真的鑰匙圈，要測時加 `-debugRealKeychain YES`（會讀使用者存的 key，可能跳出確認視窗）。**不要用 `setkey` 和 `defaults write` 測**：鑰匙圈項目綁定建立它的 app 簽章，ad-hoc 重建後或使用者自己的拾音去讀都會跳出系統的存取確認；整理工具設定跟使用者的拾音共用。
- 使用者的拾音可能也開著（`~/Desktop/Shiyin.app`），跟測試用的 DEBUG 版共用設定和視窗狀態。DEBUG 版用 `open -g -n … --args -ApplePersistenceIgnoreState YES` 啟動，否則可能照上次被 `pkill` 時的狀態還原成一個視窗都沒有；設定值可以用啟動參數帶（`-llmProvider gemini`），只在這次執行有效。
- 開關類設定的啟動參數（`-autoSummarize NO`、`-correctTranscript NO`）在 3.4.1 以前的建置沒有作用：讀進來是字串 "NO"，`as? Bool` 轉不成功就用預設值（開）。3.5 起 `AppSettings` 改用 `bool(forKey:)`。測舊版時不要以為加了參數就不會整理；`-llmProvider none` 一直有效。
- 要跟舊版比對（例如匯出格式）時，用 `git worktree` 建置舊版的 DEBUG 版。3.4.0 以前的 DEBUG 版啟動時就讀真的鑰匙圈，會在使用者畫面上一直跳出存取確認、app 卡住（2026-10-19），建置前先把 worktree 裡的 `Keychain.get` 改成直接 `return nil`；啟動後確認 log 有「debug control installed」。`shiyinctl` 會廣播給所有 DEBUG 版，同時只能開一個，3.4.0 的 `quit` 不會結束 app，要 `kill` 並確認程序真的不在了（曾經因為舊版沒結束，兩個版本同時寫同一個匯出檔）。
- 螢幕上鎖時 AX 列不出視窗。筆記輸入框可以用 `shiyinctl notestype`、`notesvalue` 直接操作背後的 NSTextView。
- SwiftUI 的 `Menu` 在畫面上只畫標題的文字（標籤裡的背景、膠囊標記會被拿掉），`shiyinctl snapshot` 卻畫得出來；HStack 裡的 Text 加 `.fixedSize(horizontal: false, vertical: true)`，量最小高度時會用很窄的寬度，整個視窗的內容被往上推（3.5 驗收時都是在真的畫面上才發現）。ScrollView、popover 裡的 Text 才可以用 fixedSize。
- 錄音中的效能（3.5.1）：SwiftUI 裡任何一個 view 變了，整個視窗都要重新排版、重算最小尺寸，所以一直在變的東西（音量條）不要用 SwiftUI 觀察的屬性（改用 AppKit 的 `LevelMeter` 自己定時讀），`@Observable` 的屬性只在值真的變了才設。量 CPU：用使用者的錄音 `record-sim` 1 倍速，暖機 30 秒後取 `ps -o time` 的差；`metrics` 的 `maxMainGapMs` 是主執行緒最長卡住多久、`volatileUpdate` 是即時文字更新幾次；`livescroll <0～1>` 捲即時逐字稿（等於拖捲軸）。視窗不在目前的桌面時，程式內的輔助使用元件樹是空的，`press` 找不到按鈕。
- 說話者的修正（3.6）：`mergespeaker`、`reassign`、`undospeaker`、`speakerhint <id> dismiss`；`voiceprint-eval` 的設定加 `dump` 會倒出每個片段的聲紋，用 numpy 離線試判斷方法（分群時同一個片段的說話者會被強制分開，所以碰撞率只在分出 1 群時有用）。收音提醒（3.7）：state 的 `recorder.levelCheck`；用 afconvert 轉成 16 kHz、numpy 照同樣的算法先離線算，再用 `record-sim` 驗證。視窗在別的桌面時 AX 列不出它的元件（小視窗、popover、右鍵選單都按不到），`shiyinctl summon` 把視窗移到目前的桌面（會出現在使用者的畫面上，先確認使用者不在電腦前）。
- 要按真的畫面上的按鈕時用 `scripts/dev/axpress.swift`（`axpress <pid> list|press <role> <name> [第幾個]|focus|setvalue|insert <role> <placeholder 或現值> <第幾個> [文字]`，直接對 pid 走 AX API，只碰標題是示範會議的視窗）。改 SwiftUI 的 TextField 要用 `insert`（給焦點後設 AXSelectedText，會換掉整個欄位的字、綁定會更新）；`setvalue` 設 AXValue 綁定不會動。不要用 CGEvent 送文字：會經過使用者的輸入法，跳出候選字視窗。按下 `Menu` 之後開著的選單掛在 app 的 AXMenu 底下，`list`／`press AXMenuItem <名稱>` 也看得到、按得到（選單會出現在使用者畫面上，按完用 Escape 關）。**不要用 System Events 的 `whose unix id`**：使用者自己的拾音也叫 Shiyin，兩個同名的 process 會抓錯（2026-10-23 抓到使用者正在看的會議）。程式內的 `shiyinctl press` 看不到 SwiftUI 內容列裡的按鈕（時間點、待確認的標記），只有系統的 AX 看得到。視窗要在目前的桌面：`summon` 後依 pid 啟用（`NSRunningApplication(processIdentifier:).activate`）讓它到最前面，`screencapture -l` 的視窗編號要重新找（選單列的 1728×33 不是主視窗）。**不要用 `open <DEBUG 的 app 路徑>` 拉到前面**：DEBUG 版剛好不在時（2026-10-29 summon 遇到 preview 視窗當掉，已修），`open` 會開出一份沒有 `SHIYIN_HOME`、沒有測試參數的新 DEBUG 版，直接用使用者正式的資料（design §37.4）。
- 用 AX 操作的小技巧：設定頁要捲動時，設垂直捲軸的 `AXValue`（0～1）；選單的「設定⋯」用的是 ⋯（U+22EF）；只想按某個視窗裡的按鈕時，從那個視窗（`AXWindows` 裡標題相符的）往下找，不然會按到別的視窗裡同名的按鈕。
- 官網截圖（website.md §16.4）：Mac 用 `shiyinctl windowsize 960 672` 調視窗、`screencapture -l` 在別的桌面也截得到；Windows 在虛擬機 150%、深色，點過時間點要再點一次分頁讓提示框消失。
- 設定頁、引導在背景打不開又不想搶前景時，用 `shiyinctl preview <settings [分頁編號]|onboarding [步驟]> [front]` 開在桌面層（被所有視窗蓋住；設定的分頁 0 整理工具、1 錄音與轉錄、2 說話者與詞彙、3 進階；引導的步驟 0～2），再 `screencapture -l` 截。桌面層的視窗 AX 看不到，要按裡面的按鈕就加 `front`（一般視窗層，會出現在使用者畫面上），用 `AXPRESS_TITLE=onboarding-preview axpress …`。設定頁打開時可能把欄位寫回共用設定，測完 `defaults read` 比對。
- 會議頁（4.1，design §33）只有三個分頁 summary（紀錄）、transcript、ask：`shiyinctl tab review`、`tab topics`、`tab notes` 是別名（review 切到紀錄並展開頂部的待確認條，notes 切到逐字稿並展開「我的筆記」）；state 的 `reviewExpanded`、`userNotesExpanded` 看展開狀態。紀錄分頁的 `snapshot` 畫得出來（ReadingColumn 在 snapshot 模式不用 ScrollView），待確認條的按鈕 AX 名稱是「待確認 N 題」。
- 企業伺服器（design §36，伺服器是另外的私人專案）：DEBUG 版用 `shiyinctl enterprise login <網址> <信箱> <輸出.json>`（伺服器開發用的登入，不經過瀏覽器）、`enterprise logout|expire|local <會議 id>`，`state` 的 `enterprise`；伺服器在本機用開發用的設定跑，DEBUG 版連 `http://127.0.0.1:<埠>`。登入會寫進共用設定的 `enterpriseServerURL`、`enterpriseServerAccount`，測完 `defaults delete`；用啟動參數帶 `-transcriptionEngine server -llmProvider enterprise` 測，測前存一份 `defaults read`、測完 diff（登出曾經把使用者的 `llmProvider` 刪掉）。
- 轉錄引擎的評估（4.1，design §32）：`shiyinctl transcribe-eval` 的設定 JSON 用 `"engine": "qwen"`，`tuning` 可以加 `qwenChunkSeconds`、`qwenRetry`、`qwenGuard`、`qwenSplitMixed`、`qwenCutsAny`；`shiyinctl qwenchunks <錄音> <輸出.json> [秒數] [any] [mixed]` 只倒出說話者片段和分段；`shiyinctl s2tw <in.txt> <out.txt>` 簡轉繁；`shiyinctl model start|pause|delete qwen` 兩個檔一起。試新模型用 llama-server 的原型工具在 `~/Library/Caches/ShiyinSpike/qwen3asr/`（`qwen-eval.py`、`run-sets2.sh`、`batch-*.sh`；評估集 `eval32/datasets.json` 有 `tw-taimecs`、`tw-ntuml`）。**GPU 的工作一次只跑一個**：llama-server 跟 DEBUG 版的 whisper／Qwen 同時跑會互相拖慢 7 倍、asr-eval 超時；llama-server 跑幾小時會變慢到每個 token 20 秒，每批重開。長時間的背景評估用 `caffeinate -i` 包起來：2026-10-24 Mac 在 07:29 休眠到 12:26，背景工作全停。
