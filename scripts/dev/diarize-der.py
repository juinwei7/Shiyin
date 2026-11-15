"""說話者辨識的同一套計分（design §54、R78）。參考：AMI segments（transcriber_start/end，每個 agent 一位說話者）。
frame 10 ms、collar 0.25 秒（參考段落邊界前後不計分）、重疊照算、說話者用匈牙利法對應。
用法：python3 -I scripts/dev/diarize-der.py <AMI 標註資料夾> <voiceprint-eval 的輸出.json> <RTTM 資料夾>
  - 第一組是拾音（DEBUG 版 shiyinctl voiceprint-eval 的輸出，name 是錄音的檔名）
  - 第二組是 RTTM 資料夾裡的 <name>.nemotron.rttm（FluidAudio CLI：fluidaudiocli nemotron3-diarize <wav> --variant offline --output …）
  - ONLY_CLEAN=1：「每一句標對」只算前後 0.2 秒內沒有別人說話的句子"""
import json, os, re, sys, glob
import numpy as np
from scipy.optimize import linear_sum_assignment

AMI, SHIYIN, NEMO = sys.argv[1], sys.argv[2], sys.argv[3]
STEP, COLLAR = 0.01, 0.25

def reference(meeting):
    out = {}
    for path in glob.glob(os.path.join(AMI, 'segments', f'{meeting}.*.segments.xml')):
        agent = path.split('.')[-3]
        xml = open(path, encoding='iso-8859-1').read()
        out[agent] = [(float(a), float(b)) for a, b in re.findall(r'transcriber_start="([\d.]+)" transcriber_end="([\d.]+)"', xml)]
    return out

def frames(spans, n):
    m = np.zeros(n, bool)
    for a, b in spans:
        m[int(round(a / STEP)):max(int(round(a / STEP)), int(round(b / STEP)))] = True
    return m

def score(ref, hyp, duration):
    n = int(duration / STEP) + 1
    R = {k: frames(v, n) for k, v in ref.items()}
    H = {k: frames(v, n) for k, v in hyp.items() if v}
    keep = np.ones(n, bool)
    for spans in ref.values():
        for a, b in spans:
            for t in (a, b):
                keep[max(0, int((t - COLLAR) / STEP)):int((t + COLLAR) / STEP) + 1] = False
    rk, hk = list(R), list(H)
    cost = np.zeros((len(rk), len(hk)))
    for i, r in enumerate(rk):
        for j, h in enumerate(hk):
            cost[i, j] = -np.sum(R[r] & H[h] & keep)
    mapping = {}
    if rk and hk:
        rows, cols = linear_sum_assignment(cost)
        mapping = {rk[i]: hk[j] for i, j in zip(rows, cols)}
    nref = sum(R[r].astype(int) for r in rk) * keep
    nhyp = (sum(H[h].astype(int) for h in hk) if hk else np.zeros(n, int)) * keep
    correct = sum((R[r] & H[mapping[r]]).astype(int) for r in mapping) * keep if mapping else np.zeros(n, int)
    total = nref.sum()
    miss = np.maximum(0, nref - nhyp).sum()
    fa = np.maximum(0, nhyp - nref).sum()
    conf = (np.minimum(nref, nhyp) - correct).sum()
    return dict(der=100 * (miss + fa + conf) / total, miss=100 * miss / total, fa=100 * fa / total, conf=100 * conf / total,
                spk=len(hk), ref=len(rk))

def rttm(path):
    out = {}
    for line in open(path):
        p = line.split()
        if p and p[0] == 'SPEAKER':
            out.setdefault(p[7], []).append((float(p[3]), float(p[3]) + float(p[4])))
    return out

shiyin = {r['name']: r for r in json.load(open(SHIYIN))['results']}
rows = []
for name, r in sorted(shiyin.items()):
    meeting = name.split('.')[0]
    ref = reference(meeting)
    if 'speakers' not in r: continue
    dur = r['seconds']
    a = score(ref, {str(s['number']): [tuple(t) for t in s['turns']] for s in r['speakers']}, dur)
    b = score(ref, rttm(os.path.join(NEMO, name + '.nemotron.rttm')), dur)
    rows.append((name, a, b))
