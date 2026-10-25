#!/bin/bash
# 把 Mac 上的工作目錄（git 追蹤的和還沒加進 git 的，不含忽略的）同步到虛擬機的 C:\src。
# 不用共用資料夾（桌面有 iCloud）。macOS 的 tar 會帶 ._ 檔（AppleDouble），會讓建置失敗，要關掉
HERE="$(cd "$(dirname "$0")" && pwd)"
STATE="${SHIYIN_VM:-$HOME/Library/Caches/ShiyinBuild-win/vm}"
cd "$(git -C "$HERE" rev-parse --show-toplevel)" || exit 1
# Resources/opencc 是 Shiyin.Core 內嵌的簡轉繁字表（design-windows §8.4）：漏掉的話建出來的組件沒有字表，Qwen 的中文轉錄會失敗（2026-10-25）
git ls-files -co --exclude-standard -z windows shared scripts Resources/Localizable.xcstrings Resources/opencc |
  COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata --null -T - -czf "$STATE/sync.tar.gz" || exit 1
"$HERE/ssh.sh" 'New-Item -ItemType Directory -Force C:\xfer, C:\src | Out-Null' &&
  "$HERE/scp.sh" "$STATE/sync.tar.gz" vm:C:/xfer/ &&
  "$HERE/ssh.sh" 'tar -xzf C:\xfer\sync.tar.gz -C C:\src; "synced"'
