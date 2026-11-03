import SwiftUI

struct WideWorkspaceView: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @State private var showsDetails = false
    @State private var sidebarHidden = false

    var body: some View {
        GeometryReader { proxy in
            let usesSidebar = proxy.size.width >= 900
            let columnVisibility = Binding<NavigationSplitViewVisibility>(
                get: { usesSidebar && workspace.showFilmstrip && !sidebarHidden ? .all : .detailOnly },
                set: { sidebarHidden = $0 == .detailOnly }
            )

            NavigationSplitView(columnVisibility: columnVisibility) {
                SidebarPhotoList()
                    .navigationSplitViewColumnWidth(min: 230, ideal: 260, max: 310)
            } detail: {
                PhotoStage(contentInsets: EdgeInsets(top: 14, leading: 14, bottom: 14, trailing: 14))
                    .navigationTitle(workspace.selectedPhoto.name)
                    #if os(iOS)
                    .navigationBarTitleDisplayMode(.inline)
                    #endif
                    .toolbar { wideToolbar }
                    .inspector(isPresented: $workspace.showControls) {
                        PhotoInspector()
                            .inspectorColumnWidth(min: 280, ideal: 320, max: 360)
                    }
                    .safeAreaInset(edge: .bottom) {
                        if !usesSidebar && workspace.showFilmstrip {
                            BottomFilmstrip(compact: false)
                        }
                    }
                    .safeAreaInset(edge: .bottom) {
                        if workspace.showAnalysis {
                            BottomAnalysisPanel(compact: false)
                        }
                    }
                    .sheet(isPresented: $showsDetails) {
                        PhotoDetailsSheet()
                            .frame(minWidth: 480, minHeight: 480)
                    }
            }
            .navigationSplitViewStyle(.balanced)
        }
    }

    @ToolbarContentBuilder
    private var wideToolbar: some ToolbarContent {
        ToolbarItemGroup(placement: .navigation) {
            ControlGroup {
                Button {
                    workspace.selectPrevious()
                } label: {
                    Image(systemName: "chevron.left")
                }
                .help("上一张")
                .keyboardShortcut(.leftArrow, modifiers: [])

                Button {
                    workspace.selectNext()
                } label: {
                    Image(systemName: "chevron.right")
                }
                .help("下一张")
                .keyboardShortcut(.rightArrow, modifiers: [])
            }
        }

        ToolbarItemGroup(placement: .principal) {
            HStack(spacing: 14) {
                Picker(selection: $workspace.sortKey) {
                    ForEach(DemoSortKey.allCases) { key in
                        Text(key.title).tag(key)
                    }
                } label: {
                    Text(workspace.sortKey.title)
                }
                .pickerStyle(.menu)
                .fixedSize()
                .help("排序方式")

                Picker(selection: $workspace.ascending) {
                    Text("升序").tag(true)
                    Text("降序").tag(false)
                } label: {
                    Text(workspace.ascending ? "升序 ↑" : "降序 ↓")
                }
                .pickerStyle(.menu)
                .fixedSize()
                .help("排序方向")

                Picker(selection: $workspace.ratingFilter) {
                    ForEach(DemoRatingFilter.allCases) { filter in
                        Text(filter.title).tag(filter)
                    }
                } label: {
                    Text(workspace.ratingFilter.title)
                }
                .pickerStyle(.menu)
                .fixedSize()
                .help("星级筛选")

                Toggle(isOn: Binding(
                    get: { workspace.showSimilarity },
                    set: {
                        workspace.showSimilarity = $0
                        if $0 { workspace.showControls = true }
                    }
                )) {
                    Image(systemName: "square.on.square")
                }
                .toggleStyle(.button)
                .help("相似聚类")
            }
        }

        ToolbarItemGroup(placement: .primaryAction) {
            Button {
                workspace.showControls.toggle()
            } label: {
                Image(systemName: "sidebar.right")
            }
            .help(workspace.showControls ? "隐藏检查器" : "显示检查器")

            Menu {
                Button(workspace.enhanced ? "关闭雾天增强" : "雾天增强", systemImage: "circle.lefthalf.filled") {
                    workspace.enhanced.toggle()
                }
                Button(workspace.showAnalysis ? "隐藏分析栏" : "显示分析栏", systemImage: "square.grid.3x3") {
                    workspace.showAnalysis.toggle()
                }
                Divider()
                Button("EXIF 详情…", systemImage: "info") {
                    showsDetails = true
                }
            } label: {
                Image(systemName: "ellipsis")
            }
            .menuIndicator(.hidden)
            .help("更多操作")
        }
    }
}

private struct SidebarPhotoList: View {
    @EnvironmentObject private var workspace: DemoWorkspace

    private var selection: Binding<Int?> {
        Binding(
            get: { workspace.selectedID },
            set: { id in
                if let photo = workspace.photos.first(where: { $0.id == id }) {
                    workspace.select(photo)
                }
            }
        )
    }

    var body: some View {
        List(selection: selection) {
            Section("照片 · \(workspace.visiblePhotos.count)") {
                ForEach(workspace.visiblePhotos) { photo in
                    SidebarPhotoRow(photo: photo)
                        .tag(photo.id)
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
    }
}

private struct SidebarPhotoRow: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    let photo: DemoPhoto

    var body: some View {
        HStack(spacing: 10) {
            DemoArtwork(photo: photo)
                .frame(width: 48, height: 48)
                .clipShape(.rect(cornerRadius: 5))

            VStack(alignment: .leading, spacing: 3) {
                Text(photo.name)
                    .font(.callout.weight(.medium))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(photo.capturedAt)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Spacer(minLength: 4)

            if workspace.rating(of: photo) > 0 {
                Label("\(workspace.rating(of: photo))", systemImage: "star.fill")
                    .labelStyle(.titleAndIcon)
                    .font(.caption.weight(.medium))
                    .foregroundStyle(.yellow)
            }
        }
        .padding(.vertical, 4)
        .accessibilityLabel("\(photo.name)，\(workspace.rating(of: photo)) 星")
    }
}

private struct PhotoInspector: View {
    @EnvironmentObject private var workspace: DemoWorkspace

    var body: some View {
        Form {
            Section("拍摄参数") {
                LabeledContent("光圈", value: workspace.selectedPhoto.aperture)
                LabeledContent("快门", value: workspace.selectedPhoto.shutter)
                LabeledContent("ISO", value: workspace.selectedPhoto.iso)
                LabeledContent("焦距", value: workspace.selectedPhoto.focalLength)
            }

            Section("评分") {
                StarRatingControl()
                    .frame(maxWidth: .infinity)
            }

            Section("显示") {
                Toggle("雾天增强", isOn: $workspace.enhanced)
                Toggle("分析栏", isOn: $workspace.showAnalysis)
                Toggle("相似聚类", isOn: $workspace.showSimilarity)
            }

            if workspace.showSimilarity {
                Section("相似照片") {
                    SimilarityPanel(compact: true)
                        .padding(.vertical, 4)
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
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
