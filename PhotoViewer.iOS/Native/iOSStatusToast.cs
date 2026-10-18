using CoreGraphics;
using Foundation;
using PhotoViewer.Core.Design;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// STATE-03 操作结果提示条：主图区底部单行/换行文本，自动消失，新提示替换旧提示并重置计时。
/// 纯展示、不接收触摸，不改变主图手势与焦点。
/// </summary>
internal sealed class iOSStatusToast : UIView
{
    /// <summary>测量文本自然尺寸时的上限（避免单行测量被压缩）。</summary>
    private const double MeasureLimit = 10000;

    private readonly UILabel _label = new();
    private NSTimer? _timer;

    /// <summary>当前提示文本（空表示未显示）。</summary>
    internal string? Message => _label.Text;

    /// <summary>提示条当前是否可见。</summary>
    internal bool IsVisible => !Hidden;

    /// <summary>建立固定深色提示条外框与文本标签。</summary>
    internal iOSStatusToast() : base(CGRect.Empty)
    {
        BackgroundColor = iOSColors.FromToken(DesignTokens.ColorStatusToastBackground, DesignTokens.ColorStatusToastBackgroundAlpha);
        Layer.CornerRadius = (nfloat)DesignTokens.StatusToastCornerRadius;
        ClipsToBounds = true;
        UserInteractionEnabled = false;
        Hidden = true;

        _label.Font = UIFont.SystemFontOfSize((nfloat)DesignTokens.StatusToastFontSize)!;
        _label.TextColor = iOSColors.FromToken(DesignTokens.ColorStatusToastForeground);
        _label.TextAlignment = UITextAlignment.Center;
        _label.Lines = 0;
        _label.LineBreakMode = UILineBreakMode.WordWrap;
        _label.BackgroundColor = UIColor.Clear;
        AddSubview(_label);
    }

    /// <summary>
    /// 显示一条提示；已有提示立即被替换，自动消失计时从头开始。
    /// </summary>
    /// <param name="message">提示文本。</param>
    internal void Show(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _label.Text = message;
        Hidden = false;
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = NSTimer.CreateScheduledTimer(DesignTokens.MotionStatusToastDuration / 1000.0, false, _ => Hide());
        SetNeedsLayout();
    }

    /// <summary>立即收起提示并停止计时。</summary>
    internal void Hide()
    {
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = null;
        Hidden = true;
    }

    /// <summary>
    /// 在给定宽度上限内量出提示条的显示尺寸：单行优先，超过最大宽后换行。
    /// </summary>
    /// <param name="maxWidth">可用宽度上限（已含提示条自身内边距）。</param>
    /// <returns>提示条尺寸。</returns>
    internal CGSize Measure(double maxWidth)
    {
        double limit = Math.Max(1, maxWidth - DesignTokens.StatusToastPaddingX * 2);
        double natural = Math.Ceiling((double)_label.SizeThatFits(new CGSize(MeasureLimit, MeasureLimit)).Width);
        double width = Math.Min(maxWidth, natural + DesignTokens.StatusToastPaddingX * 2);
        double textWidth = Math.Max(1, width - DesignTokens.StatusToastPaddingX * 2);
        double height = Math.Ceiling((double)_label.SizeThatFits(new CGSize(textWidth, MeasureLimit)).Height)
            + DesignTokens.StatusToastPaddingY * 2;
        return new CGSize(width, height);
    }

    /// <summary>文本标签按令牌内边距铺满提示条。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _label.Frame = new CGRect(DesignTokens.StatusToastPaddingX, DesignTokens.StatusToastPaddingY,
            Math.Max(1, (double)Bounds.Width - DesignTokens.StatusToastPaddingX * 2),
            Math.Max(1, (double)Bounds.Height - DesignTokens.StatusToastPaddingY * 2));
    }

    /// <summary>释放自动消失计时器。</summary>
    /// <param name="disposing">是否释放托管资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer?.Invalidate();
            _timer?.Dispose();
            _timer = null;
        }
        base.Dispose(disposing);
    }
}
