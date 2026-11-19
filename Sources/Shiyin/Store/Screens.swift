import AppKit
import CryptoKit
import Foundation

/// 會議中的畫面（design §83、R109）：錄音中截圖、貼上、拖進來；回看時放大、改時間、刪除
extension AppModel {
    // MARK: 加入

    /// ⌃⌥⌘S、錄音島、錄音畫面的「截圖」、⌘⇧S：時間是按下去那一刻的錄音時間（design §83.2）
    func captureScreen() {
        guard recorder.isRecording, let meetingId = recorder.meetingId else { return }
        let at = recorder.elapsed
        Task {
            do {
                let image = try await ScreenShot.capture()
                screenshotProblem = nil
                addScreens([image], at: at, to: meetingId, captured: true)
            } catch let failure as ScreenShot.Failure {
                screenshotProblem = failure
                RecordingIsland.shared.showScreenshotProblem()
            } catch {
                errorMessage = error.localizedDescription
            }
        }
    }

    /// 貼上（⌘V）、拖進錄音畫面：錄音中那場用當下的錄音時間，會後用播放的位置（R109.2）。回傳有沒有接手
    @discardableResult
    func addScreensHere(_ images: [CGImage]) -> Bool {
        guard !images.isEmpty, let target = screenTarget else { return false }
        addScreens(images, at: target.at, to: target.meetingId)
        return true
    }

    /// 現在加畫面會加到哪一場、哪個時間：錄音中、正在看錄音的那場時是錄音時間；
    /// 其他時候是選取的會議（逐字稿、會議紀錄分頁）的播放位置。垃圾桶裡的、正在看別的分頁時不加
    var screenTarget: (meetingId: Int64, at: Double)? {
        if recorder.isRecording, let id = recorder.meetingId, selection == id { return (id, recorder.elapsed) }
        guard let detail, let id = detail.meeting.id, detail.meeting.deletedAt == nil,
              tab == .transcript || tab == .summary else { return nil }
        return (id, player.isLoaded ? player.currentTime : 0)
    }

    /// 一張一張存（JPEG、documents/<id>/screen.jpg），存好就顯示；文字辨識在背景做，完成後補上文字和搜尋索引
    func addScreens(_ images: [CGImage], at: Double, to meetingId: Int64, captured: Bool = false) {
        guard !images.isEmpty, let meeting = try? db.meeting(meetingId) else { return }
        let language = meeting.meetingLanguage
        let db = db
        Task {
            var added = 0
            for image in images {
                let box = ImageBox(image)
                let screen: MeetingDocument
                do {
                    screen = try await Task.detached(priority: .userInitiated) {
                        try ScreenStore.save(box.image, at: at, meetingId: meetingId, db: db)
                    }.value
                } catch ScreenStore.Failure.duplicate {
                    noticeMessage = String(localized: "這張畫面已經加過了")
                    continue
                } catch {
                    errorMessage = String(localized: "沒辦法存這張畫面：\(error.localizedDescription)")
                    continue
                }
                added += 1
                guard let id = screen.id else { continue }
                readingScreens.insert(id)
                if selection == meetingId { reloadDetail(.documents) }
                if captured { screenCaptured(box.image, at: at) }
                Task {
                    let text = await Task.detached(priority: .utility) {
                        (try? DocumentReader.recognize(box.image, orientation: .up, language: language)).map(DocumentReader.normalize) ?? ""
                    }.value
                    try? db.setScreenText(id, text: text)
                    readingScreens.remove(id)
                    if selection == meetingId { reloadDetail(.documents) }
                }
            }
            guard added > 0 else { return }
            if !captured {
                noticeMessage = added == 1 ? String(localized: "已加入 \(TimeFormat.string(at)) 的畫面")
                                           : String(localized: "已加入 \(added) 張畫面")
            }
            markScreensChanged(meetingId)
        }
    }

