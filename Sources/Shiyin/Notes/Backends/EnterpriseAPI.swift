import Foundation

/// 企業伺服器的整理（R51、design §36.3、D123）：伺服器轉送 OpenAI 相容格式給公司設定的模型。
/// 提示詞照舊在 app 組好；key 是登入企業伺服器拿到的 access token；模型留空就用公司設定的預設模型
struct EnterpriseAPI: LLMBackend {
    let model: String

    private var inner: OpenAICompatibleAPI {
        let base = EnterpriseServer.shared.savedURL
        return OpenAICompatibleAPI(model: model, baseURL: base?.appending(path: "api/v1/llm").absoluteString ?? "",
                                   key: {
                                       let token = try await EnterpriseServer.shared.validAccessToken()
                                       // 等 token 的時候換了伺服器（加入連結）：新伺服器的 token 不能送到舊的伺服器
                                       guard let saved = EnterpriseServer.shared.savedURL, let base, EnterpriseServer.sameServer(saved, base) else {
                                           throw EnterpriseServer.Failure.loginExpired
                                       }
                                       return token
                                   }, requiresModel: false,
                                   name: String(localized: "公司的伺服器"), plainServerErrors: true, session: EnterpriseServer.session)
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
        await Self.models()?.map(\.id) ?? []
    }

    /// 公司在後台設定的模型（R51.1）：id 給伺服器、name 給人看；取不到是 nil
    struct Model: Identifiable, Equatable {
        let id: String
        let name: String
        let isDefault: Bool
    }

    static func models() async -> [Model]? {
        guard let (data, response) = try? await EnterpriseServer.shared.call("GET", "api/v1/llm/models", timeout: 15), response.statusCode == 200,
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let list = object["data"] as? [[String: Any]] else { return nil }
        return list.compactMap { item in
            guard let id = item["id"] as? String else { return nil }
            let name = (item["name"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? id
            return Model(id: id, name: name, isDefault: item["default"] as? Bool ?? false)
        }
    }
}
