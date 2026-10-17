import Foundation

/// OpenAI API（design §8.4）：Responses API，text.format 用 strict json_schema
struct OpenAIAPI: LLMBackend {
    let model: String

    private var base: String { LLMClient.apiBase(.openai, "https://api.openai.com") }

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let key = Keychain.get("openai"), !key.isEmpty else { throw LLMError.missingKey("OpenAI") }
        let schemaObject = try JSONSerialization.jsonObject(with: Data(format.schema.utf8))
        let body: [String: Any] = [
            "model": model,
            "instructions": system,
            "input": prompt,
            "text": ["format": ["type": "json_schema", "name": format.toolName, "schema": schemaObject, "strict": true]],
        ]
        var request = URLRequest(url: URL(string: base + "/v1/responses")!, timeoutInterval: LLMClient.timeout)
        request.httpMethod = "POST"
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let object = try await APIRequest.send(request, provider: "OpenAI")
        let outputs = object["output"] as? [[String: Any]] ?? []
        let text = outputs.flatMap { ($0["content"] as? [[String: Any]]) ?? [] }
            .first { $0["type"] as? String == "output_text" }?["text"] as? String
        guard let text else { throw LLMError.badOutput(String(localized: "回應裡沒有文字（status：\(object["status"] as? String ?? String(localized: "未知"))）")) }
        return Data(text.utf8)
    }

    func listModels() async -> [String] {
        guard let key = Keychain.get("openai") else { return [] }
        var request = URLRequest(url: URL(string: base + "/v1/models")!)
        request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return [] }
        let ids = list.compactMap { $0["id"] as? String }
        return ids.filter { $0.hasPrefix("gpt-") || $0.hasPrefix("o") }.sorted(by: >)
    }
}
