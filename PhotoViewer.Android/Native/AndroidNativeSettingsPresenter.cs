using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.SwitchMaterial;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Input;
using PhotoViewer.Core.Settings;
using PhotoViewer.ViewModels.Settings;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生设置展示器（A4）：Material 3 半屏 <see cref="BottomSheetDialog"/>，五个分类与 iOS/Windows
/// 同口径（文件 / 预览 / 控制 / EXIF / AI）。编辑走共享 <see cref="NativeSettingsSession"/> 工作副本，
/// 关闭（关闭按钮、下滑、返回）统一回写一次。
/// </summary>
public sealed class AndroidNativeSettingsPresenter : PhotoViewer.Core.Settings.INativeSettingsPresenter
{
    private readonly Activity _activity;
    private BottomSheetDialog? _dialog;
    private static AndroidSettingsSheetView? _current;
    private static BottomSheetDialog? _currentDialog;

    /// <summary>绑定宿主 Activity。</summary>
    /// <param name="activity">原生壳 Activity。</param>
    public AndroidNativeSettingsPresenter(Activity activity) => _activity = activity;

    /// <summary>当前是否已有打开中的设置 sheet。</summary>
    public static bool IsOpen => _current != null;

    /// <summary>当前设置 sheet（验收读取页面与绑定控件状态）。</summary>
    internal static AndroidSettingsSheetView? CurrentSheet => _current;

    /// <summary>当前设置对话框（验收向真实窗口派发触摸并读取 sheet 行为）。</summary>
    internal static BottomSheetDialog? CurrentDialog => _currentDialog;

    /// <summary>返回键优先关闭设置 sheet；返回是否消费。</summary>
    /// <returns>设置页存在并已处理时 true。</returns>
    public static bool HandleBack()
    {
        var current = _current;
        if (current == null) return false;
        if (current.HandleBack()) return true;
        current.RequestDismiss();
        return true;
    }

    /// <summary>展示原生设置页；已展示时直接返回。</summary>
    /// <param name="settings">共享设置 ViewModel。</param>
    /// <returns>成功接管时 true。</returns>
    public bool TryPresent(SettingsViewModel settings)
    {
        if (_dialog?.IsShowing == true) return true;
        try
        {
            var session = new NativeSettingsSession(settings);
            var content = new AndroidSettingsSheetView(_activity, session, Dismiss);
            // 对话框窗口统一接收录制态键盘/滚轮/鼠标按键，避免控件抢走手势。
            var dialog = new SettingsBottomSheetDialog(_activity,
                Resource.Style.Theme_PhotoViewer_BottomSheet, content);
            var metrics = _activity.Resources?.DisplayMetrics;
            int screenHeight = metrics?.HeightPixels ?? 1920;
            dialog.SetContentView(content, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, AndroidSheetPresentation.ContentHeight(screenHeight)));
            int applyStarted = 0;
            dialog.DismissEvent += async (_, _) =>
            {
                if (ReferenceEquals(_current, content)) _current = null;
                if (ReferenceEquals(_currentDialog, dialog)) _currentDialog = null;
                content.CommitPendingEdits();
                // 关闭按钮、下滑与返回可能重复触发 DismissEvent：只提交一次，并且等提交完成后再释放会话。
                if (Interlocked.Exchange(ref applyStarted, 1) != 0) return;
                try
                {
                    if (!await session.ApplyAsync())
                        Console.Error.WriteLine("[Native] 设置提交未成功（仓库不可写或写入失败）");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Native] 设置提交失败：{exception}");
                }
                finally
                {
                    session.Dispose();
                    // 解除外设监听，避免已关闭的 sheet 被静态事件保活。
                    content.Dispose();
                }
            };
            // SETTINGS-04：Show 之前配置初始收起的半屏位置，避免先按内容高度展开再闪回收起。
            AndroidSheetPresentation.ConfigureInitialCollapsed(content, screenHeight);
            dialog.Show();
            _dialog = dialog;
            _current = content;
            _currentDialog = dialog;
            Console.WriteLine("[Native] 设置 BottomSheet 已打开（工作副本，关闭回写）");
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] 设置 BottomSheet 打开失败：{exception}");
            return false;
        }
    }

    /// <summary>关闭设置页（关闭按钮与返回共用）。</summary>
    private void Dismiss()
    {
        var dialog = _dialog;
        _dialog = null;
        dialog?.Dismiss();
    }
}

/// <summary>
/// 设置 BottomSheet 窗口：录制态下先接收键盘、滚轮与鼠标中/侧键，再交回系统，
/// 保证 INPUT-08 的“录制期间吞掉滚轮与中键”不依赖具体控件。
/// </summary>
internal sealed class SettingsBottomSheetDialog : BottomSheetDialog
{
    private readonly AndroidSettingsSheetView _sheet;

    internal SettingsBottomSheetDialog(Context context, int theme, AndroidSettingsSheetView sheet)
        : base(context, theme) => _sheet = sheet;

    /// <summary>录制态消费按键；否则按对话框默认路径分发。</summary>
    /// <param name="e">按键事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && _sheet.HandleRecordingKey(e)) return true;
        return base.DispatchKeyEvent(e!);
    }

    /// <summary>录制态吞掉鼠标左/右/中/侧键的触摸事件，避免误触下面的按钮。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e != null && _sheet.HandleRecordingMouseTouch(e)) return true;
        return base.DispatchTouchEvent(e!);
    }

    /// <summary>录制态消费滚轮与鼠标中/侧键；否则按对话框默认路径分发。</summary>
    /// <param name="e">通用运动事件。</param>
    /// <returns>是否消费。</returns>
    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e != null)
        {
            if (e.ActionMasked == MotionEventActions.Scroll
                && _sheet.HandleRecordingWheel(e.GetAxisValue(Axis.Vscroll), (int)e.MetaState))
                return true;
            if (e.ActionMasked == MotionEventActions.ButtonPress
                && _sheet.HandleRecordingButton(e.ActionButton, (int)e.MetaState))
                return true;
        }
        return base.DispatchGenericMotionEvent(e!);
    }
}

/// <summary>
/// 设置 BottomSheet 的视图树：顶部把手 + 标题栏 + 可滚动内容；根页为五分类入口，子页在同一 sheet 内切换。
/// 所有控件颜色/圆角显式写出，不依赖 OEM 主题派生属性。
/// </summary>
internal sealed class AndroidSettingsSheetView : FrameLayout
{
    private readonly Activity _activity;
    private readonly NativeSettingsSession _session;
    private readonly Action _dismiss;
    private readonly LinearLayout _content;
    private readonly AndroidSheetScrollView _scroll;
    private readonly AndroidSheetViewport _viewport;
    private readonly TextView _title;
    private readonly TextView _back;
    private readonly LinearLayout _header;
    private readonly List<(TextView Label, Func<string> Text, Func<PerfInfoLevel>? Level)> _dynamicTexts = [];
    private readonly List<TextView> _fileInfoLabels = [];
    private bool _refreshingDynamicTexts;
    private string _page = "root";
    private bool _closed;
    private AndroidSettingsReorderList? _hotkeyList;
    private AndroidSettingsReorderList? _formatList;
    private AndroidSettingsReorderList? _exifList;
    private SettingsViewModel.HotkeyItem? _recordingItem;
    private bool _recordingPrimary;
    private LinearLayout? _scaleCard;
    private View? _scaleAddRow;
    private View? _previewSwitchSection;
    private View? _layoutSection;
    private readonly Dictionary<SettingsViewModel.ScalePreset, ScaleRowView> _scaleRows = [];
    private bool _committingScalePresets;

