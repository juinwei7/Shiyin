import Foundation

// claude / codex CLI（沿用第二版，design §8.4）。
// 工作目錄是一個空的暫存資料夾：不會讀到任何專案的 CLAUDE.md，用完就刪

struct ClaudeCLI: LLMBackend {
    let model: String

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let exe = Paths.findTool("claude") else { throw LLMError.notInstalled("claude") }
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        // 不能加 --bare：--bare 不讀 OAuth 登入資訊
        var args = ["-p", "--output-format", "json", "--tools", "", "--no-session-persistence", "--strict-mcp-config",
                    "--system-prompt", system, "--json-schema", format.schema]
        if !model.isEmpty { args += ["--model", model] }
        let process = Subprocess(exe, args, directory: dir)
        onStart(process)
        let result = try await process.run(input: Data(prompt.utf8), timeout: LLMClient.timeout)
        let text = String(decoding: result.stdout, as: UTF8.self)
        guard let line = text.split(separator: "\n").last(where: { $0.hasPrefix("{") }),
              let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else {
            let detail = result.stderrTail(500)
            if LLMError.isLoginProblem(detail + text) { throw LLMError.notLoggedIn("claude") }
            if LLMError.isLimitProblem(detail + text) { throw LLMError.limited(detail.isEmpty ? String(text.prefix(500)) : detail) }
            throw LLMError.failed(detail.isEmpty ? String(localized: "claude 沒有回傳結果（結束代碼 \(result.status)）") : detail)
        }
        if (object["is_error"] as? Bool) == true || object["structured_output"] == nil {
            let message = (object["result"] as? String) ?? result.stderrTail(500)
            if LLMError.isLoginProblem(message) { throw LLMError.notLoggedIn("claude") }
            if LLMError.isLimitProblem(message) { throw LLMError.limited(String(message.prefix(500))) }
            throw LLMError.failed(String(message.prefix(500)))
        }
        return try JSONSerialization.data(withJSONObject: object["structured_output"]!)
    }

    func listModels() async -> [String] { [] }
}

struct CodexCLI: LLMBackend {
    let model: String

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let exe = Paths.findTool("codex") else { throw LLMError.notInstalled("codex") }
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let schemaFile = dir.appendingPathComponent("schema.json")
        let output = dir.appendingPathComponent("out.json")
        try format.schema.write(to: schemaFile, atomically: true, encoding: .utf8)
        var args = ["exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never",
                    "--output-schema", schemaFile.path, "-o", output.path]
        if !model.isEmpty { args += ["-m", model] }
        args.append("-")
        let process = Subprocess(exe, args, directory: dir)
        onStart(process)
        // Codex 沒有獨立的系統提示詞參數，放在使用者提示詞前面
        let result = try await process.run(input: Data((system + "\n\n" + prompt).utf8), timeout: LLMClient.timeout)
        guard result.succeeded, let data = try? Data(contentsOf: output), !data.isEmpty else {
            let detail = result.stderrTail(500)
            if LLMError.isLoginProblem(detail) { throw LLMError.notLoggedIn("codex") }
            if LLMError.isLimitProblem(detail) { throw LLMError.limited(detail) }
            throw LLMError.failed(detail.isEmpty ? String(localized: "codex 沒有回傳結果（結束代碼 \(result.status)）") : detail)
        }
        return data
    }

    func listModels() async -> [String] { [] }
}
