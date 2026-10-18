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
/// Android 原生工具页（A5）：半屏 BottomSheet，列表页只有 EXIF 详情 / DINO 诊断（照片数据统计为 Windows 专属）；
/// 子页在同一个 sheet 内切换，导航状态写共享 <see cref="ToolsViewModel"/>，关闭后对象保留可重开。
/// </summary>
public sealed class AndroidToolsPresenter
{
    private readonly Activity _activity;
    private readonly MainViewModel _main;
    private BottomSheetDialog? _dialog;
    private static AndroidToolsSheetView? _current;

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
            int height = (int)((metrics?.HeightPixels ?? 1920) * .9);
            dialog.SetContentView(content, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, height));
            dialog.DismissEvent += (_, _) =>
            {
                if (ReferenceEquals(_current, content)) _current = null;
                // TOOLS-01/TOOLS-06：关闭浮层即取消在途 DINO 任务，主窗口入口据此回到首页。
                tools.IsPresented = false;
                tools.Deactivate();
            };
            dialog.Show();
            if (content.Parent is View parent)
            {
                var behavior = BottomSheetBehavior.From(parent);
                if (behavior != null)
                {
                    behavior.PeekHeight = (int)((metrics?.HeightPixels ?? 1920) * .55);
                    behavior.SkipCollapsed = false;
                    behavior.State = BottomSheetBehavior.StateExpanded;
                }
            }
            _dialog = dialog;
            _current = content;
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
    private readonly TextView _title;
    private readonly TextView _back;
    private bool _disposed;

    /// <summary>当前页面标识（root/exif/dino）。</summary>
    internal string Page => _tools.CurrentTool switch
    {
        ExifDetailViewModel => "exif",
        DinoDebugViewModel => "dino",
        _ => "root",
    };

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
        root.AddView(handle, new LinearLayout.LayoutParams((int)(36 * density), (int)(4 * density))
        {
            Gravity = GravityFlags.CenterHorizontal,
            TopMargin = (int)(8 * density),
        });

        var header = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding((int)(12 * density), 0, (int)(12 * density), 0);
        _back = Button("‹", 26, () => { if (!HandleBack()) Dismiss(); });
        _title = Text("工具", 17, AndroidUiTheme.TextPrimary, bold: true);
        _title.Gravity = GravityFlags.Center;
        var close = Button("✕", 20, Dismiss);
        header.AddView(_back, new LinearLayout.LayoutParams((int)(44 * density), (int)(48 * density)));
        header.AddView(_title, new LinearLayout.LayoutParams(0, (int)(48 * density), 1));
        header.AddView(close, new LinearLayout.LayoutParams((int)(44 * density), (int)(48 * density)));
        root.AddView(header, new LinearLayout.LayoutParams(LayoutParams.MatchParent, (int)(48 * density)));

        var scroll = new ScrollView(activity) { FillViewport = true };
        _content = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        _content.SetPadding((int)(16 * density), (int)(4 * density), (int)(16 * density), (int)(24 * density));
        scroll.AddView(_content, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));
        root.AddView(scroll, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 0, 1));

        BuildPage();
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

    private void Dismiss()
    {
        if (_disposed) return;
        _disposed = true;
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
            case DinoDebugViewModel dino:
                _title.Text = "DINO 诊断";
                BuildDinoPage(dino);
                break;
            default:
                _title.Text = "工具";
                BuildRootPage();
                break;
        }
    }

    /// <summary>根页两个入口（照片数据统计为 Windows 专属，不建入口）。</summary>
    private void BuildRootPage()
    {
        Add(Category("EXIF 详情", "查看当前照片的完整元数据，可在同名 RAW/JPG 间切换", () =>
        {
            _tools.OpenExifDetail();
            BuildPage();
        }));
        Add(Category("DINO 诊断", "PCA-RGB / Cosine / 锐度 / 抖动拖影与刚体拟合", () =>
        {
            _tools.OpenDinoDebug();
            BuildPage();
        }));
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

    /// <summary>DINO 诊断页：状态、5 张诊断瓦片（3+2）、刚体拟合文本。</summary>
    private void BuildDinoPage(DinoDebugViewModel dino)
    {
        var presenter = _tools.DinoPresenter;
        var status = Text(presenter.Status, 12, AndroidUiTheme.TextSecondary);
        status.SetPadding(0, 0, 0, (int)AndroidUiTheme.Dp(_activity, 10));
        Add(status);
        if (presenter.IsBusy) Add(Text("计算中…", 12, AndroidUiTheme.TextSecondary));
        var grid = new AndroidDinoGridView(_activity, presenter);
        var height = (int)((_content.Width > 0 ? _content.Width : Resources?.DisplayMetrics?.WidthPixels ?? 1080) * .62);
        Add(grid, height);
        var rigid = Text(presenter.RigidMotionText, 12, AndroidUiTheme.TextPrimary);
        rigid.SetPadding(0, (int)AndroidUiTheme.Dp(_activity, 12), 0, 0);
        rigid.SetTextIsSelectable(true);
        Add(rigid);
        presenter.Changed -= OnDinoChanged;
        presenter.Changed += OnDinoChanged;
        presenter.CrosshairChanged -= OnDinoChanged;
        presenter.CrosshairChanged += OnDinoChanged;
    }

    private void OnDinoChanged()
    {
        if (_tools.CurrentTool is DinoDebugViewModel) Post(BuildPage);
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
        row.SetPadding(0, (int)AndroidUiTheme.Dp(_activity, 12), 0, (int)AndroidUiTheme.Dp(_activity, 12));
        row.Clickable = true;
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        column.AddView(Text(title, 16, AndroidUiTheme.TextPrimary, bold: true));
        column.AddView(Text(subtitle, 12, AndroidUiTheme.TextSecondary));
        row.AddView(column, new LinearLayout.LayoutParams(0, LayoutParams.WrapContent, 1));
        row.AddView(Text("›", 22, AndroidUiTheme.TextSecondary), new LinearLayout.LayoutParams((int)AndroidUiTheme.Dp(_activity, 44), LayoutParams.WrapContent));
        row.Click += (_, _) => action();
        return row;
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

/// <summary>DINO 诊断瓦片网格（3 列 + 2 行），自绘位图、角标、说明与准星，点按回传归一化坐标。</summary>
internal sealed class AndroidDinoGridView : View
{
    private readonly DinoDiagnosticPresenter _presenter;
    private readonly Paint _fill;
    private readonly Paint _border;
    private readonly Paint _label;
    private readonly Paint _caption;
    private readonly Paint _crosshair;
    private readonly Dictionary<AnalysisTile, RectF> _frames = [];

    public AndroidDinoGridView(Context context, DinoDiagnosticPresenter presenter) : base(context)
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

    /// <summary>按 3 列排布 5 张瓦片并绘制。</summary>
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
        return AndroidBitmapConverter.FromDecodedImage($"dino-{source.GetHashCode()}-{source.Width}x{source.Height}", source);
    }

    /// <summary>点按瓦片：落准星并重算 cosine。</summary>
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
