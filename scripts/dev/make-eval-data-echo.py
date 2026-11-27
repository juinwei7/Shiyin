#!/usr/bin/env python3
"""回音消除的評估資料（R123、design §97）：用 FLEURS 的真人中文朗讀拼成「用喇叭開線上會議」的雙聲道錄音。

  scripts/dev/make-eval-data-echo.py        # 要先有 eval32/zh/fleurs.wav（make-eval-data-langs.py zh）

輸出在 ~/Library/Caches/ShiyinSpike/echo/：
  <名稱>.wav        聲道 0 麥克風、聲道 1 電腦聲音（跟拾音錄的格式一樣：16 kHz 雙聲道）
  <名稱>.ref.json   標準答案（asr-score 用）
  <名稱>.local.wav  麥克風聲道裡「本地的人＋環境噪音」那一份（沒有回音），算回音消除有沒有傷到本地的人聲
  <名稱>.mask.json  每段誰在說話（local／remote），算回音少了多少

FLEURS 的句子輪流分給本地（麥克風）和遠端（電腦聲音），約四分之一的句子跟上一句重疊（同時說話）。
遠端的聲音經過喇叭失真、高通（筆電喇叭沒有低音）、合成的房間響應、時鐘漂移，再錯開一段時間加進麥克風聲道：
拾音的兩個聲道是各自收到多少就寫多少，開始時間不同、一邊落後超過 0.5 秒補靜音，所以回音跟電腦聲音的時間差可能是負的、中途會跳。
"""
import json
import wave
from pathlib import Path

import numpy as np
from scipy.signal import butter, lfilter

RATE = 16_000
SRC = Path.home() / "Library/Caches/ShiyinSpike/eval32/zh"
OUT = Path.home() / "Library/Caches/ShiyinSpike/echo"


def read_wav(path):
    with wave.open(str(path)) as w:
        data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
        return data.reshape(-1, w.getnchannels()).astype(np.float32).T / 32768


def write_wav(path, channels):
    data = np.stack(channels, axis=1)
    data = np.clip(np.round(data * 32767), -32768, 32767).astype(np.int16)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(len(channels))
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data.tobytes())


def rms_db(x):
    return 20 * np.log10(np.sqrt(np.mean(x ** 2)) + 1e-12)


def room(rng, rt60, direct_ms, drr_db):
    """合成的房間響應：直達聲、幾個早期反射、指數衰減的殘響"""
    length = int(rt60 * RATE)
    h = np.zeros(length, dtype=np.float32)
    d0 = int(direct_ms * RATE / 1000)
    h[d0] = 1.0
    for _ in range(6):
        h[d0 + rng.integers(int(0.003 * RATE), int(0.025 * RATE))] += rng.uniform(0.2, 0.5) * rng.choice([-1, 1])
    n = np.arange(length - d0 - int(0.005 * RATE))
    tail = rng.standard_normal(len(n)).astype(np.float32) * np.exp(-6.9 * n / (rt60 * RATE))
    tail *= np.sqrt(np.sum(h ** 2) / np.sum(tail ** 2)) * 10 ** (-drr_db / 20)
    h[d0 + int(0.005 * RATE):] += tail
    return h


def timeline(rng, sentences):
    """句子輪流分給本地、遠端；回傳 [(開始取樣點, 'local'|'remote', 音訊, 文字)]"""
    placed, t, prev_end = [], RATE, RATE
    for i, (audio, text) in enumerate(sentences):
        side = "local" if i % 2 == 0 else "remote"
        if placed and rng.random() < 0.25:
            start = max(placed[-1][0] + RATE, prev_end - int(rng.uniform(1.0, 2.5) * RATE))  # 插話
        else:
            start = prev_end + int(rng.uniform(0.4, 1.2) * RATE)
        placed.append((start, side, audio, text))
        prev_end = max(prev_end, start + len(audio))
    return placed, prev_end + RATE


