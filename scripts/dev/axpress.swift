// axpress <pid> list | press <role> <name> [第幾個] | setvalue <role> <placeholder 或現值> <第幾個> <新值>：用 AX API 直接走指定 pid 的視窗（System Events 用名字找 process，兩個拾音會抓錯）。
// 只碰標題是「Lumio 秋季新品上市時程會議」的視窗（示範資料），不會碰到使用者自己的拾音
import ApplicationServices
import Foundation
let args = CommandLine.arguments
let pid = pid_t(args[1])!
let mode = args[2]
let wantRole = args.count > 3 ? args[3] : ""
let wantName = args.count > 4 ? args[4] : ""
let index = args.count > 5 ? Int(args[5]) ?? 1 : 1
let demoTitle = "Lumio 秋季新品上市時程會議"
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
    if mode == "setvalue" || mode == "focus" || mode == "insert", ["AXTextField", "AXTextArea"].contains(role) {
        let placeholder = attr(e, kAXPlaceholderValueAttribute) as? String ?? ""
        let value = attr(e, kAXValueAttribute) as? String ?? ""
        if role == wantRole && (placeholder == wantName || value == wantName) { matches.append(e) }
    }
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
if !found { print("demo window not found for pid \(pid)"); exit(2) }
if mode == "list" { print("(\(seen) elements)") }
if mode == "focus" {   // focus <role> <placeholder 或現值> <第幾個>：把鍵盤焦點放到那個欄位（之後用 CGEvent 對 pid 送按鍵）
    if matches.count >= index {
        let r = AXUIElementSetAttributeValue(matches[index - 1], kAXFocusedAttribute as CFString, kCFBooleanTrue)
        print("focused \(wantRole) \(wantName) #\(index) of \(matches.count) (result \(r.rawValue))")
    } else { print("not found (\(matches.count) matches): \(wantRole) \(wantName)"); exit(1) }
}
if mode == "insert" {   // insert <role> <placeholder 或現值> <第幾個> <文字>：先給焦點，再用 AXSelectedText 插入文字（走欄位編輯器，SwiftUI 的綁定會更新）
    let text = args.count > 6 ? args[6] : ""
    if matches.count >= index {
        let e = matches[index - 1]
        AXUIElementSetAttributeValue(e, kAXFocusedAttribute as CFString, kCFBooleanTrue)
        usleep(200_000)
        let r = AXUIElementSetAttributeValue(e, kAXSelectedTextAttribute as CFString, text as CFTypeRef)
        print("inserted into \(wantRole) \(wantName) #\(index) of \(matches.count) (result \(r.rawValue))")
    } else { print("not found (\(matches.count) matches): \(wantRole) \(wantName)"); exit(1) }
}
if mode == "setvalue" {
    let newValue = args.count > 6 ? args[6] : ""
    if matches.count >= index {
        let r = AXUIElementSetAttributeValue(matches[index - 1], kAXValueAttribute as CFString, newValue as CFTypeRef)
        print("set \(wantRole) \(wantName) #\(index) of \(matches.count) → \(newValue) (result \(r.rawValue))")
    } else {
        print("not found (\(matches.count) matches): \(wantRole) \(wantName)")
        exit(1)
    }
}
if mode == "press" {
    if matches.count >= index {
        let r = AXUIElementPerformAction(matches[index - 1], kAXPressAction as CFString)
        print("pressed \(wantRole) \(wantName) #\(index) of \(matches.count) (result \(r.rawValue))")
    } else {
        print("not found (\(matches.count) matches): \(wantRole) \(wantName)")
        exit(1)
    }
}
