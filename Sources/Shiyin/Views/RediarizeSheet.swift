import SwiftUI

/// 重新辨識說話者（design §23.4、R22）：選大約幾個人，只重新分辨誰在說話，逐字稿的文字不變
struct RediarizeSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    /// 0 是自動判斷
    @State private var count = 0

    var body: some View {
        // 同其他彈窗的樣子（R106、design §80）
        VStack(alignment: .leading, spacing: 18) {
            SheetHeader(title: String(localized: "重新辨識說話者"),
                        subtitle: String(localized: "只重新分辨誰在說話，逐字稿的文字不會變。人多、吵雜或手機遠距收音時，告訴拾音大約幾個人會分得比較準。"))
            VStack(alignment: .leading, spacing: 6) {
                SheetFieldLabel(String(localized: "大約幾個人"))
                SheetMenuField(options: [(0, String(localized: "自動判斷"))] + (2...12).map { ($0, String(localized: "\($0) 人")) },
                               selection: $count)
                    .frame(width: 220)
            }
            Text("原本取過的名字會盡量保留，其他說話者會用聲紋比對、再用整理工具猜名字。會議紀錄不會自動更新。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 10) {
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button("開始") {
                    if let id = model.rediarizeMeetingId { model.rediarize(meetingId: id, minSpeakers: count == 0 ? nil : count) }
                    dismiss()
                }
                .buttonStyle(SheetPillButtonStyle(.primary))
                .keyboardShortcut(.defaultAction)
            }
        }
        .sheetChrome()
        .onAppear {
            // 預設是這場記住的人數
            if let id = model.rediarizeMeetingId, let n = (try? model.db.meeting(id))??.minSpeakers { count = n }
        }
    }
}
