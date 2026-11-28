import AppKit
import CryptoKit
import Foundation

/// 分享視窗開著的會議（design §99.4）
struct ShareTarget: Identifiable, Equatable {
    var meetingId: Int64
    var id: Int64 { meetingId }
}

/// 透過企業伺服器分享會議（R125、design §99.4）：分享、名單、連結、期限、更新內容、停止分享；與我分享、匯入、shiyin://share
extension AppModel {
    /// 公司的伺服器有開分享、登入了（R125.7）；否則分享視窗只有「用檔案分享…」
    var sharing: EnterpriseShares.Sharing? {
        let server = EnterpriseServer.shared
        guard server.isSignedIn, let sharing = server.account?.sharing, sharing.enabled else { return nil }
        return sharing
    }

    /// meeting_shares 的 server 欄位：目前登入的伺服器
    private var shareServer: String? { EnterpriseServer.shared.savedURL?.absoluteString }

    /// 這場在目前的伺服器分享過的 id（新的在前）
    func shareId(_ meetingId: Int64) -> String? {
        guard let server = shareServer else { return nil }
        return (try? db.shareIds(meetingId, server: server))?.first
    }

    // MARK: 分享者

    /// 會議頁、分享視窗打開時問一次伺服器（D212）：過期、被撤銷（not_found）就忘掉這一列
    func loadShareDetail(_ meetingId: Int64) async {
        guard sharing != nil, let server = shareServer, let id = shareId(meetingId) else {
            shareDetails[meetingId] = nil
            return
        }
        do {
            shareDetails[meetingId] = try await EnterpriseShares.detail(id)
        } catch EnterpriseServer.Failure.server(kind: "not_found", _) {
            try? db.forgetShare(server: server, shareId: id)
            shareDetails[meetingId] = nil
        } catch {
            log.error("share detail failed: \(error.localizedDescription)")
        }
    }

    /// 要帶的內容（R125.2）：不帶錄音時沒有 audio、不帶我的筆記時 userNotes 是空的、不帶文件時沒有參考文件和畫面
    nonisolated static func apply(_ contents: EnterpriseShares.Contents, to bundle: ExportBundle) -> ExportBundle {
        var b = bundle
        if !contents.audio { b.meeting.audioFile = "" }
        if !contents.userNotes { b.meeting.userNotes = "" }
        if !contents.documents {
            b.documents = []
            b.screens = []
        }
        return b
    }

    /// 打包（背景）：tmp 裡的 .shiyin、大小、SHA-256
    private func sharePackage(_ meetingId: Int64, contents: EnterpriseShares.Contents) async throws -> (URL, Int64, String) {
        let bundle = Self.apply(contents, to: try exportBundle(meetingId: meetingId))
        let url = Paths.tmpDir.appendingPathComponent("share-\(UUID().uuidString).shiyin")
        return try await Task.detached(priority: .userInitiated) {
            try MeetingPackage.export(bundle, to: url)
            let handle = try FileHandle(forReadingFrom: url)
            defer { try? handle.close() }
            var hash = SHA256()
            var bytes: Int64 = 0
            while let chunk = try handle.read(upToCount: 4 * 1024 * 1024), !chunk.isEmpty {
                hash.update(data: chunk)
                bytes += Int64(chunk.count)
            }
            return (url, bytes, hash.finalize().map { String(format: "%02x", $0) }.joined())
        }.value
    }

    private func shareMeetingInfo(_ meetingId: Int64) throws -> EnterpriseShares.MeetingInfo {
        guard let m = try db.meeting(meetingId) else { throw ExportError.meetingGone }
        return EnterpriseShares.MeetingInfo(title: m.displayTitle, meetingDate: m.meetingDate, durationSeconds: m.durationSeconds, language: m.language)
    }

    /// 「分享」（R125.2）：打包、上傳、記下來，再加名單、建連結。回傳沒加進名單的信箱（放回欄位讓人改）；
    /// 沒傳完（取消、失敗）的分享請伺服器刪掉，不留半套
    func shareMeeting(_ meetingId: Int64, emails: [String], link: String?, days: Int?, contents: EnterpriseShares.Contents) async -> [String] {
        guard sharing != nil, let server = shareServer, !shareBusy else { return emails }
        shareBusy = true
        shareProgress = 0
        defer { shareBusy = false; shareProgress = nil }
        var created: String?
        let id: String
        do {
            let (file, bytes, sha) = try await sharePackage(meetingId, contents: contents)
            defer { try? FileManager.default.removeItem(at: file) }
            if let max = sharing?.maxBytes, bytes > max {
                errorMessage = String(localized: "這場會議超過公司的伺服器一場的上限（\(ByteCountFormatter.string(fromByteCount: max, countStyle: .file))），可以不帶錄音再試")
                return emails
            }
            try Task.checkCancellation()
            let body = EnterpriseShares.createBody(meeting: try shareMeetingInfo(meetingId), contents: contents, bytes: bytes, sha256: sha, expiresInDays: days)
            id = try await EnterpriseShares.create(body)
            created = id
            try await EnterpriseShares.upload(id, file: file, bytes: bytes) { [weak self] value in Task { @MainActor in self?.shareProgress = value } }
            try db.recordShare(meetingId, server: server, shareId: id)
        } catch {
            if let created { Task { try? await EnterpriseShares.stop(created) } }
            if !(error is CancellationError) { errorMessage = error.localizedDescription }
            return emails
        }
        var back: [String] = []
        if !emails.isEmpty {
            do { _ = try await EnterpriseShares.addRecipients(id, emails: emails) } catch {
                errorMessage = error.localizedDescription
                back = emails
            }
        }
        if let link {
            do { _ = try await EnterpriseShares.setLink(id, access: link) } catch { errorMessage = error.localizedDescription }
        }
        await loadShareDetail(meetingId)
        return back
    }

