import SwiftUI

/// 重新辨識說話者（design §23.4、R22）：選大約幾個人，只重新分辨誰在說話，逐字稿的文字不變
struct RediarizeSheet: View {
    @Environment(AppModel.self) private var model
    @Environment(\.dismiss) private var dismiss
    /// 0 是自動判斷
    @State private var count = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("重新辨識說話者").font(.title3.weight(.semibold))
            Text("只重新分辨誰在說話，逐字稿的文字不會變。人多、吵雜或手機遠距收音時，告訴拾音大約幾個人會分得比較準。")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Picker("大約幾個人", selection: $count) {
                Text("自動判斷").tag(0)
                ForEach(2...12, id: \.self) { n in Text("\(n) 人").tag(n) }
            }
            .frame(width: 260)
            Text("原本取過的名字會盡量保留，其他說話者會用聲紋比對、再用整理工具猜名字。會議紀錄不會自動更新。")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("開始") {
                    if let id = model.rediarizeMeetingId { model.rediarize(meetingId: id, minSpeakers: count == 0 ? nil : count) }
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
            }
        }
        .padding(24)
        .frame(width: 440)
        .tint(.celadon)
        .onAppear {
            // 預設是這場記住的人數
            if let id = model.rediarizeMeetingId, let n = (try? model.db.meeting(id))??.minSpeakers { count = n }
        }
    }
}
