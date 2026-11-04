#!/usr/bin/env python3
"""產生粵語、韓文的轉錄評估資料（design §41.7），加進 ~/Library/Caches/ShiyinSpike/eval32/datasets.json。資料不放進 repo。

  scripts/dev/make-eval-data-langs.py

- yue-fleurs、ko-fleurs：FLEURS 粵語（yue_hant_hk）、韓文（ko_kr）測試集的前 60 句（朗讀維基百科，CC BY 4.0），
  接成一個檔案，句子之間空 0.8 秒。只串流下載到第 60 句就停，不存整個壓縮檔
- yue-synth、ko-synth：用 macOS 的語音合成的開發團隊週會（夾英文產品名、人名），附詞彙表和關鍵詞。
  粵語只有一個聲音（善怡），韓文用三個聲音
"""
import json
import re
import subprocess
import tarfile
import tempfile
import urllib.request
import wave
from pathlib import Path

import numpy as np

EVAL = Path.home() / "Library/Caches/ShiyinSpike/eval32"
RATE = 16000
FLEURS = "https://huggingface.co/datasets/google/fleurs/resolve/main/data/{}/"
FLEURS_COUNT = 60

LANGS = {
    "yue": {
        "fleurs": "yue_hant_hk", "say": "zh_HK",
        "glossary": ["Lumio", "Redis", "Kubernetes", "GitHub Actions", "陳家豪", "李嘉欣"],
        "keywords": ["Lumio", "Redis", "Kubernetes", "GitHub Actions", "陳家豪", "李嘉欣", "Slack"],
        "script": [
            ("A", "Sinji", "大家好，我係陳家豪。今日我哋主要傾下 Lumio 下個版本嘅進度。"),
            ("B", "Sinji", "我係李嘉欣。上個禮拜五已經部署咗去測試環境，暫時未見到大問題。"),
            ("A", "Sinji", "唔該晒。上次話 API 回應好慢，而家點呀？"),
            ("B", "Sinji", "我哋加咗 Redis 做快取，平均回應時間跌到兩百毫秒左右，繁忙時間都喺五百毫秒以內。"),
            ("C", "Sinji", "測試方面，自動測試全部都喺 GitHub Actions 度跑，不過付款頁面有一個測試唔係好穩定。"),
            ("A", "Sinji", "嗰個測試可唔可以喺發佈之前搞掂？"),
            ("C", "Sinji", "應該係超時設定嘅問題，我諗今個禮拜之內可以處理好。"),
            ("B", "Sinji", "另外我想喺 Kubernetes 加多一部機，因為下個月搞推廣，流量估計會多三倍。"),
            ("A", "Sinji", "大概要貴幾多錢？"),
            ("B", "Sinji", "每個月大約多八千蚊，推廣完咗就可以減返。"),
            ("A", "Sinji", "好，我會同經理傾。仲有，嘉欣你可唔可以喺禮拜三之前寫好還原步驟，再放上 Slack？"),
            ("B", "Sinji", "冇問題，禮拜三之前搞掂。"),
            ("A", "Sinji", "咁發佈日期照舊定喺十一月五號。今日就傾到呢度，多謝大家。"),
        ],
    },
    "ko": {
        "fleurs": "ko_kr", "say": "ko_KR",
        "glossary": ["Lumio", "Redis", "Kubernetes", "GitHub Actions", "김민준", "이서연"],
        "keywords": ["Lumio", "Redis", "Kubernetes", "GitHub Actions", "김민준", "이서연", "Slack"],
        "script": [
            ("A", "Yuna", "안녕하세요, 김민준입니다. 오늘은 Lumio 다음 버전 진행 상황을 확인하겠습니다."),
            ("B", "Eddy", "이서연입니다. 지난주 금요일에 스테이징 환경에 배포했고, 아직 큰 문제는 없습니다."),
            ("A", "Yuna", "감사합니다. 지난번에 API 응답이 느리다는 이야기가 있었는데, 지금은 어떤가요?"),
            ("B", "Eddy", "Redis 캐시를 넣어서 평균 응답 시간이 이백 밀리초 정도로 줄었습니다. 피크 때도 오백 밀리초 이내입니다."),
            ("C", "Flo", "테스트는 전부 GitHub Actions에서 자동으로 돌리고 있습니다. 다만 결제 화면 테스트 하나가 불안정합니다."),
            ("A", "Yuna", "그 테스트는 출시 전까지 고칠 수 있을까요?"),
            ("C", "Flo", "타임아웃 설정 문제인 것 같아서, 이번 주 안에 해결할 수 있을 것 같습니다."),
            ("B", "Eddy", "그리고 Kubernetes 노드를 하나 더 늘리고 싶습니다. 다음 달 캠페인 때 트래픽이 세 배 정도 늘 것 같습니다."),
            ("A", "Yuna", "비용은 얼마나 늘어나나요?"),
            ("B", "Eddy", "한 달에 팔십만 원 정도입니다. 캠페인이 끝나면 다시 줄일 수 있습니다."),
            ("A", "Yuna", "알겠습니다. 부장님과 상의하겠습니다. 서연 씨, 수요일까지 롤백 절차를 정리해서 Slack에 올려 주시겠어요?"),
            ("B", "Eddy", "네, 수요일까지 하겠습니다."),
            ("A", "Yuna", "그럼 출시일은 예정대로 십일월 오일로 하겠습니다. 오늘은 여기까지 하겠습니다. 수고하셨습니다."),
        ],
    },
}


