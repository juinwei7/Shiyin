# 拾音 Shiyin：任務

> 只列還沒完成的任務。做完、照「驗證」實際跑過，就從這裡刪掉，跑了什麼、結果如何寫在 commit 訊息。
> 3.4.0 以前所有任務的驗證紀錄和變更紀錄：`git show b621ed6:docs/tasks.md`（requirements、design 裡的任務編號都指那一份）。
>
> - `[ ]` 還沒做；`[~]` 程式完成，剩下的驗證要等使用者
> - 新任務照 SDD：先寫進 `requirements.md`、改 `design.md`，再加到這裡
> - 需求見 `requirements.md`，設計見 `design.md`（以下簡稱 §）

## 3.5（R19、R20，design §21；2026-10-19 使用者已確認功能清單，自己驗收）

順序：資料表和工作線先做（後面都會用到）；小的改進先做，再做新功能；分段轉錄最後做（要先量）。測整理工具一律用假伺服器（`scripts/dev/fake-llm-server.py`）或使用者已登入的 Claude CLI；新的提示詞至少用真的整理工具跑一次。

- [~] **M9 聲紋：比對、學習、設定**（§21.4，R19.3，D29）：比對、學習、改正、設定頁、畫面上的「自動」標記和選單都驗證過
  - 待使用者：中文會議、筆電麥克風、線上會議的聲音實際用一陣子，看門檻 0.65 合不合適
- [~] **M12 偵測會議、提醒錄音**（§21.7，R19.4，D31）：提醒、開始、停止、不要再提醒、恢復提醒、30 秒收起、設定都驗證過（模擬的 app）
  - 待使用者：真的用 Zoom 或 Meet 開會；打開通知權限後看通知的按鈕
- [~] **M13 全域快捷鍵標記重點**（§21.10，R19.5）：註冊時機、標記、回饋、關掉設定都驗證過
  - 待使用者：在別的 app 實際按 ⌃⌥⌘B
- [~] **M17 驗收、準備發布 3.5.0**：版本、README、使用說明、隱私說明、驗收結果（requirements 第 15 節）都完成；提示詞、請求、匯出跟 3.4.1 相同，英文介面和日文會議走過，release 建置成功，各項在真的畫面上用 AX 按過
  - 沒在畫面上看過：匯出對話框的「正在匯出…」（要按會跳出存檔對話框的「匯出」）

## 3.5 之後的修正（R21，design §22；2026-10-20）

- [~] **K1 讀 API key**（§22.1，R21.1～R21.4，D36）：用 `keychainsim` 模擬驗證過（見 commit）
  - 待使用者：真的鑰匙圈跳出確認時按「拒絕」，整理失敗的訊息要是「macOS 沒有讓拾音讀取…」（確認 macOS 回的錯誤碼在 denied 那幾個裡）；按「允許」（不是永遠允許）後，同一次開著拾音整理、校正、猜說話者只問一次

## 錄音中畫面卡住（R25，design §25；2026-10-20 使用者回報）

- [~] **U1 驗收**：CPU 59% → 22%、30 分鐘模擬錄音一直有回應、畫面在真的螢幕上用 AX 按過（requirements 第 20 節）
  - 還差：使用者用新版實際錄一場會議

## 官網

- [~] **首屏示範**（docs/website.md W10、§10）：2026-10-19 上線（f00de71）。已用 headless Chrome 看過三種語言的整段播放、淺色深色、桌機和 390px 寬、沒有 JavaScript；正式網址在真的 CSP 下會自動播
  - 還差：使用者用真的 iPhone 看手機版

## 等真實會議錄音

目前只用公開語料（AISHELL-4、AMI）和合成會議驗證過。使用者已經用拾音錄過 3 場會議（2026-10-19，最長 109 分鐘），還沒有校正好的標準答案。

