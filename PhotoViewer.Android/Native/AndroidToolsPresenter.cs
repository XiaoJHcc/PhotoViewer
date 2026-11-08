using System;
using System.Collections.Generic;
using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Google.Android.Material.BottomSheet;
using PhotoViewer.Core.AI;
using PhotoViewer.Core;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Image;
using PhotoViewer.Shared;
using PhotoViewer.ViewModels.Main;
using PhotoViewer.ViewModels.Tools;

namespace PhotoViewer.Android.Native;

/// <summary>
/// Android 原生工具页（A5）：半屏 BottomSheet，列表页只有 EXIF 详情与按版本提供的诊断页（照片数据统计为 Windows 专属）；
/// 子页在同一个 sheet 内切换，导航状态写共享 <see cref="ToolsViewModel"/>，关闭后对象保留可重开。
/// </summary>
public sealed class AndroidToolsPresenter
{
    private readonly Activity _activity;
    private readonly MainViewModel _main;
    private BottomSheetDialog? _dialog;
    private static AndroidToolsSheetView? _current;
    private static BottomSheetDialog? _currentDialog;

    /// <summary>绑定宿主与主 VM。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">共享主 VM。</param>
    public AndroidToolsPresenter(Activity activity, MainViewModel main)
    {
        _activity = activity;
        _main = main;
    }

    /// <summary>当前工具页是否打开。</summary>
    public static bool IsOpen => _current != null;

    /// <summary>当前展示页标识（验收用）：root / exif / diagnostic；未打开为 null。</summary>
    internal static string? CurrentPageForChecks => _current?.Page;

    /// <summary>当前工具浮层视图与对话框（验收读取初始 sheet 状态与位置）。</summary>
    internal static AndroidToolsSheetView? CurrentSheetForChecks => _current;
    internal static BottomSheetDialog? CurrentDialogForChecks => _currentDialog;

    /// <summary>返回键优先在工具页内回上级；已处理时返回 true。</summary>
    /// <returns>是否消费返回。</returns>
    public static bool HandleBack()
    {
        var current = _current;
        if (current == null) return false;
        if (current.HandleBack()) return true;
        current.RequestDismiss();
        return true;
    }

    /// <summary>展示/前置工具页并明确进入 EXIF 详情（TOOLBAR-06）；已打开其他子页时也切到 EXIF。</summary>
    /// <param name="tools">共享工具 VM；调用前已由共享层选定 EXIF 或回退状态。</param>
    public void ShowOrActivateExif(ToolsViewModel tools)
    {
        // TOOLS-02：无可用 EXIF 时分组列表回列表页。
        if (!tools.IsExifDetailEnabled)
            tools.ShowList();
        if (_dialog?.IsShowing == true)
        {
            tools.IsPresented = true;
            // 已打开的 sheet 不会因共享导航状态变化自动换页，需按当前页重建内容。
            _current?.RefreshCurrentPage();
            return;
        }
        ShowOrActivate(tools);
    }

    /// <summary>打开或前置工具页；已打开时保持当前子页。</summary>
    /// <param name="tools">共享工具 VM。</param>
    public void ShowOrActivate(ToolsViewModel tools)
    {
        if (_dialog?.IsShowing == true)
        {
            tools.IsPresented = true;
            return;
        }
        try
        {
            var content = new AndroidToolsSheetView(_activity, _main, tools, Dismiss);
            var dialog = new BottomSheetDialog(_activity, Resource.Style.Theme_PhotoViewer_BottomSheet);
            var metrics = _activity.Resources?.DisplayMetrics;
            int screenHeight = metrics?.HeightPixels ?? 1920;
            dialog.SetContentView(content, new ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, AndroidSheetPresentation.ContentHeight(screenHeight)));
            dialog.DismissEvent += (_, _) =>
            {
                if (ReferenceEquals(_current, content)) _current = null;
                if (ReferenceEquals(_currentDialog, dialog)) _currentDialog = null;
                // TOOLS-01/TOOLS-06：关闭浮层即取消在途诊断任务，主窗口入口据此回到首页。
                tools.IsPresented = false;
                tools.Deactivate();
            };
            // SETTINGS-04：Show 之前配置初始收起的半屏位置，避免先按内容高度展开再闪回收起。
            AndroidSheetPresentation.ConfigureInitialCollapsed(content, screenHeight);
            dialog.Show();
            _dialog = dialog;
            _current = content;
            _currentDialog = dialog;
            tools.IsPresented = true;
            Console.WriteLine($"[Native] 工具页已打开：{tools.CurrentTool?.GetType().Name ?? "列表"}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] 工具页打开失败：{exception}");
        }
    }

    /// <summary>关闭工具页。</summary>
    private void Dismiss()
    {
        var dialog = _dialog;
        _dialog = null;
        dialog?.Dismiss();
    }
}

