# 簡轉繁字表（design §32.5、D108）

取自 OpenCC（https://github.com/BYVoid/OpenCC，Apache-2.0，LICENSE 在同一個資料夾）`data/dictionary/` 的
`STCharacters.txt`、`STPhrases.txt`、`TWVariants.txt`，只留每個鍵的第一個候選、去掉註解（鍵和值相同的詞組要保留：它們擋住逐字轉換，例如「公里」「皇后」），存成 tab 分隔的 `.tsv`。
Qwen3-ASR 輸出簡體，app 用這三張表轉成台灣繁體（`ChineseScript.toTaiwan`）；Windows 版讀同一份。
重新產生：`~/Library/Caches/ShiyinSpike/qwen3asr/opencc/` 的縮表步驟，或直接照上面的規則處理 OpenCC 的原檔。
