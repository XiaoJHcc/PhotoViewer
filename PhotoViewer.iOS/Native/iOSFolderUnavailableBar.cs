using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Platform;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// STATE-05：当前文件夹不可用时，在主图适应区底部显示持续状态「文件夹不可用」与原生文字按钮「重新打开…」。
/// 底色、字号、内边距与圆角取 STATE-03 提示条令牌；不抢焦点、不进入模态，只有按钮本身接收触摸，
/// 其余区域把点按、轻扫与捏合交给主图。
/// </summary>
internal sealed class iOSFolderUnavailableBar : UIView
{
    /// <summary>测量自然尺寸时的上限。</summary>
    private const double MeasureLimit = 10000;

    private readonly UILabel _label = new();
    private readonly UIButton _button = new(UIButtonType.System);

    /// <summary>用户点击「重新打开…」。</summary>
    internal event Action? ReopenRequested;

    /// <summary>当前是否可见，供验收读取。</summary>
    internal bool IsVisible => !Hidden;
    /// <summary>状态文本，供验收读取。</summary>
    internal string Text => _label.Text ?? string.Empty;
    /// <summary>「重新打开…」原生文字按钮，供验收核对文案与可用态。</summary>
    internal UIButton ActionButton => _button;

    /// <summary>建立深色圆角状态条、只读文本与文字按钮。</summary>
    internal iOSFolderUnavailableBar() : base(CGRect.Empty)
    {
        BackgroundColor = iOSColors.FromToken(
            DesignTokens.ColorStatusToastBackground, DesignTokens.ColorStatusToastBackgroundAlpha);
        Layer.CornerRadius = (nfloat)DesignTokens.StatusToastCornerRadius;
        ClipsToBounds = true;
        Hidden = true;

        _label.Text = StatusMessages.FolderUnavailable;
        _label.Font = UIFont.SystemFontOfSize((nfloat)DesignTokens.StatusToastFontSize)!;
        _label.TextColor = iOSColors.FromToken(DesignTokens.ColorStatusToastForeground);
        _label.Lines = 1;
        _label.LineBreakMode = UILineBreakMode.TailTruncation;
        _label.BackgroundColor = UIColor.Clear;
        AddSubview(_label);

        _button.SetTitle(StatusMessages.ReopenFolder, UIControlState.Normal);
        _button.SetTitleColor(UIColor.SystemBlue, UIControlState.Normal);
        _button.TitleLabel.Font = UIFont.SystemFontOfSize((nfloat)DesignTokens.StatusToastFontSize, UIFontWeight.Medium)!;
        _button.TitleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        _button.TouchUpInside += (_, _) => ReopenRequested?.Invoke();
        AddSubview(_button);
    }

    /// <summary>只有文字按钮参与命中测试，其余触摸穿透到主图。</summary>
    /// <param name="point">本视图坐标中的点。</param>
    /// <param name="uievent">触发触摸的事件。</param>
    /// <returns>命中的子视图；空白处为 null。</returns>
    public override UIView? HitTest(CGPoint point, UIEvent? uievent)
    {
        if (Hidden) return null;
        var hit = base.HitTest(point, uievent);
        return ReferenceEquals(hit, this) ? null : hit;
    }

    /// <summary>
    /// 在给定宽度上限内量出状态条尺寸：文本与文字按钮同行，高度取较高者加令牌内边距。
    /// </summary>
    /// <param name="maxWidth">可用宽度上限（含状态条自身内边距）。</param>
    /// <returns>状态条尺寸。</returns>
    internal CGSize Measure(double maxWidth)
    {
        var buttonSize = ButtonSize();
        double natural = Math.Ceiling((double)_label.SizeThatFits(new CGSize(MeasureLimit, MeasureLimit)).Width);
        double width = Math.Min(maxWidth, Math.Ceiling(natural + DesignTokens.StatusToastStackGap
            + (double)buttonSize.Width + DesignTokens.StatusToastPaddingX * 2));
        double textWidth = Math.Max(1, width - DesignTokens.StatusToastPaddingX * 2
            - DesignTokens.StatusToastStackGap - (double)buttonSize.Width);
        double textHeight = Math.Ceiling((double)_label.SizeThatFits(new CGSize(textWidth, MeasureLimit)).Height);
        double rowHeight = Math.Max(textHeight, (double)buttonSize.Height);
        return new CGSize(width, rowHeight + DesignTokens.StatusToastPaddingY * 2);
    }

    /// <summary>文本与按钮按令牌内边距铺满状态条，文字按钮保持自然尺寸。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var buttonSize = ButtonSize();
        double rowHeight = Math.Max(1, (double)Bounds.Height - DesignTokens.StatusToastPaddingY * 2);
        double textWidth = Math.Max(1, (double)Bounds.Width - DesignTokens.StatusToastPaddingX * 2
            - DesignTokens.StatusToastStackGap - (double)buttonSize.Width);
        double textHeight = Math.Ceiling((double)_label.SizeThatFits(new CGSize(textWidth, MeasureLimit)).Height);
        _label.Frame = new CGRect(
            DesignTokens.StatusToastPaddingX,
            DesignTokens.StatusToastPaddingY + (rowHeight - textHeight) / 2,
            textWidth,
            textHeight);
        _button.Frame = new CGRect(
            (double)Bounds.Width - DesignTokens.StatusToastPaddingX - (double)buttonSize.Width,
            DesignTokens.StatusToastPaddingY + (rowHeight - (double)buttonSize.Height) / 2,
            (double)buttonSize.Width,
            (double)buttonSize.Height);
    }

    /// <summary>文字按钮的自然尺寸。</summary>
    private CGSize ButtonSize() => _button.SizeThatFits(new CGSize(MeasureLimit, MeasureLimit));
}