print(f"{'錄音':26} | 拾音 DER 漏判 誤判 搞混 人數 | Nemotron-3 DER 漏判 誤判 搞混 人數")
for name, a, b in rows:
    f = lambda d: f"{d['der']:5.1f} {d['miss']:5.1f} {d['fa']:4.1f} {d['conf']:5.1f} {d['spk']}/{d['ref']}"
    print(f"{name:26} | {f(a)} | {f(b)}")
for label, sel in [('近距（Mix-Headset）16 場', lambda n: 'Mix-Headset' in n), ('遠距（Array1-01）', lambda n: 'Array1' in n)]:
    sub = [(a, b) for n, a, b in rows if sel(n)]
    if not sub: continue
    avg = lambda k, i: np.mean([x[i][k] for x in sub])
    cnt = lambda i: sum(x[i]['spk'] == x[i]['ref'] for x in sub)
    print(f"{label}: 拾音 DER {avg('der',0):.1f}（漏 {avg('miss',0):.1f} 誤 {avg('fa',0):.1f} 混 {avg('conf',0):.1f}）人數對 {cnt(0)}/{len(sub)}"
          f" | Nemotron DER {avg('der',1):.1f}（漏 {avg('miss',1):.1f} 誤 {avg('fa',1):.1f} 混 {avg('conf',1):.1f}）人數對 {cnt(1)}/{len(sub)}")

# 每一句（AMI 的 segment）標對的比例：取跟這句重疊最多的說話者，沒有重疊就取最近的一段（同拾音的 Diarizer.assign），
# 用 DER 的對應判斷對不對
def attribution(ref, hyp):
    rk = list(ref); hk = [k for k, v in hyp.items() if v]
    if not hk: return (0, 0, 0, 0)
    cost = np.zeros((len(rk), len(hk)))
    for i, r in enumerate(rk):
        for j, h in enumerate(hk):
            cost[i, j] = -sum(max(0, min(b, d) - max(a, c)) for a, b in ref[r] for c, d in hyp[h])
    rows, cols = linear_sum_assignment(cost)
    mapping = {rk[i]: hk[j] for i, j in zip(rows, cols)}
    turns = sorted((a, b, h) for h in hk for a, b in hyp[h])
    ok = n = ok_short = n_short = 0
    for r in rk:
        for a, b in ref[r]:
            if b - a < 0.2: continue
            over = {}
            for c, d, h in turns:
                if d > a and c < b: over[h] = over.get(h, 0) + min(b, d) - max(a, c)
            if over: best = max(over, key=over.get)
            else: best = min(turns, key=lambda t: min(abs(t[0] - b), abs(t[1] - a)))[2]
            right = mapping.get(r) == best
            # ONLY_CLEAN：只算前後 0.2 秒內沒有別人在說話的句子（design §6.3.2 的「沒重疊」）
            if ONLY_CLEAN and any(c < b + 0.2 and d > a - 0.2 for o in rk if o != r for c, d in ref[o]): continue
            n += 1; ok += right
            if b - a < 2: n_short += 1; ok_short += right
    return ok, n, ok_short, n_short

ONLY_CLEAN = os.environ.get('ONLY_CLEAN') == '1'
print('\n每一句標對的比例（全部｜2 秒內的短句）' + ('，只算沒重疊的句子' if ONLY_CLEAN else '，含插話、附和'))
tot = {0: [0, 0, 0, 0], 1: [0, 0, 0, 0]}
for name, r in sorted(shiyin.items()):
    if 'speakers' not in r: continue
    ref = reference(name.split('.')[0])
    res = [attribution(ref, {str(s['number']): [tuple(t) for t in s['turns']] for s in r['speakers']}),
           attribution(ref, rttm(os.path.join(NEMO, name + '.nemotron.rttm')))]
    for i in (0, 1): tot[i] = [x + y for x, y in zip(tot[i], res[i])]
    f = lambda x: f"{100*x[0]/max(1,x[1]):5.1f}%｜{100*x[2]/max(1,x[3]):5.1f}%"
    print(f"{name:26} 拾音 {f(res[0])}   Nemotron {f(res[1])}")
f = lambda x: f"{100*x[0]/max(1,x[1]):5.1f}%（{x[1]} 句）｜短句 {100*x[2]/max(1,x[3]):5.1f}%（{x[3]} 句）"
print('合計：拾音', f(tot[0])); print('合計：Nemotron', f(tot[1]))
