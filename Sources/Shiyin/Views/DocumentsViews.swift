import SwiftUI

/// 會議的參考文件（design §39.4、R61.1、R61.4）：會議標題下資訊列的選單，放在會議語言之後
struct DocumentsMenu: View {
    @Environment(AppModel.self) private var model
    let detail: MeetingDetail

    var body: some View {
        let reading = model.readingDocuments[detail.meeting.id ?? 0] ?? []
        Menu {
            ForEach(detail.documents) { document in
                Menu(title(document)) {
                    Button("打開") { model.openDocument(document) }
                    Button("移除") { model.removeDocument(document) }
                }
            }
            ForEach(Array(reading.enumerated()), id: \.offset) { _, name in
                Button("正在讀取「\(name)」…") {}.disabled(true)
            }
            if !detail.documents.isEmpty || !reading.isEmpty { Divider() }
            Button("加入文件…") { model.chooseDocuments() }
        } label: {
            Label(label(reading: !reading.isEmpty), systemImage: "paperclip")
        }
        .fixedSize()
        .help("會議中一起討論的文件（簡報、規格書、報價單…）。整理會議紀錄、提問時會一起參考；也可以把文件拖進視窗")
    }

    private func label(reading: Bool) -> String {
        if reading { return String(localized: "正在讀取文件…") }
        return detail.documents.isEmpty ? String(localized: "加入文件") : String(localized: "\(detail.documents.count) 份文件")
    }

    /// 「報價單.pdf（3 頁）」；照長度分配後被截掉的接「，太長，只用前 N 頁」（design §39.3）
    private func title(_ document: MeetingDocument) -> String {
        let use = detail.documentUse[document.id ?? 0]
        let pages = use?.document.pages.count ?? 0
        var text: String
        switch document.documentKind {
        case .pdf: text = String(localized: "\(document.name)（\(pages) 頁）")
        case .pptx: text = String(localized: "\(document.name)（\(pages) 張投影片）")
        case .docx, .text, .image: text = document.name
        }
        if let use, use.truncated {
            switch use.cutByPages ? document.documentKind : .text {
            case .pdf: text += String(localized: "，太長，只用前 \(use.includedPages) 頁")
            case .pptx: text += String(localized: "，太長，只用前 \(use.includedPages) 張")
            case .docx, .text, .image: text += String(localized: "，太長，只用前面的部分")
            }
        }
        return text
    }
}

/// 已經有會議紀錄、之後才加入或移除文件（R61.8）：紀錄分頁頂部提示重新整理
struct DocumentsBanner: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(spacing: 10) {
            Label("文件有變動，會議紀錄還沒用上。", systemImage: "paperclip")
            Spacer()
            Button("不用") { model.dismissDocumentsChanged() }
            Button("重新整理會議紀錄") { model.resummarize() }
                .buttonStyle(.borderedProminent)
        }
        .font(.callout)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color.celadon.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}
