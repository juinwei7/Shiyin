#!/bin/bash
# 產生 Resources/ThirdPartyLicenses.txt（design §12.1、R13.1）：app 編進去的開源套件的授權條款原文。
# 用法：scripts/dev/make-third-party-licenses.sh   （先跑過一次 scripts/build-app.sh，套件才會下載好）
# 更新套件版本（Package.swift、Package.resolved）之後要重跑，並 commit 產生的檔案。
set -euo pipefail
cd "$(dirname "$0")/../.."
CHECKOUTS="$HOME/Library/Caches/ShiyinBuild/checkouts"
OUT="Resources/ThirdPartyLicenses.txt"

resolved_version() {
  /usr/bin/python3 -c 'import json, sys
pins = json.load(open("Package.resolved"))["pins"]
print(next(p["state"]["version"] for p in pins if p["identity"] == sys.argv[1]))' "$1"
}

# checkout 的版本要跟 Package.resolved 一致，不然附的授權可能是別的版本的
checkout() {
  local dir="$CHECKOUTS/$1" version="$2"
  [ -d "$dir" ] || { echo "找不到 $dir：先跑 scripts/build-app.sh" >&2; exit 1; }
  local actual
  actual="$(git -C "$dir" describe --tags --exact-match 2>/dev/null || true)"
  [ "$actual" = "$version" ] || [ "$actual" = "v$version" ] || { echo "$1 的 checkout 是 ${actual:-未知}，Package.resolved 是 $version：先跑 scripts/build-app.sh" >&2; exit 1; }
  echo "$dir"
}

WHISPER_TAG="$(sed -n 's#.*whisper.cpp/releases/download/\([^/]*\)/.*#\1#p' Package.swift | head -1)"
GRDB_VERSION="$(resolved_version grdb.swift)"
FLUID_VERSION="$(resolved_version fluidaudio)"
GRDB_DIR="$(checkout GRDB.swift "$GRDB_VERSION")"
FLUID_DIR="$(checkout FluidAudio "$FLUID_VERSION")"
WHISPER_LICENSE="$(curl -fsSL "https://raw.githubusercontent.com/ggml-org/whisper.cpp/${WHISPER_TAG}/LICENSE")"
LLAMA_TAG="$(sed -n 's#.*llama.cpp/releases/download/\([^/]*\)/.*#\1#p' Package.swift | head -1)"
LLAMA_LICENSE="$(curl -fsSL "https://raw.githubusercontent.com/ggml-org/llama.cpp/${LLAMA_TAG}/LICENSE")"

section() {
  printf '\n\n%s\n%s\n%s\n\n' "================================================================================" "$1" "================================================================================"
}

{
  echo "拾音 Shiyin 使用了下列開源軟體，以下是各軟體附的授權條款原文。"
  echo "Shiyin uses the following open-source software. Their license terms follow, verbatim."
  echo
  echo "- whisper.cpp ${WHISPER_TAG} (MIT) https://github.com/ggml-org/whisper.cpp"
  echo "- llama.cpp ${LLAMA_TAG} (MIT) https://github.com/ggml-org/llama.cpp"
  echo "- OpenCC dictionaries (Apache-2.0) https://github.com/BYVoid/OpenCC"
  echo "- GRDB.swift ${GRDB_VERSION} (MIT) https://github.com/groue/GRDB.swift"
  echo "- FluidAudio ${FLUID_VERSION} (Apache-2.0) https://github.com/FluidInference/FluidAudio"
  echo "  and the third-party components it includes (FluidAudio ThirdPartyLicenses/)"
  section "whisper.cpp ${WHISPER_TAG}: LICENSE"
  printf '%s\n' "$WHISPER_LICENSE"
  section "llama.cpp ${LLAMA_TAG}: LICENSE"
  printf '%s\n' "$LLAMA_LICENSE"
  section "OpenCC dictionaries: LICENSE"
  cat Resources/opencc/LICENSE
  section "GRDB.swift ${GRDB_VERSION}: LICENSE"
  cat "$GRDB_DIR/LICENSE"
  section "FluidAudio ${FLUID_VERSION}: LICENSE"
  cat "$FLUID_DIR/LICENSE"
  (cd "$FLUID_DIR" && find ThirdPartyLicenses -type f | LC_ALL=C sort) | while read -r file; do
    section "FluidAudio ${FLUID_VERSION}: ${file}"
    cat "$FLUID_DIR/$file"
  done
} > "$OUT"

echo "已產生 ${OUT}（$(wc -l < "$OUT" | tr -d ' ') 行）"
