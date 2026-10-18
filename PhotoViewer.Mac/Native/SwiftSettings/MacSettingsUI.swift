// MacSettingsUI.swift — PhotoViewer 原生设置窗口（SwiftUI / macOS 26）
//
// 该文件由 MSBuild 目标 `_BuildSwiftSettingsDylib` 用 swiftc 编译成
// Contents/MonoBundle/libMacSettingsUI.dylib，供 C# 侧的
// MacNativeSettingsPresenter 通过 C ABI 调用。
//
// 设计：C# 拥有设置工作副本与全部业务/校验/持久化逻辑；本文件只负责
// macOS 26 系统设置外观的 SwiftUI 渲染与真实 AppKit 事件录制。
// 状态以 JSON 快照由 C# 推入；用户交互统一回传 JSON 动作，再由 C#
// 推送新状态刷新界面（单一数据源，避免两侧状态漂移）。

import SwiftUI
import AppKit

// MARK: - 状态模型（与 C# 侧 MacSettingsBridge DTO 一一对应，camelCase）

/// 文件格式条目。
struct SVFormat: Codable {
    var name: String
    var extensions: String
    var enabled: Bool
}

/// EXIF 显示条目。
struct SVExifItem: Codable {
    var name: String
    var enabled: Bool
}

/// 缩放比例预设条目。
struct SVPreset: Codable {
    var index: Int
    var text: String
    var display: String
}

/// 控制栏功能（快捷键）条目。
struct SVHotkey: Codable {
    var name: String
    var isDisplay: Bool
    var primary: String
    var secondary: String
    var primaryConflict: Bool
    var secondaryConflict: Bool
    /// 空串 / "primary" / "secondary"，用于显示录制态。
    var recording: String
}

/// 布局模式条目。
struct SVLayoutMode: Codable {
    var name: String
    var description: String
}

/// 文件分页状态。
struct SVFileState: Codable {
    var sameNameAsOnePhoto: Bool
    var formats: [SVFormat]
    var nativeParallelism: Int
    var nativeParallelismMax: Int
    var cpuParallelism: Int
    var cpuParallelismMax: Int
    var cacheMemory: Int
    var cacheCount: Int
    var preloadForward: Int
    var preloadBackward: Int
    var preloadCenter: Int
    var preloadDelay: Int
    var preloadMax: Int
    var budgetInfo: String
    var budgetLevel: String
    var cacheCountInfo: String
    var cacheCountLevel: String
}

/// 预览分页状态。
struct SVPreviewState: Codable {
    var showZoomIndicator: Bool
    var presets: [SVPreset]
}

/// 控制分页状态。
struct SVControlState: Codable {
    var layoutModes: [SVLayoutMode]
    var layoutIndex: Int
    var hotkeys: [SVHotkey]
    var useAppleKeyboard: Bool
    var mappingTargets: [String]
    var commandTarget: Int
    var optionTarget: Int
    var controlTarget: Int
    var recordingActive: Bool
}

/// EXIF 分页状态。
struct SVExifState: Codable {
    var items: [SVExifItem]
    var showRating: Bool
    var safeSetRating: Bool
}

/// AI 分页状态。
struct SVAiState: Codable {
    var similarityThreshold: Int
    var maxResults: Int
    var immediateCompute: Bool
    var clearButtonText: String
    var clearStatus: String
    var clearBusy: Bool
}

/// 设置窗口完整状态。
struct SVState: Codable {
    var category: Int
    var title: String
    var persistenceStatus: String
    var canGoBack: Bool
    var canGoForward: Bool
    var file: SVFileState
    var preview: SVPreviewState
    var control: SVControlState
    var exif: SVExifState
    var ai: SVAiState

    /// 解码失败时的安全占位，避免窗口空白崩溃。
    static var empty: SVState {
        SVState(
            category: 0, title: "文件", persistenceStatus: "", canGoBack: false, canGoForward: false,
            file: SVFileState(
                sameNameAsOnePhoto: true, formats: [],
                nativeParallelism: 8, nativeParallelismMax: 8,
                cpuParallelism: 8, cpuParallelismMax: 8,
                cacheMemory: 2048, cacheCount: 30,
                preloadForward: 10, preloadBackward: 5, preloadCenter: 5,
                preloadDelay: 1000, preloadMax: 10,
                budgetInfo: "", budgetLevel: "normal", cacheCountInfo: "", cacheCountLevel: "normal"),
            preview: SVPreviewState(showZoomIndicator: true, presets: []),
            control: SVControlState(
                layoutModes: [], layoutIndex: 2, hotkeys: [], useAppleKeyboard: true,
                mappingTargets: ["不映射", "Ctrl", "Alt", "Win"],
                commandTarget: 1, optionTarget: 2, controlTarget: 1, recordingActive: false),
            exif: SVExifState(items: [], showRating: true, safeSetRating: true),
            ai: SVAiState(
                similarityThreshold: 85, maxResults: 8, immediateCompute: false,
                clearButtonText: "清除特征数据库", clearStatus: "", clearBusy: false))
    }
}

