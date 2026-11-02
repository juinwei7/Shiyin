import Foundation

/// 整場會議封包 .shiyin（design §10、R7.4、R3.3）：zip 裡有 manifest、會議、逐字稿、錄音
enum MeetingPackage {
    static let formatVersion = 1

    struct Manifest: Codable {
        var format: Int
        var app: String
        var exportedAt: String
        var audio: String?
    }

    struct MeetingJSON: Codable {
        var title: String
        var titleLocked: Bool
        var meetingDate: String
        var participants: String
        var sourceFilename: String
        var durationSeconds: Int?
        var templateId: String
        var userNotes: String
        var channels: String
        /// 資料夾名稱（R9.2）；舊封包沒有這個欄位
        var folder: String?
        /// 會議語言（design §16.3）；舊封包沒有這個欄位，當成中文
        var language: String?
        var notes: MeetingNotes?
        var notesBy: String?
        var speakers: [SpeakerJSON]
        var bookmarks: [BookmarkJSON]
    }

    struct SpeakerJSON: Codable { var idx: Int; var name: String }
    struct BookmarkJSON: Codable { var time: Double; var label: String }
    struct SegmentJSON: Codable { var start: Double; var end: Double; var text: String; var lowConfidence: Bool; var speaker: Int? }
    /// 參考文件（design §39.5）：documents.json 的一項，file 是封包裡原始檔的位置
    struct DocumentJSON: Codable { var name: String; var file: String; var kind: String; var pages: [String]; var ocr: Bool }

    /// documents.json 的內容：原始檔放 documents/<序號>-<檔名>，序號從 1
    static func documentEntries(_ documents: [MeetingDocument]) -> [DocumentJSON] {
        documents.enumerated().map { i, d in
            DocumentJSON(name: d.name, file: "documents/\(i + 1)-\(d.name)", kind: d.kind, pages: d.pageTexts, ocr: d.ocr)
        }
    }

    /// 封包裡文件的位置只能是 documents/<一般的檔名>：封包可能來自別人，不能指到別的地方
    static func isSafeDocumentPath(_ file: String) -> Bool {
        let parts = file.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2, parts[0] == "documents" else { return false }
        let name = parts[1]
        return !name.isEmpty && !name.hasPrefix(".") && !name.contains("\\")
    }

    enum PackageError: LocalizedError {
        case invalid(String), tooNew(Int)
        var errorDescription: String? {
            switch self {
            case .invalid(let reason): return String(localized: "這不是有效的拾音封包：\(reason)")
            case .tooNew(let v): return String(localized: "這個封包是新版拾音匯出的（格式 \(v)），請先更新拾音再匯入")
            }
        }
    }

    static func export(_ b: ExportBundle, to url: URL) throws {
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let m = b.meeting
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        var audioName: String?
        if m.hasAudio {
            audioName = "audio.\(m.audioURL.pathExtension.lowercased())"
            try FileManager.default.copyItem(at: m.audioURL, to: dir.appendingPathComponent(audioName!))
        }
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        try encoder.encode(Manifest(format: formatVersion, app: version, exportedAt: Clock.now(), audio: audioName))
            .write(to: dir.appendingPathComponent("manifest.json"))
        try encoder.encode(MeetingJSON(
            title: m.title, titleLocked: m.titleLocked, meetingDate: m.meetingDate, participants: m.participants,
            sourceFilename: m.sourceFilename, durationSeconds: m.durationSeconds, templateId: m.templateId,
            userNotes: m.userNotes, channels: m.channels, folder: m.folder, language: m.language, notes: b.notes, notesBy: m.notesBy,
            speakers: b.speakers.map { SpeakerJSON(idx: $0.key, name: $0.value) }.sorted { $0.idx < $1.idx },
            bookmarks: b.bookmarks.map { BookmarkJSON(time: $0.time, label: $0.label) }))
            .write(to: dir.appendingPathComponent("meeting.json"))
        try encoder.encode(b.segments.map { SegmentJSON(start: $0.start, end: $0.end, text: $0.text, lowConfidence: $0.lowConfidence, speaker: $0.speaker) })
            .write(to: dir.appendingPathComponent("transcript.json"))
        // 參考文件：原始檔不在了的那份不放（不然匯入時會被當成壞掉的封包）
        let documents = b.documents.filter { FileManager.default.fileExists(atPath: $0.fileURL.path) }
        if !documents.isEmpty {
            try FileManager.default.createDirectory(at: dir.appendingPathComponent("documents"), withIntermediateDirectories: true)
            let entries = documentEntries(documents)
            for (entry, document) in zip(entries, documents) {
                try FileManager.default.copyItem(at: document.fileURL, to: dir.appendingPathComponent(entry.file))
            }
            try encoder.encode(entries).write(to: dir.appendingPathComponent("documents.json"))
        }
        try? FileManager.default.removeItem(at: url)
        try ditto(["-c", "-k", "--norsrc", "--noextattr", "--noqtn", dir.path, url.path])
    }

