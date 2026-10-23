// axpress <pid> <視窗標題> list | press <role> <name> [第幾個]：用 AX API 直接走指定 pid、指定標題的視窗，列出或按下按鈕。
// 用途：驗證時按 DEBUG 版畫面上的按鈕（視窗要在目前的桌面）。不用 System Events：它用名字找 process，使用者自己的拾音也叫 Shiyin，
// 「whose unix id」會抓錯（2026-10-23 抓到使用者正在看的會議）。只碰標題相符的視窗，所以不會按到別的視窗。
// 編譯：swiftc scripts/dev/axpress.swift -o ~/Library/Caches/ShiyinBuild/axpress
// role 是 AXButton、AXRadioButton、AXCheckBox、AXMenuButton、AXPopUpButton、AXLink；name 比對 AXTitle 或 AXDescription（SwiftUI 的 accessibilityLabel）
import ApplicationServices
import Foundation
let args = CommandLine.arguments
guard args.count >= 4, let pid = pid_t(args[1]) else {
    print("usage: axpress <pid> <window title> list | press <role> <name> [index]")
    exit(64)
}
let demoTitle = args[2]
let mode = args[3]
let wantRole = args.count > 4 ? args[4] : ""
let wantName = args.count > 5 ? args[5] : ""
let index = args.count > 6 ? Int(args[6]) ?? 1 : 1
let app = AXUIElementCreateApplication(pid)
AXUIElementSetMessagingTimeout(app, 5)
func attr(_ e: AXUIElement, _ name: String) -> AnyObject? {
    var v: AnyObject?
    AXUIElementCopyAttributeValue(e, name as CFString, &v)
    return v
}
var matches: [AXUIElement] = []
var seen = 0
func walk(_ e: AXUIElement, _ depth: Int) {
    seen += 1
    let role = attr(e, kAXRoleAttribute) as? String ?? ""
    let title = attr(e, kAXTitleAttribute) as? String ?? ""
    let desc = attr(e, kAXDescriptionAttribute) as? String ?? ""
    if ["AXButton", "AXRadioButton", "AXCheckBox", "AXMenuButton", "AXPopUpButton", "AXLink"].contains(role) {
        if mode == "list" { print("\(role) | \(title) | \(desc)") }
        if role == wantRole && (title == wantName || desc == wantName) { matches.append(e) }
    }
    if depth < 40, let kids = attr(e, kAXChildrenAttribute) as? [AXUIElement] { for k in kids { walk(k, depth + 1) } }
}
var found = false
if let windows = attr(app, kAXWindowsAttribute) as? [AXUIElement] {
    for w in windows where (attr(w, kAXTitleAttribute) as? String ?? "") == demoTitle {
        found = true
        walk(w, 0)
    }
}
if !found { print("window \"\(demoTitle)\" not found for pid \(pid)（不在目前的桌面？）"); exit(2) }
if mode == "list" { print("(\(seen) elements)") }
if mode == "press" {
    if matches.count >= index {
        let r = AXUIElementPerformAction(matches[index - 1], kAXPressAction as CFString)
        print("pressed \(wantRole) \(wantName) #\(index) of \(matches.count) (result \(r.rawValue))")
    } else {
        print("not found (\(matches.count) matches): \(wantRole) \(wantName)")
        exit(1)
    }
}
