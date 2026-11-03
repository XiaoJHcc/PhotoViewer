import SwiftUI

#if os(iOS)
struct CompactWorkspaceView: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @State private var showsFilters = false
    @State private var showsDetails = false

    var body: some View {
        NavigationStack {
            PhotoStage(contentInsets: EdgeInsets(top: 10, leading: 10, bottom: 10, trailing: 10))
                .navigationTitle(workspace.selectedPhoto.name)
                .navigationBarTitleDisplayMode(.inline)
                .toolbar { compactToolbar }
                .toolbar(workspace.showControls ? .visible : .hidden, for: .bottomBar)
                .safeAreaInset(edge: .bottom) {
                    if workspace.showFilmstrip {
                        BottomFilmstrip(compact: true)
                    }
                }
                .safeAreaInset(edge: .bottom) {
                    if workspace.showAnalysis {
                        BottomAnalysisPanel(compact: true)
                    }
                }
                .safeAreaInset(edge: .bottom) {
                    if workspace.showSimilarity {
                        BottomSimilarityPanel()
                    }
                }
                .sheet(isPresented: $showsFilters) {
                    CompactFilterSheet()
                }
                .sheet(isPresented: $showsDetails) {
                    PhotoDetailsSheet()
                }
        }
    }

    @ToolbarContentBuilder
    private var compactToolbar: some ToolbarContent {
        ToolbarItem(placement: .topBarLeading) {
            Button {
                showsFilters = true
            } label: {
                Image(systemName: "line.3.horizontal.decrease")
            }
            .accessibilityLabel("筛选")
        }

        ToolbarItem(placement: .topBarTrailing) {
            Button {
                showsDetails = true
            } label: {
                Image(systemName: "info")
            }
            .accessibilityLabel("EXIF 详情")
        }

        ToolbarItemGroup(placement: .bottomBar) {
            Button {
                workspace.selectPrevious()
            } label: {
                Image(systemName: "chevron.left")
            }
            .accessibilityLabel("上一张")

            Button {
                workspace.selectNext()
            } label: {
                Image(systemName: "chevron.right")
            }
            .accessibilityLabel("下一张")
        }

        ToolbarSpacer(.flexible, placement: .bottomBar)

        ToolbarItem(placement: .bottomBar) {
            Menu {
                ForEach(0...5, id: \.self) { rating in
                    Button {
                        workspace.setRating(rating)
                    } label: {
                        if workspace.selectedPhoto.rating == rating {
                            Label(rating == 0 ? "清除星级" : "\(rating) 星", systemImage: "checkmark")
                        } else {
                            Text(rating == 0 ? "清除星级" : "\(rating) 星")
                        }
                    }
                }
            } label: {
                Label("\(workspace.selectedPhoto.rating) 星", systemImage: workspace.selectedPhoto.rating > 0 ? "star.fill" : "star")
            }
            .menuIndicator(.hidden)
            .accessibilityLabel("评分")
        }

        ToolbarSpacer(.flexible, placement: .bottomBar)

        ToolbarItemGroup(placement: .bottomBar) {
            Button {
                workspace.toggleZoom()
            } label: {
                Image(systemName: workspace.isZoomed ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right")
            }
            .accessibilityLabel(workspace.isZoomed ? "适应窗口" : "放大到 100%")

            Menu {
                Button(workspace.enhanced ? "关闭雾天增强" : "雾天增强", systemImage: "circle.lefthalf.filled") {
                    workspace.enhanced.toggle()
                }
                Button(workspace.showAnalysis ? "隐藏分析栏" : "显示分析栏", systemImage: "square.grid.3x3") {
                    workspace.showAnalysis.toggle()
                }
                Button(workspace.showSimilarity ? "隐藏相似聚类" : "显示相似聚类", systemImage: "square.on.square") {
                    workspace.showSimilarity.toggle()
                }
                Button(workspace.showFilmstrip ? "隐藏胶片栏" : "显示胶片栏", systemImage: "photo.on.rectangle") {
                    workspace.showFilmstrip.toggle()
                }
                Button(workspace.showControls ? "隐藏底部操作栏" : "显示底部操作栏", systemImage: "slider.horizontal.3") {
                    workspace.showControls.toggle()
                }
            } label: {
                Image(systemName: "ellipsis")
            }
            .menuIndicator(.hidden)
            .accessibilityLabel("更多操作")
        }
    }
}

private struct CompactFilterSheet: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("排序与筛选") {
                    Picker("排序方式", selection: $workspace.sortKey) {
                        ForEach(DemoSortKey.allCases) { key in
                            Text(key.title).tag(key)
                        }
                    }
                    Picker("排序方向", selection: $workspace.ascending) {
                        Text("升序").tag(true)
                        Text("降序").tag(false)
                    }
                    Picker("星级", selection: $workspace.ratingFilter) {
                        ForEach(DemoRatingFilter.allCases) { filter in
                            Text(filter.title).tag(filter)
                        }
                    }
                    Toggle("相似聚类", isOn: $workspace.showSimilarity)
                }
                Section("显示") {
                    Toggle("胶片栏", isOn: $workspace.showFilmstrip)
                    Toggle("分析栏", isOn: $workspace.showAnalysis)
                    Toggle("底部操作栏", isOn: $workspace.showControls)
                    Toggle("雾天增强", isOn: $workspace.enhanced)
                }
            }
            .navigationTitle("筛选")
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }
}
#endif
