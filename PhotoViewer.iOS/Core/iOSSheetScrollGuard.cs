using System.Runtime.CompilerServices;
using CoreFoundation;
using UIKit;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 移动端半屏 sheet 内容区滚动守卫（SETTINGS-04）：
/// 区分“内容滚动”与“到顶后再下拉关闭”——当用户在滚动视图尚未位于顶部边界时起手下滑（或本次手势中曾经把内容滚离顶部），
/// 拦截当前手势触发的 sheet 下拉关闭，确保用户意图是滚动页面时不会误关；
/// 当滚动视图已经位于顶部边界、用户再次向下拉动（或拖动顶部把手/导航栏、点击外部蒙层）时，放行系统原生关闭。
/// 仅使用公开 <see cref="UINavigationControllerDelegate"/> 与 <see cref="UIPresentationController"/> 代理路径。
/// </summary>
internal sealed class iOSSheetScrollGuard : UINavigationControllerDelegate
{
    private const double TopTolerance = 1.0;

    private readonly ConditionalWeakTable<UIPanGestureRecognizer, object> _observed = [];
    private bool _blockDismissForCurrentPan;
    private int _panGeneration;

    /// <summary>导航控制器切页后登记当前页内的全部滚动容器。</summary>
    /// <param name="navigationController">所属导航控制器。</param>
    /// <param name="viewController">当前展示页。</param>
    /// <param name="animated">是否带过渡动画。</param>
    public override void DidShowViewController(
        UINavigationController navigationController,
        UIViewController viewController,
        bool animated)
    {
        Attach(viewController);
    }

    /// <summary>为目标页及其子树内的所有 <see cref="UIScrollView"/> 挂接一次平移状态监听。</summary>
    /// <param name="viewController">目标控制器。</param>
    internal void Attach(UIViewController? viewController)
    {
        if (viewController?.ViewIfLoaded is { } root)
        {
            AttachSubtree(root);
        }
    }

    /// <summary>
    /// 判定当前交互式关闭是否放行：
    /// 仅当本次滚动手势起手时内容不在顶部（或手势途中滚离了顶部）才返回 false；
    /// 已在顶部再次下拉、拖动把手/导航栏或点按外部蒙层均返回 true。
    /// </summary>
    /// <param name="topViewController">当前可见页。</param>
    /// <returns>允许关闭时返回 true。</returns>
    internal bool ShouldAllowDismiss(UIViewController? topViewController)
        => !_blockDismissForCurrentPan && !HasNonTopActiveScrollPan(topViewController?.ViewIfLoaded);

    /// <summary>判定滚动视图当前是否已停在顶部边界（允许再次下拉关闭 sheet）。</summary>
    /// <param name="scroll">目标滚动视图。</param>
    /// <returns>位于顶部或向上越界回弹态时返回 true。</returns>
    internal static bool IsAtTop(UIScrollView scroll)
        => (double)scroll.ContentOffset.Y <= -(double)scroll.AdjustedContentInset.Top + TopTolerance;

    /// <summary>验收缝：模拟一次滚动手势状态变化，核对“未到顶滚动拦截、到顶再下拉放行”。</summary>
    /// <param name="scroll">被拖拽的滚动视图。</param>
    /// <param name="state">手势状态。</param>
    internal void SimulateScrollPanForChecks(UIScrollView scroll, UIGestureRecognizerState state)
        => HandleScrollPan(scroll, state);

    /// <summary>验收缝：复位模拟手势态。</summary>
    internal void ResetForChecks()
    {
        _panGeneration++;
        _blockDismissForCurrentPan = false;
    }

    private void AttachSubtree(UIView view)
    {
        if (view is UIScrollView scroll && scroll.ScrollEnabled)
        {
            var pan = scroll.PanGestureRecognizer;
            if (!_observed.TryGetValue(pan, out _))
            {
                _observed.Add(pan, new object());
                pan.AddTarget(() =>
                {
                    if (pan.View is UIScrollView owner)
                    {
                        HandleScrollPan(owner, pan.State);
                    }
                });
            }
        }

        foreach (var child in view.Subviews)
        {
            AttachSubtree(child);
        }
    }

    private void HandleScrollPan(UIScrollView scroll, UIGestureRecognizerState state)
    {
        switch (state)
        {
            case UIGestureRecognizerState.Began:
                _panGeneration++;
                // 未在顶部边界起手：用户意图是滚动页面，本次手势全程不允许误关半屏。
                // 已在顶部边界起手：放行再次下拉关闭（若随后反向滚离顶部，Changed 中再置拦截）。
                _blockDismissForCurrentPan = !IsAtTop(scroll);
                break;
            case UIGestureRecognizerState.Changed:
                if (!IsAtTop(scroll))
                {
                    _panGeneration++;
                    _blockDismissForCurrentPan = true;
                }
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                int generation = ++_panGeneration;
                DispatchQueue.MainQueue.DispatchAsync(() =>
                {
                    if (_panGeneration == generation)
                    {
                        _blockDismissForCurrentPan = false;
                    }
                });
                break;
        }
    }

    private static bool HasNonTopActiveScrollPan(UIView? view)
    {
        if (view == null)
        {
            return false;
        }

        if (view is UIScrollView scroll && scroll.ScrollEnabled && !IsAtTop(scroll) &&
            (scroll.Tracking || scroll.Dragging ||
             scroll.PanGestureRecognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed))
        {
            return true;
        }

        foreach (var child in view.Subviews)
        {
            if (HasNonTopActiveScrollPan(child))
            {
                return true;
            }
        }

        return false;
    }
}