- [ ] **真實會議驗證與預設值**（R14 驗收、Q2；原 T-A1、T-I0、T-I2、T-I5）
  - 需要使用者：一段 10～15 分鐘的真實會議錄音（最好是線上會議、中英夾雜，與會者同意），其中 5 分鐘校正成標準答案
  - 做：用評估工具（`shiyinctl transcribe-eval`、`scripts/dev/asr-eval.py`）跑 Breeze／macOS 內建 × 逐字稿校正開關 × 分聲道，決定預設引擎和開關的預設值；分聲道目前不在設定裡開放（§17.3），這次決定要不要開放
  - 驗證：§15 T-A1 的標準（5 分鐘不超過 10 處錯字）；中文、英文都不能比現在差
  - 參考：AISHELL-4（遠距收音）Breeze CER 30.5%、macOS 內建 25.1%，錯誤以漏字為主
- [~] **端到端驗收**（原 T-F7）
  - 用真實會議走一次：錄音（麥克風＋電腦聲音）→ 即時逐字稿 → 正式轉錄與說話者 → 整理 → 校稿 → 手動編輯 → 全部格式匯出 → 封包匯出再匯入
  - 已經用 AMI 英文片段模擬錄音走完；要用真實的中文會議再走一次

## 等使用者在真的 Mac 上操作

這些會跳出系統的權限詢問或選單，使用者不在時不能做。

- [~] **麥克風和電腦聲音**（§5.1、R1；原 T-A2、T-B4）：播 YouTube 或 Meet 錄 5 分鐘，沒有斷音、兩邊時間差在 100 ms 以內；權限被拒時的說明
- [~] **錄音中換裝置**（§20.2、R18.2；原 L10）：錄音中接上、拔掉耳機，麥克風重新接上、錄音不中斷；換輸出裝置，電腦聲音用新的裝置繼續錄。看門狗、重新連接、雙聲道補靜音已經用模擬錄音驗證過
- [~] **錄音中畫面**（§5.3、R2；原 T-B5）：用真的麥克風看即時文字和「回到最新」按鈕
- [~] **通知**（R11.2；原 T-F4）：這台電腦對拾音的通知權限是「拒絕」，打開後確認實際收到通知、點通知會選到那場會議
- [~] **全域快捷鍵**（R11.3；原 T-F8）：在別的 app 按 ⌃⌥⌘R，沒在錄就開始、在錄就停止
- [~] **分享**（R7.5；原 T-D6）：匯出對話框按「分享…」選 Mail，附件有帶過去
- [~] **API 整理工具用真的 key**（R6.1、R17；原 T-C2、T-K3）：Anthropic API 整理和校稿；OpenAI API（目前的帳戶沒有額度，只測到錯誤訊息）；Groq 等 OpenAI 相容服務（只用 Codex 回答的本機伺服器測過）

## 簽章

- [~] **自簽憑證**（§12、R10.7、D5；原 T-A5、T-F5、T-F9）：3.4.1（2026-10-19 發布）是第一個用「Shiyin Self-Signed」簽章的版本（Release 的 DMG 裡的 app：Authority=Shiyin Self-Signed，簽章檢查通過）。已驗證：CI 簽出來的 app 在不信任這張憑證的 Mac 上簽章檢查通過；ad-hoc 的 3.4.0 可以更新到憑證版；憑證版會擋下 ad-hoc 版
  - 驗證（要使用者）：用 app 內更新從 3.4.0 升級，麥克風、錄電腦聲音的權限還在嗎；用 API key 的整理工具會不會跳出鑰匙圈的存取確認（從 ad-hoc 換過來可能會問一次，之後的更新不應該再問；2026-10-20 使用者裝 3.5.0 後又跳出要輸入登入密碼的確認，不確定 key 是哪一版存的。沒有 Team ID 的程式，鑰匙圈可能用每一版的 cdhash 認程式，那每次更新都會問一次：下次更新後再看一次，會問的話在 README、版本說明寫清楚要按「永遠允許」）；在另一個 macOS 帳號照安裝說明安裝 DMG

## Windows 版（R26～R28，design-windows.md；2026-10-20 使用者決定方向）

進度（2026-10-21）：W1～W8 的程式都寫好了，不碰畫面的部分（`Shiyin.Core`、`Data`、`LLM`、`Engines`、`Services`）在 Mac 上用 `dotnet test` 測過（77 個）；畫面（`Shiyin.App`）、平台（`Shiyin.Platform.Windows`）在 Mac 上只編譯過，還沒在 Windows 上跑過。剩下的驗證要虛擬機（W0.1）和推到 GitHub 讓 CI 跑（W1.2）。

