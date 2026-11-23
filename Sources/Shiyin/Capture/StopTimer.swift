import Foundation

/// 定時停止錄音（design §84、R110）：選項、輸入的時間、畫面上的字。Windows 的 StopTimer 寫同樣的規則和測試例子
enum StopTimer {
    /// 選單的選項：從現在起幾分鐘後
    static let presets: [(minutes: Int, title: String)] = [
        (30, String(localized: "30 分鐘後")),
        (60, String(localized: "1 小時後")),
        (90, String(localized: "1 小時 30 分鐘後")),
        (120, String(localized: "2 小時後")),
        (180, String(localized: "3 小時後")),
    ]

    /// 「指定時間…」輸入的時間：15:30、15：30、1530、930、9:05、15（整點）；全形數字也可以
    static func parse(_ text: String) -> (hour: Int, minute: Int)? {
        let t = (text.applyingTransform(.fullwidthToHalfwidth, reverse: false) ?? text).trimmingCharacters(in: .whitespaces)
        let parts = t.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count <= 2, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isASCIIDigit) }) else { return nil }
        let hour: Int, minute: Int
        if parts.count == 2 {
            guard parts[0].count <= 2, parts[1].count <= 2 else { return nil }
            (hour, minute) = (Int(parts[0])!, Int(parts[1])!)
        } else {
            let digits = parts[0]
            guard digits.count <= 4 else { return nil }
            (hour, minute) = digits.count <= 2 ? (Int(digits)!, 0) : (Int(digits.dropLast(2))!, Int(digits.suffix(2))!)
        }
        return (0...23).contains(hour) && (0...59).contains(minute) ? (hour, minute) : nil
    }

    /// 下一個那個時間：今天的還沒到就是今天，到了（或就是現在這一分）就是明天
    static func next(hour: Int, minute: Int, after now: Date, calendar: Calendar = .current) -> Date {
        calendar.nextDate(after: now, matching: DateComponents(hour: hour, minute: minute, second: 0), matchingPolicy: .nextTime)
            ?? now.addingTimeInterval(86_400)
    }

    /// 「15:30」（24 小時制，兩位數）：「指定時間…」的欄位也用這個
    static func clock(_ date: Date, calendar: Calendar = .current) -> String {
        let c = calendar.dateComponents([.hour, .minute], from: date)
        return String(format: "%02d:%02d", c.hour ?? 0, c.minute ?? 0)
    }

    /// 畫面上寫的：「15:30」；跟現在不是同一天時「明天 09:00」
    static func label(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        calendar.isDate(date, inSameDayAs: now) ? clock(date, calendar: calendar) : String(localized: "明天 \(clock(date, calendar: calendar))")
    }

    /// 還有多久：分鐘無條件進位，「1 小時 25 分鐘」「5 分鐘」（不到 1 分鐘也寫 1 分鐘）
    static func remaining(_ date: Date, now: Date = Date()) -> String {
        TimeFormat.duration(Int((max(0, date.timeIntervalSince(now)) / 60).rounded(.up)) * 60)
    }

    /// 「指定時間…」沒設定過時帶的值：一小時後，分鐘往上取到 5 的倍數
    static func defaultTime(now: Date = Date(), calendar: Calendar = .current) -> String {
        let c = calendar.dateComponents([.hour, .minute], from: now.addingTimeInterval(3600))
        var hour = c.hour ?? 0, minute = ((c.minute ?? 0) + 4) / 5 * 5
        if minute == 60 { (hour, minute) = ((hour + 1) % 24, 0) }
        return String(format: "%02d:%02d", hour, minute)
    }
}

private extension Character {
    var isASCIIDigit: Bool { isASCII && isNumber }
}
