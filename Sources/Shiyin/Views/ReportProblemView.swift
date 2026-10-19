import AppKit
import SwiftUI

/// 回報問題（design §21.9、R19.7）：先讓使用者看會附上的資訊，按「在 GitHub 回報」才打開瀏覽器
struct ReportProblemView: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var text = ""
    @State private var copied = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("回報問題").font(.songti(20))
            Text("按「在 GitHub 回報」會用瀏覽器打開新的 issue，並帶入下面的內容，你可以在瀏覽器裡修改後再送出。內容只有版本、環境和錯誤的種類，不包含會議內容、檔名或 API key。沒有 GitHub 帳號的話，可以按「複製」用其他方式寄給維護的人。")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            ScrollView {
                Text(text)
                    .font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(10)
            }
            .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
            HStack {
                if copied { Label("已複製", systemImage: "checkmark").foregroundStyle(.secondary) }
                Spacer()
                Button("關閉") { dismissWindow(id: "report") }
                    .keyboardShortcut(.cancelAction)
                Button("複製") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(text, forType: .string)
                    copied = true
                }
                Button("在 GitHub 回報") {
                    if let url = ProblemReport.url(body: text) { NSWorkspace.shared.open(url) }
                    dismissWindow(id: "report")
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(minWidth: 520, minHeight: 440)
        .onAppear {
            text = ProblemReport.text(db: model.db)
            copied = false
        }
    }
}
