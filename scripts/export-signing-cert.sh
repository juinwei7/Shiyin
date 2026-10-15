#!/bin/bash
# 把自簽憑證匯出給 GitHub Actions 用（design §12），讓 CI 簽出來的 app 跟這台電腦建的一致，app 內更新才會通過簽章檢查。
# 用法：
#   scripts/export-signing-cert.sh                          匯出成 .p12，印出位置和密碼
#   scripts/export-signing-cert.sh --to-github <owner/repo>  直接設成那個 repo 的 secrets（需要 gh）
# 注意：私鑰會離開這台電腦。只設在自己的 repo，不要放進任何檔案或 commit。
set -euo pipefail
DIR="$HOME/Library/Application Support/ShiyinSigning"
KEYCHAIN="$DIR/shiyin-signing.keychain-db"
[ -f "$KEYCHAIN" ] || { echo "找不到憑證，請先執行 scripts/make-signing-cert.sh"; exit 1; }
security unlock-keychain -p "$(cat "$DIR/password")" "$KEYCHAIN"

P12_PASSWORD="$(openssl rand -hex 16)"
WORK="$(mktemp -d)"
P12="$WORK/shiyin-signing.p12"
security export -k "$KEYCHAIN" -t identities -f pkcs12 -P "$P12_PASSWORD" -o "$P12"

if [ "${1:-}" = "--to-github" ]; then
  REPO="${2:?要給 owner/repo}"
  base64 -i "$P12" | gh secret set SHIYIN_SIGNING_P12 --repo "$REPO"
  printf '%s' "$P12_PASSWORD" | gh secret set SHIYIN_SIGNING_P12_PASSWORD --repo "$REPO"
  rm -rf "$WORK"
  echo "已設定 ${REPO} 的 SHIYIN_SIGNING_P12、SHIYIN_SIGNING_P12_PASSWORD"
else
  echo "已匯出：${P12}"
  echo "密碼：${P12_PASSWORD}"
fi
