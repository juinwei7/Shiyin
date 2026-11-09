import Foundation
import os

/// 本地 MCP 伺服器（design §44.1、D147）：`Shiyin --mcp`，stdio 一行一個 JSON-RPC 2.0 訊息。
/// 只做 initialize、ping、tools/list、tools/call；stdout 只寫協定訊息，log 不寫參數和會議內容（N7）
enum MCPServer {
    static let protocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
    static let fallbackProtocolVersion = "2025-06-18"
    private static let mlog = Logger(subsystem: "com.weiwei.shiyin", category: "mcp")

    static func run() -> Never {
        // 客戶端先關掉 stdout 的管線時，寫入會收到 SIGPIPE；忽略之後寫入失敗，讀到 stdin 結束就離開
        signal(SIGPIPE, SIG_IGN)
        mlog.info("mcp server started")
        while let line = readLine(strippingNewline: true) {
            if let reply = handle(line) { send(reply) }
        }
        mlog.info("mcp server stopped")
        exit(0)
    }

    /// 處理一行，回傳要寫出的回應；通知、客戶端的回應不用回
    static func handle(_ line: String) -> [String: Any]? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        guard let object = try? JSONSerialization.jsonObject(with: Data(trimmed.utf8)) else {
            return failure(NSNull(), -32700, "Parse error")
        }
        guard let message = object as? [String: Any] else { return failure(NSNull(), -32600, "Invalid request") }
        guard let method = message["method"] as? String else { return nil }
        guard let id = message["id"], !(id is NSNull) else { return nil }
        let params = message["params"] as? [String: Any] ?? [:]
        switch method {
        case "initialize":
            let asked = params["protocolVersion"] as? String ?? ""
            return success(id, [
                "protocolVersion": protocolVersions.contains(asked) ? asked : fallbackProtocolVersion,
                "capabilities": ["tools": [String: Any]()],
                "serverInfo": ["name": "shiyin", "title": "Shiyin", "version": version],
                "instructions": MCPTools.instructions,
            ])
        case "ping":
            return success(id, [:])
        case "tools/list":
            return success(id, ["tools": MCPTools.definitions])
        case "tools/call":
            let name = params["name"] as? String ?? ""
            guard MCPTools.names.contains(name) else { return failure(id, -32602, "Unknown tool: \(name)") }
            let started = Date()
            let result = MCPTools.run(name, params["arguments"] as? [String: Any] ?? [:])
            let ms = Int(Date().timeIntervalSince(started) * 1000)
            mlog.info("tool \(name, privacy: .public): \(result.isError ? "error" : "ok", privacy: .public), \(result.text.utf16.count) chars, \(ms) ms")
            return success(id, ["content": [["type": "text", "text": result.text]], "isError": result.isError])
        default:
            return failure(id, -32601, "Method not found: \(method)")
        }
    }

    private static var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"
    }

    private static func success(_ id: Any, _ result: [String: Any]) -> [String: Any] {
        ["jsonrpc": "2.0", "id": id, "result": result]
    }

    private static func failure(_ id: Any, _ code: Int, _ message: String) -> [String: Any] {
        ["jsonrpc": "2.0", "id": id, "error": ["code": code, "message": message]]
    }

    private static func send(_ reply: [String: Any]) {
        guard var data = try? JSONSerialization.data(withJSONObject: reply, options: [.withoutEscapingSlashes]) else { return }
        data.append(0x0A)
        try? FileHandle.standardOutput.write(contentsOf: data)
    }
}
