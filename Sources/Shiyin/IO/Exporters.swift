import AVFoundation
import AppKit
import Foundation

/// 匯出用的完整資料
struct ExportBundle {
    var meeting: Meeting
    var segments: [Segment]
    var speakers: [Int: String]
    var bookmarks: [Bookmark]
    var notes: MeetingNotes?

    func speakerName(_ idx: Int?) -> String? { idx.map { speakers[$0] ?? text.speaker($0) } }

    /// 匯出內容的標題、欄位名稱：跟著會議語言，不是介面語言（design §16.6）
    var text: ExportText { ExportText(meeting.meetingLanguage) }
}

/// 匯出檔裡的固定文字（design §16.6）
struct ExportText {
    let language: MeetingLanguage
    init(_ language: MeetingLanguage) { self.language = language }

    var transcriptSuffix: String { language.text(zh: "（逐字稿）", en: " (transcript)") }
    var date: String { language.text(zh: "日期", en: "Date") }
    var participants: String { language.text(zh: "與會者", en: "Participants") }
    var speakers: String { language.text(zh: "說話者", en: "Speakers") }
    var duration: String { language.text(zh: "長度", en: "Duration") }
    var noNotes: String { language.text(zh: "（尚未整理成會議紀錄）", en: "(No meeting notes yet)") }
    var details: String { language.text(zh: "詳細", en: "Details") }
    var review: String { language.text(zh: "待確認", en: "To confirm") }
    var owner: String { language.text(zh: "負責人", en: "Owner") }
    var task: String { language.text(zh: "事項", en: "Item") }
    var due: String { language.text(zh: "期限", en: "Due") }
    var time: String { language.text(zh: "時間點", en: "Time") }
    var unassigned: String { language.text(zh: "未指定", en: "Unassigned") }
    var colon: String { language.text(zh: "：", en: ": ") }
    var listSeparator: String { language.text(zh: "、", en: ", ") }
    func speaker(_ idx: Int) -> String { language.text(zh: "說話者 \(idx)", en: "Speaker \(idx)") }
    func section(_ id: String) -> String { TemplateDefinition.sectionLabel(id, language: language) }
    /// 括號：中文用全形、英文用半形加空白
    func paren(_ inner: String) -> String { language.text(zh: "（\(inner)）", en: " (\(inner))") }
    func dateLine(_ meetingDate: String) -> String { meetingDate + paren(DateText.weekday(meetingDate, language: language)) }
}

enum ExportItem: String, CaseIterable, Identifiable {
    case notes, transcript, audio, package
    var id: String { rawValue }
    var label: String {
        switch self {
        case .notes: return String(localized: "會議紀錄")
        case .transcript: return String(localized: "逐字稿")
        case .audio: return String(localized: "錄音")
        case .package: return String(localized: "整場會議（.shiyin）")
        }
    }
    var formats: [String] {
        switch self {
        case .notes: return ["md", "pdf", "docx"]
        case .transcript: return ["txt", "md", "srt", "vtt", "docx"]
        case .audio: return ["m4a", "wav"]
        case .package: return ["shiyin"]
        }
    }
}

enum ExportError: LocalizedError {
    case noAudio, failed(String)
    var errorDescription: String? {
        switch self {
        case .noAudio: return String(localized: "這場會議沒有錄音檔")
        case .failed(let reason): return String(localized: "匯出失敗：\(reason)")
        }
    }
}

// MARK: - 逐字稿（R7.2）