順序：先做實驗（結果會改設計），再做不依賴 Windows 的 `Shiyin.Core`（在 Mac 上 `dotnet test`），然後畫面、匯入和轉錄、錄音、說話者、AI 功能、系統整合、打包。實驗的通過標準在 design-windows §17；虛擬機做不到的標 `[~]`，寫明要使用者在 Windows 電腦上試什麼。使用者在用拾音錄會議時（Mac 的 `audio/recording/` 有檔案）不開虛擬機、不跑重的工作。這一段的「§」指 `design-windows.md`，「M§」指 `design.md`。

### W0 環境和實驗

- [ ] **W0.1 開發環境**（§15.2）
  - 要使用者：裝 Parallels Desktop（不用 VMware Fusion：ARM 版 Windows 沒有音效驅動）和 Windows 11 ARM64，安裝、授權要使用者操作；決定買 Pro 版（有 `prlctl exec --current-user`）或用 Standard 版加桌面代理；同意在 Mac 裝 .NET 10 SDK
  - 做：虛擬機設定自動登入、不鎖定；裝 .NET 10 SDK、Visual Studio Build Tools（C++、ARM64 和 x64）、Git、OpenSSH Server，clone repo；把 Mac 的模型複製過去
  - 驗證：Mac 上 `ssh` 進虛擬機跑 `dotnet --info`、`git -C <repo> log -1`；Mac 上 `dotnet --info`
- [ ] **W0.2 T-W1 自動化**（§15.3、D53）：WinUI 3 小 app、桌面代理、named pipe、截圖、UI Automation
- [ ] **W0.3 T-W2 錄音**（§6）
- [~] **W0.4 T-W3 whisper.dll**（§8、D46）：Mac 的部分做完了（shim、P/Invoke、同樣的取樣結果跟 Mac 逐段相同，§8）
  - 還差：建 x64（CPU 各版本＋Vulkan）、ARM64 的 dll（W8.1 的 CI）；虛擬機、CI x64、CI ARM64 的倍速和記憶體峰值，寫進 N2、N4 的數字（requirements 第 22 節 R27）；Media Foundation 解碼後再跟 Mac 比一次錯字率
- [~] **W0.5 T-W4 說話者辨識**（§9、D47、D58）：分群改成自己做，AMI、AISHELL-4 跟 Mac 一樣好；聲紋門檻 0.6、分不準的提示 0.37（§9）
  - 還差：使用者的麻將錄音給「至少 4 人」（要使用者給檔案的位置）；1 小時的錄音在 CI x64 3 分鐘內處理完
- [~] **W0.6 T-W5 即時逐字稿**（§7、D44、D61）：Mac 上定稿延遲中位數 0.50～0.62 秒、第 90 百分位 0.68～0.83 秒（中英日）；發現原本的模型（粵語微調版）日文、英文很差、遠距收音的 VAD 漏掉八成，換成原版模型＋VAD 門檻 0.35＋自動增益（D61）：快速辨識中文合成 9.2%、AISHELL-4 26.4%、AMI 39.0%、日文 15.5～28.1%
  - 還差：虛擬機的 CPU 用量
- [ ] **W0.7 T-W6 claude、codex CLI**（§10）
- [ ] **W0.8 T-W7 安裝和更新**（§14、D50）
- [ ] **W0.9 T-W8 偵測會議**（§12.4）
- [ ] **W0.10 T-W9 媒體**（§8、§13）：結果寫進 R27 的 R3.1、R9.4
- [ ] **W0.11 T-W10 長會議畫面**（§11.2）

### W1 Core（在 Mac 上測）

- [~] **W1.2 方案骨架**（§1、§3）：方案、`global.json`、套件版本集中管理、建置輸出放在 repo 外都好了；`.github/workflows/windows.yml`（`windows-latest`、`windows-11-arm`、`macos-latest` 跑 `dotnet test`）寫好了
  - 還差：推到 GitHub 讓 CI 跑一次（要使用者同意推 `windows` 分支）。套件的授權見 W7.6