    /// 「更新分享的內容」（R125.4）：照原本帶的內容重新打包上傳，名單、連結不變
    func updateShareContents(_ meetingId: Int64) async {
        guard let id = shareId(meetingId), let detail = shareDetails[meetingId], !shareBusy else { return }
        shareBusy = true
        shareProgress = 0
        defer { shareBusy = false; shareProgress = nil }
        do {
            let (file, bytes, sha) = try await sharePackage(meetingId, contents: detail.contents)
            defer { try? FileManager.default.removeItem(at: file) }
            let body = EnterpriseShares.createBody(meeting: try shareMeetingInfo(meetingId), contents: detail.contents, bytes: bytes, sha256: sha, expiresInDays: nil)
            try await EnterpriseShares.startVersion(id, body: body)
            try await EnterpriseShares.upload(id, file: file, bytes: bytes) { [weak self] value in Task { @MainActor in self?.shareProgress = value } }
            noticeMessage = String(localized: "已更新分享的內容")
        } catch is CancellationError {
        } catch {
            errorMessage = error.localizedDescription
        }
        await loadShareDetail(meetingId)
    }

    /// 回傳沒加進去的信箱（伺服器拒絕時放回欄位）
    func addShareRecipients(_ meetingId: Int64, emails: [String]) async -> [String] {
        guard let id = shareId(meetingId), !emails.isEmpty else { return emails }
        var back: [String] = []
        do { _ = try await EnterpriseShares.addRecipients(id, emails: emails) } catch {
            errorMessage = error.localizedDescription
            back = emails
        }
        await loadShareDetail(meetingId)
        return back
    }

    func removeShareRecipient(_ meetingId: Int64, email: String) async {
        guard let id = shareId(meetingId) else { return }
        do { try await EnterpriseShares.removeRecipient(id, email: email) } catch { errorMessage = error.localizedDescription }
        await loadShareDetail(meetingId)
    }

    /// access 是 nil 時停止連結
    func setShareLink(_ meetingId: Int64, access: String?) async {
        guard let id = shareId(meetingId) else { return }
        do {
            if let access { _ = try await EnterpriseShares.setLink(id, access: access) } else { try await EnterpriseShares.deleteLink(id) }
        } catch { errorMessage = error.localizedDescription }
        await loadShareDetail(meetingId)
    }

    func setShareExpiry(_ meetingId: Int64, days: Int?) async {
        guard let id = shareId(meetingId) else { return }
        do { shareDetails[meetingId] = try await EnterpriseShares.setExpiry(id, days: days) } catch { errorMessage = error.localizedDescription }
    }

    /// 「停止分享…」（R125.4）：伺服器刪掉這場，別人已經匯入的不受影響
    func stopSharing(_ meetingId: Int64) async {
        guard let server = shareServer, let id = shareId(meetingId) else { return }
        do {
            try await EnterpriseShares.stop(id)
        } catch EnterpriseServer.Failure.server(kind: "not_found", _) {
        } catch {
            errorMessage = error.localizedDescription
            return
        }
        try? db.forgetShare(server: server, shareId: id)
        shareDetails[meetingId] = nil
        noticeMessage = String(localized: "已停止分享")
    }

    // MARK: 收到的人（R125.5）

    /// 看過的分享：id → 版本（新的、更新過的才通知）
    private var seenShares: [String: Int] {
        get { (UserDefaults.standard.dictionary(forKey: AppSettings.seenSharesKey) as? [String: Int]) ?? [:] }
        set { UserDefaults.standard.set(newValue, forKey: AppSettings.seenSharesKey) }
    }

