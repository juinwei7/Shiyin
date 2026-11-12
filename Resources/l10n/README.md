# 簡體中文介面的用語（design §48.1、D155）

- `zh-Hans-terms.tsv`：台灣的介面用語 → 大陸的介面用語（右邊已經是簡體），tab 分隔。在繁體的原文上做最長匹配，換過的字不再換第二次；其餘的字用 OpenCC 的繁轉簡（`Resources/opencc/`，只換字）。app 也帶這份，執行時轉不查表的中文（`ChineseScript.toSimplifiedUI`）。
- `zh-Hans-overrides.json`：`{"繁體原文": "简体"}`，自動轉得不自然的條目直接指定，只在產生翻譯表時用。
- 改了這兩份要跑 `scripts/dev/l10n.py hans`（`check`、`windows-check` 也會先跑），再重跑 Mac 的 dump-golden（`util/ui-hans.json`）。
