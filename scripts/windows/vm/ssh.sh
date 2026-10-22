#!/bin/bash
# 連到驗證用的 Windows 虛擬機（design-windows §15.2），執行 PowerShell 指令；沒有參數時開互動的 shell。
# 金鑰、IP 放在 $SHIYIN_VM（預設 ~/Library/Caches/ShiyinBuild-win/vm），不放進 repo。輸出改成 UTF-8（中文 Windows 預設是 Big5）
STATE="${SHIYIN_VM:-$HOME/Library/Caches/ShiyinBuild-win/vm}"
SSH=(ssh -i "$STATE/id_ed25519" -o UserKnownHostsFile="$STATE/known_hosts" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 -o BatchMode=yes -o ServerAliveInterval=30 "shiyin@$(cat "$STATE/ip.txt")")
if [ $# -eq 0 ]; then exec "${SSH[@]}"; fi
exec "${SSH[@]}" "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; \$OutputEncoding = [System.Text.Encoding]::UTF8; \$ProgressPreference = 'SilentlyContinue'; $*"
