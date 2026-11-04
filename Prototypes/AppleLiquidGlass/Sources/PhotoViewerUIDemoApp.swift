import SwiftUI

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
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1200, height: 850)
        #endif
    }
}

struct MainDemoView: View {
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    @Environment(\.verticalSizeClass) private var verticalSizeClass

    var body: some View {
        #if os(iOS)
        if horizontalSizeClass == .compact || verticalSizeClass == .compact {
            CompactWorkspaceView()
        } else {
            WideWorkspaceView()
        }
        #else
        DesktopWorkspaceView()
            .frame(minWidth: 760, minHeight: 540)
        #endif
    }
}
