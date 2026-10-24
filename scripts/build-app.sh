#!/bin/bash
# 建置拾音：swift build → 組成 build/Shiyin.app → ad-hoc 簽章。
# 用法：scripts/build-app.sh [--install | --debug]
#   --install  先結束執行中的拾音，再裝到 ~/Applications
#   --debug    建置 DEBUG 版（含驗證用控制通道，見 scripts/dev/shiyinctl.swift），不能和 --install 一起用
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
BUNDLE_ID="com.weiwei.shiyin"
# 建置暫存和 .app 都不放專案資料夾：桌面有 iCloud 同步，除了會上傳幾百 MB，
# iCloud 加在檔案上的延伸屬性也會讓 codesign 失敗。專案裡的 build 是指過去的捷徑。
SCRATCH="$HOME/Library/Caches/ShiyinBuild"
APP="$SCRATCH/app/Shiyin.app"
CONFIG="release"
[ "${1:-}" = "--debug" ] && CONFIG="debug"

# --disable-keychain：下載 whisper.cpp 的 XCFramework 時不要去鑰匙圈找 GitHub 帳密
swift build -c "$CONFIG" --arch arm64 --disable-keychain --scratch-path "$SCRATCH"
BIN_DIR="$(swift build -c "$CONFIG" --arch arm64 --disable-keychain --scratch-path "$SCRATCH" --show-bin-path)"

# 重建前先結束從這個路徑啟動的拾音：不然 LaunchServices 會把重建後的 bundle 當成另一個 app，
# 兩個程序共用同一個資料庫、同時跑工作。10 秒內沒結束（例如卡在結束確認）就強制結束：
# 錄音下次開啟會自動復原，工作會重新排隊。不影響 ~/Applications 裡安裝的那一份。
if pgrep -f "$APP/Contents/MacOS/Shiyin" >/dev/null; then
  pkill -TERM -f "$APP/Contents/MacOS/Shiyin" || true
  for _ in $(seq 1 20); do pgrep -f "$APP/Contents/MacOS/Shiyin" >/dev/null || break; sleep 0.5; done
  pkill -KILL -f "$APP/Contents/MacOS/Shiyin" || true
fi
rm -rf "$APP"
mkdir -p "$SCRATCH/app"
[ -L "$ROOT/build" ] || { rm -rf "$ROOT/build"; ln -s "$SCRATCH/app" "$ROOT/build"; }
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources"
cp "$BIN_DIR/Shiyin" "$APP/Contents/MacOS/Shiyin"
cp Resources/Info.plist "$APP/Contents/Info.plist"
# 更新網址（design §12、R10.6）：發布時用 SHIYIN_UPDATE_URL=https://…/shiyin-update.json 帶入
if [ -n "${SHIYIN_UPDATE_URL:-}" ]; then
  /usr/libexec/PlistBuddy -c "Set :ShiyinUpdateURL ${SHIYIN_UPDATE_URL}" "$APP/Contents/Info.plist"
fi
# 匿名使用統計的網址（design §30.6、R40）：SHIYIN_USAGE_URL=https://… 帶入；沒帶就是不送統計的版本
if [ -n "${SHIYIN_USAGE_URL:-}" ]; then
  /usr/libexec/PlistBuddy -c "Set :ShiyinUsageURL ${SHIYIN_USAGE_URL}" "$APP/Contents/Info.plist"
