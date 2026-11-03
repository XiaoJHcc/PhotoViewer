import SwiftUI

struct StarRatingControl: View {
    @EnvironmentObject private var workspace: DemoWorkspace

    var body: some View {
        HStack(spacing: 0) {
            ForEach(0...5, id: \.self) { value in
                Button {
                    workspace.setRating(value)
                } label: {
                    Image(systemName: value == 0 ? "xmark" : workspace.selectedPhoto.rating >= value ? "star.fill" : "star")
                        .font(.system(size: 16, weight: .medium))
                        .foregroundStyle(value == 0 ? Color.secondary : workspace.selectedPhoto.rating >= value ? Color.yellow : Color.secondary)
                        .frame(width: 44, height: 44)
                        .contentShape(.rect)
                }
                .buttonStyle(.borderless)
                .accessibilityLabel(value == 0 ? "清除星级" : "\(value) 星")
            }
        }
    }
}

struct AnalysisStrip: View {
    var compact = false
    private let labels = ["1:1", "RGB", "PCA", "COS", "锐度", "抖动"]

    var body: some View {
        HStack(spacing: compact ? 6 : 12) {
            ForEach(Array(labels.enumerated()), id: \.offset) { index, label in
                VStack(spacing: 4) {
                    ZStack {
                        if index == 1 {
                            Canvas { context, size in
                                let colors: [Color] = [.red, .green, .blue]
                                for (channel, color) in colors.enumerated() {
                                    var path = Path()
                                    for point in 0...12 {
                                        let x = size.width * CGFloat(point) / 12
                                        let y = size.height * (0.25 + 0.45 * abs(sin(CGFloat(point + channel * 3) * 0.55)))
                                        if point == 0 { path.move(to: CGPoint(x: x, y: y)) } else { path.addLine(to: CGPoint(x: x, y: y)) }
                                    }
                                    context.stroke(path, with: .color(color.opacity(0.8)), lineWidth: 1.2)
                                }
                            }
                            .padding(5)
                        } else {
                            Image(systemName: ["viewfinder", "chart.xyaxis.line", "square.grid.3x3", "point.topleft.down.to.point.bottomright.curvepath", "waveform", "camera.metering.matrix"][index])
                                .font(.system(size: compact ? 18 : 22, weight: .light))
                                .foregroundStyle(.secondary)
                        }
                    }
                    .frame(width: compact ? 42 : 54, height: compact ? 42 : 54)
                    Text(label)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity)
            }
        }
        .padding(.horizontal, compact ? 8 : 14)
        .padding(.vertical, 8)
    }
}
