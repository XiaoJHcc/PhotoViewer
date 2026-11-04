import SwiftUI

struct WideWorkspaceView: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @State private var showsDetails = false

    private var columnVisibility: Binding<NavigationSplitViewVisibility> {
        Binding(
            get: { workspace.showFilmstrip ? .all : .detailOnly },
            set: { workspace.showFilmstrip = $0 != .detailOnly }
        )
    }

    var body: some View {
        NavigationSplitView(columnVisibility: columnVisibility) {
            WideSidebar()
                .navigationSplitViewColumnWidth(min: 200, ideal: 210, max: 220)
                .wideSidebarTogglePolicy()
        } detail: {
            detail
        }
        .sheet(isPresented: $showsDetails) {
            PhotoDetailsSheet()
                #if os(macOS)
                .frame(minWidth: 480, minHeight: 480)
                #endif
        }
    }

    private var detail: some View {
        HStack(spacing: 0) {
            PhotoStage(contentInsets: EdgeInsets())
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .layoutPriority(1)

            if workspace.showAnalysis {
                Divider()
                WideAnalysisColumn()
            }

            if workspace.showControls {
                WideControlColumn(showsDetails: $showsDetails)
                    .frame(width: 94)
                    .frame(maxHeight: .infinity)
            }
        }
        .background(.black)
        .navigationTitle(workspace.selectedPhoto.name)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
    }
}

private extension View {
    @ViewBuilder
    func wideSidebarTogglePolicy() -> some View {
        #if os(macOS)
        toolbar(removing: .sidebarToggle)
        #else
        self
        #endif
    }
}