- [~] **W1.3 不碰畫面的 app 邏輯**（§1 的 `Shiyin.Services`）：設定（settings.json、`--set`）、模型下載（續傳、SHA-256、多個檔案的模型）、匯入、.shiyin 封包、轉錄流程（分段、分聲道、快速辨識、說話者辨識）、工作佇列（三條線、取消、模型還沒好時先等）、錄音（寫檔、暫停、看門狗、收音提醒、空間不足、麥克風權限、即時逐字稿、自動偵測語言、模擬錄音）、會議的操作（對應 Mac 的 `AppModel`）。`dotnet test` 用假的引擎、假的整理工具、本機伺服器測過（`ServicesTests`、`RecorderTests`、`ClusteringTests`）
  - 也做了：對會議提問、問所有會議（M§21.8）；更新檔的驗證（更新金鑰的簽章、版本、架構、SHA-256，§14.2）；偵測會議的時間規則（M§21.7）；回報問題的內容（M§21.9）；Word 匯出（§13，OpenXml 的 schema 檢查零錯誤）。`AppTests`、`DocxTests` 測過
  - 還差：畫面接上之後在虛擬機的 app 裡各跑一次
- [~] **W1.4 Windows 平台層**（§1 的 `Shiyin.Platform.Windows`）：WASAPI 麥克風和 process loopback（排除拾音的程序樹、補靜音）、Media Foundation 解碼和 AAC 壓縮、認證管理員、找 claude/codex（不經過 .cmd）、防止睡眠、Job Object、偵測會議（ConsentStore）、全域快捷鍵、Velopack、回報問題的系統資訊。在 Mac 上用 `EnableWindowsTargeting` 編譯過
  - 還差：全部要在 Windows 上跑過（W0.3 T-W2、W0.7 T-W6、W0.8 T-W7、W0.9 T-W8、W0.10 T-W9）

### W2 app 骨架

- [~] **W2.1 主視窗**（§11.1）：側欄（資料夾、搜尋、垃圾桶、匯入）、會議內容六個分頁，讀示範資料的複本
  - 驗證：虛擬機裡截圖；UI Automation 切換分頁、點搜尋結果跳到那一段
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W2.2 介面文字、英文介面**（§11.4、R26.4）：翻譯表、`L()`、XAML 的 markup extension、`l10n.py` 的 Windows 模式（抽字串、`check`、從 Mac 的表 `sync` 英文）、設定裡的介面語言
  - 驗證：Windows 模式的 `check` 沒有問題；Mac 的 `l10n.py check` 結果不變；英文介面截圖沒有中文；設定改成中文、英文各重開一次
  - 做了：翻譯表直接讀 Mac 的 Localizable.xcstrings，加上 Windows 才有的 `Strings/Windows.xcstrings`（92 條英文）；`l10n.py windows-check`、`windows-apply`，CI 的 macOS 每次跑；設定的「進階」可以選介面語言。還差：英文介面的截圖、改語言各重開一次（要虛擬機）
- [~] **W2.3 DEBUG 控制通道**（§15.2）：named pipe、`shiyinctl.exe`、`state`、`select`、`tab`、`closewindow`、`quit`
  - 驗證：從 Mac 經 SSH 執行，讀到的狀態跟畫面一致
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W2.4 設定視窗**（§12.8）：七頁、settings.json、啟動參數覆蓋設定
  - 驗證：每頁截圖；改設定後重開還在；`--set` 只在這次有效、settings.json 沒被改
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W2.5 系統匣、關視窗、結束**（R27 R1.7）
  - 驗證：關掉主視窗後程序還在、系統匣小視窗打得開；有工作在跑時「結束」會確認（UI Automation 按「取消」「結束」各一次）
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W2.6 單一執行個體、開檔**（§12.1）：驗證：已經開著時再用參數開一個檔案，原本的拾音收到並匯入、第二個程序結束
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）

### W3 匯入和轉錄