/// <summary>工具页视图树：顶部把手/标题/返回/关闭 + 可滚动内容；根页两入口，子页在本 sheet 内。</summary>
internal sealed class AndroidToolsSheetView : FrameLayout
{
    private readonly Activity _activity;
    private readonly MainViewModel _main;
    private readonly ToolsViewModel _tools;
    private readonly Action _dismiss;
    private readonly LinearLayout _content;
    private readonly AndroidSheetScrollView _scroll;
    private readonly AndroidSheetViewport _viewport;
    private readonly TextView _title;
    private readonly TextView _back;
    private bool _disposed;

    /// <summary>当前页面标识（root/exif/diagnostic）。</summary>
    internal string Page => _tools.CurrentTool switch
    {
        ExifDetailViewModel => "exif",
        _ when ReferenceEquals(_tools.CurrentTool, _tools.DiagnosticDebug) => "diagnostic",
        _ => "root",
    };

    /// <summary>验收：内容滚动宿主（半屏可见高度与长页底部可达断言）。</summary>
    internal AndroidSheetScrollView ScrollHostForChecks => _scroll;

    /// <summary>构建工具页视图。</summary>
    /// <param name="activity">宿主 Activity。</param>
    /// <param name="main">主 VM。</param>
    /// <param name="tools">工具 VM。</param>
    /// <param name="dismiss">关闭回调。</param>
    public AndroidToolsSheetView(Activity activity, MainViewModel main, ToolsViewModel tools, Action dismiss) : base(activity)
    {
        _activity = activity;
        _main = main;
        _tools = tools;
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
        root.AddView(handle, new LinearLayout.LayoutParams((int)(36 * density), (int)(4 * density))
        {
            Gravity = GravityFlags.CenterHorizontal,
            TopMargin = (int)(8 * density),
            BottomMargin = (int)(4 * density),
        });

        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding((int)(12 * density), 0, (int)(12 * density), 0);
        header.Clickable = true;
        header.ImportantForAccessibility = ImportantForAccessibility.No;
        _back = Button("‹", 26, () => { if (!HandleBack()) Dismiss(); });
        _back.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);
        _title = Text("工具", 17, AndroidUiTheme.TextPrimary, bold: true);
        _title.Gravity = GravityFlags.Center;
        var close = Button("✕", 20, Dismiss);
        header.AddView(_back, new LinearLayout.LayoutParams((int)(44 * density), (int)(48 * density)));
        header.AddView(_title, new LinearLayout.LayoutParams(0, (int)(48 * density), 1));
        header.AddView(close, new LinearLayout.LayoutParams((int)(44 * density), (int)(48 * density)));
        root.AddView(header, new LinearLayout.LayoutParams(LayoutParams.MatchParent, (int)(48 * density)));