/// 侧栏分类的静态定义（标题 / SF Symbol / 系统设置风格底板颜色）。
struct SVCategory {
    let title: String
    let symbol: String
    let color: Color

    static let all: [SVCategory] = [
        SVCategory(title: "文件", symbol: "folder.fill", color: .blue),
        SVCategory(title: "预览", symbol: "photo.fill", color: .purple),
        SVCategory(title: "控制", symbol: "keyboard.fill", color: .gray),
        SVCategory(title: "EXIF", symbol: "info.circle.fill", color: .orange),
        SVCategory(title: "AI", symbol: "sparkles", color: .pink),
    ]
}

// MARK: - C ABI 出口（C# 通过 NativeLibrary 调用的导出函数）

/// 全局单例宿主：窗口、桥接模型、回调函数指针与事件监视器。
enum PV {
    /// C# 注册的动作回调；返回值 1 表示消费当前事件（录制时不透传）。
    static var actionHandler: (@convention(c) (UnsafePointer<CChar>?) -> Int32)?
    /// C# 注册的窗口关闭回调。
    static var closeHandler: (@convention(c) () -> Void)?
    /// 当前设置窗口。
    static var window: NSWindow?
    /// 当前桥接模型。
    static var bridge: SettingsBridge?
    /// 本地事件监视器句柄。
    static var monitor: Any?
    /// 窗口关闭代理，持有以防提前释放。
    static var windowDelegate: WindowCloseDelegate?
}

/// 在必要时切到主线程同步执行，保证 AppKit 调用线程正确。
private func onMain(_ block: () -> Void) {
    if Thread.isMainThread {
        block()
    } else {
        DispatchQueue.main.sync(execute: block)
    }
}

/// 注册 C# 的动作回调。
@_cdecl("pv_settings_set_action_handler")
public func pv_settings_set_action_handler(_ handler: @escaping @convention(c) (UnsafePointer<CChar>?) -> Int32) {
    PV.actionHandler = handler
}

/// 注册 C# 的窗口关闭回调。
@_cdecl("pv_settings_set_close_handler")
public func pv_settings_set_close_handler(_ handler: @escaping @convention(c) () -> Void) {
    PV.closeHandler = handler
}

