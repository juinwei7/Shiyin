import SwiftUI

/// 頭像（design §58.2、R83）：圓形的釉色底，寫名字的第一個字。
/// 會議裡的說話者用 `Color.speaker(編號)`，人物頁、側欄用 `Color.person(名字)`
struct PersonAvatar: View {
    @Environment(\.textScale) private var textScale
    let name: String
    let color: Color
    var size: CGFloat = 18
    var isMe = false
    /// 會議內容裡的頭像跟著文字大小（R59）放大；側欄、人物頁的標題不跟
    var scales = true

    var body: some View {
        let diameter = size * (scales ? textScale : 1)
        Text(Self.initials(name))
            .font(.system(size: diameter * 0.46, weight: .bold))
            .foregroundStyle(color)
            .lineLimit(1)
            .minimumScaleFactor(0.6)
            .frame(width: diameter, height: diameter)
            .background(color.opacity(0.18), in: Circle())
            .overlay {
                // 「我」（R80）外圈一圈青瓷綠
                if isMe { Circle().strokeBorder(Color.celadon, lineWidth: min(2, max(1.5, diameter / 14))) }
            }
            .accessibilityHidden(true)
    }

    /// 寫在頭像上的字：預設名稱寫數字；中日韓文字取第一個字；其他取前兩段的第一個字母
    static func initials(_ name: String) -> String {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.first, let scalar = first.unicodeScalars.first else { return "?" }
        if AppDatabase.isDefaultSpeakerName(trimmed) {
            let digits = trimmed.filter(\.isNumber)
            if !digits.isEmpty { return String(digits.prefix(2)) }
        }
        if isCJK(scalar) { return String(first) }
        let words = trimmed.split { $0.isWhitespace || $0 == "-" || $0 == "." || $0 == "_" }
        let letters = words.prefix(2).compactMap(\.first).map { String($0).uppercased() }
        return letters.isEmpty ? String(first) : letters.joined()
    }

    private static func isCJK(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xAC00...0xD7AF, 0xF900...0xFAFF, 0x20000...0x2FA1F: return true
        default: return false
        }
    }
}

extension Color {
    /// 人物頁、側欄的頭像顏色（design §58.1）：照名字算，同一個名字每次都一樣
    static func person(_ name: String) -> Color {
        var hash: UInt32 = 2_166_136_261
        for byte in name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased().utf8 {
            hash = (hash ^ UInt32(byte)) &* 16_777_619
        }
        return speaker(Int(hash % 8) + 1)
    }
}