- [~] **W3.1 匯入**（R3、R27 R3.1）：解碼、拖放、Ctrl+O、檔案總管開啟、日期推算、srt/vtt/txt、Markdown 筆記
  - 驗證：T-W9 的每種格式匯入一次；日期推算跟 Mac 的測試資料相同
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W3.2 模型下載**（M§7）：續傳、SHA-256、跟 Mac 同一份網址
  - 驗證：用 `modelurl` 指到本機伺服器，下載中斷後續傳完成；SHA 不符時刪掉並報錯
  - 做了：`ModelStore`（Range 續傳、SHA-256、多個檔案的模型、暫停後再開始會等上一次停下），`ServicesTests` 用本機伺服器測過續傳、SHA 不符、HTTP 404、伺服器不支援續傳。還差：設定頁、引導裡的下載在虛擬機跑一次
- [~] **W3.3 正式轉錄**（§8、R4、R28.4）：whisper.dll、分段、過濾、低信心、進度、預估時間
  - 驗證：`Shiyin.Eval transcribe-eval`（Mac 上）和 app 的 `transcribe-eval`（虛擬機）跑 AISHELL-4、AMI、國會聽證、FLEURS 日文（`~/Library/Caches/ShiyinSpike/`），用 `asr-score.swift` 算錯字率，跟 Mac 差 ≤ 1 個百分點；4 小時的錄音記憶體峰值
  - 做了：Mac 上的評估見 W0.4；`TranscriptionPipeline`、`JobRunner` 用假的引擎測過。還差：虛擬機裡的 app 跑 `transcribe-eval`、4 小時的記憶體峰值
- [~] **W3.4 快速辨識**（R27 R4.1、R20.8）：驗證：沒有 Breeze 時新會議用快速辨識轉完、模型開始下載；放進模型後出現「重新轉錄」提示
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W3.5 工作佇列**（M§21.2、R4.2、R4.3）：三條線、取消、重試、重開後續跑、當掉兩次標失敗
  - 驗證：同 Mac 的方法（`cancel`、`retry`、轉錄中 kill 程序再開）
  - 做了：`JobRunner`（三條線、取消、重開後續跑、模型還沒好時先等），`ServicesTests` 測過從轉錄到整理、壓縮、校正、猜說話者整串；當掉兩次標失敗用 Data 的 `RequeueRunning`。還差：虛擬機裡轉錄中 kill 程序再開
- [~] **W3.6 播放器、時間軸**（R8）：倍速、點時間跳播、播放中高亮、時間軸的重點和待確認標記
  - 驗證：UI Automation 點時間點、改倍速；2,000 段的會議播放時 `metrics` 的 UI 執行緒最長卡住時間跟 Mac 同一個標準（R20.3）
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W3.7 逐字稿分頁**（R8.1、R8.3、R20.6）：搜尋、篩選說話者、單段編輯、用詞修正、草稿保留
  - 驗證：單段編輯的「完成」「取消」、用詞修正的「取代」都用 UI Automation 按過（CLAUDE.md：不能只用控制通道存資料）
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）

### W4 錄音

- [~] **W4.1 錄音**（§6、R1）：麥克風、電腦聲音、兩者、暫停、雙聲道 WAV
  - 驗證：T-W2 的標準；`record-sim` 4 小時不中斷
  - 做了：`Recorder`、`RecordingWriter`（對齊、補靜音、暫停、標頭每 2 秒更新）、WASAPI 的麥克風和 process loopback（Platform）。`RecorderTests` 用模擬錄音測過雙聲道、暫停、即時逐字稿。還差：WASAPI 在虛擬機錄一次（T-W2）
- [~] **W4.2 復原、換裝置、watchdog、磁碟空間**（R1.5、R18.2、R18.3）
  - 驗證：錄音中 kill 程序，重開後復原並轉錄；虛擬機切換音訊裝置後自動接回；模擬磁碟快滿的警告
  - 做了：復原（`RecoverRecordings`，`AppTests` 測過標頭沒更新的錄音、找不到會議的錄音、空的會議）、看門狗和換裝置（`RecorderTests` 用停住的模擬音源測過）、空間不足的提醒。還差：虛擬機裡切換音訊裝置
