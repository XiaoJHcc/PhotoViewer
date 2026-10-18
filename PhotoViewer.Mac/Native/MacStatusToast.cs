using AppKit;
using CoreGraphics;
using PhotoViewer.Core.Design;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Mac.Native;

/// <summary>
/// 主图区底部的非模态操作结果提示条（STATE-03）：单行或按词换行，停留
/// <see cref="DesignTokens.MotionStatusToastDuration"/> 后自动消失；新提示替换旧提示并重置计时。
/// 提示条不参与命中测试，也不接受第一响应者，浏览与评分手势不被阻断。
/// </summary>
internal sealed class MacStatusToast : NSView
{
    /// <summary>换行文字测量时使用的高度上限。</summary>
    private const double MeasureLimit = 10000;

    private readonly NSTextField _label;
    private int _version;
    private bool _disposed;

    /// <summary>当前提示是否可见，供验收读取。</summary>
    internal bool IsVisible => !Hidden;
    /// <summary>当前提示文本，供验收读取。</summary>
    internal string Text => _label.StringValue;

    /// <summary>建立深色圆角提示条与只读文本。</summary>
    public MacStatusToast()
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
            LineBreakMode = NSLineBreakMode.ByWordWrapping,
            MaximumNumberOfLines = 0,
        };
        if (_label.Cell is { } cell) cell.Wraps = true;
        AddSubview(_label);
    }

    /// <summary>顶边为原点的布局坐标。</summary>
    public override bool IsFlipped => true;

    /// <summary>提示条只做展示，不成为键盘焦点。</summary>
    public override bool AcceptsFirstResponder() => false;

    /// <summary>不参与命中测试，点击与拖动穿透到主图。</summary>
    public override NSView? HitTest(CGPoint aPoint) => null;

    /// <summary>显示一条提示并重置自动消失计时；空文本直接忽略。</summary>
    /// <param name="message">提示正文。</param>
    internal void Show(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        int version = ++_version;
        _label.StringValue = message;
        ((NSView)_label).AccessibilityLabel = message;
        Hidden = false;
        if (Superview != null) Layout(Superview.Bounds);
        _ = HideAfterDelayAsync(version);
    }

    /// <summary>提示条随主图分区尺寸重新落位：最大宽 500、水平居中、距底 24。</summary>
    /// <param name="photoBounds">主图分区当前边界。</param>
    internal void Layout(CGRect photoBounds)
    {
        if (_disposed) return;
        double width = Math.Min(DesignTokens.StatusToastMaxWidth, Math.Max(1, photoBounds.Width));
        double textWidth = Math.Max(1, width - DesignTokens.StatusToastPaddingX * 2);
        double textHeight = Measure(textWidth);
        double height = textHeight + DesignTokens.StatusToastPaddingY * 2;
        Frame = new CGRect(
            (photoBounds.Width - width) / 2,
            Math.Max(0, photoBounds.Height - DesignTokens.StatusToastBottomMargin - height),
            width,
            height);
        _label.Frame = new CGRect(DesignTokens.StatusToastPaddingX, DesignTokens.StatusToastPaddingY, textWidth, textHeight);
    }

    /// <summary>提示正文在给定宽度下的换行高度。</summary>
    private double Measure(double width)
    {
        var cell = _label.Cell;
        if (cell == null) return 0;
        return (double)cell.CellSizeForBounds(new CGRect(0, 0, (nfloat)width, MeasureLimit)).Height;
    }

    /// <summary>停留时长到达后收起；期间出现新提示则旧计时作废。</summary>
    private async Task HideAfterDelayAsync(int version)
    {
        await Task.Delay(DesignTokens.MotionStatusToastDuration).ConfigureAwait(false);
        UiDispatcher.Post(() =>
        {
            if (_disposed || version != _version) return;
            Hidden = true;
        });
    }

    /// <summary>释放时作废在途计时。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ++_version;
            _label.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>提示文字不拦截鼠标。</summary>
    private sealed class PassThroughLabel : NSTextField
    {
        /// <summary>点击穿透到提示条下方的视图。</summary>
        public override NSView? HitTest(CGPoint aPoint) => null;
    }
}
