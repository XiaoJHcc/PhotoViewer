using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.ViewModels.Main;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// EXIF 信息块（TOOLBAR-05 / TOOLBAR-06）：系统深色薄材质衬底的圆角块，块内按设置顺序排 EXIF 项，
/// 每项上标签（次要色）下数值（半粗主色），居中尾部截断。横排时块高取玻璃按钮边长、项目自左向右排；
/// 竖排时块宽为项目宽加两侧内边距、项目自上而下排。整块只有一个点按目标并直达 EXIF 详情，
/// 读屏也只暴露一个按钮元素，值为各项「名称 值」的串联。
/// <para>无衬底（<see cref="Bare"/>，手机横屏控制栏）去掉模糊衬底与块内边距，直接放在栏背景上；
/// <see cref="VisibleCount"/> 限定实际显示的前若干项（横屏放不下时裁掉末尾项，被裁的项仍可从详情查看）。</para>
/// <para>项目尺寸、间距、内边距、整块尺寸与逐项矩形全部取共享 <see cref="ExifBlockGeometry"/>，
/// 槽位挤压时各项等量收窄，本类只把结果落到原生标签上。</para>
/// </summary>
internal sealed class iOSExifBlock : UIView
{
    private readonly UIVisualEffectView _background;
    private readonly List<UILabel> _labels = [];
    private readonly List<UILabel> _values = [];
    private readonly UIFont _labelFont;
    private readonly UIFont _valueFont;
    private IReadOnlyList<ExifDisplayEntry> _entries = Array.Empty<ExifDisplayEntry>();
    private bool _horizontal = true;
    private bool _bare;
    private int _visibleCount = int.MaxValue;

