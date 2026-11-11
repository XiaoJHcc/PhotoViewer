using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 主图视口的适应区/视图区分离：零内边距时与引入适应区之前的公式逐位一致；非零内边距时适应态在适应区内
/// 居中贴边、平移极限落在适应区边缘、视口中心语义（含导航图可见范围）取适应区中心。
/// </summary>
internal static class PhotoViewportChecks
{
    /// <summary>几何比较容差（点、倍率、UV 统一用它）。</summary>
    private const double Tolerance = 1e-6;

    /// <summary>跑零内边距等价与适应区几何两组断言。</summary>
    /// <param name="c">断言收集器。</param>
    public static void Run(Checks c)
    {
        CheckZeroInsets(c);
        CheckFitArea(c);
        CheckFitAreaImageChange(c);
    }

    /// <summary>零内边距：默认与显式清零的内边距完全一致，且关键数值与引入适应区之前的手算值相同。</summary>
    /// <param name="c">断言收集器。</param>
    private static void CheckZeroInsets(Checks c)
    {
        c.Section("视口适应区：零内边距等价");
        PhotoViewport implicitZero = RunLegacySequence(explicitInsets: false);
        PhotoViewport explicitZero = RunLegacySequence(explicitInsets: true);

        c.Equal("默认内边距为零", default(EdgeInsets), implicitZero.FitInsets);
        c.Equal("显式清零后适应区宽等于视图宽", implicitZero.ViewWidth, explicitZero.FitWidth);
        c.Equal("适应倍率一致", implicitZero.FitScale, explicitZero.FitScale);
        c.Equal("缩放一致", implicitZero.Scale, explicitZero.Scale);
        c.Equal("横向平移一致", implicitZero.PanX, explicitZero.PanX);
        c.Equal("纵向平移一致", implicitZero.PanY, explicitZero.PanY);
        c.Equal("适应状态一致", implicitZero.IsFit, explicitZero.IsFit);
        c.Equal("跟随对焦一致", implicitZero.FollowsFocus, explicitZero.FollowsFocus);
        c.Equal("视口中心 UV 一致", implicitZero.CenterUV, explicitZero.CenterUV);
        c.Equal("导航图可见范围一致", implicitZero.NavigationViewport, explicitZero.NavigationViewport);
        c.Equal("图像点视图坐标一致", implicitZero.ViewPoint(.5, .5), explicitZero.ViewPoint(.5, .5));

        // 手算：1000×800 视图、4000×3000 图像 → 适应倍率 0.25；缩放/平移与导航图白框的既有口径。
        Near(c, "适应倍率 0.25", .25, implicitZero.FitScale);
        Near(c, "最终缩放 0.5", .5, implicitZero.Scale);
        Near(c, "中心定位后横向平移钳制到 500", 500, implicitZero.PanX);
        Near(c, "中心定位后纵向平移钳制到 -350", -350, implicitZero.PanY);
        var center = implicitZero.CenterUV;
        Near(c, "视口中心横向 UV 0.25", .25, center.U);
        Near(c, "视口中心纵向 UV 0.733333（-375 被钳到 -350）", 1100d / 1500, center.V);
        var navigation = implicitZero.NavigationViewport;
        Near(c, "导航图可见框 x 0", 0, navigation.X);
        Near(c, "导航图可见框 y 52.5", 52.5, navigation.Y);
        Near(c, "导航图可见框宽 75", 75, navigation.Width);
        Near(c, "导航图可见框高 60", 60, navigation.Height);
        var photoCenter = implicitZero.ViewPoint(.5, .5);
        Near(c, "照片中心横向视图坐标 1000", 1000, photoCenter.X);
        Near(c, "照片中心纵向视图坐标 50", 50, photoCenter.Y);
    }

