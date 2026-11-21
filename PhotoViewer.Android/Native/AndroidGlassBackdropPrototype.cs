using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using PhotoViewer.Android.Backdrop;
using PhotoViewer.Android.Core;

namespace PhotoViewer.Android.Native;

/// <summary>
/// 阶段 0 取样原型（验收专用）：把取样源按效果链画进自己的 <c>OnDraw</c>，并记录每帧心跳。
/// 只在 <c>pv-check glass</c> 运行期间由检查创建，产品路径不引用。
/// </summary>
internal sealed class GlassBackdropSampleView : View
{
    private readonly int[] _location = new int[2];
    private IBackdrop? _source;
    private BackdropEffect _effect = BackdropEffect.None;
    private Color? _tint;

    /// <summary>建一个取样视图。</summary>
    /// <param name="context">宿主上下文。</param>
    internal GlassBackdropSampleView(Context context) : base(context)
    {
    }

    /// <summary>已绘制帧数（心跳计数）。</summary>
    internal int DrawCount { get; private set; }

    /// <summary>是否每帧自动排下一帧重绘（连续取样，用于心跳与实时性测量）。</summary>
    internal bool Continuous { get; set; }

    /// <summary>每帧心跳记录器（可为空）。</summary>
    internal AndroidNativeChecks.GlassFrameIntervalRecorder? Recorder { get; set; }

    /// <summary>
    /// 换取样源。传 <c>null</c> 就是「空取样」负对照：本视图不画任何东西，
    /// 回读到的只会是它下面原有的画面——检查靠这一点区分「没画」与「画对」。
    /// </summary>
    /// <param name="source">取样源；null 表示不画取样。</param>
    internal void SetSource(IBackdrop? source)
    {
        _source = source;
        Invalidate();
    }

    /// <summary>换效果链快照。</summary>
    /// <param name="effect">效果链快照。</param>
    internal void SetEffect(BackdropEffect effect)
    {
        _effect = effect;
        Invalidate();
    }

    /// <summary>设置叠在取样之上的表面色（模拟产品色层；null 表示不叠）。</summary>
    /// <param name="tint">表面色。</param>
    internal void SetTint(Color? tint)
    {
        _tint = tint;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnDraw(Canvas canvas)
    {
        DrawCount++;
        Recorder?.Mark();

        // 取样画的是整层、表面色又要盖满本视图：都必须裁在本视图边界内，
        // 且 save/restore 成对——子视图的 Canvas 裁剪不会自动收回，漏了会污染同层后续绘制。
        int save = canvas.Save();
        try
        {
            canvas.ClipRect(0, 0, Width, Height);
            if (_source is { } source)
            {
                BackdropDrawer.Draw(canvas, source, _effect, BackdropDrawContext.ForView(this, _location));
            }

            if (_tint is { } tint) canvas.DrawColor(tint);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }

        if (Continuous) PostInvalidateOnAnimation();
    }
}

/// <summary>
/// 阶段 0 检查用的临时舞台：把全窗叠加层挂到窗口装饰视图上，提供层容器、实色块、取样视图与
/// 原型主图；<see cref="Dispose"/> 一次性拆除，并把两处会被原型主图改写的进程级状态还原。
/// </summary>
/// <remarks>
/// 原型主图是独立的 <see cref="AndroidPhotoView"/> 实例（不动产品主图），它的构造/渲染器创建会改写
/// <see cref="AndroidMemoryBudgetProvider.ExternalFootprint"/> 与
/// <see cref="AndroidDisplayCapabilities.SurfaceIsP3"/>：前者改成「产品原读数 + 临时主图占用」的组合委托，
/// 后者在拆除时按进入前的读数还原；两份状态都只在仍是自己那份时才收回，不覆盖别处后装的读数。
/// </remarks>
internal sealed class GlassBackdropStage : IDisposable
{
    private readonly Context _context;
    private readonly ViewGroup _overlay;
    private readonly ViewGroup? _decor;
    private readonly List<AndroidPhotoView> _photos = [];
    private readonly List<Func<long>?> _photoFootprints = [];
    private readonly List<LayerBackdropLayout> _layers = [];
    private readonly bool _surfaceWasP3;
    private readonly Func<long>? _productFootprint;
    private Func<long>? _footprint;
    private bool _disposed;