- [~] **W4.3 錄音畫面**（R1.4、R25）：音量條、計時、按鈕、窄視窗
  - 驗證：`record-sim` 1 倍速時 CPU 跟 `metrics`（同 Mac U1 的量法），真的畫面截圖、UI Automation 按暫停、標記重點、停止
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W4.4 即時逐字稿、我的筆記、重點**（§7、R2）：驗證：模擬錄音時暫定文字 1 秒內、定稿 5 秒內；往回捲動不會被拉走、「回到最新」；筆記每行有時間
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W4.5 自動偵測語言**（R16）：驗證：`detect-eval` 跑 Mac 用過的同一批片段，結果跟 Mac 相同
  - 做了：Mac 3.6 的 DEBUG 版 `shiyinctl detect-eval` 和 Windows 的 `shiyin-eval detect-eval` 跑同樣 36 個位置（AISHELL-4、中文合成、AMI、日文合成、國會聽證兩場、FLEURS）：36/36 判斷、用掉的秒數、三個語言的機率逐位元相同，36/36 判斷正確。還差：虛擬機裡錄音時真的切換一次（WASAPI 的聲音經過重新取樣）
- [~] **W4.6 收音提醒**（R24）：驗證：Mac 用過的太小聲、太吵、沒聲音的錄音，`record-sim` 出現同樣的提醒
  - 做了：Mac 用過的錄音是使用者的會議做的，改用 AMI 前 150 秒做四種：原本、調小 26 dB、加白雜訊（−28 dBFS）、60 秒後變 0。Mac 3.6 的 DEBUG 版 `record-sim` 2 倍速和 Windows 的 `Recorder.StartSimulated`（Mac 上跑）：原本都不提醒；調小 26 dB 都是「聲音小」（Mac 第 89 秒、Windows 第 89.5 秒）、加雜訊都是「吵」（74.6／74 秒）、變 0 都是「沒有聲音」（71.5／71.8 秒），差在讀狀態的間隔內；最後的說話、背景音量、計數完全相同。還差：畫面上的提示列（要虛擬機）；使用者的錄音要使用者同意才用
- [~] **W4.7 防止睡眠**（R1.9）：驗證：錄音和轉錄中 `powercfg /requests` 列出拾音，結束後消失
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] 使用者：真的筆電麥克風、藍牙耳機、線上會議錄一場

### W5 說話者

- [~] **W5.1 辨識、改名**（§9、R5、R28.6）：「至少 N 人」、每人的聲紋、分不準的判斷（sherpa-onnx 沒有，自己補）
  - 驗證：`Shiyin.Eval` 在 Mac 上跑 AMI 9 場、AISHELL-4、麻將錄音，人數和標錯時間符合 T-W4 的標準；改名後逐字稿和會議紀錄一起改
  - 做了：分群改成自己做（D58），AMI、AISHELL-4 跟 Mac 一樣好（W0.5）；改名、合併、復原在 `AppTests` 測過。2026-10-21 在虛擬機發現只有一個人的錄音被分成 3～7 人，加上「不減平均的群中心」的兩步合併（§9）：單人 6 段都是 1 位，其他結果不變；虛擬機裡「重新辨識說話者」後那段合成會議是 1 位。還差：麻將錄音（要使用者同意）
- [~] **W5.2 聲紋**（R19.3）：驗證：AMI 上誤認 ≤ 2%、自動帶入的比例跟 Mac 比；「不是他」、設定的改名、刪除、關閉
  - 做了：聲紋門檻 0.6（AMI 9 場：帶入 16/17、帶錯 0、陌生人 0）。還差：「不是他」、設定裡的改名、刪除在畫面上按一次
- [~] **W5.3 猜說話者**（R19.2）：驗證：假伺服器和真的 Claude CLI 各跑一次，建議的「套用」「忽略」用 UI Automation 按過
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W5.4 合併、改句子的說話者、復原、重新辨識、分不準的提醒**（R22、R23）：驗證：同 Mac 3.6 的驗收項目（`mergespeaker`、`reassign`、`undospeaker`、`rediarize`、`speakerhint`）
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）

### W6 整理和 AI 功能