def make(name, rng, sentences, echo):
    placed, total = timeline(rng, sentences)
    local = np.zeros(total, dtype=np.float32)
    remote = np.zeros(total, dtype=np.float32)
    near_room = room(rng, 0.25, 1.5, 9)
    for start, side, audio, _ in placed:
        target = local if side == "local" else remote
        target[start:start + len(audio)] += audio
    local = np.convolve(local, near_room)[:total]
    local *= 10 ** ((-28 - rms_db(local[local != 0])) / 20)
    remote *= 10 ** ((-24 - rms_db(remote[remote != 0])) / 20)
    noise = lfilter([1], [1, -0.95], rng.standard_normal(total)).astype(np.float32)
    noise *= 10 ** ((-60 - rms_db(noise)) / 20)
    near = local + noise

    mic = near.copy()
    if echo:
        # 喇叭：失真＋沒有低音；房間：直達聲 1～3 ms
        played = np.tanh(echo["drive"] * remote) / echo["drive"]
        b, a = butter(2, 200 / (RATE / 2), "high")
        played = lfilter(b, a, played).astype(np.float32)
        acoustic = np.convolve(played, room(rng, echo["rt60"], 2, echo["drr"]))[:total]
        acoustic *= 10 ** ((-28 + echo["level_db"] - rms_db(acoustic[np.abs(remote) > 0])) / 20)
        # 時鐘漂移：麥克風的時鐘比喇叭快 ppm
        n = np.arange(total, dtype=np.float64)
        acoustic = np.interp(n * (1 + echo["ppm"] * 1e-6), n, acoustic).astype(np.float32)
        # 聲道錯開：回音在檔案裡比電腦聲音晚 offset（可能是負的），中途跳一次
        jump_at = int(total * echo["jump_at"])
        offsets = [echo["offset_ms"], echo["offset_ms"] + echo["jump_ms"]]
        shifted = np.zeros(total, dtype=np.float32)
        for part, (lo, hi) in enumerate([(0, jump_at), (jump_at, total)]):
            k = int(offsets[part] * RATE / 1000)
            src = np.arange(lo, hi) - k
            ok = (src >= 0) & (src < total)
            shifted[lo:hi][ok] = acoustic[src[ok]]
        mic += shifted
    peak = max(np.abs(mic).max(), np.abs(remote).max())
    if peak > 0.98:
        mic, remote, near = mic * 0.98 / peak, remote * 0.98 / peak, near * 0.98 / peak

    write_wav(OUT / f"{name}.wav", [mic, remote])
    write_wav(OUT / f"{name}.local.wav", [near])
    segments = sorted(({"start": round(s / RATE, 2), "end": round((s + len(a)) / RATE, 2), "text": t} for s, _, a, t in placed),
                      key=lambda x: x["start"])
    (OUT / f"{name}.ref.json").write_text(json.dumps({"lang": "zh", "segments": segments}, ensure_ascii=False, indent=1))
    mask = [{"start": round(s / RATE, 3), "end": round((s + len(a)) / RATE, 3), "side": side} for s, side, a, _ in placed]
    (OUT / f"{name}.mask.json").write_text(json.dumps(mask, indent=1))
    print(f"{name}: {total / RATE:.0f} 秒")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    fleurs = read_wav(SRC / "fleurs.wav")[0]
    ref = json.loads((SRC / "fleurs.ref.json").read_text())["segments"]
    sentences = [(fleurs[int(max(0, s["start"] - 0.1) * RATE):int((s["end"] + 0.1) * RATE)], s["text"]) for s in ref]
    variants = {
        # 筆電喇叭、一般音量：回音跟本地人聲差不多大，回音比電腦聲音晚 90 ms、中途再晚 40 ms
        "zh-echo-laptop": dict(drive=1.5, rt60=0.35, drr=4, level_db=0, ppm=40, offset_ms=90, jump_ms=40, jump_at=0.45),
        # 大聲、殘響長、回音比電腦聲音早（麥克風比電腦聲音晚開始收）、中途跳 70 ms、時鐘漂移較大
        "zh-echo-loud": dict(drive=3.0, rt60=0.5, drr=0, level_db=6, ppm=-60, offset_ms=-120, jump_ms=-70, jump_at=0.6),
        # 戴耳機：沒有回音（回音消除不能把這種錄音弄壞）
        "zh-echo-headphones": None,
    }
    for i, (name, echo) in enumerate(variants.items()):
        make(name, np.random.default_rng(100 + i), sentences, echo)


if __name__ == "__main__":
    main()
