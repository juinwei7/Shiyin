import Foundation

/// 透過企業伺服器分享會議（R125、design §99）：M§99.2 的端點、上傳和下載的續傳、伺服器回應的檢查。
/// 名單、連結、期限只存在伺服器（D212），app 只記「這場分享過」的 id（meeting_shares）
@MainActor
enum EnterpriseShares {
    /// info 的 sharing（M§99.2）：關著、舊的伺服器沒有這段
    struct Sharing: Codable, Equatable {
        var enabled: Bool
        var publicLinks: Bool
        /// nil 是可以不限期
        var maxDays: Int?
        var maxBytes: Int64?
    }

    /// 要帶的內容（R125.2）：會議資訊、逐字稿、說話者名字、會議紀錄、重點標記一定帶
    struct Contents: Codable, Equatable, Hashable {
        var audio = true
        var userNotes = true
        var documents = true
    }

    struct MeetingInfo: Equatable {
        var title: String
        var meetingDate: String
        var durationSeconds: Int?
        var language: String
    }

    struct Person: Equatable, Hashable, Identifiable {
        var email: String
        var name: String?
        var id: String { email }
        var display: String { name.flatMap { $0.isEmpty ? nil : $0 } ?? email }
    }

    struct Recipient: Equatable, Identifiable {
        var email: String
        var name: String?
        var openedAt: String?
        var importedAt: String?
        var importedVersion: Int?
        var id: String { email }
    }

    struct Link: Equatable {
        var url: String
        var access: String   // company | public
    }

    struct Detail: Equatable {
        var id: String
        var status: String
        var version: Int
        var expiresAt: String?
        var meeting: MeetingInfo?
        var contents: Contents
        var bytes: Int64
        var uploading: Bool
        var recipients: [Recipient]
        var link: Link?
    }

    struct SharedItem: Equatable, Identifiable {
        var id: String
        var from: Person
        var meeting: MeetingInfo?
        var contents: Contents
        var bytes: Int64
        var version: Int
        var updatedAt: String
        var expiresAt: String?
        var importedVersion: Int?
        /// 沒匯入過、或分享者更新過（R125.5）
        var isNew: Bool { importedVersion.map { version > $0 } ?? true }
        var title: String { meeting?.title.isEmpty == false ? meeting!.title : String(localized: "未命名的會議") }
    }

    struct Summary: Equatable {
        var from: Person
        var meeting: MeetingInfo?
        var contents: Contents
        var bytes: Int64
        var version: Int
        var expiresAt: String?
    }

    nonisolated static let chunkSize = 8 * 1024 * 1024
    /// 名單最多幾個人（M§99.2：伺服器一場最多 500 人，回應超過時截斷）
    nonisolated static let maxRecipients = 500

    // MARK: 請求的內容、回應的檢查（純函式，標準答案 server/shares.json）

    nonisolated static func createBody(meeting: MeetingInfo, contents: Contents, bytes: Int64, sha256: String, expiresInDays: Int?) -> [String: Any] {
        var meetingJSON: [String: Any] = ["title": meeting.title, "meetingDate": meeting.meetingDate, "language": meeting.language]
        meetingJSON["durationSeconds"] = meeting.durationSeconds.map { $0 as Any } ?? NSNull()
        return [
            "meeting": meetingJSON,
            "contents": ["audio": contents.audio, "userNotes": contents.userNotes, "documents": contents.documents],
            "package": ["bytes": bytes, "sha256": sha256],
            "expiresInDays": expiresInDays.map { $0 as Any } ?? NSNull(),
        ]
    }

    nonisolated static func parseSharing(_ value: Any?) -> Sharing? {
        guard let json = value as? [String: Any], json["enabled"] as? Bool == true else { return nil }
        return Sharing(enabled: true, publicLinks: json["publicLinks"] as? Bool ?? false, maxDays: positive(json["maxDays"]).map(Int.init),
                       maxBytes: positive(json["maxBytes"]))
    }

