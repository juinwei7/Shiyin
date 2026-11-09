import Foundation

// 進入點（design §44.1）：`Shiyin --mcp` 是給 AI 工具用的本地 MCP 伺服器，在 SwiftUI、AppModel 啟動之前分流，
// 不跑 app 的啟動流程（復原錄音、工作佇列、自動整理、檢查更新）、不出現在 Dock
if CommandLine.arguments.dropFirst().contains("--mcp") {
    MCPServer.run()
}
MainActor.assumeIsolated { ShiyinApp.main() }
