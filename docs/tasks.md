# 拾音 Shiyin：任務

> 只列還沒完成的任務。做完、照「驗證」實際跑過，就從這裡刪掉，跑了什麼、結果如何寫在 commit 訊息。
> 3.4.0 以前所有任務的驗證紀錄和變更紀錄：`git show b621ed6:docs/tasks.md`（requirements、design 裡的任務編號都指那一份）。
>
> - `[ ]` 還沒做；`[~]` 程式完成，剩下的驗證要等使用者
> - 新任務照 SDD：先寫進 `requirements.md`、改 `design.md`，再加到這裡
> - 需求見 `requirements.md`，設計見 `design.md`（以下簡稱 §）

## 減法：三個分頁、五個動作、四頁設定、三步引導（R44，design §33；2026-10-24 使用者決定先做減法）

M1～M6、M8（官網截圖）已完成（2026-10-25，Mac 和 Windows）。剩 M7 iPhone 之後。

- [ ] **M7 iPhone 接 R44**（`ios` 分支，之後）：待確認的條、設定分組

## Qwen3-ASR 轉錄引擎（R43，design §32；2026-10-24 使用者要求打磨辨識準確度）

Q1～Q6 已完成（2026-10-24，驗證在 commit 訊息；使用者決定預設維持 Breeze、Qwen 可選）。Q7 Windows 另外排。評估資料在 `~/Library/Caches/ShiyinSpike/eval32/`（`datasets.json` 已加 `tw-taimecs`、`tw-ntuml`），原型工具在 `~/Library/Caches/ShiyinSpike/qwen3asr/`（`qwen-eval.py`、`linktest/` 的 C 薄層）。

- [ ] **Q7 Windows 版接 R43**（§32.8、R43.9）：llama.cpp 預建 DLL（cpu／cuda／vulkan）、shim、模型下載、分段、重試、簡轉繁；標準答案比對
  - 驗證：`dotnet test` 通過；虛擬機上 `transcribe-eval` 跑 synth 的錯誤率跟 Mac 一樣

## 待確認就地回答、收音提醒送通知（R41、R42，design §31；2026-10-23 使用者決定先做使用者體驗）

V1～V3、V5～V8 已完成（2026-10-25 在虛擬機驗完 V5、V7）；剩 V4 的後台要使用者跑、V9 iPhone 之後。驗證用示範資料：把 `~/Library/Caches/ShiyinDemo/home` 複製到暫存的 `SHIYIN_HOME`，DEBUG 版加 `-ApplePersistenceIgnoreState YES -autoSummarize NO -correctTranscript NO` 啟動。

- [~] **V4 統計（Mac 加後台）**（R41.7、§31.3、§30.4）：程式完成，app 的累計和送出、worker 收新舊格式都驗過
  - 還差（要使用者跑，token 在鑰匙圈）：在 `workers/usage` 跑 `npx wrangler@4 d1 execute shiyin-usage --remote --file migrate-review.sql` 加三欄，再 `npx wrangler@4 deploy`；部署前後台收到帶 `review` 的資料會忽略那一組，不會失敗
- [ ] **V9 iPhone 接 R41、R42**（`ios` 分支，之後）：標記與就地回答共用 Mac 的 view；收音提醒走即時動態

## 還沒設定整理工具（R35，design §19.6；2026-10-22 使用者回饋）

- [ ] **iPhone 接 R35**（R35.5）：`ios` 分支接 Mac 的 commit；iPhone 沒有 CLI，沒選過時一律「還沒設定」，引導、設定頁只列 API 和「不整理」
  - 驗證：模擬器上沒選過時匯入一場，沒有整理失敗、會議頁有提示；選 Gemini 有「取得 API key」

## 等使用者在真的 Mac 上操作

2026-10-22 使用者確認：電腦聲音、偵測會議（有提醒）、通知、全域快捷鍵、分享、讀 API key 的確認視窗、更新後權限還在都沒問題，真實會議的基本功能沒問題；真的 API key 的整理工具不另外驗。

- [~] **錄音中換裝置**（§20.2、R18.2；原 L10）：錄音中接上、拔掉耳機，麥克風重新接上、錄音不中斷；換輸出裝置，電腦聲音用新的裝置繼續錄。看門狗、重新連接、雙聲道補靜音已經用模擬錄音驗證過

## 簽章

