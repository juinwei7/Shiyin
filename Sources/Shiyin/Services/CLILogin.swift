import AppKit
import Foundation

/// Claude Code、Codex 的登入狀態與重新登入（3.1.2，使用者回報：登入過期時只看到英文錯誤，不知道怎麼辦）
@MainActor
enum CLILogin {
    struct Status: Equatable {
        var loggedIn: Bool
        /// 已登入時的說明，例如「someone@example.com（Max 方案）」或「Logged in using ChatGPT」
        var detail: String
    }

    /// 查登入狀態，不會送出請求、不消耗額度。找不到指令或看不懂輸出時回傳 nil。
    /// 注意：「登入過期、刷新失敗」時這裡可能還是顯示已登入，要實際送出請求才知道（設定裡的「測試整理工具」）
    static func status(_ provider: LLMProvider) async -> Status? {
        #if DEBUG
        // -debugCLILogin no：當成沒登入，不執行 CLI（design §34.6）
        if UserDefaults.standard.string(forKey: "debugCLILogin") == "no", provider == .claude || provider == .codex {
            return Status(loggedIn: false, detail: "")
        }
        #endif
        switch provider {
        case .claude:
            guard let exe = Paths.findTool("claude"),
                  let output = await run(exe, ["auth", "status", "--json"]),
                  let object = try? JSONSerialization.jsonObject(with: Data(output.stdout.utf8)) as? [String: Any],
                  let loggedIn = object["loggedIn"] as? Bool else { return nil }
            var detail = object["email"] as? String ?? (object["authMethod"] as? String ?? "")
            if let plan = object["subscriptionType"] as? String, !plan.isEmpty {
                detail += String(localized: "（\(plan.capitalized) 方案）")
            }
            return Status(loggedIn: loggedIn, detail: detail)
        case .codex:
            guard let exe = Paths.findTool("codex"), let output = await run(exe, ["login", "status"]) else { return nil }
            // codex 把狀態印在 stdout 或 stderr，兩個都看
            let line = (output.stdout + "\n" + output.stderr).split(separator: "\n").first { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
                .map(String.init) ?? ""
            let loggedIn = line.localizedCaseInsensitiveContains("logged in") && !line.localizedCaseInsensitiveContains("not logged in")
            return Status(loggedIn: loggedIn, detail: line)
        default:
            return nil
        }
    }

    /// 查登入狀態並記進快取（design §34.1）；看不出來時快取是 nil，當作準備好
    @discardableResult
    static func refresh(_ provider: LLMProvider) async -> Status? {
        let status = await status(provider)
        CLILoginCache.store(provider, status?.loggedIn)
        return status
    }

    /// 目前的整理工具是 claude、codex 時，距離上次查超過 60 秒才再查（app 啟動、回到前景）
    static func refreshIfStale() async {
        let provider = AppSettings.provider
        guard provider == .claude || provider == .codex, CLILoginCache.isStale(provider, after: 60) else { return }
        await refresh(provider)
    }

    /// 打開「終端機」執行登入指令（會開瀏覽器登入頁）。用 .command 檔案打開，不需要自動化權限
    static func openLogin(_ provider: LLMProvider) {
        let tool = provider == .codex ? "codex" : "claude"
        guard let exe = Paths.findTool(tool) else { return }
        let arguments = provider == .codex ? "login" : "auth login"
        let intro = String(localized: "拾音：正在打開瀏覽器登入 \(provider.shortLabel)。登入完成後，回到拾音按「重試」或「測試整理工具」。")
        let done = String(localized: "完成後可以關掉這個視窗。")
        let script = """
            #!/bin/zsh
            clear
            echo \(shellQuote(intro))
            echo
            \(shellQuote(exe.path)) \(arguments)
            echo
            echo \(shellQuote(done))
            """
        do {
            let file = FileManager.default.temporaryDirectory.appendingPathComponent("shiyin-\(tool)-login.command")
            try script.write(to: file, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)
            NSWorkspace.shared.open(file)
        } catch {
            log.error("open login failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    private static func shellQuote(_ text: String) -> String {
        "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func run(_ exe: URL, _ arguments: [String]) async -> (stdout: String, stderr: String)? {
        let process = Subprocess(exe, arguments, directory: FileManager.default.temporaryDirectory)
        guard let result = try? await process.run(timeout: 20) else { return nil }
        return (String(decoding: result.stdout, as: UTF8.self), result.stderrTail(500))
    }
}
