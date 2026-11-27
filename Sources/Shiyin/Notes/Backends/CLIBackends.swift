import Foundation
import os

// claude / codex CLI（沿用第二版，design §8.4）。
// 工作目錄是一個空的暫存資料夾：不會讀到任何專案的 CLAUDE.md，用完就刪

struct ClaudeCLI: LLMBackend {
    let model: String

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat, images: [LLMImage],
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let exe = Paths.findTool("claude") else { throw LLMError.notInstalled("claude") }
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        // 不能加 --bare：--bare 不讀 OAuth 登入資訊。有畫面的圖片時（design §83.5）改用 stream-json：
        // stdin 一行使用者訊息（提示詞、每張的說明和圖片），最後一行一樣是 result（structured_output）
        let streaming = !images.isEmpty
        var args = ["-p"] + (streaming ? ["--input-format", "stream-json", "--output-format", "stream-json", "--verbose"] : ["--output-format", "json"])
            + ["--tools", "", "--no-session-persistence", "--strict-mcp-config", "--system-prompt", system, "--json-schema", format.schema]
        if !model.isEmpty { args += ["--model", model] }
        let process = Subprocess(exe, args, directory: dir)
        onStart(process)
        let input = streaming ? try Self.streamMessage(prompt, images: images) : Data(prompt.utf8)
        let result = try await process.run(input: input, timeout: LLMClient.timeout)
        let text = String(decoding: result.stdout, as: UTF8.self)
        guard let line = text.split(separator: "\n").last(where: { $0.hasPrefix("{") }),
              let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else {
            let detail = result.stderrTail(500)
            if LLMError.isLoginProblem(detail + text) { throw LLMError.notLoggedIn("claude") }
            if LLMError.isLimitProblem(detail + text) { throw LLMError.cliLimited(.claude, output: detail + "\n" + text) }
            throw LLMError.failed(detail.isEmpty ? String(localized: "claude 沒有回傳結果（結束代碼 \(result.status)）") : detail,
                                  cause: .cli(result))
        }
        if (object["is_error"] as? Bool) == true || object["structured_output"] == nil {
            let message = (object["result"] as? String) ?? result.stderrTail(500)
            if LLMError.isLoginProblem(message) { throw LLMError.notLoggedIn("claude") }
            if LLMError.isLimitProblem(message) { throw LLMError.cliLimited(.claude, output: message) }
            throw LLMError.failed(String(message.prefix(500)), cause: .cliError)
        }
        return try JSONSerialization.data(withJSONObject: object["structured_output"]!)
    }

    /// stream-json 的一行使用者訊息：提示詞，再來每張畫面的說明和圖片
    static func streamMessage(_ prompt: String, images: [LLMImage]) throws -> Data {
        var content: [[String: Any]] = [["type": "text", "text": prompt]]
        for image in images {
            content.append(["type": "text", "text": image.label])
            content.append(["type": "image", "source": ["type": "base64", "media_type": "image/jpeg", "data": image.jpeg.base64EncodedString()]])
        }
        let message: [String: Any] = ["type": "user", "message": ["role": "user", "content": content]]
        return try JSONSerialization.data(withJSONObject: message) + Data("\n".utf8)
    }

    func listModels() async -> [String] { [] }
}

struct CodexCLI: LLMBackend {
    let model: String

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat, images: [LLMImage],
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let tool = Paths.locateTool("codex") else { throw LLMError.notInstalled("codex") }
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let schemaFile = dir.appendingPathComponent("schema.json")
        let output = dir.appendingPathComponent("out.json")
        try format.schema.write(to: schemaFile, atomically: true, encoding: .utf8)
        var args = ["exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never",
                    "--output-schema", schemaFile.path, "-o", output.path]
        if !model.isEmpty { args += ["-m", model] }
        // 會議中的畫面（design §83.5）：圖片寫進暫存資料夾、每張一個 --image；提示詞最後列出第幾張是哪個時間
        var attached = ""
        for (index, image) in images.enumerated() {
            let file = dir.appendingPathComponent("screen-\(index + 1).jpg")
            try image.jpeg.write(to: file)
            args += ["--image", file.path]
            attached += "\n[\(index + 1)] \(image.label)"
        }
        args.append("-")
        let process = Subprocess(tool.url, args, environment: tool.environment, directory: dir)
        onStart(process)
        // Codex 沒有獨立的系統提示詞參數，放在使用者提示詞前面
        let result = try await process.run(input: Data((system + "\n\n" + prompt + (attached.isEmpty ? "" : "\n" + attached)).utf8),
                                           timeout: LLMClient.timeout)
        guard result.succeeded, let data = try? Data(contentsOf: output), !data.isEmpty else {
            let detail = result.stderrTail(500)
            if LLMError.isLoginProblem(detail) { throw LLMError.notLoggedIn("codex") }
            if LLMError.isLimitProblem(detail) { throw LLMError.cliLimited(.codex, output: detail) }
            let message = detail.isEmpty ? String(localized: "codex 沒有回傳結果（結束代碼 \(result.status)）") : detail
            // 桌面版附的 codex 出錯：建議另外裝 CLI（R55.4）
            if let app = tool.desktopApp {
                throw LLMError.failed(message + "\n" + String(localized: "這是 \(app) 附的 codex，也可以另外安裝 Codex CLI（npm install -g @openai/codex）再試。"),
                                      cause: .cli(result))
            }
            throw LLMError.failed(message, cause: .cli(result))
        }
        return data
    }

    func listModels() async -> [String] { [] }
}

