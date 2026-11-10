import SwiftUI

/// 設定「進階」的「AI 工具（MCP）」（design §44.4）：開關、加到 Claude Code、Codex、Claude Desktop、複製設定
struct MCPSettings: View {
    @AppStorage(AppSettings.mcpEnabledKey) private var enabled = false
    @State private var hasClaude = false
    @State private var hasCodex = false
    @State private var hasDesktop = false
    @State private var outcome: MCPSetup.Outcome?
    @State private var busy = false

    /// 公司的企業伺服器關掉了（R69.6）
    private var blocked: Bool { EnterpriseServer.shared.account?.mcp == false }

    var body: some View {
        Toggle("讓 AI 工具讀取會議", isOn: blocked ? .constant(false) : $enabled)
            .disabled(blocked)
            .onChange(of: enabled) { _, on in
                outcome = nil
                guard on else { return }
                do { try MCPSetup.installLauncher() } catch let error as MCPSetup.SetupError {
                    outcome = MCPSetup.Outcome(ok: false, message: error.localizedDescription)
                } catch {}
            }
            // 進到設定頁時找一次（找 claude、codex 會讀檔案系統，不在每次重畫時做）
            .task {
                hasClaude = Paths.locateTool("claude") != nil
                hasCodex = Paths.locateTool("codex") != nil
                hasDesktop = MCPSetup.claudeDesktopInstalled
            }
        Group {
            if blocked {
                Text("公司的企業伺服器關掉了這個功能。")
            } else {
                Text("Claude Code、Claude Desktop、Codex 等支援 MCP 的 AI 工具可以搜尋、讀你的會議紀錄和逐字稿（只能讀、不能改），拾音不用開著。讀到的內容會交給那個 AI 工具處理，跟整理會議紀錄一樣會離開這台電腦。")
            }
        }
        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        if enabled && !blocked {
            HStack {
                Button("加到 Claude Code") { run { await MCPSetup.addToClaudeCode() } }
                    .disabled(busy || !hasClaude)
                Button("加到 Codex") { run { await MCPSetup.addToCodex() } }
                    .disabled(busy || !hasCodex)
                Button("加到 Claude Desktop") { run { MCPSetup.addToClaudeDesktop() } }
                    .disabled(busy || !hasDesktop)
                // 「複製」在翻譯表是 Duplicate（複製一份範本），這裡是 Copy
                Menu(MeetingLanguage.ui(zh: "複製", en: "Copy")) {
                    Button("複製 Claude Code 指令") { copy(.claudeCodeCommand) }
                    Button("複製設定（JSON）") { copy(.configJSON) }
                }
                .fixedSize()
                if busy { ProgressView().controlSize(.small) }
            }
            if let missing { Text(missing).font(.caption).foregroundStyle(.secondary) }
            if let outcome {
                Text(outcome.message)
                    .font(.callout)
                    .foregroundStyle(outcome.ok ? Color.secondary : Color.red)
                    .fixedSize(horizontal: false, vertical: true)
                    .textSelection(.enabled)
            }
        }
    }

    /// 這台 Mac 上找不到的工具
    private var missing: String? {
        var names: [String] = []
        if !hasClaude { names.append("claude") }
        if !hasCodex { names.append("codex") }
        if !hasDesktop { names.append("Claude Desktop") }
        guard !names.isEmpty else { return nil }
        return String(localized: "這台 Mac 上找不到 \(ListFormatter.localizedString(byJoining: names))；其他支援 MCP 的工具可以用「複製」自己貼上設定。")
    }

    private func run(_ action: @escaping () async -> MCPSetup.Outcome) {
        busy = true
        outcome = nil
        Task {
            let result = await action()
            outcome = result
            busy = false
        }
    }

    private func copy(_ item: MCPSetup.CopyItem) {
        guard !MCPSetup.isTranslocated else {
            outcome = MCPSetup.Outcome(ok: false, message: MCPSetup.SetupError.translocated.localizedDescription)
            return
        }
        MCPSetup.copy(item)
        outcome = MCPSetup.Outcome(ok: true, message: String(localized: "已複製"))
    }
}
