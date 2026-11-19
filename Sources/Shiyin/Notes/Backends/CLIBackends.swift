import Foundation

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
