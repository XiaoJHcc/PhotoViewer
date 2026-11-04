import SwiftUI

#if os(macOS)
import AppKit

struct DesktopWorkspaceView: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @State private var showsDetails = false

    var body: some View {
        Group {
            if workspace.showFilmstrip {
                NavigationSplitView(columnVisibility: .constant(.all)) {
                    DesktopFileSidebar()
                        .navigationSplitViewColumnWidth(min: 200, ideal: 210, max: 220)
                        .toolbar(removing: .sidebarToggle)
                } detail: {
                    desktopDetail
                }
            } else {
                desktopDetail
            }
        }
        .background(.black)
        .navigationTitle(workspace.selectedPhoto.name)
        .background {
            DesktopWindowConfigurator()
                .frame(width: 0, height: 0)
        }
        .sheet(isPresented: $showsDetails) {
            PhotoDetailsSheet()
                .frame(minWidth: 480, minHeight: 480)
        }
    }

    private var desktopDetail: some View {
        HStack(spacing: 0) {
            HStack(spacing: 0) {
                PhotoStage(contentInsets: EdgeInsets())
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .layoutPriority(1)

                if workspace.showAnalysis {
                    Divider()
                    DesktopAnalysisSidebar()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .layoutPriority(1)

            if workspace.showControls {
                DesktopControlInspector(showsDetails: $showsDetails)
                    .frame(width: 94)
                    .frame(maxHeight: .infinity)
            }
        }
    }
}

private struct DesktopWindowConfigurator: NSViewRepresentable {
    func makeNSView(context: Context) -> DesktopWindowHostView {
        DesktopWindowHostView()
    }

    func updateNSView(_ view: DesktopWindowHostView, context: Context) {
        view.configureWindow()
    }
}

private final class DesktopWindowHostView: NSView {
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        configureWindow()
    }

    func configureWindow() {
        guard let window else { return }
        DispatchQueue.main.async { [weak window] in
            guard let window else { return }
            window.toolbar = nil
            window.toolbarStyle = .unified
            window.tabbingMode = .disallowed
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            for buttonType in [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton] {
                window.standardWindowButton(buttonType)?.isHidden = false
            }
            if !window.styleMask.contains(.fullSizeContentView) {
                window.styleMask.insert(.fullSizeContentView)
            }
        }
    }
}

private struct DesktopDarkScrollerConfigurator: NSViewRepresentable {
    func makeNSView(context: Context) -> DesktopDarkScrollHostView {
        DesktopDarkScrollHostView()
    }

    func updateNSView(_ view: DesktopDarkScrollHostView, context: Context) {
        view.configureScroller()
    }
}

private final class DesktopDarkScrollHostView: NSView {
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        configureScroller()
    }

    func configureScroller() {
        DispatchQueue.main.async { [weak self] in
            guard let scrollView = self?.enclosingScrollView else { return }
            scrollView.scrollerStyle = .legacy
            scrollView.autohidesScrollers = false
            scrollView.hasVerticalScroller = true
            if !(scrollView.verticalScroller is DarkVerticalScroller) {
                scrollView.verticalScroller = DarkVerticalScroller()
            }
        }
    }
}

private struct DesktopFileSidebar: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

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

    private var candidates: [DemoPhoto] {
        Array(workspace.visiblePhotos.filter { $0.id != workspace.selectedID }.prefix(6))
    }

    var body: some View {
        ScrollViewReader { proxy in
            List(selection: selection) {
                DesktopFilterBar()
                    .padding(.bottom, 8)

                if workspace.showSimilarity {
                    if candidates.isEmpty {
                        Text("无相似照片")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(Array(candidates.enumerated()), id: \.element.id) { index, photo in
                            DesktopSimilarityRow(photo: photo, score: 96 - index * 3)
                                .tag(photo.id)
                        }
                    }
                }

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
            .listStyle(.sidebar)
            .scrollIndicators(.visible)
            .background {
                DesktopDarkScrollerConfigurator()
                    .frame(width: 0, height: 0)
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

private struct DesktopSimilarityRow: View {
    let photo: DemoPhoto
    let score: Int

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
                Text("\(score)%")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 4)
        .accessibilityLabel("\(photo.name)，相似度 \(score)%")
    }
}

private struct DesktopFilterBar: View {
    @EnvironmentObject private var workspace: DemoWorkspace

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 0) {
                FinderPopUpButton(
                    systemName: "arrow.up.arrow.down",
                    titles: DemoSortKey.allCases.map(\.title),
                    selectedTitle: workspace.sortKey.title,
                    help: "排序方式"
                ) { title in
                    if let key = DemoSortKey.allCases.first(where: { $0.title == title }) {
                        workspace.sortKey = key
                    }
                }

                Divider()
                    .frame(height: 14)

                FinderPopUpButton(
                    systemName: workspace.ascending ? "chevron.up" : "chevron.down",
                    titles: ["升序", "降序"],
                    selectedTitle: workspace.ascending ? "升序" : "降序",
                    help: "排序方向"
                ) { title in
                    workspace.ascending = title == "升序"
                }

                Divider()
                    .frame(height: 14)

                FinderPopUpButton(
                    systemName: "star",
                    titles: DemoRatingFilter.allCases.map(\.title),
                    selectedTitle: workspace.ratingFilter.title,
                    help: "星级筛选"
                ) { title in
                    if let filter = DemoRatingFilter.allCases.first(where: { $0.title == title }) {
                        workspace.ratingFilter = filter
                    }
                }
            }
            .frame(width: 112, height: 32)
            .glassEffect(.regular, in: .capsule)
            .controlSize(.small)

            Text("共 \(workspace.visiblePhotos.count) 张")
                .font(.callout)
                .foregroundStyle(.secondary)

            DisclosureGroup(isExpanded: $workspace.showSimilarity) {
                EmptyView()
            } label: {
                Text("相似聚类")
            }
            .controlSize(.small)
        }
    }
}

