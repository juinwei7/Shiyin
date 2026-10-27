import Foundation

/// OpenAI 相容 API（design §19.3、D24）：Groq、OpenRouter、Ollama、LM Studio 等。
/// 用 Chat Completions；模型不支援 json_schema 時退回 JSON 模式
struct OpenAICompatibleAPI: LLMBackend {
    static let baseURLKey = "openaiCompatibleBaseURL"
    /// 設定頁「常用」選單
    static let presets: [(name: String, url: String)] = [
        ("Groq", "https://api.groq.com/openai/v1"),
        ("OpenRouter", "https://openrouter.ai/api/v1"),
        ("Ollama", "http://localhost:11434/v1"),
        ("LM Studio", "http://localhost:1234/v1"),
    ]

    let model: String
    let baseURL: String
    /// 企業伺服器（design §36.3）：key 換成登入拿到的 access token、模型可以留空（伺服器用公司設定的預設模型）、錯誤訊息裡的名稱
    var key: (@MainActor () async throws -> String?)? = nil
    var requiresModel = true
    var name: String? = nil

    /// 設定裡的網址；DEBUG 建置可以用 llmurl 取代
    @MainActor
    static var savedBaseURL: String {
        LLMClient.apiBase(.openaiCompatible, UserDefaults.standard.string(forKey: baseURLKey) ?? "")
    }

    /// 整理過的網址：只接受 http、https；去掉結尾的「/」和使用者多貼的「/chat/completions」
    static func normalized(_ text: String) -> URL? {
        var s = text.trimmingCharacters(in: .whitespacesAndNewlines)
        while s.hasSuffix("/") { s.removeLast() }
        if s.hasSuffix("/chat/completions") { s.removeLast("/chat/completions".count) }
        while s.hasSuffix("/") { s.removeLast() }
        guard let url = URL(string: s), let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let host = url.host(percentEncoded: false), !host.isEmpty else { return nil }
        return url
    }

    /// 主機是這台 Mac：內容不會離開電腦（隱私說明用，design §19.3）
    static func isLocal(_ url: URL) -> Bool {
        let host = (url.host(percentEncoded: false) ?? "").lowercased().trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        return ["localhost", "127.0.0.1", "::1"].contains(host)
    }

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard !baseURL.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw LLMError.notConfigured(String(localized: "還沒有填 OpenAI 相容 API 的網址，請到設定 → 整理工具填寫"))
        }
        guard let base = Self.normalized(baseURL) else {
            throw LLMError.notConfigured(String(localized: "OpenAI 相容 API 的網址不對，要以 http:// 或 https:// 開頭"))
        }
        guard !model.isEmpty || !requiresModel else {
            throw LLMError.notConfigured(String(localized: "還沒有填 OpenAI 相容 API 的模型名稱，請到設定 → 整理工具填寫"))
        }
        let provider = name ?? base.host(percentEncoded: false) ?? "OpenAI 相容 API"   // l10n-skip
        let schema = try JSONSerialization.jsonObject(with: Data(format.schema.utf8))
        let strict: [String: Any] = ["type": "json_schema",
                                     "json_schema": ["name": format.toolName, "schema": schema, "strict": true]]
        var response = try await complete(base, provider: provider, system: system, prompt: prompt, responseFormat: strict)
        if Self.rejectsSchema(response) {
            // 模型不支援 json_schema：改用 JSON 模式，schema 放進系統提示詞（D24）
            let fallback = system + "\n\nRespond with only a JSON object that matches this JSON schema, with no other text:\n"  // l10n-skip
                + format.schema
            response = try await complete(base, provider: provider, system: fallback, prompt: prompt,
                                          responseFormat: ["type": "json_object"])
        }
        guard response.status == 200 else { throw APIRequest.error(response, provider: provider) }
        let choice = (response.object["choices"] as? [[String: Any]])?.first
        let finish = choice?["finish_reason"] as? String
        if finish == "length" { throw LLMError.failed(String(localized: "回應不完整（可能超過長度上限）")) }
        guard let text = (choice?["message"] as? [String: Any])?["content"] as? String, !text.isEmpty else {
            throw LLMError.badOutput(String(localized: "回應裡沒有文字（status：\(finish ?? String(localized: "未知"))）"))
        }
        let json = Self.extractJSON(text)
        guard (try? JSONSerialization.jsonObject(with: Data(json.utf8))) is [String: Any] else {
            throw LLMError.badOutput(String(localized: "回應不是 JSON。") + LLMProvider.openaiCompatible.badOutputHint)
        }
        return Data(json.utf8)
    }

    func listModels() async -> [String] {
        guard let base = Self.normalized(baseURL) else { return [] }
        var request = URLRequest(url: base.appendingPathComponent("models"), timeoutInterval: 15)
        // 有存 key 卻讀不到（macOS 沒允許）時不送出：不帶 key 送出只會換來看不懂的錯誤（design §22.1）
        let key: String?
        do { key = try await currentKey() } catch { return [] }
        if let key, !key.isEmpty {
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return [] }
        return list.compactMap { $0["id"] as? String }.sorted()
    }

    private func complete(_ base: URL, provider: String, system: String, prompt: String,
                          responseFormat: [String: Any]) async throws -> APIRequest.Response {
        let body: [String: Any] = [
            "model": model,
            "messages": [["role": "system", "content": system], ["role": "user", "content": prompt]],
            "response_format": responseFormat,
        ]
        var request = URLRequest(url: base.appendingPathComponent("chat/completions"), timeoutInterval: LLMClient.timeout)
        request.httpMethod = "POST"
        if let key = try await currentKey(), !key.isEmpty {
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }
        if self.key != nil { EnterpriseServer.addLanguage(&request) }
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await APIRequest.fetch(request, provider: provider)
    }

    private func currentKey() async throws -> String? {
        if let key { return try await key() }
        return try Keychain.read("openaiCompatible")
    }

    /// 400／422 而且錯誤訊息提到 response_format、json_schema 或 schema：模型不支援 JSON schema
    static func rejectsSchema(_ response: APIRequest.Response) -> Bool {
        guard response.status == 400 || response.status == 422 else { return false }
        let message = response.message.lowercased()
        return ["response_format", "json_schema", "schema"].contains { message.contains($0) }
    }

    /// 模型在 JSON 前後加了說明或 ```json 圍欄時，取第一個「{」到最後一個「}」
    static func extractJSON(_ text: String) -> String {
        guard let start = text.firstIndex(of: "{"), let end = text.lastIndex(of: "}"), start < end else { return text }
        return String(text[start...end])
    }
}
