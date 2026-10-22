#!/usr/bin/env python3
"""翻譯表工具（design §16.1）。

  scripts/dev/l10n.py extract   用 Swift 編譯器抽出介面字串（型別正確的 %@、%lld），合併進 Resources/Localizable.xcstrings
  scripts/dev/l10n.py check     列出缺英文翻譯的條目、程式碼裡沒查表的介面中文；有問題時 exit 1
  scripts/dev/l10n.py apply <翻譯.json>  把英文翻譯寫進翻譯表
  scripts/dev/l10n.py windows-check     Windows 版（windows/src 的 C#）：列出 L.T、L.F 用到但兩份翻譯表都沒有英文的條目、沒查表的介面中文
  scripts/dev/l10n.py windows-apply <翻譯.json>  把 Windows 才有的英文翻譯寫進 windows/src/Shiyin.App/Strings/Windows.xcstrings
                                     （Mac 的 Localizable.xcstrings 不動，R26.5；app 先讀 Mac 的再讀 Windows 的）

為什麼不用 xcstringstool：它的 extract 看不出型別，插值會變成 %arg，跟執行時 SwiftUI 查的鍵對不上；
sync 又不接受編譯器產生的 stringsdata，所以自己合併（xcstrings 就是 JSON）。
"""
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CATALOG = ROOT / "Resources" / "Localizable.xcstrings"
SOURCES = ROOT / "Sources" / "Shiyin"
SCRATCH = Path.home() / "Library" / "Caches" / "ShiyinBuild-l10n"
CJK = re.compile("[\u3000-\u303f\u4e00-\u9fff\uff00-\uffef]")  # 中文字和全形標點

# 不顯示在介面上的中文：DEBUG 控制通道、送給 LLM 的提示詞（會議紀錄、逐字稿校正）、各語言分開寫好的內容
# 範本：中文版是寫進資料庫的內容，英文版另外寫好，介面文字都用 language.text(zh:en:)
SKIP_FILES = {"Debug/DebugControl.swift", "Notes/NotesPrompt.swift", "Notes/Templates.swift", "Notes/TranscriptCorrection.swift"}


def own_swift_files():
    return [p for p in SOURCES.rglob("*.swift") if str(p.relative_to(SOURCES)) not in SKIP_FILES]


def load_catalog():
    if CATALOG.exists():
        return json.loads(CATALOG.read_text())
    return {"sourceLanguage": "zh-Hant", "strings": {}, "version": "1.0"}


def save_catalog(cat):
    cat["strings"] = dict(sorted(cat["strings"].items()))
    CATALOG.write_text(json.dumps(cat, ensure_ascii=False, indent=2) + "\n")


