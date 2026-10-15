// 把 HTML 轉成 A4 PDF（自動分頁），給 make-dmg.sh 產生安裝說明用。
// 用法：swift scripts/html-to-pdf.swift <輸入.html> <輸出.pdf>
import AppKit

let args = CommandLine.arguments
let html = try Data(contentsOf: URL(fileURLWithPath: args[1]))
let output = URL(fileURLWithPath: args[2])
guard let string = NSAttributedString(html: html, options: [.characterEncoding: String.Encoding.utf8.rawValue],
                                      documentAttributes: nil) else { fatalError("讀不懂 HTML") }
let info = NSPrintInfo()
info.paperSize = NSSize(width: 595, height: 842)
info.topMargin = 56; info.bottomMargin = 56; info.leftMargin = 56; info.rightMargin = 56
info.horizontalPagination = .fit
info.verticalPagination = .automatic
info.jobDisposition = .save
info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = output
let width = info.paperSize.width - info.leftMargin - info.rightMargin
let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
view.textStorage?.setAttributedString(string)
view.isVerticallyResizable = true
view.sizeToFit()
view.layoutManager?.ensureLayout(for: view.textContainer!)
view.frame.size.height = (view.layoutManager?.usedRect(for: view.textContainer!).height ?? 100) + 20
let operation = NSPrintOperation(view: view, printInfo: info)
operation.showsPrintPanel = false
operation.showsProgressPanel = false
guard operation.run() else { fatalError("PDF 產生失敗") }
print("已產生 \(output.path)")
