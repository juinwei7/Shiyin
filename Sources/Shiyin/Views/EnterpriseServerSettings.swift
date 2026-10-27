import AuthenticationServices
import SwiftUI

/// 設定 → 進階 → 企業伺服器（R49、design §36.3）：輸入網址、到伺服器的登入頁用公司帳號登入；
/// 登入後顯示帳號、轉錄和整理要不要用企業伺服器（R49.3）、登出
struct EnterpriseServerSettings: View {
    @Environment(AppModel.self) private var model
    @Environment(\.webAuthenticationSession) private var webAuthenticationSession
    @AppStorage(AppSettings.transcriptionEngineKey) private var engine = "breeze"
    @AppStorage(AppSettings.providerKey) private var storedProvider = ""
    @State private var url = ""
    @State private var busy = false
    @State private var error: String?
    /// 登入前的設定：取消勾選時改回去（R49.3）
    @State private var previousEngine: String?
    @State private var previousProvider: String?

    private var server: EnterpriseServer { .shared }

    var body: some View {
        if let account = server.account {
            LabeledContent("伺服器") {
                Text("\(account.serverName)（\(server.host)）").textSelection(.enabled)
            }
            LabeledContent("帳號") {
                HStack(spacing: 8) {
                    Text(account.email).textSelection(.enabled)
                    if server.isSignedIn {
                        Text("已登入").foregroundStyle(Color.celadon)
                    } else {
                        Text("登入已失效").foregroundStyle(.red)
                    }
                }
            }
            if case .expired(let message) = server.status {
                Text(message).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
                Button(busy ? "登入中…" : "重新登入") { Task { await signIn(server.savedURL?.absoluteString ?? "") } }
                    .disabled(busy)
            }
            Toggle("用企業伺服器轉錄", isOn: Binding(
                get: { engine == "server" },
                set: { on in
                    if on { previousEngine = engine == "server" ? previousEngine : engine; engine = "server" }
                    else { engine = previousEngine.flatMap { $0 == "server" ? nil : $0 } ?? "breeze" }
                    model.runner.wake()
                    model.refreshEnvironment()
                }))
            if account.llm {
                Toggle("用企業伺服器整理會議紀錄", isOn: Binding(
                    get: { storedProvider == LLMProvider.enterprise.rawValue },
                    set: { on in
                        if on { previousProvider = storedProvider; storedProvider = LLMProvider.enterprise.rawValue }
                        else { storedProvider = previousProvider.flatMap { $0 == LLMProvider.enterprise.rawValue ? nil : $0 } ?? "" }
                        NotificationCenter.default.post(name: .summarizerStateChanged, object: nil)
                    }))
            }
            Text("轉錄：錄音會送到 \(server.host)（公司的伺服器），轉完就刪。說話者辨識和聲紋在這台 Mac 上做，聲紋、筆記、會議紀錄不會因為轉錄送出。伺服器用的轉錄引擎：\(engineName(account.engine))。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Button("登出", role: .destructive) {
                Task {
                    await server.signOut()
                    model.refreshEnvironment()
                    NotificationCenter.default.post(name: .summarizerStateChanged, object: nil)
                }
            }
        } else {
            TextField("網址", text: $url, prompt: Text(verbatim: "https://shiyin.example.com"))
                .onSubmit { Task { await signIn(url) } }
            HStack {
                Button(busy ? "登入中…" : "登入") { Task { await signIn(url) } }
                    .disabled(url.trimmingCharacters(in: .whitespaces).isEmpty || busy)
                    .buttonStyle(.borderedProminent)
            }
            if let error {
                Text(error).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
            }
            Text("公司有拾音的企業伺服器時，填入公司給的網址、按「登入」，在打開的網頁用公司帳號登入。登入後錄音交給公司的伺服器轉錄，沒有 NVIDIA 顯示卡的電腦也很快；整理也可以用公司提供的模型。")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func engineName(_ engine: String) -> String {
        engine == "qwen" ? String(localized: "Qwen3-ASR（阿里巴巴開源）") : String(localized: "Breeze ASR 25（聯發科）")
    }

    /// 檢查網址 → 打開伺服器的登入頁（ASWebAuthenticationSession，用預設瀏覽器的登入狀態）→ 換 token（M§36.2）
    private func signIn(_ text: String) async {
        busy = true
        error = nil
        defer { busy = false }
        do {
            let attempt = try await server.beginSignIn(text)
            let callback = try await webAuthenticationSession.authenticate(using: attempt.loginURL, callbackURLScheme: EnterpriseServer.callbackScheme,
                                                                           preferredBrowserSession: .shared)
            try await server.completeSignIn(attempt, callback: callback)
            applyDefaults(llm: attempt.info.llm)
        } catch let failure as ASWebAuthenticationSessionError where failure.code == .canceledLogin {
            // 使用者關掉登入視窗
        } catch is CancellationError {
        } catch let failure as EnterpriseServer.Failure where failure == .cancelled {
        } catch {
            self.error = error.localizedDescription
        }
    }

    /// 登入後預設轉錄、整理都用企業伺服器（R49.3），可以再取消勾選改回原本的
    private func applyDefaults(llm: Bool) {
        if engine != "server" { previousEngine = engine }
        if llm && storedProvider != LLMProvider.enterprise.rawValue { previousProvider = storedProvider }
        model.applyEnterpriseDefaults(llm: llm)
    }
}
