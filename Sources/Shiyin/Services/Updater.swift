import AppKit
import Foundation
import Observation
import Security

/// app 內更新（R10.6、design §12、D10）：
/// 讀更新資訊 JSON → 比版本 → 使用者同意後下載 DMG → 驗證 SHA-256 → 取出新 app → 檢查簽章符合目前 app 的
/// designated requirement → app 結束後由小腳本替換並重新開啟
@MainActor
@Observable
final class Updater {
    struct Manifest: Codable, Equatable {
        var version: String
        var url: String
        var sha256: String
        var notes: String?
    }

    enum State: Equatable {
        case idle, checking, upToDate, available(Manifest), downloading, installing, failed(String)

        var label: String {
            switch self {
            case .idle: return ""
            case .checking: return String(localized: "正在檢查…")
            case .upToDate: return String(localized: "已是最新版本")
            case .available(let m): return String(localized: "有新版本 \(m.version)")
            case .downloading: return String(localized: "正在下載更新…")
            case .installing: return String(localized: "正在準備安裝…")
            case .failed(let reason): return reason
            }
        }
    }

    enum UpdateError: LocalizedError {
        case noURL, insecureURL, http(Int), badManifest, checksum, noApp, signature(String), notNewer, busy

        var errorDescription: String? {
            switch self {
            case .noURL: return String(localized: "還沒有設定更新網址")
            case .insecureURL: return String(localized: "更新網址必須是 https")
            case .http(let code): return String(localized: "更新網址回傳 HTTP \(code)")
            case .badManifest: return String(localized: "更新資訊的格式不對")
            case .checksum: return String(localized: "下載的檔案驗證失敗（SHA-256 不符），已刪除")
            case .noApp: return String(localized: "更新檔裡找不到拾音")
            case .signature(let reason): return String(localized: "新版的簽章跟目前的拾音不符，拒絕安裝（\(reason)）")
            case .notNewer: return String(localized: "更新檔的版本沒有比較新")
            case .busy: return String(localized: "錄音或轉錄、整理進行中，完成後再更新")
            }
        }
    }

    static let urlKey = "updateURL"
    private static let lastCheckedKey = "updateLastChecked"

    var state: State = .idle
    /// 有新版時要問使用者（主視窗會跳出對話框）
    var prompt: Manifest?

    static var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    /// 設定裡填的網址優先，沒填就用建置時寫進 Info.plist 的
    static var feedURLString: String {
        let custom = UserDefaults.standard.string(forKey: urlKey)?.trimmingCharacters(in: .whitespaces) ?? ""
        if !custom.isEmpty { return custom }
        return (Bundle.main.object(forInfoDictionaryKey: "ShiyinUpdateURL") as? String ?? "").trimmingCharacters(in: .whitespaces)
    }

    /// 只接受 https；localhost、127.0.0.1 例外，給測試用
    static func validated(_ url: URL?) throws -> URL {
        guard let url, let scheme = url.scheme?.lowercased() else { throw UpdateError.noURL }
        if scheme == "https" { return url }
        if scheme == "http", ["localhost", "127.0.0.1"].contains(url.host?.lowercased() ?? "") { return url }
        throw UpdateError.insecureURL
    }

    /// 版本號依數字逐段比較，例如 3.0.10 比 3.0.9 新
    static func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // MARK: 檢查

