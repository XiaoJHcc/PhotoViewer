using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation;

namespace PhotoViewer.Mac.Native;

/// <summary>窗口状态快照 DTO，序列化为 camelCase JSON 交由 SwiftUI 侧解码渲染。</summary>
internal sealed class MacSettingsStateDto
{
    /// <summary>当前分类索引（0~4）。</summary>
    public int Category { get; set; }
    /// <summary>当前分类标题。</summary>
    public string Title { get; set; } = string.Empty;
    /// <summary>设置页顶部的一行持久化状态（读取失败/损坏备份/保存失败）；为空表示正常（SETTINGS-14）。</summary>
    public string PersistenceStatus { get; set; } = string.Empty;
    /// <summary>历史后退是否可用。</summary>
    public bool CanGoBack { get; set; }
    /// <summary>历史前进是否可用。</summary>
    public bool CanGoForward { get; set; }
    /// <summary>文件分页状态。</summary>
    public MacFileStateDto File { get; set; } = new();
    /// <summary>预览分页状态。</summary>
    public MacPreviewStateDto Preview { get; set; } = new();
    /// <summary>控制分页状态。</summary>
    public MacControlStateDto Control { get; set; } = new();
    /// <summary>EXIF 分页状态。</summary>
    public MacExifStateDto Exif { get; set; } = new();
    /// <summary>AI 分页状态。</summary>
    public MacAiStateDto Ai { get; set; } = new();
}

/// <summary>文件格式条目。</summary>
internal sealed class MacFormatDto
{
    /// <summary>格式显示名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>扩展名文本（含分隔符）。</summary>
    public string Extensions { get; set; } = string.Empty;
    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; }
}

/// <summary>文件分页状态。</summary>
internal sealed class MacFileStateDto
{
    /// <summary>同名文件视为同一张图片。</summary>
    public bool SameNameAsOnePhoto { get; set; }
    /// <summary>支持的文件格式列表（顺序即优先级）。</summary>
    public List<MacFormatDto> Formats { get; set; } = [];
    /// <summary>原生解码线程。</summary>
    public int NativeParallelism { get; set; }
    /// <summary>原生解码线程上限。</summary>
    public int NativeParallelismMax { get; set; }
    /// <summary>软件解码线程。</summary>
    public int CpuParallelism { get; set; }
    /// <summary>软件解码线程上限。</summary>
    public int CpuParallelismMax { get; set; }
    /// <summary>缓存内存上限（MB）。</summary>
    public int CacheMemory { get; set; }
    /// <summary>缓存数量上限（张）。</summary>
    public int CacheCount { get; set; }
    /// <summary>下一张预载数量。</summary>
    public int PreloadForward { get; set; }
    /// <summary>上一张预载数量。</summary>
    public int PreloadBackward { get; set; }
    /// <summary>列表滚动预载数量。</summary>
    public int PreloadCenter { get; set; }
    /// <summary>滚动预载延时（ms）。</summary>
    public int PreloadDelay { get; set; }
    /// <summary>预载数量上限。</summary>
    public int PreloadMax { get; set; }
    /// <summary>设备性能预算说明。</summary>
    public string BudgetInfo { get; set; } = string.Empty;
    /// <summary>性能预算说明的语义级别。</summary>
    public string BudgetLevel { get; set; } = "normal";
    /// <summary>缓存数量提示说明。</summary>
    public string CacheCountInfo { get; set; } = string.Empty;
    /// <summary>缓存数量提示的语义级别。</summary>
    public string CacheCountLevel { get; set; } = "normal";
}

/// <summary>缩放比例预设条目。</summary>
internal sealed class MacPresetDto
{
    public string Id { get; set; } = string.Empty;
    /// <summary>预设索引（重排后随之变化）。</summary>
    public int Index { get; set; }
    /// <summary>百分比文本。</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>比例换算展示文本。</summary>
    public string Display { get; set; } = string.Empty;
}

/// <summary>预览分页状态。</summary>
internal sealed class MacPreviewStateDto
{
    /// <summary>缩放时显示指示器。</summary>
    public bool ShowZoomIndicator { get; set; }
    /// <summary>缩放比例预设列表。</summary>
    public List<MacPresetDto> Presets { get; set; } = [];
}

/// <summary>控制栏功能（快捷键）条目。</summary>
internal sealed class MacHotkeyDto
{
    /// <summary>功能名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否在控制栏显示。</summary>
    public bool IsDisplay { get; set; }
    /// <summary>主快捷键显示文本。</summary>
    public string Primary { get; set; } = string.Empty;
    /// <summary>次快捷键显示文本。</summary>
    public string Secondary { get; set; } = string.Empty;
    /// <summary>主快捷键是否冲突。</summary>
    public bool PrimaryConflict { get; set; }
    /// <summary>次快捷键是否冲突。</summary>
    public bool SecondaryConflict { get; set; }
    /// <summary>录制态：空串 / "primary" / "secondary"。</summary>
    public string Recording { get; set; } = string.Empty;
}

