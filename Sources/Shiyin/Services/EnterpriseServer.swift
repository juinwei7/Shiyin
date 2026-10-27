import CryptoKit
import Foundation

/// 企業伺服器（design §36、R49～R52）：網址、登入（伺服器的登入頁＋OAuth 授權碼＋PKCE，D121）、換 token、呼叫 API。
/// 網址和帳號（信箱、名字、伺服器名稱，不是密鑰）存 UserDefaults，有帳號就算登入了；refresh token 只存在鑰匙圈（R49.6），
/// 第一次真的要呼叫伺服器時才讀（§22.1：不在啟動、打開設定頁時讀）；access token 只放記憶體
@MainActor
@Observable
final class EnterpriseServer {
    static let shared = EnterpriseServer()

    static let urlKey = "enterpriseServerURL"
    static let accountKey = "enterpriseServerAccount"
    static let keychainAccount = "enterpriseServer"
    /// 這版 app 說的 API 版本（M§36.2）
    static let apiVersion = 1
    nonisolated static let callbackScheme = "shiyin"
    nonisolated static let redirectURI = "shiyin://auth"

    /// 伺服器的 /api/v1/info
    struct Info: Codable, Equatable {
        struct Transcription: Codable, Equatable {
            var engine: String
            var languages: [String]
        }
        var server: String
        var apiVersion: Int
        var minAppApi: Int
        var name: String
        var version: String
        var maxAudioSeconds: Int?
        var transcription: Transcription
        var llm: Bool
    }

    /// 登入的帳號（不含密鑰）
    struct Account: Codable, Equatable {
        var email: String
        var name: String
        var serverName: String
        var engine: String
        var llm: Bool
    }

    enum Status: Equatable {
        case signedOut
        case signedIn
        /// 登入失效（帳號被停用、離職、撤銷、30 天沒用）：用到伺服器的工作停著等重新登入（R49.4）
        case expired(String)
    }

    private(set) var account: Account?
    private(set) var status: Status = .signedOut {
        // 登入、失效、重新登入：整理工具「準備好了沒」要重算（R51.3；只看帳號的話，登入完成前算的會留著「登入已失效」）
        didSet { if status != oldValue { NotificationCenter.default.post(name: .summarizerStateChanged, object: nil) } }
    }

    @ObservationIgnored private var accessToken: String?
    @ObservationIgnored private var accessExpires = Date.distantPast
    @ObservationIgnored private var refreshing: Task<String, Error>?

    private init() {
        if let data = UserDefaults.standard.data(forKey: Self.accountKey), let saved = try? JSONDecoder().decode(Account.self, from: data) {
            account = saved
            status = .signedIn
        }
    }

    var isSignedIn: Bool { account != nil && status == .signedIn }
    /// 有登入過（失效了也算）：設定頁顯示帳號、正式轉錄的選項照樣出現
    var hasAccount: Bool { account != nil }

    var savedURL: URL? { UserDefaults.standard.string(forKey: Self.urlKey).flatMap { Self.normalized($0) } }
    /// 隱私說明用的主機名稱（design §36.5）
    var host: String { savedURL?.host() ?? "" }

    // MARK: 網址

    /// 只接受 https；DEBUG 版可以用本機的 http（R49.2）。去掉結尾的 /，貼了 /api/v1/info 之類的也去掉
    nonisolated static func normalized(_ text: String) -> URL? {
        var value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if !value.contains("://") { value = "https://" + value }
        while value.hasSuffix("/") { value.removeLast() }
        for suffix in ["/api/v1/info", "/api/v1", "/admin", "/login"] where value.hasSuffix(suffix) { value.removeLast(suffix.count) }
        guard let url = URL(string: value), let host = url.host(), !host.isEmpty, let scheme = url.scheme?.lowercased() else { return nil }
        if scheme == "https" { return url }
        #if DEBUG
        if scheme == "http" && ["127.0.0.1", "localhost", "::1"].contains(host) { return url }
        #endif
        return nil
    }

