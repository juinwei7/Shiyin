#!/usr/bin/env python3
"""驗證用的假 LLM 伺服器（design §19.5）：記下 app 送出的請求，回固定的成功回應。

用法：
  python3 scripts/dev/fake-llm-server.py <記錄檔.jsonl> [埠，預設 18766] [--codex]
  shiyinctl llmurl anthropic http://127.0.0.1:18766       # Anthropic、OpenAI、Gemini 改送到這裡
  OpenAI 相容 API 的網址直接填 http://127.0.0.1:18766/v1

加 --codex：OpenAI 相容 API（/chat/completions）改由已登入的 codex CLI 回答，用真的模型測。
json_schema 時把 schema 交給 codex 的 --output-schema；退回 JSON 模式時不給 schema，只靠提示詞。

只聽 127.0.0.1。記錄檔每行一個請求：方法、路徑、header（key 只留前 6 個字）、body（解析成 JSON）。
改變行為：POST /__mode，body 是 JSON，例如
  {"reject_json_schema": true}     OpenAI 相容 API 收到 json_schema 時回 400（測退回 JSON 模式）
  {"fence": true}                  OpenAI 相容 API 的內容包在 ```json 圍欄裡
  {"gemini_status": "incomplete"}  Gemini 回傳的狀態
  {"gemini_error_array": true}     Gemini 回 400，錯誤包在陣列裡（跟 Interactions 實際的格式一樣）
  {"bad_key": true}                全部回 key 錯誤
  {}                               改回預設
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

USE_CODEX = "--codex" in sys.argv
ARGS = [a for a in sys.argv[1:] if a != "--codex"]
LOG = ARGS[0]
PORT = int(ARGS[1]) if len(ARGS) > 1 else 18766
MODE = {}
SECRET_HEADERS = {"authorization", "x-api-key", "x-goog-api-key"}

NOTES = {
    "title": "假伺服器的會議紀錄",
    "summary": [{"text": "這是假伺服器回傳的摘要。", "time": "00:05"}],
    "decisions": [{"text": "照原定時程。", "time": "00:10"}],
    "actionItems": [{"owner": "", "task": "確認假伺服器的結果", "due": "", "time": "00:15"}],
    "openQuestions": [],
    "topics": [{"title": "測試", "start": "00:00", "points": [{"text": "假伺服器", "time": "00:05"}]}],
    "custom": [],
    "reviewItems": [],
}
CORRECTIONS = {"corrections": []}


def ask_codex(messages, schema):
    """用 codex exec 回答（跟 app 的 CodexCLI 一樣：系統提示詞接在使用者提示詞前面）"""
    prompt = "\n\n".join(m["content"] for m in messages)
    with tempfile.TemporaryDirectory() as d:
        out = os.path.join(d, "out.txt")
        codex = "/opt/homebrew/bin/codex" if os.path.exists("/opt/homebrew/bin/codex") else shutil.which("codex")
        args = [codex, "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only",
                "--color", "never", "-o", out]
        if schema is not None:
            path = os.path.join(d, "schema.json")
            with open(path, "w") as f:
                json.dump(schema, f)
            args += ["--output-schema", path]
        subprocess.run(args + ["-"], input=prompt.encode(), cwd=d, capture_output=True, timeout=600)
        with open(out, encoding="utf-8") as f:
            return f.read()


def answer_for(text):
    """依 schema 或提示詞判斷要回會議紀錄還是逐字稿校正"""
    return CORRECTIONS if "corrections" in text else NOTES


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, status, obj):
        data = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def record(self, body):
        headers = {}
        for k, v in self.headers.items():
            k = k.lower()
            if k in ("host", "content-length", "connection", "accept-encoding", "accept-language", "user-agent", "accept"):
                continue
            headers[k] = (v[:13] + "…") if k in SECRET_HEADERS else v
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(json.dumps({"method": self.command, "path": self.path, "headers": headers, "body": body},
                               ensure_ascii=False, sort_keys=True) + "\n")

    def do_GET(self):
        self.record(None)
        if MODE.get("bad_key"):
            return self.reply(401, {"error": {"message": "Invalid API Key"}})
        if self.path.startswith("/v1beta/models"):
            text = ["generateContent", "countTokens"]
            return self.reply(200, {"models": [
                {"name": "models/gemini-3.8-flash", "supportedGenerationMethods": text},
                {"name": "models/gemini-4-argon", "supportedGenerationMethods": text},
                {"name": "models/gemini-embedding-001", "supportedGenerationMethods": ["embedContent"]},
                {"name": "models/gemini-3.8-flash-tts", "supportedGenerationMethods": text},
                {"name": "models/gemini-3.8-live", "supportedGenerationMethods": ["bidiGenerateContent"]},
                {"name": "models/imagen-4", "supportedGenerationMethods": ["predict"]}]})
        if self.path.endswith("/models") or "/models?" in self.path:
            return self.reply(200, {"data": [{"id": "fake-model-a"}, {"id": "fake-model-b"}]})
        self.reply(404, {"error": {"message": "not found"}})

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("content-length", 0)))
        try:
            body = json.loads(raw)
        except ValueError:
            body = raw.decode(errors="replace")
        if self.path == "/__mode":
            MODE.clear()
            MODE.update(body or {})
            return self.reply(200, MODE)
        self.record(body)
        text = json.dumps(body, ensure_ascii=False)
        if MODE.get("bad_key"):
            return self.reply(401, {"error": {"message": "Invalid API Key"}})

        if self.path == "/v1/messages":   # Anthropic
            tool = body["tool_choice"]["name"]
            return self.reply(200, {"content": [{"type": "tool_use", "name": tool, "input": answer_for(tool)}],
                                    "stop_reason": "tool_use"})
        if self.path == "/v1/responses":   # OpenAI
            name = body["text"]["format"]["name"]
            out = json.dumps(answer_for(name), ensure_ascii=False)
            return self.reply(200, {"status": "completed", "output": [
                {"type": "message", "content": [{"type": "output_text", "text": out}]}]})
        if self.path == "/v1beta/interactions":   # Gemini
            if MODE.get("gemini_error_array"):
                return self.reply(400, [{"error": {"code": 400, "message": "API key not valid. Please pass a valid API key.",
                                                    "status": "INVALID_ARGUMENT"}}])
            out = json.dumps(answer_for(json.dumps(body["response_format"]["schema"])), ensure_ascii=False)
            return self.reply(200, {"object": "interaction", "status": MODE.get("gemini_status", "completed"), "steps": [
                {"type": "thought", "content": [{"type": "text", "text": "(thinking)"}]},
                {"type": "model_output", "content": [{"type": "text", "text": out}]}]})
        if self.path.endswith("/chat/completions"):   # OpenAI 相容
            fmt = body.get("response_format", {})
            if fmt.get("type") == "json_schema" and MODE.get("reject_json_schema"):
                return self.reply(400, {"error": {"message": "This model does not support response format `json_schema`.",
                                                  "type": "invalid_request_error"}})
            source = json.dumps(fmt.get("json_schema", {}).get("schema", {})) if fmt.get("type") == "json_schema" \
                else body["messages"][0]["content"]
            if USE_CODEX:
                schema = fmt.get("json_schema", {}).get("schema") if fmt.get("type") == "json_schema" else None
                out = ask_codex(body["messages"], schema)
            else:
                out = json.dumps(answer_for(source), ensure_ascii=False)
            if MODE.get("fence"):
                out = "好的，以下是結果：\n```json\n" + out + "\n```"
            return self.reply(200, {"choices": [{"index": 0, "finish_reason": "stop",
                                                 "message": {"role": "assistant", "content": out}}]})
        self.reply(404, {"error": {"message": "not found"}})


print(f"fake LLM server on http://127.0.0.1:{PORT}, log {LOG}", flush=True)
ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