/// 创建（或复用）设置窗口并载入初始状态。
@_cdecl("pv_settings_show")
public func pv_settings_show(_ json: UnsafePointer<CChar>?) {
    guard let json else { return }
    let text = String(cString: json)
    onMain {
        ensureWindow()
        PV.bridge?.apply(text)
        PV.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
}

/// 用 C# 计算出的最新状态刷新界面。
@_cdecl("pv_settings_update_state")
public func pv_settings_update_state(_ json: UnsafePointer<CChar>?) {
    guard let json else { return }
    let text = String(cString: json)
    onMain { PV.bridge?.apply(text) }
}

/// 关闭设置窗口；会触发关闭回调，由 C# 完成工作副本回写。
@_cdecl("pv_settings_close")
public func pv_settings_close() {
    onMain { PV.window?.close() }
}

/// 读取窗口号，供宿主 screencapture 取证。
@_cdecl("pv_settings_window_number")
public func pv_settings_window_number() -> Int32 {
    var number: Int32 = 0
    onMain { number = Int32(PV.window?.windowNumber ?? 0) }
    return number
}

/// 校验窗口是否为深色外观。
@_cdecl("pv_settings_is_dark")
public func pv_settings_is_dark() -> Int32 {
    var dark = false
    onMain { dark = PV.window?.appearance?.name == .darkAqua }
    return dark ? 1 : 0
}

/// 校验内容控制器是否已挂载 SwiftUI 宿主视图。
@_cdecl("pv_settings_has_hosting_controller")
public func pv_settings_has_hosting_controller() -> Int32 {
    var mounted = false
    onMain { mounted = (PV.window?.contentViewController as? NSHostingController<SettingsRootView>)?.view.superview != nil }
    return mounted ? 1 : 0
}

/// 调整窗口内容尺寸，用于窗口缩放回归。
@_cdecl("pv_settings_set_content_size")
public func pv_settings_set_content_size(_ width: Double, _ height: Double) {
    onMain { PV.window?.setContentSize(NSSize(width: width, height: height)) }
}

/// 进入快捷键录制态（供程序化验收复用）。
@_cdecl("pv_settings_begin_recording")
public func pv_settings_begin_recording(_ hotkeyIndex: Int32, _ primary: Int32) {
    onMain { PV.bridge?.send(["type": "beginRecord", "index": Int(hotkeyIndex), "intValue": primary != 0 ? 1 : 0]) }
}

/// 把设置窗口真实合成画面（优先 screencapture -l 窗口捕获，回退 cacheDisplay）导出为 PNG（验收取证）。
@_cdecl("pv_settings_render")
public func pv_settings_render(_ path: UnsafePointer<CChar>?) {
    guard let path else { return }
    let text = String(cString: path)
    var winNum: Int = 0
    onMain {
        PV.window?.contentView?.layoutSubtreeIfNeeded()
        PV.window?.displayIfNeeded()
        winNum = PV.window?.windowNumber ?? 0
    }
    if winNum > 0 {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        proc.arguments = ["-o", "-x", "-l", "\(winNum)", text]
        try? proc.run()
        proc.waitUntilExit()
        if proc.terminationStatus == 0,
           let attrs = try? FileManager.default.attributesOfItem(atPath: text),
           let size = attrs[.size] as? NSNumber, size.intValue > 2048 {
            return
        }
    }
    onMain {
        guard let view = PV.window?.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let data = rep.representation(using: .png, properties: [:]) else { return }
        try? data.write(to: URL(fileURLWithPath: text))
    }
}

/// 创建窗口、SwiftUI 宿主控制器与本地事件监视器。
private func ensureWindow() {
    if PV.window != nil { return }
    let bridge = SettingsBridge()
    PV.bridge = bridge
    let hosting = NSHostingController(rootView: SettingsRootView(vm: bridge))
    let window = NSWindow(
        contentRect: NSRect(x: 0, y: 0, width: 880, height: 720),
        styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
        backing: .buffered, defer: false)
    window.title = "PhotoViewer 设置"
    window.titlebarAppearsTransparent = true
    window.appearance = NSAppearance(named: .darkAqua)
    window.contentViewController = hosting
    window.minSize = NSSize(width: 760, height: 540)
    window.toolbarStyle = .unified
    window.isReleasedWhenClosed = false
    window.center()
    let delegate = WindowCloseDelegate()
    PV.windowDelegate = delegate
    window.delegate = delegate
    PV.window = window
    installMonitor()
}

/// 安装全局本地事件监视器：录制态时捕获真实按键/鼠标/滚轮事件并交由 C# 判定。
private func installMonitor() {
    guard PV.monitor == nil else { return }
    let mask: NSEvent.EventTypeMask = [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown, .scrollWheel]
    PV.monitor = NSEvent.addLocalMonitorForEvents(matching: mask) { event in
        guard let bridge = PV.bridge, bridge.state.control.recordingActive else { return event }
        if let eventWindow = event.window, eventWindow !== PV.window { return event }
        return bridge.capture(event) ? nil : event
    }
}

/// 窗口关闭代理：清理宿主并通知 C# 完成回写。
final class WindowCloseDelegate: NSObject, NSWindowDelegate {
    func windowWillClose(_ notification: Notification) {
        PV.window = nil
        PV.bridge = nil
        PV.closeHandler?()
    }
}

// MARK: - 桥接模型

/// 面向 SwiftUI 的可观察桥接：持有 C# 推送的状态，并把交互回传为 JSON 动作。
final class SettingsBridge: ObservableObject {
    @Published var state: SVState = .empty
    @Published var selectedCategory: Int? = 0
    @Published var presetDrafts: [Int: String] = [:]

    /// 应用 C# 推送的完整状态；控件自带的草稿在此复位为权威值。
    func apply(_ json: String) {
        guard let data = json.data(using: .utf8) else { return }
        do {
            let decoded = try JSONDecoder().decode(SVState.self, from: data)
            state = decoded
            selectedCategory = decoded.category
            presetDrafts = [:]
        } catch {
            FileHandle.standardError.write(Data("[MacSettingsUI] state decode failed: \(error)\n".utf8))
        }
    }

    /// 把动作序列化后交给 C# 处理；返回 C# 的消费标记（录制用）。
    @discardableResult
    func send(_ action: [String: Any]) -> Int32 {
        guard let data = try? JSONSerialization.data(withJSONObject: action),
              let json = String(data: data, encoding: .utf8) else { return 0 }
        return sendRaw(json)
    }

    /// 侧栏选择变化：本地即时更新并回传分类动作。
    func requestCategory(_ index: Int?) {
        selectedCategory = index
        guard let index, index != state.category else { return }
        send(["type": "selectCategory", "index": index])
    }

    /// 录制态捕获真实事件：按事件类型只读取相关字段（无关字段在合成事件上会抛异常），
    /// 序列化后回传 C#，由 C# 复用共享手势映射判定。
    func capture(_ event: NSEvent) -> Bool {
        let type = event.type.rawValue
        let modifiers = event.modifierFlags.rawValue
        var keyCode = 0
        var buttonNumber = 0
        var deltaY = 0.0
        var precise = false
        var characters = ""
        switch event.type {
        case .keyDown:
            keyCode = Int(event.keyCode)
            characters = Self.escapeJSON(String((event.charactersIgnoringModifiers ?? "").prefix(4)))
        case .leftMouseDown, .rightMouseDown, .otherMouseDown:
            buttonNumber = event.buttonNumber
        case .scrollWheel:
            let delta = event.scrollingDeltaY
            deltaY = delta.isFinite ? Double(delta) : 0
            precise = event.hasPreciseScrollingDeltas
        default:
            return false
        }
        let json = "{"
            + "\"type\":\"recordEvent\","
            + "\"eventType\":\(type),"
            + "\"keyCode\":\(keyCode),"
            + "\"modifiers\":\(modifiers),"
            + "\"buttonNumber\":\(buttonNumber),"
            + "\"scrollingDeltaY\":\(deltaY),"
            + "\"precise\":\(precise),"
            + "\"characters\":\"\(characters)\""
            + "}"
        return sendRaw(json) == 1
    }

    /// 直接把 JSON 字符串交给 C# 处理。
    func sendRaw(_ json: String) -> Int32 {
        guard let handler = PV.actionHandler else { return 0 }
        return json.withCString { handler($0) }
    }

    /// 转义 JSON 字符串字面量中的控制字符与引号。
    private static func escapeJSON(_ text: String) -> String {
        var output = ""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": output += "\\\""
            case "\\": output += "\\\\"
            case "\n": output += "\\n"
            case "\r": output += "\\r"
            case "\t": output += "\\t"
            default:
                if scalar.value < 0x20 { output += String(format: "\\u%04x", scalar.value) }
                else { output.unicodeScalars.append(scalar) }
            }
        }
        return output
    }

    // MARK: 绑定工厂

    /// 布尔绑定：写入即回传 setBool 动作。
    func bindBool(_ key: String, _ get: @escaping () -> Bool) -> Binding<Bool> {
        Binding(get: get, set: { [weak self] value in
            self?.send(["type": "setBool", "key": key, "boolValue": value])
        })
    }

    /// 整数绑定：写入即回传 setInt 动作。
    func bindInt(_ key: String, _ get: @escaping () -> Int) -> Binding<Int> {
        Binding(get: get, set: { [weak self] value in
            self?.send(["type": "setInt", "key": key, "intValue": value])
        })
    }

    /// 整数绑定（Double 形态，供 Slider 使用）。
    func bindIntAsDouble(_ key: String, _ get: @escaping () -> Int) -> Binding<Double> {
        Binding(
            get: { Double(get()) },
            set: { [weak self] value in
                self?.send(["type": "setInt", "key": key, "intValue": Int(value.rounded())])
            })
    }
}