fi
# whisper.cpp 是動態 framework（design §6.2）；SwiftPM 的資源 bundle 放 Resources
ditto "$BIN_DIR/whisper.framework" "$APP/Contents/Frameworks/whisper.framework"
# llama.cpp（Qwen3-ASR，design §32.2）也是動態 framework
ditto "$BIN_DIR/llama.framework" "$APP/Contents/Frameworks/llama.framework"
# 簡轉繁的字表（design §32.5）
ditto Resources/opencc "$APP/Contents/Resources/opencc"
for bundle in "$BIN_DIR"/*.bundle; do ditto "$bundle" "$APP/Contents/Resources/$(basename "$bundle")"; done
chmod -R u+w "$APP"
if [ -f Resources/AppIcon.icns ]; then
  cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
fi
# AccentColor（青瓷綠）要編譯成 Assets.car，List 選取色等系統元件才會套用（design §9.3）
xcrun actool Resources/Assets.xcassets --compile "$APP/Contents/Resources" --platform macosx \
  --minimum-deployment-target 26.0 --accent-color AccentColor \
  --output-partial-info-plist "$SCRATCH/assets-partial.plist" >/dev/null
# 介面翻譯（design §16.1）：翻譯表編成各語言的 Localizable.strings；
# app 名稱、權限說明、檔案類型名稱的中文放在 Resources/zh-Hant.lproj/InfoPlist.strings（英文寫在 Info.plist）
xcrun xcstringstool compile Resources/Localizable.xcstrings --output-directory "$APP/Contents/Resources" >/dev/null
for lproj in Resources/*.lproj; do ditto "$lproj" "$APP/Contents/Resources/$(basename "$lproj")"; done
mkdir -p "$APP/Contents/Resources/en.lproj"
# 第三方授權（design §12.1、R13.1）：原文放一份；另外包成 Credits.html，系統的「關於拾音」視窗會顯示。
# Credits.html 放在各語言的 lproj（最上面是贊助連結，R13.3），Resources 底下不能再有一份，不然系統會先拿那份
cp Resources/ThirdPartyLicenses.txt "$APP/Contents/Resources/ThirdPartyLicenses.txt"
/usr/bin/python3 -c 'import html, sys
text = html.escape(open(sys.argv[1], encoding="utf-8").read())
kofi = "https://ko-fi.com/weiwei_chang"
sponsor = {
    "zh-Hant": f"覺得拾音好用的話，可以<a href=\"{kofi}\">請我喝杯咖啡</a>。",
    "en": f"Enjoying Shiyin? <a href=\"{kofi}\">Buy me a coffee</a>.",
}
for lang, line in sponsor.items():
    with open(f"{sys.argv[2]}/{lang}.lproj/Credits.html", "w", encoding="utf-8") as f:
        f.write(f"<!doctype html><html lang=\"{lang}\"><meta charset=\"utf-8\">"
                f"<p style=\"font: 12px -apple-system; text-align: center\">{line}</p>"
                f"<pre style=\"font: 10px Menlo; white-space: pre-wrap\">{text}</pre>")' \
  Resources/ThirdPartyLicenses.txt "$APP/Contents/Resources"

xattr -cr "$APP"
# 簽章（design §12、R10.7）：有已信任的自簽憑證就用它；沒有就用 ad-hoc，
# 並把 designated requirement 指定成只看 bundle identifier，讓更新後的 app 仍符合同一個要求
SIGN_DIR="$HOME/Library/Application Support/ShiyinSigning"
SIGN_KC="$SIGN_DIR/shiyin-signing.keychain-db"
IDENTITY=""
if [ -f "$SIGN_KC" ] && [ -f "$SIGN_DIR/password" ]; then
  security unlock-keychain -p "$(cat "$SIGN_DIR/password")" "$SIGN_KC" 2>/dev/null || true
  IDENTITY="$(security find-identity -v -p codesigning "$SIGN_KC" 2>/dev/null | awk '/Shiyin Self-Signed/ {print $2; exit}')"
fi
# codesign 只在鑰匙圈搜尋清單裡找簽章身分（加了 --keychain 也一樣，不然會 no identity found），
# 簽章期間暫時加進清單，簽完或失敗都還原
ORIG_KCS=()
if [ -n "$IDENTITY" ]; then
  while IFS= read -r kc; do ORIG_KCS+=("$kc"); done < <(security list-keychains -d user | sed -e 's/^[[:space:]]*"//' -e 's/"$//')
  restore_keychains() { security list-keychains -d user -s ${ORIG_KCS[@]+"${ORIG_KCS[@]}"}; }
  trap restore_keychains EXIT
  security list-keychains -d user -s "$SIGN_KC" ${ORIG_KCS[@]+"${ORIG_KCS[@]}"}
fi
sign() {
  if [ -n "$IDENTITY" ]; then
    codesign --force --sign "$IDENTITY" --keychain "$SIGN_KC" --timestamp=none "$@"
  else
    codesign --force --sign - "$@"
  fi
}
sign "$APP/Contents/Frameworks/whisper.framework"
sign "$APP/Contents/Frameworks/llama.framework"
if [ -n "$IDENTITY" ]; then
  sign --identifier "$BUNDLE_ID" "$APP"
  restore_keychains
  trap - EXIT
  echo "簽章：Shiyin Self-Signed"
else
  sign --identifier "$BUNDLE_ID" -r="designated => identifier \"$BUNDLE_ID\"" "$APP"
  echo "簽章：ad-hoc（還沒有信任的自簽憑證；執行 scripts/make-signing-cert.sh --trust 後重建，權限才確定能在更新後保留）"
fi
codesign --verify --strict --deep "$APP"
echo "已建置（${CONFIG}）：${APP}（專案裡的 build/Shiyin.app 指向這裡）"

if [ "${1:-}" = "--install" ]; then
  if pgrep -xq Shiyin; then
    echo "結束執行中的拾音…"
    osascript -e "tell application id \"$BUNDLE_ID\" to quit" || true
    for _ in $(seq 1 60); do pgrep -xq Shiyin || break; sleep 0.5; done
    pgrep -xq Shiyin && { echo "拾音沒有結束（可能在錄音或工作中，請手動結束後再試）"; exit 1; }
  fi
  mkdir -p "$HOME/Applications"
  rm -rf "$HOME/Applications/Shiyin.app"
  cp -R "$APP" "$HOME/Applications/Shiyin.app"
  echo "已安裝：$HOME/Applications/Shiyin.app"
fi
