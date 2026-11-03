import SwiftUI

struct PhotoStage: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var contentInsets: EdgeInsets

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                Color.black
                DemoArtwork(photo: workspace.selectedPhoto, enhanced: workspace.enhanced)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .scaleEffect(workspace.isZoomed ? 1.85 : 1)
                    .padding(contentInsets)
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.18), value: workspace.isZoomed)
                    .animation(reduceMotion ? nil : .easeOut(duration: 0.16), value: workspace.selectedID)
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
            .clipped()
            .contentShape(.rect)
            .onTapGesture(count: 2) {
                workspace.toggleZoom()
            }
            .gesture(
                DragGesture(minimumDistance: 28)
                    .onEnded { value in
                        guard !workspace.isZoomed, abs(value.translation.width) > abs(value.translation.height) * 1.4 else { return }
                        if value.translation.width < 0 {
                            workspace.selectNext()
                        } else {
                            workspace.selectPrevious()
                        }
                    }
            )
            .contextMenu {
                Button(workspace.showFilmstrip ? "隐藏照片列表" : "显示照片列表", systemImage: "photo.on.rectangle") {
                    workspace.showFilmstrip.toggle()
                }
                Button(workspace.showAnalysis ? "隐藏分析栏" : "显示分析栏", systemImage: "square.grid.3x3") {
                    workspace.showAnalysis.toggle()
                }
                Button(workspace.showControls ? "隐藏检查器" : "显示检查器", systemImage: "sidebar.right") {
                    workspace.showControls.toggle()
                }
                Divider()
                Button(workspace.enhanced ? "关闭雾天增强" : "雾天增强", systemImage: "circle.lefthalf.filled") {
                    workspace.enhanced.toggle()
                }
            }
            .overlay(alignment: .bottomTrailing) {
                if workspace.isZoomed {
                    ZoomNavigator()
                        .padding(16)
                        .transition(.opacity)
                }
            }
        }
        .background(.black)
    }
}

private struct ZoomNavigator: View {
    @EnvironmentObject private var workspace: DemoWorkspace

    var body: some View {
        VStack(spacing: 5) {
            DemoArtwork(photo: workspace.selectedPhoto)
                .frame(width: 112, height: 74)
                .overlay {
                    RoundedRectangle(cornerRadius: 2)
                        .stroke(.white, lineWidth: 1)
                        .frame(width: 48, height: 32)
                }
                .clipShape(.rect(cornerRadius: 7))
            Text("185%")
                .font(.caption2.monospacedDigit().weight(.semibold))
        }
        .padding(7)
        .glassEffect(.clear, in: .rect(cornerRadius: 12))
        .accessibilityLabel("缩放导航图，185%")
    }
}

