import AppKit
import Foundation

struct Symbol: Decodable {
    let id: String
    let name: String
}

guard CommandLine.arguments.count == 3 else {
    fputs("usage: render-apple-symbols.swift symbols.json output-directory\n", stderr)
    exit(2)
}

let input = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true)
let symbols = try JSONDecoder().decode([Symbol].self, from: Data(contentsOf: input))
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

for symbol in symbols {
    guard let base = NSImage(systemSymbolName: symbol.name, accessibilityDescription: nil) else {
        fputs("missing SF Symbol: \(symbol.id) = \(symbol.name)\n", stderr)
        exit(1)
    }
    for pointSize in [18.0, 24.0] {
        guard let configured = base.withSymbolConfiguration(
            NSImage.SymbolConfiguration(pointSize: pointSize, weight: .regular)
        ) else {
            fputs("cannot configure SF Symbol: \(symbol.id)\n", stderr)
            exit(1)
        }
        let canvas = 32.0
        let scale = 2.0
        guard let bitmap = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(canvas * scale),
            pixelsHigh: Int(canvas * scale),
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else {
            fputs("cannot create bitmap for: \(symbol.id)\n", stderr)
            exit(1)
        }
        NSGraphicsContext.saveGraphicsState()
        guard let context = NSGraphicsContext(bitmapImageRep: bitmap) else { exit(1) }
        NSGraphicsContext.current = context
        context.cgContext.scaleBy(x: scale, y: scale)
        NSColor.clear.setFill()
        NSRect(x: 0, y: 0, width: canvas, height: canvas).fill()
        let imageSize = configured.size
        let rect = NSRect(
            x: (canvas - imageSize.width) / 2,
            y: (canvas - imageSize.height) / 2,
            width: imageSize.width,
            height: imageSize.height
        )
        configured.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1,
                        respectFlipped: true, hints: [.interpolation: NSImageInterpolation.high])
        NSGraphicsContext.restoreGraphicsState()
        guard let png = bitmap.representation(using: .png, properties: [:]) else {
            fputs("cannot encode SF Symbol: \(symbol.id)\n", stderr)
            exit(1)
        }
        let filename = symbol.id.replacingOccurrences(of: ".", with: "_") + "-\(Int(pointSize)).png"
        try png.write(to: output.appendingPathComponent(filename))
    }
}