    enum Failure: LocalizedError, Equatable {
        case invalidURL
        case notShiyin
        case serverTooOld
        case appTooOld
        case unreachable(String)
        case untrusted
        case loginExpired
        case forbidden(String)
        case denied(String)
        case server(kind: String, message: String)
        case cancelled

        var errorDescription: String? {
            switch self {
            case .invalidURL: return String(localized: "網址不對：要以 https:// 開頭，例如 https://shiyin.example.com")
            case .notShiyin: return String(localized: "這個網址不是拾音的企業伺服器")
            case .serverTooOld: return String(localized: "企業伺服器的版本太舊，請通知公司的管理者更新")
            case .appTooOld: return String(localized: "拾音的版本太舊，請先更新拾音")
            case .unreachable(let detail): return String(localized: "連不到企業伺服器（\(detail)）。請確認網址，在公司外面要先連 VPN。")
            case .untrusted: return String(localized: "企業伺服器的憑證不受這台 Mac 信任，請公司的管理者協助安裝憑證")
            case .loginExpired: return String(localized: "企業伺服器的登入已失效，請重新登入")
            case .forbidden(let message), .denied(let message): return message
            case .server(_, let message): return message
            case .cancelled: return String(localized: "已取消")
            }
        }

        /// 設定問題（登入失效、帳號被停用）：工作停著等，不算失敗（R49.4、R51.3）
        var isSetupProblem: Bool {
            switch self {
            case .loginExpired, .forbidden, .denied, .notShiyin, .serverTooOld, .appTooOld, .invalidURL, .untrusted: return true
            default: return false
            }
        }

        /// 暫時的問題：連不上、伺服器忙、5xx（R50.5：等著、自動再試）
        var isTemporary: Bool {
            switch self {
            case .unreachable: return true
            case .server(let kind, _): return kind == "busy" || kind == "server_error"
            default: return false
            }
        }
    }

    /// 確認那是拾音的伺服器、版本相容（R49.1、R49.2）
    func fetchInfo(_ url: URL) async throws -> Info {
        var request = URLRequest(url: url.appending(path: "api/v1/info"))
        request.timeoutInterval = 15
        Self.addLanguage(&request)
        let (data, response) = try await Self.send(request)
        guard response.statusCode == 200, let info = try? JSONDecoder().decode(Info.self, from: data), info.server == "shiyin" else {
            throw Failure.notShiyin
        }
        if info.apiVersion < Self.apiVersion { throw Failure.serverTooOld }
        if info.minAppApi > Self.apiVersion { throw Failure.appTooOld }
        return info
    }

    /// 重新讀伺服器的資訊（R51.5、design §36.3）：公司在後台加了模型、改了名稱或轉錄引擎；連不上就保留舊的。不讀鑰匙圈
    func refreshInfo() async {
        guard let url = savedURL, let before = account, let info = try? await fetchInfo(url) else { return }
        // 讀的時候登出、換了帳號：不要蓋掉
        guard let current = account, current.email == before.email else { return }
        var updated = current
        updated.serverName = info.name
        updated.engine = info.transcription.engine
        updated.llm = info.llm
        if updated != current {
            save(updated)
            NotificationCenter.default.post(name: .summarizerStateChanged, object: nil)
        }
    }

    // MARK: 登入（M§36.2）

    /// 一次登入：PKCE 的 verifier 和 state 只放記憶體
    struct Attempt {
        let base: URL
        let info: Info
        let verifier: String
        let state: String

        var loginURL: URL {
            let challenge = Self.base64URL(Data(SHA256.hash(data: Data(verifier.utf8))))
            var components = URLComponents(url: base.appending(path: "login"), resolvingAgainstBaseURL: false)!
            components.queryItems = [
                URLQueryItem(name: "client_id", value: "shiyin-app"),
                URLQueryItem(name: "redirect_uri", value: EnterpriseServer.redirectURI),
                URLQueryItem(name: "state", value: state),
                URLQueryItem(name: "code_challenge", value: challenge),
                URLQueryItem(name: "code_challenge_method", value: "S256"),
            ]
            return components.url!
        }