        // SETTINGS-04：内容区手势只滚动内容，不能把 sheet 拖下或收起；拖动关闭只由把手/标题承担。
        var scroll = new AndroidSheetScrollView(activity);
        _scroll = scroll;
        _content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _content.SetPadding((int)(16 * density), (int)(4 * density), (int)(16 * density), (int)(24 * density));
        scroll.AddView(_content, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 0, 1));

        BuildPage();
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

    /// <summary>返回上一页；已在根页时返回 false。</summary>
    internal bool HandleBack()
    {
        if (_tools.CurrentTool == null) return false;
        _tools.ShowList();
        BuildPage();
        return true;
    }

    /// <summary>请求关闭。</summary>
    internal void RequestDismiss() => Dismiss();

    /// <summary>按共享导航状态重建当前页（已展示时 EXIF 入口切页；不改变滚动/返回栈语义）。</summary>
    internal void RefreshCurrentPage() => BuildPage();

    private void Dismiss()
    {
        if (_disposed) return;
        _disposed = true;
        _viewport.Detach();
        _dismiss();
    }

    /// <summary>按共享 ToolsViewModel 当前页重建内容。</summary>
    private void BuildPage()
    {
        _content.RemoveAllViews();
        _back.Visibility = _tools.CurrentTool == null ? ViewStates.Invisible : ViewStates.Visible;
        switch (_tools.CurrentTool)
        {
            case ExifDetailViewModel exif:
                _title.Text = "EXIF 详情";
                BuildExifPage(exif);
                break;
            case var tool when ReferenceEquals(tool, _tools.DiagnosticDebug):
                _title.Text = AndroidAppCapabilities.DiagnosticName;
                BuildDiagnosticPage();
                break;
            default:
                _title.Text = "工具";
                BuildRootPage();
                break;
        }
    }

    /// <summary>根页两个入口（照片数据统计为 Windows 专属，不建入口；与设置页同款分组卡片）。</summary>
    private void BuildRootPage()
    {
        var rows = new List<View>
        {
            Category("EXIF 详情", "查看当前照片的完整元数据，可在同名 RAW/JPG 间切换", () =>
            {
                _tools.OpenExifDetail();
                BuildPage();
            }),
            Category(AndroidAppCapabilities.DiagnosticName,
#if PHOTOVIEWER_DINO
                "PCA-RGB / Cosine / 锐度 / 抖动拖影与刚体拟合",
#else
                "锐度 / 抖动拖影与刚体拟合",
#endif
                () =>
            {
                _tools.OpenDiagnostic();
                BuildPage();
            }),
        };
        Add(Card(rows));
    }

    /// <summary>EXIF 详情页：格式切换 + 分组表格（ID / 名称 / 中文名 / 值）。</summary>
    private void BuildExifPage(ExifDetailViewModel exif)
    {
        var formats = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        formats.SetGravity(GravityFlags.CenterVertical);
        formats.SetPadding(0, 0, 0, (int)AndroidUiTheme.Dp(_activity, 8));
        formats.AddView(Text(exif.BaseFileName, 14, AndroidUiTheme.TextPrimary), new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        foreach (var entry in exif.Formats)
        {
            var button = Text(entry.Label, 12, entry.IsSelected ? Color.White : AndroidUiTheme.TextSecondary);
            button.Gravity = GravityFlags.Center;
            var background = new GradientDrawable();
            background.SetColor(entry.IsSelected ? AndroidUiTheme.Accent : AndroidUiTheme.ControlFill);
            background.SetCornerRadius((float)AndroidUiTheme.Dp(_activity, 6));
            button.Background = background;
            button.SetPadding((int)AndroidUiTheme.Dp(_activity, 10), (int)AndroidUiTheme.Dp(_activity, 6),
                (int)AndroidUiTheme.Dp(_activity, 10), (int)AndroidUiTheme.Dp(_activity, 6));
            button.Clickable = true;
            var captured = entry;
            button.Click += (_, _) => { exif.SelectedFormat = captured; BuildPage(); };
            var parameters = new LinearLayout.LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent)
            {
                LeftMargin = (int)AndroidUiTheme.Dp(_activity, 6),
            };
            formats.AddView(button, parameters);
        }
        Add(formats);

        if (exif.IsLoading || exif.Groups == null)
        {
            Add(Text("载入中…", 13, AndroidUiTheme.TextSecondary));
            return;
        }
        foreach (var group in exif.Groups)
        {
            Add(SectionHeader(group.Name));
            var card = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
            var background = new GradientDrawable();
            background.SetColor(Color.ParseColor("#262626"));
            background.SetCornerRadius((float)AndroidUiTheme.Dp(_activity, 8));
            card.Background = background;
            for (int index = 0; index < group.Tags.Count; index++)
            {
                if (index > 0) card.AddView(Divider());
                card.AddView(TagRow(group.Tags[index]));
            }
            Add(card);
        }
    }

    /// <summary>单条 EXIF 行：ID 60dp + 名称/中文名 2:2 + 值 3，值最多 3 行尾部截断。</summary>
    private View TagRow(MetadataTag tag)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        double density = AndroidUiTheme.Density(_activity);
        // TOOLS-03：单元格内边距与 ID 列宽取设计令牌。
        int paddingX = (int)AndroidUiTheme.Dp(_activity, DesignTokens.ExiftableCellPaddingX);
        int paddingY = (int)AndroidUiTheme.Dp(_activity, DesignTokens.ExiftableCellPaddingY);
        row.SetPadding(paddingX, paddingY, paddingX, paddingY);
        var id = Text(tag.TagIdHex, 9, AndroidUiTheme.TextSecondary);
        id.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        id.SetSingleLine(true);
        row.AddView(id, new LinearLayout.LayoutParams(
            (int)AndroidUiTheme.Dp(_activity, DesignTokens.ExiftableIdWidth), LayoutParams.WrapContent));
        var name = Text(tag.Name, 11, AndroidUiTheme.TextPrimary);
        name.SetTextIsSelectable(true);
        row.AddView(name, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 2));
        var chinese = Text(tag.ChineseName ?? "", 11, AndroidUiTheme.TextSecondary);
        chinese.SetTextIsSelectable(true);
        row.AddView(chinese, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 2));
        var value = Text(tag.Value, 12, AndroidUiTheme.TextPrimary);
        value.SetMaxLines(ExifTableGeometry.ValueMaxLines);
        value.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        // TOOLS-03：值列可长按选择复制。
        value.SetTextIsSelectable(true);
        row.AddView(value, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 3));
        return row;
    }

    /// <summary>诊断页：AI 版五瓦片，标准版三瓦片，刚体拟合文本保持在网格下方。</summary>
    private void BuildDiagnosticPage()
    {
#if PHOTOVIEWER_DINO
        var presenter = _tools.DinoPresenter;
#else
        var presenter = _tools.CvPresenter;
#endif
        var status = Text(presenter.Status, 12, AndroidUiTheme.TextSecondary);
        status.SetPadding(0, 0, 0, (int)AndroidUiTheme.Dp(_activity, 10));
        Add(status);
        if (presenter.IsBusy) Add(Text("计算中…", 12, AndroidUiTheme.TextSecondary));
        var grid = new AndroidDiagnosticGridView(_activity, presenter);
        var height = (int)((_content.Width > 0 ? _content.Width : Resources?.DisplayMetrics?.WidthPixels ?? 1080) * .62);
        Add(grid, height);
        var rigid = Text(presenter.RigidMotionText, 12, AndroidUiTheme.TextPrimary);
        rigid.SetPadding(0, (int)AndroidUiTheme.Dp(_activity, 12), 0, 0);
        rigid.SetTextIsSelectable(true);
        Add(rigid);
        presenter.Changed -= OnDiagnosticChanged;
        presenter.Changed += OnDiagnosticChanged;
        presenter.CrosshairChanged -= OnDiagnosticChanged;
        presenter.CrosshairChanged += OnDiagnosticChanged;
    }

    private void OnDiagnosticChanged()
    {
        if (ReferenceEquals(_tools.CurrentTool, _tools.DiagnosticDebug)) Post(BuildPage);
    }

    private void Add(View view, int? height = null)
    {
        var parameters = new LinearLayout.LayoutParams(LayoutParams.MatchParent, height ?? LayoutParams.WrapContent);
        parameters.BottomMargin = (int)AndroidUiTheme.Dp(_activity, 8);
        _content.AddView(view, parameters);
    }

    private View Category(string title, string subtitle, Action action)
    {
        var row = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        int paddingLeft = (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidCardPaddingX);
        int paddingRight = (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidRowPaddingRight);
        int paddingY = (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidRowPaddingY);
        row.SetPadding(paddingLeft, paddingY, paddingRight, paddingY);
        row.SetMinimumHeight((int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidRowMinHeight));
        row.Clickable = true;
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        column.AddView(Text(title, 16, AndroidUiTheme.TextPrimary, bold: true));
        column.AddView(Text(subtitle, 12, AndroidUiTheme.TextSecondary));
        var chevron = Text("›", 22, AndroidUiTheme.TextSecondary);
        chevron.Gravity = GravityFlags.Center;
        row.AddView(column, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(chevron, new LinearLayout.LayoutParams(
            (int)AndroidUiTheme.Dp(_activity, DesignTokens.SettingsAndroidNavigationChevronWidth),
            LayoutParams.WrapContent));
        row.Click += (_, _) => action();
        return row;
    }

    /// <summary>圆角卡片 + 行间 1dp 分隔线，与设置页分类列表同一外观。</summary>
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
                var divider = Divider();
                card.AddView(divider, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 1));
            }
            card.AddView(rows[index], new LinearLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        }
        return card;
    }

    private View SectionHeader(string title)
    {
        var text = Text(title, 13, AndroidUiTheme.TextSecondary, bold: true);
        text.SetPadding(0, (int)AndroidUiTheme.Dp(_activity, 10), 0, (int)AndroidUiTheme.Dp(_activity, 4));
        return text;
    }

    private View Divider()
    {
        var divider = new View(_activity);
        divider.SetBackgroundColor(Color.ParseColor("#333333"));
        return divider;
    }

    private TextView Text(string? value, double sizeSp, Color color, bool bold = false)
    {
        var view = new TextView(_activity)
        {
            Text = value,
            TextSize = (float)sizeSp,
            Typeface = bold ? Typeface.Create(Typeface.Default, TypefaceStyle.Bold) : Typeface.Default,
        };
        view.SetTextColor(color);
        return view;
    }

    private TextView Button(string label, double sizeSp, Action action)
    {
        var text = Text(label, sizeSp, AndroidUiTheme.TextPrimary);
        text.Gravity = GravityFlags.Center;
        text.Clickable = true;
        text.Click += (_, _) => action();
        return text;
    }
}

