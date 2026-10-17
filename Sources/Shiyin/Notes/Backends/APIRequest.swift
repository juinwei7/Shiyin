import Foundation

/// API 整理工具共用的 HTTP 送出與錯誤處理（design §8.4、§19.1）
@MainActor
enum APIRequest {
    struct Response {
        let status: Int
        let object: [String: Any]
        /// 錯誤回應裡的訊息；沒有的話是回應開頭的原文
        let message: String
    }

    /// 送出請求，HTTP 狀態不是 200 時丟出看得懂的錯誤
    static func send(_ request: URLRequest, provider: String) async throws -> [String: Any] {
        let response = try await fetch(request, provider: provider)
        guard response.status == 200 else { throw error(response, provider: provider) }
        return response.object
    }

    /// 送出請求，不檢查 HTTP 狀態（OpenAI 相容 API 要看錯誤內容決定要不要退回 JSON 模式）
    static func fetch(_ request: URLRequest, provider: String) async throws -> Response {
        let (data, response): (Data, URLResponse)
        do { (data, response) = try await URLSession.shared.data(for: request) }
        catch { throw LLMError.failed(String(localized: "連不到 \(provider)：\(error.localizedDescription)")) }
        let parsed = try? JSONSerialization.jsonObject(with: data)
        let object = parsed as? [String: Any] ?? [:]
        // Gemini 的 Interactions API 把錯誤包在陣列裡：[{"error": {...}}]（design §19.2）
        let errorObject = (object["error"] ?? (parsed as? [[String: Any]])?.first?["error"]) as? [String: Any]
        let message = (errorObject?["message"] as? String) ?? String(decoding: data.prefix(300), as: UTF8.self)
        return Response(status: (response as? HTTPURLResponse)?.statusCode ?? 0, object: object, message: message)
    }

    static func error(_ response: Response, provider: String) -> LLMError {
        let status = response.status
        // Gemini 的 key 錯誤是 HTTP 400 加上「API key not valid」
        let keyProblem = status == 401 || (status == 400 && response.message.localizedCaseInsensitiveContains("API key"))
        let hint = keyProblem ? String(localized: "（API key 錯誤或已失效）") : status == 429 ? String(localized: "（超過用量限制，請稍後再試）") : ""
        return LLMError.failed(String(localized: "\(provider) 回傳 HTTP \(status)\(hint)：\(response.message)"))
    }
}
