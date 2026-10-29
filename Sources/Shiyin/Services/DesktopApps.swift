import AppKit
import Foundation

/// Codex、Claude 的桌面版（R55、R56，design §37）。
/// Codex 桌面版（現在叫 ChatGPT）附一份 codex，找不到使用者自己裝的 CLI 時用它；Claude 桌面版只看有沒有裝，用來提示怎麼安裝 Claude Code
enum DesktopApps {
    struct Codex: Equatable {
        /// 桌面版 app 的位置（「打開 ChatGPT 登入」用）
        let app: URL
        /// app 自己的名稱，例如「ChatGPT」
        let name: String
        /// 說明檔的 entrypoint
        let executable: URL
        /// 說明檔的 pathDir（有 rg），接在子程序的 PATH 前面
        let pathDir: URL?
    }

    static let codexBundleID = "com.openai.codex"
    static let claudeBundleID = "com.anthropic.claudefordesktop"

    /// 問 LaunchServices 比看檔案慢，設定頁每次重畫都會找工具：結果留 30 秒（同 Windows）
    private static let cacheSeconds: TimeInterval = 30
    private static let lock = NSLock()
    nonisolated(unsafe) private static var codexCache: (at: Date, value: Codex?)?
    nonisolated(unsafe) private static var claudeCache: (at: Date, value: Bool)?
    nonisolated(unsafe) private static var lastProblem: String?

    /// Codex 桌面版附的 codex；沒有桌面版、說明檔看不懂時是 nil
    static func codex() -> Codex? {
        lock.lock()
        if let cached = codexCache, Date().timeIntervalSince(cached.at) < cacheSeconds {
            lock.unlock()
            return cached.value
        }
        lock.unlock()
        let value = findCodex()
        lock.lock()
        codexCache = (Date(), value)
        lock.unlock()
        return value
    }

    /// 這台 Mac 有沒有 Claude 桌面版（R56.2）
    static var hasClaude: Bool {
        #if DEBUG
        // -debugClaudeDesktop yes|no：假裝有、沒有 Claude 桌面版（design §37.4）
        switch UserDefaults.standard.string(forKey: "debugClaudeDesktop")?.lowercased() {
        case "yes": return true
        case "no": return false
        default: break
        }
        #endif
        lock.lock()
        if let cached = claudeCache, Date().timeIntervalSince(cached.at) < cacheSeconds {
            lock.unlock()
            return cached.value
        }
        lock.unlock()
        let value = !NSWorkspace.shared.urlsForApplications(withBundleIdentifier: claudeBundleID).isEmpty
        lock.lock()
        claudeCache = (Date(), value)
        lock.unlock()
        return value
    }

    /// 「重新檢查」：剛裝好、剛移除時馬上看得到
    static func clearCache() {
        lock.lock()
        codexCache = nil
        claudeCache = nil
        lock.unlock()
    }

    private static func findCodex() -> Codex? {
        var apps = NSWorkspace.shared.urlsForApplications(withBundleIdentifier: codexBundleID)
        #if DEBUG
        // -debugCodexApp <路徑>：不問 LaunchServices，把這個資料夾當成 Codex 桌面版（測說明檔不對的情況，design §37.4）
        if let path = UserDefaults.standard.string(forKey: "debugCodexApp"), !path.isEmpty {
            apps = [URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true)]
        }
        #endif
        var problems: [String] = []
        for app in apps {
            switch readCodex(app: app) {
            case .success(let codex):
                report(nil)
                return codex
            case .failure(let problem):
                problems.append(problem.reason)
            }
        }
        report(problems.isEmpty ? nil : problems.joined(separator: ", "))
        return nil
    }

    /// 說明檔不對只記原因，同一個原因不重複記（每 30 秒會重新找一次）
    private static func report(_ problem: String?) {
        lock.lock()
        let changed = problem != lastProblem
        lastProblem = problem
        lock.unlock()
        if changed, let problem { log.notice("codex desktop app skipped: \(problem, privacy: .public)") }
    }

    struct Problem: Error { let reason: String }

    /// 照桌面版的說明檔 `Contents/Resources/codex-cli/codex-package.json` 找 codex（design §37.1）
    static func readCodex(app: URL) -> Result<Codex, Problem> {
        let root = app.appendingPathComponent("Contents/Resources/codex-cli", isDirectory: true)
        guard let data = try? Data(contentsOf: root.appendingPathComponent("codex-package.json")) else {
            return .failure(Problem(reason: "no codex-package.json"))
        }
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return .failure(Problem(reason: "unreadable codex-package.json"))
        }
        guard (object["layoutVersion"] as? Int) == 1 else { return .failure(Problem(reason: "layoutVersion is not 1")) }
        guard let entry = object["entrypoint"] as? String, let executable = inside(root, entry) else {
            return .failure(Problem(reason: "bad entrypoint"))
        }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: executable.path, isDirectory: &isDirectory), !isDirectory.boolValue,
              FileManager.default.isExecutableFile(atPath: executable.path) else {
            return .failure(Problem(reason: "entrypoint is not executable"))
        }
        // pathDir 只是補 rg，不對就不加，不影響整理
        var pathDir: URL?
        if let path = object["pathDir"] as? String, let dir = inside(root, path),
           FileManager.default.fileExists(atPath: dir.path, isDirectory: &isDirectory), isDirectory.boolValue {
            pathDir = dir
        }
        let info = Bundle(url: app)?.infoDictionary
        let name = (info?["CFBundleDisplayName"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            ?? (info?["CFBundleName"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            ?? app.deletingPathExtension().lastPathComponent
        return .success(Codex(app: app, name: name, executable: executable, pathDir: pathDir))
    }

    /// 說明檔裡的相對路徑：不接受絕對路徑、`..`，解開連結之後也要在 codex-cli 裡面
    private static func inside(_ root: URL, _ relative: String) -> URL? {
        guard !relative.isEmpty, !relative.hasPrefix("/"), !relative.hasPrefix("~"),
              !relative.split(separator: "/").contains("..") else { return nil }
        let base = root.resolvingSymlinksInPath().standardizedFileURL.path
        let url = root.appendingPathComponent(relative)
        let resolved = url.resolvingSymlinksInPath().standardizedFileURL.path
        guard resolved.hasPrefix(base + "/") else { return nil }
        return url
    }
}