/// <summary>诊断瓦片网格（3 列），自绘位图、角标、说明与准星，点按回传归一化坐标。</summary>
internal sealed class AndroidDiagnosticGridView : View
{
#if PHOTOVIEWER_DINO
    private readonly DinoDiagnosticPresenter _presenter;
#else
    private readonly CvDiagnosticPresenter _presenter;
#endif
    private readonly Paint _fill;
    private readonly Paint _border;
    private readonly Paint _label;
    private readonly Paint _caption;
    private readonly Paint _crosshair;
    private readonly Dictionary<AnalysisTile, RectF> _frames = [];

#if PHOTOVIEWER_DINO
    public AndroidDiagnosticGridView(Context context, DinoDiagnosticPresenter presenter) : base(context)
#else
    public AndroidDiagnosticGridView(Context context, CvDiagnosticPresenter presenter) : base(context)
#endif
    {
        _presenter = presenter;
        _fill = AndroidUiTheme.Paint(Color.ParseColor("#1E1E1E")!);
        _border = AndroidUiTheme.Paint(Color.ParseColor("#444444")!, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        _label = AndroidUiTheme.TextPaint(context, 10, Color.White, bold: true);
        _caption = AndroidUiTheme.TextPaint(context, 9, Color.ParseColor("#AAAAAA")!);
        _crosshair = AndroidUiTheme.Paint(Color.Argb(220, 255, 255, 255)!, Math.Max(1, AndroidUiTheme.Dp(context, 1)));
        SetWillNotDraw(false);
        Clickable = true;
    }

    /// <summary>按 3 列排布当前版本的诊断瓦片。</summary>
    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float density = (float)AndroidUiTheme.Density(Context!);
        float margin = 4 * density;
        float gap = 8 * density;
        float width = (Width - margin * 2 - gap * 2) / 3;
        float y = margin;
        _frames.Clear();
        var tiles = _presenter.Tiles;
        for (int row = 0; row < 2; row++)
        {
            float height = width * 0.82f;
            for (int column = 0; column < 3; column++)
            {
                int index = row * 3 + column;
                if (index >= tiles.Count) break;
                var tile = tiles[index];
                var rect = new RectF(margin + column * (width + gap), y, margin + column * (width + gap) + width, y + height);
                _frames[tile] = rect;
                AndroidUiTheme.DrawRounded(canvas, rect, 6 * density, Color.ParseColor("#1E1E1E")!,
                    Color.ParseColor("#444444")!, Math.Max(1, density));
                var content = new RectF(rect.Left + 2 * density, rect.Top + 20 * density,
                    rect.Right - 2 * density, rect.Bottom - 18 * density);
                var bitmap = BitmapFor(tile);
                if (bitmap != null)
                {
                    float scale = Math.Min(content.Width() / bitmap.Width, content.Height() / bitmap.Height);
                    float w = bitmap.Width * scale, h = bitmap.Height * scale;
                    var dst = new RectF(content.CenterX() - w / 2, content.CenterY() - h / 2,
                        content.CenterX() + w / 2, content.CenterY() + h / 2);
                    using var paint = new Paint(PaintFlags.FilterBitmap) { FilterBitmap = true };
                    canvas.DrawBitmap(bitmap, new Rect(0, 0, bitmap.Width, bitmap.Height), dst, paint);
                }
                if (_presenter.CrosshairU is { } u && _presenter.CrosshairV is { } v)
                {
                    float cx = content.Left + (float)u * content.Width();
                    float cy = content.Top + (float)v * content.Height();
                    canvas.DrawLine(content.Left, cy, content.Right, cy, _crosshair);
                    canvas.DrawLine(cx, content.Top, cx, content.Bottom, _crosshair);
                }
                AndroidUiTheme.DrawCentered(canvas, tile.ShortLabel, _label, rect.CenterX(),
                    rect.Top + 14 * density, rect.Width() - 4 * density);
                AndroidUiTheme.DrawCentered(canvas, tile.Caption, _caption, rect.CenterX(),
                    rect.Bottom - 5 * density, rect.Width() - 4 * density);
            }
            y += height + gap;
        }
    }

    private Bitmap? BitmapFor(AnalysisTile tile)
    {
        var source = tile.Source;
        if (source == null) return null;
        return AndroidBitmapConverter.FromDecodedImage($"diagnostic-{source.GetHashCode()}-{source.Width}x{source.Height}", source);
    }

    /// <summary>点按瓦片：落准星并交给当前诊断 presenter 处理。</summary>
    /// <param name="e">事件。</param>
    /// <returns>消费。</returns>
    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.ActionMasked != MotionEventActions.Up) return true;
        float density = (float)AndroidUiTheme.Density(Context!);
        float x = e.GetX(), y = e.GetY();
        foreach (var entry in _frames)
        {
            if (!entry.Value.Contains(x, y)) continue;
            var content = new RectF(entry.Value.Left + 2 * density, entry.Value.Top + 20 * density,
                entry.Value.Right - 2 * density, entry.Value.Bottom - 18 * density);
            double u = Math.Clamp((x - content.Left) / Math.Max(1, content.Width()), 0, 1);
            double v = Math.Clamp((y - content.Top) / Math.Max(1, content.Height()), 0, 1);
            _presenter.OnTileClicked(u, v);
            return true;
        }
        return true;
    }
}