private struct FinderPopUpButton: NSViewRepresentable {
    let systemName: String
    let titles: [String]
    let selectedTitle: String
    let help: String
    let onSelect: (String) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSButton {
        let button = NSButton(frame: NSRect(x: 0, y: 0, width: 32, height: 26))
        button.isBordered = false
        button.controlSize = .small
        button.imagePosition = .imageLeading
        button.imageHugsTitle = true
        button.attributedTitle = NSAttributedString(
            string: "⌄",
            attributes: [
                .font: NSFont.systemFont(ofSize: 9, weight: .semibold),
                .foregroundColor: NSColor.secondaryLabelColor
            ]
        )
        button.target = context.coordinator
        button.action = #selector(Coordinator.showMenu(_:))
        return button
    }

    func updateNSView(_ button: NSButton, context: Context) {
        button.image = NSImage(systemSymbolName: systemName, accessibilityDescription: help)
        button.toolTip = help
        context.coordinator.configure(
            titles: titles,
            selectedTitle: selectedTitle,
            onSelect: onSelect
        )
    }

    @MainActor final class Coordinator: NSObject {
        private let menu = NSMenu()
        private var onSelect: (String) -> Void = { _ in }

        func configure(titles: [String], selectedTitle: String, onSelect: @escaping (String) -> Void) {
            self.onSelect = onSelect
            menu.removeAllItems()
            for title in titles {
                let item = NSMenuItem(title: title, action: #selector(select(_:)), keyEquivalent: "")
                item.target = self
                item.state = title == selectedTitle ? .on : .off
                menu.addItem(item)
            }
        }

        @objc func showMenu(_ sender: NSButton) {
            menu.popUp(positioning: nil, at: NSPoint(x: 0, y: sender.bounds.height), in: sender)
        }

        @objc private func select(_ sender: NSMenuItem) {
            onSelect(sender.title)
        }
    }
}

private struct DesktopAnalysisSidebar: View {
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

private struct DesktopControlInspector: View {
    @EnvironmentObject private var workspace: DemoWorkspace
    @Binding var showsDetails: Bool