    /// 啟動 30 秒後檢查一次，之後每小時看看距離上次檢查是否超過 24 小時
    func scheduleAutomaticChecks() {
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(30))
            while let self {
                let last = UserDefaults.standard.object(forKey: Self.lastCheckedKey) as? Date ?? .distantPast
                if !Self.feedURLString.isEmpty, Date().timeIntervalSince(last) > 24 * 3600 {
                    await self.check(userInitiated: false)
                }
                try? await Task.sleep(for: .seconds(3600))
            }
        }
    }

    func check(userInitiated: Bool) async {
        if case .downloading = state { return }
        if case .installing = state { return }
        do {
            let url = try Self.validated(URL(string: Self.feedURLString))
            state = .checking
            let request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard code == 200 else { throw UpdateError.http(code) }
            guard let manifest = try? JSONDecoder().decode(Manifest.self, from: data) else { throw UpdateError.badManifest }
            UserDefaults.standard.set(Date(), forKey: Self.lastCheckedKey)
            if Self.isNewer(manifest.version, than: Self.currentVersion) {
                state = .available(manifest)
                prompt = manifest
            } else {
                state = .upToDate
            }
        } catch UpdateError.noURL where !userInitiated {
            state = .idle
        } catch {
            // 自動檢查失敗不跳視窗打擾使用者，只顯示在設定裡
            state = .failed(error.localizedDescription)
            log.error("update check failed: \(error.localizedDescription, privacy: .public)")
        }
    }

    // MARK: 安裝

    func install(_ manifest: Manifest, model: AppModel) async {
        var workDir: URL?
        do {
            guard !model.recorder.isRecording, !model.runner.isBusy else { throw UpdateError.busy }
            guard Self.isNewer(manifest.version, than: Self.currentVersion) else { throw UpdateError.notNewer }
            let feed = try Self.validated(URL(string: Self.feedURLString))
            let dmgURL = try Self.validated(URL(string: manifest.url, relativeTo: feed)?.absoluteURL)

            state = .downloading
            let work = try Paths.makeTempDirectory()
            workDir = work
            let (downloaded, response) = try await URLSession.shared.download(from: dmgURL)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard code == 200 else { throw UpdateError.http(code) }
            let dmg = work.appendingPathComponent("update.dmg")
            try FileManager.default.moveItem(at: downloaded, to: dmg)
            let hash = try await Task.detached { try ModelStore.sha256(dmg) }.value
            guard hash == manifest.sha256.lowercased() else { throw UpdateError.checksum }

            state = .installing
            let staged = try await Self.extractApp(from: dmg, into: work)
            try Self.checkSignature(staged)
            let stagedVersion = Bundle(url: staged)?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
            guard Self.isNewer(stagedVersion, than: Self.currentVersion) else { throw UpdateError.notNewer }

            let target = Bundle.main.bundleURL
            guard FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path) else {
                // 一般使用者帳號不能改 /Applications：打開 DMG 讓使用者自己拖
                NSWorkspace.shared.open(dmg)
                state = .failed(String(localized: "沒有權限替換「\(target.deletingLastPathComponent().path)」裡的拾音，已經打開下載好的 DMG，請把拾音拖進「應用程式」取代舊版"))
                return
            }
            // 下載期間可能開始了錄音或轉錄：結束 app 時會跳確認，取消的話更新就停在一半，所以先擋下來
            guard !model.recorder.isRecording, !model.runner.isBusy else { throw UpdateError.busy }
            let replacement = try Self.launchReplacement(staged: staged, target: target, work: work)
            NSApp.terminate(nil)
            // 走到這裡表示結束被取消了：停掉替換腳本、清掉下載的檔案，舊版不受影響，可以再按一次
            replacement.terminate()
            try? FileManager.default.removeItem(at: work)
            state = .failed(String(localized: "已取消更新，舊版沒有改動"))
        } catch {
            // 失敗時把下載和解出來的檔案都刪掉，舊版完全不受影響
            if let workDir { try? FileManager.default.removeItem(at: workDir) }
            state = .failed(error.localizedDescription)
            model.errorMessage = String(localized: "更新失敗：\(error.localizedDescription)")
        }
    }

    /// 唯讀掛載 DMG，把裡面的拾音複製出來
    private static func extractApp(from dmg: URL, into work: URL) async throws -> URL {
        let mount = work.appendingPathComponent("mnt", isDirectory: true)
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: true)
        let attach = try await Subprocess(URL(fileURLWithPath: "/usr/bin/hdiutil"),
                                          ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, dmg.path])
            .run(timeout: 120)
        guard attach.succeeded else { throw UpdateError.noApp }
        defer {
            let detach = Process()
            detach.executableURL = URL(fileURLWithPath: "/usr/bin/hdiutil")
            detach.arguments = ["detach", mount.path, "-force"]
            try? detach.run()
            detach.waitUntilExit()
        }
        let items = (try? FileManager.default.contentsOfDirectory(at: mount, includingPropertiesForKeys: nil)) ?? []
        guard let app = items.first(where: { $0.pathExtension == "app" && Bundle(url: $0)?.bundleIdentifier == Bundle.main.bundleIdentifier })
        else { throw UpdateError.noApp }
        let staged = work.appendingPathComponent("Shiyin.app", isDirectory: true)
        let copy = try await Subprocess(URL(fileURLWithPath: "/usr/bin/ditto"), [app.path, staged.path]).run(timeout: 300)
        guard copy.succeeded else { throw UpdateError.noApp }
        return staged
    }

    /// 新 app 的簽章要有效，而且符合目前這個 app 的 designated requirement（自簽憑證時等於同一張憑證）
    static func checkSignature(_ app: URL) throws {
        var selfCode: SecCode?
        var selfStatic: SecStaticCode?
        var requirement: SecRequirement?
        guard SecCodeCopySelf([], &selfCode) == errSecSuccess, let selfCode,
              SecCodeCopyStaticCode(selfCode, [], &selfStatic) == errSecSuccess, let selfStatic,
              SecCodeCopyDesignatedRequirement(selfStatic, [], &requirement) == errSecSuccess, let requirement
        else { throw UpdateError.signature(String(localized: "讀不到目前 app 的簽章")) }
        var newStatic: SecStaticCode?
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &newStatic) == errSecSuccess, let newStatic else {
            throw UpdateError.signature(String(localized: "讀不到新版的簽章"))
        }
        let flags = SecCSFlags(rawValue: UInt32(kSecCSCheckAllArchitectures | kSecCSCheckNestedCode | kSecCSStrictValidate))
        let status = SecStaticCodeCheckValidity(newStatic, flags, requirement)
        guard status == errSecSuccess else {
            throw UpdateError.signature(SecCopyErrorMessageString(status, nil) as String? ?? String(localized: "錯誤代碼 \(status)"))
        }
    }

    /// 寫一個固定內容的小腳本，路徑用參數傳入：等這個 app 結束 → 舊版改名備份 → 放進新版 → 成功刪備份、失敗還原 → 重新開啟
    @discardableResult
    private static func launchReplacement(staged: URL, target: URL, work: URL) throws -> Process {
        let script = work.appendingPathComponent("replace.sh")
        try """
        #!/bin/sh
        pid="$1"; new="$2"; target="$3"; work="$4"; backup="$3.updating"
        i=0
        while kill -0 "$pid" 2>/dev/null; do
          i=$((i + 1)); [ "$i" -gt 600 ] && exit 1
          sleep 0.1
        done
        rm -rf "$backup"
        if mv "$target" "$backup"; then
          if /usr/bin/ditto "$new" "$target"; then
            rm -rf "$backup"
          else
            rm -rf "$target"; mv "$backup" "$target"
          fi
        fi
        rm -rf "$work"
        /usr/bin/open "$target"
        """.write(to: script, atomically: true, encoding: .utf8)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [script.path, String(ProcessInfo.processInfo.processIdentifier), staged.path, target.path, work.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        return process
    }
}
