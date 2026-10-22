#!/bin/bash
# 產生發布用的 DMG（design §12、§16.7、R10.1）：拾音.app、Applications 捷徑、中英文安裝說明、第三方授權
# 用法：scripts/make-dmg.sh   （會先跑 release 建置）
#   SHIYIN_UPDATE_URL=https://…/shiyin-update.json  寫進 app，之後可以 app 內更新
#   SHIYIN_USAGE_URL=https://…                      寫進 app 的匿名使用統計網址（design §30.6）；沒帶就不送統計
#   SHIYIN_RELEASE_NOTES="這版改了什麼"               寫進更新資訊
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
SCRATCH="$HOME/Library/Caches/ShiyinBuild"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)"
STAGE="$SCRATCH/dmg-stage"
OUT_DIR="$SCRATCH/dist"
DMG="$OUT_DIR/Shiyin-${VERSION}.dmg"

scripts/build-app.sh
rm -rf "$STAGE"
mkdir -p "$STAGE" "$OUT_DIR"
ditto "$SCRATCH/app/Shiyin.app" "$STAGE/Shiyin.app"   # Finder 會依在地化設定顯示成「拾音」
# 中英文共用一個 DMG：捷徑和磁碟名稱用不分語言的英文
ln -s /Applications "$STAGE/Applications"
# 安裝說明：HTML 用系統的文字排版轉成 PDF
swift scripts/html-to-pdf.swift scripts/install-guide.html "$STAGE/安裝說明.pdf"
swift scripts/html-to-pdf.swift scripts/install-guide.en.html "$STAGE/Install Guide.pdf"
cp Resources/ThirdPartyLicenses.txt "$STAGE/Third-Party Licenses.txt"   # 第三方授權（design §12.1、R13.1）

rm -f "$DMG"
# CI 上 hdiutil 偶爾會回「資源忙碌」，最多試三次
for attempt in 1 2 3; do
  hdiutil create -volname "Shiyin ${VERSION}" -srcfolder "$STAGE" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null && break
  [ "$attempt" = 3 ] && exit 1
  echo "hdiutil 失敗，5 秒後重試（第 ${attempt} 次）"
  sleep 5
done
rm -rf "$STAGE"
[ -L "$ROOT/dist" ] || { rm -rf "$ROOT/dist"; ln -s "$OUT_DIR" "$ROOT/dist"; }

# 更新資訊（design §12、R10.6）：跟 DMG 放在同一個位置；更新內容可以用 SHIYIN_RELEASE_NOTES 帶入
SHA="$(shasum -a 256 "$DMG" | cut -d' ' -f1)"
VERSION="$VERSION" DMG_NAME="$(basename "$DMG")" SHA="$SHA" NOTES="${SHIYIN_RELEASE_NOTES:-}" \
  /usr/bin/python3 -c 'import json, os; print(json.dumps({"version": os.environ["VERSION"], "url": os.environ["DMG_NAME"], "sha256": os.environ["SHA"], "notes": os.environ["NOTES"]}, ensure_ascii=False, indent=2))' \
  > "$OUT_DIR/shiyin-update.json"

echo "已產生：${DMG}（專案裡的 dist/ 指向這裡）"
echo "更新資訊：${OUT_DIR}/shiyin-update.json（跟 DMG 一起上傳到更新網址的位置）"
du -h "$DMG"