// MARK: - 根视图

/// 设置窗口根视图：macOS 26 原生 NavigationSplitView 双栏结构。
/// 侧栏用系统 `List(selection:)` 承载选中态，由 macOS 绘制标准侧栏高亮
/// （自动跟随系统强调色、圆角与左右留白），不再手绘背景色与行内边距。
struct SettingsRootView: View {
    @ObservedObject var vm: SettingsBridge

    var body: some View {
        NavigationSplitView(columnVisibility: .constant(.all)) {
            List(selection: selection) {
                ForEach(Array(SVCategory.all.enumerated()), id: \.offset) { index, category in
                    Label {
                        Text(category.title)
                    } icon: {
                        CategoryIcon(category: category)
                    }
                    .tag(index)
                }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 200, ideal: 210, max: 220)
            .toolbar(removing: .sidebarToggle)
        } detail: {
            SettingsDetailView(vm: vm)
        }
        .preferredColorScheme(.dark)
        .frame(minWidth: 760, minHeight: 540)
    }

    /// 侧栏选中绑定：用户点选回传分类动作；C# 推送的分类变更经 getter 反映到侧栏。
    /// 忽略清空选择（设置侧栏始终有当前分类），避免出现无高亮的空态。
    private var selection: Binding<Int?> {
        Binding(
            get: { vm.selectedCategory ?? vm.state.category },
            set: { newValue in
                guard let newValue else { return }
                vm.requestCategory(newValue)
            })
    }
}

/// 侧栏分类图标：系统设置风格的彩色圆角图标底板（渐变随强调色保持层次）。
struct CategoryIcon: View {
    let category: SVCategory

    var body: some View {
        Image(systemName: category.symbol)
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(.white)
            .frame(width: 22, height: 22)
            .background(category.color.gradient, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
    }
}

/// 右侧详情：按分类切换到对应分页，并挂载历史前进/后退与分页标题。
struct SettingsDetailView: View {
    @ObservedObject var vm: SettingsBridge