    /// <summary>非零内边距：适应态居中贴边、放大后的平移极限落在适应区边缘、小轴居中、中心语义与双击锚点。</summary>
    /// <param name="c">断言收集器。</param>
    private static void CheckFitArea(Checks c)
    {
        c.Section("视口适应区：非零内边距");
        var viewport = FitAreaViewport();
        c.Equal("内边距原样记录", Insets, viewport.FitInsets);
        Near(c, "适应区宽 = 视图宽 − 左右内边距", 940, viewport.FitWidth);
        Near(c, "适应区高 = 视图高 − 上下内边距", 550, viewport.FitHeight);
        Near(c, "适应区中心横向偏移 (L−R)/2", 10, viewport.FitCenterX);
        Near(c, "适应区中心纵向偏移 (T−B)/2", -35, viewport.FitCenterY);
        Near(c, "适应倍率按适应区算", 550d / 3000, viewport.FitScale);
        Near(c, "适应态横向平移到适应区中心", 10, viewport.PanX);
        Near(c, "适应态纵向平移到适应区中心", -35, viewport.PanY);

        var fit = PhotoRect(viewport);
        var area = FitRect(viewport);
        c.True("适应态照片在适应区内（四边不越界）",
            fit.Left >= area.Left - Tolerance && fit.Top >= area.Top - Tolerance
            && fit.Right <= area.Right + Tolerance && fit.Bottom <= area.Bottom + Tolerance,
            $"照片 {Format(fit)} 超出适应区 {Format(area)}");
        Near(c, "适应态照片水平居中于适应区", (area.Left + area.Right) / 2, (fit.Left + fit.Right) / 2);
        Near(c, "适应态照片垂直居中于适应区", (area.Top + area.Bottom) / 2, (fit.Top + fit.Bottom) / 2);
        c.True("适应态至少有轴贴边", Near(fit.Top, area.Top) || Near(fit.Bottom, area.Bottom)
            || Near(fit.Left, area.Left) || Near(fit.Right, area.Right),
            $"照片 {Format(fit)} 在适应区 {Format(area)} 内没有贴边");
        var fitCenter = viewport.CenterUV;
        Near(c, "适应态 CenterUV 横向为 0.5", .5, fitCenter.U);
        Near(c, "适应态 CenterUV 纵向为 0.5", .5, fitCenter.V);

        // 放大态：100% 物理倍率下自适应区中心放大，平移极限让照片边缘落在适应区边缘。
        viewport.ZoomToPhysical(1);
        Near(c, "物理 100% 缩放 = 1/BackingScale", .5, viewport.Scale);
        Near(c, "以适应区中心为锚点的缩放不移动照片", 10, viewport.PanX);
        Near(c, "以适应区中心为锚点的缩放不移动照片（纵向）", -35, viewport.PanY);
        viewport.PanBy(1e6, 1e6);
        var topLeft = PhotoRect(viewport);
        Near(c, "拖到极限时照片左边缘对齐适应区左边缘", area.Left, topLeft.Left);
        Near(c, "拖到极限时照片上边缘对齐适应区上边缘", area.Top, topLeft.Top);
        viewport.PanBy(-1e6, -1e6);
        var bottomRight = PhotoRect(viewport);
        Near(c, "拖到另一极限时照片右边缘对齐适应区右边缘", area.Right, bottomRight.Right);
        Near(c, "拖到另一极限时照片下边缘对齐适应区下边缘", area.Bottom, bottomRight.Bottom);

        viewport.FitToView();
        viewport.ZoomToPhysical(1);
        var zoomed = viewport.NavigationViewport;
        Near(c, "放大后导航框横向偏移按适应区算", 39.75, zoomed.X);
        Near(c, "放大后导航框纵向偏移按适应区算", 35.625, zoomed.Y);
        Near(c, "放大后导航框宽 = 适应区宽/显示宽", 70.5, zoomed.Width);
        Near(c, "放大后导航框高 = 适应区高/显示高", 41.25, zoomed.Height);

        // 无锚点缩放（键盘比例缩放）锚定适应区中心：照片中心不动。
        viewport.FitToView();
        viewport.ZoomBy(2, null);
        Near(c, "无锚点缩放锚定适应区中心（横向）", 10, viewport.PanX);
        Near(c, "无锚点缩放锚定适应区中心（纵向）", -35, viewport.PanY);

        // 双击/Fit 无锚点：适应态以适应区中心为不动点放大到物理 100%。
        viewport.FitToView();
        var anchorBefore = viewport.ViewPoint(.5, .5);
        viewport.ToggleFit(null);
        Near(c, "ToggleFit 放大到物理 100%", .5, viewport.Scale);
        var anchorAfter = viewport.ViewPoint(.5, .5);
        Near(c, "ToggleFit 后适应区中心的图像点不动（横向）", anchorBefore.X, anchorAfter.X);
        Near(c, "ToggleFit 后适应区中心的图像点不动（纵向）", anchorBefore.Y, anchorAfter.Y);
        Near(c, "ToggleFit 锚点就是适应区中心（横向）", 510, anchorAfter.X);
        Near(c, "ToggleFit 锚点就是适应区中心（纵向）", 365, anchorAfter.Y);

        // 照片小于适应区的轴（横向贴边、纵向留白）居中于适应区。
        var shortImage = new PhotoViewport();
        shortImage.SetFitInsets(Insets);
        shortImage.ResizeView(1000, 800, 2);
        shortImage.ApplyImage(4000, 1500, 1, 4000);
        Near(c, "短图适应态横向贴边", 10, shortImage.PanX);
        Near(c, "短图小于适应区的轴居中于适应区", -35, shortImage.PanY);
        var shortRect = PhotoRect(shortImage);
        Near(c, "短图垂直居中于适应区", 365, (shortRect.Top + shortRect.Bottom) / 2);
        shortImage.ToggleFit(null);
        Near(c, "短图放大后纵向仍在适应区内居中", -35, shortImage.PanY);
    }

