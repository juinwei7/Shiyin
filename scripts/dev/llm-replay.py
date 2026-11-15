"""把 llm-proxy.py 記下來的整理請求重送給 OpenAI 相容 API，換參數比較（design §55、R79）。
用法：python3 -I scripts/dev/llm-replay.py <記錄資料夾> <輸出資料夾> <標籤> [temperature|-] [重複次數] [加進請求的 JSON]
- 記錄資料夾的 index.tsv 每一列是一次請求；每個請求重送「重複次數」次，輸出 <標籤>-<第幾個請求>-<第幾次>.json
- 上游：環境變數 LLM_URL（預設 http://127.0.0.1:18080/v1/chat/completions）
- 印出每次的秒數、輸出的 token 數、各段的條數（摘要、決議、待辦、未決問題、議題、要點、待確認）"""
import json, os, sys, time, urllib.request
LOG, OUT, LABEL = sys.argv[1], sys.argv[2], sys.argv[3]
TEMP = None if len(sys.argv) < 5 or sys.argv[4] == '-' else float(sys.argv[4])
REPEAT = int(sys.argv[5]) if len(sys.argv) > 5 else 1
EXTRA = json.loads(sys.argv[6]) if len(sys.argv) > 6 else {}
URL = os.environ.get('LLM_URL', 'http://127.0.0.1:18080/v1/chat/completions')
os.makedirs(OUT, exist_ok=True)
stamps = [line.split('\t')[0] for line in open(os.path.join(LOG, 'index.tsv')).read().splitlines()]
for index, stamp in enumerate(stamps):
    req = json.load(open(os.path.join(LOG, stamp + '-request.json')))
    if TEMP is not None: req['temperature'] = TEMP
    req.update(EXTRA)
    for n in range(REPEAT):
        started = time.time()
        r = urllib.request.urlopen(urllib.request.Request(URL, data=json.dumps(req).encode(), headers={'Content-Type': 'application/json'}), timeout=3600)
        resp = json.loads(r.read())
        elapsed = time.time() - started
        msg = resp['choices'][0]['message']
        try:
            notes, ok = json.loads(msg['content']), True
        except Exception:
            notes, ok = {}, False
        counts = {k: (len(v) if isinstance(v, list) else 1) for k, v in notes.items()}
        counts['points'] = sum(len(t.get('points', [])) for t in notes.get('topics', []))
        json.dump({'request': stamp, 'label': LABEL, 'run': n, 'elapsed': elapsed, 'usage': resp.get('usage'), 'ok': ok, 'notes': notes},
                  open(os.path.join(OUT, f'{LABEL}-{index}-{n}.json'), 'w'), ensure_ascii=False)
        print(LABEL, stamp, n, f"{elapsed:.0f}s", resp.get('usage', {}).get('completion_tokens'), 'ok' if ok else 'BAD JSON',
              {k: counts.get(k, 0) for k in ['summary', 'decisions', 'actionItems', 'openQuestions', 'topics', 'points', 'reviewItems']}, flush=True)
