using System;
using System.Collections.Generic;
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

    /// <summary>绑定宿主 Activity。</summary>
    /// <param name="activity">原生壳 Activity。</param>
    public AndroidNativeSettingsPresenter(Activity activity) => _activity = activity;

    /// <summary>当前是否已有打开中的设置 sheet。</summary>
    public static bool IsOpen => _current != null;

    /// <summary>当前设置 sheet（验收读取页面与绑定控件状态）。</summary>
    internal static AndroidSettingsSheetView? CurrentSheet => _current;

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
            int height = (int)((metrics?.HeightPixels ?? 1920) * .85);
            dialog.SetContentView(content, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, height));
            int applyStarted = 0;
            dialog.DismissEvent += async (_, _) =>
            {
                if (ReferenceEquals(_current, content)) _current = null;
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
            dialog.Show();
            var parent = content.Parent as View;
            if (parent != null)
            {
                var behavior = BottomSheetBehavior.From(parent);
                if (behavior != null)
                {
                    behavior.PeekHeight = (int)((metrics?.HeightPixels ?? 1920) * .5);
                    behavior.SkipCollapsed = false;
                    behavior.State = BottomSheetBehavior.StateExpanded;
                }
            }
            _dialog = dialog;
            _current = content;
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
    private readonly TextView _title;
    private readonly TextView _back;
    private readonly List<(TextView Label, Func<string> Text)> _dynamicTexts = [];
    private string _page = "root";
    private bool _closed;
    private AndroidSettingsReorderList? _hotkeyList;
    private SettingsViewModel.HotkeyItem? _recordingItem;
    private bool _recordingPrimary;
    private LinearLayout? _scaleCard;
    private View? _scaleAddRow;
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

        var scroll = new ScrollView(activity) { FillViewport = true };
        scroll.SetBackgroundColor(Color.Transparent);
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
        BuildPage("root");
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

    /// <summary>释放时解除外设监听。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            _recordingItem = null;
            AndroidInputDevices.Changed -= OnInputDevicesChanged;
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
        _scaleCard = null;
        _scaleAddRow = null;
        _scaleRows.Clear();
        _content.RemoveAllViews();
        _dynamicTexts.Clear();
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
            CategoryRow("AI", "相似聚类阈值与即时计算", "ai"),
        }));
    }

    /// <summary>文件页：同名合并、文件格式勾选/排序、缓存与预加载。</summary>
    private void BuildFilePage()
    {
        Add(_content, Section(null, null, SwitchRow("同名文件视为同一张图片",
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
        Add(_content, SectionView("支持的文件格式", null, formats));
        var info = new List<View>();
        var performance = InfoLabel(() => Settings.PerformanceBudgetInfo);
        var cache = InfoLabel(() => Settings.BitmapCacheCountInfo);
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
            value => Settings.VisibleCenterDelayMs = value, 100, () => 5000, 100, value => $"{value} ms"));
        Add(_content, Section("缓存与预加载", null, cacheRows.ToArray()));
        Add(_content, performance);
        Add(_content, cache);
        RefreshDynamicTexts();
    }

    /// <summary>预览页：缩放指示器开关与缩放预设编辑。</summary>
    private void BuildPreviewPage()
    {
        Add(_content, Section("缩放位置显示", null,
            SwitchRow("缩放时显示指示器", null, () => Settings.ShowZoomIndicator,
                value => Settings.ShowZoomIndicator = value)));
        _scaleRows.Clear();
        _scaleCard = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetColor(Color.ParseColor("#262626"));
        background.SetCornerRadius((float)AndroidUiTheme.Dp(_activity, 10));
        _scaleCard.Background = background;
        _scaleCard.ClipToOutline = true;
        _scaleAddRow = TextButtonRow("＋ 添加缩放比例", AddScalePreset);
        RebuildScaleCard();
        Add(_content, SectionView("缩放比例预设", "点击数值编辑，单位为百分比（100 = 物理 100%）。", _scaleCard));
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
        Add(_content, Section("控制栏布局位置", null,
            NavigationRow("布局模式", CurrentLayoutModeDescription(), CurrentLayoutModeName(), ShowLayoutModePicker)));
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
        Add(_content, SectionView("控制栏功能",
            "勾选决定是否显示在控制栏；拖动手柄排序，轻点手柄可用上移/下移菜单。", hotkeys));
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
        Add(_content, SectionView("EXIF 显示/收纳",
            "勾选决定是否显示在控制栏；拖动手柄排序，轻点手柄可用上移/下移菜单。", items));
        Add(_content, Section("评分与写回", null,
            SwitchRow("显示星级评分", null, () => Settings.ShowRating, value => Settings.ShowRating = value),
            SwitchRow("安全修改文件星级", "检验文件字节变化再写回，降低损坏原文件概率",
                () => Settings.SafeSetRating, value => Settings.SafeSetRating = value)));
    }

    /// <summary>AI 页：相似聚类阈值/数量与即时计算。</summary>
    private void BuildAiPage()
    {
        Add(_content, Section("相似聚类", null,
            StepperRow("相似度阈值", null, () => (int)Math.Round(Settings.SimilarityThreshold * 100),
                value => Settings.SimilarityThreshold = value / 100.0,
                (int)Math.Round(SettingsViewModel.SimilarityThresholdMin * 100),
                () => (int)Math.Round(SettingsViewModel.SimilarityThresholdMax * 100), 1, value => $"{value}%"),
            StepperRow("最多数量", null, () => Settings.SimilarityMaxResults,
                value => Settings.SimilarityMaxResults = value,
                SettingsViewModel.SimilarityMaxResultsMin, () => SettingsViewModel.SimilarityMaxResultsMax,
                1, value => $"{value} 张")));
        Add(_content, Section("分析诊断", null,
            SwitchRow("即时计算 DINO/CV", null, () => Settings.AnalysisImmediateCompute,
                value => Settings.AnalysisImmediateCompute = value)));
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
    private sealed record ScaleRowView(LinearLayout Root, EditText Input, TextView Delete);

    private ScaleRowView CreateScalePresetRow(SettingsViewModel.ScalePreset preset)
    {
        var row = Row();
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
        row.AddView(input, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(percent, new LinearLayout.LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));
        row.AddView(delete, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
        return new ScaleRowView(row, input, delete);
    }

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

    /// <summary>验收：读取指定快捷键行真实视图的可见性与读屏文本。</summary>
    internal AndroidSettingsReorderList.RowSnapshot? HotkeyRowSnapshotForChecks(int index)
        => _hotkeyList?.SnapshotForChecks(index);

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
        row.AddView(chevron, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
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
        row.AddView(chevron, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
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

    /// <summary>分区：可选标题 + 卡片 + 可选脚注。</summary>
    private View Section(string? title, string? footer, params View[] rows)
        => SectionView(title, footer, Card(rows));

    /// <summary>分区：标题 + 现成卡片（可排序表自带圆角卡片）+ 脚注。</summary>
    private View SectionView(string? title, string? footer, View card)
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
        if (!string.IsNullOrWhiteSpace(footer))
        {
            var note = Text(footer, 12, AndroidUiTheme.TextSecondary);
            note.SetPadding((int)AndroidUiTheme.Dp(_activity, 4), (int)AndroidUiTheme.Dp(_activity, 6), 0, 0);
            section.AddView(note);
        }
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

    /// <summary>动态文本（性能/缓存信息）包装。</summary>
    private TextView InfoLabel(Func<string> text)
    {
        var label = Text(text(), 12, AndroidUiTheme.TextSecondary);
        label.SetPadding((int)AndroidUiTheme.Dp(_activity, 4), (int)AndroidUiTheme.Dp(_activity, 6), 0, 0);
        _dynamicTexts.Add((label, text));
        return label;
    }

    /// <summary>刷新所有动态文本。</summary>
    private void RefreshDynamicTexts()
    {
        foreach (var item in _dynamicTexts) item.Label.Text = item.Text();
    }

    /// <summary>通用行容器：水平、左右 16dp、上下 12dp、最小高度 52dp。</summary>
    private LinearLayout Row()
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        double density = AndroidUiTheme.Density(_activity);
        row.SetPadding((int)(16 * density), (int)(12 * density), (int)(12 * density), (int)(12 * density));
        row.SetMinimumHeight((int)(52 * density));
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
