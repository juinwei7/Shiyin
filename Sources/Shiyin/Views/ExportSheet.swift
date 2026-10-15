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
    @State private var shareAnchor: NSView?

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("匯出").font(.title3.weight(.semibold))
            Form {
                Picker("項目", selection: $item) {
                    ForEach(ExportItem.allCases) { item in
                        Text(item.label).tag(item).disabled(item == .audio && model.detail?.meeting.hasAudio != true)
                    }
                }
                Picker("格式", selection: $format) {
                    ForEach(item.formats, id: \.self) { Text(formatLabel($0)).tag($0) }
                }
                .disabled(item.formats.count == 1)
                if item == .transcript {
                    Toggle("包含時間點", isOn: $times).disabled(format == "srt" || format == "vtt")
                    Toggle("包含說話者", isOn: $speakers)
                }
            }
            .formStyle(.columns)
            Text(hint)
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                ShareButton { await exportToTemp() }
                Spacer()
                Button("取消", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("存檔…") { Task { await save() } }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .disabled(working)
            }
        }
        .padding(20)
        .frame(width: 460)
        .onChange(of: item) { _, newValue in format = newValue.formats[0] }
    }

    private var hint: String {
        switch item {
        case .notes: return "PDF 和 docx 的段落標題使用宋體；Markdown 適合貼到 Notion、GitHub。"
        case .transcript: return "srt、vtt 是字幕格式，可以搭配錄音或影片在播放器裡顯示。"
        case .audio: return "m4a 檔案小、適合分享；wav 是 16 kHz 單聲道，適合給其他轉錄工具使用。"
        case .package: return "包含錄音、逐字稿、說話者、會議紀錄、筆記和重點標記。傳給同事後，對方雙擊就能匯入拾音。"
        }
    }

    private func formatLabel(_ format: String) -> String {
        ["md": "Markdown", "pdf": "PDF", "docx": "Word（docx）", "txt": "純文字（txt）", "srt": "字幕（srt）",
         "vtt": "字幕（vtt）", "m4a": "m4a", "wav": "wav", "shiyin": ".shiyin 封包"][format] ?? format
    }

    private func save() async {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = model.suggestedFilename(item, format: format)
        if let type = UTType(filenameExtension: format) { panel.allowedContentTypes = [type] }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        working = true
        defer { working = false }
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, to: url)
            model.noticeMessage = "已匯出 \(url.lastPathComponent)"
            dismiss()
        } catch {
            model.errorMessage = error.localizedDescription
        }
    }

    private func exportToTemp() async -> URL? {
        guard let dir = try? Paths.makeTempDirectory() else { return nil }
        let url = dir.appendingPathComponent(model.suggestedFilename(item, format: format))
        do {
            try await model.export(item, format: format, times: times, speakers: speakers, to: url)
            return url
        } catch {
            model.errorMessage = error.localizedDescription
            return nil
        }
    }
}

/// 分享按鈕（Mail、AirDrop、訊息；R7.5）
struct ShareButton: NSViewRepresentable {
    let makeFile: () async -> URL?

    func makeNSView(context: Context) -> NSButton {
        let button = NSButton(title: "分享…", target: context.coordinator, action: #selector(Coordinator.share(_:)))
        button.bezelStyle = .push
        return button
    }

    func updateNSView(_ nsView: NSButton, context: Context) {
        context.coordinator.makeFile = makeFile
    }

    func makeCoordinator() -> Coordinator { Coordinator(makeFile: makeFile) }

    @MainActor
    final class Coordinator: NSObject {
        var makeFile: () async -> URL?
        init(makeFile: @escaping () async -> URL?) { self.makeFile = makeFile }

        @objc func share(_ sender: NSButton) {
            Task {
                guard let url = await makeFile() else { return }
                NSSharingServicePicker(items: [url]).show(relativeTo: sender.bounds, of: sender, preferredEdge: .minY)
            }
        }
    }
}
