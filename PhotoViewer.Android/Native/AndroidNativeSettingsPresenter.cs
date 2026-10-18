using System;
using System.Collections.Generic;
using System.Globalization;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.SwitchMaterial;
using PhotoViewer.Core.Design;
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
            var content = new AndroidSettingsSheetView(_activity, settings, session, Dismiss);
            var dialog = new BottomSheetDialog(_activity, Resource.Style.Theme_PhotoViewer_BottomSheet);
            var metrics = _activity.Resources?.DisplayMetrics;
            int height = (int)((metrics?.HeightPixels ?? 1920) * .85);
            dialog.SetContentView(content, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, height));
            dialog.DismissEvent += (_, _) =>
            {
                if (ReferenceEquals(_current, content)) _current = null;
                session.Apply();
                session.Dispose();
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
/// 设置 BottomSheet 的视图树：顶部把手 + 标题栏 + 可滚动内容；根页为五分类入口，子页在同一 sheet 内切换。
/// 所有控件颜色/圆角显式写出，不依赖 OEM 主题派生属性。
/// </summary>
internal sealed class AndroidSettingsSheetView : FrameLayout
{
    private readonly Activity _activity;
    private readonly SettingsViewModel _source;
    private readonly NativeSettingsSession _session;
    private readonly Action _dismiss;
    private readonly LinearLayout _content;
    private readonly TextView _title;
    private readonly TextView _back;
    private readonly TextView _persistenceStatus;
    private readonly List<(TextView Label, Func<string> Text)> _dynamicTexts = [];
    private string _page = "root";
    private bool _closed;

    /// <summary>当前工作设置副本（验收读取关闭回写后的值）。</summary>
    internal SettingsViewModel Settings => _session.WorkingSettings;
    /// <summary>当前页面标识（root/files/preview/control/exif/ai）。</summary>
    internal string Page => _page;

    /// <summary>建立 sheet 视图树。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="source">源设置 ViewModel（持久化状态与工作副本的提交目标）。</param>
    /// <param name="session">设置工作副本会话。</param>
    /// <param name="dismiss">关闭回调。</param>
    public AndroidSettingsSheetView(Activity activity, SettingsViewModel source,
        NativeSettingsSession session, Action dismiss) : base(activity)
    {
        _activity = activity;
        _source = source;
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

        // SETTINGS-14：读取/保存失败在设置页顶部显示一行可见状态。
        _persistenceStatus = Text("", 12, AndroidUiTheme.ErrorText);
        _persistenceStatus.SetPadding((int)(16 * density), (int)(6 * density),
            (int)(16 * density), (int)(6 * density));
        root.AddView(_persistenceStatus, new LinearLayout.LayoutParams(
            LayoutParams.MatchParent, LayoutParams.WrapContent));
        _source.PropertyChanged += OnSourceChanged;
        RefreshPersistenceStatus();

        var scroll = new ScrollView(activity) { FillViewport = true };
        scroll.SetBackgroundColor(Color.Transparent);
        _content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _content.SetPadding((int)(16 * density), (int)(4 * density), (int)(16 * density), (int)(24 * density));
        scroll.AddView(_content, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 0, 1));

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

    /// <summary>关闭并避免重复应用。</summary>
    private void Dismiss()
    {
        if (_closed) return;
        _closed = true;
        _session.Apply();
        _dismiss();
    }

    /// <summary>源设置持久化状态变化时刷新顶部状态行。</summary>
    private void OnSourceChanged(object? sender, global::System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.PersistenceStatus)) Post(RefreshPersistenceStatus);
    }

    /// <summary>显示或隐藏设置页顶部的持久化失败状态行。</summary>
    private void RefreshPersistenceStatus()
    {
        string? status = _source.PersistenceStatus;
        _persistenceStatus.Text = status ?? "";
        _persistenceStatus.Visibility = string.IsNullOrWhiteSpace(status)
            ? ViewStates.Gone
            : ViewStates.Visible;
    }

    /// <summary>释放时解除源设置订阅。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.PropertyChanged -= OnSourceChanged;
        base.Dispose(disposing);
    }

    /// <summary>重建页面内容。</summary>
    /// <param name="page">页面标识。</param>
    private void BuildPage(string page)
    {
        _page = page;
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

    /// <summary>根页：五个分类入口（与 iOS 设置根页同序）。</summary>
    private void BuildRootPage()
    {
        Add(_content, CategoryRow("文件", "同名合并、文件格式与缓存预加载", "files"));
        Add(_content, CategoryRow("预览", "缩放指示器与缩放比例预设", "preview"));
        Add(_content, CategoryRow("控制", "布局模式与控制栏显示", "control"));
        Add(_content, CategoryRow("EXIF", "EXIF 显示项与评分写回策略", "exif"));
        Add(_content, CategoryRow("AI", "相似聚类阈值与即时计算", "ai"));
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
        Add(_content, Section("缓存与预加载", null,
            StepperRow("原生解码线程", null, () => Settings.NativePreloadParallelism,
                value => Settings.NativePreloadParallelism = value, 1, () => Settings.NativePreloadParallelismMaximum,
                1, value => $"{value} 线程"),
            StepperRow("软件解码线程", null, () => Settings.CpuPreloadParallelism,
                value => Settings.CpuPreloadParallelism = value, 1, () => Settings.CpuPreloadParallelismMaximum,
                1, value => $"{value} 线程"),
            StepperRow("缓存内存上限", null, () => Settings.BitmapCacheMaxMemory,
                value => Settings.BitmapCacheMaxMemory = value, 512, () => 32768, 512, value => $"{value} MB"),
            StepperRow("缓存数量上限", null, () => Settings.BitmapCacheMaxCount,
                value => Settings.BitmapCacheMaxCount = value, 1, () => 400, 10, value => $"{value} 张"),
            StepperRow("下一张预载数量", null, () => Settings.PreloadForwardCount,
                value => Settings.PreloadForwardCount = value, 0, () => Settings.PreloadMaximum, 1, value => $"{value} 张"),
            StepperRow("上一张预载数量", null, () => Settings.PreloadBackwardCount,
                value => Settings.PreloadBackwardCount = value, 0, () => Settings.PreloadMaximum, 1, value => $"{value} 张"),
            StepperRow("列表滚动预载数量", null, () => Settings.VisibleCenterPreloadCount,
                value => Settings.VisibleCenterPreloadCount = value, 0, () => Settings.PreloadMaximum, 1, value => $"{value} 张"),
            StepperRow("滚动预载延时", null, () => Settings.VisibleCenterDelayMs,
                value => Settings.VisibleCenterDelayMs = value, 100, () => 5000, 100, value => $"{value} ms")));
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
        var rows = new List<View>();
        var presets = Settings.ScalePresets;
        for (int index = 0; index < presets.Count; index++)
        {
            var preset = presets[index];
            rows.Add(ScalePresetRow(preset));
        }
        rows.Add(TextButtonRow("＋ 添加缩放比例", () =>
        {
            var preset = new SettingsViewModel.ScalePreset("100");
            EditScalePreset(preset, isNew: true);
        }));
        Add(_content, Section("缩放比例预设", "点击数值编辑，单位为百分比（100 = 物理 100%）。", Card(rows)));
    }

    /// <summary>控制页：布局模式与控制栏功能（显隐/排序）。</summary>
    private void BuildControlPage()
    {
        Add(_content, Section("控制栏布局位置", null,
            NavigationRow("布局模式", null, CurrentLayoutModeName(), ShowLayoutModePicker)));
        var hotkeys = new AndroidSettingsReorderList(_activity,
            () => Settings.Hotkeys.Count,
            index =>
            {
                var item = Settings.Hotkeys[index];
                string primary = HotkeyBinding(item.PrimaryHotkeyText, item.HasPrimaryConflict);
                string secondary = HotkeyBinding(item.SecondaryHotkeyText, item.HasSecondaryConflict);
                // SETTINGS-01：移动端只读显示主/次绑定与冲突；冲突用文本表达，不只换颜色。
                return new AndroidSettingsReorderList.SettingsReorderItem(
                    item.Name, $"主 {primary} · 次 {secondary}",
                    () => item.IsDisplay, value => item.IsDisplay = value,
                    $"{item.Name}，{(item.IsDisplay ? "已启用" : "未启用")}，主绑定 {primary}，次绑定 {secondary}");
            },
            (from, to) => Settings.MoveHotkey(from, to));
        Add(_content, SectionView("控制栏功能",
            "勾选决定是否显示在控制栏；拖动手柄排序，轻点手柄可用上移/下移菜单。", hotkeys));
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

    /// <summary>快捷键副标题中的绑定文本；冲突在同一绑定后标注（SETTINGS-11）。</summary>
    /// <param name="text">绑定显示文本。</param>
    /// <param name="conflict">该绑定是否冲突。</param>
    /// <returns>带冲突标记的文本。</returns>
    private static string HotkeyBinding(string text, bool conflict) => conflict ? $"{text}（冲突）" : text;

    /// <summary>缩放预设行：数值、编辑、删除；只剩一项时删除无效。</summary>
    private View ScalePresetRow(SettingsViewModel.ScalePreset preset)
    {
        var row = Row();
        var text = Text($"{preset.Display}", 15, AndroidUiTheme.TextPrimary);
        text.Clickable = true;
        text.Click += (_, _) => EditScalePreset(preset, isNew: false);
        bool canRemove = Settings.CanRemoveScalePreset;
        var delete = Button("−", 18, () =>
        {
            Settings.RemoveScalePreset(preset);
            BuildPage("preview");
        });
        delete.Enabled = canRemove;
        delete.Alpha = canRemove ? 1f : .35f;
        delete.ContentDescription = $"删除缩放比例 {preset.Text}%";
        row.AddView(text, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(delete, new LinearLayout.LayoutParams(ButtonSize(), ButtonSize()));
        return row;
    }

    /// <summary>编辑/新增缩放预设；百分比必须 &gt;0 且 ≤1600，非法输入不写入并恢复原值（SETTINGS-07）。</summary>
    private void EditScalePreset(SettingsViewModel.ScalePreset preset, bool isNew)
    {
        string original = preset.Text;
        var input = new EditText(_activity)
        {
            Text = original,
            InputType = InputTypes.ClassNumber | InputTypes.NumberFlagDecimal,
        };
        input.SetTextColor(AndroidUiTheme.TextPrimary);
        input.SetHintTextColor(AndroidUiTheme.TextSecondary);
        input.SetSingleLine(true);
        var builder = new AlertDialog.Builder(_activity);
        builder.SetTitle(isNew ? "添加缩放比例" : "编辑缩放比例");
        builder.SetView(input);
        builder.SetPositiveButton("确定", (_, _) => { });
        builder.SetNegativeButton("取消", (_, _) => { });
        var dialog = builder.Create();
        if (dialog == null) return;
        dialog.Show();
        var positive = dialog.GetButton((int)DialogButtonType.Positive);
        if (positive == null) return;
        positive.Click += (_, _) =>
        {
            // SETTINGS-07：输入必须 > 0 且 ≤ 1600%；非法时不写入，提示一次并恢复原值。
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !SettingsViewModel.IsValidScalePresetPercent(value))
            {
                Toast.MakeText(_activity, "请输入大于 0 且不超过 1600 的数字", ToastLength.Short)?.Show();
                input.Text = original;
                input.SetSelection(input.Text?.Length ?? 0);
                return;
            }
            preset.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            if (isNew) Settings.ScalePresets.Add(preset);
            Settings.ApplyScalePreset();
            dialog.Dismiss();
            BuildPage("preview");
        };
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
