import Foundation

/// Anthropic API（design §8.4）：強制呼叫一個 tool，input_schema 就是要的格式
struct AnthropicAPI: LLMBackend {
    let model: String

    private var base: String { LLMClient.apiBase(.anthropic, "https://api.anthropic.com") }

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let key = try Keychain.read("anthropic"), !key.isEmpty else { throw LLMError.missingKey("Anthropic") }
        let schema = try JSONSerialization.jsonObject(with: Data(format.schema.utf8))
        let body: [String: Any] = [
            "model": model,
            "max_tokens": 16_000,
            "system": system,
            "messages": [["role": "user", "content": prompt]],
            "tools": [["name": format.toolName, "description": format.toolDescription, "input_schema": schema]],
            "tool_choice": ["type": "tool", "name": format.toolName],
        ]
        var request = URLRequest(url: URL(string: base + "/v1/messages")!, timeoutInterval: LLMClient.timeout)
        request.httpMethod = "POST"
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let object = try await APIRequest.send(request, provider: "Anthropic")
        guard let content = object["content"] as? [[String: Any]],
              let input = content.first(where: { $0["type"] as? String == "tool_use" })?["input"] else {
            throw LLMError.badOutput(String(localized: "回應裡沒有會議紀錄（stop_reason：\(object["stop_reason"] as? String ?? String(localized: "未知"))）"))
        }
        return try JSONSerialization.data(withJSONObject: input)
    }

    func listModels() async -> [String] {
        guard let key = Keychain.get("anthropic") else { return [] }
        var request = URLRequest(url: URL(string: base + "/v1/models?limit=100")!)
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return [] }
        return list.compactMap { $0["id"] as? String }
    }
}
