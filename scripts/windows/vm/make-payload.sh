#!/bin/bash
# 做虛擬機全自動安裝用的第二張光碟（design-windows §15.2）：autounattend.xml、第一次登入的設定腳本、SSH 公鑰、OpenSSH、驅動程式。
#   make-payload.sh <虛擬機帳號 shiyin 的密碼> <OpenSSH-ARM64.msi> <UTM guest tools 的資料夾（有 Drivers\ 和 utm-guest-tools-*.exe）>
# 金鑰沒有的話先建（$SHIYIN_VM/id_ed25519）；輸出 $SHIYIN_VM/payload.iso，掛成虛擬機的第二台光碟機
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
STATE="${SHIYIN_VM:-$HOME/Library/Caches/ShiyinBuild-win/vm}"
password="$1"; msi="$2"; tools="$3"
mkdir -p "$STATE"
[ -f "$STATE/id_ed25519" ] || ssh-keygen -q -t ed25519 -N '' -C shiyin-vm -f "$STATE/id_ed25519"
dir=$(mktemp -d)
python3 - "$HERE/payload/autounattend.xml" "$dir/autounattend.xml" "$password" <<'PY'
import sys, html
s = open(sys.argv[1], encoding='utf-8').read()
open(sys.argv[2], 'w', encoding='utf-8').write(s.replace('__PASSWORD__', html.escape(sys.argv[3])))
PY
cp "$HERE/payload/shiyin-setup.ps1" "$dir/"
cp "$STATE/id_ed25519.pub" "$dir/authorized_keys"
cp "$msi" "$dir/OpenSSH-ARM64.msi"
cp -R "$tools/Drivers" "$dir/Drivers"
cp "$tools"/utm-guest-tools-*.exe "$dir/"
rm -f "$STATE/payload.iso"
hdiutil makehybrid -quiet -iso -joliet -default-volume-name SHIYIN -o "$STATE/payload.iso" "$dir"
rm -rf "$dir"
echo "$STATE/payload.iso"
