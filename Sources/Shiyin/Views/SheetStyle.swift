import SwiftUI

// MARK: - 彈窗的共用樣子（R106、design §80）

/// 彈窗的標題：大標題、一行灰字說明、右上角 ✕
struct SheetHeader: View {
    let title: String
    var subtitle: String? = nil
    /// 標題列右邊的小字（「第 1 組，共 2 組」）
    var trailing: String? = nil
    /// ✕ 做什麼；nil 是關掉彈窗
    var onClose: (() -> Void)? = nil
    /// 沒有「取消」的彈窗：Esc 等於按 ✕（有「取消」的彈窗 Esc 是那個按鈕，不能兩個都設）
    var escCloses = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(.title2).fontWeight(.bold)
                if let subtitle {
                    Text(subtitle).font(.body).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
            if let trailing { Text(trailing).font(.callout).foregroundStyle(.secondary).padding(.top, 4) }
            Button { if let onClose { onClose() } else { dismiss() } } label: {
                Image(systemName: "xmark").font(.system(size: 14, weight: .medium))
            }
            .buttonStyle(.plain)
            .foregroundStyle(.secondary)
            .padding(.top, 4)
            .help("關閉")
            .accessibilityLabel(Text("關閉"))
            .modifier(CancelShortcut(enabled: escCloses))
        }
    }
}

private struct CancelShortcut: ViewModifier {
    let enabled: Bool
    func body(content: Content) -> some View {
        if enabled { content.keyboardShortcut(.cancelAction) } else { content }
    }
}

/// 輸入框：細框圓角 10，有焦點時框變亮（不用青瓷綠的焦點框）
struct SheetTextField: View {
    let placeholder: String
    @Binding var text: String
    var focusOnAppear = false
    var onSubmit: () -> Void = {}
    @FocusState private var focused: Bool

    var body: some View {
        TextField(placeholder, text: $text)
            .textFieldStyle(.plain)
            .font(.system(size: 15))
            .focused($focused)
            .focusEffectDisabled()
            .sheetFieldBox(focused: focused)
            .onSubmit(onSubmit)
            // 等外面的 onAppear 把字填好再給焦點，字才會全選
            .onAppear { if focusOnAppear { DispatchQueue.main.async { focused = true } } }
    }
}

/// 就地編輯、頁面裡的輸入框（逐字稿改句子、待確認的回答、提問）用同一種框：`.textFieldStyle(.sheet)`；
/// compact 是放在清單裡、小一號的
struct SheetFieldStyle: TextFieldStyle {
    var compact = false

    func _body(configuration: TextField<Self._Label>) -> some View {
        FocusedFieldBox(compact: compact) { configuration }
    }
}

extension TextFieldStyle where Self == SheetFieldStyle {
    static var sheet: SheetFieldStyle { SheetFieldStyle() }
    static var sheetCompact: SheetFieldStyle { SheetFieldStyle(compact: true) }
}

private struct FocusedFieldBox<Content: View>: View {
    let compact: Bool
    @ViewBuilder let content: Content
    @FocusState private var focused: Bool

    var body: some View {
        content
            .textFieldStyle(.plain)
            .focused($focused)
            .focusEffectDisabled()
            .sheetFieldBox(focused: focused, compact: compact)
    }
}

extension View {
    /// 輸入框的外框（多行的、選單式的欄位也用）
    func sheetFieldBox(focused: Bool = false, compact: Bool = false) -> some View {
        let shape = RoundedRectangle(cornerRadius: compact ? 8 : 10, style: .continuous)
        return padding(.horizontal, compact ? 9 : 12)
            .padding(.vertical, compact ? 5 : 9)
            .background(shape.fill(Color.primary.opacity(0.04)))
            .overlay(shape.strokeBorder(Color.primary.opacity(focused ? 0.75 : 0.25), lineWidth: focused ? 1.5 : 1))
    }

    /// 彈窗的外距、寬度
    func sheetChrome(width: CGFloat = 440) -> some View {
        padding(24).frame(width: width)
    }
}

/// 選單式的欄位（範本這類選一個）：外框同輸入框，右邊上下箭頭
struct SheetMenuField<Value: Hashable>: View {
    let options: [(value: Value, title: String)]
    @Binding var selection: Value
    /// 不能選的（例如沒有錄音時的「錄音檔」）
    var isDisabled: (Value) -> Bool = { _ in false }

    var body: some View {
        Menu {
            ForEach(options, id: \.value) { option in
                Button(option.title) { selection = option.value }
                    .disabled(isDisabled(option.value))
            }
        } label: {
            HStack {
                Text(options.first { $0.value == selection }?.title ?? "").font(.system(size: 15)).lineLimit(1)
                Spacer(minLength: 8)
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
            }
            .foregroundStyle(.primary)
            .contentShape(Rectangle())
            .sheetFieldBox()
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
    }
}

/// 日期欄位：外框同輸入框，裡面是沒有框的日期（可以打字、↑↓ 改；系統的日期欄位自己有框，放進來會框中框），
/// 右邊的日曆打開月曆選
struct SheetDateField: View {
    let label: String
    @Binding var date: Date
    @State private var showsCalendar = false