/// Antigravity CLI（agy，R121、design §95）。agy 是會自己讀寫檔案、執行指令的代理：每次用 --settings 帶只在這次有效的設定，
/// 把讀寫檔案、指令、連網、MCP 都擋掉（D204），使用者自己的設定檔不動；工作目錄是空的暫存資料夾，用完就刪
struct AntigravityCLI: LLMBackend {
    let model: String

    /// 登入：直接開 agy（沒有登入的子指令；第一次開會問配色、信任資料夾，接著在瀏覽器登入）
    static let loginArguments = ""
    static var loginIntro: String {
        String(localized: "拾音：正在打開 Antigravity CLI。照畫面在瀏覽器登入，完成後輸入 /quit 離開，再回到拾音按「重試」或「測試整理工具」。")
    }

    /// 只在這次有效的設定（--settings，不會存檔）：每個工具動作都要核准，拒絕規則擋掉所有會碰到檔案、指令、網路、MCP 的動作；
    /// 背景模式沒有人核准，等於全部不能用（design §95.2）
    static let sessionSettings = #"{"toolPermission":"strict","agentMode":"","permissions":{"deny":["read_file(*)","write_file(*)","command(*)","unsandboxed(*)","read_url(*)","execute_url(*)","download_url(*)","mcp(*)"]}}"#

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat, images: [LLMImage],
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let exe = Paths.findTool("agy") else { throw LLMError.notInstalled("agy") }
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        // 設定、schema 放在工作目錄外面；工作目錄是空的
        let workspace = dir.appendingPathComponent("workspace", isDirectory: true)
        try FileManager.default.createDirectory(at: workspace, withIntermediateDirectories: true)
        let schemaFile = dir.appendingPathComponent("schema.json"), settingsFile = dir.appendingPathComponent("settings.json")
        try format.schema.write(to: schemaFile, atomically: true, encoding: .utf8)
        try Self.sessionSettings.write(to: settingsFile, atomically: true, encoding: .utf8)
        // 提示詞從 stdin 送（stream-json 一行一則），不放在命令列參數（R121.4）
        var args = ["--input-format", "stream-json", "--output-format", "stream-json", "--json-schema", schemaFile.path,
                    "--settings", settingsFile.path, "--disable-slash-commands"]
        if !model.isEmpty { args += ["--model", model] }
        let process = Subprocess(exe, args, environment: Paths.subprocessEnvironment(), directory: workspace)
        onStart(process)
        // 第一行就帶 conversation_id：先記下來，逾時、取消、沒有 result 那一行時也刪得到這次的對話（§95.4）
        let conversation = OSAllocatedUnfairLock<String?>(initialState: nil)
        defer { if let id = conversation.withLock({ $0 }) { Self.removeConversation(id) } }
        let result = try await process.run(input: try Self.message(system: system, prompt: prompt, schema: format.schema),
                                           timeout: LLMClient.timeout,
                                           onLine: { line in
                                               guard let id = Self.conversationID(in: line) else { return }
                                               conversation.withLock { if $0 == nil { $0 = id } }
                                           })
        let parsed = Self.parse(result.stdout)
        if let id = parsed.conversationID { conversation.withLock { if $0 == nil { $0 = id } } }
        if let output = parsed.output { return output }
        let detail = [parsed.error ?? "", result.stderrTail(500)].filter { !$0.isEmpty }.joined(separator: "\n")
        if LLMError.isLoginProblem(detail) { throw LLMError.notLoggedIn("agy") }
        if LLMError.isLimitProblem(detail) { throw LLMError.cliLimited(.antigravity, output: detail) }
        if detail.isEmpty {
            throw LLMError.failed(String(localized: "agy 沒有回傳結果（結束代碼 \(result.status)）"), cause: .cli(result))
        }
        throw LLMError.failed(String(detail.prefix(500)), cause: parsed.error == nil ? .cli(result) : .cliError)
    }

    /// stream-json 的一行使用者訊息。agy 沒有系統提示詞參數，放在前面；--json-schema 只在最後整理格式，
    /// 不在提示詞裡寫出 schema 時第一輪會自己發明格式（實測），所以照 OpenAI 相容 API 的退回做法把 schema 也寫進去（D24）
    static func message(system: String, prompt: String, schema: String) throws -> Data {
        let text = system + "\n\n" + prompt
            + "\n\nRespond with only a JSON object that matches this JSON schema, with no other text:\n" + schema  // l10n-skip
        let line: [String: Any] = ["event": "user", "message": ["content": text]]
        return try JSONSerialization.data(withJSONObject: line) + Data("\n".utf8)
    }

    struct Parsed {
        var output: Data?
        var error: String?
        var conversationID: String?
    }

    /// 最後一行 result：status 是 SUCCESS 時取 structured_output（沒有就從 response 取 JSON）；其他狀態取 error
    static func parse(_ stdout: Data) -> Parsed {
        let lines = String(decoding: stdout, as: UTF8.self).split(separator: "\n")
        guard let object = lines.reversed().lazy.compactMap({ line -> [String: Any]? in
                  guard let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any],
                        object["event"] as? String == "result" else { return nil }
                  return object["result"] as? [String: Any]
              }).first else { return Parsed() }
        var parsed = Parsed(conversationID: (object["conversation_id"] as? String).flatMap { $0.isEmpty ? nil : $0 })
        if object["status"] as? String == "SUCCESS" {
            // 只收物件、陣列（同 Windows）：null、字串給 JSONSerialization.data 會丟 Objective-C 例外（try? 接不到，整個 app 會當掉）
            if let structured = object["structured_output"], structured is [String: Any] || structured is [Any],
               JSONSerialization.isValidJSONObject(structured),
               let data = try? JSONSerialization.data(withJSONObject: structured) {
                parsed.output = data
            } else if let response = object["response"] as? String {
                let json = OpenAICompatibleAPI.extractJSON(response)
                if (try? JSONSerialization.jsonObject(with: Data(json.utf8))) is [String: Any] { parsed.output = Data(json.utf8) }
            }
            if parsed.output == nil { parsed.error = String(localized: "agy 沒有回傳 JSON") }
        } else {
            parsed.error = (object["error"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? (object["status"] as? String)
        }
        return parsed
    }

    /// stream-json 一行裡的 conversation_id（step_update、result 都有）
    nonisolated static func conversationID(in line: String) -> String? {
        guard line.contains("conversation_id"),
              let match = line.firstMatch(of: #/"conversation_id"\s*:\s*"([0-9A-Fa-f-]{36})"/#) else { return nil }
        return String(match.1)
    }

    /// agy 沒有不留紀錄的參數，每次都會把整段對話存進 ~/.gemini/antigravity-cli（D204）：只刪這次的對話檔和 brain 資料夾。
    /// id 一定要是 UUID 才刪；agy 改了存放方式時這裡什麼都不做
    static func removeConversation(_ id: String) {
        guard UUID(uuidString: id) != nil else { return }
        let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".gemini/antigravity-cli", isDirectory: true)
        try? FileManager.default.removeItem(at: root.appendingPathComponent("conversations/\(id).db"))
        try? FileManager.default.removeItem(at: root.appendingPathComponent("brain/\(id)", isDirectory: true))
    }

    /// agy models：一行一個「代號<Tab>名稱」，沒登入時結束代碼 1
    func listModels() async -> [String] {
        guard let exe = Paths.findTool("agy"),
              let result = try? await Subprocess(exe, ["models"], environment: Paths.subprocessEnvironment(),
                                                 directory: FileManager.default.temporaryDirectory).run(timeout: 30),
              result.succeeded else { return [] }
        return Self.models(String(decoding: result.stdout, as: UTF8.self))
    }

    static func models(_ output: String) -> [String] {
        output.split(separator: "\n").compactMap { line in
            let parts = line.split(separator: "\t", maxSplits: 1)
            return parts.count == 2 ? String(parts[0]).trimmingCharacters(in: .whitespaces) : nil
        }
    }
}
