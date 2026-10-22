#!/bin/bash
# 官網用的圖（docs/website.md §6），只能在 macOS 上跑（用系統的 sips 和 Chrome）。
# 截圖（docs/images）或圖示換過之後跑一次，連同 site/public 一起 commit。
#   - 截圖轉成 AVIF：1920、960 寬，品質 60
#   - favicon、apple-touch-icon
#   - 分享預覽圖 og.png、og-en.png、og-ja.png（1200×630，用 site/og.html 截圖；沒有 Chrome 就跳過）
set -euo pipefail
cd "$(dirname "$0")/.."
out=site/public

# Windows 版的截圖在 windows/ 底下（docs/website.md §16.1，1422 寬：原尺寸一份、960 寬一份）
for dir in docs/images docs/images/en docs/images/windows docs/images/en/windows; do
  dst="$out/images${dir#docs/images}"
  mkdir -p "$dst"
  for name in summary recording transcript; do
    sips -s format avif -s formatOptions 60 "$dir/$name.png" --out "$dst/$name.avif" >/dev/null
    sips -s format avif -s formatOptions 60 --resampleWidth 960 "$dir/$name.png" --out "$dst/$name-960.avif" >/dev/null
  done
done
shasum -a 256 docs/images/{,en/,windows/,en/windows/}{summary,recording,transcript}.png > "$out/images/sources.sha256"

sips -z 64 64 docs/images/icon.png --out "$out/favicon.png" >/dev/null
sips -z 180 180 docs/images/icon.png --out "$out/apple-touch-icon.png" >/dev/null

chrome="/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
if [ -x "$chrome" ]; then
  profile="$(mktemp -d)"
  for lang in zh en ja; do
    name=og.png; [ "$lang" != zh ] && name="og-$lang.png"
    "$chrome" --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$profile" \
      --force-device-scale-factor=1 --window-size=1200,630 --virtual-time-budget=3000 \
      --screenshot="$PWD/$out/$name" "file://$PWD/site/og.html?$lang" 2>/dev/null
  done
  rm -rf "$profile"
else
  echo "找不到 Google Chrome，沒有更新分享預覽圖"
fi

ls -la "$out" "$out/images" "$out/images/en"
