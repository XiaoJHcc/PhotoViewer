import SwiftUI

struct DemoArtwork: View {
    let photo: DemoPhoto
    var enhanced = false

    private var palette: [Color] {
        switch photo.palette {
        case 0: [.init(red: 0.08, green: 0.17, blue: 0.31), .init(red: 0.95, green: 0.49, blue: 0.25), .init(red: 0.11, green: 0.23, blue: 0.28)]
        case 1: [.init(red: 0.18, green: 0.12, blue: 0.31), .init(red: 0.93, green: 0.55, blue: 0.48), .init(red: 0.16, green: 0.20, blue: 0.32)]
        case 2: [.init(red: 0.06, green: 0.25, blue: 0.34), .init(red: 0.75, green: 0.86, blue: 0.74), .init(red: 0.08, green: 0.27, blue: 0.22)]
        case 3: [.init(red: 0.15, green: 0.24, blue: 0.42), .init(red: 0.96, green: 0.72, blue: 0.35), .init(red: 0.20, green: 0.18, blue: 0.28)]
        case 4: [.init(red: 0.07, green: 0.20, blue: 0.26), .init(red: 0.44, green: 0.76, blue: 0.72), .init(red: 0.12, green: 0.29, blue: 0.25)]
        default: [.init(red: 0.25, green: 0.13, blue: 0.20), .init(red: 0.93, green: 0.45, blue: 0.31), .init(red: 0.20, green: 0.14, blue: 0.24)]
        }
    }

    var body: some View {
        Canvas { context, size in
            let rect = CGRect(origin: .zero, size: size)
            let sky = Gradient(colors: [palette[0], palette[1].opacity(0.88), palette[1]])
            context.fill(Path(rect), with: .linearGradient(sky, startPoint: .zero, endPoint: CGPoint(x: 0, y: size.height)))

            let sunX = size.width * (0.24 + CGFloat(photo.id % 4) * 0.15)
            let sunY = size.height * (0.24 + CGFloat(photo.id % 3) * 0.05)
            let sunRadius = min(size.width, size.height) * 0.095
            let sunRect = CGRect(x: sunX - sunRadius, y: sunY - sunRadius, width: sunRadius * 2, height: sunRadius * 2)
            context.fill(Path(ellipseIn: sunRect), with: .color(.white.opacity(0.78)))

            for layer in 0..<3 {
                let baseline = size.height * (0.57 + CGFloat(layer) * 0.11)
                var mountain = Path()
                mountain.move(to: CGPoint(x: 0, y: size.height))
                mountain.addLine(to: CGPoint(x: 0, y: baseline))
                let segments = 6
                for segment in 0...segments {
                    let x = size.width * CGFloat(segment) / CGFloat(segments)
                    let wave = sin(CGFloat(segment * 2 + layer * 3 + photo.id) * 1.17)
                    let y = baseline - wave * size.height * (0.12 - CGFloat(layer) * 0.018) - size.height * 0.05
                    mountain.addLine(to: CGPoint(x: x, y: y))
                }
                mountain.addLine(to: CGPoint(x: size.width, y: size.height))
                mountain.closeSubpath()
                let color = layer == 0 ? palette[2].opacity(0.62) : palette[2].opacity(0.78 + Double(layer) * 0.1)
                context.fill(mountain, with: .color(color))
            }

            let water = CGRect(x: 0, y: size.height * 0.79, width: size.width, height: size.height * 0.21)
            context.fill(Path(water), with: .color(palette[0].opacity(0.76)))
            for line in 0..<9 {
                let y = size.height * (0.81 + CGFloat(line) * 0.019)
                let width = size.width * (0.07 + CGFloat((line + photo.id) % 5) * 0.035)
                let x = sunX - width / 2 + sin(CGFloat(line * 4)) * size.width * 0.04
                let reflection = CGRect(x: x, y: y, width: width, height: max(1, size.height * 0.004))
                context.fill(Path(reflection), with: .color(palette[1].opacity(0.32)))
            }
        }
        .aspectRatio(photo.aspectRatio, contentMode: .fit)
        .saturation(enhanced ? 1.2 : 1)
        .contrast(enhanced ? 1.12 : 1)
        .brightness(enhanced ? 0.025 : 0)
        .background(.black)
        .accessibilityHidden(true)
    }
}
