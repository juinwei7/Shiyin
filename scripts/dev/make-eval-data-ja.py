#!/usr/bin/env python3
"""產生日文的轉錄評估資料（design §18.7、T-J0），加進 ~/Library/Caches/ShiyinSpike/eval32/datasets.json。資料不放進 repo。

  scripts/dev/make-eval-data-ja.py

- ja-fleurs：FLEURS 日文測試集的前 60 句（朗讀維基百科，CC BY 4.0），接成一個檔案，句子之間空 0.8 秒
- ja-synth：用 macOS 的日文語音合成的開發團隊週會（3 個人，夾片假名、英文產品名、人名），附詞彙表和關鍵詞
- ja-diet-1、ja-diet-2：日本眾議院內閣委員會 2025-04-16 AI 法案專家聽證的兩段問答（使用者沒有日文會議錄音）
  - 音訊：眾議院網路審議直播站的 HLS，用 ffmpeg 只抓需要的部分
  - 正確答案：國會會議錄 API 裡對應的發言。會議錄刪掉了語助詞、稍微整理過句子，錯字率會比實際高，只用來比較
"""
import json
import re
import subprocess
import tarfile
import tempfile
import unicodedata
import urllib.request
import wave
from pathlib import Path

import numpy as np

EVAL = Path.home() / "Library/Caches/ShiyinSpike/eval32"
JA = EVAL / "ja"
RATE = 16000

FLEURS = "https://huggingface.co/datasets/google/fleurs/resolve/main/data/ja_jp/"
FLEURS_COUNT = 60

DIET_MINUTES = "https://kokkai.ndl.go.jp/api/meeting?issueID=121704889X01420250416&recordPacking=json"
DIET_VIDEO = "https://hlsvod.shugiintv.go.jp/vod/_definst_/amlst:2025/2025-0416-0900-01/playlist.m3u8"
# 直播站發言者清單上的時間（秒），跟會議錄的發言編號。直播站的時間從委員長點名（「次に、三木圭恵君。」）開始，
# 到下一位議員的點名之前。會議錄不記委員長的點名插話（「三木圭恵君」「はい、よろしいですか」），辨識結果會多出這幾個字
DIET_BLOCKS = [
    ("ja-diet-1", "三木圭恵（日本維新の会）的提問與四位專家的回答", 5521.7, 6440.6, 33, 48),
    ("ja-diet-2", "おおたけりえ（立憲民主党）的提問與四位專家的回答", 4930.0, 5521.7, 22, 33),
]
DIET_GLOSSARY = ["松尾", "生貝", "安野", "さくらインターネット", "LLM", "GPU", "AGI"]
DIET_KEYWORDS = DIET_GLOSSARY + ["生成AI", "EU", "SNS", "GPT"]

# 合成會議：(說話者, 語音編號, 台詞)。語音編號對應 macOS 日文語音清單的順序
SYNTH_GLOSSARY = ["Lumio", "Redis", "Kubernetes", "GitHub Actions", "綾瀬", "小田切"]
SYNTH_KEYWORDS = SYNTH_GLOSSARY + ["Slack", "ロールバック", "レイテンシ", "ステージング"]
SYNTH_SCRIPT = [
    ("A", "Kyoko", "綾瀬です。それでは定例ミーティングを始めます。今日はLumioの次のリリースについて確認したいと思います。"),
    ("B", "Eddy", "はい、小田切です。先週の金曜日にステージング環境へデプロイしました。今のところ大きな問題は出ていません。"),
    ("A", "Kyoko", "ありがとうございます。レイテンシはどうですか。前回はAPIのレスポンスが遅いという話がありましたよね。"),
    ("B", "Eddy", "Redisのキャッシュを入れたので、平均で二百ミリ秒くらいまで下がりました。ピーク時でも五百ミリ秒以内に収まっています。"),
    ("C", "Flo", "森です。テストのほうは、自動テストを全部GitHub Actionsで回しています。ただ、決済画面のテストが一件だけ不安定です。"),
    ("A", "Kyoko", "その一件は、リリースまでに直せそうですか。"),
    ("C", "Flo", "原因はタイムアウトの設定だと思うので、今週中には対応できると思います。"),
    ("B", "Eddy", "あと、Kubernetesのノードを一台増やしたいです。来月のキャンペーンでアクセスが三倍くらいになる見込みなので。"),
    ("A", "Kyoko", "費用はどのくらい増えますか。"),
    ("B", "Eddy", "月に八万円くらいです。キャンペーンが終わったら元に戻せます。"),
    ("A", "Kyoko", "わかりました。それは私のほうで部長に相談します。"),
    ("C", "Flo", "もう一つ、リリース当日のロールバック手順をまだ確認していません。"),
    ("A", "Kyoko", "それは大事ですね。小田切さん、手順書を水曜日までに作ってもらえますか。"),
    ("B", "Eddy", "はい、水曜日までに作って、Slackのチャンネルに共有します。"),
    ("A", "Kyoko", "森さんは、その手順書でロールバックのリハーサルをお願いします。"),
    ("C", "Flo", "了解です。木曜日の午後にやります。"),
    ("A", "Kyoko", "では、リリース日は予定どおり十一月五日でいきましょう。何かほかにありますか。"),
    ("B", "Eddy", "特にありません。"),
    ("C", "Flo", "大丈夫です。"),
    ("A", "Kyoko", "それでは、今日はこれで終わります。お疲れさまでした。"),
]


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
    """pieces：[(說話者, 音訊, 文字)]，接成一個檔案，回傳音訊和標準答案的段落"""
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


