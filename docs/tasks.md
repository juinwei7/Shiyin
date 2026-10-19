# 拾音 Shiyin：任務

> 只列還沒完成的任務。做完、照「驗證」實際跑過，就從這裡刪掉，跑了什麼、結果如何寫在 commit 訊息。
> 3.4.0 以前所有任務的驗證紀錄和變更紀錄：`git show b621ed6:docs/tasks.md`（requirements、design 裡的任務編號都指那一份）。
>
> - `[ ]` 還沒做；`[~]` 程式完成，剩下的驗證要等使用者
> - 新任務照 SDD：先寫進 `requirements.md`、改 `design.md`，再加到這裡
> - 需求見 `requirements.md`，設計見 `design.md`（以下簡稱 §）

## 3.4.1 穩定性修正（R18，design §20；2026-10-19 使用者已確認）

順序：小而獨立的先做；L2 的 migration v6 同時加 L3 要用的欄位。能重現的問題，改之前先重現一次。

- [~] **L10 錄音中換裝置、看門狗**（§20.2，R18.2，D25）：模擬錄音驗證過看門狗、重新連接、雙聲道補靜音
  - 待使用者：錄音中接上、拔掉耳機（麥克風重新接上、錄音不中斷），換輸出裝置（電腦聲音用新的裝置繼續錄）
- [~] **L12 發布 3.4.1**：版本 3.4.1、requirements 第 14 節驗收結果、匯出比對、release 建置都完成
  - 待使用者確認：`git push`（main 領先 origin），再推 tag：說明文字存成檔案，`git tag -a v3.4.1 -F <檔案>`、`git push origin v3.4.1`（已用 `node site/build.mjs --releases` 試建通過）
  - 發布後：下面「簽章」那一項的驗證（第一個用自簽憑證簽的版本）

```
拾音 3.4.1：穩定性修正
- 錄音中接上或拔掉耳機、換了輸出裝置，會自動接上繼續錄；有音源沒有送出聲音時，錄音畫面會提示並重新連接
- 磁碟空間不夠時會提醒；錄音寫不進磁碟時馬上停止並說明，已經錄到的部分會保留、照常轉錄
- 筆記自動存檔後切換分頁，不會再顯示舊的內容，接著打字也不會蓋掉之前的字
- 轉錄中結束拾音不會再當掉
- 用 Claude Code、Codex CLI 整理時，它們提早結束不會再讓拾音閃退
- 同一個工作讓拾音意外結束 2 次後就不再自動重試，可以按「重試」再試一次
- 復原上次沒正常結束的錄音時，出錯也不會刪掉錄音檔，下次開啟再試
- 匯出對話框一定匯出打開時的那一場會議，標題會寫出會議名稱
- 匯入 .shiyin 封包時會檢查錄音檔和逐字稿，有問題就不匯入
- 會議多的時候，存逐字稿、取代文字、刪除會議快很多
- 打開拾音時不再讀出 API key，更新後不會一開就跳出鑰匙圈的存取確認
- log 不再寫進整理工具回傳的錯誤內容

Shiyin 3.4.1: Stability fixes
- Plugging in or unplugging headphones, or switching output devices, while recording now reconnects automatically; if a source stops sending audio, the recording screen tells you and reconnects
- Shiyin warns you when disk space is low; if the recording can't be written to disk, recording stops right away with an explanation, and everything recorded so far is kept and transcribed
- After notes are saved automatically, switching tabs no longer shows old content, and new typing no longer overwrites what you wrote
- Quitting Shiyin while transcribing no longer crashes
- Shiyin no longer crashes when Claude Code or Codex CLI exits early while summarizing
- If a task makes Shiyin quit unexpectedly twice, it isn't retried automatically; click “Retry” to try again
- Recovering an interrupted recording never deletes the audio file, even if something goes wrong; Shiyin tries again next launch
- The export dialog always exports the meeting it was opened for, and its title shows the meeting name
- Importing a .shiyin package checks the audio file and transcript, and rejects broken packages
- Saving transcripts, replacing text and deleting meetings are much faster when you have many meetings
- Shiyin no longer reads your API keys at launch, so updating doesn't bring up keychain prompts as soon as it opens
- Logs no longer include error text returned by summarizers
```

## 等真實會議錄音

目前只用公開語料（AISHELL-4、AMI）和合成會議驗證過，使用者的資料庫裡也都是匯入的會議，還沒有用拾音直接錄過。

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

- [~] **麥克風和電腦聲音**（§5.1、R1；原 T-A2、T-B4）：播 YouTube 或 Meet 錄 5 分鐘，沒有斷音、兩邊時間差在 100 ms 以內；權限被拒時的說明；切換輸入裝置、拔耳機不會卡住
- [~] **錄音中畫面**（§5.3、R2；原 T-B5）：用真的麥克風看即時文字和「回到最新」按鈕
- [~] **通知**（R11.2；原 T-F4）：這台電腦對拾音的通知權限是「拒絕」，打開後確認實際收到通知、點通知會選到那場會議
- [~] **全域快捷鍵**（R11.3；原 T-F8）：在別的 app 按 ⌃⌥⌘R，沒在錄就開始、在錄就停止
- [~] **分享**（R7.5；原 T-D6）：匯出對話框按「分享…」選 Mail，附件有帶過去
- [~] **API 整理工具用真的 key**（R6.1、R17；原 T-C2、T-K3）：Anthropic API 整理和校稿；OpenAI API（目前的帳戶沒有額度，只測到錯誤訊息）；Groq 等 OpenAI 相容服務（只用 Codex 回答的本機伺服器測過）

## 簽章（下一次發布後驗證）

- [~] **自簽憑證**（§12、R10.7、D5；原 T-A5、T-F5、T-F9）：2026-10-19 起 CI 用「Shiyin Self-Signed」簽章（secrets 已設定），下一次發布的版本開始生效。已驗證：CI 簽出來的 app 在不信任這張憑證的 Mac 上簽章檢查通過；ad-hoc 的 3.4.0 可以更新到憑證版；憑證版會擋下 ad-hoc 版
  - 驗證（下一次發布後，要使用者）：用 app 內更新從 3.4.0 升級，麥克風、錄電腦聲音的權限還在嗎；用 API key 的整理工具會不會跳出鑰匙圈的存取確認（從 ad-hoc 換過來可能會問一次，之後的更新不應該再問）；在另一個 macOS 帳號照安裝說明安裝 DMG