    var body: some View {
        VStack(spacing: 0) {
            if !vm.state.persistenceStatus.isEmpty {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill")
                    Text(vm.state.persistenceStatus)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                }
                .font(.callout)
                .foregroundStyle(.orange)
                .padding(.horizontal, 20)
                .padding(.vertical, 8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.orange.opacity(0.12))
            }
            Group {
                switch vm.state.category {
                case 0: FileSettingsPage(vm: vm)
                case 1: PreviewSettingsPage(vm: vm)
                case 2: ControlSettingsPage(vm: vm)
                case 3: ExifSettingsPage(vm: vm)
                default: AiSettingsPage(vm: vm)
                }
            }
        }
        .navigationTitle(vm.state.title)
        .toolbar {
            ToolbarItem(placement: .navigation) {
                ControlGroup {
                    Button {
                        vm.send(["type": "navigate", "forward": false])
                    } label: {
                        Image(systemName: "chevron.left")
                    }
                    .disabled(!vm.state.canGoBack)
                    .help("后退")

                    Button {
                        vm.send(["type": "navigate", "forward": true])
                    } label: {
                        Image(systemName: "chevron.right")
                    }
                    .disabled(!vm.state.canGoForward)
                    .help("前进")
                }
                .controlGroupStyle(.navigation)
            }
        }
    }
}

// MARK: - 通用行与工具

/// 设置界面统一的尺寸规范：对齐 macOS 系统设置分组表单的行高与留白。
/// 内嵌可排序列表的行高与边距全部取自此处，避免各分页各行其是导致单行高度不一致。
enum SettingsMetrics {
    /// 单行内容（勾选框 / 文字 / 把手）的行高。
    static let rowHeight: CGFloat = 38
    /// 含副标题两行内容时的行高。
    static let rowHeightTall: CGFloat = 54
    /// 行内容距卡片边缘的水平留白。
    static let rowHorizontalInset: CGFloat = 8
    /// 行内容在行内的上下留白。
    static let rowVerticalInset: CGFloat = 4

    /// 行内容可用高度（行高扣除上下留白）。
    static func contentHeight(_ rowHeight: CGFloat) -> CGFloat { rowHeight - rowVerticalInset * 2 }
}

/// 内嵌可排序列表的统一行：固定行高、统一四周留白、透明行背景。
private struct SettingsListRowModifier: ViewModifier {
    let height: CGFloat

    func body(content: Content) -> some View {
        content
            .frame(height: SettingsMetrics.contentHeight(height))
            .listRowInsets(EdgeInsets(top: SettingsMetrics.rowVerticalInset,
                                      leading: SettingsMetrics.rowHorizontalInset,
                                      bottom: SettingsMetrics.rowVerticalInset,
                                      trailing: SettingsMetrics.rowHorizontalInset))
            .listRowBackground(Color.clear)
    }
}

/// 内嵌可排序列表的统一容器：固定行高、不自带滚动，交由外层 Form 滚动。
private struct SettingsReorderListModifier: ViewModifier {
    let rows: Int
    let rowHeight: CGFloat

    func body(content: Content) -> some View {
        content
            .listStyle(.plain)
            .environment(\.defaultMinListRowHeight, rowHeight)
            .scrollDisabled(true)
            .scrollContentBackground(.hidden)
            .frame(height: CGFloat(max(1, rows)) * rowHeight)
    }
}

extension View {
    /// 按统一规范设置内嵌列表行的行高与留白。
    func settingsListRow(_ height: CGFloat) -> some View { modifier(SettingsListRowModifier(height: height)) }
    /// 按统一规范设置内嵌可排序列表容器。
    func settingsReorderList(rows: Int, rowHeight: CGFloat) -> some View {
        modifier(SettingsReorderListModifier(rows: rows, rowHeight: rowHeight))
    }
}

/// 把语义级别映射到 SwiftUI 颜色。
func levelColor(_ level: String) -> Color {
    switch level {
    case "warning": return .orange
    case "error": return .red
    default: return .secondary
    }
}

/// 拖拽把手图标。
struct DragHandle: View {
    var body: some View {
        Image(systemName: "line.3.horizontal")
            .font(.system(size: 12, weight: .medium))
            .foregroundStyle(.tertiary)
            .padding(.horizontal, 4)
            .padding(.vertical, 4)
    }
}

/// 一行可勾选并带拖拽把手的列表项内容。
struct CheckableRow<Trailing: View>: View {
    let enabled: Binding<Bool>
    let title: String
    let subtitle: String?
    @ViewBuilder let trailing: () -> Trailing

    init(enabled: Binding<Bool>, title: String, subtitle: String? = nil, @ViewBuilder trailing: @escaping () -> Trailing) {
        self.enabled = enabled
        self.title = title
        self.subtitle = subtitle
        self.trailing = trailing
    }

    var body: some View {
        HStack(spacing: 8) {
            Toggle("", isOn: enabled)
                .toggleStyle(.checkbox)
                .labelsHidden()
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                if let subtitle, !subtitle.isEmpty {
                    Text(subtitle).font(.caption).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: 8)
            trailing()
            DragHandle()
        }
    }
}

/// 将 SwiftUI List.onMove 的插入偏移（移除前坐标）转换为 C# Move(fromIndex, toIndex) 的目标索引（移除后坐标）。
func convertMoveOffset(from offsets: IndexSet, to destination: Int) -> (from: Int, to: Int)? {
    guard let from = offsets.first else { return nil }
    let to = destination > from ? destination - 1 : destination
    guard from != to else { return nil }
    return (from, to)
}

