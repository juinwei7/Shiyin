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

    func speakerName(_ idx: Int?) -> String? { idx.map { speakers[$0] ?? "說話者 \($0)" } }
}

enum ExportItem: String, CaseIterable, Identifiable {
    case notes, transcript, audio, package
    var id: String { rawValue }
    var label: String {
        switch self {
        case .notes: return "會議紀錄"
        case .transcript: return "逐字稿"
        case .audio: return "錄音"
        case .package: return "整場會議（.shiyin）"
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
        case .noAudio: return "這場會議沒有錄音檔"
        case .failed(let reason): return "匯出失敗：\(reason)"
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
            return "# \(b.meeting.displayTitle)（逐字稿）\n\n- 日期：\(b.meeting.meetingDate)（\(DateText.weekday(b.meeting.meetingDate))）\n\n"
                + b.segments.map { line(b, $0, times: times, speakers: speakers, markdown: true) }.joined(separator: "\n\n") + "\n"
        default:
            return b.segments.map { line(b, $0, times: times, speakers: speakers) }.joined(separator: "\n") + "\n"
        }
    }

    private static func line(_ b: ExportBundle, _ s: Segment, times: Bool, speakers: Bool, markdown: Bool = false) -> String {
        var prefix = ""
        if times { prefix += markdown ? "`\(TimeFormat.string(s.start))` " : "[\(TimeFormat.string(s.start))] " }
        if speakers, let name = b.speakerName(s.speaker) { prefix += markdown ? "**\(name)**：" : "\(name)：" }
        return prefix + s.text
    }

    static func html(_ b: ExportBundle, times: Bool, speakers: Bool) -> String {
        let body = b.segments.map { s -> String in
            var prefix = ""
            if times { prefix += "<span class=\"time\">\(TimeFormat.string(s.start))</span> " }
            if speakers, let name = b.speakerName(s.speaker) { prefix += "<b>\(HTML.escape(name))</b>：" }
            return "<p>\(prefix)\(HTML.escape(s.text))</p>"
        }.joined(separator: "\n")
        return HTML.document(title: "\(b.meeting.displayTitle)（逐字稿）", body: "<h1>\(HTML.escape(b.meeting.displayTitle))（逐字稿）</h1>\n<p class=\"meta\">\(b.meeting.meetingDate)</p>\n" + body)
    }
}

// MARK: - 會議紀錄（R7.3）

enum NotesExporter {
    static func markdown(_ b: ExportBundle) -> String {
        let m = b.meeting
        var out = ["# \(m.displayTitle)", ""]
        out.append("- 日期：\(m.meetingDate)（\(DateText.weekday(m.meetingDate))）")
        if !m.participantList.isEmpty { out.append("- 與會者：\(m.participantList.joined(separator: "、"))") }
        let speakerNames = b.speakers.sorted { $0.key < $1.key }.map(\.value)
        if !speakerNames.isEmpty { out.append("- 說話者：\(speakerNames.joined(separator: "、"))") }
        if m.durationSeconds != nil { out.append("- 長度：\(TimeFormat.duration(m.durationSeconds))") }
        guard let notes = b.notes, !notes.isEmpty else {
            out += ["", "（尚未整理成會議紀錄）"]
            return out.joined(separator: "\n") + "\n"
        }
        func t(_ time: String) -> String { time.isEmpty ? "" : "（\(time)）" }
        func section(_ title: String, _ items: [MeetingNotes.Item]) {
            guard !items.isEmpty else { return }
            out += ["", "## \(title)"] + items.map { "- \($0.text)\(t($0.time))" }
        }
        section("摘要", notes.summary)
        section("決議", notes.decisions)
        if !notes.actionItems.isEmpty {
            out += ["", "## 待辦", "| 負責人 | 事項 | 期限 | 時間點 |", "|---|---|---|---|"]
            for item in notes.actionItems {
                out.append("| \(cell(item.owner)) | \(cell(item.task)) | \(cell(item.due)) | \(cell(item.time)) |")
            }
        }
        section("未決問題", notes.openQuestions)
        for custom in notes.custom { section(custom.title, custom.items) }
        if !notes.topics.isEmpty {
            out += ["", "## 詳細"]
            for topic in notes.topics {
                out += ["", "### [\(topic.start)] \(topic.title)"] + topic.points.map { "- \($0.text)\(t($0.time))" }
            }
        }
        if !notes.reviewItems.isEmpty { out += ["", "## 待確認"] + notes.reviewItems.map { "- [\($0.time)] \($0.question)" } }
        return out.joined(separator: "\n") + "\n"
    }

