#!/bin/bash
# 在虛擬機執行一段 PowerShell（從標準輸入讀）：run.sh [參數…] <<'PS' … PS
# 存成有 BOM 的 .ps1（Windows PowerShell 5.1 才讀得對中文），複製過去執行（SSH 的工作階段，看不到桌面）
HERE="$(cd "$(dirname "$0")" && pwd)"
tmp=$(mktemp -t vmps).ps1
python3 -c 'import sys; s=sys.stdin.read().replace("\r\n","\n"); open(sys.argv[1],"w",encoding="utf-8-sig").write(s.replace("\n","\r\n"))' "$tmp"
name="run-$$.ps1"
"$HERE/ssh.sh" 'New-Item -ItemType Directory -Force C:\xfer\tmp | Out-Null' && "$HERE/scp.sh" "$tmp" "vm:C:/xfer/tmp/$name"
rm -f "$tmp"
"$HERE/ssh.sh" "& C:\\xfer\\tmp\\$name $*; Remove-Item C:\\xfer\\tmp\\$name"