    /// 正的整數（不是數字、小數、負的、0 是 nil）
    nonisolated static func positive(_ value: Any?) -> Int64? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let double = number.doubleValue
        guard double.isFinite, double >= 1, double <= 9_007_199_254_740_992, double == double.rounded() else { return nil }
        return Int64(double)
    }

    nonisolated static func string(_ value: Any?) -> String? { value as? String }

    nonisolated static func parseMeeting(_ value: Any?) -> MeetingInfo? {
        guard let json = value as? [String: Any] else { return nil }
        return MeetingInfo(title: string(json["title"]) ?? "", meetingDate: string(json["meetingDate"]) ?? "",
                           durationSeconds: positive(json["durationSeconds"]).map(Int.init), language: string(json["language"]) ?? "zh")
    }

    nonisolated static func parseContents(_ value: Any?) -> Contents {
        let json = value as? [String: Any] ?? [:]
        return Contents(audio: json["audio"] as? Bool ?? true, userNotes: json["userNotes"] as? Bool ?? true, documents: json["documents"] as? Bool ?? true)
    }

    nonisolated static func parsePerson(_ value: Any?) -> Person? {
        guard let json = value as? [String: Any], let email = string(json["email"]), !email.isEmpty else { return nil }
        return Person(email: email, name: string(json["name"]))
    }

    nonisolated static func parseRecipients(_ value: Any?) -> [Recipient] {
        (value as? [Any] ?? []).prefix(maxRecipients).compactMap { item in
            guard let json = item as? [String: Any], let email = string(json["email"]), !email.isEmpty else { return nil }
            return Recipient(email: email, name: string(json["name"]), openedAt: string(json["openedAt"]), importedAt: string(json["importedAt"]),
                             importedVersion: positive(json["importedVersion"]).map(Int.init))
        }
    }

    nonisolated static func parseLink(_ value: Any?) -> Link? {
        guard let json = value as? [String: Any], let url = string(json["url"]), url.hasPrefix("https://") || url.hasPrefix("http://"),
              let access = string(json["access"]), ["company", "public"].contains(access) else { return nil }
        return Link(url: url, access: access)
    }

    /// GET /api/v1/shares/<id>：id、status 不對就是 nil；version 不合理當成 1、bytes 不合理當成 0
    nonisolated static func parseDetail(_ value: Any?) -> Detail? {
        guard let json = value as? [String: Any], let id = string(json["id"]), !id.isEmpty,
              let status = string(json["status"]), ["uploading", "ready"].contains(status) else { return nil }
        return Detail(id: id, status: status, version: positive(json["version"]).map(Int.init) ?? 1, expiresAt: string(json["expiresAt"]),
                      meeting: parseMeeting(json["meeting"]), contents: parseContents(json["contents"]), bytes: positive(json["bytes"]) ?? 0,
                      uploading: json["uploading"] as? Bool ?? false, recipients: parseRecipients(json["recipients"]), link: parseLink(json["link"]))
    }

    nonisolated static func parseSharedWithMe(_ value: Any?) -> [SharedItem] {
        ((value as? [String: Any])?["shares"] as? [Any] ?? []).compactMap { item in
            guard let json = item as? [String: Any], let id = string(json["id"]), !id.isEmpty, let from = parsePerson(json["from"]) else { return nil }
            return SharedItem(id: id, from: from, meeting: parseMeeting(json["meeting"]), contents: parseContents(json["contents"]),
                              bytes: positive(json["bytes"]) ?? 0, version: positive(json["version"]).map(Int.init) ?? 1,
                              updatedAt: string(json["updatedAt"]) ?? "", expiresAt: string(json["expiresAt"]),
                              importedVersion: positive(json["importedVersion"]).map(Int.init))
        }
    }

    nonisolated static func parseSummary(_ value: Any?) -> Summary? {
        guard let json = value as? [String: Any], let from = parsePerson(json["from"]) else { return nil }
        return Summary(from: from, meeting: parseMeeting(json["meeting"]), contents: parseContents(json["contents"]),
                       bytes: positive(json["bytes"]) ?? 0, version: positive(json["version"]).map(Int.init) ?? 1, expiresAt: string(json["expiresAt"]))
    }

    /// shiyin://share?server=…&id=…&t=…（網頁的「用拾音打開」，R125.6）
    struct ShareLink: Equatable {
        var server: URL
        var id: String
        var token: String?
    }

    nonisolated static func isShareLink(_ url: URL) -> Bool { url.scheme?.lowercased() == "shiyin" && url.host()?.lowercased() == "share" }

    nonisolated static func parseShareLink(_ url: URL) -> ShareLink? {
        guard isShareLink(url), let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let serverText = items.first(where: { $0.name == "server" })?.value, let server = EnterpriseServer.normalized(serverText),
              let id = items.first(where: { $0.name == "id" })?.value, id.count == 32, id.allSatisfy(\.isHexDigit) else { return nil }
        let token = items.first { $0.name == "t" }?.value.flatMap { $0.isEmpty ? nil : $0 }
        return ShareLink(server: server, id: id.lowercased(), token: token)
    }

    // MARK: API（M§99.2）

    private static var server: EnterpriseServer { .shared }

    /// 403 是「公司關掉了分享」這類，不當成登入失效（call 的 expireOnForbidden）
    private static func call(_ method: String, _ path: String, json: Any? = nil, body: Data? = nil, headers: [String: String] = [:],
                             timeout: TimeInterval = 60) async throws -> (Any?, Data, HTTPURLResponse) {
        let (data, response) = try await server.call(method, path, json: json, body: body, headers: headers, timeout: timeout, expireOnForbidden: false)
        guard (200..<300).contains(response.statusCode) else { throw EnterpriseServer.failure(data, response) }
        return (data.isEmpty ? nil : try? JSONSerialization.jsonObject(with: data), data, response)
    }

    static func people(_ query: String) async throws -> [Person] {
        let q = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed.subtracting(CharacterSet(charactersIn: "&+=#?"))) ?? ""
        let (json, _, _) = try await call("GET", "api/v1/people?q=\(q)", timeout: 15)
        return ((json as? [String: Any])?["people"] as? [Any] ?? []).compactMap(parsePerson)
    }

    /// 建分享（status uploading），回傳 id
    static func create(_ body: [String: Any]) async throws -> String {
        let (json, _, _) = try await call("POST", "api/v1/shares", json: body)
        guard let id = (json as? [String: Any])?["id"] as? String, id.count == 32, id.allSatisfy(\.isHexDigit) else {
            throw EnterpriseServer.Failure.server(kind: "bad_response", message: String(localized: "企業伺服器的回應不對"))
        }
        return id
    }

    /// 更新內容（R125.4）：建新的版本，接著 upload
    static func startVersion(_ id: String, body: [String: Any]) async throws {
        _ = try await call("POST", "api/v1/shares/\(id)/versions", json: body)
    }

    /// 從伺服器收到的地方接著傳，一次 8 MB（同轉錄的錄音上傳，design §36.2 的防線）。SHA-256 不對時從頭傳一次
    static func upload(_ id: String, file: URL, bytes: Int64, progress: @escaping (Double) -> Void) async throws {
        for attempt in 0..<2 {
            let (_, head) = try await server.call("HEAD", "api/v1/shares/\(id)/package", timeout: 30, expireOnForbidden: false)
            guard head.statusCode == 200 else { throw EnterpriseServer.failure(Data(), head) }
            var offset = offset(head, bytes: bytes) ?? 0
            let handle = try FileHandle(forReadingFrom: file)
            defer { try? handle.close() }
            let chunks = Int((bytes + Int64(chunkSize) - 1) / Int64(chunkSize))
            var sent = 0, stalled = 0, restart = false
            while offset < bytes {
                try Task.checkCancellation()
                sent += 1
                if stalled >= 3 || sent > chunks * 2 + 3 {
                    throw EnterpriseServer.Failure.server(kind: "upload_stalled", message: String(localized: "會議一直沒辦法完整傳到企業伺服器"))
                }
                try handle.seek(toOffset: UInt64(offset))
                let chunk = try handle.read(upToCount: chunkSize) ?? Data()
                if chunk.isEmpty { break }
                let (data, response) = try await server.call("PATCH", "api/v1/shares/\(id)/package", body: chunk, headers: [
                    "Upload-Offset": String(offset), "Content-Type": "application/offset+octet-stream",
                ], timeout: 300, expireOnForbidden: false)
                let before = offset
                switch response.statusCode {
                case 204: offset = Self.offset(response, bytes: bytes) ?? offset + Int64(chunk.count)
                case 409: offset = Self.offset(response, bytes: bytes) ?? 0
                case 400 where attempt == 0:
                    restart = true
                default: throw EnterpriseServer.failure(data, response)
                }
                if restart { break }
                stalled = offset > before ? 0 : stalled + 1
                progress(Double(offset) / Double(max(bytes, 1)))
            }
            if !restart { return }
        }
    }

    nonisolated static func offset(_ response: HTTPURLResponse, bytes: Int64) -> Int64? {
        guard let value = response.value(forHTTPHeaderField: "Upload-Offset").flatMap({ Int64($0) }) else { return nil }
        return (0...bytes).contains(value) ? value : 0
    }

    static func detail(_ id: String) async throws -> Detail {
        let (json, _, _) = try await call("GET", "api/v1/shares/\(id)", timeout: 20)
        guard let detail = parseDetail(json) else { throw EnterpriseServer.Failure.server(kind: "bad_response", message: String(localized: "企業伺服器的回應不對")) }
        return detail
    }

    static func setExpiry(_ id: String, days: Int?) async throws -> Detail {
        let (json, _, _) = try await call("PATCH", "api/v1/shares/\(id)", json: ["expiresInDays": days.map { $0 as Any } ?? NSNull()])
        guard let detail = parseDetail(json) else { throw EnterpriseServer.Failure.server(kind: "bad_response", message: String(localized: "企業伺服器的回應不對")) }
        return detail
    }

    static func addRecipients(_ id: String, emails: [String]) async throws -> [Recipient] {
        let (json, _, _) = try await call("POST", "api/v1/shares/\(id)/recipients", json: ["emails": emails])
        return parseRecipients((json as? [String: Any])?["recipients"])
    }

    static func removeRecipient(_ id: String, email: String) async throws {
        let path = email.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/"))) ?? email
        _ = try await call("DELETE", "api/v1/shares/\(id)/recipients/\(path)")
    }

    static func setLink(_ id: String, access: String) async throws -> Link {
        let (json, _, _) = try await call("POST", "api/v1/shares/\(id)/link", json: ["access": access])
        guard let link = parseLink(json) else { throw EnterpriseServer.Failure.server(kind: "bad_response", message: String(localized: "企業伺服器的回應不對")) }
        return link
    }

    static func deleteLink(_ id: String) async throws { _ = try await call("DELETE", "api/v1/shares/\(id)/link") }

    static func stop(_ id: String) async throws { _ = try await call("DELETE", "api/v1/shares/\(id)") }

    static func sharedWithMe() async throws -> [SharedItem] {
        let (json, _, _) = try await call("GET", "api/v1/shared-with-me", timeout: 20)
        return parseSharedWithMe(json)
    }

    static func hide(_ id: String) async throws { _ = try await call("DELETE", "api/v1/shared-with-me/\(id)") }

    // MARK: 收到的人：摘要、下載、回報匯入（登入的是同一台時帶 token；用連結的只帶代碼）

    /// 連結的請求：登入的是同一台伺服器時走 call（帶 token），否則直接送（公開的連結不用登入）
    private static func linkRequest(_ link: ShareLink, _ method: String, _ path: String, json: Any? = nil, headers: [String: String] = [:],
                                    timeout: TimeInterval = 60) async throws -> (Data, HTTPURLResponse) {
        var fullPath = path
        if let token = link.token { fullPath += (path.contains("?") ? "&" : "?") + "t=" + (token.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? "") }
        if server.isSignedIn, let saved = server.savedURL, EnterpriseServer.sameServer(saved, link.server) {
            let (data, response) = try await server.call(method, fullPath, json: json, headers: headers, timeout: timeout, expireOnForbidden: false)
            return (data, response)
        }
        var request = URLRequest(url: EnterpriseServer.url(link.server, fullPath))
        request.httpMethod = method
        request.timeoutInterval = timeout
        EnterpriseServer.addLanguage(&request)
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        if let json {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: json)
        }
        return try await EnterpriseServer.send(request)
    }

    static func summary(_ link: ShareLink) async throws -> Summary {
        let (data, response) = try await linkRequest(link, "GET", "api/v1/shares/\(link.id)/summary", timeout: 20)
        guard response.statusCode == 200 else { throw EnterpriseServer.failure(data, response) }
        guard let summary = parseSummary(try? JSONSerialization.jsonObject(with: data)) else {
            throw EnterpriseServer.Failure.server(kind: "bad_response", message: String(localized: "企業伺服器的回應不對"))
        }
        return summary
    }

    /// 下載封包，8 MB 一段（Range），檔案裡已經有的接著下載。每一段都看 X-Shiyin-Share-Version：跟 version（檔案是哪一版）不同
    /// 表示分享者剛更新過，前面下載的不能接，清掉從頭下載（最多重來兩次）。回傳下載到的版本
    static func download(_ link: ShareLink, to file: URL, bytes: Int64, version expected: Int,
                         progress: @escaping (Double) -> Void) async throws -> Int {
        if !FileManager.default.fileExists(atPath: file.path) { FileManager.default.createFile(atPath: file.path, contents: nil) }
        let handle = try FileHandle(forWritingTo: file)
        defer { try? handle.close() }
        var have = Int64(try handle.seekToEnd())
        var version = expected
        var total = bytes
        var restarts = 0
        repeat {
            try Task.checkCancellation()
            let end = total > 0 ? min(have + Int64(chunkSize), total) - 1 : have + Int64(chunkSize) - 1
            let (data, response) = try await linkRequest(link, "GET", "api/v1/shares/\(link.id)/package", headers: ["Range": "bytes=\(have)-\(end)"], timeout: 300)
            if let served = response.value(forHTTPHeaderField: "X-Shiyin-Share-Version").flatMap(Int.init), served != version {
                restarts += 1
                guard restarts <= 2 else { throw EnterpriseServer.Failure.server(kind: "download_stalled", message: String(localized: "會議沒辦法完整下載")) }
                try handle.truncate(atOffset: 0)
                (have, total, version) = (0, 0, served)
                continue
            }
            switch response.statusCode {
            case 206, 200:
                if response.statusCode == 200 {
                    // 伺服器整個送過來（不支援 Range）：從頭寫
                    try handle.truncate(atOffset: 0)
                    have = 0
                }
                if let range = response.value(forHTTPHeaderField: "Content-Range"), let slash = range.lastIndex(of: "/"), let all = Int64(range[range.index(after: slash)...]) {
                    total = all
                } else if response.statusCode == 200 {
                    total = Int64(data.count)
                }
                try handle.write(contentsOf: data)
                have += Int64(data.count)
                if data.isEmpty { throw EnterpriseServer.Failure.server(kind: "download_stalled", message: String(localized: "會議沒辦法完整下載")) }
            case 416 where total > 0 && have >= total:
                break
            default:
                throw EnterpriseServer.failure(data, response)
            }
            progress(Double(have) / Double(max(total, 1)))
        } while total == 0 || have < total
        return version
    }

    /// 回報匯入了（送不出去不管，R125.5 的「已匯入」只是顯示用）
    static func imported(_ link: ShareLink, version: Int) async {
        _ = try? await linkRequest(link, "POST", "api/v1/shares/\(link.id)/imported", json: ["version": version], timeout: 15)
    }
}
