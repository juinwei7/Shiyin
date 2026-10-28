import AppKit
import AuthenticationServices

/// 加入連結（R49.7、design §36.2、§36.3）：shiyin://join?server=… → 問要不要加入 → 公司的登入頁 → 套用預設。
/// 不經過設定視窗（SwiftUI 的設定視窗從 app 外面打不開），用 NSAlert 和 AppKit 的 ASWebAuthenticationSession。
/// 連結誰都做得出來：標題寫主機（punycode），伺服器給的名稱只當成「自稱」；登入的是別台時，新的登入成功了才換掉
/// （EnterpriseServer.completeSignIn）
@MainActor
enum EnterpriseJoin {
    static func isJoin(_ url: URL) -> Bool { url.scheme?.lowercased() == "shiyin" && url.host()?.lowercased() == "join" }

    private static var busy = false
    private static var session: ASWebAuthenticationSession?
    private static let anchor = Anchor()

    static func handle(_ url: URL) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        NSApp.activate()
        let text = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "server" }?.value ?? ""
        let server = EnterpriseServer.shared
        guard let target = EnterpriseServer.normalized(text) else {
            return notice(String(localized: "沒辦法加入"), EnterpriseServer.Failure.invalidURL.errorDescription ?? "", style: .warning)
        }
        if server.isSignedIn, let saved = server.savedURL, EnterpriseServer.sameServer(saved, target), let account = server.account {
            return notice(String(localized: "已經加入「\(account.serverName)」了"),
                          String(localized: "這台 Mac 已經用 \(account.email) 登入 \(EnterpriseServer.displayHost(target))。"))
        }
        let attempt: EnterpriseServer.Attempt
        do { attempt = try await server.beginSignIn(text) } catch {
            return notice(String(localized: "沒辦法加入"), error.localizedDescription, style: .warning)
        }
        let host = EnterpriseServer.displayHost(attempt.base)
        let confirm = NSAlert()
        confirm.messageText = String(localized: "加入 \(host) 的企業伺服器？")
        var info = String(localized: "這台伺服器自稱「\(attempt.info.name)」。") + "\n\n"
            + String(localized: "會打開 \(host) 的登入頁，用公司帳號登入。登入後錄音交給公司的伺服器轉錄，整理也用公司設定的模型。")
        if let current = server.account {
            info += "\n\n" + String(localized: "登入成功之後，才會登出目前的「\(current.serverName)」（\(server.host)）；沒有登入成功就維持原本的登入。")
        }
        confirm.informativeText = info
        confirm.addButton(withTitle: String(localized: "加入"))
        confirm.addButton(withTitle: String(localized: "取消"))
        guard confirm.runModal() == .alertFirstButtonReturn else { return }
        do {
            let callback = try await authenticate(attempt.loginURL)
            try await server.completeSignIn(attempt, callback: callback)
            AppModel.shared.applyEnterpriseDefaults(llm: attempt.info.llm)
            notice(String(localized: "已加入「\(attempt.info.name)」"),
                   String(localized: "錄音會交給公司的伺服器轉錄。要改回這台 Mac，到「設定 → 進階 → 企業伺服器」。"))
        } catch let failure as ASWebAuthenticationSessionError where failure.code == .canceledLogin {
        } catch let failure as EnterpriseServer.Failure where failure == .cancelled {
        } catch {
            notice(String(localized: "沒辦法登入"), error.localizedDescription, style: .warning)
        }
    }

    private static func notice(_ title: String, _ message: String, style: NSAlert.Style = .informational) {
        let alert = NSAlert()
        alert.alertStyle = style
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }

    /// 用預設瀏覽器的登入狀態打開登入頁，等 shiyin://auth 回來（同設定頁的登入，§36.3）
    private static func authenticate(_ url: URL) async throws -> URL {
        try await withCheckedThrowingContinuation { continuation in
            let session = ASWebAuthenticationSession(url: url, callback: .customScheme(EnterpriseServer.callbackScheme)) { callback, error in
                if let callback { continuation.resume(returning: callback) }
                else { continuation.resume(throwing: error ?? EnterpriseServer.Failure.cancelled) }
            }
            session.presentationContextProvider = anchor
            self.session = session
            if !session.start() { continuation.resume(throwing: EnterpriseServer.Failure.cancelled) }
        }
    }

    private final class Anchor: NSObject, ASWebAuthenticationPresentationContextProviding {
        func presentationAnchor(for session: ASWebAuthenticationSession) -> ASPresentationAnchor {
            MainActor.assumeIsolated { NSApp.keyWindow ?? NSApp.windows.first { $0.isVisible && $0.canBecomeKey } ?? NSWindow() }
        }
    }
}