- [~] **W6.1 整理、範本、待確認、修訂、版本、編輯器**（R6）：驗證：假伺服器和真的 Claude CLI；會議紀錄編輯器的「完成」「取消」、待確認的送出用 UI Automation 按過；復原回到上一版
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W6.2 草稿紀錄**（R19.1）：驗證：模擬錄音停止後出現草稿，正式版好了自動取代；改過的草稿留版本
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W6.3 逐字稿校正、詞彙表建議**（R14.4、R14.5、R20.2）：驗證：依詞處理、一次接受、忽略、單筆還原
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W6.4 對會議提問、問所有會議**（R19.6）：驗證：假伺服器和真的 Claude CLI；點出處打開那場會議並跳到時間點；取消 1 秒內停
  - 做了：`AskService`（一場會議、所有會議），`AppTests` 用假伺服器測過關鍵詞、挑會議、拿掉不在範圍的出處。還差：真的 Claude CLI、畫面上點出處
- [~] **W6.5 CLI 登入**（§10）：驗證：沒登入時的錯誤訊息、按「登入」開出終端機
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）

### W7 系統整合、匯出

- [~] **W7.1 通知**（§12.2）：驗證：完成、失敗、偵測會議的通知出現，點了打開那場會議；通知關掉時改用小視窗
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W7.2 全域快捷鍵**（§12.3）：驗證：用控制通道模擬按下（同 Mac 的 `hotkey`）；另一個程序先註冊同一組時顯示原因
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W7.3 偵測會議**（§12.4）：驗證：T-W8 的方法；「不要再提醒這個 app」、設定裡恢復
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] 使用者：真的 Teams、Zoom、Meet 會議
- [~] **W7.4 匯出**（§13）：docx、PDF、m4a、wav、剪貼簿、分享
  - 驗證：同一場會議 Mac 和 Windows 各匯出，docx、PDF 抽出文字比對相同；剪貼簿貼到 Word 有格式；分享視窗打得開（虛擬機裡）
  - 做了：Word（`DocxWriter`，OpenXml 的 schema 檢查零錯誤）、PDF（WebView2 印 A4）、m4a、wav、剪貼簿（純文字＋HTML）、分享（DataTransferManager）。Word 跟 Mac 比過（同一個 .shiyin 匯入兩邊再匯出，抽每段文字）：逐字稿三種語言、日文會議紀錄相同，中英會議紀錄只差 Mac 把表格攤成一格一段、空的主題多一個項目符號（§13）。還差：PDF、分享、剪貼簿在虛擬機試；PDF 跟 Mac 比抽出來的文字和頁數
- [~] **W7.5 資料夾、垃圾桶、壓縮、儲存空間**（R9）：驗證：同 Mac 3.0 的驗收項目
  - 程式寫好了（Mac 上編譯過），還差上面的驗證（要虛擬機）
- [~] **W7.6 首次使用引導、使用說明、回報問題、關於**（R10.2、R10.4、R19.7、R27 R13.1）：驗證：全新的 `SHIYIN_HOME` 走完引導；回報的內容沒有會議內容、key；授權條款列出所有套件和模型
  - 做了：「關於拾音」（系統匣、設定的「進階」）：版本、用到的軟體和模型，「完整的授權條款」用預設的文字編輯器打開；`scripts/windows/make-third-party-licenses.py` 依 app 實際用到的套件產生授權檔（套件、.NET、ONNX Runtime、Windows App SDK、WebView2、whisper.cpp 原文，模型列來源和授權），Mac 上 `dotnet publish -r win-x64` 看過安裝資料夾裡有 `Third-Party Licenses.txt`、所有 DLL 都沒有匯入 VC++ 執行階段。還差：上面的驗證（要虛擬機）

### W8 打包、發布

- [~] **W8.1 whisper.dll 的 CI 建置**（§8）：驗證：CI 產生 x64、ARM64 的 dll，雜湊記在 log；跟虛擬機自己建的轉錄結果相同
  - 做了：`scripts/windows/build-whisper.ps1`、`release-windows.yml` 的建置步驟。還差：推到 GitHub 讓 CI 跑（要使用者同意推分支）
