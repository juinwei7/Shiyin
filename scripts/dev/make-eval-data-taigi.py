#!/usr/bin/env python3
"""產生台語的轉錄評估資料（design §41.9），加進 ~/Library/Caches/ShiyinSpike/eval32/datasets.json。資料不放進 repo。

  scripts/dev/make-eval-data-taigi.py

來源跟聯發科 Breeze Taigi 的評測一樣（arXiv 2603.19259 §3.2）：行政院的「廣播公益廣告月包」，同一則廣告有華語、臺語、
客語三個版本，附廣告腳本。正確答案是腳本的華語文字（台語錄音也對華語文字算字錯誤率，跟論文一樣），所以數字只拿來比引擎之間的高低，
完美的台語辨識也不會是 0%。腳本的整理（拿掉角色名、音效、舞台指示）是自動的，難免有幾個字不準，對每個引擎都一樣。

- taigi-psa：台語版接成一個檔案，句子之間空 0.8 秒
- taigi-psa-zh：同樣的廣告的華語版（看換成台語模型之後，國語有沒有變差）
- taigi-mix：一則華語、一則台語交替（國台語交替的會議的替代品；真的會議是一句裡面也會換）
"""
import difflib
import io
import json
import re
import subprocess
import tempfile
import urllib.request
import wave
import zipfile
from pathlib import Path

import numpy as np

EVAL = Path.home() / "Library/Caches/ShiyinSpike/eval32"
OUT = EVAL / "taigi"
RATE = 16000
# 行政院全球資訊網 → 音檔下載區的月包（2026-11-05 查的）
MONTHS = [
    ("113-02", "69490A841325D976"), ("113-03", "5DD8727A640DB7A7"), ("113-06", "FD4B7ACB79C2F6A2"),
    ("113-08", "24113ECCC479A7B"), ("113-10", "A552F3B433C3349A"), ("113-12", "CE03B798159FBAFE"),
    ("114-01", "9A3284F09C848359"), ("114-08", "242EECC388534D07"), ("115-03", "4A5B06954B22A34D"),
    ("115-04", "3F7EEEAE8DB5FA54"),
]
LANG = {"華語": "zh", "國語": "zh", "臺語": "tw", "台語": "tw", "閩南語": "tw", "客語": "hak", "自然語": "mix"}
LANG_IN_NAME = r"(華語|國語|臺語|台語|閩南語|客語|自然語)"
TOOLS = Path.home() / "Library/Caches/ShiyinBuild/tools"
# 舊的月包腳本是 PDF：用 PDFKit 讀文字（不用另外裝套件）
PDF_TEXT = """
import PDFKit
print(PDFDocument(url: URL(fileURLWithPath: CommandLine.arguments[1]))?.string ?? "")
"""


def download(month, file_id):
    path = OUT / "zips" / f"{month}.zip"
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        request = urllib.request.Request(f"https://www.ey.gov.tw/File/{file_id}?A=C", headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(request) as r:
            path.write_bytes(r.read())
    z = zipfile.ZipFile(path)
    for info in z.infolist():   # 舊的月包檔名是 Big5（沒有 UTF-8 旗標）
        if not info.flag_bits & 0x800:
            info.filename = info.filename.encode("cp437").decode("cp950", errors="replace")
    z.NameToInfo = {info.filename: info for info in z.infolist()}
    return z


def norm_title(text):
    """「3、法務部 民法意定監護制度(華、臺、客語)- 舊檔續播」「法務部 民法意定監護制度(臺語)30秒」→「法務部民法意定監護制度」"""
    text = re.sub(r"^\s*\d+\s*[、.．]\s*", "", text)
    text = re.sub(r"[（(]\s*(?:華|臺|台|客|國|閩|自然)[^）)]*[）)].*$", "", text)
    text = re.sub(r"\s" + LANG_IN_NAME + r".*$", "", text)
    text = re.sub(r"\d+\s*秒.*$", "", text)
    return re.sub(r"[\s　()（）]", "", text)


def docx_paragraphs(data):
    xml = zipfile.ZipFile(io.BytesIO(data)).read("word/document.xml").decode("utf-8")
    return ["".join(re.findall(r"<w:t[^>]*>([^<]*)</w:t>", p)) for p in re.findall(r"<w:p[ >].*?</w:p>", xml, re.S)]


def pdf_lines(data):
    tool = TOOLS / "pdf-text"
    if not tool.exists():
        TOOLS.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "pdf-text.swift").write_text(PDF_TEXT)
            subprocess.run(["swiftc", str(Path(tmp) / "pdf-text.swift"), "-o", str(tool)], check=True)
    with tempfile.NamedTemporaryFile(suffix=".pdf") as f:
        f.write(data)
        f.flush()
        text = subprocess.run([str(tool), f.name], capture_output=True, text=True, check=True).stdout
    return [line for line in text.splitlines() if not re.fullmatch(r"\s*\d+\s*", line)]   # 拿掉頁碼


