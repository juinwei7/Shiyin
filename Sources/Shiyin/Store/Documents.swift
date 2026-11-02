import AppKit
import Foundation

/// 會議的參考文件（design §39.4、R61）：加入、移除、打開，文件有變動的提示
extension AppModel {
    /// 「加入文件…」：選檔後加到選取中的會議
    func chooseDocuments() {
        guard let id = selection else { return }
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = true
        panel.canChooseDirectories = false
        panel.allowedContentTypes = DocumentReader.openPanelTypes
        panel.message = String(localized: "選擇會議中一起討論的文件（PDF、Word、PowerPoint、純文字、圖片）")
        panel.prompt = String(localized: "加入")
        panel.begin { response in
            guard response == .OK else { return }
            let urls = panel.urls
            Task { @MainActor in self.addDocuments(urls, to: id) }
        }
    }

    /// 拖進主視窗的文件：加到選取中的會議（R61.1）
    func addDroppedDocuments(_ urls: [URL]) {
        guard let id = selection, let meeting = try? db.meeting(id), meeting.deletedAt == nil else {
            errorMessage = String(localized: "先選一場會議，再把文件拖進來")
            return
        }
        addDocuments(urls, to: id)
    }

    /// 一份一份依序讀（加入的順序固定），讀取中記在 readingDocuments；讀完複製原始檔、寫資料庫
    func addDocuments(_ urls: [URL], to meetingId: Int64) {
        guard !urls.isEmpty, let meeting = try? db.meeting(meetingId) else { return }
        let language = meeting.meetingLanguage
        let db = db
        readingDocuments[meetingId, default: []] += urls.map(\.lastPathComponent)
        Task {
            var added: [MeetingDocument] = []
            var failures: [String] = []
            for url in urls {
                do {
                    added.append(try await Task.detached(priority: .userInitiated) {
                        try DocumentStore.add(url, meetingId: meetingId, language: language, db: db)
                    }.value)
                } catch {
                    failures.append(error.localizedDescription)
                }
                if var names = readingDocuments[meetingId], let index = names.firstIndex(of: url.lastPathComponent) {
                    names.remove(at: index)
                    readingDocuments[meetingId] = names.isEmpty ? nil : names
                }
                if selection == meetingId { reloadDetail(.documents) }
            }
            if !failures.isEmpty { errorMessage = failures.joined(separator: "\n") }
            guard !added.isEmpty else { return }
            if added.count == 1 {
                let name = added[0].name
                noticeMessage = added[0].ocr ? String(localized: "已加入「\(name)」，用文字辨識讀出文字") : String(localized: "已加入「\(name)」")
            } else {
                noticeMessage = String(localized: "已加入 \(added.count) 份文件")
            }
            markDocumentsChanged(meetingId)
        }
    }

    /// 移除：不另外確認（重新加入就回來了）
    func removeDocument(_ document: MeetingDocument) {
        guard let id = document.id else { return }
        perform(.detail([.documents])) { try db.deleteDocument(id) }
        noticeMessage = String(localized: "已移除「\(document.name)」")
        markDocumentsChanged(document.meetingId)
    }

    func openDocument(_ document: MeetingDocument) {
        guard FileManager.default.fileExists(atPath: document.fileURL.path) else {
            errorMessage = String(localized: "找不到「\(document.name)」的原始檔")
            return
        }
        NSWorkspace.shared.open(document.fileURL)
    }

    /// 已經有會議紀錄（含草稿）時，紀錄分頁頂部提示重新整理（R61.8）
    private func markDocumentsChanged(_ meetingId: Int64) {
        if (try? db.meeting(meetingId))?.notes != nil { documentsChanged.insert(meetingId) }
    }

    func dismissDocumentsChanged() {
        if let id = selection { documentsChanged.remove(id) }
    }
}

/// 存參考文件：讀出文字、寫資料庫、複製原始檔（design §39.1、§39.4）；封包匯入也用
enum DocumentStore {
    static func add(_ url: URL, meetingId: Int64, language: MeetingLanguage, db: AppDatabase) throws -> MeetingDocument {
        let accessing = url.startAccessingSecurityScopedResource()
        defer { if accessing { url.stopAccessingSecurityScopedResource() } }
        let result = try DocumentReader.read(url, language: language)
        // 同一份（檔名和讀出來的文字都一樣）加過了就不再加：送給整理工具會重複
        let name = safeName(url.lastPathComponent)
        if try db.documents(meetingId).contains(where: { $0.name == name && $0.pageTexts == result.pages }) {
            throw DocumentReader.DocumentError.duplicate(name)
        }
        return try save(url, name: url.lastPathComponent, kind: result.kind, pages: result.pages, ocr: result.ocr,
                        meetingId: meetingId, db: db)
    }

    /// 寫資料庫（拿到 id）再複製原始檔到 documents/<id>/<檔名>；複製失敗就把那一列刪掉
    static func save(_ source: URL, name: String, kind: DocumentKind, pages: [String], ocr: Bool,
                     meetingId: Int64, db: AppDatabase) throws -> MeetingDocument {
        let document = try db.addDocument(meetingId, name: safeName(name), kind: kind, pages: pages, ocr: ocr)
        do {
            let destination = Paths.documentsDir.appendingPathComponent(document.file)
            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: source, to: destination)
        } catch {
            try? db.deleteDocument(document.id!)
            throw error
        }
        return document
    }

    /// 檔名只取最後一段、去掉開頭的「.」；是空的就叫 document
    static func safeName(_ name: String) -> String {
        let last = String((name as NSString).lastPathComponent.drop { $0 == "." })
        return last.isEmpty ? "document" : last
    }
}