    var body: some View {
        HStack {
            // 日期欄位自己有一點內距、比一行字高：拉回來跟其他輸入框的字對齊、同高
            PlainDatePicker(label: label, date: $date)
                .padding(.leading, -3)
                .padding(.vertical, -2)
            Spacer(minLength: 8)
            Button { showsCalendar.toggle() } label: {
                Image(systemName: "calendar").font(.system(size: 14)).foregroundStyle(.secondary)
            }
            .buttonStyle(.plain)
            .help("選日期")
            .accessibilityLabel(Text("選日期"))
            .popover(isPresented: $showsCalendar, arrowEdge: .bottom) {
                DatePicker(label, selection: $date, displayedComponents: .date)
                    .datePickerStyle(.graphical)
                    .labelsHidden()
                    .padding(12)
            }
        }
        .sheetFieldBox()
    }
}

private struct PlainDatePicker: NSViewRepresentable {
    let label: String
    @Binding var date: Date

    func makeNSView(context: Context) -> NSDatePicker {
        let picker = NSDatePicker()
        picker.datePickerStyle = .textField
        picker.datePickerElements = .yearMonthDay
        picker.isBezeled = false
        picker.isBordered = false
        picker.drawsBackground = false
        picker.font = .systemFont(ofSize: 15)
        picker.setAccessibilityLabel(label)
        picker.target = context.coordinator
        picker.action = #selector(Coordinator.changed(_:))
        return picker
    }

    func updateNSView(_ picker: NSDatePicker, context: Context) {
        context.coordinator.date = $date
        if picker.dateValue != date { picker.dateValue = date }
    }

    func makeCoordinator() -> Coordinator { Coordinator(date: $date) }

    @MainActor
    final class Coordinator: NSObject {
        var date: Binding<Date>
        init(date: Binding<Date>) { self.date = date }
        @objc func changed(_ sender: NSDatePicker) { date.wrappedValue = sender.dateValue }
    }
}

/// 欄位上面的灰色小標籤
struct SheetFieldLabel: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View { Text(text).font(.callout).foregroundStyle(.secondary) }
}

/// 彈窗的按鈕：圓角 10、高 34。主要按鈕用字的顏色當底（深色模式白底黑字），次要的淡底細框，刪除類紅底白字；
/// compact 是就地編輯用的小一號（高 26）
struct SheetPillButtonStyle: ButtonStyle {
    enum Kind { case primary, secondary, destructive }
    var kind: Kind
    var compact = false
    @Environment(\.isEnabled) private var isEnabled

    init(_ kind: Kind, compact: Bool = false) { self.kind = kind; self.compact = compact }
    init(prominent: Bool) { kind = prominent ? .primary : .secondary }

    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: compact ? 8 : 10, style: .continuous)
        configuration.label
            .font(compact ? .callout.weight(.medium) : .body.weight(.medium))
            .padding(.horizontal, compact ? 12 : 18)
            .frame(minWidth: compact ? 52 : 68, minHeight: compact ? 26 : 34)
            .foregroundStyle(foreground)
            .background(shape.fill(background))
            .overlay(shape.strokeBorder(kind == .secondary ? Color.primary.opacity(0.18) : Color.clear))
            .contentShape(shape)
            .opacity(isEnabled ? (configuration.isPressed ? 0.75 : 1) : 0.35)
    }

    private var foreground: Color {
        switch kind {
        case .primary: Color(nsColor: .windowBackgroundColor)
        case .secondary: .primary
        case .destructive: .white
        }
    }

    private var background: Color {
        switch kind {
        case .primary: .primary
        case .secondary: Color.primary.opacity(0.06)
        case .destructive: Color(nsColor: .systemRed)
        }
    }
}

// MARK: - 確認

/// 要確認的事（刪除、移除、分成兩場…）：取代系統的確認對話框，樣子同其他彈窗
struct ConfirmRequest: Identifiable {
    let id: String
    let title: String
    let message: String
    let confirmTitle: String
    var destructive = true
    let action: () -> Void
}

struct ConfirmSheet: View {
    let request: ConfirmRequest
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 22) {
            SheetHeader(title: request.title, subtitle: request.message)
            HStack(spacing: 10) {
                Spacer()
                Button("取消") { dismiss() }
                    .buttonStyle(SheetPillButtonStyle(.secondary))
                    .keyboardShortcut(.cancelAction)
                Button(request.confirmTitle) {
                    dismiss()
                    request.action()
                }
                .buttonStyle(SheetPillButtonStyle(request.destructive ? .destructive : .primary))
                .keyboardShortcut(.defaultAction)
            }
        }
        .sheetChrome()
    }
}