enum TranscriptExporter {
    static func text(_ b: ExportBundle, format: String, times: Bool, speakers: Bool) -> String {
        switch format {
        case "srt":
            return b.segments.enumerated().map { i, s in
                "\(i + 1)\n\(TimeFormat.subtitle(s.start, separator: ",")) --> \(TimeFormat.subtitle(max(s.end, s.start + 0.5), separator: ","))\n\(line(b, s, times: false, speakers: speakers))\n"
            }.joined(separator: "\n")
        case "vtt":
            return "WEBVTT\n\n" + b.segments.map { s in
                "\(TimeFormat.subtitle(s.start, separator: ".")) --> \(TimeFormat.subtitle(max(s.end, s.start + 0.5), separator: "."))\n\(line(b, s, times: false, speakers: speakers))\n"
            }.joined(separator: "\n")
        case "md":
            let x = b.text
            return "# \(b.meeting.displayTitle)\(x.transcriptSuffix)\n\n- \(x.date)\(x.colon)\(x.dateLine(b.meeting.meetingDate))\n\n"
                + b.segments.map { line(b, $0, times: times, speakers: speakers, markdown: true) }.joined(separator: "\n\n") + "\n"
        default:
            return b.segments.map { line(b, $0, times: times, speakers: speakers) }.joined(separator: "\n") + "\n"
        }
    }

    private static func line(_ b: ExportBundle, _ s: Segment, times: Bool, speakers: Bool, markdown: Bool = false) -> String {
        var prefix = ""
        if times { prefix += markdown ? "`\(TimeFormat.string(s.start))` " : "[\(TimeFormat.string(s.start))] " }
        if speakers, let name = b.speakerName(s.speaker) { prefix += markdown ? "**\(name)**\(b.text.colon)" : "\(name)\(b.text.colon)" }
        return prefix + s.text
    }

    static func html(_ b: ExportBundle, times: Bool, speakers: Bool) -> String {
        let body = b.segments.map { s -> String in
            var prefix = ""
            if times { prefix += "<span class=\"time\">\(TimeFormat.string(s.start))</span> " }
            if speakers, let name = b.speakerName(s.speaker) { prefix += "<b>\(HTML.escape(name))</b>\(b.text.colon)" }
            return "<p>\(prefix)\(HTML.escape(s.text))</p>"
        }.joined(separator: "\n")
        let x = b.text
        return HTML.document(title: b.meeting.displayTitle + x.transcriptSuffix,
                             body: "<h1>\(HTML.escape(b.meeting.displayTitle + x.transcriptSuffix))</h1>\n<p class=\"meta\">\(b.meeting.meetingDate)</p>\n" + body,
                             language: x.language)
    }
}

// MARK: - 會議紀錄（R7.3）

enum NotesExporter {
    static func markdown(_ b: ExportBundle) -> String {
        let m = b.meeting
        let x = b.text
        var out = ["# \(m.displayTitle)", ""]
        out.append("- \(x.date)\(x.colon)\(x.dateLine(m.meetingDate))")
        if !m.participantList.isEmpty { out.append("- \(x.participants)\(x.colon)\(m.participantList.joined(separator: x.listSeparator))") }
        let speakerNames = b.speakers.sorted { $0.key < $1.key }.map(\.value)
        if !speakerNames.isEmpty { out.append("- \(x.speakers)\(x.colon)\(speakerNames.joined(separator: x.listSeparator))") }
        if m.durationSeconds != nil { out.append("- \(x.duration)\(x.colon)\(TimeFormat.duration(m.durationSeconds, language: x.language))") }
        guard let notes = b.notes, !notes.isEmpty else {
            out += ["", x.noNotes]
            return out.joined(separator: "\n") + "\n"
        }
        func t(_ time: String) -> String { time.isEmpty ? "" : x.paren(time) }
        func section(_ title: String, _ items: [MeetingNotes.Item]) {
            guard !items.isEmpty else { return }
            out += ["", "## \(title)"] + items.map { "- \($0.text)\(t($0.time))" }
        }
        section(x.section("summary"), notes.summary)
        section(x.section("decisions"), notes.decisions)
        if !notes.actionItems.isEmpty {
            out += ["", "## \(x.section("actionItems"))", "| \(x.owner) | \(x.task) | \(x.due) | \(x.time) |", "|---|---|---|---|"]
            for item in notes.actionItems {
                out.append("| \(cell(item.owner)) | \(cell(item.task)) | \(cell(item.due)) | \(cell(item.time)) |")
            }
        }
        section(x.section("openQuestions"), notes.openQuestions)
        for custom in notes.custom { section(custom.title, custom.items) }
        if !notes.topics.isEmpty {
            out += ["", "## \(x.details)"]
            for topic in notes.topics {
                out += ["", "### [\(topic.start)] \(topic.title)"] + topic.points.map { "- \($0.text)\(t($0.time))" }
            }
        }
        if !notes.reviewItems.isEmpty { out += ["", "## \(x.review)"] + notes.reviewItems.map { "- [\($0.time)] \($0.question)" } }
        return out.joined(separator: "\n") + "\n"
    }

