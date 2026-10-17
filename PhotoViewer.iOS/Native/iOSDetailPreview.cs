using CoreGraphics;
using UIKit;

namespace PhotoViewer.iOS.Native;

/// <summary>
/// 原生细节预览瓦片（中心 / 对焦点，UIKit 版）。外观完全复用 <see cref="iOSDiagnosticTile"/> 的外框、
/// 内层圆角裁切、左上角药丸角标与 1:1 内容绘制，只额外加上：
/// 触控板 hover（<see cref="UIHoverGestureRecognizer"/>）切换绿色高亮描边并把绿框位置回报给主图；
/// **手指点按在"定格"与"解除"之间切换**（iPad 平台差异，见 [ANALYSIS-01] 的 iOS 差异）——
/// 定格后离开也不会消失，再点一次或由宿主清空即解除；
/// 双击请求主图定位到裁剪中心并放大到 100%。
/// 缩放按 1:1 呈现（裁剪边长 = 瓦片边长，与旧壳 <c>DetailPreview</c> 的 1:1 细节口径一致）。
/// 位图由调用方传入并持有，本视图只引用不释放。
/// </summary>
internal sealed class iOSDetailPreview : UIView
{
    private readonly iOSDiagnosticTile _tile = new();
    private readonly UIView _overlay;
    private readonly UITapGestureRecognizer _single;
    private readonly UITapGestureRecognizer _double;
    private readonly UIHoverGestureRecognizer _hover;
    private double _centerU = .5, _centerV = .5;
    private double? _frameWidth, _frameHeight;
    private bool _hovering;
    private bool _frozen;

    /// <summary>双击内容区：请求主图定位到裁剪中心并放大（参数为归一化中心）。</summary>
    internal Action<double, double>? FocusRequested { get; set; }

    /// <summary>高亮状态变化：参数依次为是否高亮、归一化中心、对焦框归一化宽高（无对焦框时为 null）。</summary>
    internal Action<bool, double, double, double?, double?>? HighlightChanged { get; set; }

    /// <summary>手指点按是否已定格绿框。</summary>
    internal bool IsFrozen => _frozen;

    /// <summary>当前是否显示绿色描边（hover 或定格）。</summary>
    internal bool IsHighlighted => !_overlay.Hidden;

    /// <summary>构造：方形内容按 1:1 呈现，叠加层只画高亮描边。</summary>
    public iOSDetailPreview()
    {
        _tile.AspectRatio = 1;
        AddSubview(_tile);

        _overlay = new UIView { UserInteractionEnabled = false, Hidden = true };
        _overlay.Layer.BorderColor = UIColor.FromRGB(0, 255, 0).CGColor;
        _overlay.Layer.BorderWidth = 1;
        _overlay.Layer.CornerRadius = 6;
        AddSubview(_overlay);

        // 单指点按 = 定格/解除；双击 = 主图定位。单点必须等双击失败，否则双击会先触发一次定格。
        _single = new UITapGestureRecognizer(OnSingleTap) { NumberOfTapsRequired = 1 };
        _double = new UITapGestureRecognizer(OnDoubleTap) { NumberOfTapsRequired = 2 };
        _single.RequireGestureRecognizerToFail(_double);
        AddGestureRecognizer(_single);
        AddGestureRecognizer(_double);

        // 触控板/鼠标悬停：进入即联动主图绿框，离开撤销（定格态除外）。
        _hover = new UIHoverGestureRecognizer(OnHover);
        AddGestureRecognizer(_hover);
    }

    /// <summary>左上角药丸角标；空则隐藏。</summary>
    internal string? CornerLabel
    {
        get => _tile.CornerLabel;
        set => _tile.CornerLabel = value;
    }

    /// <summary>设置裁剪内容（已按显示朝向裁好的 1:1 方块）；null 清空。引用比较，相同实例不重绘。</summary>
    /// <param name="image">由调用方持有的位图。</param>
    internal void SetContent(UIImage? image) => _tile.Source = image;

    /// <summary>当前是否已贴上裁剪内容（代理期按旧壳口径留空）。</summary>
    internal bool HasContent => _tile.HasContent;

    /// <summary>设置裁剪中心与对焦框（归一化）；用于双击定位与主图绿框。</summary>
    /// <param name="u">归一化中心横坐标。</param>
    /// <param name="v">归一化中心纵坐标。</param>
    /// <param name="frameWidth">对焦框归一化宽；null 表示使用裁剪区。</param>
    /// <param name="frameHeight">对焦框归一化高。</param>
    internal void SetCenter(double u, double v, double? frameWidth, double? frameHeight)
    {
        _centerU = u;
        _centerV = v;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
    }

    /// <summary>分析栏隐藏或切图时清除高亮（hover 与定格一并清除，同时清掉主图绿框）。</summary>
    internal void ClearHighlight()
    {
        _hovering = false;
        _frozen = false;
        ApplyHighlight();
    }

    /// <summary>尺寸变化时让内部瓦片与描边层铺满本视图。</summary>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        _tile.Frame = Bounds;
        _overlay.Frame = Bounds;
        _overlay.Layer.CornerRadius = 6;
    }

    /// <summary>手指点按入口（识别器与验收共用）：切换定格态并同步绿框。</summary>
    internal void ToggleFrozen()
    {
        _frozen = !_frozen;
        ApplyHighlight();
    }

    /// <summary>按 hover/定格状态刷新描边并回报主图绿框；状态不变时不重复回报。</summary>
    private void ApplyHighlight()
    {
        bool visible = _hovering || _frozen;
        if (_overlay.Hidden == visible)
        {
            _overlay.Hidden = !visible;
            HighlightChanged?.Invoke(visible, _centerU, _centerV, _frameWidth, _frameHeight);
        }
    }

    /// <summary>单指点按：定格/解除绿框。</summary>
    /// <param name="recognizer">点击识别器。</param>
    private void OnSingleTap(UITapGestureRecognizer recognizer) => ToggleFrozen();

    /// <summary>双击：请求主图按裁剪中心放大到 100%。</summary>
    /// <param name="recognizer">双击识别器。</param>
    private void OnDoubleTap(UITapGestureRecognizer recognizer) => FocusRequested?.Invoke(_centerU, _centerV);

    /// <summary>悬停状态变化：进入时高亮，离开时在非定格态撤销。</summary>
    /// <param name="recognizer">hover 识别器。</param>
    private void OnHover(UIHoverGestureRecognizer recognizer)
    {
        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began:
            case UIGestureRecognizerState.Changed:
                _hovering = true;
                ApplyHighlight();
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                _hovering = false;
                ApplyHighlight();
                break;
        }
    }
}
