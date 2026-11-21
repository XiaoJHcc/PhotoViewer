using AppKit;
using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// STATE-05：当前文件夹不可用时，在主图适应区底部显示持续状态「文件夹不可用」与原生文字按钮「重新打开…」。
/// 底色、字号、内边距与圆角取 STATE-03 提示条令牌；不抢焦点、不进入模态，只有按钮本身接收鼠标，
/// 其余区域照常把点击与拖动交给主图。
/// </summary>
internal sealed class MacFolderUnavailableBar : NSView
{
    /// <summary>单行测量时使用的高度上限。</summary>
    private const double MeasureLimit = 10000;

    private readonly NSTextField _label;
    private readonly NSButton _button;

    /// <summary>用户点击「重新打开…」。</summary>
    internal event Action? ReopenRequested;

    /// <summary>当前是否可见，供验收读取。</summary>
    internal bool IsVisible => !Hidden;
    /// <summary>状态文本，供验收读取。</summary>
    internal string Text => _label.StringValue;
    /// <summary>「重新打开…」原生文字按钮，供验收核对文案与可用态。</summary>
    internal NSButton ActionButton => _button;

    /// <summary>建立深色圆角状态条与只读文本、文字按钮。</summary>
    public MacFolderUnavailableBar()
    {
        WantsLayer = true;
        Layer!.BackgroundColor = MacDesignColors.Argb(
            DesignTokens.ColorStatusToastBackground, DesignTokens.ColorStatusToastBackgroundAlpha).CGColor;
        Layer.CornerRadius = (nfloat)DesignTokens.StatusToastCornerRadius;
        Layer.MasksToBounds = true;
        Hidden = true;

        _label = new PassThroughLabel
        {
            Editable = false,
            Selectable = false,
            Bordered = false,
            DrawsBackground = false,
            Alignment = NSTextAlignment.Left,
            TextColor = MacDesignColors.Argb(DesignTokens.ColorStatusToastForeground),
            Font = NSFont.SystemFontOfSize((nfloat)DesignTokens.StatusToastFontSize) ?? throw new InvalidOperationException("系统字体不可用"),
            LineBreakMode = NSLineBreakMode.TruncatingTail,
            MaximumNumberOfLines = 1,
            StringValue = StatusMessages.FolderUnavailable,
        };
        _button = new NSButton
        {
            Bordered = false,
            AttributedTitle = ActionTitle(),
        };
        ((NSView)_button).AccessibilityLabel = StatusMessages.ReopenFolder;
        _button.Activated += (_, _) => ReopenRequested?.Invoke();
        AddSubview(_label);
        AddSubview(_button);
    }

    /// <summary>顶边为原点的布局坐标。</summary>
    public override bool IsFlipped => true;

    /// <summary>状态条只做展示，不成为键盘焦点。</summary>
    public override bool AcceptsFirstResponder() => false;

    /// <summary>只有文字按钮参与命中测试，其余点击与拖动穿透到主图。</summary>
    public override NSView? HitTest(CGPoint aPoint)
    {
        if (Hidden) return null;
        var hit = base.HitTest(aPoint);
        return ReferenceEquals(hit, this) ? null : hit;
    }

    /// <summary>
    /// 在主图适应区底部落位：最大宽 500、水平居中、距底 24；「计算中」药丸可见时整体上移到药丸上方，
    /// 与提示条避让同一口径。高度取文本与按钮中较高者加内边距。
    /// </summary>
    /// <param name="photoBounds">主图适应区当前边界（窗口坐标）。</param>
    /// <param name="stackOverBusyPill">「计算中」药丸是否可见。</param>
    internal void Layout(CGRect photoBounds, bool stackOverBusyPill)
    {
        _button.SizeToFit();
        var buttonSize = _button.Frame.Size;
        double buttonWidth = Math.Ceiling((double)buttonSize.Width);
        double maxWidth = Math.Max(1, Math.Min(DesignTokens.StatusToastMaxWidth, (double)photoBounds.Width));
        double contentWidth = NaturalLabelWidth() + DesignTokens.StatusToastStackGap + buttonWidth;
        double width = Math.Min(maxWidth, Math.Ceiling(contentWidth + DesignTokens.StatusToastPaddingX * 2));
        double labelWidth = Math.Max(1, width - DesignTokens.StatusToastPaddingX * 2 - DesignTokens.StatusToastStackGap - buttonWidth);
        double labelHeight = LabelHeight(labelWidth);
        double rowHeight = Math.Max(labelHeight, (double)buttonSize.Height);
        double height = rowHeight + DesignTokens.StatusToastPaddingY * 2;
        double bottom = stackOverBusyPill
            ? DesignTokens.ImageBusyBottomOffset + DesignTokens.ImageBusyHeight + DesignTokens.StatusToastStackGap
            : DesignTokens.StatusToastBottomMargin;
        Frame = new CGRect(
            (double)photoBounds.X + ((double)photoBounds.Width - width) / 2,
            (double)photoBounds.Y + Math.Max(0, (double)photoBounds.Height - bottom - height),
            width,
            height);
        _label.Frame = new CGRect(
            DesignTokens.StatusToastPaddingX,
            DesignTokens.StatusToastPaddingY + (rowHeight - labelHeight) / 2,
            labelWidth,
            labelHeight);
        _button.Frame = new CGRect(
            width - DesignTokens.StatusToastPaddingX - buttonWidth,
            DesignTokens.StatusToastPaddingY + (rowHeight - (double)buttonSize.Height) / 2,
            buttonWidth,
            (double)buttonSize.Height);
    }

    /// <summary>标签的自然宽度（单行、不换行）。</summary>
    private double NaturalLabelWidth()
        => _label.Cell == null ? 0 : Math.Ceiling((double)_label.Cell.CellSize.Width);

    /// <summary>标签在给定宽度下的单行高度。</summary>
    private double LabelHeight(double width)
    {
        var cell = _label.Cell;
        if (cell == null) return 0;
        return Math.Ceiling((double)cell.CellSizeForBounds(new CGRect(0, 0, (nfloat)width, MeasureLimit)).Height);
    }

    /// <summary>「重新打开…」按钮文案：系统强调色，字号与提示条一致。</summary>
    private static NSAttributedString ActionTitle()
    {
        using var paragraph = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
        using var attributes = new NSDictionary(
            NSStringAttributeKey.Font, NSFont.SystemFontOfSize((nfloat)DesignTokens.StatusToastFontSize, NSFontWeight.Medium)
                ?? throw new InvalidOperationException("系统字体不可用"),
            NSStringAttributeKey.ForegroundColor, NSColor.SystemBlue,
            NSStringAttributeKey.ParagraphStyle, paragraph);
        return new NSAttributedString(StatusMessages.ReopenFolder, attributes);
    }

    /// <summary>状态文字不拦截鼠标。</summary>
    private sealed class PassThroughLabel : NSTextField
    {
        /// <summary>点击穿透到按钮或下方的视图。</summary>
        public override NSView? HitTest(CGPoint aPoint) => null;
    }

    /// <summary>释放文本与按钮。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _label.Dispose();
            _button.Dispose();
        }
        base.Dispose(disposing);
    }
}
