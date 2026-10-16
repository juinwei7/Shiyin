#!/bin/zsh
# 驗證 app 內更新用（tasks T-F6）：用 build/Shiyin.app（先跑 scripts/build-app.sh）做出測試用的更新檔。
# 用法：scripts/dev/make-update-fixtures.sh <輸出資料夾>
# 然後在輸出資料夾跑 `python3 -m http.server 18765 --bind 127.0.0.1`，用 shiyinctl setupdateurl 指到下面的 JSON：
#   good.json    正常的新版（目前版本的修訂號加 1，同樣的簽法）
#   badsha.json  SHA-256 不符
#   badsig.json  簽章的 identifier 不同
#   same.json    版本跟目前一樣
set -euo pipefail
OUT="${1:?要給輸出資料夾}"
SRC="$HOME/Library/Caches/ShiyinBuild/app/Shiyin.app"
rm -rf "$OUT"; mkdir -p "$OUT/good" "$OUT/other"
mkdmg() { hdiutil create -volname "Shiyin Test" -srcfolder "$1" -fs HFS+ -format UDZO -ov "$2" >/dev/null; }
# 版本號跟著建置出來的 app 走：3.1.0 → 新版 3.1.1
CUR="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$SRC/Contents/Info.plist")"
NEXT="${CUR%.*}.$(( ${CUR##*.} + 1 ))"

ditto "$SRC" "$OUT/good/Shiyin.app"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $NEXT" "$OUT/good/Shiyin.app/Contents/Info.plist"
codesign --force --sign - "$OUT/good/Shiyin.app/Contents/Frameworks/whisper.framework"
codesign --force --sign - --identifier com.weiwei.shiyin -r='designated => identifier "com.weiwei.shiyin"' "$OUT/good/Shiyin.app"
mkdmg "$OUT/good" "$OUT/Shiyin-$NEXT.dmg"

ditto "$OUT/good/Shiyin.app" "$OUT/other/Shiyin.app"
codesign --force --sign - --identifier com.example.notshiyin -r='designated => identifier "com.example.notshiyin"' "$OUT/other/Shiyin.app"
mkdmg "$OUT/other" "$OUT/Shiyin-$NEXT-other.dmg"

GOOD="$(shasum -a 256 "$OUT/Shiyin-$NEXT.dmg" | cut -d' ' -f1)"
OTHER="$(shasum -a 256 "$OUT/Shiyin-$NEXT-other.dmg" | cut -d' ' -f1)"
ZERO="0000000000000000000000000000000000000000000000000000000000000000"
manifest() { printf '{"version":"%s","url":"%s","sha256":"%s","notes":"%s"}\n' "$@"; }
manifest "$NEXT" "Shiyin-$NEXT.dmg" "$GOOD" "測試用的更新" > "$OUT/good.json"
manifest "$NEXT" "Shiyin-$NEXT.dmg" "$ZERO" "" > "$OUT/badsha.json"
manifest "$NEXT" "Shiyin-$NEXT-other.dmg" "$OTHER" "" > "$OUT/badsig.json"
manifest "$CUR" "Shiyin-$NEXT.dmg" "$GOOD" "" > "$OUT/same.json"
rm -rf "$OUT/good" "$OUT/other"
ls -la "$OUT"