/// <summary>布局模式条目。</summary>
internal sealed class MacLayoutModeDto
{
    /// <summary>显示名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>说明文本。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>控制分页状态。</summary>
internal sealed class MacControlStateDto
{
    /// <summary>布局模式列表。</summary>
    public List<MacLayoutModeDto> LayoutModes { get; set; } = [];
    /// <summary>当前布局模式索引。</summary>
    public int LayoutIndex { get; set; }
    /// <summary>控制栏功能列表（顺序即显示顺序）。</summary>
    public List<MacHotkeyDto> Hotkeys { get; set; } = [];
    /// <summary>是否使用苹果键盘映射。</summary>
    public bool UseAppleKeyboard { get; set; }
    /// <summary>映射目标选项文本。</summary>
    public List<string> MappingTargets { get; set; } = [];
    /// <summary>⌘ Command 映射目标索引。</summary>
    public int CommandTarget { get; set; }
    /// <summary>⌥ Option 映射目标索引。</summary>
    public int OptionTarget { get; set; }
    /// <summary>⌃ Control 映射目标索引。</summary>
    public int ControlTarget { get; set; }
    /// <summary>当前是否处于手势录制态。</summary>
    public bool RecordingActive { get; set; }
}

/// <summary>EXIF 显示条目。</summary>
internal sealed class MacExifItemDto
{
    /// <summary>字段显示名。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否显示。</summary>
    public bool Enabled { get; set; }
}

/// <summary>EXIF 分页状态。</summary>
internal sealed class MacExifStateDto
{
    /// <summary>EXIF 显示项列表（顺序即显示顺序）。</summary>
    public List<MacExifItemDto> Items { get; set; } = [];
    /// <summary>显示星级评分。</summary>
    public bool ShowRating { get; set; }
    /// <summary>安全修改文件星级。</summary>
    public bool SafeSetRating { get; set; }
}

/// <summary>AI 分页状态。</summary>
internal sealed class MacAiStateDto
{
    /// <summary>相似度阈值（百分数）。</summary>
    public int SimilarityThreshold { get; set; }
    /// <summary>最多数量。</summary>
    public int MaxResults { get; set; }
    /// <summary>即时计算 DINO/CV。</summary>
    public bool ImmediateCompute { get; set; }
    /// <summary>清库按钮当前文案。</summary>
    public string ClearButtonText { get; set; } = string.Empty;
    /// <summary>清库状态文字。</summary>
    public string ClearStatus { get; set; } = string.Empty;
    /// <summary>清库是否进行中。</summary>
    public bool ClearBusy { get; set; }
}

/// <summary>SwiftUI 回传的交互动作 DTO。</summary>
internal sealed class MacSettingsActionDto
{
    /// <summary>动作类型。</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>标量设置键（setBool / setInt 使用）。</summary>
    public string? Key { get; set; }
    /// <summary>文本载荷（applyPreset 使用）。</summary>
    public string? Text { get; set; }
    public string? PresetId { get; set; }
    /// <summary>录制事件字符载荷。</summary>
    public string? Characters { get; set; }
    /// <summary>布尔值。</summary>
    public bool BoolValue { get; set; }
    /// <summary>整数值。</summary>
    public int IntValue { get; set; }
    /// <summary>列表/预设索引。</summary>
    public int Index { get; set; }
    /// <summary>移动源索引。</summary>
    public int From { get; set; }
    /// <summary>移动目标索引。</summary>
    public int To { get; set; }
    /// <summary>历史导航方向。</summary>
    public bool Forward { get; set; }
    /// <summary>原生命令事件类型。</summary>
    public int EventType { get; set; }
    /// <summary>原生键码。</summary>
    public int KeyCode { get; set; }
    /// <summary>原生修饰键掩码。</summary>
    public ulong Modifiers { get; set; }
    /// <summary>鼠标按键编号。</summary>
    public int ButtonNumber { get; set; }
    /// <summary>滚轮纵向增量。</summary>
    public double ScrollingDeltaY { get; set; }
    /// <summary>是否为精确滚轮事件。</summary>
    public bool Precise { get; set; }
}

/// <summary>设置桥接 JSON 契约的 source-generated 上下文，兼容裁剪发布。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MacSettingsStateDto))]
[JsonSerializable(typeof(MacSettingsActionDto))]
internal partial class MacSettingsJsonContext : JsonSerializerContext;

/// <summary>libMacSettingsUI.dylib 的 C ABI 绑定：动态加载导出函数并提供强类型封装。
/// 每个导出入口都先 <see cref="EnsureLoaded"/>：动态库未载入时函数指针仍为 null，
/// 直接调用会跳到地址 0 崩溃（退出时关闭从未打开过的设置窗口曾命中）。</summary>
internal static unsafe class MacSettingsNative
{
    private const string LibraryName = "libMacSettingsUI.dylib";

