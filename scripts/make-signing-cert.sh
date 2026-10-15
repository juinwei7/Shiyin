#!/bin/bash
# 建立拾音用的自簽程式簽章憑證（design §12、R10.7）。只要做一次。
# 憑證放在獨立的鑰匙圈檔案，密碼存在 ~/Library/Application Support/ShiyinSigning/password，
# 建置時用它自動解鎖，不會動到登入鑰匙圈，也不會跳出授權視窗。
# 每次建置都用同一張憑證簽章 → designated requirement 固定 → 更新後 macOS 權限（麥克風、系統錄音）不用重新允許。
set -euo pipefail

NAME="Shiyin Self-Signed"
DIR="$HOME/Library/Application Support/ShiyinSigning"
KEYCHAIN="$DIR/shiyin-signing.keychain-db"

mkdir -p "$DIR"
chmod 700 "$DIR"

# --trust：讓 macOS 信任這張憑證（需要輸入登入密碼，只要做一次）。信任之後 codesign 才能使用它
if [ "${1:-}" = "--trust" ]; then
  [ -f "$KEYCHAIN" ] || { echo "請先執行 scripts/make-signing-cert.sh 建立憑證"; exit 1; }
  PEM="$(mktemp)"
  security find-certificate -c "$NAME" -p "$KEYCHAIN" > "$PEM"
  echo "macOS 會要求輸入登入密碼，確認信任「${NAME}」用於程式簽章"
  security add-trusted-cert -r trustRoot -p codeSign "$PEM"
  rm -f "$PEM"
  security find-identity -v -p codesigning "$KEYCHAIN"
  exit 0
fi

if [ -f "$KEYCHAIN" ] && security find-identity -p codesigning "$KEYCHAIN" 2>/dev/null | grep -q "$NAME"; then
  echo "憑證已存在：${KEYCHAIN}"
  echo "還沒信任的話，執行 scripts/make-signing-cert.sh --trust"
  exit 0
fi

PASSWORD="$(openssl rand -hex 24)"
printf '%s' "$PASSWORD" > "$DIR/password"
chmod 600 "$DIR/password"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/cert.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
EOF
openssl req -x509 -newkey rsa:2048 -nodes -days 3650 -config "$WORK/cert.cnf" \
  -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
# 用 -legacy 讓 macOS 的 security 讀得懂（OpenSSL 3 預設的 p12 加密 macOS 不支援）
openssl pkcs12 -export -legacy -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -name "$NAME" \
  -out "$WORK/cert.p12" -passout pass:shiyin 2>/dev/null \
  || openssl pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -name "$NAME" -out "$WORK/cert.p12" -passout pass:shiyin

rm -f "$KEYCHAIN"
security create-keychain -p "$PASSWORD" "$KEYCHAIN"
security set-keychain-settings "$KEYCHAIN"          # 不自動上鎖
security unlock-keychain -p "$PASSWORD" "$KEYCHAIN"
security import "$WORK/cert.p12" -k "$KEYCHAIN" -P shiyin -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$PASSWORD" "$KEYCHAIN" >/dev/null
echo "已建立憑證「${NAME}」：${KEYCHAIN}"
echo "下一步：執行 scripts/make-signing-cert.sh --trust（需要輸入登入密碼），之後的建置就會用這張憑證簽章"
security find-identity -p codesigning "$KEYCHAIN"
