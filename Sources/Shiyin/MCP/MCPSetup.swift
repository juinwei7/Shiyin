import AppKit
import Foundation

/// 把本地 MCP 加到 AI 工具（design §44.3）：啟動檔、Claude Code、Codex、Claude Desktop、複製設定
enum MCPSetup {
    static let serverName = "shiyin"

    struct Outcome: Equatable {
        var ok: Bool
        var message: String
    }

    // MARK: 啟動檔（D148）

    /// AI 工具登記的都是這個檔：拾音每次啟動時改寫成目前的路徑，搬家、更新後不用重新設定
    static var launcherURL: URL { Paths.home.appendingPathComponent("bin/shiyin-mcp") }

    static func launcherScript(executable: String, home: String?) -> String {
        var lines = ["#!/bin/sh", "# 拾音的 MCP 伺服器（design §44.3）。拾音每次啟動時重寫，指到目前這份拾音；不要手動改"]  // l10n-skip
        if let home { lines.append("export SHIYIN_HOME=\(shellQuote(home))") }
        lines.append("exec \(shellQuote(executable)) --mcp \"$@\"")
        return lines.joined(separator: "\n") + "\n"
    }

    enum SetupError: LocalizedError {
        case translocated
        var errorDescription: String? {
            switch self {
            case .translocated: return String(localized: "先把拾音拖進「應用程式」資料夾再打開，AI 工具才找得到它。")
            }
        }
    }

    /// 沒拖進「應用程式」就從 DMG、下載資料夾打開時，macOS 把拾音搬到暫時的路徑（App Translocation），啟動檔不能指過去（design §44.3）
    static var isTranslocated: Bool {
        #if DEBUG
        if ProcessInfo.processInfo.environment["SHIYIN_FAKE_TRANSLOCATED"] == "1" { return true }
        #endif
        return Bundle.main.bundlePath.contains("/AppTranslocation/")
    }