    /// folder：封包沒帶資料夾時放進的資料夾（目前正在看的那個）。在背景執行（design §21.13）
    static func importPackage(_ url: URL, db: AppDatabase, folder: String = "") throws -> Meeting {
        let accessing = url.startAccessingSecurityScopedResource()
        defer { if accessing { url.stopAccessingSecurityScopedResource() } }
        let dir = try Paths.makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        try ditto(["-x", "-k", url.path, dir.path])
        let decoder = JSONDecoder()
        guard let manifestData = try? Data(contentsOf: dir.appendingPathComponent("manifest.json")),
              let manifest = try? decoder.decode(Manifest.self, from: manifestData) else { throw PackageError.invalid(String(localized: "缺少 manifest.json")) }
        guard manifest.format <= formatVersion else { throw PackageError.tooNew(manifest.format) }
        guard let meetingData = try? Data(contentsOf: dir.appendingPathComponent("meeting.json")),
              let info = try? decoder.decode(MeetingJSON.self, from: meetingData) else { throw PackageError.invalid(String(localized: "缺少 meeting.json")) }
        // transcript.json 存在卻讀不出來就報錯，不要當成沒有逐字稿（design §20.10、R18.10）
        let transcriptURL = dir.appendingPathComponent("transcript.json")
        var segments: [SegmentJSON] = []
        if FileManager.default.fileExists(atPath: transcriptURL.path) {
            guard let data = try? Data(contentsOf: transcriptURL), let decoded = try? decoder.decode([SegmentJSON].self, from: data) else {
                throw PackageError.invalid(String(localized: "transcript.json 讀不出來"))
            }
            segments = decoded
        }
        // 錄音檔只接受封包裡的一般檔案：封包可能來自別人，路徑或符號連結可以指到這台電腦上的任何檔案
        var audioName: String?
        if let audio = manifest.audio {
            let name = (audio as NSString).lastPathComponent
            let values = try? dir.appendingPathComponent(name).resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard name == audio, !name.isEmpty, !name.hasPrefix("."), values?.isRegularFile == true, values?.isSymbolicLink != true else {
                throw PackageError.invalid(String(localized: "封包裡的錄音檔不正確"))
            }
            audioName = name
        }
        // 參考文件（design §39.5）：先全部檢查過，再建立會議
        var documents: [DocumentJSON] = []
        let documentsURL = dir.appendingPathComponent("documents.json")
        if FileManager.default.fileExists(atPath: documentsURL.path) {
            guard let data = try? Data(contentsOf: documentsURL), let decoded = try? decoder.decode([DocumentJSON].self, from: data),
                  decoded.allSatisfy({ entry in
                      let values = try? dir.appendingPathComponent(entry.file).resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
                      return DocumentKind(rawValue: entry.kind) != nil && isSafeDocumentPath(entry.file)
                          && values?.isRegularFile == true && values?.isSymbolicLink != true
                  })
            else { throw PackageError.invalid(String(localized: "封包裡的文件不正確")) }
            documents = decoded
        }

        let audioExt = audioName.map { Importer.safeExtension(URL(fileURLWithPath: $0)) }
        var meeting = try db.insertMeeting(Meeting(
            title: info.title, titleLocked: info.titleLocked, meetingDate: info.meetingDate, participants: info.participants,
            sourceFilename: info.sourceFilename, durationSeconds: info.durationSeconds, notesBy: info.notesBy,
            templateId: info.templateId, userNotes: info.userNotes, source: "package", channels: info.channels,
            folder: info.folder.flatMap { $0.isEmpty ? nil : $0 } ?? folder, language: info.language ?? "zh"),
            audioExtension: audioExt)
        do {
            if let audioName {
                try FileManager.default.copyItem(at: dir.appendingPathComponent(audioName), to: meeting.audioURL)
            }
            let id = meeting.id!
            try db.saveTranscript(id, segments: segments.enumerated().map { i, s in
                Segment(meetingId: id, seq: i, start: s.start, end: s.end, text: s.text, lowConfidence: s.lowConfidence,
                        speaker: s.speaker, kind: "final")
            }, duration: info.durationSeconds ?? 0, speakerCount: 0)
            for speaker in info.speakers { try db.renameSpeaker(id, idx: speaker.idx, name: speaker.name) }
            for bookmark in info.bookmarks { try db.addBookmark(id, time: bookmark.time, label: bookmark.label) }
            if let notes = info.notes { try db.saveNotes(id, notes: notes, by: info.notesBy, reason: String(localized: "匯入")) }
            try db.updateUserNotes(id, info.userNotes)
            for entry in documents {
                _ = try DocumentStore.save(dir.appendingPathComponent(entry.file), name: entry.name, kind: DocumentKind(rawValue: entry.kind)!,
                                           pages: entry.pages, ocr: entry.ocr, meetingId: id, db: db)
            }
            meeting = try db.meeting(id) ?? meeting
        } catch {
            try? db.deleteMeeting(meeting.id!)
            try? FileManager.default.removeItem(at: meeting.audioURL)
            throw error
        }
        return meeting
    }

