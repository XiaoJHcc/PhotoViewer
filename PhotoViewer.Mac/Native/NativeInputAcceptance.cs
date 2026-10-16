using AppKit;
using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Storage;
using PhotoViewer.Mac.Core;

namespace PhotoViewer.Mac.Native;

/// <summary>主图区域输入回归，仅在隔离冒烟会话中以 --pv-native-smoke --pv-native-input 运行（不加载照片）。
/// 覆盖：NSEvent 必须按类型取值（曾一次性读取全部字段，对鼠标/键盘事件抛 NSInternalInconsistencyException
/// 被 ObjC 桥吞掉，导致主图右键菜单、空首屏点击乃至全部快捷键失效）、空首屏左键打开文件、主图右键弹出原生上下文菜单、
/// 顶边拖动区双击按系统偏好缩放/最小化。
/// 点击直接投递到主图视图（与胶片栏回归一致），因为冒烟进程非最前台窗口时系统不会派发首击。</summary>
internal static class NativeInputAcceptance
{
    /// <summary>执行输入回归并写出检查清单。</summary>
    internal static async Task RunAsync(MacMainWindow window, MacNativeSettingsPresenter settings, string output)
    {
        var checks = new List<string>();
        await Task.Delay(400);
        var photo = window.Photo;
        if (window.Session.Image != null) throw new InvalidOperationException("输入回归要求未加载照片的空窗口");

        // 1) 各事件类型只读取对应字段并映射到正确手势（回归根因）。
        VerifyGestureMapping(checks);

        // 2) 空首屏结构：提示可见、主图铺满展示区、提示不拦截点击。
        //    点击点取主图中心，正好落在提示文字上，故同时验证提示标签的点击透传。
        var pane = photo.Superview ?? throw new InvalidOperationException("主图缺少父视图");
        var hint = pane.Subviews.OfType<NSTextField>().FirstOrDefault(field => field.StringValue.Contains("点击打开文件"))
            ?? throw new InvalidOperationException("空首屏缺少“点击打开文件”提示");
        if (hint.Hidden) throw new InvalidOperationException("空首屏提示被隐藏");
        if (photo.Frame != pane.Bounds) throw new InvalidOperationException($"主图未铺满展示区域：photo={photo.Frame} pane={pane.Bounds}");
        var center = new CGPoint(photo.Bounds.Width / 2, photo.Bounds.Height / 2);
        if (!hint.Frame.Contains(center)) throw new InvalidOperationException("空首屏提示未覆盖主图中心，回归点选取无效");
        if (hint.HitTest(center) != null) throw new InvalidOperationException("空首屏提示未透传点击");
        checks.Add("empty state: hint visible over photo centre and passes clicks through to full-bleed photo view");

        // 3) 键盘路径：默认绑定的方向键必须被主窗口消费（同一根因也曾使全部快捷键失效）。
        var location = photo.ConvertPointToView(center, null);
        using (var next = NSEvent.KeyEvent(NSEventType.KeyDown, location, 0, NSProcessInfo.ProcessInfo.SystemUptime,
                   window.Window.WindowNumber, null!, "\u001c", "\u001c", false, 124))
        {
            if (photo.InputHandler?.Invoke(next!) != true) throw new InvalidOperationException("默认绑定的下一页方向键未被主窗口消费");
        }
        checks.Add("keyboard: bound arrow key reaches shared command table through photo input handler");

        // 4) 空首屏左键打开文件选择器（用桩实现避免模态面板阻塞）。
        var stub = new RecordingFilePicker();
        FilePicker.Initialize(stub);
        try
        {
            using var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, location, 0, NSProcessInfo.ProcessInfo.SystemUptime,
                window.Window.WindowNumber, null!, 1, 1, 1) ?? throw new InvalidOperationException("左键事件创建失败");
            photo.MouseDown(click);
            for (int attempt = 0; attempt < 40 && !stub.FileRequested; attempt++) await Task.Delay(25);
            if (!stub.FileRequested) throw new InvalidOperationException("空首屏左键未打开文件选择器");
            checks.Add("empty state: left click opens file picker");
        }
        finally { FilePicker.Initialize(new MacFilePicker()); }

        // 5) 主图右键弹出原生上下文菜单（放行到 base.RightMouseDown 的路径）。
        VerifyContextMenu(window, photo, location, checks);

        // 6) 隐藏式标题栏(FullSizeContentView)不会自带系统“双击标题栏”动作，验证顶边拖动区自实现。
        await VerifyTitlebarDoubleClick(window, checks);

        // 7) 退出路径：从未展示过设置窗口时关闭设置必须是空操作，不得去调尚未载入的设置动态库
        //    （退出时 WillTerminate 会调用它；修复前函数指针为 null，直接跳到地址 0 SIGSEGV）。
        if (settings.IsPresented) throw new InvalidOperationException("输入回归不应先打开设置窗口");
        settings.CloseWindow();
        checks.Add("shutdown: closing a never-presented settings window is a no-op and does not touch the unloaded settings dylib");

