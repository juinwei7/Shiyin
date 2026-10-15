#!/bin/zsh
# 驗證 app 內更新用（tasks T-F6）：用 build/Shiyin.app（先跑 scripts/build-app.sh）做出測試用的更新檔。
# 用法：scripts/dev/make-update-fixtures.sh <輸出資料夾>
# 然後在輸出資料夾跑 `python3 -m http.server 18765 --bind 127.0.0.1`，用 shiyinctl setupdateurl 指到下面的 JSON：
#   good.json    正常的 3.0.1（同樣的簽法）
#   badsha.json  SHA-256 不符
#   badsig.json  簽章的 identifier 不同
#   same.json    版本跟目前一樣
set -euo pipefail
OUT="${1:?要給輸出資料夾}"
SRC="$HOME/Library/Caches/ShiyinBuild/app/Shiyin.app"
rm -rf "$OUT"; mkdir -p "$OUT/good" "$OUT/other"
mkdmg() { hdiutil create -volname "拾音測試" -srcfolder "$1" -fs HFS+ -format UDZO -ov "$2" >/dev/null; }

ditto "$SRC" "$OUT/good/Shiyin.app"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString 3.0.1" -c "Set :CFBundleVersion 2" "$OUT/good/Shiyin.app/Contents/Info.plist"
codesign --force --sign - "$OUT/good/Shiyin.app/Contents/Frameworks/whisper.framework"
codesign --force --sign - --identifier com.weiwei.shiyin -r='designated => identifier "com.weiwei.shiyin"' "$OUT/good/Shiyin.app"
mkdmg "$OUT/good" "$OUT/Shiyin-3.0.1.dmg"

ditto "$OUT/good/Shiyin.app" "$OUT/other/Shiyin.app"
codesign --force --sign - --identifier com.example.notshiyin -r='designated => identifier "com.example.notshiyin"' "$OUT/other/Shiyin.app"
mkdmg "$OUT/other" "$OUT/Shiyin-3.0.1-other.dmg"

GOOD="$(shasum -a 256 "$OUT/Shiyin-3.0.1.dmg" | cut -d' ' -f1)"
OTHER="$(shasum -a 256 "$OUT/Shiyin-3.0.1-other.dmg" | cut -d' ' -f1)"
ZERO="0000000000000000000000000000000000000000000000000000000000000000"
printf '{"version":"3.0.1","url":"Shiyin-3.0.1.dmg","sha256":"%s","notes":"測試用的更新"}\n' "$GOOD" > "$OUT/good.json"
printf '{"version":"3.0.1","url":"Shiyin-3.0.1.dmg","sha256":"%s","notes":""}\n' "$ZERO" > "$OUT/badsha.json"
printf '{"version":"3.0.1","url":"Shiyin-3.0.1-other.dmg","sha256":"%s","notes":""}\n' "$OTHER" > "$OUT/badsig.json"
printf '{"version":"3.0.0","url":"Shiyin-3.0.1.dmg","sha256":"%s","notes":""}\n' "$GOOD" > "$OUT/same.json"
rm -rf "$OUT/good" "$OUT/other"
ls -la "$OUT"
