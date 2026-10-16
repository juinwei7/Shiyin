#!/usr/bin/env python3
"""產生轉錄評估用的資料（design §17.1、R14.1）。資料都放在 ~/Library/Caches/ShiyinSpike/eval32，不放進 repo。

  scripts/dev/make-eval-data.py

- AISHELL-4 M_R003S01C01（38 分鐘中文會議，遠距收音）：TextGrid 轉成標準答案
- AMI ES2004a（英文會議，CC BY 4.0）：
  - 下載四個人的個人麥克風（Headset-0～3）
  - words.xml 轉成標準答案（同一個人、間隔 1 秒內的字併成一段）
  - 混音：四個麥克風加起來，當作「現場會議、一支麥克風」
  - 模擬線上會議：A 的麥克風放聲道 0（麥克風），B、C、D 混在聲道 1（電腦聲音）
  - 模擬線上會議（有回音）：再把聲道 1 延遲 50 ms、音量 30% 加進聲道 0，模擬用喇叭開會時麥克風收到對方的聲音
- 寫出 datasets.json；其他資料（合成的中英夾雜會議、使用者的真實會議）自己加進去
"""
import json
import re
import urllib.request
import wave
from pathlib import Path

import numpy as np

EVAL = Path.home() / "Library/Caches/ShiyinSpike/eval32"
SPIKE = Path.home() / "Library/Caches/ShiyinSpike"
AMI_ANN = SPIKE / "Datasets/ami_public_1.6.2"
AMI_URL = "https://groups.inf.ed.ac.uk/ami/AMICorpusMirror/amicorpus/ES2004a/audio/ES2004a.Headset-{}.wav"
RATE = 16000


def read_wav(path):
    with wave.open(str(path)) as w:
        assert w.getframerate() == RATE and w.getsampwidth() == 2, path
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
        return data.reshape(-1, w.getnchannels()).mean(axis=1)


def write_wav(path, channels):
    n = max(len(c) for c in channels)
    stacked = np.zeros((n, len(channels)), dtype=np.float32)
    for i, c in enumerate(channels):
        stacked[: len(c), i] = c
    peak = np.abs(stacked).max()
    if peak > 0.99:
        stacked *= 0.99 / peak
    with wave.open(str(path), "wb") as w:
        w.setnchannels(len(channels))
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((stacked * 32767).astype(np.int16).tobytes())


def aishell():
    tg = (SPIKE / "aishell4/M_R003S01C01.TextGrid").read_text(encoding="utf-8")
    segments = []
    for tier in re.split(r"item \[\d+\]:", tg)[1:]:
        speaker = re.search(r'name = "([^"]*)"', tier).group(1)
        for start, end, text in re.findall(r'xmin = ([\d.]+)\s*\n\s*xmax = ([\d.]+)\s*\n\s*text = "([^"]*)"', tier):
            text = re.sub(r"<[^>]*>", "", text).strip()
            if text:
                segments.append({"start": float(start), "end": float(end), "speaker": speaker, "text": text})
    segments.sort(key=lambda s: s["start"])
    (EVAL / "aishell.ref.json").write_text(json.dumps({"lang": "zh", "segments": segments}, ensure_ascii=False))
    return {"name": "aishell", "audio": str(SPIKE / "aishell4/mono.wav"), "ref": str(EVAL / "aishell.ref.json"),
            "language": "zh", "note": "AISHELL-4 M_R003S01C01，38 分鐘、6 人、遠距收音"}


