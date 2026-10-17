import Foundation

/// Gemini API（design §19.2、D23）：Interactions API；store: false，不讓 Google 保存會議內容
struct GeminiAPI: LLMBackend {
    let model: String

    private var base: String { LLMClient.apiBase(.gemini, "https://generativelanguage.googleapis.com") }

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard let key = Keychain.get("gemini"), !key.isEmpty else { throw LLMError.missingKey("Gemini") }
        let schema = try JSONSerialization.jsonObject(with: Data(format.schema.utf8))
        let body: [String: Any] = [
            "model": model,
            "system_instruction": system,
            "input": prompt,
            "response_format": ["type": "text", "mime_type": "application/json", "schema": schema],
            "store": false,
        ]
        var request = URLRequest(url: URL(string: base + "/v1beta/interactions")!, timeoutInterval: LLMClient.timeout)
        request.httpMethod = "POST"
        request.setValue(key, forHTTPHeaderField: "x-goog-api-key")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let object = try await APIRequest.send(request, provider: "Gemini")
        let status = object["status"] as? String ?? String(localized: "未知")
        switch status {
        case "completed": break
        case "incomplete": throw LLMError.failed(String(localized: "回應不完整（可能超過長度上限）"))
        default: throw LLMError.failed(String(localized: "Gemini 沒有完成整理（status：\(status)）"))
        }
        // steps 裡還有思考過程（thought），只取模型的輸出
        let text = (object["steps"] as? [[String: Any]] ?? [])
            .filter { $0["type"] as? String == "model_output" }
            .flatMap { ($0["content"] as? [[String: Any]]) ?? [] }
            .filter { $0["type"] as? String == "text" }
            .compactMap { $0["text"] as? String }
            .joined()
        guard !text.isEmpty else { throw LLMError.badOutput(String(localized: "回應裡沒有文字（status：\(status)）")) }
        return Data(text.utf8)
    }

    func listModels() async -> [String] {
        guard let key = Keychain.get("gemini") else { return [] }
        var request = URLRequest(url: URL(string: base + "/v1beta/models?pageSize=1000")!)
        request.setValue(key, forHTTPHeaderField: "x-goog-api-key")
        guard let (data, _) = try? await URLSession.shared.data(for: request),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["models"] as? [[String: Any]] else { return [] }
        // 只留產生文字的 Gemini 模型（design §19.2）
        let excluded = ["embedding", "tts", "image", "live", "audio"]
        return list.compactMap { $0["name"] as? String }
            .filter { $0.hasPrefix("models/gemini-") }
            .map { String($0.dropFirst("models/".count)) }
            .filter { name in !excluded.contains { name.contains($0) } }
    }
}
