#!/usr/bin/env python3
"""匿名使用統計的假後台（design §30.7）：把收到的每個請求（路徑、標頭、內容）一行一筆寫進紀錄檔。

用法：python3 scripts/dev/fake-usage-server.py <埠> <紀錄檔>
回應碼預設 204；在紀錄檔旁邊放 <紀錄檔>.code（內容是數字，例如 500、400）就改回那個碼，測錯誤處理用。
DEBUG 版用 shiyinctl usageurl http://127.0.0.1:<埠>/v1/usage 指過來。
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

port = int(sys.argv[1])
log_path = Path(sys.argv[2])
code_path = Path(str(log_path) + ".code")


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        code = int(code_path.read_text().strip()) if code_path.exists() else 204
        try:
            payload = json.loads(body)
        except ValueError:
            payload = body.decode("utf-8", "replace")
        with log_path.open("a", encoding="utf-8") as f:
            f.write(json.dumps({"path": self.path, "headers": dict(self.headers), "body": payload, "replied": code},
                               ensure_ascii=False) + "\n")
        self.send_response(code)
        self.end_headers()

    def log_message(self, *args):
        pass


HTTPServer(("127.0.0.1", port), Handler).serve_forever()
