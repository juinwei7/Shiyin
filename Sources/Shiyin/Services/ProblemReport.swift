import AppKit
import Foundation
import GRDB

/// 回報問題（design §21.9、R19.7、D33）：組出要附上的環境資訊，打開瀏覽器的 GitHub 新 issue 頁面讓使用者自己送出。
/// 不帶會議內容、檔名、API key、模型名稱、網址；錯誤只寫種類（jobs.error_kind），不讀 jobs.error（可能含逐字稿）
@MainActor
enum ProblemReport {
    static let defaultIssuesURL = "https://github.com/juinwei7/Shiyin/issues"

    /// Info.plist 的 ShiyinIssuesURL（fork 的人可以改），空的用預設
    static var issuesURL: String {
        let value = (Bundle.main.object(forInfoDictionaryKey: "ShiyinIssuesURL") as? String)?.trimmingCharacters(in: .whitespaces) ?? ""
        return value.isEmpty ? defaultIssuesURL : value
    }

    static func text(db: AppDatabase) -> String {
        let info = Bundle.main.infoDictionary ?? [:]
        let version = info["CFBundleShortVersionString"] as? String ?? "?"
        let build = info["CFBundleVersion"] as? String ?? "?"
        let os = ProcessInfo.processInfo.operatingSystemVersion
        let memory = ByteCountFormatter.string(fromByteCount: Int64(ProcessInfo.processInfo.physicalMemory), countStyle: .memory)
        let on = String(localized: "開"), off = String(localized: "關")
        var lines: [String] = []
        lines.append(String(localized: "## 問題描述"))
        lines.append(String(localized: "（請寫發生了什麼事、怎麼重現。不要貼會議內容）"))
        lines.append("")
        lines.append(String(localized: "## 環境"))
        lines.append(String(localized: "- 拾音 \(version)（build \(build)）"))
        let osVersion = "\(os.majorVersion).\(os.minorVersion).\(os.patchVersion)"
        lines.append(String(localized: "- macOS \(osVersion)（\(sysctl("kern.osversion") ?? "?")）"))
        lines.append(String(localized: "- \(sysctl("machdep.cpu.brand_string") ?? "Apple Silicon")，記憶體 \(memory)"))
        lines.append(String(localized: "- 介面語言：\(MeetingLanguage.interface.uiName)"))
        let engine = AppSettings.usesEnterpriseTranscription ? String(localized: "企業伺服器")
            : ["system": String(localized: "macOS 內建辨識"), "qwen": "Qwen3-ASR 1.7B"][AppSettings.transcriptionEngine] ?? "Breeze ASR 25"
        lines.append(String(localized: "- 轉錄：\(engine)；說話者辨識：\(AppSettings.diarize ? on : off)；聲紋：\(AppSettings.voiceprints ? on : off)"))
        lines.append(String(localized: "- 整理工具：\(AppSettings.provider.label)"))
        // 企業伺服器（R52.3）：有沒有連、伺服器的版本；不寫網址和帳號
        let enterprise = EnterpriseServer.shared
        if let account = enterprise.account {
            let state = enterprise.isSignedIn ? String(localized: "已登入") : String(localized: "登入已失效")
            lines.append(String(localized: "- 企業伺服器：\(state)（伺服器 \(account.serverVersion ?? "?")）"))
        }
        lines.append("")
        lines.append(String(localized: "## 最近失敗的工作"))
        let failures = (try? db.recentFailures(limit: 5)) ?? []
        if failures.isEmpty {
            lines.append(String(localized: "- 沒有"))
        } else {
            for failure in failures {
                let errorKind = failure.errorKind ?? String(localized: "（沒有記錄種類）")
                lines.append(String(localized: "- \(failure.date) \(failure.kind)：\(errorKind)"))
            }
        }
        return lines.joined(separator: "\n")
    }

    static func url(body: String) -> URL? {
        var components = URLComponents(string: issuesURL.hasSuffix("/") ? issuesURL + "new" : issuesURL + "/new")
        components?.queryItems = [URLQueryItem(name: "title", value: ""), URLQueryItem(name: "body", value: body)]
        return components?.url
    }

    private static func sysctl(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &buffer, &size, nil, 0) == 0 else { return nil }
        return String(cString: buffer)
    }
}

extension AppDatabase {
    /// 回報問題用：最近失敗的工作的種類、錯誤種類、日期（不讀 error，那裡可能有逐字稿）
    func recentFailures(limit: Int) throws -> [(kind: String, errorKind: String?, date: String)] {
        try pool.read { db in
            try Row.fetchAll(db, sql: "SELECT kind, error_kind, finished_at FROM jobs WHERE status = 'failed' ORDER BY finished_at DESC, id DESC LIMIT ?",
                             arguments: [limit])
                .map { ($0["kind"], $0["error_kind"], String(($0["finished_at"] as String?)?.prefix(10) ?? "")) }
        }
    }
}