    /// 截圖的回饋：音效、選單列圖示換成相機 1 秒、錄音島展開顯示縮圖（design §83.2）
    private func screenCaptured(_ image: CGImage, at: Double) {
        NSSound(named: "Pop")?.play()
        screenFlash = true
        RecordingIsland.shared.flashScreen(NSImage(cgImage: image, size: .zero), at: at)
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(1))
            screenFlash = false
        }
    }

    // MARK: 回看（design §83.4）

    /// 放大：Quick Look，← → 看同一場的其他張；錄音跳到那個時間
    func previewScreen(_ screen: MeetingDocument) {
        guard let detail, let at = screen.at else { return }
        let items = ScreenPreviewFiles.links(for: detail.screens, title: detail.meeting.displayTitle)
        guard let index = detail.screens.firstIndex(where: { $0.id == screen.id }), items.indices.contains(index) else { return }
        if player.isLoaded { player.seek(to: at, play: false) }
        screenPreviewItems = items
        screenPreview = items[index]
    }

    /// 用預覽程式打開：檔名是「<會議> 12-34.jpg」的連結，不是 screen.jpg
    func openScreen(_ screen: MeetingDocument) {
        guard let detail else { return }
        let items = ScreenPreviewFiles.links(for: detail.screens, title: detail.meeting.displayTitle)
        guard let index = detail.screens.firstIndex(where: { $0.id == screen.id }), items.indices.contains(index) else {
            errorMessage = String(localized: "找不到這張畫面的檔案")
            return
        }
        NSWorkspace.shared.open(items[index])
    }

    func copyScreenImage(_ screen: MeetingDocument) {
        guard let image = NSImage(contentsOf: screen.fileURL) else {
            errorMessage = String(localized: "找不到這張畫面的檔案")
            return
        }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects([image])
        noticeMessage = String(localized: "已複製 \(TimeFormat.string(screen.at ?? 0)) 的畫面")
    }

    /// 改時間（共用的彈窗，design §80）
    func moveScreen(_ id: Int64, to seconds: Double) {
        guard let meetingId = detail?.meeting.id else { return }
        perform(.detail([.documents])) { try db.setScreenTime(id, at: seconds) }
        noticeMessage = String(localized: "已改到 \(TimeFormat.string(seconds))")
        markScreensChanged(meetingId)
    }

    func deleteScreen(_ id: Int64) {
        guard let screen = detail?.screens.first(where: { $0.id == id }) else { return }
        perform(.detail([.documents])) { try db.deleteDocument(id) }
        noticeMessage = String(localized: "已刪除 \(TimeFormat.string(screen.at ?? 0)) 的畫面")
        markScreensChanged(screen.meetingId)
    }

    /// 「⋯」和右鍵同一份（R105）
    func screenMenu(_ screen: MeetingDocument) -> [MenuEntry] {
        guard let id = screen.id, let at = screen.at else { return [] }
        var entries: [MenuEntry] = [
            .action(String(localized: "放大"), icon: "arrow.up.left.and.arrow.down.right", shortcut: "↩") { self.previewScreen(screen) },
        ]
        if player.isLoaded {
            entries.append(.action(String(localized: "從這裡播放"), icon: "play") { self.player.seek(to: at, play: true) })
        }
        entries += [
            .action(String(localized: "用預覽程式打開"), icon: "photo") { self.openScreen(screen) },
            .action(String(localized: "複製圖片"), icon: "doc.on.doc", shortcut: "⌘C") { self.copyScreenImage(screen) },
            .divider,
            .action(String(localized: "改時間…"), icon: "clock") { self.movingScreen = id },
            .action(String(localized: "刪除…"), icon: "trash", shortcut: "⌫", destructive: true) { self.confirmDeleteScreen = id },
        ]
        return entries
    }

    /// 捲到那張、亮一下（搜尋結果）
    func flashScreen(_ id: Int64) {
        highlightScreen = nil
        Task { @MainActor in
            highlightScreen = id
            try? await Task.sleep(for: .seconds(6))
            if highlightScreen == id { highlightScreen = nil }
        }
    }

    /// 已經有會議紀錄（含草稿）時，紀錄分頁頂部提示重新整理（同參考文件，design §83.4）
    func markScreensChanged(_ meetingId: Int64) {
        if (try? db.meeting(meetingId))?.notes != nil { screensChanged.insert(meetingId) }
    }

    // MARK: ⌘V

    /// ⌘V：剪貼簿只有圖片（沒有文字）時加成畫面（R109.2）。在「我的筆記」裡也一樣（筆記是純文字，圖片本來貼不進去）。
    /// 選單的「編輯 → 貼上」照舊交給輸入框；回傳 nil 表示接手了
    func handlePasteKey(_ event: NSEvent) -> NSEvent? {
        guard event.modifierFlags.intersection([.command, .option, .control, .shift]) == .command,
              event.charactersIgnoringModifiers?.lowercased() == "v",
              let window = event.window, window.identifier?.rawValue.hasPrefix("main") == true, window.attachedSheet == nil
        else { return event }
        let pasteboard = NSPasteboard.general
        guard pasteboard.string(forType: .string) == nil, screenTarget != nil else { return event }
        let images = ScreenImages.read(pasteboard)
        guard !images.isEmpty else { return event }
        addScreensHere(images)
        return nil
    }
}

/// CGImage 跨 Task 傳（CGImage 不會再被改）
struct ImageBox: @unchecked Sendable {
    let image: CGImage
    init(_ image: CGImage) { self.image = image }
}

/// 存畫面（design §83.1）
enum ScreenStore {
    enum Failure: Error { case duplicate, unreadable }

    static func save(_ image: CGImage, at: Double, meetingId: Int64, db: AppDatabase) throws -> MeetingDocument {
        guard let jpeg = ScreenImages.jpeg(image) else { throw Failure.unreadable }
        // 同一場、同一張（內容一樣）不再加
        let hash = SHA256.hash(data: jpeg)
        for existing in try db.screens(meetingId) {
            if let data = try? Data(contentsOf: existing.fileURL), SHA256.hash(data: data) == hash { throw Failure.duplicate }
        }
        let screen = try db.addScreen(meetingId, at: at)
        do {
            let url = Paths.documentsDir.appendingPathComponent(screen.file)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try jpeg.write(to: url, options: .atomic)
        } catch {
            _ = try? db.deleteDocument(screen.id!)
            throw error
        }
        return screen
    }
}

/// 放大、用預覽程式打開時給的檔案：暫存資料夾裡的硬連結，檔名是「<會議> 12-34.jpg」（Quick Look、預覽程式的標題）
enum ScreenPreviewFiles {
    static func links(for screens: [MeetingDocument], title: String) -> [URL] {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("ShiyinScreens", isDirectory: true)
        try? FileManager.default.removeItem(at: folder)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let base = DocumentStore.safeName(title).replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        var used = Set<String>()
        return screens.map { screen in
            let time = TimeFormat.string(screen.at ?? 0).replacingOccurrences(of: ":", with: "-")
            var name = "\(base) \(time)"
            var n = 2
            while used.contains(name) { name = "\(base) \(time)-\(n)"; n += 1 }
            used.insert(name)
            let link = folder.appendingPathComponent(name + ".jpg")
            if (try? FileManager.default.linkItem(at: screen.fileURL, to: link)) == nil {
                try? FileManager.default.copyItem(at: screen.fileURL, to: link)
            }
            return link
        }
    }
}