    /// <summary>装配舞台。</summary>
    /// <param name="activity">宿主 Activity。</param>
    internal GlassBackdropStage(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        _context = activity;
        // 原型主图会另建渲染器（改写表面 P3 读数）与覆盖外部占用读数：进入前先留底，拆除时还原。
        _surfaceWasP3 = AndroidDisplayCapabilities.SurfaceIsP3;
        _productFootprint = AndroidMemoryBudgetProvider.ExternalFootprint;

        _overlay = new FrameLayout(activity);
        _overlay.SetBackgroundColor(Color.Argb(255, 0, 0, 0));
        _decor = activity.Window?.DecorView as ViewGroup;
        _decor?.AddView(_overlay, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _overlay.BringToFront();
    }

    /// <summary>叠加层（全窗、黑底，盖住产品画面，保证回读区域只含检查内容）。</summary>
    internal ViewGroup Overlay => _overlay;

    /// <summary>叠加层是否挂到了窗口装饰视图上；false 表示当前窗口无法装配检查 UI。</summary>
    internal bool IsAttached => _decor != null;

    /// <summary>加一层录制容器（<see cref="LayerBackdropLayout"/>），随舞台一起释放。</summary>
    /// <param name="parent">父容器。</param>
    /// <param name="background">层底色。</param>
    /// <param name="frame">叠加层坐标下的矩形；null 表示铺满父容器。</param>
    /// <returns>层容器。</returns>
    internal LayerBackdropLayout AddLayer(ViewGroup parent, Color background, Rect? frame = null)
    {
        var layer = new LayerBackdropLayout(_context);
        layer.SetBackgroundColor(background);
        parent.AddView(layer, frame is { } rect
            ? FrameParameters(rect)
            : new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _layers.Add(layer);
        return layer;
    }

    /// <summary>加一个实色块（录进所在层）。</summary>
    /// <param name="parent">父容器。</param>
    /// <param name="color">颜色。</param>
    /// <param name="frame">叠加层坐标下的矩形。</param>
    /// <returns>实色块视图。</returns>
    internal View AddBlock(ViewGroup parent, Color color, Rect frame)
    {
        var block = new View(_context);
        block.SetBackgroundColor(color);
        parent.AddView(block, FrameParameters(frame));
        return block;
    }

    /// <summary>加一个取样视图。</summary>
    /// <param name="parent">父容器（取样方不得位于它取样的层内）。</param>
    /// <param name="source">取样源。</param>
    /// <param name="frame">叠加层坐标下的矩形。</param>
    /// <param name="tint">叠加的表面色；null 表示不叠。</param>
    /// <returns>取样视图。</returns>
    internal GlassBackdropSampleView AddSampler(ViewGroup parent, IBackdrop source, Rect frame, Color? tint = null)
    {
        var sampler = new GlassBackdropSampleView(_context);
        sampler.SetSource(source);
        if (tint is { } value) sampler.SetTint(value);
        parent.AddView(sampler, FrameParameters(frame));
        return sampler;
    }

    /// <summary>
    /// 加一份原型主图（真实 <see cref="AndroidPhotoView"/> 实例，图像由调用方设置）。
    /// 它的构造会把外部占用读数换成自己的；这里立刻换回「产品原读数 + 临时主图占用」的组合委托。
    /// </summary>
    /// <param name="parent">父容器（通常是照片层）。</param>
    /// <param name="frame">叠加层坐标下的矩形。</param>
    /// <returns>主图视图。</returns>
    internal AndroidPhotoView AddPhoto(ViewGroup parent, Rect frame)
    {
        var photo = new AndroidPhotoView(_context);
        _photos.Add(photo);
        // 构造刚把读数换成这份闭包，先留底再组合，产品的纹理占用才不会从预算里消失。
        _photoFootprints.Add(AndroidMemoryBudgetProvider.ExternalFootprint);
        parent.AddView(photo, FrameParameters(frame));
        InstallFootprint();
        return photo;
    }

    /// <summary>拆除检查 UI：清图、摘视图、释放层与原型主图，并还原两处进程级状态。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var photo in _photos)
        {
            try
            {
                photo.SetImageForChecks(null, int.MinValue);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[NativeCheck] 原型主图清图失败：{exception.GetType().Name}：{exception.Message}");
            }
        }

        _decor?.RemoveView(_overlay);

        foreach (var photo in _photos)
        {
            try
            {
                if (photo.Parent is ViewGroup parent) parent.RemoveView(photo);
                photo.Dispose();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[NativeCheck] 原型主图释放失败：{exception.GetType().Name}：{exception.Message}");
            }
        }
        _photos.Clear();
        _photoFootprints.Clear();

        foreach (var layer in _layers)
        {
            try
            {
                layer.Dispose();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[NativeCheck] 检查层释放失败：{exception.GetType().Name}：{exception.Message}");
            }
        }
        _layers.Clear();

        // 只在读数仍是我们那份组合委托时还原：期间若别处重装过读数，不去覆盖它。
        if (_footprint is { } footprint && ReferenceEquals(AndroidMemoryBudgetProvider.ExternalFootprint, footprint))
        {
            AndroidMemoryBudgetProvider.ExternalFootprint = _productFootprint;
        }
        _footprint = null;

        // 原型主图释放时渲染器会把表面 P3 置为 false；按进入前的读数收回。
        AndroidDisplayCapabilities.SurfaceIsP3 = _surfaceWasP3;
    }

    /// <summary>把外部占用读数换成「产品原读数 + 各原型主图占用」的组合委托。</summary>
    private void InstallFootprint()
    {
        var product = _productFootprint;
        var photos = _photoFootprints.ToArray();
        Func<long> footprint = () =>
        {
            long total = product?.Invoke() ?? 0;
            foreach (var reading in photos) total += reading?.Invoke() ?? 0;
            return total;
        };
        _footprint = footprint;
        AndroidMemoryBudgetProvider.ExternalFootprint = footprint;
    }

    private static FrameLayout.LayoutParams FrameParameters(Rect frame)
        => new(frame.Width(), frame.Height())
        {
            LeftMargin = frame.Left,
            TopMargin = frame.Top,
        };
}
