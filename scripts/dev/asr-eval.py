#!/usr/bin/env python3
"""轉錄評估（design §17.1、R14.1）：用同一組設定跑所有評估資料，算分數，結果累積在 results.jsonl。

  scripts/dev/asr-eval.py <這次的名稱> [--options '{"engine": "system"}'] [--only aishell,ami-mix]
  scripts/dev/asr-eval.py <這次的名稱> --correct-from 3.1-breeze [--only synth]   # 拿某次的轉錄結果跑逐字稿校正（design §17.4）
  scripts/dev/asr-eval.py --table          # 列出目前所有結果

要先開著 DEBUG 版的拾音（scripts/build-app.sh --debug），轉錄由 app 用跟正式流程一樣的程式跑（shiyinctl transcribe-eval）。
資料在 ~/Library/Caches/ShiyinSpike/eval32（scripts/dev/make-eval-data.py 產生）。
"""
import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EVAL = Path.home() / "Library/Caches/ShiyinSpike/eval32"
BUILD = Path.home() / "Library/Caches/ShiyinBuild"
CTL = BUILD / "shiyinctl"
SCORER = BUILD / "tools/asr-score"
SCORER_SRC = ROOT / "scripts/dev/asr-score.swift"


def ensure_scorer():
    if not SCORER.exists() or SCORER.stat().st_mtime < SCORER_SRC.stat().st_mtime:
        SCORER.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["swiftc", "-O", str(SCORER_SRC), "-o", str(SCORER)], check=True)


def audio_seconds(path):
    out = subprocess.run(["afinfo", path], capture_output=True, text=True).stdout
    for line in out.splitlines():
        if "estimated duration" in line:
            return float(line.split(":")[1].split()[0])
    return 600.0


def wait_for(output, seconds, name):
    deadline = time.time() + seconds
    while not output.exists():
        if time.time() > deadline:
            sys.exit(f"{name} 超過時間沒有結果，app 有開著嗎？")
        time.sleep(3)


def run(label, extra, only, correct_from=None):
    ensure_scorer()
    datasets = json.loads((EVAL / "datasets.json").read_text())
    if only:
        datasets = [d for d in datasets if d["name"] in only]
    out_dir = EVAL / "runs" / label
    out_dir.mkdir(parents=True, exist_ok=True)
    for d in datasets:
        options = {"language": d.get("language", "zh"), "glossary": d.get("glossary", [])}
        options.update(extra)
        options_path = out_dir / f"{d['name']}.options.json"
        options_path.write_text(json.dumps(options, ensure_ascii=False))
        output = out_dir / f"{d['name']}.json"
        output.unlink(missing_ok=True)
        if correct_from:
            source = EVAL / "runs" / correct_from / f"{d['name']}.json"
            if not source.exists():
                print(f"[{label}] {d['name']}：沒有 {correct_from} 的轉錄結果，跳過")
                continue
            print(f"[{label}] {d['name']}：校正中…", flush=True)
            subprocess.run([str(CTL), "correct-eval", str(source), str(options_path), str(output)], check=True)
            wait_for(output, 3600, d["name"])
        else:
            print(f"[{label}] {d['name']}：轉錄中…", flush=True)
            subprocess.run([str(CTL), "transcribe-eval", d["audio"], str(options_path), str(output)], check=True)
            wait_for(output, max(600, audio_seconds(d["audio"]) * 2), d["name"])
        result = json.loads(output.read_text())
        if "error" in result:
            print(f"  錯誤：{result['error']}")
            continue
        metrics_path = out_dir / f"{d['name']}.metrics.json"
        cmd = [str(SCORER), d["ref"], str(output), "--json", str(metrics_path)]
        if d.get("keywords"):
            cmd += ["--keywords", ",".join(d["keywords"])]
        if d.get("window"):
            cmd += ["--window", str(d["window"][0]), str(d["window"][1])]
        print(subprocess.run(cmd, capture_output=True, text=True, check=True).stdout.rstrip().replace("\n", "\n  ").join(["  ", ""]))
        if correct_from:
            print(f"  建議 {len(result.get('suggestions', []))} 則")
        metrics = json.loads(metrics_path.read_text())
        with open(EVAL / "results.jsonl", "a") as f:
            f.write(json.dumps({"label": label, "dataset": d["name"], "options": extra, "metrics": metrics,
                                "time": time.strftime("%Y-%m-%d %H:%M")}, ensure_ascii=False) + "\n")


def table():
    rows = [json.loads(line) for line in (EVAL / "results.jsonl").read_text().splitlines() if line.strip()]
    latest = {}
    for r in rows:
        latest[(r["label"], r["dataset"])] = r
    print(f"{'設定':22} {'資料':16} {'錯誤率':>7} {'去語助詞':>8} {'替換':>6} {'漏':>6} {'多':>6} {'關鍵詞':>6} {'說話者':>7} {'倍速':>6}")
    for (label, name), r in latest.items():
        m = r["metrics"]
        raw, nof = m["raw"], m["noFillers"]
        kw = m.get("keywords")
        spk = m.get("speakers")
        speed = ""
        if m.get("elapsed"):
            speed = f"{audio_seconds(next(d['audio'] for d in json.loads((EVAL / 'datasets.json').read_text()) if d['name'] == name)) / m['elapsed']:.1f}×"
        print(f"{label:22} {name:16} {raw['rate']:6.1f}% {nof['rate']:7.1f}% {raw['sub']:6} {raw['del']:6} {raw['ins']:6} "
              f"{(str(kw['hit']) + '/' + str(kw['total'])) if kw else '':>6} {(format(spk['accuracy'], '.1f') + '%') if spk else '':>7} {speed:>6}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("label", nargs="?")
    parser.add_argument("--options", default="{}")
    parser.add_argument("--only", default="")
    parser.add_argument("--table", action="store_true")
    parser.add_argument("--correct-from", default="")
    args = parser.parse_args()
    if args.table:
        table()
        return
    if not args.label:
        parser.error("要給這次的名稱")
    run(args.label, json.loads(args.options), set(filter(None, args.only.split(","))), args.correct_from or None)
    table()


if __name__ == "__main__":
    main()
