import Foundation

enum LLMProvider: String, CaseIterable, Identifiable {
    case claude, codex, anthropic, openai, none
    var id: String { rawValue }

    var label: String {
        switch self {
        case .claude: return String(localized: "Claude（claude CLI）")
        case .codex: return String(localized: "Codex（codex CLI）")
        case .anthropic: return "Anthropic API"
        case .openai: return "OpenAI API"
        case .none: return String(localized: "不整理")
        }
    }

    /// 「由 xx 整理」用的名稱
    var shortLabel: String {
        switch self {
        case .claude: return "Claude"
        case .codex: return "Codex"
        case .anthropic: return "Anthropic API"
        case .openai: return "OpenAI API"
        case .none: return String(localized: "不整理")
        }
    }

    var usesAPIKey: Bool { self == .anthropic || self == .openai }
    var keychainAccount: String { self == .anthropic ? "anthropic" : "openai" }

    /// 模型設定存在 UserDefaults 的 key；API 的預設模型
    var modelKey: String { "\(rawValue)Model" }
    var defaultModel: String {
        switch self {
        case .anthropic: return "claude-sonnet-5-5"
        case .openai: return "gpt-5"
        default: return ""
        }
    }
}

enum LLMError: LocalizedError {
    case notInstalled(String)
    /// claude / codex 沒登入或登入過期（使用者回報：只看到英文的 OAuth 錯誤，不知道要做什麼）
    case notLoggedIn(String)
    case missingKey(String)
    case failed(String)
    case badOutput(String)

    var errorDescription: String? {
        switch self {
        case .notInstalled(let name): return String(localized: "找不到 \(name) 指令，請先安裝並登入，或到設定 → 整理工具改用 API key")
        case .notLoggedIn(let name):
            return name == "codex"
                ? String(localized: "Codex 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：codex login）。")
                : String(localized: "Claude Code 還沒登入或登入已過期，請重新登入後再試一次（終端機指令：claude auth login）。")
        case .missingKey(let name): return String(localized: "還沒有填 \(name) 的 API key，請到設定 → 整理工具填寫")
        case .failed(let detail): return detail
        case .badOutput(let detail): return String(localized: "回傳的會議紀錄格式不正確：\(detail)")
        }
    }

    /// CLI 的錯誤訊息是不是登入問題，例如「Not logged in · Please run /login」「Failed to authenticate: OAuth session expired」
    static func isLoginProblem(_ text: String) -> Bool {
        let t = text.lowercased()
        // 自己的說明文字裡有「claude auth login」「codex login」，存在工作紀錄的失敗訊息也要認得
        return ["not logged in", "/login", "auth login", "codex login", "failed to authenticate", "oauth", "login required",
                "invalid api key", "unauthorized", "401"].contains { t.contains($0) }
    }
}

/// 呼叫 claude / codex CLI 或 Anthropic / OpenAI API 產生會議紀錄（design §8.4）
@MainActor
enum LLMClient {
    static let timeout: TimeInterval = 20 * 60

    static func generate(provider: LLMProvider, model: String, system: String, prompt: String,
                         onStart: (Subprocess) -> Void) async throws -> MeetingNotes {
        switch provider {
        case .claude, .codex:
            // 工作目錄是一個空的暫存資料夾：不會讀到任何專案的 CLAUDE.md，用完就刪
            let dir = try Paths.makeTempDirectory()
            defer { try? FileManager.default.removeItem(at: dir) }
            return provider == .claude
                ? try await claude(model: model, system: system, prompt: prompt, dir: dir, onStart: onStart)
                : try await codex(model: model, system: system, prompt: prompt, dir: dir, onStart: onStart)
        case .anthropic:
            return try await anthropic(model: model.isEmpty ? provider.defaultModel : model, system: system, prompt: prompt)
        case .openai:
            return try await openai(model: model.isEmpty ? provider.defaultModel : model, system: system, prompt: prompt)
        case .none:
            throw LLMError.failed(String(localized: "整理工具設定為「不整理」"))
        }
    }

    // MARK: CLI（沿用第二版）

    private static func claude(model: String, system: String, prompt: String, dir: URL,
                               onStart: (Subprocess) -> Void) async throws -> MeetingNotes {
        guard let exe = Paths.findTool("claude") else { throw LLMError.notInstalled("claude") }
        // 不能加 --bare：--bare 不讀 OAuth 登入資訊
        var args = ["-p", "--output-format", "json", "--tools", "", "--no-session-persistence", "--strict-mcp-config",
                    "--system-prompt", system, "--json-schema", MeetingNotes.jsonSchema]
        if !model.isEmpty { args += ["--model", model] }
        let process = Subprocess(exe, args, directory: dir)
        onStart(process)
        let result = try await process.run(input: Data(prompt.utf8), timeout: timeout)
        let text = String(decoding: result.stdout, as: UTF8.self)
        guard let line = text.split(separator: "\n").last(where: { $0.hasPrefix("{") }),
              let object = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else {
            let detail = result.stderrTail(500)
            if LLMError.isLoginProblem(detail + text) { throw LLMError.notLoggedIn("claude") }
            throw LLMError.failed(detail.isEmpty ? String(localized: "claude 沒有回傳結果（結束代碼 \(result.status)）") : detail)
        }
        if (object["is_error"] as? Bool) == true || object["structured_output"] == nil {
            let message = (object["result"] as? String) ?? result.stderrTail(500)
            if LLMError.isLoginProblem(message) { throw LLMError.notLoggedIn("claude") }
            throw LLMError.failed(String(message.prefix(500)))
        }
        return try decode(JSONSerialization.data(withJSONObject: object["structured_output"]!))
    }