- [~] **另一個 macOS 帳號安裝 DMG**（R10.1；原 T-F5）：照安裝說明在另一個帳號裝一次（更新後權限保留、鑰匙圈的確認，使用者 2026-10-22 確認沒問題）

## 官網

- [~] **首屏示範**（docs/website.md W10、§10）：2026-10-19 上線（f00de71）。已用 headless Chrome 看過三種語言的整段播放、淺色深色、桌機和 390px 寬、沒有 JavaScript；正式網址在真的 CSP 下會自動播
  - 還差：使用者用真的 iPhone 看手機版

## Windows 版（R26～R28，design-windows.md；2026-10-20 使用者決定方向）

進度（2026-10-22）：4.0.0 跟 Mac 一起發布（`win-v4.0.0`），功能對齊 Mac 4.0.0。Mac 上 `dotnet test` 95 個、CI（windows-latest、windows-11-arm、macos-latest）都通過；虛擬機（§15.2）用 UI Automation 走過幾乎所有畫面；`release-windows.yml` 手動執行（run 39739673918）建好兩個架構的安裝檔，更新說明用正式的更新金鑰簽好，ARM64 版在虛擬機裝起來轉錄、檢查更新都正常；使用者確認在 Windows 電腦上錄音沒問題、朋友的 RTX 3060 用上 CUDA。需求在 requirements 第 22 節；虛擬機的驗收結果用 `git show 0e96276:docs/requirements.md` 看。

使用者在用拾音錄會議時（Mac 的 `audio/recording/` 有檔案）不開虛擬機、不跑重的工作。這一段的「§」指 `design-windows.md`，「M§」指 `design.md`。

### 要使用者的 Windows 電腦

- [~] **錄音中換裝置**（R18.2、§6）：錄音中換預設的麥克風、輸出裝置，錄音不中斷、接到新的裝置（W0.3、W4.2）
- [~] **偵測會議**（T-W8、§12.4、D55）：開 Teams、Zoom、Chrome 的會議，5 秒內有沒有提醒；ConsentStore 在真的電腦上讀得到（W0.9、W1.4、W7.3 的真的 app）
- [~] **自動偵測語言**（R16、W4.5）：`detect-eval` 36 個位置跟 Mac 逐位元相同、模擬錄音會自動換語言；要真的麥克風錄一次（WASAPI 的聲音經過重新取樣）
- [~] **播放**（R8、T-W9、T-W10）：改倍速時不變調、播放中高亮和跟著捲動、時間軸的標記（虛擬機沒有音效裝置，播放不會前進；W0.10、W0.11、W3.6）
- [~] **拖放、選檔匯入**（W3.1）：拖放、在選檔視窗選一個檔案（虛擬機裡用 UI Automation 操作 Windows 的檔案對話框不穩）
- [~] **Word**（W7.4、T-W9）：docx 用 Word 打開、剪貼簿的富文字貼到 Word
- [~] **安裝和更新**（T-W7、W0.8）：Smart App Control 開著時的畫面（虛擬機是評估模式，不會擋）；更新後麥克風權限還在
- [~] **速度**：x64 電腦（有顯卡、沒顯卡）的轉錄速度；真的筆電麥克風、藍牙耳機、線上會議錄一場

### 要使用者的帳號或同意

- [~] **claude、codex CLI**（T-W6、§10、W0.7）：沒登入時的狀態和錯誤訊息、「登入」開出終端機都在虛擬機驗過，整理、猜說話者、提問改用 Anthropic API 測過（W5.3、W6.1、W6.4）
  - 要使用者：登入 claude CLI 後真的整理、猜說話者、提問一次；原生安裝版（claude.ai/install.ps1）也試一次
- [~] **說話者辨識：麻將錄音**（T-W4、§9、W0.5、W5.1）：使用者的麻將錄音給「至少 4 人」（要使用者同意用這個錄音）
- [~] **程式碼簽章**（W8.2、§14.1）：要不要用使用者的名義申請 SignPath Foundation 的免費簽章（申請下來前沒有 Authenticode 簽章，SmartScreen 顯示「不明的發行者」）

### CI 上量

- [ ] **速度和記憶體**（T-W3、R27 N2、N4；W0.4、W3.3、W4.1）：CI x64、ARM64 的轉錄倍速和記憶體峰值（含 4 小時的錄音），寫進 N2、N4 和 README 的〈Windows 版〉；只有 CPU 時 beam、執行緒要不要改（§8）；1 小時的錄音說話者辨識 3 分鐘內；`record-sim` 4 小時不中斷
