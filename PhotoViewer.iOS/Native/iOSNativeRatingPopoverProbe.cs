using CoreGraphics;
using UIKit;

namespace PhotoViewer.iOS.Native;

internal static class iOSNativeRatingPopoverProbe
{
    internal static void Capture(iOSNativeRootViewController root, UIPopoverPresentationController presentation,
        string stage, List<string> lines)
    {
        var content = presentation.PresentedViewController.View!;
        var container = presentation.ContainerView!;
        lines.Add($"SOURCE {stage}: opening={root.RatingSourcesPopover.IsOpening}; itemType={presentation.SourceItem?.GetType().FullName ?? "null"}; " +
            $"sourceView={presentation.SourceView}; sourceRect={presentation.SourceRect}");
        var anchorInContainer = presentation.SourceItem is UIView sourceView
            ? sourceView.ConvertRectToView(sourceView.Bounds, container)
            : presentation.SourceView?.ConvertRectToView(presentation.SourceRect, container) ?? CGRect.Empty;
        var contentInContainer = content.ConvertRectToView(content.Bounds, container);
        lines.Add($"GEOMETRY {stage}: anchor={anchorInContainer}; preferred={content.Bounds.Size}; " +
            $"presentedFrame={presentation.FrameOfPresentedViewInContainerView}; " +
            $"presentedInContainer={presentation.PresentedView?.ConvertRectToView(presentation.PresentedView.Bounds, container)}; " +
            $"contentInContainer={contentInContainer}; arrow={presentation.ArrowDirection}; layoutMargins={presentation.PopoverLayoutMargins}");
        lines.Add($"GEOMETRY {stage}: contentFrame={content.Frame}; safe={content.SafeAreaInsets}; " +
            $"additionalSafe={presentation.PresentedViewController.AdditionalSafeAreaInsets}; " +
            $"layoutMargins={content.LayoutMargins}; directionalMargins={content.DirectionalLayoutMargins}; " +
            $"preservesSuperviewMargins={content.PreservesSuperviewLayoutMargins}; " +
            $"insetsMarginsFromSafeArea={content.InsetsLayoutMarginsFromSafeArea}");
        if (presentation.PresentedView is { } presented)
        {
            for (UIView? view = presented; view != null; view = view.Superview)
            {
                lines.Add($"VIEW {stage}: {view.Class.Name}; frame={view.Frame}; " +
                    $"inContainer={view.ConvertRectToView(view.Bounds, container)}; safe={view.SafeAreaInsets}; " +
                    $"clips={view.ClipsToBounds}; background={view.BackgroundColor}");
                if (ReferenceEquals(view, container)) break;
            }
            DescribeChildren(presented, container, stage, lines, 0);
        }
    }

    private static void DescribeChildren(UIView parent, UIView container, string stage, List<string> lines, int depth)
    {
        if (depth >= 6) return;
        foreach (var child in parent.Subviews)
        {
            lines.Add($"CHILD {stage}: {new string(' ', depth * 2)}{child.Class.Name}; " +
                $"frame={child.Frame}; bounds={child.Bounds}; inContainer={child.ConvertRectToView(child.Bounds, container)}; " +
                $"safe={child.SafeAreaInsets}; clips={child.ClipsToBounds}; background={child.BackgroundColor}");
            DescribeChildren(child, container, stage, lines, depth + 1);
        }
    }
}
