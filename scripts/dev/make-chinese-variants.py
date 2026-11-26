#!/usr/bin/env python3
"""內建辨識的繁體字依詞改正（design §6.2.2、R14.8、D89）用的詞典：從 OpenCC 的詞典取需要的部分，
產生 Sources/Shiyin/Transcribe/ChineseVariantsData.swift（不用另外打包資源檔）。

  python3 scripts/dev/make-chinese-variants.py [OpenCC 詞典的資料夾]

預設用 Windows 版的 OpenccNetLib 附的詞典（`dotnet restore` 後在 NuGet 的快取裡），兩邊同一份。
改了 TARGETS 要重新產生；改正的規則在 ChineseVariants.swift。
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OPENCC = "OpenccNet 1.7.0"
DICTS = Path(sys.argv[1]) if len(sys.argv) > 1 else \
    Path.home() / ".nuget/packages/openccnetlib/1.7.0/contentFiles/any/any/dicts"
OUT = ROOT / "Sources/Shiyin/Transcribe/ChineseVariantsData.swift"

# 系統的中文辨識常選錯的字：同一個簡體字有好幾個繁體寫法時，選了不對的那個（design §6.2.2）
TARGETS = "麵裏迴隻註錶係製韆癥穀鑒鉆閑殭紮輓復闆發幹"


def entries(name):
    for line in (DICTS / name).read_text(encoding="utf-8").splitlines():
        if line and not line.startswith("#"):
            key, values = line.split("\t", 1)
            yield key, values.split(" ")


def main():
    to_simplified = {}
    for key, values in entries("TSCharacters.txt"):
        if len(key) == 1 and key not in to_simplified and values[0] != key:
            to_simplified[key] = values[0]
    simplified = sorted({to_simplified.get(c, c) for c in TARGETS})
    characters = {key: values for key, values in entries("STCharacters.txt") if key in simplified and len(values) > 1}
    phrases = {}
    for key, values in entries("STPhrases.txt"):
        # 一個字換一個字（長度不同的詞組不用）；只留含這些簡體字的
        if key not in phrases and len(values[0]) == len(key) and any(c in simplified for c in key):
            phrases[key] = values[0]
    variants = {key: values[0] for key, values in entries("TWVariants.txt") if len(key) == 1 and values[0] != key}
    for name in ["LICENSE.txt"]:
        assert (DICTS / name).exists(), f"找不到 {DICTS / name}"

    lines = [
        f"// 產生的檔案，不要手改：python3 scripts/dev/make-chinese-variants.py（design §6.2.2、R14.8）",
        f"// 來源：OpenCC 的詞典（Apache-2.0，https://github.com/BYVoid/OpenCC），{OPENCC} 附的那一份",
        "",
        "enum ChineseVariantsData {",
        "    /// 要改正的字",
        f'    static let targets = "{TARGETS}"',
        "    /// 繁轉簡（TSCharacters 的第一個寫法），每兩個字一組：繁、簡",
        f'    static let traditionalToSimplified = "{"".join(k + v for k, v in sorted(to_simplified.items()))}"',
        "    /// 台灣的異體字（TWVariants），每兩個字一組",
        f'    static let taiwanVariants = "{"".join(k + v for k, v in sorted(variants.items()))}"',
        "    /// 這些簡體字的繁體寫法（STCharacters），每行「簡體、繁體們」，第一個最常見",
        '    static let characters = """',
        *[f"    {k}{''.join(v)}" for k, v in sorted(characters.items())],
        '    """',
        "    /// 含這些簡體字的詞組（STPhrases，一個字換一個字的），每行「簡體 TAB 繁體」",
        '    static let phrases = """',
        *[f"    {k}\t{v}" for k, v in sorted(phrases.items())],
        '    """',
        "}",
        "",
    ]
    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"已產生 {OUT.relative_to(ROOT)}：繁轉簡 {len(to_simplified)} 字、詞組 {len(phrases)} 條、"
          f"{OUT.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