def scripts(paragraphs):
    """腳本：「N、標題」開頭，接著台詞，直到下一個「N、」。有分「華語」「臺語」「客語」小節的只取華語"""
    items, current = [], None
    for line in paragraphs:
        heading = re.match(r"^\s*(\d+)\s*[、．.]\s*(\D.+)$", line)
        if heading and ("語" in line or "部" in line or "院" in line or "府" in line or "署" in line):
            current = {"heading": line.strip(), "lines": []}
            items.append(current)
        elif current is not None and line.strip():
            current["lines"].append(line.strip())
    for item in items:
        sections, name = {}, None
        for line in item["lines"]:
            bare = re.sub(r"[【】\[\]\s]", "", line)
            if bare in LANG:
                name = LANG[bare]
                continue
            sections.setdefault(name, []).append(line)
        item["zh"] = sections.get("zh") or sections.get(None) or []
        item["tw"] = sections.get("tw") or []
    return items


def clean(lines):
    """台詞→會念出來的文字：拿掉角色名、音效、舞台指示、配音說明"""
    out = []
    for line in lines:
        line = line.replace("　", " ").strip()
        if re.match(r"^[（(【\[]", line) and re.fullmatch(r"[（(【\[].*[）)】\]]", line):
            continue   # 整行都是舞台指示
        if re.match(r"^(背景音效|音效|音樂|SE|BGM|角色|配音)", line) or re.search(r"[男女]聲[；;，,]", line) or line in ("聲音。", "聲音"):
            continue
        if re.match(r"^[-–—]\s*(新檔首播|舊檔續播)", line) or re.search(r"(的聲音|響起|安靜下來)[。，]?$", line):
            continue   # 標題換行剩下的「-新檔首播」、音效的描述
        line = re.sub(r"^(旁白|OS)(?=[\u4e00-\u9fff])", "", line)
        line = re.sub(r"【[^】]*】", "", line)
        line = re.sub(r"^[^：:「」，。！？]{1,16}[：:]", "", line)   # 角色名：「民宿主人（溫柔）：」「OS：」
        line = re.sub(r"[（(][^（）()]{0,10}[）)]", lambda m: "" if re.search(r"[一-鿿]", m.group(0)) and not re.search(r"\d", m.group(0)) else m.group(0), line)
        line = re.sub(r"^[^。！？]{0,30}(的聲音|音效)[。，]", "", line.strip())   # 開頭的音效描述
        line = re.sub(r"^(旁白|OS)(?=[\u4e00-\u9fff])", "", line.strip())
        line = re.sub(r"[~～]", "", line).strip()
        if line:
            out.append(line)
    return "".join(out)


def to_samples(data):
    with tempfile.TemporaryDirectory() as tmp:
        src, dst = Path(tmp) / "a.mp3", Path(tmp) / "a.wav"
        src.write_bytes(data)
        subprocess.run(["afconvert", "-f", "WAVE", "-d", f"LEI16@{RATE}", "-c", "1", str(src), str(dst)], check=True)
        with wave.open(str(dst)) as w:
            return np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768


def write_wav(path, samples):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes())