        static func base64URL(_ data: Data) -> String {
            data.base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
                .replacingOccurrences(of: "=", with: "")
        }

        static func random() -> String {
            var bytes = [UInt8](repeating: 0, count: 32)
            _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
            return base64URL(Data(bytes))
        }
    }

    /// 第一步：檢查網址，準備好登入頁的網址（設定頁再用 ASWebAuthenticationSession 打開）
    func beginSignIn(_ text: String) async throws -> Attempt {
        guard let url = Self.normalized(text) else { throw Failure.invalidURL }
        let info = try await fetchInfo(url)
        return Attempt(base: url, info: info, verifier: Attempt.random() + Attempt.random(), state: Attempt.random())
    }

    /// 第二步：登入頁轉回 shiyin://auth?code=…&state=…，用授權碼加 PKCE 換 token
    func completeSignIn(_ attempt: Attempt, callback: URL) async throws {
        let items = URLComponents(url: callback, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        // state 不是這次發的（網頁裡的連結、舊的登入）就忽略
        guard value("state") == attempt.state else { throw Failure.cancelled }
        if value("error") != nil {
            throw Failure.denied(value("error_description") ?? String(localized: "企業伺服器拒絕了這次登入"))
        }
        guard let code = value("code") else { throw Failure.cancelled }
        let tokens = try await tokenRequest(attempt.base, [
            "grant_type": "authorization_code", "code": code, "code_verifier": attempt.verifier,
            "device": Host.current().localizedName ?? "Mac", "platform": "mac", "appVersion": Updater.currentVersion,
        ])
        try Keychain.set(Self.keychainAccount, tokens.refreshToken)
        UserDefaults.standard.set(attempt.base.absoluteString, forKey: Self.urlKey)
        let account = Account(email: tokens.email, name: tokens.name, serverName: attempt.info.name,
                              engine: attempt.info.transcription.engine, llm: attempt.info.llm)
        save(account)
        accessToken = tokens.accessToken
        accessExpires = Date().addingTimeInterval(TimeInterval(tokens.expiresIn) - 60)
        status = .signedIn
        log.info("enterprise server signed in")
    }

    /// 登出（R49.5）：伺服器那邊撤銷這台電腦（送不出去也照樣清掉本機的），轉錄、整理改回本機的
    func signOut() async {
        if let url = savedURL, let token = try? await validAccessToken() {
            var request = URLRequest(url: url.appending(path: "api/v1/auth/logout"))
            request.httpMethod = "POST"
            request.timeoutInterval = 10
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            _ = try? await Self.send(request)
        }
        forgetLogin()
        UserDefaults.standard.removeObject(forKey: Self.accountKey)
        account = nil
        status = .signedOut
        // 只清掉存下來的值是企業伺服器的設定（啟動參數帶的值不算：曾經因此刪掉使用者自己選的整理工具）
        let saved = Bundle.main.bundleIdentifier.flatMap { UserDefaults.standard.persistentDomain(forName: $0) } ?? [:]
        if saved[AppSettings.transcriptionEngineKey] as? String == "server" {
            UserDefaults.standard.removeObject(forKey: AppSettings.transcriptionEngineKey)
        }
        if saved[AppSettings.providerKey] as? String == LLMProvider.enterprise.rawValue {
            UserDefaults.standard.removeObject(forKey: AppSettings.providerKey)
        }
        log.info("enterprise server signed out")
    }

    private func forgetLogin() {
        Keychain.delete(Self.keychainAccount)
        accessToken = nil
        accessExpires = .distantPast
    }

    private func save(_ account: Account) {
        self.account = account
        if let data = try? JSONEncoder().encode(account) { UserDefaults.standard.set(data, forKey: Self.accountKey) }
    }

    private struct TokenResponse {
        var accessToken: String
        var expiresIn: Int
        var refreshToken: String
        var email: String
        var name: String
    }

    private func tokenRequest(_ base: URL, _ body: [String: String]) async throws -> TokenResponse {
        var request = URLRequest(url: base.appending(path: "api/v1/auth/token"))
        request.httpMethod = "POST"
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        Self.addLanguage(&request)
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await Self.send(request)
        guard response.statusCode == 200 else { throw Self.failure(data, response) }
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let access = json["access_token"] as? String, let refresh = json["refresh_token"] as? String else {
            throw Failure.notShiyin
        }
        let user = json["user"] as? [String: Any]
        return TokenResponse(accessToken: access, expiresIn: json["expires_in"] as? Int ?? 3600, refreshToken: refresh,
                             email: user?["email"] as? String ?? "", name: user?["name"] as? String ?? "")
    }

    /// 還有效的 access token；過期了用 refresh token 換（同時只換一次：refresh token 換過就作廢，design §5.2）
    func validAccessToken() async throws -> String {
        guard account != nil, let base = savedURL else { throw Failure.loginExpired }
        if let accessToken, accessExpires > Date() { return accessToken }
        if let refreshing { return try await refreshing.value }
        let task = Task { @MainActor () throws -> String in
            defer { self.refreshing = nil }
            // 鑰匙圈讀不到（沒有、使用者拒絕存取）：當成要重新登入，但不刪鑰匙圈的項目
            guard let refresh = try? Keychain.read(Self.keychainAccount), !refresh.isEmpty else {
                self.status = .expired(Failure.loginExpired.errorDescription ?? "")
                throw Failure.loginExpired
            }
            do {
                let tokens = try await self.tokenRequest(base, ["grant_type": "refresh_token", "refresh_token": refresh,
                                                                "appVersion": Updater.currentVersion])
                try Keychain.set(Self.keychainAccount, tokens.refreshToken)
                self.accessToken = tokens.accessToken
                self.accessExpires = Date().addingTimeInterval(TimeInterval(tokens.expiresIn) - 60)
                if self.status != .signedIn { self.status = .signedIn }
                return tokens.accessToken
            } catch let failure as Failure {
                // 伺服器說登入失效、帳號被停用：清掉這台的登入，等使用者重新登入（R49.4）
                if failure == .loginExpired { self.markExpired(nil) }
                else if case .forbidden(let message) = failure { self.markExpired(message) }
                throw failure
            }
        }
        refreshing = task
        return try await task.value
    }

    /// 伺服器說 access token 不能用了（整理的請求回 401）：丟掉，下次用 refresh token 換新的
    func invalidateAccess() {
        accessToken = nil
        accessExpires = .distantPast
    }

    private func markExpired(_ message: String?) {
        forgetLogin()
        status = .expired(message ?? Failure.loginExpired.errorDescription ?? "")
        log.info("enterprise server login expired")
    }

    // MARK: API

    /// 呼叫 API：帶 access token，401 時換一次 token 再送（R49.4）
    func call(_ method: String, _ path: String, json: Any? = nil, body: Data? = nil, headers: [String: String] = [:],
              timeout: TimeInterval = 60) async throws -> (Data, HTTPURLResponse) {
        guard let base = savedURL else { throw Failure.loginExpired }
        for attempt in 0..<2 {
            let token = try await validAccessToken()
            var request = URLRequest(url: base.appending(path: path))
            request.httpMethod = method
            request.timeoutInterval = timeout
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
            Self.addLanguage(&request)
            for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
            if let json {
                request.setValue("application/json", forHTTPHeaderField: "Content-Type")
                request.httpBody = try JSONSerialization.data(withJSONObject: json)
            } else if let body {
                request.httpBody = body
            }
            let (data, response) = try await Self.send(request)
            if response.statusCode == 401 && attempt == 0 {
                accessToken = nil
                accessExpires = .distantPast
                continue
            }
            if response.statusCode == 401 { markExpired(nil); throw Failure.loginExpired }
            if response.statusCode == 403 {
                let failure = Self.failure(data, response)
                markExpired(failure.errorDescription)
                throw failure
            }
            return (data, response)
        }
        throw Failure.loginExpired
    }

    /// 送出請求；連不上、逾時、憑證不受信任換成 Failure
    nonisolated static func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else { throw Failure.notShiyin }
            return (data, http)
        } catch let error as URLError {
            switch error.code {
            case .cancelled: throw CancellationError()
            case .serverCertificateUntrusted, .serverCertificateHasBadDate, .serverCertificateHasUnknownRoot, .serverCertificateNotYetValid,
                 .secureConnectionFailed, .clientCertificateRejected:
                throw Failure.untrusted
            case .appTransportSecurityRequiresSecureConnection: throw Failure.invalidURL
            default: throw Failure.unreachable(error.localizedDescription)
            }
        }
    }

    /// 伺服器的錯誤 {"error": {"kind", "message"}}（M§36.2）
    nonisolated static func failure(_ data: Data, _ response: HTTPURLResponse) -> Failure {
        let error = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? [String: Any]
        let kind = error?["kind"] as? String ?? (response.statusCode >= 500 ? "server_error" : "bad_request")
        let message = error?["message"] as? String ?? String(localized: "企業伺服器回傳錯誤（HTTP \(response.statusCode)）")
        switch (response.statusCode, kind) {
        case (401, _), (_, "unauthorized"): return .loginExpired
        case (403, _), (_, "forbidden"): return .forbidden(message)
        default: return .server(kind: kind, message: message)
        }
    }

    /// 伺服器給的訊息用 app 的介面語言（M§36.2）
    nonisolated static func addLanguage(_ request: inout URLRequest) {
        request.setValue(MeetingLanguage.ui(zh: "zh-TW", en: "en"), forHTTPHeaderField: "Accept-Language")
    }

    // MARK: DEBUG

    #if DEBUG
    /// shiyinctl enterpriselogin：伺服器開發用的登入（design §11.1 的 devLogin）不經過瀏覽器，直接送出信箱，走一樣的 completeSignIn
    func debugDevSignIn(_ text: String, email: String) async throws {
        let attempt = try await beginSignIn(text)
        _ = try await Self.send(URLRequest(url: attempt.loginURL))
        var request = URLRequest(url: attempt.base.appending(path: "login/dev"))
        request.httpMethod = "POST"
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        let challenge = URLComponents(url: attempt.loginURL, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "code_challenge" }?.value ?? ""
        var form = URLComponents()
        form.queryItems = [URLQueryItem(name: "email", value: email), URLQueryItem(name: "state", value: attempt.state),
                           URLQueryItem(name: "challenge", value: challenge)]
        request.httpBody = Data((form.percentEncodedQuery ?? "").replacingOccurrences(of: "+", with: "%2B").utf8)
        let (data, _) = try await Self.send(request)
        let html = String(decoding: data, as: UTF8.self)
        guard let range = html.range(of: #"window\.location\.replace\("([^"]+)"\)"#, options: .regularExpression) else { throw Failure.notShiyin }
        let quoted = String(html[range]).replacingOccurrences(of: "window.location.replace(", with: "").dropLast()
        let callback = try JSONDecoder().decode(String.self, from: Data(quoted.utf8))
        try await completeSignIn(attempt, callback: URL(string: callback)!)
    }

    /// 測試用：讓 access token 立刻過期（下一次呼叫會換新的）
    func debugExpireAccess() { accessExpires = .distantPast }

    var debugState: [String: Any] {
        var state: [String: Any] = ["url": savedURL?.absoluteString ?? "", "hasAccount": account != nil]
        switch status {
        case .signedOut: state["status"] = "signedOut"
        case .signedIn: state["status"] = "signedIn"
        case .expired(let message): state["status"] = "expired"; state["message"] = message
        }
        if let account { state["email"] = account.email; state["serverName"] = account.serverName; state["engine"] = account.engine }
        return state
    }
    #endif
}