    var body: some View {
        DarkScrollView {
            VStack(spacing: 12) {
                DesktopInspectorSection(title: "EXIF") {
                    Button {
                        showsDetails = true
                    } label: {
                        VStack(spacing: 4) {
                            DesktopExifBlock(label: "光圈", value: workspace.selectedPhoto.aperture)
                            DesktopExifBlock(label: "快门", value: workspace.selectedPhoto.shutter)
                            DesktopExifBlock(label: "ISO", value: workspace.selectedPhoto.iso)
                            DesktopExifBlock(label: "焦距", value: workspace.selectedPhoto.focalLength)
                        }
                    }
                    .buttonStyle(.plain)
                    .help("EXIF 详情")
                }

                DesktopInspectorSection(title: "评分") {
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
                    DesktopCommandButton(systemName: "chevron.left", help: "上一张") {
                        workspace.selectPrevious()
                    }
                    .keyboardShortcut(.leftArrow, modifiers: [])

                    DesktopCommandButton(systemName: "chevron.right", help: "下一张") {
                        workspace.selectNext()
                    }
                    .keyboardShortcut(.rightArrow, modifiers: [])

                    DesktopCommandButton(
                        systemName: workspace.isZoomed ? "arrow.down.right.and.arrow.up.left" : "arrow.up.left.and.arrow.down.right",
                        help: workspace.isZoomed ? "适应窗口" : "放大到 100%"
                    ) {
                        workspace.toggleZoom()
                    }

                    DesktopCommandButton(systemName: "circle.lefthalf.filled", help: workspace.enhanced ? "关闭雾天增强" : "雾天增强") {
                        workspace.enhanced.toggle()
                    }

                    DesktopCommandButton(systemName: "square.grid.3x3", help: workspace.showAnalysis ? "隐藏分析栏" : "显示分析栏") {
                        workspace.showAnalysis.toggle()
                    }

                    DesktopCommandButton(systemName: "photo.on.rectangle", help: workspace.showFilmstrip ? "隐藏胶片栏" : "显示胶片栏") {
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

private struct DesktopInspectorSection<Content: View>: View {
    let title: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: 6) {
            DesktopInspectorHeading(title: title)
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

private struct DesktopInspectorHeading: View {
    let title: String

    var body: some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
    }
}

private struct DesktopExifBlock: View {
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

private struct DesktopCommandButton: View {
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

private final class DarkVerticalScroller: NSScroller {
    override func draw(_ dirtyRect: NSRect) {
        let track = bounds.insetBy(dx: 5, dy: 4)
        NSColor(white: 0.12, alpha: 0.35).setFill()
        NSBezierPath(roundedRect: track, xRadius: track.width / 2, yRadius: track.width / 2).fill()

        let knobHeight = max(18, bounds.height * knobProportion - 8)
        let travel = max(0, bounds.height - knobHeight - 8)
        let knob = NSRect(
            x: 4,
            y: 4 + travel * CGFloat(doubleValue),
            width: max(0, bounds.width - 8),
            height: knobHeight
        )
        NSColor(white: 0.22, alpha: 0.95).setFill()
        NSBezierPath(roundedRect: knob, xRadius: knob.width / 2, yRadius: knob.width / 2).fill()
    }
}

private struct DarkScrollView<Content: View>: NSViewRepresentable {
    let photoIDs: [Int]
    let centeringRequest: DemoCenteringRequest?
    let reduceMotion: Bool
    let content: Content

    init(
        photoIDs: [Int] = [],
        centeringRequest: DemoCenteringRequest? = nil,
        reduceMotion: Bool = false,
        @ViewBuilder content: () -> Content
    ) {
        self.photoIDs = photoIDs
        self.centeringRequest = centeringRequest
        self.reduceMotion = reduceMotion
        self.content = content()
    }

    func makeCoordinator() -> Coordinator {
        Coordinator()
    }

    func makeNSView(context: Context) -> NSScrollView {
        let scrollView = NSScrollView()
        scrollView.drawsBackground = false
        scrollView.hasVerticalScroller = true
        scrollView.hasHorizontalScroller = false
        scrollView.autohidesScrollers = false
        scrollView.scrollerStyle = .legacy
        scrollView.verticalScroller = DarkVerticalScroller()

        let hostingView = NSHostingView(rootView: content)
        hostingView.frame = NSRect(
            x: 0,
            y: 0,
            width: scrollView.contentView.bounds.width,
            height: hostingView.intrinsicContentSize.height
        )
        hostingView.autoresizingMask = [.width]
        scrollView.documentView = hostingView
        context.coordinator.hostingView = hostingView
        return scrollView
    }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        if let hostingView = context.coordinator.hostingView {
            hostingView.rootView = content
            hostingView.setFrameSize(NSSize(
                width: scrollView.contentView.bounds.width,
                height: hostingView.intrinsicContentSize.height
            ))
        }
        if !(scrollView.verticalScroller is DarkVerticalScroller) {
            scrollView.verticalScroller = DarkVerticalScroller()
        }
        context.coordinator.scrollToRequest(
            centeringRequest,
            photoIDs: photoIDs,
            reduceMotion: reduceMotion
        )
    }

    @MainActor final class Coordinator {
        var hostingView: NSHostingView<Content>?
        private var lastRequestID: UUID?

        func scrollToRequest(
            _ request: DemoCenteringRequest?,
            photoIDs: [Int],
            reduceMotion: Bool
        ) {
            guard let request, lastRequestID != request.id else { return }
            lastRequestID = request.id
            guard let index = photoIDs.firstIndex(of: request.photoID),
                  let hostingView,
                  let scrollView = hostingView.enclosingScrollView else { return }

            let viewportHeight = scrollView.contentView.bounds.height
            let target = 7 + CGFloat(index) * 143 + (138 - viewportHeight) / 2
            let maximum = max(0, hostingView.frame.height - viewportHeight)
            let point = NSPoint(x: 0, y: min(max(0, target), maximum))

            if reduceMotion {
                scrollView.contentView.setBoundsOrigin(point)
            } else {
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0.35
                    context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                    scrollView.contentView.animator().setBoundsOrigin(point)
                }
            }
        }
    }
}
#endif

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
