#!/bin/bash
# 複製檔案進出虛擬機：scp.sh <來源…> <目的地>；vm: 開頭的是虛擬機上的路徑，例如 vm:C:/xfer/
STATE="${SHIYIN_VM:-$HOME/Library/Caches/ShiyinBuild-win/vm}"
IP=$(cat "$STATE/ip.txt")
args=()
for a in "$@"; do
  case "$a" in
    vm:*) args+=("shiyin@$IP:${a#vm:}") ;;
    *) args+=("$a") ;;
  esac
done
exec scp -q -r -i "$STATE/id_ed25519" -o UserKnownHostsFile="$STATE/known_hosts" -o BatchMode=yes "${args[@]}"