    /// 純文字（貼到 Slack、訊息）
    static func plain(_ b: ExportBundle) -> String {
        markdown(b).replacingOccurrences(of: "## ", with: "").replacingOccurrences(of: "### ", with: "").replacingOccurrences(of: "# ", with: "")
    }

    static func html(_ b: ExportBundle) -> String {
        let m = b.meeting
        let x = b.text
        var body = "<h1>\(HTML.escape(m.displayTitle))</h1>\n<p class=\"meta\">\(x.dateLine(m.meetingDate))"
        if !m.participantList.isEmpty {
            body += x.language.text(zh: "　", en: " · ") + "\(x.participants)\(x.colon)\(HTML.escape(m.participantList.joined(separator: x.listSeparator)))"
        }
        body += "</p>\n"
        guard let notes = b.notes, !notes.isEmpty else {
            return HTML.document(title: m.displayTitle, body: body + "<p>\(x.noNotes)</p>", language: x.language)
        }
        func t(_ time: String) -> String { time.isEmpty ? "" : " <span class=\"time\">\(time)</span>" }
        func list(_ title: String, _ items: [MeetingNotes.Item]) {
            guard !items.isEmpty else { return }
            body += "<h2>\(HTML.escape(title))</h2>\n<ul>" + items.map { "<li>\(HTML.escape($0.text))\(t($0.time))</li>" }.joined() + "</ul>\n"
        }
        list(x.section("summary"), notes.summary)
        list(x.section("decisions"), notes.decisions)
        if !notes.actionItems.isEmpty {
            body += "<h2>\(x.section("actionItems"))</h2>\n<table><tr><th>\(x.owner)</th><th>\(x.task)</th><th>\(x.due)</th></tr>"
            body += notes.actionItems.map { "<tr><td>\(HTML.escape($0.owner.isEmpty ? x.unassigned : $0.owner))</td><td>\(HTML.escape($0.task))\(t($0.time))</td><td>\(HTML.escape($0.due.isEmpty ? "—" : $0.due))</td></tr>" }.joined()
            body += "</table>\n"
        }
        list(x.section("openQuestions"), notes.openQuestions)
        for custom in notes.custom { list(custom.title, custom.items) }
        if !notes.topics.isEmpty {
            body += "<h2>\(x.details)</h2>\n"
            for topic in notes.topics {
                body += "<h3><span class=\"time\">\(topic.start)</span> \(HTML.escape(topic.title))</h3>\n<ul>"
                    + topic.points.map { "<li>\(HTML.escape($0.text))\(t($0.time))</li>" }.joined() + "</ul>\n"
            }
        }
        if !notes.reviewItems.isEmpty {
            body += "<h2>\(x.review)</h2>\n<ul>" + notes.reviewItems.map { "<li><span class=\"time\">\($0.time)</span> \(HTML.escape($0.question))</li>" }.joined() + "</ul>\n"
        }
        return HTML.document(title: m.displayTitle, body: body, language: x.language)
    }

    private static func cell(_ text: String) -> String {
        text.isEmpty ? "—" : text.replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ")
    }
}

// MARK: - HTML → PDF、docx、剪貼簿（design §10）