def read_wav(path):
    with wave.open(str(path)) as w:
        assert w.getframerate() == RATE and w.getsampwidth() == 2, path
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768
        return data.reshape(-1, w.getnchannels()).mean(axis=1)


def write_wav(path, samples):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(samples, -1, 1) * 32767).astype(np.int16).tobytes())


def concat(pieces, gap):
    silence = np.zeros(int(gap * RATE), dtype=np.float32)
    audio, segments, t = [silence], [], gap
    for speaker, samples, text in pieces:
        segment = {"start": round(t, 3), "end": round(t + len(samples) / RATE, 3), "text": text}
        if speaker:
            segment["speaker"] = speaker
        segments.append(segment)
        audio += [samples, silence]
        t += len(samples) / RATE + gap
    return np.concatenate(audio), segments


def to_wav(source, target):
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source), "-vn", "-ac", "1", "-ar", str(RATE),
                    "-c:a", "pcm_s16le", str(target)], check=True)


def fleurs(code, spec, out):
    base = FLEURS.format(spec["fleurs"])
    rows = {}
    with urllib.request.urlopen(base + "test.tsv") as r:
        for line in r.read().decode().splitlines():
            cols = line.split("\t")
            rows[cols[1]] = (cols[0], cols[2])
    pieces, seen = [], set()
    with urllib.request.urlopen(base + "audio/test.tar.gz") as r, tempfile.TemporaryDirectory() as tmp:
        with tarfile.open(fileobj=r, mode="r|gz") as tar:
            for member in tar:
                name = Path(member.name).name
                if name not in rows or rows[name][0] in seen:
                    continue
                seen.add(rows[name][0])
                path = Path(tmp) / name
                path.write_bytes(tar.extractfile(member).read())
                to_wav(path, path.with_suffix(".16k.wav"))
                pieces.append((None, read_wav(path.with_suffix(".16k.wav")), rows[name][1]))
                if len(pieces) == FLEURS_COUNT:
                    break
    audio, segments = concat(pieces, 0.8)
    write_wav(out / "fleurs.wav", audio)
    (out / "fleurs.ref.json").write_text(json.dumps({"lang": code, "segments": segments}, ensure_ascii=False, indent=1))
    return {"name": f"{code}-fleurs", "audio": str(out / "fleurs.wav"), "ref": str(out / "fleurs.ref.json"), "language": code,
            "note": f"FLEURS {spec['fleurs']} 測試集前 {FLEURS_COUNT} 句（朗讀維基百科，CC BY 4.0），{len(audio) / RATE / 60:.0f} 分鐘"}


def voices(locale):
    """macOS 的語音，名稱跟著系統語言（中文介面是「Eddy (韓文（南韓）)」），要用清單上的完整名稱"""
    out = subprocess.run(["say", "-v", "?"], capture_output=True, text=True, check=True).stdout
    found = {}
    for line in out.splitlines():
        match = re.match(r"(.+?)\s{2,}" + locale + r"\s", line)
        if match:
            name = match.group(1).strip()
            found[name.split(" (")[0]] = name
    return found


def synth(code, spec, out):
    names = voices(spec["say"])
    pieces = []
    with tempfile.TemporaryDirectory() as tmp:
        for i, (speaker, voice, text) in enumerate(spec["script"]):
            path = Path(tmp) / f"{i}.wav"
            subprocess.run(["say", "-v", names[voice], "-o", str(path), "--file-format=WAVE",
                            f"--data-format=LEI16@{RATE}", text], check=True)
            pieces.append((speaker, read_wav(path), text))
    audio, segments = concat(pieces, 0.6)
    write_wav(out / "synth.wav", audio)
    (out / "synth.ref.json").write_text(json.dumps({"lang": code, "segments": segments}, ensure_ascii=False, indent=1))
    speakers = len({s for s, _, _ in spec["script"]})
    return {"name": f"{code}-synth", "audio": str(out / "synth.wav"), "ref": str(out / "synth.ref.json"), "language": code,
            "glossary": spec["glossary"], "keywords": spec["keywords"],
            "note": f"合成的{'粵語' if code == 'yue' else '韓文'}開發週會（{len(audio) / RATE:.0f} 秒、{speakers} 人的台詞，夾英文產品名）"}


def main():
    new = []
    for code, spec in LANGS.items():
        out = EVAL / code
        out.mkdir(parents=True, exist_ok=True)
        new += [synth(code, spec, out), fleurs(code, spec, out)]
    path = EVAL / "datasets.json"
    datasets = [d for d in json.loads(path.read_text()) if d["name"] not in {n["name"] for n in new}]
    path.write_text(json.dumps(datasets + new, ensure_ascii=False, indent=4))
    for d in new:
        print(d["name"], "：", d["note"])


if __name__ == "__main__":
    main()