def to_wav(source, target, start=None, end=None):
    cmd = ["ffmpeg", "-v", "error", "-y"]
    if start is not None:
        cmd += ["-ss", str(start), "-to", str(end)]
    cmd += ["-i", str(source), "-vn", "-ac", "1", "-ar", str(RATE), "-c:a", "pcm_s16le", str(target)]
    subprocess.run(cmd, check=True)


def fleurs():
    rows = {}
    with urllib.request.urlopen(FLEURS + "test.tsv") as r:
        for line in r.read().decode().splitlines():
            cols = line.split("\t")
            rows[cols[1]] = (cols[0], cols[2])
    pieces, seen = [], set()
    with urllib.request.urlopen(FLEURS + "audio/test.tar.gz") as r, tempfile.TemporaryDirectory() as tmp:
        with tarfile.open(fileobj=r, mode="r|gz") as tar:
            for member in tar:
                name = Path(member.name).name
                if name not in rows or rows[name][0] in seen:
                    continue
                seen.add(rows[name][0])
                path = Path(tmp) / name
                path.write_bytes(tar.extractfile(member).read())
                to_wav(path, path.with_suffix(".16k.wav"))  # FLEURS 是 32 位元浮點數
                pieces.append((None, read_wav(path.with_suffix(".16k.wav")), rows[name][1]))
                if len(pieces) == FLEURS_COUNT:
                    break
    audio, segments = concat(pieces, 0.8)
    write_wav(JA / "fleurs.wav", audio)
    (JA / "fleurs.ref.json").write_text(json.dumps({"lang": "ja", "segments": segments}, ensure_ascii=False, indent=1))
    return {"name": "ja-fleurs", "audio": str(JA / "fleurs.wav"), "ref": str(JA / "fleurs.ref.json"), "language": "ja",
            "note": f"FLEURS 日文測試集前 {FLEURS_COUNT} 句（朗讀維基百科，CC BY 4.0），{len(audio) / RATE / 60:.0f} 分鐘"}


def japanese_voices():
    """macOS 的日文語音，名稱跟著系統語言（中文介面是「Eddy (日文（日本）)」），要用清單上的完整名稱"""
    out = subprocess.run(["say", "-v", "?"], capture_output=True, text=True, check=True).stdout
    voices = {}
    for line in out.splitlines():
        match = re.match(r"(.+?)\s{2,}ja_JP\s", line)
        if match:
            name = match.group(1).strip()
            voices[name.split(" (")[0]] = name
    return voices


def synth():
    voices = japanese_voices()
    pieces = []
    with tempfile.TemporaryDirectory() as tmp:
        for i, (speaker, voice, text) in enumerate(SYNTH_SCRIPT):
            path = Path(tmp) / f"{i}.wav"
            subprocess.run(["say", "-v", voices[voice], "-o", str(path), "--file-format=WAVE",
                            f"--data-format=LEI16@{RATE}", text], check=True)
            pieces.append((speaker, read_wav(path), text))
    audio, segments = concat(pieces, 0.6)
    write_wav(JA / "synth.wav", audio)
    (JA / "synth.ref.json").write_text(json.dumps({"lang": "ja", "segments": segments}, ensure_ascii=False, indent=1))
    return {"name": "ja-synth", "audio": str(JA / "synth.wav"), "ref": str(JA / "synth.ref.json"), "language": "ja",
            "glossary": SYNTH_GLOSSARY, "keywords": SYNTH_KEYWORDS,
            "note": f"合成的日文開發週會（{len(audio) / RATE:.0f} 秒、3 人，夾片假名和英文產品名）"}


def clean_speech(text):
    text = re.sub(r"^○\S+　", "", text)  # 「○三木委員　」這類開頭
    text = re.sub(r"〔[^〕]*〕", "", text)  # 〔拍手〕等註記
    return re.sub(r"[\r\n　]+", "", text)


def diet():
    minutes = JA / "diet-minutes.json"
    if not minutes.exists():
        with urllib.request.urlopen(DIET_MINUTES) as r:
            minutes.write_bytes(r.read())
    speeches = json.loads(minutes.read_text())["meetingRecord"][0]["speechRecord"]
    datasets = []
    for name, note, start, end, first, last in DIET_BLOCKS:
        audio = JA / f"{name}.wav"
        if not audio.exists():
            to_wav(DIET_VIDEO, audio, start, end)
        # 沒有每句話的時間，整段當成一個段落；不算說話者準確度
        text = "".join(clean_speech(s["speech"]) for s in speeches[first:last])
        segments = [{"start": 0, "end": end - start, "text": text}]
        halfwidth = unicodedata.normalize("NFKC", text).upper()  # 會議錄的英文是全形（ＡＩ）
        ref = JA / f"{name}.ref.json"
        ref.write_text(json.dumps({"lang": "ja", "segments": segments}, ensure_ascii=False, indent=1))
        datasets.append({"name": name, "audio": str(audio), "ref": str(ref), "language": "ja",
                         "glossary": DIET_GLOSSARY, "keywords": [k for k in DIET_KEYWORDS if k.upper() in halfwidth],
                         "note": f"眾議院內閣委員會 2025-04-16 AI 法案專家聽證：{note}，{(end - start) / 60:.0f} 分鐘"})
    return datasets


def main():
    JA.mkdir(parents=True, exist_ok=True)
    new = [synth(), *diet(), fleurs()]
    path = EVAL / "datasets.json"
    datasets = [d for d in json.loads(path.read_text()) if d["name"] not in {n["name"] for n in new}]
    path.write_text(json.dumps(datasets + new, ensure_ascii=False, indent=4))
    for d in new:
        print(d["name"], "：", d["note"])


if __name__ == "__main__":
    main()