    private static func codex(model: String, system: String, prompt: String, dir: URL,
                              onStart: (Subprocess) -> Void) async throws -> MeetingNotes {
        guard let exe = Paths.findTool("codex") else { throw LLMError.notInstalled("codex") }
        let schema = dir.appendingPathComponent("schema.json")
        let output = dir.appendingPathComponent("out.json")
        try MeetingNotes.jsonSchema.write(to: schema, atomically: true, encoding: .utf8)
        var args = ["exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never",
                    "--output-schema", schema.path, "-o", output.path]
        if !model.isEmpty { args += ["-m", model] }
        args.append("-")
        let process = Subprocess(exe, args, directory: dir)
        onStart(process)
        // Codex 沒有獨立的系統提示詞參數，放在使用者提示詞前面
        let result = try await process.run(input: Data((system + "\n\n" + prompt).utf8), timeout: timeout)
        guard result.succeeded, let data = try? Data(contentsOf: output), !data.isEmpty else {
            let detail = result.stderrTail(500)
            if LLMError.isLoginProblem(detail) { throw LLMError.notLoggedIn("codex") }
            throw LLMError.failed(detail.isEmpty ? String(localized: "codex 沒有回傳結果（結束代碼 \(result.status)）") : detail)
        }
        return try decode(data)
    }

    // MARK: API（design §8.4）

    private static func anthropic(model: String, system: String, prompt: String) async throws -> MeetingNotes {
        guard let key = Keychain.get("anthropic"), !key.isEmpty else { throw LLMError.missingKey("Anthropic") }
        let schema = try JSONSerialization.jsonObject(with: Data(MeetingNotes.jsonSchema.utf8))
        let body: [String: Any] = [
            "model": model,
            "max_tokens": 16_000,
            "system": system,
            "messages": [["role": "user", "content": prompt]],
            "tools": [["name": "save_meeting_notes", "description": String(localized: "儲存整理好的會議紀錄"), "input_schema": schema]],
            "tool_choice": ["type": "tool", "name": "save_meeting_notes"],
        ]
        var request = URLRequest(url: URL(string: "https://api.anthropic.com/v1/messages")!, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let object = try await send(request, provider: "Anthropic")
        guard let content = object["content"] as? [[String: Any]],
              let input = content.first(where: { $0["type"] as? String == "tool_use" })?["input"] else {
            throw LLMError.badOutput(String(localized: "回應裡沒有會議紀錄（stop_reason：\(object["stop_reason"] as? String ?? String(localized: "未知"))）"))
        }
        return try decode(JSONSerialization.data(withJSONObject: input))
    }

    private static func openai(model: String, system: String, prompt: String) async throws -> MeetingNotes {
        guard let key = Keychain.get("openai"), !key.isEmpty else { throw LLMError.missingKey("OpenAI") }
        let schema = try JSONSerialization.jsonObject(with: Data(MeetingNotes.jsonSchema.utf8))
        let body: [String: Any] = [
            "model": model,
            "instructions": system,
            "input": prompt,
            "text": ["format": ["type": "json_schema", "name": "meeting_notes", "schema": schema, "strict": true]],
        ]
        var request = URLRequest(url: URL(string: "https://api.openai.com/v1/responses")!, timeoutInterval: timeout)
        request.httpMethod = "POST"
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let object = try await send(request, provider: "OpenAI")
        let outputs = object["output"] as? [[String: Any]] ?? []
        let text = outputs.flatMap { ($0["content"] as? [[String: Any]]) ?? [] }
            .first { $0["type"] as? String == "output_text" }?["text"] as? String
        guard let text else { throw LLMError.badOutput(String(localized: "回應裡沒有文字（status：\(object["status"] as? String ?? String(localized: "未知"))）")) }
        return try decode(Data(text.utf8))
    }

    private static func send(_ request: URLRequest, provider: String) async throws -> [String: Any] {
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await URLSession.shared.data(for: request) }
        catch { throw LLMError.failed(String(localized: "連不到 \(provider)：\(error.localizedDescription)")) }
        let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else {
            let message = ((object["error"] as? [String: Any])?["message"] as? String) ?? String(decoding: data.prefix(300), as: UTF8.self)
            let hint = status == 401 ? String(localized: "（API key 錯誤或已失效）") : status == 429 ? String(localized: "（超過用量限制，請稍後再試）") : ""
            throw LLMError.failed(String(localized: "\(provider) 回傳 HTTP \(status)\(hint)：\(message)"))
        }
        return object
    }

    /// 列出可用的模型，給設定頁選（失敗就回空陣列）
    static func listModels(_ provider: LLMProvider) async -> [String] {
        guard provider.usesAPIKey, let key = Keychain.get(provider.keychainAccount) else { return [] }
        var request = URLRequest(url: URL(string: provider == .anthropic ? "https://api.anthropic.com/v1/models?limit=100"
                                          : "https://api.openai.com/v1/models")!)
        if provider == .anthropic {
            request.setValue(key, forHTTPHeaderField: "x-api-key")
            request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        } else {
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return [] }
        let ids = list.compactMap { $0["id"] as? String }
        return provider == .openai ? ids.filter { $0.hasPrefix("gpt-") || $0.hasPrefix("o") }.sorted(by: >) : ids
    }

    private static func decode(_ data: Data) throws -> MeetingNotes {
        do { return try JSONDecoder().decode(MeetingNotes.self, from: data) }
        catch { throw LLMError.badOutput(error.localizedDescription) }
    }
}