/// 等宽数值文本。
struct ValueText: View {
    let text: String
    var body: some View {
        Text(text).monospacedDigit().foregroundStyle(.secondary)
    }
}

// MARK: - 文件分页

/// 文件分页：同名合并、支持格式勾选排序、缓存与预加载。
struct FileSettingsPage: View {
    @ObservedObject var vm: SettingsBridge
    private var file: SVFileState { vm.state.file }

    var body: some View {
        Form {
            Section {
                Toggle(isOn: vm.bindBool("file.sameName") { vm.state.file.sameNameAsOnePhoto }) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("同名文件视为同一张图片")
                        Text("按列表排序优先级高的作为代表显示图片，修改星级时同步至所有同名文件")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.switch)
            }

            Section("支持的文件格式") {
                List {
                    ForEach(Array(file.formats.enumerated()), id: \.element.name) { index, format in
                        CheckableRow(
                            enabled: vm.bindBool("file.format.\(index)") { vm.state.file.formats[index].enabled },
                            title: format.name,
                            subtitle: format.extensions
                        ) { EmptyView() }
                        .settingsListRow(SettingsMetrics.rowHeightTall)
                    }
                    .onMove { offsets, destination in
                        if let move = convertMoveOffset(from: offsets, to: destination) {
                            vm.send(["type": "moveFormat", "from": move.from, "to": move.to])
                        }
                    }
                }
                .settingsReorderList(rows: file.formats.count, rowHeight: SettingsMetrics.rowHeightTall)
            }

            Section {
                Stepper(value: vm.bindInt("file.nativeParallelism") { vm.state.file.nativeParallelism },
                        in: 1...max(1, file.nativeParallelismMax)) {
                    HStack {
                        Text("原生解码线程")
                        Spacer()
                        ValueText(text: "\(file.nativeParallelism) 线程")
                    }
                }
                Stepper(value: vm.bindInt("file.cpuParallelism") { vm.state.file.cpuParallelism },
                        in: 1...max(1, file.cpuParallelismMax)) {
                    HStack {
                        Text("软件解码线程")
                        Spacer()
                        ValueText(text: "\(file.cpuParallelism) 线程")
                    }
                }
                SliderRow(
                    title: "缓存内存上限",
                    value: vm.bindIntAsDouble("file.cacheMemory") { vm.state.file.cacheMemory },
                    range: 512...32768,
                    valueText: "\(file.cacheMemory) MB")
                SliderRow(
                    title: "缓存数量上限",
                    value: vm.bindIntAsDouble("file.cacheCount") { vm.state.file.cacheCount },
                    range: 1...400,
                    valueText: "\(file.cacheCount) 张")
                Stepper(value: vm.bindInt("file.preloadForward") { vm.state.file.preloadForward },
                        in: 0...max(0, file.preloadMax)) {
                    HStack {
                        Text("下一张预载数量")
                        Spacer()
                        ValueText(text: "\(file.preloadForward) 张")
                    }
                }
                Stepper(value: vm.bindInt("file.preloadBackward") { vm.state.file.preloadBackward },
                        in: 0...max(0, file.preloadMax)) {
                    HStack {
                        Text("上一张预载数量")
                        Spacer()
                        ValueText(text: "\(file.preloadBackward) 张")
                    }
                }
                Stepper(value: vm.bindInt("file.preloadCenter") { vm.state.file.preloadCenter },
                        in: 0...max(0, file.preloadMax)) {
                    HStack {
                        Text("列表滚动预载数量")
                        Spacer()
                        ValueText(text: "\(file.preloadCenter) 张")
                    }
                }
                SliderRow(
                    title: "滚动预载延时",
                    value: vm.bindIntAsDouble("file.preloadDelay") { vm.state.file.preloadDelay },
                    range: 100...5000,
                    valueText: "\(file.preloadDelay) ms")
            } header: {
                Text("缓存与预加载")
            } footer: {
                VStack(alignment: .leading, spacing: 4) {
                    Text(file.budgetInfo).foregroundStyle(levelColor(file.budgetLevel))
                    Text(file.cacheCountInfo).foregroundStyle(levelColor(file.cacheCountLevel))
                }
                .font(.callout)
            }
        }
        .formStyle(.grouped)
    }
}

/// 拉条行：上方标题 + 右侧等宽数值，下方连续拉条。
struct SliderRow: View {
    let title: String
    let value: Binding<Double>
    let range: ClosedRange<Double>
    let valueText: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text(title)
                Spacer()
                ValueText(text: valueText)
            }
            Slider(value: value, in: range)
                .tint(.blue)
        }
        .padding(.vertical, 2)
    }
}

// MARK: - 预览分页

/// 预览分页：缩放指示器与缩放比例预设增删编辑。
struct PreviewSettingsPage: View {
    @ObservedObject var vm: SettingsBridge
    private var preview: SVPreviewState { vm.state.preview }