enum HTML {
    static func escape(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;").replacingOccurrences(of: "\"", with: "&quot;")
    }

    /// 英文用系統字和襯線標題，中文用蘋方和宋體（design §16.6）
    static func document(title: String, body: String, language: MeetingLanguage = .zh) -> String {
        let bodyFont = language == .en ? "-apple-system, \"PingFang TC\", sans-serif" : "\"PingFang TC\", -apple-system, sans-serif"
        let headingFont = language == .en ? "\"New York\", Georgia, \"Songti TC\", serif" : "\"Songti TC\", serif"
        return """
        <!doctype html><html lang="\(language == .en ? "en" : "zh-Hant")"><head><meta charset="utf-8"><title>\(escape(title))</title><style>
        body { font-family: \(bodyFont); font-size: 12pt; line-height: 1.6; color: #1E2625; }
        h1, h2, h3 { font-family: \(headingFont); font-weight: 600; }
        h1 { font-size: 22pt; margin-bottom: 4pt; } h2 { font-size: 15pt; margin-top: 18pt; } h3 { font-size: 13pt; }
        .meta { color: #68726F; font-size: 10pt; }
        .time { color: #2D6A5D; font-size: 9.5pt; }
        table { border-collapse: collapse; width: 100%; } th, td { border-bottom: 1px solid #DCE1DE; padding: 4pt 6pt; text-align: left; vertical-align: top; }
        th { color: #68726F; font-weight: normal; font-size: 10pt; }
        </style></head><body>\(body)</body></html>
        """
    }

    @MainActor
    static func attributed(_ html: String) throws -> NSAttributedString {
        guard let data = html.data(using: .utf8),
              let string = NSAttributedString(html: data, options: [.characterEncoding: String.Encoding.utf8.rawValue],
                                              documentAttributes: nil)
        else { throw ExportError.failed(String(localized: "無法產生文件")) }
        return string
    }

    @MainActor
    static func docx(_ html: String) throws -> Data {
        let string = try attributed(html)
        return try string.data(from: NSRange(location: 0, length: string.length),
                               documentAttributes: [.documentType: NSAttributedString.DocumentType.officeOpenXML])
    }

    /// 用 NSTextView 排版後印成 PDF（自動分頁，A4）
    @MainActor
    static func pdf(_ html: String, to url: URL) throws {
        let string = try attributed(html)
        let info = NSPrintInfo()
        info.paperSize = NSSize(width: 595, height: 842)
        info.topMargin = 56; info.bottomMargin = 56; info.leftMargin = 56; info.rightMargin = 56
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.isVerticallyCentered = false
        info.jobDisposition = .save
        info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = url
        let width = info.paperSize.width - info.leftMargin - info.rightMargin
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
        view.textStorage?.setAttributedString(string)
        view.isVerticallyResizable = true
        view.sizeToFit()
        view.layoutManager?.ensureLayout(for: view.textContainer!)
        let used = view.layoutManager?.usedRect(for: view.textContainer!).height ?? 100
        view.frame.size.height = used + 20
        let operation = NSPrintOperation(view: view, printInfo: info)
        operation.showsPrintPanel = false
        operation.showsProgressPanel = false
        guard operation.run() else { throw ExportError.failed(String(localized: "PDF 產生失敗")) }
    }

    /// 同時放純文字、HTML、RTF，貼到哪裡都有合適的格式（R7.3）
    @MainActor
    static func copy(plain: String, html: String) {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(plain, forType: .string)
        board.setString(html, forType: .html)
        if let rtf = try? attributed(html).rtf(from: NSRange(location: 0, length: (try? attributed(html).length) ?? 0)) {
            board.setData(rtf, forType: .rtf)
        }
    }
}

// MARK: - 錄音（R7.1）

enum AudioExporter {
    static func export(_ b: ExportBundle, format: String, to url: URL) async throws {
        guard b.meeting.hasAudio else { throw ExportError.noAudio }
        let source = b.meeting.audioURL
        try? FileManager.default.removeItem(at: url)
        if format == source.pathExtension.lowercased() {
            try FileManager.default.copyItem(at: source, to: url)
        } else if format == "m4a" {
            guard let session = AVAssetExportSession(asset: AVURLAsset(url: source), presetName: AVAssetExportPresetAppleM4A)
            else { throw ExportError.failed(String(localized: "無法建立轉檔")) }
            try await session.export(to: url, as: .m4a)
        } else {
            // wav：解碼成 16 kHz 單聲道（轉錄用的格式，音質足夠聽清楚人聲）
            let samples = try await AudioDecoder.decode(source)
            try AudioDecoder.writeWAV(samples, to: url)
        }
    }
}
