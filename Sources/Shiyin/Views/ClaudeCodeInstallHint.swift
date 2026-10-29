import AppKit
import SwiftUI

/// 只有 Claude 桌面版、沒有 Claude Code 時的說明（R56.2、design §37.3）：拾音不用桌面版裡的那一份，請使用者另外裝命令列版
struct ClaudeCodeInstallHint: View {
    static let command = "curl -fsSL https://claude.ai/install.sh | bash"
    static let guide = URL(string: "https://code.claude.com/docs/en/setup")!

    /// 「重新檢查」：裝好之後讓畫面重新找 claude、查登入
    var onRecheck: () -> Void
    @State private var copied = false

    /// 設定頁、引導要不要顯示：claude 找不到、有 Claude 桌面版
    static var applies: Bool { Paths.findTool("claude") == nil && DesktopApps.hasClaude }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("你有 Claude 桌面版：另外安裝 Claude Code（命令列版）之後，拾音就能用你的 Claude 訂閱（Pro 以上）整理。")
                .fixedSize(horizontal: false, vertical: true)
            Text("打開「終端機」，貼上這一行安裝：").foregroundStyle(.secondary)
            HStack(spacing: 8) {
                Text(verbatim: Self.command)
                    .font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(.horizontal, 6).padding(.vertical, 3)
                    .background(RoundedRectangle(cornerRadius: 4).fill(Color.secondary.opacity(0.12)))
                // 「複製」的英文在別處是 Duplicate（複製會議），這裡是複製到剪貼簿
                Button(copied ? String(localized: "已複製") : MeetingLanguage.ui(zh: "複製", en: "Copy")) {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(Self.command, forType: .string)
                    copied = true
                }
            }
            HStack(spacing: 12) {
                Link("安裝說明", destination: Self.guide)
                Button("重新檢查") {
                    copied = false
                    onRecheck()
                }
            }
            Text("裝好之後在終端機執行 claude 登入，再按「重新檢查」。").font(.caption).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}