    /// <summary>建立深色模糊衬底、圆角裁切与整块点按识别器。</summary>
    internal iOSExifBlock() : base(CGRect.Empty)
    {
        BackgroundColor = UIColor.Clear;
        _labelFont = UIFont.SystemFontOfSize((nfloat)DesignTokens.ToolbarExifLabelFontSize)!;
        _valueFont = UIFont.SystemFontOfSize((nfloat)DesignTokens.ToolbarExifValueFontSize, UIFontWeight.Semibold)!;
        _background = new UIVisualEffectView(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemThinMaterialDark));
        AddSubview(_background);
        Layer.CornerRadius = (nfloat)ExifBlockGeometry.CornerRadius;
        ClipsToBounds = true;
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        AccessibilityLabel = "EXIF 信息，点击查看详情";
        UserInteractionEnabled = true;
        AddGestureRecognizer(new UITapGestureRecognizer(_ => Tap()));
    }

    /// <summary>项目是否横排（手机竖屏顶行与宽形态行布局为横排，EXIF 列为竖排）。</summary>
    internal bool Horizontal
    {
        get => _horizontal;
        set
        {
            if (_horizontal == value) return;
            _horizontal = value;
            SetNeedsLayout();
        }
    }

    /// <summary>整块点按回调（宿主接到 EXIF 详情入口）。</summary>
    internal Action? Tapped { get; set; }

    /// <summary>无衬底变体：去掉模糊衬底与圆角裁切、项目从块缘起排（手机横屏直接放在控制栏背景上）。</summary>
    internal bool Bare
    {
        get => _bare;
        set
        {
            if (_bare == value) return;
            _bare = value;
            _background.Hidden = value;
            Layer.CornerRadius = value ? 0 : (nfloat)ExifBlockGeometry.CornerRadius;
            SetNeedsLayout();
        }
    }

    /// <summary>实际显示的前若干项（超过这个数量的末尾项隐藏但保留文本，供详情入口与验收读取）。</summary>
    internal int VisibleCount
    {
        get => _visibleCount;
        set
        {
            int count = Math.Max(0, value);
            if (_visibleCount == count) return;
            _visibleCount = count;
            SetNeedsLayout();
        }
    }

    /// <summary>当前实际显示的项数（项数与 <see cref="VisibleCount"/> 取小）。</summary>
    internal int ShownItemCount => Math.Min(_entries.Count, _visibleCount);

    /// <summary>当前各项的标签文本（验收核对项数与顺序）。</summary>
    internal IReadOnlyList<UILabel> ItemLabels => _labels;

    /// <summary>当前各项的数值文本（验收核对内容）。</summary>
    internal IReadOnlyList<UILabel> ItemValues => _values;

    /// <summary>深色模糊衬底视图（验收核对材质）。</summary>
    internal UIVisualEffectView Background => _background;

    /// <summary>点按路径：识别器与验收共用同一入口，直达 EXIF 详情。</summary>
    internal void Tap() => Tapped?.Invoke();

    /// <summary>
    /// 更新块内 EXIF 项：项数不变时只改文本，项数变化时重建标签视图；读屏值与布局随之刷新。
    /// </summary>
    /// <param name="entries">按设置顺序的 EXIF 展示条目。</param>
    internal void SetEntries(IReadOnlyList<ExifDisplayEntry> entries)
    {
        _entries = entries;
        if (_labels.Count != entries.Count) Rebuild();
        else UpdateText();
        UpdateAccessibilityValue();
        SetNeedsLayout();
    }

    /// <summary>铺满衬底，并按共享矩形把每项的标签/数值对在项目内竖直居中。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _background.Frame = Bounds;
        int shown = ShownItemCount;
        double mainLength = Horizontal ? (double)Bounds.Width : (double)Bounds.Height;
        double labelHeight = _labelFont.LineHeight;
        double valueHeight = _valueFont.LineHeight;
        double pairHeight = labelHeight + 1 + valueHeight;
        for (int index = 0; index < _labels.Count; index++)
        {
            bool visible = index < shown;
            _labels[index].Hidden = !visible;
            _values[index].Hidden = !visible;
            if (!visible) continue;
            var rect = ExifBlockGeometry.ItemRect(index, shown, Horizontal, mainLength, Bare);
            double top = rect.Y + (rect.Height - pairHeight) / 2;
            _labels[index].Frame = new CGRect(rect.X, top, rect.Width, labelHeight);
            _values[index].Frame = new CGRect(rect.X, top + labelHeight + 1, rect.Width, valueHeight);
        }
    }

    /// <summary>按当前项数重建标签视图（旧视图先摘下再释放，避免叠在块上）。</summary>
    private void Rebuild()
    {
        foreach (var label in _labels) label.RemoveFromSuperview();
        foreach (var label in _values) label.RemoveFromSuperview();
        foreach (var label in _labels) label.Dispose();
        foreach (var label in _values) label.Dispose();
        _labels.Clear();
        _values.Clear();
        foreach (var entry in _entries)
        {
            var label = TextLabel(entry.DisplayName, _labelFont, UIColor.SecondaryLabel);
            var value = TextLabel(entry.DisplayValue, _valueFont, UIColor.Label);
            _labels.Add(label);
            _values.Add(value);
            AddSubview(label);
            AddSubview(value);
        }
    }

    /// <summary>项数不变的就地刷新：只替换文本，保住现有视图与读屏焦点。</summary>
    private void UpdateText()
    {
        int count = Math.Min(_entries.Count, _labels.Count);
        for (int index = 0; index < count; index++)
        {
            _labels[index].Text = _entries[index].DisplayName;
            _values[index].Text = _entries[index].DisplayValue;
        }
    }

    /// <summary>读屏值：各项「名称 值」按「，」串联。</summary>
    private void UpdateAccessibilityValue()
        => AccessibilityValue = string.Join("，", _entries.Select(entry => $"{entry.DisplayName} {entry.DisplayValue}"));

    /// <summary>生成居中的单行标签：尾部截断、不接收触摸。</summary>
    /// <param name="text">文本。</param>
    /// <param name="font">字号。</param>
    /// <param name="color">文字颜色。</param>
    /// <returns>配置完成的标签。</returns>
    private static UILabel TextLabel(string text, UIFont font, UIColor color) => new(CGRect.Empty)
    {
        Text = text,
        Font = font,
        TextColor = color,
        TextAlignment = UITextAlignment.Center,
        LineBreakMode = UILineBreakMode.TailTruncation,
        BackgroundColor = UIColor.Clear,
        UserInteractionEnabled = false,
    };
}