    /// <summary>当前工作设置副本（验收读取关闭回写后的值）。</summary>
    internal SettingsViewModel Settings => _session.WorkingSettings;
    /// <summary>当前页面标识（root/files/preview/control/exif/ai）。</summary>
    internal string Page => _page;
    /// <summary>当前是否处于快捷键录制态。</summary>
    internal bool IsRecording => _recordingItem != null;
    /// <summary>键鼠录制控件是否可见（外设能力）。</summary>
    internal bool BindingControlsAvailable => AndroidInputDevices.HasBindingDevice;

    /// <summary>建立 sheet 视图树。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="session">设置工作副本会话。</param>
    /// <param name="dismiss">关闭回调。</param>
    public AndroidSettingsSheetView(Activity activity, NativeSettingsSession session, Action dismiss) : base(activity)
    {
        _activity = activity;
        _session = session;
        _dismiss = dismiss;
        double density = AndroidUiTheme.Density(activity);
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#1A1A1A"));
        background.SetCornerRadii([24 * (float)density, 24 * (float)density, 24 * (float)density, 24 * (float)density, 0, 0, 0, 0]);
        Background = background;

        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        AddView(root, new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));

        var handle = new View(activity);
        var handleBackground = new GradientDrawable();
        handleBackground.SetColor(Color.ParseColor("#666666"));
        handleBackground.SetCornerRadius(2 * (float)density);
        handle.Background = handleBackground;
        // 把手与标题栏是 sheet 的拖动关闭载体：消费按下，让 BottomSheetBehavior 走标准拦截路径；
        // 内容区由 AndroidSheetScrollView 隔断，两者互不影响。
        handle.Clickable = true;
        handle.ImportantForAccessibility = ImportantForAccessibility.No;
        var handleParams = new LinearLayout.LayoutParams((int)(36 * density), (int)(4 * density))
        {
            Gravity = GravityFlags.CenterHorizontal,
            TopMargin = (int)(8 * density),
            BottomMargin = (int)(4 * density),
        };
        root.AddView(handle, handleParams);

        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding((int)(12 * density), 0, (int)(12 * density), 0);
        header.Clickable = true;
        header.ImportantForAccessibility = ImportantForAccessibility.No;
        _header = header;
        _back = Button("‹", 26, () => { if (!GoBack()) Dismiss(); });
        _back.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 26);
        _back.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);
        _back.Visibility = ViewStates.Invisible;
        _title = Text("设置", 17, AndroidUiTheme.TextPrimary, bold: true);
        _title.Gravity = GravityFlags.Center;
        var close = Button("✕", 20, Dismiss);
        header.AddView(_back, new LinearLayout.LayoutParams((int)(44 * density), (int)(48 * density)));
        header.AddView(_title, new LinearLayout.LayoutParams(0, (int)(48 * density), 1));
        header.AddView(close, new LinearLayout.LayoutParams((int)(44 * density), (int)(48 * density)));
        root.AddView(header, new LinearLayout.LayoutParams(LayoutParams.MatchParent, (int)(48 * density)));

        // SETTINGS-04：内容区手势只滚动内容，不能把 sheet 拖下或收起；拖动关闭只由把手/标题承担。
        var scroll = new AndroidSheetScrollView(activity);
        scroll.SetBackgroundColor(Color.Transparent);
        _scroll = scroll;
        _content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _content.SetPadding((int)(16 * density), (int)(4 * density), (int)(16 * density), (int)(24 * density));
        _content.Clickable = true;
        _content.Click += (_, _) => CommitScalePresets();
        scroll.Clickable = true;
        scroll.Click += (_, _) => CommitScalePresets();
        scroll.AddView(_content, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 0, 1));

        // INPUT-13：外设连接只增删录制控件，不重置工作副本、不关闭页面、不弹软键盘。
        AndroidInputDevices.Changed += OnInputDevicesChanged;
        // SETTINGS-06/12：内存读数与能力提示随周期探测更新，只刷提示行。
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        BuildPage("root");
        _viewport = AndroidSheetViewport.Create(this, root);
    }

    /// <summary>窗口挂载后开始同步内容可见高度。</summary>
    protected override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        _viewport.Attach();
    }

    /// <summary>视图分离时解除绘制前回调。</summary>
    protected override void OnDetachedFromWindow()
    {
        _viewport.Detach();
        base.OnDetachedFromWindow();
    }

    /// <summary>返回上一页；已在根页时返回 false（由调用方关闭 sheet）。</summary>
    /// <returns>是否消费返回。</returns>
    internal bool HandleBack() => GoBack();

    /// <summary>请求关闭 sheet（返回键在根页时使用）。</summary>
    internal void RequestDismiss() => Dismiss();

    /// <summary>返回根页；已在根页时返回 false。</summary>
    private bool GoBack()
    {
        if (_page == "root") return false;
        BuildPage("root");
        return true;
    }

    /// <summary>关闭并避免重复关闭；提交由 Dialog 的 DismissEvent 统一 await 完成。</summary>
    private void Dismiss()
    {
        if (_closed) return;
        _closed = true;
        CommitScalePresets();
        CancelRecording();
        _dismiss();
    }

    /// <summary>关闭事件（含下滑）统一提交未失焦的预设输入。</summary>
    internal void CommitPendingEdits() => CommitScalePresets();

    /// <summary>内存读数与能力提示随周期探测更新（SETTINGS-06）：只刷提示行，不重建页面结构、不动设置值。</summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SettingsViewModel.PerformanceBudgetInfo)
            or nameof(SettingsViewModel.BitmapCacheCountInfo)
            or nameof(SettingsViewModel.PerformanceBudgetInfoLevel)
            or nameof(SettingsViewModel.BitmapCacheCountInfoLevel))) return;
        // 其他页面没有提示行，不必排队刷新；回调可能来自探测线程，统一回 UI 线程刷新。
        if (_dynamicTexts.Count == 0) return;
        Post(() =>
        {
            if (!_closed) RefreshDynamicTexts();
        });
    }

    /// <summary>外设连接/断开：取消不再可用的录制，刷新控制页行，不触碰工作副本或草稿。</summary>
    private void OnInputDevicesChanged()
    {
        Post(() =>
        {
            if (_closed) return;
            if (!AndroidInputDevices.HasBindingDevice) CancelRecording();
            if (_page == "control") _hotkeyList?.NotifyAllRowsChanged();
        });
    }

    /// <summary>释放时解除外设与设置监听。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            _recordingItem = null;
            _viewport.Detach();
            AndroidInputDevices.Changed -= OnInputDevicesChanged;
            Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>重建页面内容。</summary>
    /// <param name="page">页面标识。</param>
    private void BuildPage(string page)
    {
        CommitScalePresets();
        if (page != "control") CancelRecording();
        _page = page;
        _hotkeyList = null;
        _formatList = null;
        _exifList = null;
        _scaleCard = null;
        _scaleAddRow = null;
        _previewSwitchSection = null;
        _layoutSection = null;
        _scaleRows.Clear();
        _content.RemoveAllViews();
        _dynamicTexts.Clear();
        _fileInfoLabels.Clear();
        _back.Visibility = page == "root" ? ViewStates.Invisible : ViewStates.Visible;
        _title.Text = page switch
        {
            "files" => "文件",
            "preview" => "预览",
            "control" => "控制",
            "exif" => "EXIF",
            "ai" => "AI",
            _ => "设置",
        };
        switch (page)
        {
            case "files": BuildFilePage(); break;
            case "preview": BuildPreviewPage(); break;
            case "control": BuildControlPage(); break;
            case "exif": BuildExifPage(); break;
            case "ai": BuildAiPage(); break;
            default: BuildRootPage(); break;
        }
    }

    /// <summary>根页：五个分类入口（与工具页同一分组列表导航外观）。</summary>
    private void BuildRootPage()
    {
        Add(_content, Card(new List<View>
        {
            CategoryRow("文件", "同名合并、文件格式与缓存预加载", "files"),
            CategoryRow("预览", "缩放指示器与缩放比例预设", "preview"),
            CategoryRow("控制", "布局模式与控制栏显示", "control"),
            CategoryRow("EXIF", "EXIF 显示项与评分写回策略", "exif"),
            CategoryRow("AI",
#if PHOTOVIEWER_DINO
                "相似聚类阈值与即时计算",
#else
                "即时分析计算",
#endif
                "ai"),
        }));
    }

    /// <summary>文件页：同名合并、文件格式勾选/排序、缓存与预加载。</summary>
    private void BuildFilePage()
    {
        Add(_content, Section(null, SwitchRow("同名文件视为同一张图片",
            "按列表排序优先级高的作为代表显示图片，修改星级时同步至所有同名文件",
            () => Settings.SameNameAsOnePhoto, value => Settings.SameNameAsOnePhoto = value)));
        var formats = new AndroidSettingsReorderList(_activity,
            () => Settings.FileFormats.Count,
            index =>
            {
                var item = Settings.FileFormats[index];
                return new AndroidSettingsReorderList.SettingsReorderItem(
                    item.DisplayName, item.ExtensionsText,
                    () => item.IsEnabled, value => item.IsEnabled = value);
            },
            (from, to) => Settings.MoveFileFormat(from, to));
        _formatList = formats;
        Add(_content, SectionView("支持的文件格式", formats));
        var info = new List<View>();
        var performance = InfoLabel(() => Settings.PerformanceBudgetInfo, () => Settings.PerformanceBudgetInfoLevel);
        var cache = InfoLabel(() => Settings.BitmapCacheCountInfo, () => Settings.BitmapCacheCountInfoLevel);
        _fileInfoLabels.Add(performance);
        _fileInfoLabels.Add(cache);
        var cacheRows = new List<View>
        {
            StepperRow("原生解码线程", null, () => Settings.NativePreloadParallelism,
                value => Settings.NativePreloadParallelism = value, 1, () => Settings.NativePreloadParallelismMaximum,
                1, value => $"{value} 线程"),
            StepperRow("软件解码线程", null, () => Settings.CpuPreloadParallelism,
                value => Settings.CpuPreloadParallelism = value, 1, () => Settings.CpuPreloadParallelismMaximum,
                1, value => $"{value} 线程"),
        };
        // SETTINGS-06@android：缓存容量由实测预算自动推导，两项用户上限不显示（模型字段保留）。
        if (Settings.ShowsCacheLimits)
        {
            cacheRows.Add(StepperRow("缓存内存上限", null, () => Settings.BitmapCacheMaxMemory,
                value => Settings.BitmapCacheMaxMemory = value, 512, () => 32768, 512, value => $"{value} MB"));
            cacheRows.Add(StepperRow("缓存数量上限", null, () => Settings.BitmapCacheMaxCount,
                value => Settings.BitmapCacheMaxCount = value, 1, () => 400, 10, value => $"{value} 张"));
        }
        cacheRows.Add(StepperRow("下一张预载数量", null, () => Settings.PreloadForwardCount,
            value => Settings.PreloadForwardCount = value, 0, () => Settings.PreloadMaximum, 1, value => $"{value} 张"));
        cacheRows.Add(StepperRow("上一张预载数量", null, () => Settings.PreloadBackwardCount,
            value => Settings.PreloadBackwardCount = value, 0, () => Settings.PreloadMaximum, 1, value => $"{value} 张"));
        cacheRows.Add(StepperRow("列表滚动预载数量", null, () => Settings.VisibleCenterPreloadCount,
            value => Settings.VisibleCenterPreloadCount = value, 0, () => Settings.PreloadMaximum, 1, value => $"{value} 张"));
        cacheRows.Add(StepperRow("滚动预载延时", null, () => Settings.VisibleCenterDelayMs,
            value => Settings.VisibleCenterDelayMs = value,
            SettingsDocumentCodec.MinimumVisibleCenterDelayMs,
            () => SettingsDocumentCodec.MaximumVisibleCenterDelayMs,
            SettingsDocumentCodec.VisibleCenterDelayStepMs,
            value => $"{value} ms"));
        Add(_content, Section("缓存与预加载", cacheRows.ToArray()));
        Add(_content, performance);
        Add(_content, cache);
        RefreshDynamicTexts();
    }

    /// <summary>预览页：缩放指示器开关与缩放预设编辑。</summary>
    private void BuildPreviewPage()
    {
        _previewSwitchSection = Section(null,
            SwitchRow("缩放时显示指示器", null, () => Settings.ShowZoomIndicator,
                value => Settings.ShowZoomIndicator = value));
        Add(_content, _previewSwitchSection);
        _scaleRows.Clear();
        _scaleCard = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#262626"));
        background.SetCornerRadius((float)AndroidUiTheme.Dp(_activity, 10));
        _scaleCard.Background = background;
        _scaleCard.ClipToOutline = true;
        _scaleAddRow = TextButtonRow("＋ 添加缩放比例", AddScalePreset);
        RebuildScaleCard();
        Add(_content, SectionView("缩放比例预设", _scaleCard));
    }

    /// <summary>按工作副本顺序原地重排预设行，保留已存在的输入框实例。</summary>
    private void RebuildScaleCard()
    {
        var card = _scaleCard;
        if (card == null) return;
        card.RemoveAllViews();
        bool first = true;
        foreach (var preset in Settings.ScalePresets)
        {
            if (!_scaleRows.TryGetValue(preset, out var row))
            {
                row = CreateScalePresetRow(preset);
                _scaleRows[preset] = row;
            }
            if (!first) card.AddView(CreateScaleDivider(), new LinearLayout.LayoutParams(LayoutParams.MatchParent, 1));
            card.AddView(row.Root, new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
            first = false;
        }
        if (!first) card.AddView(CreateScaleDivider(), new LinearLayout.LayoutParams(LayoutParams.MatchParent, 1));
        if (_scaleAddRow != null)
            card.AddView(_scaleAddRow, new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        UpdateScaleDeleteButtons();
    }

    private View CreateScaleDivider()
    {
        var divider = new View(_activity);
        divider.SetBackgroundColor(Color.ParseColor("#333333"));
        return divider;
    }

    /// <summary>更新所有预设行的删除可用态（至少保留一项）。</summary>
    private void UpdateScaleDeleteButtons()
    {
        bool canRemove = Settings.CanRemoveScalePreset;
        foreach (var row in _scaleRows.Values)
        {
            row.Delete.Enabled = canRemove;
            row.Delete.Alpha = canRemove ? 1f : .35f;
        }
    }

    /// <summary>控制页：布局模式与控制栏功能（显隐/排序，键鼠外设连接时含主/次绑定录制）。</summary>
    private void BuildControlPage()
    {
        _layoutSection = Section("边栏布局位置",
            NavigationRow("布局模式", CurrentLayoutModeDescription(), CurrentLayoutModeName(), ShowLayoutModePicker));
        Add(_content, _layoutSection);
        var hotkeys = new AndroidSettingsReorderList(_activity,
            () => Settings.Hotkeys.Count,
            index =>
            {
                var item = Settings.Hotkeys[index];
                var captured = item;
                // SETTINGS-01 / INPUT-13：有键盘/鼠标/触控板时提供完整主/次绑定、录制与清空；
                // 仅直接触控时由行退回只读摘要，命令显隐与排序不变。
                return new AndroidSettingsReorderList.SettingsReorderItem(
                    item.Name, null,
                    () => item.IsDisplay, value => item.IsDisplay = value,
                    hotkey: item,
                    recordingEnabled: () => AndroidInputDevices.HasBindingDevice,
                    beginRecording: primary => BeginRecording(captured, primary),
                    clearBinding: primary => ClearBinding(captured, primary),
                    isRecording: primary => ReferenceEquals(_recordingItem, captured) && _recordingPrimary == primary);
            },
            (from, to) => Settings.MoveHotkey(from, to));
        _hotkeyList = hotkeys;
        Add(_content, SectionView("控制栏功能", hotkeys));
    }

    /// <summary>进入主/次绑定录制态；同一时间只有一行在录制。</summary>
    /// <param name="item">目标快捷键行。</param>
    /// <param name="primary">是否录制主绑定。</param>
    internal void BeginRecording(SettingsViewModel.HotkeyItem item, bool primary)
    {
        if (!AndroidInputDevices.HasBindingDevice) return;
        _recordingItem = item;
        _recordingPrimary = primary;
        _hotkeyList?.NotifyAllRowsChanged();
    }

    /// <summary>取消录制并保留原绑定。</summary>
    internal void CancelRecording()
    {
        if (_recordingItem == null) return;
        _recordingItem = null;
        _recordingPrimary = false;
        _hotkeyList?.NotifyAllRowsChanged();
    }

    /// <summary>清空主/次绑定并重新检测冲突。</summary>
    /// <param name="item">目标快捷键行。</param>
    /// <param name="primary">是否清空主绑定。</param>
    internal void ClearBinding(SettingsViewModel.HotkeyItem item, bool primary)
    {
        if (primary) item.PrimaryHotkey = null;
        else item.SecondaryHotkey = null;
        Settings.CheckHotkeyConflicts();
        if (ReferenceEquals(_recordingItem, item) && _recordingPrimary == primary) CancelRecording();
        else _hotkeyList?.NotifyAllRowsChanged();
    }

    /// <summary>
    /// 录制态键盘：Esc 与系统返回键取消并保留原绑定，Delete/Backspace 清空，其余完整手势写入槽位。
    /// 只有真实按下（<see cref="KeyEventActions.Down"/>）参与录制：抬起与输入法的多字符提交（Multiple）
    /// 只被吞掉，不写绑定；系统返回键不参与映射，取消语义保证它永远不会被录成无法派发的死绑定。
    /// </summary>
    /// <param name="e">按键事件。</param>
    /// <returns>录制态是否消费该事件。</returns>
    internal bool HandleRecordingKey(KeyEvent? e)
    {
        if (_recordingItem == null || e == null) return false;
        if (e.Action != KeyEventActions.Down) return true;
        if (e.KeyCode is Keycode.Escape or Keycode.Back)
        {
            CancelRecording();
            return true;
        }
        if (e.KeyCode is Keycode.Del or Keycode.ForwardDel)
        {
            AssignRecordedGesture(null);
            return true;
        }
        // 只按修饰键不产出手势，也不结束录制。
        if (e.KeyCode is Keycode.ShiftLeft or Keycode.ShiftRight or Keycode.CtrlLeft or Keycode.CtrlRight
            or Keycode.AltLeft or Keycode.AltRight or Keycode.MetaLeft or Keycode.MetaRight)
            return true;
        var gesture = AndroidInputMapping.FromKeyEvent(e.KeyCode, (int)e.MetaState);
        if (gesture != null) AssignRecordedGesture(gesture);
        return true;
    }

    /// <summary>录制态滚轮：上/下滚各成一档，带修饰键时一并记录。</summary>
    /// <param name="vertical">纵向滚动量，正数为向上。</param>
    /// <param name="metaState">原生修饰键状态。</param>
    /// <returns>录制态是否消费。</returns>
    internal bool HandleRecordingWheel(float vertical, int metaState)
    {
        if (_recordingItem == null || Math.Abs(vertical) < .01f) return false;
        var action = vertical > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
        AssignRecordedGesture(AppGesture.FromMouse(
            new MouseGestureEx(action, AndroidInputMapping.ModifiersOf(metaState))));
        return true;
    }

    /// <summary>录制态吞掉来自鼠标/触控板的触摸事件（含左右键），防止触发下层按钮。</summary>
    /// <param name="e">触摸事件。</param>
    /// <returns>是否消费。</returns>
    internal bool HandleRecordingMouseTouch(MotionEvent? e)
    {
        if (_recordingItem == null || e == null) return false;
        var source = e.Source;
        return (source & InputSourceType.Mouse) == InputSourceType.Mouse
            || (source & InputSourceType.Touchpad) == InputSourceType.Touchpad;
    }

    /// <summary>录制态鼠标按键：中键与侧键可录制，左右键不产生绑定。</summary>
    /// <param name="button">原生按键。</param>
    /// <param name="metaState">原生修饰键状态。</param>
    /// <returns>录制态是否消费。</returns>
    internal bool HandleRecordingButton(MotionEventButtonState button, int metaState)
    {
        if (_recordingItem == null) return false;
        MouseAction? action = button switch
        {
            MotionEventButtonState.Tertiary => MouseAction.MiddleClick,
            MotionEventButtonState.Back => MouseAction.XButton1Click,
            MotionEventButtonState.Forward => MouseAction.XButton2Click,
            _ => null,
        };
        if (action == null) return false;
        AssignRecordedGesture(AppGesture.FromMouse(
            new MouseGestureEx(action.Value, AndroidInputMapping.ModifiersOf(metaState))));
        return true;
    }

    /// <summary>把录制结果写入当前槽位、结束录制并刷新行内冲突。</summary>
    private void AssignRecordedGesture(AppGesture? gesture)
    {
        var item = _recordingItem;
        if (item == null) return;
        if (_recordingPrimary) item.PrimaryHotkey = gesture;
        else item.SecondaryHotkey = gesture;
        _recordingItem = null;
        _recordingPrimary = false;
        Settings.CheckHotkeyConflicts();
        _hotkeyList?.NotifyAllRowsChanged();
        Console.WriteLine($"[Native] 快捷键录制：{item.Name} {(gesture == null ? "已清空" : gesture.ToDisplayString())}");
    }

    /// <summary>EXIF 页：显示项勾选/排序、评分显示与安全写回。</summary>
    private void BuildExifPage()
    {
        var items = new AndroidSettingsReorderList(_activity,
            () => Settings.ExifDisplayItems.Count,
            index =>
            {
                var item = Settings.ExifDisplayItems[index];
                return new AndroidSettingsReorderList.SettingsReorderItem(
                    item.DisplayName, null,
                    () => item.IsEnabled, value => item.IsEnabled = value);
            },
            (from, to) => Settings.MoveExifDisplay(from, to));
        _exifList = items;
        Add(_content, SectionView("EXIF 显示/收纳", items));
        Add(_content, Section("评分与写回",
            SwitchRow("显示星级评分", null, () => Settings.ShowRating, value => Settings.ShowRating = value),
            SwitchRow("安全修改文件星级", "检验文件字节变化再写回，降低损坏原文件概率",
                () => Settings.SafeSetRating, value => Settings.SafeSetRating = value)));
    }

    /// <summary>AI 页：按版本提供相似聚类参数及相互独立的 DINO/CV 即时计算开关。</summary>
    private void BuildAiPage()
    {
#if PHOTOVIEWER_DINO
        Add(_content, Section("相似聚类",
            StepperRow("相似度阈值", null, () => (int)Math.Round(Settings.SimilarityThreshold * 100),
                value => Settings.SimilarityThreshold = value / 100.0,
                (int)Math.Round(SettingsViewModel.SimilarityThresholdMin * 100),
                () => (int)Math.Round(SettingsViewModel.SimilarityThresholdMax * 100), 1, value => $"{value}%"),
            StepperRow("最多数量", null, () => Settings.SimilarityMaxResults,
                value => Settings.SimilarityMaxResults = value,
                SettingsViewModel.SimilarityMaxResultsMin, () => SettingsViewModel.SimilarityMaxResultsMax,
                1, value => $"{value} 张")));
#endif
        var analysisRows = new List<View>
        {
#if PHOTOVIEWER_DINO
            SwitchRow("即时计算 DINO", null, () => Settings.AnalysisImmediateCompute,
                value => Settings.AnalysisImmediateCompute = value),
#endif
            SwitchRow("即时计算 CV", null, () => Settings.AnalysisImmediateCvCompute,
                value => Settings.AnalysisImmediateCvCompute = value),
        };
        Add(_content, Section("分析诊断", analysisRows.ToArray()));
    }

    /// <summary>布局模式选择对话框。</summary>
    private void ShowLayoutModePicker()
    {
        var modes = new List<string>();
        var values = new List<LayoutMode>();
        foreach (var item in Settings.LayoutModes)
        {
            modes.Add(item.DisplayName);
            values.Add(item.Value);
        }
        var builder = new AlertDialog.Builder(_activity);
        builder.SetTitle("布局模式");
        builder.SetItems(modes.ToArray(), (_, args) =>
        {
            int which = args.Which;
            if (which >= 0 && which < values.Count)
            {
                Settings.LayoutMode = values[which];
                BuildPage("control");
            }
        });
        builder.SetNegativeButton("取消", (_, _) => { });
        builder.Show();
    }

    private string CurrentLayoutModeName()
    {
        foreach (var item in Settings.LayoutModes)
            if (item.Value == Settings.LayoutMode) return item.DisplayName;
        return Settings.LayoutMode.ToString();
    }

    private string CurrentLayoutModeDescription()
    {
        foreach (var item in Settings.LayoutModes)
            if (item.Value == Settings.LayoutMode) return item.Description;
        return string.Empty;
    }

    /// <summary>单个预设行：唯一居中数值输入框、固定 % 与删除按钮。</summary>
    private sealed record ScaleRowView(LinearLayout Root, EditText Input, TextView Percent, TextView Delete);

    private ScaleRowView CreateScalePresetRow(SettingsViewModel.ScalePreset preset)
    {
        // SETTINGS-15：缩放预设是单行紧凑行，不随通用设置行恢复的标准高度。
        var row = CompactRow();
        var density = (float)AndroidUiTheme.Density(_activity);
        var input = new EditText(_activity)
        {
            Text = preset.Text,
            InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal,
            Gravity = GravityFlags.Center,
            TextSize = 15,
        };
        input.SetSingleLine(true);
        input.SetTextColor(AndroidUiTheme.TextPrimary);
        input.SetHintTextColor(AndroidUiTheme.TextSecondary);
        var inputBackground = new GradientDrawable();
        inputBackground.SetColor(AndroidUiTheme.ControlFill);
        inputBackground.SetCornerRadius(6 * density);
        inputBackground.SetStroke(Math.Max(1, (int)density), AndroidUiTheme.ControlBorder);
        input.Background = inputBackground;
        input.SetPadding((int)(8 * density), (int)(6 * density), (int)(8 * density), (int)(6 * density));
        input.SetMinimumHeight((int)(40 * density));
        input.ContentDescription = "缩放比例百分比";
        input.FocusChange += (_, args) =>
        {
            if (args.HasFocus || _committingScalePresets) return;
            CommitScalePreset(preset, input, reorder: true);
            HideKeyboard(input);
        };
        input.EditorAction += (_, args) =>
        {
            if (args.ActionId == ImeAction.Done) CommitScalePresets();
            args.Handled = true;
        };

        var percent = Text("%", 15, AndroidUiTheme.TextSecondary);
        percent.Gravity = GravityFlags.Center;
        percent.SetPadding((int)(4 * density), 0, (int)(4 * density), 0);

        var delete = Button("−", 18, () => DeleteScalePreset(preset));
        delete.ContentDescription = "删除缩放比例";

        // SETTINGS-07：输入框与 % 作为一组在整行水平居中；行尾删除按钮不挤偏中心。
        // 行首放一个与删除按钮等宽的透明平衡位，两侧权重相等，输入组中点正好落在行中点。
        var leadingBalance = new View(_activity);
        var leadingSpacer = new View(_activity);
        var trailingSpacer = new View(_activity);
        row.AddView(leadingBalance, new LinearLayout.LayoutParams(ButtonSize(), 1));
        row.AddView(leadingSpacer, new LinearLayout.LayoutParams(0, 1, 1f));
        row.AddView(input, new LinearLayout.LayoutParams(ScalePresetInputWidth(), LayoutParams.WrapContent));
        row.AddView(percent, new LinearLayout.LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));
        row.AddView(trailingSpacer, new LinearLayout.LayoutParams(0, 1, 1f));
        row.AddView(delete, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
        return new ScaleRowView(row, input, percent, delete);
    }

    /// <summary>预设输入框宽度（dp→px）：固定宽使输入组在整行内可计算地居中。</summary>
    private int ScalePresetInputWidth() => (int)AndroidUiTheme.Dp(_activity, 96);

    /// <summary>校验并写入单个输入框；非法值恢复原文本并提示。</summary>
    private bool CommitScalePreset(SettingsViewModel.ScalePreset preset, EditText input, bool reorder)
    {
        string original = preset.Text;
        string text = input.Text?.Trim() ?? string.Empty;
        if (!TryParsePresetPercent(text, out double value) || !SettingsViewModel.IsValidScalePresetPercent(value))
        {
            Toast.MakeText(_activity, "请输入大于 0 且不超过 1600 的数字", ToastLength.Short)?.Show();
            input.Text = original;
            input.SetSelection(input.Text?.Length ?? 0);
            return false;
        }
        string normalized = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (preset.Text != normalized) preset.Text = normalized;
        if (input.Text != preset.Text) input.Text = preset.Text;
        if (reorder && !ScalePresetsAscending()) SortScalePresetsInPlace();
        return true;
    }

    /// <summary>提交所有预设输入（失焦、空白点击、导航与关闭共用），再按数值原地重排。</summary>
    private void CommitScalePresets()
    {
        if (_committingScalePresets) return;
        _committingScalePresets = true;
        bool valid = true;
        foreach (var preset in Settings.ScalePresets)
        {
            if (!_scaleRows.TryGetValue(preset, out var row)) continue;
            if (row.Input.HasFocus)
            {
                row.Input.ClearFocus();
                HideKeyboard(row.Input);
            }
            valid &= CommitScalePreset(preset, row.Input, reorder: false);
        }
        _committingScalePresets = false;
        if (valid && !ScalePresetsAscending()) SortScalePresetsInPlace();
        if (!valid) UpdateScaleDeleteButtons();
    }

    /// <summary>排序工作副本并原地重排行视图，不重建页面、不重建输入框。</summary>
    private void SortScalePresetsInPlace()
    {
        Settings.SortScalePreset();
        RebuildScaleCard();
    }

    /// <summary>提交后新增默认 100% 项并原地插入排序位置。</summary>
    private void AddScalePreset()
    {
        CommitScalePresets();
        Settings.AddScalePreset();
        Settings.SortScalePreset();
        RebuildScaleCard();
    }

    /// <summary>提交后删除指定项并原地重排行；至少保留一项。</summary>
    private void DeleteScalePreset(SettingsViewModel.ScalePreset preset)
    {
        CommitScalePresets();
        if (!Settings.CanRemoveScalePreset) return;
        Settings.RemoveScalePreset(preset);
        _scaleRows.Remove(preset);
        RebuildScaleCard();
    }

    private static bool TryParsePresetPercent(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    /// <summary>预设是否已按数值升序（重复值允许）。</summary>
    private bool ScalePresetsAscending()
    {
        for (int index = 1; index < Settings.ScalePresets.Count; index++)
            if (Settings.ScalePresets[index - 1].Value > Settings.ScalePresets[index].Value) return false;
        return true;
    }

    /// <summary>收起数值输入框的软键盘。</summary>
    private void HideKeyboard(View input)
    {
        var manager = _activity.GetSystemService(Context.InputMethodService) as InputMethodManager;
        manager?.HideSoftInputFromWindow(input.WindowToken, 0);
    }

    /// <summary>验收：切换设置子页。</summary>
    internal void ShowPageForChecks(string page) => BuildPage(page);

    /// <summary>验收：当前布局模式说明。</summary>
    internal string LayoutModeDescriptionForChecks() => CurrentLayoutModeDescription();

    /// <summary>验收：控制页布局分组的可见标题（分组不带标题时为空串）。</summary>
    internal string LayoutSectionTitleForChecks() => SectionTitleText(_layoutSection);

    /// <summary>验收：预览页缩放指示器开关分组的可见标题（应为空串，SETTINGS-07）。</summary>
    internal string PreviewSwitchSectionTitleForChecks() => SectionTitleText(_previewSwitchSection);

    /// <summary>验收：缩放预设行输入组居中与删除按钮位置（像素，相对行左缘）。</summary>
    /// <param name="index">预设索引。</param>
    /// <returns>快照文本；行未布局时返回 not-laid-out。</returns>
    internal string ScalePresetGroupGeometryForChecks(int index)
    {
        if (index < 0 || index >= Settings.ScalePresets.Count
            || !_scaleRows.TryGetValue(Settings.ScalePresets[index], out var row)) return "missing";
        var root = row.Root;
        if (root.Width <= 0) return "not-laid-out";
        double groupLeft = row.Input.Left;
        double groupRight = row.Percent.Right;
        double groupMid = (groupLeft + groupRight) / 2;
        double rowMid = root.Width / 2.0;
        double offset = groupMid - rowMid;
        return $"rowWidth={root.Width} groupMid={groupMid:0.0} rowMid={rowMid:0.0} offset={offset:0.0}"
            + $" deleteLeft={row.Delete.Left} deleteWidth={row.Delete.Width}";
    }

    /// <summary>分组标题文本：分组视图的第一个子视图是标题文本时取它，否则视为不带标题。</summary>
    /// <param name="section">分组视图。</param>
    /// <returns>标题文本或空串。</returns>
    private static string SectionTitleText(View? section)
        => section is LinearLayout stack && stack.ChildCount > 0 && stack.GetChildAt(0) is TextView text
            ? text.Text ?? string.Empty
            : string.Empty;

    /// <summary>验收：读取指定快捷键行真实视图的可见性与读屏文本。</summary>
    internal AndroidSettingsReorderList.RowSnapshot? HotkeyRowSnapshotForChecks(int index)
        => _hotkeyList?.SnapshotForChecks(index);

    /// <summary>验收：快捷键行的布局快照（行高、勾选对齐、图标语义）。</summary>
    internal AndroidSettingsReorderList.RowLayoutSnapshot? HotkeyRowLayoutForChecks(int index)
        => _hotkeyList?.LayoutForChecks(index);

    /// <summary>验收：支持的文件格式行的布局快照。</summary>
    internal AndroidSettingsReorderList.RowLayoutSnapshot? FormatRowLayoutForChecks(int index)
        => _formatList?.LayoutForChecks(index);

    /// <summary>验收：EXIF 显示行的布局快照。</summary>
    internal AndroidSettingsReorderList.RowLayoutSnapshot? ExifRowLayoutForChecks(int index)
        => _exifList?.LayoutForChecks(index);

    /// <summary>验收：缩放预设行的真实高度（像素）。</summary>
    internal int ScalePresetRowHeightForChecks(int index)
        => index >= 0 && index < Settings.ScalePresets.Count
            && _scaleRows.TryGetValue(Settings.ScalePresets[index], out var row) ? row.Root.Height : 0;

    /// <summary>验收：半屏内容滚动宿主（手势断言用）。</summary>
    internal AndroidSheetScrollView ScrollHostForChecks => _scroll;

    /// <summary>验收：文件页内存/缓存提示行（多行显示与语义色断言用）。</summary>
    internal IReadOnlyList<TextView> FileInfoLabelsForChecks => _fileInfoLabels;

    /// <summary>验收：提示语义级别对应的文字颜色（常规次要 / 警示 / 错误）。</summary>
    /// <param name="level">语义级别。</param>
    /// <returns>文字颜色。</returns>
    internal static Color LevelColorForChecks(PerfInfoLevel level) => LevelColor(() => level);

    /// <summary>验收：顶部标题栏（拖动关闭断言用）。</summary>
    internal View HeaderForChecks => _header;

    /// <summary>验收：进入指定行主/次绑定录制。</summary>
    internal void BeginRecordingForChecks(int index, bool primary)
    {
        if (index >= 0 && index < Settings.Hotkeys.Count) BeginRecording(Settings.Hotkeys[index], primary);
    }

    /// <summary>验收：注入一次按键到录制态。</summary>
    internal bool FeedRecordingKeyForChecks(Keycode key, KeyEventActions action = KeyEventActions.Down)
        => HandleRecordingKey(new KeyEvent(action, key));

    /// <summary>验收：注入一次滚轮到录制态。</summary>
    internal bool FeedRecordingWheelForChecks(float vertical) => HandleRecordingWheel(vertical, 0);

    /// <summary>验收：注入一次鼠标按键到录制态。</summary>
    internal bool FeedRecordingButtonForChecks(MotionEventButtonState button) => HandleRecordingButton(button, 0);

    /// <summary>验收：写入真实输入框并走完整提交路径；返回该逻辑项提交后的文本。</summary>
    internal string CommitScalePresetForChecks(int index, string text)
    {
        if (index < 0 || index >= Settings.ScalePresets.Count) return string.Empty;
        var preset = Settings.ScalePresets[index];
        if (_scaleRows.TryGetValue(preset, out var row)) row.Input.Text = text;
        CommitScalePresets();
        return preset.Text;
    }

    /// <summary>验收：写入真实输入框但不提交（模拟未失焦草稿）。</summary>
    internal void SetScalePresetInputForChecks(int index, string text)
    {
        if (index < 0 || index >= Settings.ScalePresets.Count) return;
        if (_scaleRows.TryGetValue(Settings.ScalePresets[index], out var row)) row.Input.Text = text;
    }

    /// <summary>验收：真实输入框实例（用于断言原地提交未重建控件）。</summary>
    internal object? ScalePresetInputIdentityForChecks(int index)
        => index >= 0 && index < Settings.ScalePresets.Count
            && _scaleRows.TryGetValue(Settings.ScalePresets[index], out var row) ? row.Input : null;

    /// <summary>验收：真实输入框文本。</summary>
    internal string ScalePresetInputTextForChecks(int index)
        => index >= 0 && index < Settings.ScalePresets.Count
            && _scaleRows.TryGetValue(Settings.ScalePresets[index], out var row) ? row.Input.Text ?? string.Empty : string.Empty;

    /// <summary>验收：提交全部草稿（空白/导航/关闭共用路径）。</summary>
    internal void CommitScalePresetsForChecks() => CommitScalePresets();

    /// <summary>验收：当前缩放预设列表文本。</summary>
    internal string ScalePresetsForChecks()
    {
        var parts = new List<string>();
        foreach (var preset in Settings.ScalePresets) parts.Add(preset.Text);
        return string.Join(",", parts);
    }

    /// <summary>验收：读取真实行视图的控件构成。</summary>
    internal string ScalePresetRowShapeForChecks(int index)
    {
        if (index < 0 || index >= Settings.ScalePresets.Count
            || !_scaleRows.TryGetValue(Settings.ScalePresets[index], out var row)) return "missing";
        int inputs = 0, percent = 0, deletes = 0, centered = 0;
        for (int child = 0; child < row.Root.ChildCount; child++)
        {
            switch (row.Root.GetChildAt(child))
            {
                case EditText input:
                    inputs++;
                    if ((input.Gravity & GravityFlags.HorizontalGravityMask) == GravityFlags.CenterHorizontal)
                        centered++;
                    break;
                case TextView text when text.Text == "%":
                    percent++;
                    break;
                case TextView text when text.Text == "−":
                    deletes++;
                    break;
            }
        }
        return $"inputs={inputs} centered={centered} percent={percent} delete={deletes}";
    }

    /// <summary>验收：缩放预设是否按数值升序。</summary>
    internal bool ScalePresetsAscendingForChecks() => ScalePresetsAscending();

    /// <summary>验收：按“＋”语义新增一项 100% 并原地重排。</summary>
    internal void AddScalePresetForChecks() => AddScalePreset();

    /// <summary>验收：按“−”语义删除一项并原地重排。</summary>
    internal void RemoveScalePresetForChecks(int index)
    {
        if (index >= 0 && index < Settings.ScalePresets.Count)
            DeleteScalePreset(Settings.ScalePresets[index]);
    }

    /// <summary>分类入口行。</summary>
    private View CategoryRow(string title, string subtitle, string page)
    {
        var row = Row();
        row.Clickable = true;
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        column.AddView(Text(title, 16, AndroidUiTheme.TextPrimary, bold: true));
        column.AddView(Text(subtitle, 12, AndroidUiTheme.TextSecondary));
        var chevron = Text("›", 22, AndroidUiTheme.TextSecondary);
        chevron.Gravity = GravityFlags.Center;
        row.AddView(column, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(chevron, new LinearLayout.LayoutParams(ChevronWidth(), LayoutParams.WrapContent));
        row.Click += (_, _) => BuildPage(page);
        return row;
    }

    /// <summary>开关行。</summary>
    private View SwitchRow(string title, string? subtitle, Func<bool> getter, Action<bool> setter)
    {
        var row = Row();
        var column = TextColumn(title, subtitle);
        var toggle = new SwitchMaterial(_activity)
        {
            Checked = getter(),
        };
        toggle.CheckedChange += (_, args) => setter(args.IsChecked);
        row.AddView(column, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(toggle, new LinearLayout.LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));
        return row;
    }

    /// <summary>数值步进行（Material 风格 +/-，显式步长与范围）。</summary>
    private View StepperRow(string title, string? subtitle, Func<int> getter, Action<int> setter,
        int minimum, Func<int> maximum, int step, Func<int, string>? formatter = null)
    {
        var row = Row();
        var column = TextColumn(title, subtitle);
        int current = Math.Clamp(getter(), minimum, Math.Max(minimum, maximum()));
        var valueLabel = Text(formatter?.Invoke(current) ?? current.ToString(CultureInfo.InvariantCulture), 13,
            AndroidUiTheme.TextSecondary);
        valueLabel.Gravity = GravityFlags.Center;
        var minus = Button("−", 18, () =>
        {
            int next = Math.Clamp(getter() - step, minimum, Math.Max(minimum, maximum()));
            setter(next);
            current = next;
            valueLabel.Text = formatter?.Invoke(next) ?? next.ToString(CultureInfo.InvariantCulture);
            RefreshDynamicTexts();
        });
        var plus = Button("＋", 18, () =>
        {
            int next = Math.Clamp(getter() + step, minimum, Math.Max(minimum, maximum()));
            setter(next);
            current = next;
            valueLabel.Text = formatter?.Invoke(next) ?? next.ToString(CultureInfo.InvariantCulture);
            RefreshDynamicTexts();
        });
        row.AddView(column, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(minus, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
        row.AddView(valueLabel, new LinearLayout.LayoutParams((int)AndroidUiTheme.Dp(_activity, 64), ButtonSize()));
        row.AddView(plus, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
        return row;
    }

    /// <summary>导航行（点击弹选择）。</summary>
    private View NavigationRow(string title, string? subtitle, string value, Action action)
    {
        var row = Row();
        row.Clickable = true;
        var column = TextColumn(title, subtitle);
        var current = Text(value, 13, AndroidUiTheme.TextSecondary);
        current.Gravity = GravityFlags.CenterVertical;
        var chevron = Text("›", 22, AndroidUiTheme.TextSecondary);
        chevron.Gravity = GravityFlags.Center;
        row.AddView(column, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(current, new LinearLayout.LayoutParams(LayoutParams.WrapContent, ButtonSize()));
        row.AddView(chevron, new LinearLayout.LayoutParams(ChevronWidth(), ButtonSize()));
        row.Click += (_, _) => action();
        return row;
    }

    /// <summary>整行按钮（添加预设等）。</summary>
    private View TextButtonRow(string title, Action action)
    {
        var row = Row();
        row.Clickable = true;
        var text = Text(title, 14, AndroidUiTheme.Accent);
        text.Gravity = GravityFlags.Center;
        row.AddView(text, new LinearLayout.LayoutParams(LayoutParams.MatchParent, ButtonSize()));
        row.Click += (_, _) => action();
        return row;
    }

    /// <summary>分区：可选标题 + 卡片。</summary>
    private View Section(string? title, params View[] rows) => SectionView(title, Card(rows));

    /// <summary>分区：标题 + 现成卡片（可排序表自带圆角卡片）。</summary>
    private View SectionView(string? title, View card)
    {
        var section = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        section.SetPadding(0, (int)AndroidUiTheme.Dp(_activity, 10), 0, 0);
        if (!string.IsNullOrWhiteSpace(title))
        {
            var header = Text(title, 13, AndroidUiTheme.TextSecondary, bold: true);
            header.SetPadding((int)AndroidUiTheme.Dp(_activity, 4), 0, 0, (int)AndroidUiTheme.Dp(_activity, 6));
            section.AddView(header);
        }
        section.AddView(card);
        return section;
    }

    /// <summary>把若干行装进圆角卡片，并在行间插入 1dp 分隔线。</summary>
    private LinearLayout Card(params View[] rows) => Card(new List<View>(rows));

    private LinearLayout Card(List<View> rows)
    {
        var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#262626"));
        background.SetCornerRadius((float)AndroidUiTheme.Dp(_activity, 10));
        card.Background = background;
        card.ClipToOutline = true;
        for (int index = 0; index < rows.Count; index++)
        {
            if (index > 0)
            {
                var divider = new View(_activity);
                divider.SetBackgroundColor(Color.ParseColor("#333333"));
                card.AddView(divider, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 1));
            }
            card.AddView(rows[index], new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        }
        return card;
    }

    /// <summary>
    /// 动态文本（内存/缓存提示）包装：按内容换行显示，颜色取语义级别（SETTINGS-06 / SETTINGS-12）。
    /// </summary>
    /// <param name="text">文本取值。</param>
    /// <param name="level">语义级别取值；为空时按常规级。</param>
    private TextView InfoLabel(Func<string> text, Func<PerfInfoLevel>? level = null)
    {
        var label = Text(text(), 12, LevelColor(level));
        // 系统总内存/当前可用与超限原因可能一行放不下：按内容换行，不省略、不截断。
        label.SetSingleLine(false);
        label.SetPadding((int)AndroidUiTheme.Dp(_activity, 4), (int)AndroidUiTheme.Dp(_activity, 6), 0, 0);
        _dynamicTexts.Add((label, text, level));
        return label;
    }

    /// <summary>提示语义级别取色：常规用次要文字，警示与错误用语义色（状态同时由文本表达）。</summary>
    /// <param name="level">语义级别取值。</param>
    /// <returns>文字颜色。</returns>
    private static Color LevelColor(Func<PerfInfoLevel>? level) => (level?.Invoke() ?? PerfInfoLevel.Normal) switch
    {
        PerfInfoLevel.Warning => AndroidUiTheme.WarningText,
        PerfInfoLevel.Error => AndroidUiTheme.ErrorText,
        _ => AndroidUiTheme.TextSecondary,
    };

    /// <summary>刷新所有动态文本与其语义色（只改提示行，不触发页面重建）。</summary>
    private void RefreshDynamicTexts()
    {
        if (_refreshingDynamicTexts) return;
        _refreshingDynamicTexts = true;
        try
        {
            foreach (var item in _dynamicTexts)
            {
                item.Label.Text = item.Text();
                item.Label.SetTextColor(LevelColor(item.Level));
            }
        }
        finally
        {
            _refreshingDynamicTexts = false;
        }
    }

    /// <summary>
    /// 通用行容器：水平、内容左右 16dp / 12dp、上下 12dp，最小行高取标准行令牌（52dp）；
    /// 大字体或多行内容时按内容增高。
    /// </summary>
    private LinearLayout Row() => BuildRow(compact: false);

    /// <summary>
    /// 单行紧凑行容器（SETTINGS-15）：缩放预设等只有一行的可编辑内容取 48dp 最小行高与 2dp 上下内边距，
    /// 不随通用行一起增高。
    /// </summary>
    private LinearLayout CompactRow() => BuildRow(compact: true);

    private LinearLayout BuildRow(bool compact)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int paddingLeft = (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidCardPaddingX);
        // SETTINGS-07：紧凑行（缩放预设）左右对称，输入组才能落在整行中点；标准行行尾留白取行令牌。
        int paddingRight = compact
            ? paddingLeft
            : (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidRowPaddingRight);
        int paddingY = (int)AndroidUiTheme.Dp(_activity, compact
            ? DesignTokens.SettingsAndroidListRowPaddingY
            : DesignTokens.SettingsAndroidRowPaddingY);
        int minimumHeight = (int)AndroidUiTheme.Dp(_activity, compact
            ? DesignTokens.SettingsAndroidListRowMinHeight
            : DesignTokens.SettingsAndroidRowMinHeight);
        row.SetPadding(paddingLeft, paddingY, paddingRight, paddingY);
        row.SetMinimumHeight(minimumHeight);
        return row;
    }

    /// <summary>标题/副标题竖列。</summary>
    private LinearLayout TextColumn(string title, string? subtitle)
    {
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        column.AddView(Text(title, 15, AndroidUiTheme.TextPrimary));
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var sub = Text(subtitle, 12, AndroidUiTheme.TextSecondary);
            sub.SetPadding(0, (int)AndroidUiTheme.Dp(_activity, 2), 0, 0);
            column.AddView(sub);
        }
        return column;
    }

    /// <summary>普通文本。</summary>
    private TextView Text(string? value, double sizeSp, Color color, bool bold = false)
        => new TextView(_activity)
        {
            Text = value,
            TextSize = (float)sizeSp,
            Typeface = bold ? Typeface.Create(Typeface.Default, TypefaceStyle.Bold) : Typeface.Default,
        }.WithTextColor(color);

    /// <summary>文本按钮（透明底，点击区域 44dp）。</summary>
    private TextView Button(string label, double sizeSp, Action action)
    {
        var text = Text(label, sizeSp, AndroidUiTheme.TextPrimary);
        text.Gravity = GravityFlags.Center;
        text.Clickable = true;
        text.Click += (_, _) => action();
        return text;
    }

    private int ButtonSize() => (int)AndroidUiTheme.Dp(_activity, 44);

    /// <summary>分类行/导航行行尾箭头的视觉容器宽度（SETTINGS-03：20dp，居中绘制）。</summary>
    private int ChevronWidth() => (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidNavigationChevronWidth);

    private void Add(LinearLayout parent, View child)
    {
        var parameters = new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent);
        parameters.TopMargin = 0;
        parent.AddView(child, parameters);
    }

}

/// <summary>TextView 颜色设置的链式辅助。</summary>
internal static class AndroidTextViewExtensions
{
    /// <summary>设置文本颜色并返回自身。</summary>
    /// <param name="view">文本视图。</param>
    /// <param name="color">颜色。</param>
    /// <returns>同一个视图。</returns>
    internal static TextView WithTextColor(this TextView view, Color color)
    {
        view.SetTextColor(color);
        return view;
    }
}