        await File.WriteAllLinesAsync(Path.Combine(output, "native-input-checks.txt"), checks);
        Console.WriteLine("[NativeSmoke] PASS input");
    }

    /// <summary>标题栏双击回归：顶边拖动区内的左键双击经窗口级监视器按系统偏好 AppleActionOnDoubleClick
    /// 分派缩放/最小化/不动作，区外双击不得被接管。
    /// 断言语义 —— 默认(未设或 Maximize)双击把非标准帧放大到标准帧；None 完全不动；Minimize 最小化并可还原；
    /// 主图中心双击既不被消费也不动窗口（主图自己的双击是缩放适应）。
    /// 只断言“放大”方向：AppKit 的“用户帧”由用户手动改尺寸记录，程序化设帧不会写回，故反向还原不可判定。
    /// 结束后删除应用域覆盖并还原窗口，不影响用户系统偏好与当前窗口几何。</summary>
    private static async Task VerifyTitlebarDoubleClick(MacMainWindow window, List<string> checks)
    {
        var preferences = NSUserDefaults.StandardUserDefaults;
        const string key = MacMainWindow.TitlebarDoubleClickPreference;
        var zone = window.DragZones[0];
        if (zone.Frame.Width < 1 || zone.Frame.Height < 1) throw new InvalidOperationException($"顶边拖动区未布局，双击无法命中：{zone.Frame}");
        var original = window.Window.Frame;
        try
        {
            window.Window.SetFrame(new CGRect(original.X, original.Y,
                Math.Max(window.Window.MinSize.Width, original.Width - 200),
                Math.Max(window.Window.MinSize.Height, original.Height - 200)), true);
            await Task.Delay(80);
            if (window.Window.IsZoomed) throw new InvalidOperationException("回归用窗口帧仍处于标准(缩放)尺寸，无法判定双击缩放");

            // 命中区内之前，先确认区外(主图中心)的双击不会被标题栏接管。
            var photoCentre = window.Photo.ConvertPointToView(new CGPoint(window.Photo.Bounds.Width / 2, window.Photo.Bounds.Height / 2), null);
            if (DoubleClick(window, photoCentre)) throw new InvalidOperationException("主图中心的双击被标题栏接管，会吞掉主图自身的双击缩放");

            if (preferences.StringForKey(key) is null or "Maximize")
            {
                if (!DoubleClick(window, ZonePoint(zone))) throw new InvalidOperationException("顶边拖动区内的双击未被标题栏接管");
                if (!await WaitForAsync(() => window.Window.IsZoomed)) throw new InvalidOperationException("未设置/最大化偏好下双击未把窗口放大到标准尺寸");
                checks.Add("titlebar: double click inside the top drag zone zooms to the standard frame (system default / Maximize)");
                window.Window.SetFrame(original, true);
                await Task.Delay(80);
            }

            preferences.SetString("None", key);
            var frozen = window.Window.Frame;
            if (!DoubleClick(window, ZonePoint(zone))) throw new InvalidOperationException("偏好 None 时顶边拖动区内的双击未被标题栏接管");
            await Task.Delay(120);
            if (window.Window.Frame != frozen || window.Window.IsMiniaturized) throw new InvalidOperationException("偏好 None 时双击仍改变了窗口状态");
            checks.Add("titlebar: AppleActionOnDoubleClick=None leaves the window untouched");

            preferences.SetString("Minimize", key);
            if (!DoubleClick(window, ZonePoint(zone))) throw new InvalidOperationException("偏好 Minimize 时顶边拖动区内的双击未被标题栏接管");
            if (!await WaitForAsync(() => window.Window.IsMiniaturized)) throw new InvalidOperationException("偏好 Minimize 时双击未最小化窗口");
            window.Window.Deminiaturize(null);
            if (!await WaitForAsync(() => !window.Window.IsMiniaturized)) throw new InvalidOperationException("最小化后无法还原窗口");
            checks.Add("titlebar: AppleActionOnDoubleClick=Minimize minimises the window");
            checks.Add("titlebar: double click outside the drag zone is left to the view under the cursor");
        }
        finally
        {
            // 原生壳不写该键(读的是系统全局域)，删掉应用域覆盖即恢复用户设置。
            preferences.RemoveObject(key);
            if (window.Window.IsMiniaturized) window.Window.Deminiaturize(null);
            window.Window.SetFrame(original, true);
        }
    }

    /// <summary>拖动区内一点的窗口坐标（取区内左上角内 1 点，避开边界判定）。</summary>
    private static CGPoint ZonePoint(NSView zone) => zone.ConvertPointToView(new CGPoint(1, 1), null);

    /// <summary>合成一次真实左键双击交给窗口级监视器入口，返回是否被标题栏接管。</summary>
    private static bool DoubleClick(MacMainWindow window, CGPoint windowPoint)
    {
        using var click = NSEvent.MouseEvent(NSEventType.LeftMouseDown, windowPoint, 0, NSProcessInfo.ProcessInfo.SystemUptime,
            window.Window.WindowNumber, null!, 2, 2, 1) ?? throw new InvalidOperationException("双击事件创建失败");
        return window.HandleTitlebarDoubleClick(click);
    }

    /// <summary>轮询等待窗口状态生效，超时返回 false。</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>逐类型构造真实 NSEvent 并核对 FromEvent 不抛异常且映射到预期手势。</summary>
    private static void VerifyGestureMapping(List<string> checks)
    {
        void Expect(string label, NSEvent? nativeEvent, Func<AppGesture, bool>? matches)
        {
            if (nativeEvent == null) throw new InvalidOperationException($"{label} 事件创建失败");
            using (nativeEvent)
            {
                AppGesture? gesture;
                try { gesture = MacInput.FromEvent(nativeEvent); }
                catch (Exception exception) { throw new InvalidOperationException($"{label} FromEvent 抛异常：{exception.Message}"); }
                bool matched = matches == null ? gesture == null : gesture != null && matches(gesture);
                if (!matched) throw new InvalidOperationException($"{label} 未映射到预期手势");
            }
        }

        var location = new CGPoint(100, 100);
        double uptime = NSProcessInfo.ProcessInfo.SystemUptime;
        Expect("LeftMouseDown", NSEvent.MouseEvent(NSEventType.LeftMouseDown, location, 0, uptime, 0, null!, 1, 1, 1),
            gesture => gesture.Mouse is { Action: MouseAction.LeftClick });
        Expect("RightMouseDown", NSEvent.MouseEvent(NSEventType.RightMouseDown, location, 0, uptime, 0, null!, 1, 1, 1),
            gesture => gesture.Mouse is { Action: MouseAction.LeftClick or MouseAction.RightClick });
        Expect("OtherMouseDown", NSEvent.MouseEvent(NSEventType.OtherMouseDown, location, 0, uptime, 0, null!, 1, 1, 1),
            gesture => gesture.Mouse != null);
        Expect("KeyDown 'n'", NSEvent.KeyEvent(NSEventType.KeyDown, location, 0, uptime, 0, null!, "n", "n", false, 45),
            gesture => gesture.Key is { Key: GestureKey.N });
        Expect("KeyDown ←", NSEvent.KeyEvent(NSEventType.KeyDown, location, 0, uptime, 0, null!, "\u001c", "\u001c", false, 123),
            gesture => gesture.Key is { Key: GestureKey.Left });
        Expect("KeyUp", NSEvent.KeyEvent(NSEventType.KeyUp, location, 0, uptime, 0, null!, "n", "n", false, 45), null);
        checks.Add("gesture mapping: mouse / key / key-up events read type-specific fields without throwing");
    }

    /// <summary>在真实主图上触发右键，用菜单代理捕获 MenuWillOpen，超时取消跟踪避免阻塞。</summary>
    private static void VerifyContextMenu(MacMainWindow window, MetalPhotoView photo, CGPoint location, List<string> checks)
    {
        var menu = photo.Menu ?? throw new InvalidOperationException("主图未设置上下文菜单");
        var probe = new MenuOpenProbe();
        menu.Delegate = probe;
        try
        {
            using var timer = NSTimer.CreateTimer(0.6, _ => menu.CancelTracking());
            NSRunLoop.Main.AddTimer(timer, NSRunLoopMode.Common);
            using var right = NSEvent.MouseEvent(NSEventType.RightMouseDown, location, 0, NSProcessInfo.ProcessInfo.SystemUptime,
                window.Window.WindowNumber, null!, 1, 1, 1) ?? throw new InvalidOperationException("右键事件创建失败");
            photo.RightMouseDown(right);
        }
        finally { menu.Delegate = null; }
        if (!probe.Opened) throw new InvalidOperationException("主图右键未弹出原生上下文菜单");
        checks.Add("photo area: right click opens native context menu");
    }

    /// <summary>记录菜单打开事件。</summary>
    private sealed class MenuOpenProbe : NSObject, INSMenuDelegate
    {
        public bool Opened;
        public void MenuWillOpen(NSMenu menu) => Opened = true;
    }

    /// <summary>记录选择器调用而不弹出模态面板。</summary>
    private sealed class RecordingFilePicker : IFilePickerService
    {
        public bool FileRequested;
        public Task<IFolderRef?> PickFolderAsync(string title) => Task.FromResult<IFolderRef?>(null);
        public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
        {
            FileRequested = true;
            return Task.FromResult<IFileRef?>(null);
        }
    }
}