    /// 寫啟動檔，內容一樣就不寫。DEBUG 版沒有指定 SHIYIN_HOME 時不寫：不然使用者的 AI 工具會被指到 DEBUG 版
    @discardableResult
    static func installLauncher() throws -> URL {
        let customHome = ProcessInfo.processInfo.environment["SHIYIN_HOME"].flatMap { $0.isEmpty ? nil : Paths.home.path }
        #if DEBUG
        guard customHome != nil else { throw CocoaError(.featureUnsupported) }
        #endif
        guard !isTranslocated else { throw SetupError.translocated }
        guard let executable = Bundle.main.executableURL?.path else { throw CocoaError(.fileNoSuchFile) }
        removeQuarantine(in: Bundle.main.bundleURL)
        let url = launcherURL
        let script = launcherScript(executable: executable, home: customHome)
        if (try? String(contentsOf: url, encoding: .utf8)) != script {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(script.utf8).write(to: url, options: .atomic)
        }
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    /// 拿掉 bundle 的隔離屬性（D150）：從網路下載的拾音每個檔案都有 com.apple.quarantine，AI 工具直接執行裡面的執行檔時
    /// Gatekeeper 會跳出提示、程序卡住。拾音跑得起來就代表使用者放行過。主執行檔沒有隔離屬性就不走整個 bundle（每次啟動都會叫）
    @discardableResult
    static func removeQuarantine(in bundle: URL) -> Int {
        let name = "com.apple.quarantine"
        let executable = Bundle(url: bundle)?.executableURL ?? bundle
        guard getxattr(executable.path, name, nil, 0, 0, XATTR_NOFOLLOW) >= 0 else { return 0 }
        var urls = [bundle]
        if let items = FileManager.default.enumerator(at: bundle, includingPropertiesForKeys: nil) {
            for case let url as URL in items { urls.append(url) }
        }
        var removed = 0, failed = 0
        for url in urls where getxattr(url.path, name, nil, 0, 0, XATTR_NOFOLLOW) >= 0 {
            if removexattr(url.path, name, XATTR_NOFOLLOW) == 0 { removed += 1 } else { failed += 1 }
        }
        log.info("mcp: removed quarantine from \(removed) file(s), \(failed) failed")
        return removed
    }

    static func shellQuote(_ text: String) -> String { "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    // MARK: 複製

    static var claudeCodeCommand: String { "claude mcp add --scope user \(serverName) -- \(shellQuote(launcherURL.path))" }

    static var configJSON: String {
        let object: [String: Any] = ["mcpServers": [serverName: ["command": launcherURL.path]]]
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    enum CopyItem { case claudeCodeCommand, configJSON }

    /// 設定頁「複製」選單的兩項：先確定啟動檔在，再放進剪貼簿
    static func copy(_ item: CopyItem) {
        _ = try? installLauncher()
        let text = item == .claudeCodeCommand ? claudeCodeCommand : configJSON
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }

    // MARK: Claude Code、Codex

    static func addToClaudeCode() async -> Outcome {
        guard let tool = Paths.locateTool("claude") else { return Outcome(ok: false, message: String(localized: "找不到 claude")) }
        return await register(tool, remove: ["mcp", "remove", "--scope", "user", serverName],
                              add: { ["mcp", "add", "--scope", "user", serverName, "--", $0] },
                              done: String(localized: "已加到 Claude Code，開新的對話就能用。"),
                              failed: { String(localized: "沒有加到 Claude Code：\($0)") })
    }

    static func addToCodex() async -> Outcome {
        guard let tool = Paths.locateTool("codex") else { return Outcome(ok: false, message: String(localized: "找不到 codex")) }
        return await register(tool, remove: ["mcp", "remove", serverName],
                              add: { ["mcp", "add", serverName, "--", $0] },
                              done: String(localized: "已加到 Codex，開新的對話就能用。"),
                              failed: { String(localized: "沒有加到 Codex：\($0)") })
    }

    /// 先移除同名的（不在也沒關係）再加，按兩次不會重複
    private static func register(_ tool: Paths.ToolLocation, remove: [String], add: (String) -> [String],
                                 done: String, failed: (String) -> String) async -> Outcome {
        do {
            let launcher = try installLauncher()
            _ = try? await Subprocess(tool.url, remove, environment: tool.environment).run(timeout: 20)
            let result = try await Subprocess(tool.url, add(launcher.path), environment: tool.environment).run(timeout: 20)
            guard result.succeeded else {
                let stdout = String(decoding: result.stdout, as: UTF8.self)
                return Outcome(ok: false, message: failed(firstLine(result.stderr.isEmpty ? stdout : result.stderr)))
            }
            return Outcome(ok: true, message: done)
        } catch {
            return Outcome(ok: false, message: failed(error.localizedDescription))
        }
    }

    private static func firstLine(_ text: String) -> String {
        let line = text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.first { !$0.isEmpty } ?? ""
        return line.isEmpty ? String(localized: "沒有錯誤訊息") : String(line.prefix(200))
    }

    // MARK: Claude Desktop

    static var claudeDesktopConfigURL: URL {
        #if DEBUG
        if let path = ProcessInfo.processInfo.environment["SHIYIN_CLAUDE_DESKTOP_CONFIG"], !path.isEmpty { return URL(fileURLWithPath: path) }
        #endif
        return FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Claude/claude_desktop_config.json")
    }

    static var claudeDesktopInstalled: Bool {
        #if DEBUG
        if ProcessInfo.processInfo.environment["SHIYIN_CLAUDE_DESKTOP_CONFIG"] != nil { return true }
        #endif
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return ["/Applications/Claude.app", home + "/Applications/Claude.app"].contains { FileManager.default.fileExists(atPath: $0) }
    }

    /// 只改 mcpServers.shiyin，其他設定保留（鍵的順序會變）；原檔先複製成 .bak。不是 JSON 物件就不動
    static func addToClaudeDesktop() -> Outcome {
        let url = claudeDesktopConfigURL
        let fm = FileManager.default
        do {
            let launcher = try installLauncher()
            var root: [String: Any] = [:]
            if fm.fileExists(atPath: url.path) {
                guard let object = try? JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any],
                      object["mcpServers"] == nil || object["mcpServers"] is [String: Any] else {
                    return Outcome(ok: false, message: String(localized: "Claude Desktop 的設定檔不是拾音看得懂的格式，沒有改動：\(url.path)"))
                }
                root = object
                let backup = url.appendingPathExtension("bak")
                try? fm.removeItem(at: backup)
                try fm.copyItem(at: url, to: backup)
            }
            var servers = root["mcpServers"] as? [String: Any] ?? [:]
            servers[serverName] = ["command": launcher.path]
            root["mcpServers"] = servers
            try fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
            try data.write(to: url, options: .atomic)
            return Outcome(ok: true, message: String(localized: "已加到 Claude Desktop，重新開啟 Claude Desktop 後就能用。"))
        } catch {
            return Outcome(ok: false, message: String(localized: "沒有加到 Claude Desktop：\(error.localizedDescription)"))
        }
    }
}

/// 會議連結（design §44.5）：shiyin://open?meeting=<id>&t=<秒數>
enum MeetingLink {
    static func parse(_ url: URL) -> (meetingId: Int64, seconds: Double?)? {
        guard url.scheme == "shiyin", url.host == "open",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let id = items.first(where: { $0.name == "meeting" })?.value.flatMap({ Int64($0) }) else { return nil }
        return (id, items.first { $0.name == "t" }?.value.flatMap { Double($0) }.map { max(0, $0) })
    }
}
