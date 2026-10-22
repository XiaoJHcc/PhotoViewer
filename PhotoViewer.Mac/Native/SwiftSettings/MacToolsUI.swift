// MacToolsUI.swift — PhotoViewer 原生工具窗口（SwiftUI / macOS 26）
//
// 与设置窗口同目录、同动态库：由 csproj 的 `_BuildSwiftSettingsDylib` 目标
// 与 MacSettingsUI.swift 一起编译成 libMacSettingsUI.dylib。
//
// 设计：C# 拥有工具页业务（EXIF 详情 / DINO 诊断）与页面 NSView；本文件只
// 提供与设置窗口一致的 NavigationSplitView 双栏外壳。分类选择由 SwiftUI
// 侧栏发起，经 C ABI 回传 C#；C# 随后以 JSON 状态推送选中项与 EXIF 可用性。

import SwiftUI
import AppKit

/// 工具窗口状态（camelCase 与 C# 侧 DTO 对应）。
struct SVToolsState: Codable {
    var selected: Int
    var exifEnabled: Bool

    static let empty = SVToolsState(selected: 0, exifEnabled: true)
}

/// 全局单例宿主：窗口、桥接模型与选择回调。
enum PVTools {
    /// C# 注册的分类选择回调。
    static var selectHandler: (@convention(c) (Int32) -> Void)?
    /// 当前工具窗口。
    static var window: NSWindow?
    /// 当前桥接模型。
    static var bridge: ToolsBridge?
}

/// 面向 SwiftUI 的可观察桥接：持有选中分类与详情宿主视图。
final class ToolsBridge: ObservableObject {
    @Published var selected: Int = 0
    @Published var exifEnabled: Bool = true
    let detailView: NSView

    init(detailView: NSView) {
        self.detailView = detailView
    }

    /// 应用 C# 推送的完整状态。
    func apply(_ json: String) {
        guard let data = json.data(using: .utf8) else { return }
        do {
            let decoded = try JSONDecoder().decode(SVToolsState.self, from: data)
            selected = decoded.selected
            exifEnabled = decoded.exifEnabled
        } catch {
            FileHandle.standardError.write(Data("[MacToolsUI] state decode failed: \(error)\n".utf8))
        }
    }

    /// 侧栏选择变化：本地即时更新并回传 C#；EXIF 不可用时忽略该分类。
    func requestSelection(_ index: Int) {
        if index == 0 && !exifEnabled { return }
        selected = index
        PVTools.selectHandler?(Int32(index))
    }
}

/// 工具窗口根视图：与设置窗口相同的双栏结构与侧栏行高。
struct ToolsRootView: View {
    @ObservedObject var vm: ToolsBridge

    var body: some View {
        NavigationSplitView(columnVisibility: .constant(.all)) {
            List(selection: selection) {
                Label {
                    Text("EXIF 详情")
                } icon: {
                    CategoryIcon(symbol: "info.circle.fill", color: .orange)
                }
                .tag(0)
                .disabled(!vm.exifEnabled)
                Label {
                    Text("DINO 诊断")
                } icon: {
                    CategoryIcon(symbol: "sparkles", color: .pink)
                }
                .tag(1)
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 200, ideal: 210, max: 220)
            .toolbar(removing: .sidebarToggle)
        } detail: {
            ToolsDetailHost(view: vm.detailView)
                .navigationTitle(vm.selected == 0 ? "EXIF 详情" : "DINO 诊断")
        }
        .preferredColorScheme(.dark)
    }

    /// 侧栏选中绑定：用户点选回传 C#；忽略清空选择。
    private var selection: Binding<Int?> {
        Binding(
            get: { vm.selected },
            set: { newValue in
                guard let newValue else { return }
                vm.requestSelection(newValue)
            })
    }
}

/// 把 C# 拥有的详情视图挂到 SwiftUI 详情栏。
struct ToolsDetailHost: NSViewRepresentable {
    let view: NSView

    func makeNSView(context: Context) -> NSView { view }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

/// 注册 C# 的分类选择回调。
@_cdecl("pv_tools_set_select_handler")
public func pv_tools_set_select_handler(_ handler: @escaping @convention(c) (Int32) -> Void) {
    PVTools.selectHandler = handler
}

/// 创建（或复用）工具窗口并返回窗口指针；Swift 不设置窗口代理，关窗语义归 C#。
@_cdecl("pv_tools_create")
public func pv_tools_create(_ detailView: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let detailView else { return nil }
    var result: UnsafeMutableRawPointer?
    onMain {
        if PVTools.window == nil {
            let view = Unmanaged<NSView>.fromOpaque(detailView).takeUnretainedValue()
            let bridge = ToolsBridge(detailView: view)
            PVTools.bridge = bridge
            let hosting = NSHostingController(rootView: ToolsRootView(vm: bridge))
            let window = makeSidebarWindow(
                title: "工具",
                size: NSSize(width: 880, height: 720),
                minSize: NSSize(width: 560, height: 420),
                content: hosting)
            let toolbar = NSToolbar(identifier: "PhotoViewer.Tools")
            toolbar.displayMode = .iconOnly
            toolbar.showsBaselineSeparator = false
            window.toolbar = toolbar
            (window as? SidebarWindow)?.pinnedTitle = "工具"
            PVTools.window = window
        }
        result = PVTools.window.map { Unmanaged.passUnretained($0).toOpaque() }
    }
    return result
}

/// 用 C# 计算出的最新状态刷新侧栏与详情标题。
@_cdecl("pv_tools_update_state")
public func pv_tools_update_state(_ json: UnsafePointer<CChar>?) {
    guard let json else { return }
    let text = String(cString: json)
    onMain { PVTools.bridge?.apply(text) }
}

/// 程序化选择分类（验收用），与用户点击走同一条选择绑定路径。
@_cdecl("pv_tools_select")
public func pv_tools_select(_ index: Int32) {
    onMain { PVTools.bridge?.requestSelection(Int(index)) }
}