def concat(pieces, gap=0.8):
    silence = np.zeros(int(gap * RATE), dtype=np.float32)
    audio, segments, t = [silence], [], gap
    for samples, text in pieces:
        segments.append({"start": round(t, 3), "end": round(t + len(samples) / RATE, 3), "text": text})
        audio += [samples, silence]
        t += len(samples) / RATE + gap
    return np.concatenate(audio), segments


def collect():
    items, seen = [], set()
    for month, file_id in MONTHS:
        z = download(month, file_id)
        names = z.namelist()
        docx = next((n for n in names if n.endswith(".docx") and "腳本" in n), None)
        pdf = next((n for n in names if n.endswith(".pdf") and "腳本" in n), None)
        if not docx and not pdf:
            print(f"{month}：沒有腳本，跳過")
            continue
        texts = scripts(docx_paragraphs(z.read(docx)) if docx else pdf_lines(z.read(pdf)))
        audio = {}
        for n in names:
            m = re.search(r"[（(\s]" + LANG_IN_NAME + r"[）)]?\s*\d*\s*秒?", Path(n).name)
            if n.endswith(".mp3") and m:
                audio.setdefault(norm_title(Path(n).stem), {})[LANG[m.group(1)]] = n
        for title, files in audio.items():
            if "zh" not in files or "tw" not in files or title in seen:
                continue
            best = max(texts, key=lambda s: difflib.SequenceMatcher(None, title, norm_title(s["heading"])).ratio(), default=None)
            score = difflib.SequenceMatcher(None, title, norm_title(best["heading"])).ratio() if best else 0
            ref = clean(best["zh"]) if best else ""
            if score < 0.6 or len(ref) < 20:
                print(f"{month}：{title} 對不到腳本（{score:.2f}），跳過")
                continue
            if any(difflib.SequenceMatcher(None, ref, i["ref"]).ratio() > 0.85 for i in items):
                print(f"{month}：{title} 跟前面的廣告幾乎一樣（換了名字續播），跳過")
                continue
            seen.add(title)
            items.append({"month": month, "title": title, "heading": best["heading"], "match": round(score, 2), "ref": ref,
                          "taigiScript": clean(best["tw"]), "zh": z.read(files["zh"]), "tw": z.read(files["tw"])})
    return items


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    items = collect()
    for item in items:
        item["zhSamples"], item["twSamples"] = to_samples(item.pop("zh")), to_samples(item.pop("tw"))
    sets = {
        "taigi-psa": [(i["twSamples"], i["ref"]) for i in items],
        "taigi-psa-zh": [(i["zhSamples"], i["ref"]) for i in items],
        "taigi-mix": [(i["zhSamples"] if k % 2 == 0 else i["twSamples"], i["ref"]) for k, i in enumerate(items)],
    }
    notes = {"taigi-psa": "台語版", "taigi-psa-zh": "同樣廣告的華語版", "taigi-mix": "華語、台語一則一則交替"}
    new = []
    for name, pieces in sets.items():
        audio, segments = concat(pieces)
        write_wav(OUT / f"{name}.wav", audio)
        (OUT / f"{name}.ref.json").write_text(json.dumps({"lang": "zh", "segments": segments}, ensure_ascii=False, indent=1))
        new.append({"name": name, "audio": str(OUT / f"{name}.wav"), "ref": str(OUT / f"{name}.ref.json"), "language": "zh",
                    "note": f"行政院廣播公益廣告 {len(pieces)} 則的{notes[name]}，正確答案是華語腳本（{len(audio) / RATE / 60:.0f} 分鐘）"})
    (OUT / "manifest.json").write_text(json.dumps([{k: v for k, v in i.items() if not k.endswith("Samples")} for i in items],
                                                  ensure_ascii=False, indent=1))
    path = EVAL / "datasets.json"
    datasets = [d for d in json.loads(path.read_text()) if d["name"] not in {n["name"] for n in new}]
    path.write_text(json.dumps(datasets + new, ensure_ascii=False, indent=4))
    for d in new:
        print(d["name"], "：", d["note"])


if __name__ == "__main__":
    main()
