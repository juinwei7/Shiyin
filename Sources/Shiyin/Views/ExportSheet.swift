import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// 匯出對話框（R7）：選項目、格式、選項，再存檔或分享
struct ExportSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    @State private var item: ExportItem = .notes
    @State private var format = "md"
    @State private var times = true
    @State private var speakers = true
    @State private var working = false
    /// 「分享…」的位置：分享選單（Mail、AirDrop、訊息；R7.5）從按鈕下面打開
    @State private var shareAnchor = MenuAnchorBox()
    /// 打開時那一場會議的資料（design §20.9）：對話框開著時選取的會議換了也不變
    @State private var bundle: ExportBundle?

    var body: some View {
        // 同其他彈窗的樣子（R106、design §80）
        VStack(alignment: .leading, spacing: 16) {
            SheetHeader(title: bundle.map { String(localized: "匯出「\($0.meeting.displayTitle)」") } ?? String(localized: "匯出"))
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "項目"))
                    SheetMenuField(options: ExportItem.allCases.map { ($0, $0.label) }, selection: $item) { item in
                        item == .audio && bundle?.meeting.hasAudio != true || item == .mindMap && !hasMindMap
                    }
                }
                VStack(alignment: .leading, spacing: 6) {
                    SheetFieldLabel(String(localized: "格式"))
                    SheetMenuField(options: item.formats.map { ($0, formatLabel($0)) }, selection: $format)
                        .disabled(item.formats.count == 1)
                }
            }
            if item == .transcript {
                HStack(spacing: 18) {
                    Toggle("包含時間點", isOn: $times).disabled(format == "srt" || format == "vtt")
                    Toggle("包含說話者", isOn: $speakers)
                }
                .toggleStyle(.checkbox)
            }
            Text(hint)
                .font(.callout)
                .foregroundStyle(item == .notes && !hasNotes ? Color.signal : .secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("分享…") { Task { await share() } }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .background(MenuAnchor(box: shareAnchor))
                    .disabled(working)
                if working {
                    ProgressView().controlSize(.small)
                    Text("正在匯出…").foregroundStyle(.secondary)
                }
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("存檔…") { Task { await save() } }
                    .buttonStyle(SheetPillButtonStyle(.primary))
                    .keyboardShortcut(.defaultAction)
                    .disabled(working)
            }
        }
        .sheetChrome(width: 480)
        .onChange(of: item) { _, newValue in format = newValue.formats[0] }
        // 還沒有會議紀錄（整理中或整理失敗）時，預設改匯出逐字稿（使用者回報：匯出的會議紀錄只有標題）
        .onAppear {
            bundle = model.exportMeetingId.flatMap { try? model.exportBundle(meetingId: $0) }
            if !hasNotes { item = .transcript }
        }
    }

    private var hasNotes: Bool { bundle?.notes != nil }
    /// 心智圖要有內容（design §42.4）
    private var hasMindMap: Bool { bundle?.notes.map { !$0.isEmpty } ?? false }

    private var hint: String {
        switch item {
        case .notes:
            if !hasNotes {
                return String(localized: "這場會議還沒有會議紀錄（還在整理或整理失敗），匯出的會議紀錄只會有標題和基本資料。要留下內容，請改匯出逐字稿。")
            }
            return String(localized: "PDF 和 docx 的段落標題使用宋體；Markdown 適合貼到 Notion、GitHub。")
        case .mindMap: return String(localized: "依會議紀錄畫成心智圖。PNG 適合貼到聊天軟體、簡報；SVG 可以再用其他軟體編輯。")
        case .transcript: return String(localized: "srt、vtt 是字幕格式，可以搭配錄音或影片在播放器裡顯示。")
        case .audio: return String(localized: "m4a 檔案小、適合分享；wav 是 16 kHz 單聲道，適合給其他轉錄工具使用。")
        case .package: return String(localized: "包含錄音、逐字稿、說話者、會議紀錄、筆記和重點標記。傳給同事後，對方雙擊就能匯入拾音。")
        }
    }

    private func meetingId() throws -> Int64 {
        guard let id = model.exportMeetingId else { throw AppModel.ExportError.meetingGone }
        return id
    }

    private func formatLabel(_ format: String) -> String {
        ["md": "Markdown", "pdf": "PDF", "png": String(localized: "PNG 圖片"), "svg": "SVG", "docx": String(localized: "Word（docx）"), "txt": String(localized: "純文字（txt）"), "srt": String(localized: "字幕（srt）"),
         "vtt": String(localized: "字幕（vtt）"), "m4a": "m4a", "wav": "wav", "shiyin": String(localized: ".shiyin 封包")][format] ?? format
    }

    private func save() async {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = model.suggestedFilename(item, format: format, meeting: bundle?.meeting)
        if let type = UTType(filenameExtension: format) { panel.allowedContentTypes = [type] }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        working = true
        defer { working = false }
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, meetingId: try meetingId(), to: url)
            model.noticeMessage = String(localized: "已匯出 \(url.lastPathComponent)")
            dismiss()
        } catch {
            model.errorMessage = error.localizedDescription
        }
    }

    private func share() async {
        guard let url = await exportToTemp(), let view = shareAnchor.view else { return }
        NSSharingServicePicker(items: [url]).show(relativeTo: view.bounds, of: view, preferredEdge: .minY)
    }

    private func exportToTemp() async -> URL? {
        guard let dir = try? Paths.makeTempDirectory() else { return nil }
        let url = dir.appendingPathComponent(model.suggestedFilename(item, format: format, meeting: bundle?.meeting))
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, meetingId: try meetingId(), to: url)
            return url
        } catch {
            model.errorMessage = error.localizedDescription
            return nil
        }
    }
}