    private static func ditto(_ arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        process.arguments = arguments
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw PackageError.invalid(String(localized: "壓縮檔讀寫失敗")) }
    }
}

/// 匯入逐字稿（srt、vtt、txt，R3.4）
enum TranscriptImporter {
    struct Parsed { var segments: [(start: Double, end: Double, text: String)] }

    static func parse(_ text: String, ext: String) -> Parsed {
        if ext == "txt" {
            let lines = text.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            return Parsed(segments: lines.map { (0, 0, $0) })
        }
        // srt／vtt：時間碼行之後到空行之前是文字
        var segments: [(Double, Double, String)] = []
        let blocks = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n\n")
        for block in blocks {
            let lines = block.components(separatedBy: "\n")
            guard let index = lines.firstIndex(where: { $0.contains("-->") }) else { continue }
            let times = lines[index].components(separatedBy: "-->").map { $0.trimmingCharacters(in: .whitespaces) }
            guard times.count == 2, let start = seconds(times[0]), let end = seconds(times[1].components(separatedBy: " ")[0]) else { continue }
            let content = lines[(index + 1)...].joined(separator: " ").trimmingCharacters(in: .whitespaces)
            if !content.isEmpty { segments.append((start, end, content)) }
        }
        return Parsed(segments: segments)
    }

    /// 00:01:02,345 或 00:01:02.345 或 01:02.345
    static func seconds(_ text: String) -> Double? {
        let parts = text.replacingOccurrences(of: ",", with: ".").split(separator: ":").map { Double($0) }
        guard !parts.isEmpty, parts.allSatisfy({ $0 != nil }) else { return nil }
        return parts.compactMap { $0 }.reduce(0) { $0 * 60 + $1 }
    }
}