    /// 查新的分享（啟動、回到前景、每 15 分鐘）；新的或更新過的送通知
    func refreshSharedWithMe(notify: Bool = true) async {
        guard sharing != nil else {
            sharedWithMe = []
            return
        }
        do {
            let items = try await EnterpriseShares.sharedWithMe()
            var seen = seenShares
            for item in items where seen[item.id].map({ item.version > $0 }) ?? true {
                if notify && item.isNew { notifier.sharedMeeting(item) }
                seen[item.id] = item.version
            }
            seenShares = seen.filter { id, _ in items.contains { $0.id == id } }
            sharedWithMe = items
            if let selected = selectedShared, !items.contains(where: { $0.id == selected }) { selectedShared = nil }
        } catch {
            log.error("shared-with-me failed: \(error.localizedDescription)")
        }
    }

    /// 查新的分享的時機（design §99.4）：啟動、回到前景、每 15 分鐘
    func startSharePolling() {
        Task { [weak self] in
            while !Task.isCancelled {
                await self?.refreshSharedWithMe()
                try? await Task.sleep(for: .seconds(15 * 60))
            }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in await self?.refreshSharedWithMe() }
        }
        notifier.onOpenShare = { [weak self] id in
            guard let self else { return }
            Task {
                await self.refreshSharedWithMe(notify: false)
                self.openWindowById?("main")
                self.selection = nil
                self.selectedShared = id
            }
        }
    }

    /// 「匯入」：下載、匯入成自己的一份、記下是誰分享的、回報伺服器
    func importShared(_ item: EnterpriseShares.SharedItem) async {
        guard let server = EnterpriseServer.shared.savedURL else { return }
        await importShare(EnterpriseShares.ShareLink(server: server, id: item.id, token: nil), from: item.from.display, bytes: item.bytes)
        await refreshSharedWithMe(notify: false)
    }

    func hideShared(_ id: String) async {
        do { try await EnterpriseShares.hide(id) } catch { errorMessage = error.localizedDescription }
        if selectedShared == id { selectedShared = nil }
        await refreshSharedWithMe(notify: false)
    }

    private func importShare(_ link: EnterpriseShares.ShareLink, from: String, bytes: Int64) async {
        guard !shareBusy else { return }
        shareBusy = true
        shareProgress = 0
        defer { shareBusy = false; shareProgress = nil }
        let file = Paths.tmpDir.appendingPathComponent("shared-\(link.id).shiyin")
        do {
            let version = try await EnterpriseShares.download(link, to: file, bytes: bytes) { [weak self] value in Task { @MainActor in self?.shareProgress = value } }
            let (db, folder) = (db, currentFolder)
            let meeting = try await Task.detached(priority: .userInitiated) { try MeetingPackage.importPackage(file, db: db, folder: folder) }.value
            try? FileManager.default.removeItem(at: file)
            if let id = meeting.id { try db.setSharedFrom(id, from) }
            await EnterpriseShares.imported(link, version: version)
            reload()
            showTrash = false
            selectedShared = nil
            selection = meeting.id
            noticeMessage = String(localized: "已匯入「\(meeting.displayTitle)」")
        } catch {
            // 下載到一半的檔案留著：再按一次「匯入」接著下載（同一個版本）
            errorMessage = error.localizedDescription
        }
    }

    /// shiyin://share（網頁的「用拾音打開」，R125.6）：先拿標題、分享者，問過再匯入
    func openShareLink(_ url: URL) async {
        start()
        guard let link = EnterpriseShares.parseShareLink(url) else {
            errorMessage = String(localized: "這個分享的連結不對")
            return
        }
        NSApp.activate()
        do {
            let summary = try await EnterpriseShares.summary(link)
            let alert = NSAlert()
            let title = summary.meeting?.title.isEmpty == false ? summary.meeting!.title : String(localized: "未命名的會議")
            alert.messageText = String(localized: "匯入\(summary.from.display)分享的「\(title)」？")
            alert.informativeText = String(localized: "來自 \(EnterpriseServer.displayHost(link.server))。匯入後是你自己的一份，可以編輯、重新整理。")
            alert.addButton(withTitle: String(localized: "匯入"))
            alert.addButton(withTitle: String(localized: "取消"))
            guard alert.runModal() == .alertFirstButtonReturn else { return }
            await importShare(link, from: summary.from.display, bytes: summary.bytes)
        } catch EnterpriseServer.Failure.loginExpired {
            errorMessage = String(localized: "這個分享只有公司裡的人打得開，請先在「設定 → 企業伺服器」登入 \(EnterpriseServer.displayHost(link.server))")
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}

extension AppModel {
    /// 側欄點「與我分享」的一場：右邊換成預覽
    func selectShared(_ id: String) {
        selection = nil
        listSelection = []
        selectedShared = id
    }

    /// 「與我分享」的右鍵和「⋯」（R125.5）
    func sharedMenu(_ item: EnterpriseShares.SharedItem) -> [MenuEntry] {
        [.action(item.importedVersion == nil ? String(localized: "匯入") : String(localized: "再匯入一次"), icon: "square.and.arrow.down") {
            Task { await self.importShared(item) }
         },
         .divider,
         .action(String(localized: "從清單移除"), icon: "minus.circle") { Task { await self.hideShared(item.id) } }]
    }
}