private struct WideSidebar: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var similarPhotos: [DemoPhoto] {
        Array(workspace.visiblePhotos.filter { $0.id != workspace.selectedID }.prefix(6))
    }

    var body: some View {
        ScrollViewReader { proxy in
            List {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        filterBar

                        Text("共 \(workspace.visiblePhotos.count) 张")
                            .font(.callout)
                            .foregroundStyle(.secondary)

                        Toggle("相似聚类", isOn: $workspace.showSimilarity)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .listRowInsets(EdgeInsets(top: 8, leading: 12, bottom: 8, trailing: 12))
                    .listRowBackground(Color.clear)
                    .listRowSeparator(.hidden)
                }

                if workspace.showSimilarity, !similarPhotos.isEmpty {
                    Section {
                        ForEach(similarPhotos) { photo in
                            DemoThumbnailCard(photo: photo)
                                .frame(maxWidth: .infinity)
                                .id("similarity-\(photo.id)")
                                .listRowInsets(EdgeInsets(top: 3, leading: 6, bottom: 3, trailing: 6))
                                .listRowBackground(Color.clear)
                                .listRowSeparator(.hidden)
                        }
                    }
                }

                Section {
                    ForEach(workspace.visiblePhotos) { photo in
                        DemoThumbnailCard(photo: photo)
                            .frame(maxWidth: .infinity)
                            .id(photo.id)
                            .listRowInsets(EdgeInsets(top: 3, leading: 6, bottom: 3, trailing: 6))
                            .listRowBackground(Color.clear)
                            .listRowSeparator(.hidden)
                            .contextMenu {
                                ForEach(0...5, id: \.self) { rating in
                                    Button(rating == 0 ? "清除星级" : "\(rating) 星") {
                                        workspace.setRating(rating, photoID: photo.id)
                                    }
                                }
                            }
                    }
                }
            }
            .listStyle(.sidebar)
            .onChange(of: workspace.centeringRequest) { _, request in
                guard let request else { return }
                withAnimation(reduceMotion ? nil : .easeOut(duration: 0.35)) {
                    proxy.scrollTo(request.photoID, anchor: .center)
                }
            }
        }
    }

    private var filterBar: some View {
        HStack(spacing: 6) {
            Menu {
                Picker("排序方式", selection: $workspace.sortKey) {
                    ForEach(DemoSortKey.allCases) { key in
                        Text(key.title).tag(key)
                    }
                }
                .pickerStyle(.inline)
            } label: {
                Image(systemName: "arrow.up.arrow.down")
            }
            .help("排序方式")
            .accessibilityLabel("排序方式")

            Menu {
                Picker("排序方向", selection: $workspace.ascending) {
                    Text("升序").tag(true)
                    Text("降序").tag(false)
                }
                .pickerStyle(.inline)
            } label: {
                Image(systemName: workspace.ascending ? "chevron.up" : "chevron.down")
            }
            .help("排序方向")
            .accessibilityLabel("排序方向")

            Menu {
                Picker("星级筛选", selection: $workspace.ratingFilter) {
                    ForEach(DemoRatingFilter.allCases) { filter in
                        Text(filter.title).tag(filter)
                    }
                }
                .pickerStyle(.inline)
            } label: {
                Image(systemName: "star")
            }
            .help("星级筛选")
            .accessibilityLabel("星级筛选")
        }
        .controlSize(.small)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct WideAnalysisColumn: View {
    private let labels = ["1:1", "RGB", "PCA", "COS", "锐度", "抖动"]
    private let symbols = ["viewfinder", "chart.xyaxis.line", "square.grid.3x3", "point.topleft.down.to.point.bottomright.curvepath", "waveform", "camera.metering.matrix"]

    var body: some View {
        ScrollView(.vertical) {
            VStack(spacing: 10) {
                ForEach(Array(labels.enumerated()), id: \.offset) { index, label in
                    VStack(spacing: 5) {
                        Image(systemName: symbols[index])
                            .font(.system(size: 22, weight: .light))
                            .foregroundStyle(.secondary)
                            .frame(width: 64, height: 64)
                            .background(Color.primary.opacity(0.06), in: .rect(cornerRadius: 6))
                            .overlay {
                                RoundedRectangle(cornerRadius: 6)
                                    .stroke(.quaternary, lineWidth: 1)
                            }
                        Text(label)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .padding(10)
        }
        .frame(width: 94)
        .background(.regularMaterial)
    }
}

private struct WideControlColumn: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Binding var showsDetails: Bool

    var body: some View {
        ScrollView(.vertical) {
            VStack(spacing: 12) {
                WideInspectorSection(title: "EXIF") {
                    Button {
                        showsDetails = true
                    } label: {
                        VStack(spacing: 4) {
                            WideExifBlock(label: "光圈", value: workspace.selectedPhoto.aperture)
                            WideExifBlock(label: "快门", value: workspace.selectedPhoto.shutter)
                            WideExifBlock(label: "ISO", value: workspace.selectedPhoto.iso)
                            WideExifBlock(label: "焦距", value: workspace.selectedPhoto.focalLength)
                        }
                    }
                    .buttonStyle(.plain)
                    .help("EXIF 详情")
                }

                WideInspectorSection(title: "评分") {
                    VStack(spacing: 2) {
                        ForEach((0...5).reversed(), id: \.self) { value in
                            Button {
                                workspace.setRating(value)
                            } label: {
                                Image(systemName: value == 0 ? "xmark" : workspace.selectedPhoto.rating >= value ? "star.fill" : "star")
                                    .font(.system(size: value == 0 ? 12 : 16, weight: .medium))
                                    .foregroundStyle(value == 0 ? Color.secondary : workspace.selectedPhoto.rating >= value ? Color.yellow : Color.secondary)
                                    .frame(width: 30, height: 30)
                                    .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(value == 0 ? "清除星级" : "\(value) 星")
                        }
                    }
                }

                VStack(spacing: 12) {
                    WideCommandButton(systemName: "chevron.left", help: "上一张") {
                        workspace.selectPrevious()
                    }
                    .keyboardShortcut(.leftArrow, modifiers: [])

                    WideCommandButton(systemName: "chevron.right", help: "下一张") {
                        workspace.selectNext()
                    }
                    .keyboardShortcut(.rightArrow, modifiers: [])

                    WideCommandButton(
                        systemName: workspace.isZoomed ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right",
                        help: workspace.isZoomed ? "适应窗口" : "放大到 100%"
                    ) {
                        workspace.toggleZoom()
                    }

                    WideCommandButton(systemName: "circle.lefthalf.filled", help: workspace.enhanced ? "关闭雾天增强" : "雾天增强") {
                        workspace.enhanced.toggle()
                    }

                    WideCommandButton(systemName: "square.grid.3x3", help: workspace.showAnalysis ? "隐藏分析栏" : "显示分析栏") {
                        workspace.showAnalysis.toggle()
                    }

                    WideCommandButton(systemName: "photo.on.rectangle", help: workspace.showFilmstrip ? "隐藏胶片栏" : "显示胶片栏") {
                        workspace.showFilmstrip.toggle()
                    }
                }
                .padding(.top, 4)
            }
            .padding(.top, 10)
            .padding(.horizontal, 8)
            .padding(.bottom, 10)
        }
    }
}

private struct WideInspectorSection<Content: View>: View {
    let title: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: 6) {
            Text(title)
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            content
        }
        .frame(maxWidth: .infinity)
        .padding(6)
        .background(.regularMaterial, in: .rect(cornerRadius: 12))
        .overlay {
            RoundedRectangle(cornerRadius: 12)
                .stroke(.quaternary, lineWidth: 1)
        }
    }
}

private struct WideExifBlock: View {
    let label: String
    let value: String

    var body: some View {
        VStack(spacing: 3) {
            Text(label)
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
            Text(value)
                .font(.system(size: 12, weight: .semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
        .frame(width: 50, height: 40)
    }
}

private struct WideCommandButton: View {
    let systemName: String
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemName)
                .font(.system(size: 18, weight: .regular))
                .foregroundStyle(.primary)
                .frame(width: 40, height: 40)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .glassEffect(.regular, in: .circle)
        .help(help)
    }
}

struct BottomFilmstrip: View {
    var compact: Bool

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            FilmstripView(compact: compact)
                .frame(height: compact ? 102 : 154)
        }
        .background(.regularMaterial)
    }
}

struct BottomAnalysisPanel: View {
    var compact: Bool

    var body: some View {
        VStack(spacing: 0) {
            Divider()
            AnalysisStrip(compact: compact)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 4)
        }
        .background(.regularMaterial)
    }
}

struct BottomSimilarityPanel: View {
    var body: some View {
        VStack(spacing: 0) {
            Divider()
            SimilarityPanel(compact: true)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 6)
        }
        .background(.regularMaterial)
    }
}
