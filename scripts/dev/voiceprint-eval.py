#!/usr/bin/env python3
"""聲紋門檻評估（design §21.4.1）。

用法：
  shiyinctl voiceprint-eval <設定.json> <結果.json>        # app 跑說話者辨識，輸出每位說話者的聲紋和說話時段
  python3 scripts/dev/voiceprint-eval.py <結果.json> [AMI 標註資料夾]

每位說話者依說話時段跟 AMI 標註（segments/*.segments.xml）重疊最多的人，對到真實身分（global_name）。
模擬使用者的做法：第一場會議（設定裡的第一個）每個人都取名一次，之後的會議用聲紋自動帶入；
再模擬「每場會議結束後使用者把名字改對，聲紋越存越多」的情況。其他組的會議裡每個人都是陌生人，帶入任何名字都算認錯。
輸出各門檻的自動帶入比例和認錯比例。
"""
import json
import math
import os
import re
import sys
from collections import defaultdict

RESULT = sys.argv[1]
AMI = sys.argv[2] if len(sys.argv) > 2 else os.path.expanduser("~/Library/Caches/ShiyinSpike/Datasets/ami_public_1.6.2")
MIN_SECONDS = 8.0          # 說話少於這個秒數不比對、不學（跟 app 一樣，design §21.4）


def meeting_id(name):
    return re.match(r"([A-Z]{2}\d{4}[a-d])", name).group(1)


def reference(meeting):
    """{global_name: [(start, end)]}"""
    meetings = open(os.path.join(AMI, "corpusResources/meetings.xml"), encoding="iso-8859-1").read()
    block = re.search(r'observation="%s".*?</meeting>' % meeting, meetings, re.S).group(0)
    agents = dict(re.findall(r'nxt_agent="(\w)"[^>]*global_name="(\w+)"', block))
    out = {}
    for agent, person in agents.items():
        path = os.path.join(AMI, "segments", f"{meeting}.{agent}.segments.xml")
        xml = open(path, encoding="iso-8859-1").read()
        out[person] = [(float(a), float(b)) for a, b in re.findall(r'transcriber_start="([\d.]+)" transcriber_end="([\d.]+)"', xml)]
    return out


def overlap(turns, segments):
    total = 0.0
    for s, e in turns:
        for a, b in segments:
            total += max(0.0, min(e, b) - max(s, a))
    return total


def cosine(a, b):
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    return dot / (na * nb) if na and nb else 0.0


data = json.load(open(RESULT))["results"]
meetings = []
for item in data:
    if "error" in item:
        print("跳過", item["name"], item["error"])
        continue
    ref = reference(meeting_id(item["name"]))
    speakers = []
    for sp in item["speakers"]:
        scores = {p: overlap(sp["turns"], segs) for p, segs in ref.items()}
        best = max(scores, key=scores.get)
        purity = scores[best] / max(sp["seconds"], 0.01)
        speakers.append({"number": sp["number"], "seconds": sp["seconds"], "embedding": sp["embedding"],
                         "truth": best if scores[best] > 0 else None, "purity": min(purity, 1.0)})
    meetings.append({"name": item["name"], "people": set(ref), "speakers": speakers})
    print(f'{item["name"]}: {item["seconds"] / 60:.1f} 分鐘，辨識出 {len(speakers)} 位（標註 {len(ref)} 位），'
          f'說話 ≥ {MIN_SECONDS:.0f} 秒的 {sum(s["seconds"] >= MIN_SECONDS for s in speakers)} 位；'
          f'對到的人 {sorted(set(s["truth"] for s in speakers if s["truth"]))}')

enroll = meetings[0]
group = enroll["people"]


def simulate(threshold, progressive):
    library = defaultdict(list)          # 真實身分 → 聲紋（使用者取的名字就是真實身分）
    for sp in enroll["speakers"]:
        if sp["truth"] and sp["seconds"] >= MIN_SECONDS:
            library[sp["truth"]].append(sp["embedding"])
    stats = {"known": 0, "correct": 0, "wrong": 0, "stranger": 0, "stranger_wrong": 0}
    for m in meetings[1:]:
        same_group = m["people"] == group
        candidates = []
        for i, sp in enumerate(m["speakers"]):
            if sp["seconds"] < MIN_SECONDS:
                continue
            for person, prints in library.items():
                score = max(cosine(sp["embedding"], p) for p in prints)
                if score >= threshold:
                    candidates.append((score, i, person))
        candidates.sort(reverse=True)
        used_speakers, used_people, assigned = set(), set(), {}
        for score, i, person in candidates:
            if i in used_speakers or person in used_people:
                continue
            used_speakers.add(i); used_people.add(person); assigned[i] = person
        for i, sp in enumerate(m["speakers"]):
            if sp["seconds"] < MIN_SECONDS:
                continue
            if same_group:
                if sp["truth"] in library:
                    stats["known"] += 1
                if i in assigned:
                    if assigned[i] == sp["truth"]:
                        stats["correct"] += 1
                    else:
                        stats["wrong"] += 1
            else:
                stats["stranger"] += 1
                if i in assigned:
                    stats["stranger_wrong"] += 1
        if progressive and same_group:
            # 使用者把這場的名字改對（自動帶入的、沒帶入的都一樣），聲紋存起來
            for sp in m["speakers"]:
                if sp["truth"] and sp["seconds"] >= MIN_SECONDS:
                    library[sp["truth"]].append(sp["embedding"])
    return stats


for progressive in (False, True):
    print()
    print("每場結束後把名字改對（聲紋越存越多）" if progressive else "只用第一場學到的聲紋")
    print(" 門檻  自動帶入  帶錯（同組）  陌生人被帶入  認錯率（帶錯＋陌生人被帶入）／帶入次數")
    for t in [x / 100 for x in range(30, 96, 5)]:
        s = simulate(t, progressive)
        filled = s["correct"] + s["wrong"] + s["stranger_wrong"]
        wrong = s["wrong"] + s["stranger_wrong"]
        print(f' {t:.2f}  {s["correct"]:3d}/{s["known"]:<3d}({s["correct"] / max(s["known"], 1):5.1%})  '
              f'{s["wrong"]:3d}  {s["stranger_wrong"]:3d}/{s["stranger"]:<3d}  {wrong / max(filled, 1):6.1%}')
