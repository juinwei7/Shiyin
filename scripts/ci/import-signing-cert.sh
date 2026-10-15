#!/bin/bash
# GitHub Actions 用（design §12）：把 secrets 裡的自簽憑證放到 build-app.sh 認得的位置，並設成系統信任。
# 沒有設定 SHIYIN_SIGNING_P12 時什麼都不做，build-app.sh 會改用 ad-hoc 簽章。
set -euo pipefail
if [ -z "${SHIYIN_SIGNING_P12:-}" ]; then
  echo "沒有設定 SHIYIN_SIGNING_P12，改用 ad-hoc 簽章"
  exit 0
fi
NAME="Shiyin Self-Signed"
DIR="$HOME/Library/Application Support/ShiyinSigning"
KEYCHAIN="$DIR/shiyin-signing.keychain-db"
mkdir -p "$DIR"
chmod 700 "$DIR"
PASSWORD="$(openssl rand -hex 24)"
printf '%s' "$PASSWORD" > "$DIR/password"
chmod 600 "$DIR/password"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
printf '%s' "$SHIYIN_SIGNING_P12" | base64 --decode > "$WORK/cert.p12"
security create-keychain -p "$PASSWORD" "$KEYCHAIN"
security set-keychain-settings "$KEYCHAIN"
security unlock-keychain -p "$PASSWORD" "$KEYCHAIN"
security import "$WORK/cert.p12" -k "$KEYCHAIN" -P "${SHIYIN_SIGNING_P12_PASSWORD:-}" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$PASSWORD" "$KEYCHAIN" >/dev/null
# runner 的 sudo 不用密碼：設成系統信任，codesign 才肯用這張憑證
security find-certificate -c "$NAME" -p "$KEYCHAIN" > "$WORK/cert.pem"
sudo security add-trusted-cert -d -r trustRoot -p codeSign -k /Library/Keychains/System.keychain "$WORK/cert.pem"
security find-identity -v -p codesigning "$KEYCHAIN"