struct DemoThumbnailCard: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    let photo: DemoPhoto

    var body: some View {
        let rating = workspace.rating(of: photo)
        VStack(spacing: 4) {
            Button {
                workspace.select(photo)
            } label: {
                VStack(spacing: 4) {
                    DemoArtwork(photo: photo)
                        .frame(width: 80, height: 80)
                        .background(.black)
                        .clipShape(.rect(cornerRadius: 5))
                    Text(photo.name)
                        .font(.system(size: 10, weight: .semibold))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Text(photo.capturedAt)
                        .font(.system(size: 9, weight: .medium))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                .frame(width: 88)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)

            HStack(spacing: 0) {
                ForEach(0...5, id: \.self) { value in
                    Button {
                        workspace.setRating(value, photoID: photo.id)
                    } label: {
                        Image(systemName: value == 0 ? "xmark" : rating >= value ? "star.fill" : "star")
                            .font(.system(size: value == 0 ? 8 : 9, weight: .medium))
                            .foregroundStyle(value == 0 ? Color.secondary : rating >= value ? Color.yellow : Color.secondary)
                            .frame(width: 14, height: 22)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(value == 0 ? "\(photo.name) 清除星级" : "\(photo.name) \(value) 星")
                }
            }
        }
        .padding(4)
        .frame(width: 98, height: 138)
        .background {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(.white.opacity(workspace.selectedID == photo.id ? 0.09 : 0.001))
        }
        .overlay {
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .stroke(workspace.selectedID == photo.id ? Color.accentColor : .clear, lineWidth: 2)
        }
        .id(photo.id)
    }
}

struct CompactThumbnailCard: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    let photo: DemoPhoto

    var body: some View {
        Button {
            workspace.select(photo)
        } label: {
            VStack(spacing: 4) {
                DemoArtwork(photo: photo)
                    .frame(width: 62, height: 62)
                    .clipShape(.rect(cornerRadius: 6))
                    .overlay(alignment: .bottomTrailing) {
                        if workspace.rating(of: photo) > 0 {
                            HStack(spacing: 1) {
                                Image(systemName: "star.fill")
                                Text("\(workspace.rating(of: photo))")
                            }
                            .font(.system(size: 8, weight: .bold))
                            .foregroundStyle(.yellow)
                            .padding(3)
                            .background(.black.opacity(0.58), in: .capsule)
                            .padding(4)
                        }
                    }
                Text(photo.name.replacingOccurrences(of: ".ARW", with: ""))
                    .font(.system(size: 9, weight: .medium))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            .padding(4)
            .frame(width: 72, height: 88)
            .background {
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(.white.opacity(workspace.selectedID == photo.id ? 0.09 : 0.001))
            }
            .overlay {
                RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .stroke(workspace.selectedID == photo.id ? Color.accentColor : .clear, lineWidth: 2)
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .id(photo.id)
    }
}

struct FilmstripView: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    var compact = false

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView(.horizontal, showsIndicators: false) {
                LazyHStack(spacing: compact ? 3 : 5) {
                    ForEach(workspace.visiblePhotos) { photo in
                        if compact {
                            CompactThumbnailCard(photo: photo)
                        } else {
                            DemoThumbnailCard(photo: photo)
                        }
                    }
                }
                .padding(.horizontal, compact ? 6 : 8)
                .padding(.vertical, compact ? 5 : 7)
            }
            .onChange(of: workspace.centeringRequest) { _, request in
                guard let request else { return }
                withAnimation(reduceMotion ? nil : .easeOut(duration: 0.35)) {
                    proxy.scrollTo(request.photoID, anchor: .center)
                }
            }
        }
    }
}

struct SimilarityPanel: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    var compact = false

    private var candidates: [DemoPhoto] {
        Array(workspace.visiblePhotos.filter { $0.id != workspace.selectedID }.prefix(compact ? 3 : 4))
    }

    var body: some View {
        HStack(spacing: compact ? 10 : 14) {
            if candidates.isEmpty {
                Text("无相似照片")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .padding(18)
            } else {
                ForEach(Array(candidates.enumerated()), id: \.element.id) { index, photo in
                    Button {
                        workspace.select(photo)
                    } label: {
                        VStack(spacing: 4) {
                            DemoArtwork(photo: photo)
                                .frame(width: compact ? 54 : 64, height: compact ? 54 : 64)
                                .clipShape(.rect(cornerRadius: 7))
                            Text("\(96 - index * 3)%")
                                .font(.caption2.monospacedDigit().weight(.semibold))
                                .foregroundStyle(.secondary)
                        }
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .padding(8)
    }
}

struct PhotoDetailsSheet: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                Section(workspace.selectedPhoto.name) {
                    DetailRow(label: "光圈", value: workspace.selectedPhoto.aperture)
                    DetailRow(label: "快门", value: workspace.selectedPhoto.shutter)
                    DetailRow(label: "ISO", value: workspace.selectedPhoto.iso)
                    DetailRow(label: "焦距", value: workspace.selectedPhoto.focalLength)
                    DetailRow(label: "拍摄时间", value: "2026-\(workspace.selectedPhoto.capturedAt):00 +08:00")
                    DetailRow(label: "文件大小", value: String(format: "%.1f MB", Double(workspace.selectedPhoto.fileSize) / 1_000_000))
                    DetailRow(label: "评分", value: workspace.selectedPhoto.rating == 0 ? "无" : "\(workspace.selectedPhoto.rating) 星")
                }
            }
            .navigationTitle("EXIF")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }
}

private struct DetailRow: View {
    let label: String
    let value: String

    var body: some View {
        HStack {
            Text(label).foregroundStyle(.secondary)
            Spacer()
            Text(value).textSelection(.enabled)
        }
    }
}