- [~] **W8.2 安裝檔、安裝說明**（§14.1）：Velopack、檔案關聯、開始功能表、安裝說明（中英，SmartScreen、Smart App Control）
  - 驗證：在虛擬機另一個 Windows 帳號從 Edge 下載安裝，照安裝說明放行 SmartScreen；開始功能表的名稱依語言；雙擊 .shiyin 會匯入；解除安裝不刪資料
  - 要使用者：決定要不要用自己的名義申請 SignPath Foundation 的免費簽章（申請下來前沒有 Authenticode 簽章）
  - 做了：`scripts/windows/pack.ps1`（publish、vpk pack、安裝檔改名、檢查 onnxruntime.dll 只有一份）、檔案關聯（Velopack 的安裝、解除安裝 hook）。還差：上面的驗證全部（要虛擬機）
- [~] **W8.3 app 內更新**（§14.2、R27 R10.6）：更新金鑰（`make-update-key.ps1`）、下載後套用前驗證
  - 驗證：本機伺服器放正常、SHA 不符、更新金鑰不符、同版本四種更新檔（同 Mac 的 `make-update-fixtures.sh`）；錄音中不能更新；更新後麥克風權限、資料都在
  - 要使用者：同意把更新金鑰的私鑰放進 GitHub secrets
  - 做了：`Updater`（簽章、版本、架構、SHA-256；`AppTests` 測過正常、金鑰不符、SHA 不符、版本不對、架構不對、忙碌中六種）、Velopack 那一邊、`scripts/windows/update-key.cs`（Mac 上測過 keygen、sign、verify）。還差：產生正式的更新金鑰（要使用者同意把私鑰放進 GitHub secrets）、本機伺服器的更新測試
- [~] **W8.4 官網**（§14.4）：先改 `docs/website.md` §7
  - 驗證：`node site/build.mjs --releases <兩種混在一起的 JSON>` 建置成功，下載區有 Mac 和 Windows，`/download` 還是 DMG
  - 做了：真的 Releases 加一個假的 `win-v3.6.0` 建置成功，`/download` 還是 DMG、`/download/windows`、`/download/windows-arm64` 指到兩個安裝檔，下載區用 headless Chrome 看過（中文 1280 寬、日文 390 寬）；只有 Mac 的 Releases 時跟 main 的建置只差沒用到的樣式。還差：第一個 Windows 版發布時改常見問題「只有 Mac 版」、真的上線看一次
- [~] **W8.5 發布的工作流程**（§14.3、D51）：`release-windows.yml`，Release 不設成 latest；W8.4 的官網要先上線
  - 驗證：手動執行只產生 artifact；推測試用的 tag 到 fork（不是正式 repo）確認不會變成 latest、Mac 的 `releases/latest/download/shiyin-update.json` 還是 Mac 的
  - 做了：`.github/workflows/release-windows.yml`（推 win-v* 才發布、不設成 latest、更新說明沒簽章就不發布）。還差：上面的驗證
- [~] **W8.6 README、CLAUDE.md**：Windows 的系統需求、安裝、隱私（認證管理員、資料位置）；CLAUDE.md 補 Windows 的指令
  - 驗證：README 的系統需求跟 requirements R27 的 N4 一致、連結都打得開；CLAUDE.md 列的指令每個實際跑一次
  - 做了：README（中英）加〈Windows 版〉：系統需求（同 R27 N4）、安裝（SmartScreen、智慧型應用程式控制）、跟 Mac 不一樣的地方、資料位置和認證管理員、授權、從原始碼建置；寫明還沒有正式發布。CLAUDE.md 的 Windows 指令（Mac 上跑得到的都跑過）。還差：發布時把「還沒有正式發布」改成下載連結、記憶體和轉錄速度的數字（W0.4）、Windows 上的指令（`build-whisper.ps1`、`pack.ps1`）實際跑一次

### W9 驗收

- [ ] **W9.1 驗收**（requirements 第 22 節）：照第 8 節和 3.1～3.6 各節的驗收項目在虛擬機走一次，英文介面、日文會議；R28 的比對全部通過
  - 驗證：第 22 節的驗收結果表每一條 R26～R28、R27 的每一列都有 ✅ 或 🟡（🟡 寫明還差什麼）；Mac 的 `git diff v3.6.0 -- Sources` 只有 DEBUG 的程式
- [~] 使用者：x64 電腦（有顯卡、沒顯卡各一台最好）的轉錄速度、SmartScreen 的實際安裝體驗
