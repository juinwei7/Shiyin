// 產生拾音的 app 圖示：青瓷綠圓角方塊＋一排波形條，其中一條是 VU 琥珀色（design §9.3）
// 用法：swift scripts/make-icon.swift Resources/AppIcon.icns
import AppKit

let output = CommandLine.arguments.dropFirst().first ?? "Resources/AppIcon.icns"
let fm = FileManager.default
let work = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("ShiyinIcon-\(UUID().uuidString)")
let iconset = work.appendingPathComponent("AppIcon.iconset")
try fm.createDirectory(at: iconset, withIntermediateDirectories: true)

func color(_ hex: UInt32) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

func render(_ size: Int) -> Data {
    let s = CGFloat(size)
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    // Apple 圖示範本：1024 畫布上，主體 824×824，圓角約 185
    let inset = s * 100 / 1024
    let body = CGRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let radius = body.width * 0.2245
    ctx.setShadow(offset: CGSize(width: 0, height: -s * 0.01), blur: s * 0.02, color: CGColor(gray: 0, alpha: 0.3))
    ctx.addPath(CGPath(roundedRect: body, cornerWidth: radius, cornerHeight: radius, transform: nil))
    ctx.setFillColor(color(0x2D6A5D))
    ctx.fillPath()
    ctx.setShadow(offset: .zero, blur: 0, color: nil)

    // 波形條：高度依序像一段說話的音量，第 5 條是琥珀色（低信心／VU 警示）
    let heights: [CGFloat] = [0.22, 0.42, 0.62, 0.36, 0.78, 0.50, 0.30]
    let barWidth = body.width * 0.068
    let gap = body.width * 0.048
    let total = CGFloat(heights.count) * barWidth + CGFloat(heights.count - 1) * gap
    var x = body.midX - total / 2
    for (i, h) in heights.enumerated() {
        let height = body.height * h
        let bar = CGRect(x: x, y: body.midY - height / 2, width: barWidth, height: height)
        ctx.addPath(CGPath(roundedRect: bar, cornerWidth: barWidth / 2, cornerHeight: barWidth / 2, transform: nil))
        ctx.setFillColor(i == 4 ? color(0xE2AD3B) : color(0xF5F7F6))
        ctx.fillPath()
        x += barWidth + gap
    }
    let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    try render(base).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try render(base * 2).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", output]
try iconutil.run()
iconutil.waitUntilExit()
try? fm.removeItem(at: work)
print(iconutil.terminationStatus == 0 ? "已產生 \(output)" : "iconutil 失敗")