    private static nint _library;
    private static delegate* unmanaged[Cdecl]<nint, void> _show;
    private static delegate* unmanaged[Cdecl]<nint, void> _updateState;
    private static delegate* unmanaged[Cdecl]<nint, void> _setActionHandler;
    private static delegate* unmanaged[Cdecl]<nint, void> _setCloseHandler;
    private static delegate* unmanaged[Cdecl]<void> _close;
    private static delegate* unmanaged[Cdecl]<int> _windowNumber;
    private static delegate* unmanaged[Cdecl]<int> _isDark;
    private static delegate* unmanaged[Cdecl]<int> _hasHostingController;
    private static delegate* unmanaged[Cdecl]<double, double, void> _setContentSize;
    private static delegate* unmanaged[Cdecl]<int, int, void> _beginRecording;
    private static delegate* unmanaged[Cdecl]<nint, void> _render;

    /// <summary>SwiftUI 回传动作的处理入口，由设置展示器在展示时挂接。</summary>
    internal static Func<string, int>? ActionSink { get; set; }
    /// <summary>窗口关闭回调入口，由设置展示器在展示时挂接。</summary>
    internal static Action? CloseSink { get; set; }

    /// <summary>首次调用时从 app bundle 的 MonoBundle 载入 SwiftUI 动态库。</summary>
    internal static void EnsureLoaded()
    {
        if (_library != 0) return;
        string monoBundle = System.IO.Path.Combine(NSBundle.MainBundle.BundlePath, "Contents", "MonoBundle", LibraryName);
        string path = File.Exists(monoBundle) ? monoBundle : System.IO.Path.Combine(AppContext.BaseDirectory, LibraryName);
        if (!File.Exists(path)) throw new InvalidOperationException($"原生设置动态库缺失：{path}");
        _library = NativeLibrary.Load(path);
        _show = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "pv_settings_show");
        _updateState = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "pv_settings_update_state");
        _setActionHandler = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "pv_settings_set_action_handler");
        _setCloseHandler = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "pv_settings_set_close_handler");
        _close = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(_library, "pv_settings_close");
        _windowNumber = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_library, "pv_settings_window_number");
        _isDark = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_library, "pv_settings_is_dark");
        _hasHostingController = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_library, "pv_settings_has_hosting_controller");
        _setContentSize = (delegate* unmanaged[Cdecl]<double, double, void>)NativeLibrary.GetExport(_library, "pv_settings_set_content_size");
        _beginRecording = (delegate* unmanaged[Cdecl]<int, int, void>)NativeLibrary.GetExport(_library, "pv_settings_begin_recording");
        _render = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "pv_settings_render");
        _setActionHandler((nint)(delegate* unmanaged[Cdecl]<nint, int>)&HandleAction);
        _setCloseHandler((nint)(delegate* unmanaged[Cdecl]<void>)&HandleClose);
    }

    /// <summary>展示（或复用）设置窗口并载入状态 JSON。</summary>
    internal static void Show(string json) { EnsureLoaded(); InvokeUtf8(json, _show); }

    /// <summary>用最新状态 JSON 刷新窗口。</summary>
    internal static void UpdateState(string json) { EnsureLoaded(); InvokeUtf8(json, _updateState); }

    /// <summary>关闭设置窗口。</summary>
    internal static void Close() { EnsureLoaded(); _close(); }

    /// <summary>窗口号，供宿主截图取证。</summary>
    internal static int WindowNumber() { EnsureLoaded(); return _windowNumber(); }

    /// <summary>窗口是否为深色外观。</summary>
    internal static bool IsDark() { EnsureLoaded(); return _isDark() != 0; }

    /// <summary>内容控制器是否已挂载 SwiftUI 宿主视图。</summary>
    internal static bool HasHostingController() { EnsureLoaded(); return _hasHostingController() != 0; }

    /// <summary>调整窗口内容尺寸。</summary>
    internal static void SetContentSize(double width, double height) { EnsureLoaded(); _setContentSize(width, height); }

    /// <summary>进入快捷键录制态。</summary>
    internal static void BeginRecording(int index, bool primary) { EnsureLoaded(); _beginRecording(index, primary ? 1 : 0); }

    /// <summary>把窗口内容导出为 PNG。</summary>
    internal static void Render(string path) { EnsureLoaded(); InvokeUtf8(path, _render); }

    /// <summary>把托管字符串以 UTF-8 传入 C 函数。</summary>
    private static void InvokeUtf8(string value, delegate* unmanaged[Cdecl]<nint, void> function)
    {
        IntPtr pointer = Marshal.StringToCoTaskMemUTF8(value);
        try { function(pointer); }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    /// <summary>SwiftUI 动作回传入口：转交处理并返回消费标记。</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HandleAction(nint utf8Json)
    {
        try { return ActionSink?.Invoke(Marshal.PtrToStringUTF8(utf8Json) ?? string.Empty) ?? 0; }
        catch (Exception exception)
        {
            Console.Error.WriteLine("[MacSettingsUI] action failed: " + exception);
            return 0;
        }
    }

    /// <summary>窗口关闭回传入口。</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleClose()
    {
        try { CloseSink?.Invoke(); }
        catch (Exception exception) { Console.Error.WriteLine("[MacSettingsUI] close failed: " + exception); }
    }
}
