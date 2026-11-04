import SwiftUI

#if os(iOS)
import UIKit
#else
import AppKit
#endif

@main
struct PhotoViewerUIDemoApp: App {
    @StateObject private var workspace = DemoWorkspace()

    var body: some Scene {
        WindowGroup {
            MainDemoView()
                .environmentObject(workspace)
                .preferredColorScheme(.dark)
                #if os(iOS)
                .statusBarHidden(true)
                #endif
        }
        #if os(macOS)
        .windowStyle(.automatic)
        .windowToolbarStyle(.unified)
        .defaultSize(width: 1200, height: 850)
        #endif
    }
}

struct MainDemoView: View {
    var body: some View {
        #if os(iOS)
        if UIDevice.current.userInterfaceIdiom == .phone {
            CompactWorkspaceView()
        } else {
            WideWorkspaceView()
        }
        #else
        WideWorkspaceView()
            .frame(minWidth: 760, minHeight: 540)
            .background(SidebarWindowConfiguration().frame(width: 0, height: 0))
        #endif
    }
}

#if os(macOS)
private struct SidebarWindowConfiguration: NSViewRepresentable {
    func makeNSView(context: Context) -> SidebarWindowView {
        SidebarWindowView()
    }

    func updateNSView(_ view: SidebarWindowView, context: Context) {}
}

private final class SidebarWindowView: NSView {
    private weak var configuredWindow: NSWindow?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let window, configuredWindow !== window else { return }
        configuredWindow = window
        window.appearance = NSAppearance(named: .darkAqua)
        window.titlebarAppearsTransparent = true
        window.toolbarStyle = .unified
        window.styleMask.insert(.fullSizeContentView)
        let toolbar = NSToolbar(identifier: "PhotoViewer.UIPrototype")
        toolbar.displayMode = .iconOnly
        window.toolbar = toolbar
    }
}
#endif
