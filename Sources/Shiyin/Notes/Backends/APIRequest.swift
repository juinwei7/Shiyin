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

    /// 送出請求，不檢查 HTTP 狀態（OpenAI 相容 API 要看錯誤內容決定要不要退回 JSON 模式）。
    /// 網路暫時出錯、429、5xx 時自動重試一次（design §21.14、R20.5）；取消（Task 被取消）時丟出 CancellationError
    static func fetch(_ request: URLRequest, provider: String) async throws -> Response {
        var retried = false
        while true {
            let (data, response): (Data, URLResponse)
            do { (data, response) = try await URLSession.shared.data(for: request) }
            catch {
                if Task.isCancelled || (error as? URLError)?.code == .cancelled { throw CancellationError() }
                if !retried, let code = (error as? URLError)?.code, retryableErrors.contains(code) {
                    retried = true
                    log.info("API request retry after network error \((error as NSError).code)")
                    try await Task.sleep(for: .seconds(5))
                    continue
                }
                throw LLMError.unreachable(String(localized: "連不到 \(provider)：\(error.localizedDescription)"))
            }
            let http = response as? HTTPURLResponse
            let result = parse(data, status: http?.statusCode ?? 0)
            // 429 是帳戶沒有額度時，等多久都不會好，不重試
            if !retried, retryableStatuses.contains(result.status), !isNoCredit(result) {
                retried = true
                let wait = min(30, max(1, Double(http?.value(forHTTPHeaderField: "retry-after") ?? "") ?? 5))
                log.info("API request retry after HTTP \(result.status), wait \(Int(wait))s")
                try await Task.sleep(for: .seconds(wait))
                continue
            }
            return result
        }
    }

    private static let retryableStatuses: Set<Int> = [429, 500, 502, 503, 504]
    private static let retryableErrors: Set<URLError.Code> = [.timedOut, .networkConnectionLost, .notConnectedToInternet,
                                                              .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed]

    private static func parse(_ data: Data, status: Int) -> Response {
        let parsed = try? JSONSerialization.jsonObject(with: data)
        let object = parsed as? [String: Any] ?? [:]
        // Gemini 的 Interactions API 把錯誤包在陣列裡：[{"error": {...}}]（design §19.2）
        let errorObject = (object["error"] ?? (parsed as? [[String: Any]])?.first?["error"]) as? [String: Any]
        let message = (errorObject?["message"] as? String) ?? String(decoding: data.prefix(300), as: UTF8.self)
        return Response(status: status, object: object, message: message)
    }

    private static func isNoCredit(_ response: Response) -> Bool {
        let message = response.message.lowercased()
        return response.status == 429 && ["credit", "quota", "billing"].contains { message.contains($0) }
    }

    static func error(_ response: Response, provider: String) -> LLMError {
        let status = response.status
        // Gemini 的 key 錯誤是 HTTP 400 加上「API key not valid」
        let keyProblem = status == 401 || (status == 400 && response.message.localizedCaseInsensitiveContains("API key"))
        // 429 有兩種：額度用完（等多久都不會好，要加值）、短時間內用太多
        let noCredit = isNoCredit(response)
        let hint = keyProblem ? String(localized: "（API key 錯誤或已失效）")
            : noCredit ? String(localized: "（帳戶沒有額度，要到服務的網站加值）")
            : status == 429 ? String(localized: "（超過用量限制，請稍後再試）") : ""
        let message = String(localized: "\(provider) 回傳 HTTP \(status)\(hint)：\(response.message)")
        // 設定問題和用量上限分開（design §34.4）：key 錯誤或沒權限要改設定，429 等一下或加值
        if keyProblem || status == 403 { return LLMError.rejected(message) }
        if status == 429 { return LLMError.limited(message) }
        return LLMError.failed(message)
    }
}