def run_extraction():
    """用 Swift 編譯器抽出會查表的字串，回傳 {相對路徑: [(行號, 鍵)]}"""
    out = SCRATCH / "stringsdata"
    out.mkdir(parents=True, exist_ok=True)
    # 程式沒變時 swift build 會跳過編譯，就不會產生 stringsdata：先更新所有原始碼的修改時間，強迫重新編譯
    for swift in SOURCES.rglob("*.swift"):
        swift.touch()
    subprocess.run(
        ["swift", "build", "-c", "debug", "--arch", "arm64", "--disable-keychain", "--scratch-path", str(SCRATCH),
         "--target", "Shiyin", "-Xswiftc", "-emit-localized-strings", "-Xswiftc", "-emit-localized-strings-path",
         "-Xswiftc", str(out)],
        cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    found = {}
    for swift in own_swift_files():
        data = out / f"{swift.stem}.stringsdata"
        rel = str(swift.relative_to(SOURCES))
        found[rel] = []
        if data.exists():
            for entry in json.loads(data.read_text())["tables"].get("Localizable", []):
                found[rel].append((entry["location"]["startingLine"], entry["key"]))
    return found


def extract():
    found = run_extraction()
    keys = {}
    for rel, entries in found.items():
        for line, key in entries:
            keys.setdefault(key, f"{rel}:{line}")
    cat = load_catalog()
    added = [k for k in keys if k not in cat["strings"]]
    stale = [k for k in cat["strings"] if k not in keys]
    for k in added:
        cat["strings"][k] = {}
    for k in keys:
        cat["strings"][k]["extractionState"] = "manual"
    for k in stale:
        del cat["strings"][k]   # 程式裡已經不用的條目直接刪掉，翻譯表保持乾淨
    save_catalog(cat)
    print(f"抽出 {len(keys)} 條；新增 {len(added)}、刪除不再使用的 {len(stale)}")
    return found


def string_literals(line):
    """一行裡所有的字串字面值（包含插值裡面的），回傳內容清單；遇到多行字串就放棄"""
    found = []

    def scan(text, i, end_char=None):
        n = len(text)
        while i < n:
            c = text[i]
            if end_char and c == end_char:
                return i
            if c == "/" and text[i:i + 2] == "//":
                return n
            if c == "(" and end_char:
                i = scan(text, i + 1, ")") + 1
                continue
            if c == '"':
                if text[i:i + 3] == '"""':
                    return n
                j = i + 1
                buf = []
                while j < n and text[j] != '"':
                    if text[j] == "\\" and j + 1 < n and text[j + 1] == "(":
                        k = scan(text, j + 2, ")")
                        buf.append("%@")
                        j = k + 1
                        continue
                    if text[j] == "\\":
                        buf.append(text[j:j + 2])
                        j += 2
                        continue
                    buf.append(text[j])
                    j += 1
                found.append("".join(buf))
                i = j + 1
                continue
            i += 1
        return i

    scan(line, 0)
    return found


def has_english(entry):
    loc = entry.get("localizations", {}).get("en", {})
    if "stringUnit" in loc:
        return bool(loc["stringUnit"].get("value"))
    return "variations" in loc


def check():
    found = extract()
    cat = load_catalog()
    problems = 0
    used = {k for entries in found.values() for _, k in entries}
    missing = [k for k in sorted(used) if CJK.search(k) and not has_english(cat["strings"].get(k, {}))]
    if missing:
        problems += len(missing)
        print(f"缺英文翻譯（{len(missing)} 條）：")
        for k in missing:
            print(f"  {k}")
    # 每一行：原始碼裡的中文字串數量 > 編譯器記錄的查表字串數量，就表示有沒查表的中文
    loose = []
    for swift in own_swift_files():
        rel = str(swift.relative_to(SOURCES))
        per_line = {}
        for line_no, key in found.get(rel, []):
            if CJK.search(key):
                per_line[line_no] = per_line.get(line_no, 0) + 1
        for no, line in enumerate(swift.read_text().split("\n"), 1):
            stripped = line.strip()
            if stripped.startswith("//") or "log." in line or "zh:" in line or "l10n-skip" in line:
                continue
            literals = [t for t in string_literals(line) if CJK.search(t)]
            if len(literals) > per_line.get(no, 0):
                loose.append(f"{rel}:{no}: {stripped[:90]}")
    if loose:
        problems += len(loose)
        print(f"程式碼裡沒查表的中文（{len(loose)} 行）：")
        for item in loose:
            print(f"  {item}")
    if problems:
        sys.exit(1)
    print("翻譯表完整，沒有漏掉的介面中文")


def apply(path):
    """把翻譯（JSON：{鍵: 英文} 或 {鍵: {"one": …, "other": …}}）寫進翻譯表"""
    cat = load_catalog()
    translations = json.loads(Path(path).read_text())
    unknown = []
    for key, value in translations.items():
        if key not in cat["strings"]:
            unknown.append(key)
            continue
        entry = cat["strings"][key]
        locs = entry.setdefault("localizations", {})
        if isinstance(value, dict):
            locs["en"] = {"variations": {"plural": {form: {"stringUnit": {"state": "translated", "value": text}}
                                                    for form, text in value.items()}}}
        else:
            locs["en"] = {"stringUnit": {"state": "translated", "value": value}}
    save_catalog(cat)
    print(f"寫入 {len(translations) - len(unknown)} 條")
    if unknown:
        print(f"翻譯表裡沒有這些鍵（{len(unknown)} 條）：")
        for k in unknown:
            print(f"  {k}")


# MARK: Windows 版（design-windows §11.4）

WINDOWS_SOURCES = ROOT / "windows" / "src"
WINDOWS_CATALOG = WINDOWS_SOURCES / "Shiyin.App" / "Strings" / "Windows.xcstrings"
# 不顯示在介面上的中文：提示詞、範本、資料庫的 SQL、評估工具、DEBUG 控制通道
WINDOWS_SKIP = {"Shiyin.Core/Notes/NotesPrompt.cs", "Shiyin.Core/Notes/Templates.cs", "Shiyin.Core/Notes/TranscriptCorrection.cs",
                "Shiyin.Core/Notes/SpeakerNaming.cs", "Shiyin.Core/Notes/AskPrompt.cs", "Shiyin.Data/MigrationSql.cs",
                "Shiyin.App/Debug/ControlPipe.cs", "Shiyin.Core/IO/Exporters.cs", "Shiyin.Core/Model/TimeFormat.cs"}
WINDOWS_SKIP_DIRS = ("Shiyin.Eval/", "Shiyin.Ctl/", "/obj/", "/bin/")
CALL = re.compile(r'\bL\.(T|F)\(\s*"((?:[^"\\]|\\.)*)"')
CS_STRING = re.compile(r'"((?:[^"\\]|\\.)*)"')


def windows_files():
    for p in sorted(WINDOWS_SOURCES.rglob("*.cs")):
        rel = str(p.relative_to(WINDOWS_SOURCES))
        if rel in WINDOWS_SKIP or any(d in "/" + rel for d in WINDOWS_SKIP_DIRS):
            continue
        yield rel, p


def unescape(text):
    return text.replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\")


def load_windows_catalog():
    if WINDOWS_CATALOG.exists():
        return json.loads(WINDOWS_CATALOG.read_text())
    return {"sourceLanguage": "zh-Hant", "strings": {}, "version": "1.0"}


def windows_keys():
    """{鍵: [位置]}：L.T、L.F 的第一個參數"""
    keys = {}
    for rel, path in windows_files():
        for no, line in enumerate(path.read_text().split("\n"), 1):
            for m in CALL.finditer(line):
                keys.setdefault(unescape(m.group(2)), []).append(f"{rel}:{no}")
    return keys


def windows_check():
    mac = load_catalog()["strings"]
    win = load_windows_catalog()["strings"]
    keys = windows_keys()
    missing = [k for k in sorted(keys) if CJK.search(k) and not has_english(win.get(k, {})) and not has_english(mac.get(k, {}))]
    problems = 0
    if missing:
        problems += len(missing)
        print(f"缺英文翻譯（{len(missing)} 條）：")
        for k in missing:
            print(f"  {k}    ← {keys[k][0]}")
    # 一行裡的中文字串比 L.T、L.F 用到的多：有沒查表的中文
    loose = []
    for rel, path in windows_files():
        for no, line in enumerate(path.read_text().split("\n"), 1):
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("///") or "Log." in line or "l10n-skip" in line:
                continue
            if ".Text(" in line or "Ui(\"" in line:   # MeetingLanguage.Text(zh, en, ja)、MeetingLanguages.Ui(zh, en)：各語言自己寫好
                continue
            literals = [t for t in CS_STRING.findall(line.split("//")[0]) if CJK.search(t)]
            looked_up = [m.group(2) for m in CALL.finditer(line) if CJK.search(m.group(2))]
            if len(literals) > len(looked_up):
                loose.append(f"{rel}:{no}: {stripped[:100]}")
    if loose:
        problems += len(loose)
        print(f"程式碼裡沒查表的中文（{len(loose)} 行）：")
        for item in loose:
            print(f"  {item}")
    if problems:
        sys.exit(1)
    print("Windows 版的翻譯完整，沒有漏掉的介面中文")


def windows_apply(path):
    """Windows 才有的英文翻譯：{鍵: 英文} 或 {鍵: {"one": …, "other": …}}，寫進 Windows.xcstrings"""
    cat = load_windows_catalog()
    translations = json.loads(Path(path).read_text())
    for key, value in translations.items():
        entry = cat["strings"].setdefault(key, {"extractionState": "manual"})
        locs = entry.setdefault("localizations", {})
        if isinstance(value, dict):
            locs["en"] = {"variations": {"plural": {form: {"stringUnit": {"state": "translated", "value": text}}
                                                    for form, text in value.items()}}}
        else:
            locs["en"] = {"stringUnit": {"state": "translated", "value": value}}
    cat["strings"] = dict(sorted(cat["strings"].items()))
    WINDOWS_CATALOG.parent.mkdir(parents=True, exist_ok=True)
    WINDOWS_CATALOG.write_text(json.dumps(cat, ensure_ascii=False, indent=2) + "\n")
    print(f"寫入 {len(translations)} 條")


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else "check"
    if command == "apply":
        apply(sys.argv[2])
    elif command == "windows-apply":
        windows_apply(sys.argv[2])
    else:
        {"extract": extract, "check": check, "windows-check": windows_check}[command]()
