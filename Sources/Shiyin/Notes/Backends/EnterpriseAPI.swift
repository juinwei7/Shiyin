import Foundation

/// 企業伺服器的整理（R51、design §36.3、D123）：伺服器轉送 OpenAI 相容格式給公司設定的模型。
/// 提示詞照舊在 app 組好；key 是登入企業伺服器拿到的 access token；模型留空就用公司設定的預設模型
struct EnterpriseAPI: LLMBackend {
    let model: String

    private var inner: OpenAICompatibleAPI {
        OpenAICompatibleAPI(model: model, baseURL: EnterpriseServer.shared.savedURL?.appending(path: "api/v1/llm").absoluteString ?? "",
                            key: { try await EnterpriseServer.shared.validAccessToken() }, requiresModel: false,
                            name: String(localized: "企業伺服器"))
    }

    func generateJSON(system: String, prompt: String, format: LLMClient.OutputFormat,
                      onStart: (Subprocess) -> Void) async throws -> Data {
        guard EnterpriseServer.shared.hasAccount else { throw LLMError.notConfigured(SummarizerProblem.enterpriseSignedOut.message) }
        do {
            do {
                return try await inner.generateJSON(system: system, prompt: prompt, format: format, onStart: onStart)
            } catch LLMError.rejected {
                // access token 被撤銷或過期（401、403）：換一次 token 再送；換不到就是登入失效、帳號被停用（R49.4）
                EnterpriseServer.shared.invalidateAccess()
                _ = try await EnterpriseServer.shared.validAccessToken()
                return try await inner.generateJSON(system: system, prompt: prompt, format: format, onStart: onStart)
            }
        } catch let failure as EnterpriseServer.Failure {
            // 登入失效、帳號被停用：設定問題，暫停自動整理到重新登入（R51.3）
            throw LLMError.notConfigured(failure.errorDescription ?? SummarizerProblem.enterpriseSignedOut.message)
        } catch LLMError.rejected {
            throw LLMError.notConfigured(EnterpriseServer.Failure.loginExpired.errorDescription ?? "")
        }
    }

    /// 伺服器提供的模型（GET /api/v1/llm/models）
    func listModels() async -> [String] {
        guard let (data, response) = try? await EnterpriseServer.shared.call("GET", "api/v1/llm/models", timeout: 15), response.statusCode == 200,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return [] }
        return list.compactMap { $0["id"] as? String }
    }
}
