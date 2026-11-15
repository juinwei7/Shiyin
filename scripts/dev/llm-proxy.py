"""記錄拾音送給 OpenAI 相容 API 的請求和回應，照樣轉送（本機整理模型的評估，design §55、R79）。只聽 127.0.0.1。
用法：python3 -I scripts/dev/llm-proxy.py <監聽埠> <上游網址，例如 llama-server 的 http://127.0.0.1:18080> <記錄資料夾>
DEBUG 版：shiyinctl llmprovider openaiCompatible <模型>、shiyinctl llmurl openaiCompatible http://127.0.0.1:<監聽埠>/v1
記錄裡有逐字稿，用真的會議評估完要刪掉"""
import http.server, json, os, sys, time, urllib.request
PORT, UPSTREAM, LOG = int(sys.argv[1]), sys.argv[2].rstrip('/'), sys.argv[3]
os.makedirs(LOG, exist_ok=True)
class H(http.server.BaseHTTPRequestHandler):
    def _forward(self, method):
        body = self.rfile.read(int(self.headers.get('Content-Length') or 0)) if method == 'POST' else None
        req = urllib.request.Request(UPSTREAM + self.path, data=body, method=method,
                                     headers={'Content-Type': 'application/json'})
        started = time.time()
        try:
            with urllib.request.urlopen(req, timeout=3600) as r:
                status, data = r.status, r.read()
        except urllib.error.HTTPError as e:
            status, data = e.code, e.read()
        if method == 'POST':
            stamp = time.strftime('%H%M%S') + f'-{int(started*1000)%1000:03d}'
            open(os.path.join(LOG, stamp + '-request.json'), 'wb').write(body)
            open(os.path.join(LOG, stamp + '-response.json'), 'wb').write(data)
            open(os.path.join(LOG, 'index.tsv'), 'a').write(f'{stamp}\t{self.path}\t{status}\t{time.time()-started:.1f}\n')
        self.send_response(status); self.send_header('Content-Type', 'application/json'); self.send_header('Content-Length', str(len(data))); self.end_headers(); self.wfile.write(data)
    def do_POST(self): self._forward('POST')
    def do_GET(self): self._forward('GET')
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(('127.0.0.1', PORT), H).serve_forever()