    var body: some View {
        Form {
            Section("缩放位置显示") {
                Toggle("缩放时显示指示器", isOn: vm.bindBool("preview.showZoom") { vm.state.preview.showZoomIndicator })
                    .toggleStyle(.switch)
            }
            Section("缩放比例预设") {
                ForEach(preview.presets, id: \.index) { preset in
                    HStack(spacing: 8) {
                        TextField("", text: Binding(
                            get: { vm.presetDrafts[preset.index] ?? preset.text },
                            set: { vm.presetDrafts[preset.index] = $0 }))
                            .textFieldStyle(.roundedBorder)
                            .frame(width: 90)
                            .multilineTextAlignment(.center)
                            .onSubmit {
                                vm.send(["type": "applyPreset", "index": preset.index,
                                         "text": vm.presetDrafts[preset.index] ?? preset.text])
                            }
                        Text("%").foregroundStyle(.secondary)
                        Text(preset.display).monospacedDigit().foregroundStyle(.secondary)
                        Spacer(minLength: 8)
                        Button {
                            vm.send(["type": "removePreset", "index": preset.index])
                        } label: {
                            Image(systemName: "minus.circle.fill").foregroundStyle(.red)
                        }
                        .buttonStyle(.plain)
                        .help("移除预设")
                    }
                    .labelsHidden()
                }
                HStack {
                    Spacer()
                    Button {
                        vm.send(["type": "addPreset"])
                    } label: {
                        Label("添加预设", systemImage: "plus.circle.fill")
                    }
                }
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - 控制分页

/// 控制分页：布局、控制栏功能/快捷键（含真实录制）、苹果键盘映射。
struct ControlSettingsPage: View {
    @ObservedObject var vm: SettingsBridge
    private var control: SVControlState { vm.state.control }

    var body: some View {
        Form {
            Section {
                Picker("布局模式", selection: vm.bindInt("control.layout") { vm.state.control.layoutIndex }) {
                    ForEach(Array(control.layoutModes.enumerated()), id: \.offset) { index, mode in
                        Text(mode.name).tag(index)
                    }
                }
            } header: {
                Text("控制栏布局位置")
            } footer: {
                if control.layoutIndex >= 0, control.layoutIndex < control.layoutModes.count {
                    Text(control.layoutModes[control.layoutIndex].description).font(.callout).foregroundStyle(.secondary)
                }
            }

            Section("控制栏功能与快捷键") {
                List {
                    ForEach(Array(control.hotkeys.enumerated()), id: \.element.name) { index, hotkey in
                        HotkeyRow(
                            vm: vm,
                            index: index,
                            hotkey: hotkey,
                            isEnabled: vm.bindBool("control.hotkey.display.\(index)") { vm.state.control.hotkeys[index].isDisplay })
                        .settingsListRow(SettingsMetrics.rowHeight)
                    }
                    .onMove { offsets, destination in
                        if let move = convertMoveOffset(from: offsets, to: destination) {
                            vm.send(["type": "moveHotkey", "from": move.from, "to": move.to])
                        }
                    }
                }
                .settingsReorderList(rows: control.hotkeys.count, rowHeight: SettingsMetrics.rowHeight)
            }

            Section("苹果键盘映射") {
                Toggle("使用苹果键盘", isOn: vm.bindBool("control.useAppleKeyboard") { vm.state.control.useAppleKeyboard })
                    .toggleStyle(.switch)
                if control.useAppleKeyboard {
                    MappingPicker(vm: vm, title: "⌘ Command", key: "control.mapCommand", selected: control.commandTarget, targets: control.mappingTargets)
                    MappingPicker(vm: vm, title: "⌥ Option", key: "control.mapOption", selected: control.optionTarget, targets: control.mappingTargets)
                    MappingPicker(vm: vm, title: "⌃ Control", key: "control.mapControl", selected: control.controlTarget, targets: control.mappingTargets)
                }
            }
        }
        .formStyle(.grouped)
    }
}

/// 单行快捷键：勾选、名称、主/次录制按钮、拖拽把手。
struct HotkeyRow: View {
    @ObservedObject var vm: SettingsBridge
    let index: Int
    let hotkey: SVHotkey
    let isEnabled: Binding<Bool>

    var body: some View {
        HStack(spacing: 8) {
            Toggle("", isOn: isEnabled).toggleStyle(.checkbox).labelsHidden()
            Text(hotkey.name)
            Spacer(minLength: 8)
            RecordButton(
                text: hotkey.primary,
                conflict: hotkey.primaryConflict,
                recording: hotkey.recording == "primary",
                tooltip: "点击录制；删除键清空，Escape 取消"
            ) {
                vm.send(["type": "beginRecord", "index": index, "intValue": 1])
            }
            RecordButton(
                text: hotkey.secondary,
                conflict: hotkey.secondaryConflict,
                recording: hotkey.recording == "secondary",
                tooltip: "点击录制；删除键清空，Escape 取消"
            ) {
                vm.send(["type": "beginRecord", "index": index, "intValue": 0])
            }
            DragHandle()
        }
    }
}

/// 快捷键录制按钮：显示当前手势文本、录制态或冲突橙色高亮。
struct RecordButton: View {
    let text: String
    let conflict: Bool
    let recording: Bool
    let tooltip: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(recording ? "按下按键…" : (text.isEmpty ? "未设置" : text))
                .font(.system(size: 11))
                .lineLimit(1)
                .frame(width: 108)
                .padding(.vertical, 3)
                .padding(.horizontal, 6)
                .background(RoundedRectangle(cornerRadius: 5).fill(Color(nsColor: .controlBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 5)
                    .stroke(conflict ? Color.orange : Color(nsColor: .separatorColor), lineWidth: 1))
                .foregroundStyle(conflict ? Color.orange : Color.primary)
        }
        .buttonStyle(.plain)
        .help(tooltip)
    }
}

/// 苹果键盘映射下拉行。
struct MappingPicker: View {
    @ObservedObject var vm: SettingsBridge
    let title: String
    let key: String
    let selected: Int
    let targets: [String]

    var body: some View {
        Picker(title, selection: vm.bindInt(key) { selectedForBinding }) {
            ForEach(Array(targets.enumerated()), id: \.offset) { index, target in
                Text(target).tag(index)
            }
        }
    }

    /// 读取当前动作对应字段的最新值，避免闭包捕获过期快照。
    private var selectedForBinding: Int {
        switch key {
        case "control.mapCommand": return vm.state.control.commandTarget
        case "control.mapOption": return vm.state.control.optionTarget
        default: return vm.state.control.controlTarget
        }
    }
}

// MARK: - EXIF 分页

/// EXIF 分页：显示项勾选排序、星级显示与安全写回。
struct ExifSettingsPage: View {
    @ObservedObject var vm: SettingsBridge
    private var exif: SVExifState { vm.state.exif }

    var body: some View {
        Form {
            Section("EXIF 显示/收纳") {
                List {
                    ForEach(Array(exif.items.enumerated()), id: \.element.name) { index, item in
                        CheckableRow(
                            enabled: vm.bindBool("exif.item.\(index)") { vm.state.exif.items[index].enabled },
                            title: item.name
                        ) { EmptyView() }
                        .settingsListRow(SettingsMetrics.rowHeight)
                    }
                    .onMove { offsets, destination in
                        if let move = convertMoveOffset(from: offsets, to: destination) {
                            vm.send(["type": "moveExif", "from": move.from, "to": move.to])
                        }
                    }
                }
                .settingsReorderList(rows: exif.items.count, rowHeight: SettingsMetrics.rowHeight)
            }
            Section("评分与写回") {
                Toggle("显示星级评分", isOn: vm.bindBool("exif.showRating") { vm.state.exif.showRating })
                    .toggleStyle(.switch)
                Toggle(isOn: vm.bindBool("exif.safeSetRating") { vm.state.exif.safeSetRating }) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("安全修改文件星级")
                        Text("检验文件字节变化再写回，降低损坏原文件概率")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.switch)
            }
        }
        .formStyle(.grouped)
    }
}

// MARK: - AI 分页

/// AI 分页：相似聚类阈值/数量、分析诊断与特征数据库清理。
struct AiSettingsPage: View {
    @ObservedObject var vm: SettingsBridge
    private var ai: SVAiState { vm.state.ai }
    @State private var confirmingClear = false

    var body: some View {
        Form {
            Section("相似聚类") {
                SliderRow(
                    title: "相似度阈值",
                    value: vm.bindIntAsDouble("ai.threshold") { vm.state.ai.similarityThreshold },
                    range: 75...95,
                    valueText: "\(ai.similarityThreshold)%")
                SliderRow(
                    title: "最多数量",
                    value: vm.bindIntAsDouble("ai.maxResults") { vm.state.ai.maxResults },
                    range: 1...32,
                    valueText: "\(ai.maxResults) 张")
            }
            Section("分析诊断") {
                Toggle("即时计算DINO/CV", isOn: vm.bindBool("ai.immediateCompute") { vm.state.ai.immediateCompute })
                    .toggleStyle(.switch)
            }
            Section("特征数据库") {
                HStack {
                    Text("清除已提取的 DINO / CV 特征（不可撤销）")
                    Spacer()
                    Button(ai.clearButtonText) { confirmingClear = true }
                        .disabled(ai.clearBusy)
                }
                if !ai.clearStatus.isEmpty {
                    Text(ai.clearStatus).font(.callout).foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.grouped)
        .confirmationDialog("清除特征数据库？", isPresented: $confirmingClear) {
            Button("清除", role: .destructive) { vm.send(["type": "clearDatabase"]) }
            Button("取消", role: .cancel) {}
        } message: {
            Text("已提取的 DINO/CV 特征将被清除，此操作不可撤销。")
        }
    }
}