def ami_reference():
    segments = []
    for agent in "ABCD":
        root = (AMI_ANN / f"words/ES2004a.{agent}.words.xml").read_text(encoding="iso-8859-1")
        words = []
        for attrs, text in re.findall(r"<w ([^>]*)>([^<]*)</w>", root):
            start = re.search(r'starttime="([\d.]+)"', attrs)
            end = re.search(r'endtime="([\d.]+)"', attrs)
            if not start or not end:
                continue
            words.append((float(start.group(1)), float(end.group(1)), text, 'punc="true"' in attrs))
        current = None
        for start, end, text, punc in words:
            if punc:
                if current:
                    current["text"] += text
                continue
            if current and start - current["end"] < 1.0:
                current["text"] += " " + text
                current["end"] = end
            else:
                if current:
                    segments.append(current)
                current = {"start": start, "end": end, "speaker": agent, "text": text}
        if current:
            segments.append(current)
    segments.sort(key=lambda s: s["start"])
    (EVAL / "ami.ref.json").write_text(json.dumps({"lang": "en", "segments": segments}, ensure_ascii=False))


def ami():
    ami_dir = EVAL / "ami"
    ami_dir.mkdir(parents=True, exist_ok=True)
    heads = []
    for i in range(4):
        path = ami_dir / f"ES2004a.Headset-{i}.wav"
        if not path.exists():
            print(f"下載 {path.name}…")
            urllib.request.urlretrieve(AMI_URL.format(i), path)
        heads.append(read_wav(path))
    n = min(len(h) for h in heads)
    heads = [h[:n] for h in heads]
    ami_reference()
    mix = sum(heads)
    remote = heads[1] + heads[2] + heads[3]
    delay = int(0.05 * RATE)
    echo = heads[0] + 0.3 * np.concatenate([np.zeros(delay, dtype=np.float32), remote[:-delay]])
    write_wav(EVAL / "ami-mix.wav", [mix])
    write_wav(EVAL / "ami-online.wav", [heads[0], remote])
    write_wav(EVAL / "ami-online-echo.wav", [echo, remote])
    # 理想情況（戴耳機、麥克風只收到自己）：AMI 的個人麥克風也會收到同一間會議室其他人的聲音，
    # 用標準答案裡 A 說話的時間（前後各留 0.3 秒）把其他時間靜音
    keep = np.zeros(n, dtype=bool)
    for s in json.loads((EVAL / "ami.ref.json").read_text())["segments"]:
        if s["speaker"] == "A":
            keep[max(0, int((s["start"] - 0.3) * RATE)):int((s["end"] + 0.3) * RATE)] = True
    write_wav(EVAL / "ami-online-clean.wav", [np.where(keep, heads[0], 0), remote])
    ref = str(EVAL / "ami.ref.json")
    return [
        {"name": "ami-mix", "audio": str(EVAL / "ami-mix.wav"), "ref": ref, "language": "en",
         "note": "AMI ES2004a 四個個人麥克風混音，現場會議"},
        {"name": "ami-online", "audio": str(EVAL / "ami-online.wav"), "ref": ref, "language": "en", "channels": "both",
         "note": "模擬線上會議：A 在麥克風聲道，B、C、D 在電腦聲音聲道"},
        {"name": "ami-online-echo", "audio": str(EVAL / "ami-online-echo.wav"), "ref": ref, "language": "en", "channels": "both",
         "note": "模擬線上會議，麥克風聲道有 50 ms、30% 的回音"},
        {"name": "ami-online-clean", "audio": str(EVAL / "ami-online-clean.wav"), "ref": ref, "language": "en", "channels": "both",
         "note": "模擬線上會議的理想情況：麥克風聲道只留 A 說話的時間（戴耳機）"},
    ]


def main():
    EVAL.mkdir(parents=True, exist_ok=True)
    datasets_path = EVAL / "datasets.json"
    existing = json.loads(datasets_path.read_text()) if datasets_path.exists() else []
    generated = [aishell()] + ami()
    names = {d["name"] for d in generated}
    datasets = generated + [d for d in existing if d["name"] not in names]
    datasets_path.write_text(json.dumps(datasets, ensure_ascii=False, indent=2))
    for d in datasets:
        print(f"{d['name']:16} {d.get('note', '')}")


if __name__ == "__main__":
    main()