    /// <summary>非零内边距下的切图相对平移：以适应区中心为基准保存与还原。</summary>
    /// <param name="c">断言收集器。</param>
    private static void CheckFitAreaImageChange(Checks c)
    {
        c.Section("视口适应区：切图相对平移");
        var viewport = FitAreaViewport();
        viewport.ToggleFit(null);
        viewport.PanBy(100, 100);
        Near(c, "放大态平移生效", 110, viewport.PanX);
        Near(c, "放大态平移生效（纵向）", 65, viewport.PanY);
        viewport.BeginImageChange();
        viewport.ApplyImage(3000, 2250, 1, 3000);
        Near(c, "切到 3/4 尺寸照片后相对适应区中心的横向偏移按比例缩小", 85, viewport.PanX);
        Near(c, "切到 3/4 尺寸照片后相对适应区中心的纵向偏移按比例缩小", 40, viewport.PanY);
    }

    /// <summary>验收用的非对称内边距：左 40、上 90、右 20、下 160。</summary>
    private static EdgeInsets Insets => new(40, 90, 20, 160);

    /// <summary>1000×800 视图、4000×3000 图像、非对称内边距的适应态视口。</summary>
    /// <returns>已适应到适应区的视口。</returns>
    private static PhotoViewport FitAreaViewport()
    {
        var viewport = new PhotoViewport();
        viewport.SetFitInsets(Insets);
        viewport.ResizeView(1000, 800, 2);
        viewport.ApplyImage(4000, 3000, 1, 4000);
        return viewport;
    }

    /// <summary>
    /// 一段代表性操作序列：缩放视图 → 上屏 → 适应 → 比例放大 → 拖动 → 切回适应 → 手势写入 → 导航图定位。
    /// 默认内边距与显式 <see cref="PhotoViewport.SetFitInsets"/> 清零走同一条序列，用于证明两者结果逐位相同。
    /// </summary>
    /// <param name="explicitInsets">是否显式写入全零内边距。</param>
    /// <returns>序列结束后的视口。</returns>
    private static PhotoViewport RunLegacySequence(bool explicitInsets)
    {
        var viewport = new PhotoViewport();
        if (explicitInsets) viewport.SetFitInsets(new EdgeInsets(0, 0, 0, 0));
        viewport.ResizeView(1000, 800, 2);
        viewport.ApplyImage(4000, 3000, 1, 4000);
        viewport.FitToView();
        viewport.ZoomBy(2, null);
        viewport.PanBy(300, 200);
        viewport.ToggleFit(null);
        viewport.ApplyGestureView(.5, 100, 50);
        viewport.CenterOnNormalizedPoint(.25, .75);
        return viewport;
    }

    /// <summary>照片在视图坐标中的外接矩形。</summary>
    /// <param name="viewport">视口。</param>
    private static (double Left, double Top, double Right, double Bottom) PhotoRect(PhotoViewport viewport)
    {
        double width = viewport.ImageWidth * viewport.Scale, height = viewport.ImageHeight * viewport.Scale;
        double centerX = viewport.ViewWidth / 2 + viewport.PanX, centerY = viewport.ViewHeight / 2 + viewport.PanY;
        return (centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2);
    }

    /// <summary>适应区在视图坐标中的矩形。</summary>
    /// <param name="viewport">视口。</param>
    private static (double Left, double Top, double Right, double Bottom) FitRect(PhotoViewport viewport)
    {
        var insets = viewport.FitInsets;
        return (insets.Left, insets.Top, insets.Left + viewport.FitWidth, insets.Top + viewport.FitHeight);
    }

    private static bool Near(double actual, double expected, double tolerance = Tolerance)
        => Math.Abs(actual - expected) <= tolerance;

    private static void Near(Checks c, string item, double expected, double actual, double tolerance = Tolerance)
        => c.True(item, Near(actual, expected, tolerance), $"期望 {expected}，实际 {actual}");

    private static string Format((double Left, double Top, double Right, double Bottom) rect)
        => $"({rect.Left:0.###},{rect.Top:0.###},{rect.Right:0.###},{rect.Bottom:0.###})";
}