    /// 純文字（貼到 Slack、訊息）
    static func plain(_ b: ExportBundle) -> String {
        markdown(b).replacingOccurrences(of: "## ", with: "").replacingOccurrences(of: "### ", with: "").replacingOccurrences(of: "# ", with: "")
    }

    static func html(_ b: ExportBundle) -> String {
        let m = b.meeting
        var body = "<h1>\(HTML.escape(m.displayTitle))</h1>\n<p class=\"meta\">\(m.meetingDate)（\(DateText.weekday(m.meetingDate))）"
        if !m.participantList.isEmpty { body += "　與會者：\(HTML.escape(m.participantList.joined(separator: "、")))" }
        body += "</p>\n"
        guard let notes = b.notes, !notes.isEmpty else {
            return HTML.document(title: m.displayTitle, body: body + "<p>（尚未整理成會議紀錄）</p>")
        }
        func t(_ time: String) -> String { time.isEmpty ? "" : " <span class=\"time\">\(time)</span>" }
        func list(_ title: String, _ items: [MeetingNotes.Item]) {
            guard !items.isEmpty else { return }
            body += "<h2>\(HTML.escape(title))</h2>\n<ul>" + items.map { "<li>\(HTML.escape($0.text))\(t($0.time))</li>" }.joined() + "</ul>\n"
        }
        list("摘要", notes.summary)
        list("決議", notes.decisions)
        if !notes.actionItems.isEmpty {
            body += "<h2>待辦</h2>\n<table><tr><th>負責人</th><th>事項</th><th>期限</th></tr>"
            body += notes.actionItems.map { "<tr><td>\(HTML.escape($0.owner.isEmpty ? "未指定" : $0.owner))</td><td>\(HTML.escape($0.task))\(t($0.time))</td><td>\(HTML.escape($0.due.isEmpty ? "—" : $0.due))</td></tr>" }.joined()
            body += "</table>\n"
        }
        list("未決問題", notes.openQuestions)
        for custom in notes.custom { list(custom.title, custom.items) }
        if !notes.topics.isEmpty {
            body += "<h2>詳細</h2>\n"
            for topic in notes.topics {
                body += "<h3><span class=\"time\">\(topic.start)</span> \(HTML.escape(topic.title))</h3>\n<ul>"
                    + topic.points.map { "<li>\(HTML.escape($0.text))\(t($0.time))</li>" }.joined() + "</ul>\n"
            }
        }
        if !notes.reviewItems.isEmpty {
            body += "<h2>待確認</h2>\n<ul>" + notes.reviewItems.map { "<li><span class=\"time\">\($0.time)</span> \(HTML.escape($0.question))</li>" }.joined() + "</ul>\n"
        }
        return HTML.document(title: m.displayTitle, body: body)
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

    static func document(title: String, body: String) -> String {
        """
        <!doctype html><html><head><meta charset="utf-8"><title>\(escape(title))</title><style>
        body { font-family: "PingFang TC", -apple-system, sans-serif; font-size: 12pt; line-height: 1.6; color: #1E2625; }
        h1, h2, h3 { font-family: "Songti TC", serif; font-weight: 600; }
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
        else { throw ExportError.failed("無法產生文件") }
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
        guard operation.run() else { throw ExportError.failed("PDF 產生失敗") }
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
            else { throw ExportError.failed("無法建立轉檔") }
            try await session.export(to: url, as: .m4a)
        } else {
            // wav：解碼成 16 kHz 單聲道（轉錄用的格式，音質足夠聽清楚人聲）
            let samples = try await AudioDecoder.decode(source)
            try AudioDecoder.writeWAV(samples, to: url)
        }
    }
}
