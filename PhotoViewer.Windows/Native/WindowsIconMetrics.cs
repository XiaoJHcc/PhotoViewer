using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoViewer.Shared;
using Windows.Storage.Streams;
using Windows.UI;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 图标槽的实际像素核对：把生成字形的锚点几何（<see cref="IconCatalog.FluentInk"/> 与
/// <see cref="IconCatalog.FluentCellEm"/>）与原生宿主真正渲染出来的墨迹边界对起来。
/// 墨迹中心按"槽中心 + 设计锚点偏移"核对，不再假设墨迹中心等于槽中心；墨迹尺寸按同字号超大槽的
/// 对照渲染核对，模板缩放或槽裁切都会让尺寸对不上，因此对称裁切与静默缩图都能被看到。
/// </summary>
internal static class WindowsIconMetrics
{
    /// <summary>判定墨迹的 alpha 阈值：探针槽底是 alpha=1 的近透明填充，只有字形能到该值。</summary>
    private const byte InkAlpha = 128;

    /// <summary>墨迹与槽边的安全余量（DIP）：留出光栅化与像素取整的量。</summary>
    private const double SlotMargin = 0.5;

    /// <summary>像素边界容差（px）：光栅化、抗锯齿与 1 像素取整。</summary>
    private const double EdgeTolerance = 1.5;

    /// <summary>允许的模板缩放窗口：原生模板把排版框对到槽中心时会有极小的等比差，超过即视为静默缩放。</summary>
    private const double MinHostScale = 0.95;

    /// <summary>允许的模板缩放上限；超出说明字形被放大到槽外或取错了字号。</summary>
    private const double MaxHostScale = 1.10;

    /// <summary>字形墨迹相对设计锚点的几何（em，屏幕 Y 向下）；锚点即排版框中心。</summary>
    internal readonly record struct GlyphInk(double Width, double Height, double CenterX, double CenterY)
    {
        /// <summary>统一的方形排版框边长（em）；槽小于它就一定会被模板缩放或裁边。</summary>
        internal static double Cell => IconCatalog.FluentCellEm;

        /// <summary>按 <see cref="IconCatalog.FluentInk"/> 取某个字形的墨迹几何。</summary>
        /// <param name="glyph">字形码位。</param>
        /// <returns>墨迹宽高与墨迹中心相对设计锚点的偏移。</returns>
        internal static GlyphInk FromCatalog(string glyph)
        {
            var ink = IconCatalog.FluentInk(glyph);
            return new GlyphInk(
                ink.Right - ink.Left,
                ink.Bottom - ink.Top,
                (ink.Left + ink.Right) / 2,
                (ink.Top + ink.Bottom) / 2);
        }
    }

    /// <summary>一次像素回读的结果：位图尺寸与墨迹边界（px，含端点）。</summary>
    internal readonly record struct InkPixels(int Width, int Height, int MinX, int MinY, int MaxX, int MaxY)
    {
        /// <summary>位图里是否有达到阈值的墨迹。</summary>
        internal bool HasInk => MaxX >= MinX && MaxY >= MinY;

        /// <summary>墨迹宽（px）。</summary>
        internal int InkWidth => MaxX - MinX + 1;

        /// <summary>墨迹高（px）。</summary>
        internal int InkHeight => MaxY - MinY + 1;

        /// <summary>墨迹中心横坐标（px，相对位图左缘）。</summary>
        internal double CenterX => (MinX + MaxX + 1) / 2.0;

        /// <summary>墨迹中心纵坐标（px，相对位图上缘）。</summary>
        internal double CenterY => (MinY + MaxY + 1) / 2.0;

        /// <summary>墨迹是否贴住左缘（贴边即可能被裁）。</summary>
        internal bool TouchesLeft => HasInk && MinX <= 0;

        /// <summary>墨迹是否贴住右缘。</summary>
        internal bool TouchesRight => HasInk && MaxX >= Width - 1;

        /// <summary>墨迹是否贴住上缘。</summary>
        internal bool TouchesTop => HasInk && MinY <= 0;

        /// <summary>墨迹是否贴住下缘。</summary>
        internal bool TouchesBottom => HasInk && MaxY >= Height - 1;
    }

    /// <summary>一个待核对的图标样本。</summary>
    /// <param name="Label">报告名称（语义 ID 或窗口 + 位置）。</param>
    /// <param name="Glyph">字形码位。</param>
    /// <param name="FontSize">标称字号（DIP）。</param>
    /// <param name="SlotSize">声明的图标槽边长（DIP）；0 表示以原生模板实际槽为准，不校验声明值。</param>
    /// <param name="AsText">用 <see cref="TextBlock"/> 还是 <see cref="FontIcon"/> 承载。</param>
    /// <param name="Host">宿主说明（原生模板位置或自绘槽）。</param>
    /// <param name="Target">原生宿主里的真实字形元素；为 null 时按声明槽自建同样式的槽。</param>
    internal sealed record IconSample(
        string Label,
        string Glyph,
        double FontSize,
        double SlotSize,
        bool AsText,
        string Host,
        FrameworkElement? Target = null);

    /// <summary>按声明槽与标称字号建一个探针槽（槽底近透明，回读范围即完整槽位）。</summary>
    /// <param name="size">槽边长（DIP）。</param>
    /// <returns>槽元素。</returns>
    internal static Grid NewProbeSlot(double size) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
        Width = size,
        Height = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    /// <summary>按语义字形造一个与实现同构的图标元素（字体族随填充状态）。</summary>
    /// <param name="glyph">字形码位。</param>
    /// <param name="fontSize">字号（DIP）。</param>
    /// <param name="asText">是否用 <see cref="TextBlock"/> 承载。</param>
    /// <returns>图标元素。</returns>
    internal static FrameworkElement CreateIcon(string glyph, double fontSize, bool asText)
    {
        var family = new FontFamily(WindowsIcons.FontFamilyForGlyph(glyph));
        var brush = new SolidColorBrush(Microsoft.UI.Colors.White);
        if (asText)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = family,
                FontSize = fontSize,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = family,
            FontSize = fontSize,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>取图标元素所在的原生图标槽：模板里包住图标的 Viewbox（会按槽等比缩放），没有则退回元素自身。</summary>
    /// <param name="icon">原生模板里的字形元素。</param>
    /// <returns>承载该字形、决定最终像素尺寸的元素。</returns>
    internal static FrameworkElement ResolveSlotOwner(FrameworkElement icon)
    {
        for (var parent = VisualTreeHelper.GetParent(icon); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Viewbox box && box.ActualWidth > 0 && box.ActualHeight > 0)
            {
                return box;
            }
        }

        return icon;
    }

    /// <summary>在子树里按字形找真实字形元素（标题栏的 <c>IconSource</c> 由模板造出元素，没有公开属性可取）。</summary>
    /// <param name="root">子树根。</param>
    /// <param name="glyph">字形码位。</param>
    /// <returns>找到的元素，没有则为 null。</returns>
    internal static FrameworkElement? FindIcon(DependencyObject? root, string glyph)
    {
        if (root == null)
        {
            return null;
        }

        if (root is FontIcon icon && icon.Glyph == glyph && icon.ActualWidth > 0 && icon.ActualHeight > 0)
        {
            return icon;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            if (FindIcon(VisualTreeHelper.GetChild(root, index), glyph) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>回读一个元素的实际像素，并求墨迹边界。</summary>
    /// <param name="element">要回读的元素。</param>
    /// <returns>位图尺寸与墨迹边界（px）。</returns>
    internal static async Task<InkPixels> RenderInkAsync(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        using var reader = DataReader.FromBuffer(await bitmap.GetPixelsAsync());
        var pixels = new byte[checked(width * height * 4)];
        reader.ReadBytes(pixels);

        int minX = width, maxX = -1, minY = height, maxY = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < InkAlpha)
            {
                continue;
            }

            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        return new InkPixels(width, height, minX, minY, maxX, maxY);
    }

    /// <summary>
    /// 核对一个图标样本：先在同字号的超大槽里回读不受缩放与裁切的对照墨迹，再回读原生宿主（或按声明槽自建的槽），
    /// 逐项比对墨迹尺寸、墨迹中心与设计锚点、槽内余量与贴边情况。
    /// </summary>
    /// <param name="probeHost">挂探针元素的容器（主窗口/设置窗口/工具窗口的根）。</param>
    /// <param name="sample">样本。</param>
    /// <param name="require">断言回调（失败即登记并影响退出码）。</param>
    /// <param name="checks">报告行收集器。</param>
    /// <returns>任务。</returns>
    internal static async Task CheckAsync(
        Panel probeHost,
        IconSample sample,
        Action<bool, string> require,
        List<string> checks)
    {
        var ink = GlyphInk.FromCatalog(sample.Glyph);
        double rasterization = probeHost.XamlRoot?.RasterizationScale ?? 1.0;
        double cell = GlyphInk.Cell * sample.FontSize;

        // 对照渲染：同字号、槽放大到排版框之外，既不会被模板缩放，也不会被槽裁掉墨迹。
        double referenceSize = Math.Ceiling(cell + Math.Max(ink.Width, ink.Height) * sample.FontSize) + 8;
        var referenceSlot = NewProbeSlot(referenceSize);
        referenceSlot.Children.Add(CreateIcon(sample.Glyph, sample.FontSize, sample.AsText));
        probeHost.Children.Add(referenceSlot);
        InkPixels reference;
        try
        {
            probeHost.UpdateLayout();
            // UpdateLayout 不等待合成与随包字体的首次加载。
            await Task.Delay(150);
            reference = await RenderInkAsync(referenceSlot);
        }
        finally
        {
            probeHost.Children.Remove(referenceSlot);
        }

        double referencePixels = referenceSize * rasterization;
        require(Matches(reference.Width, referencePixels) && Matches(reference.Height, referencePixels),
            $"ICON-05 {sample.Label} 未回读到完整对照槽: {reference.Width}×{reference.Height}px, 预期 {referencePixels:0.##}px");
        require(reference.HasInk, $"ICON-05 {sample.Label} 对照槽里没有可见墨迹");

        double expectedWidth = ink.Width * sample.FontSize;
        double expectedHeight = ink.Height * sample.FontSize;
        double expectedCenterX = referenceSize / 2 + ink.CenterX * sample.FontSize;
        double expectedCenterY = referenceSize / 2 + ink.CenterY * sample.FontSize;
        double referenceInkWidth = reference.InkWidth / rasterization;
        double referenceInkHeight = reference.InkHeight / rasterization;
        double referenceCenterX = reference.CenterX / rasterization;
        double referenceCenterY = reference.CenterY / rasterization;

        // 生成几何：对照墨迹的宽高与锚点偏移必须等于 FluentInk 给出的值，而不是被摆到槽中心。
        require(Math.Abs(referenceInkWidth - expectedWidth) <= EdgeTolerance / rasterization + 0.5
                && Math.Abs(referenceInkHeight - expectedHeight) <= EdgeTolerance / rasterization + 0.5,
            $"ICON-05 {sample.Label} 对照墨迹与生成几何不符: {referenceInkWidth:0.00}×{referenceInkHeight:0.00}, "
            + $"FluentInk 预期 {expectedWidth:0.00}×{expectedHeight:0.00}");
        require(Math.Abs(referenceCenterX - expectedCenterX) <= EdgeTolerance / rasterization + 0.5
                && Math.Abs(referenceCenterY - expectedCenterY) <= EdgeTolerance / rasterization + 0.5,
            $"ICON-05 {sample.Label} 对照墨迹未落在设计锚点上: "
            + $"中心差 ({referenceCenterX - expectedCenterX:+0.00;-0.00;0},{referenceCenterY - expectedCenterY:+0.00;-0.00;0}) DIP");

        // 原生宿主：槽尺寸与缩放系数都取真实模板的排版结果，不按声明值假设。
        FrameworkElement host;
        InkPixels actual;
        double slotWidth, slotHeight, hostScale;
        string hostDetail;
        if (sample.Target == null)
        {
            // 自绘槽：按实现同构的固定尺寸槽 + 居中的字形元素回读。
            var slot = NewProbeSlot(sample.SlotSize);
            slot.Children.Add(CreateIcon(sample.Glyph, sample.FontSize, sample.AsText));
            probeHost.Children.Add(slot);
            try
            {
                probeHost.UpdateLayout();
                await Task.Delay(150);
                actual = await RenderInkAsync(slot);
            }
            finally
            {
                probeHost.Children.Remove(slot);
            }

            slotWidth = slotHeight = sample.SlotSize;
            hostScale = 1.0;
            hostDetail = $"{sample.Host} slot={sample.SlotSize:0.#}×{sample.SlotSize:0.#}";
        }
        else
        {
            host = ResolveSlotOwner(sample.Target);
            slotWidth = host.ActualWidth;
            slotHeight = host.ActualHeight;
            hostScale = Math.Min(
                slotWidth / Math.Max(sample.Target.ActualWidth, 1e-6),
                slotHeight / Math.Max(sample.Target.ActualHeight, 1e-6));
            actual = await RenderInkAsync(host);
            hostDetail = $"{sample.Host} {host.GetType().Name}({host.Name}) slot={slotWidth:0.#}×{slotHeight:0.#}"
                + $", glyph element={sample.Target.ActualWidth:0.#}×{sample.Target.ActualHeight:0.#}";
        }

        double slotPixelsW = slotWidth * rasterization, slotPixelsH = slotHeight * rasterization;
        require(Matches(actual.Width, slotPixelsW) && Matches(actual.Height, slotPixelsH),
            $"ICON-05 {sample.Label} 未回读到完整图标槽: {actual.Width}×{actual.Height}px, "
            + $"预期 {slotPixelsW:0.##}×{slotPixelsH:0.##}px（{hostDetail}）");
        require(actual.HasInk, $"ICON-05 {sample.Label} 在 {actual.Width}×{actual.Height}px 槽内没有可见墨迹（{hostDetail}）");

        if (sample.SlotSize > 0)
        {
            require(Matches(slotWidth, sample.SlotSize) && Matches(slotHeight, sample.SlotSize),
                $"ICON-05 {sample.Label} 原生图标槽不是声明的 {sample.SlotSize:0.#}: {slotWidth:0.#}×{slotHeight:0.#}（{hostDetail}）");
        }

        // 排版框（1.2 em）必须整块落在槽里；模板把放不下的排版框缩到槽高就是静默缩图。
        require(cell <= Math.Min(slotWidth, slotHeight) + SlotMargin,
            $"ICON-05 {sample.Label} 字形排版框越槽: {cell:0.##} DIP > 槽 {Math.Min(slotWidth, slotHeight):0.##} DIP（{hostDetail}）");
        require(hostScale >= MinHostScale,
            $"ICON-05 {sample.Label} 原生模板静默缩小字形: 宿主缩放 {hostScale:0.###}（{hostDetail}）");
        require(hostScale <= MaxHostScale,
            $"ICON-05 {sample.Label} 原生模板放大字形: 宿主缩放 {hostScale:0.###}（{hostDetail}）");

        // 实际墨迹与"标称字号 × 宿主缩放"的预期边界（px）逐项比对：尺寸、中心、四边。
        double inkWidthPx = expectedWidth * hostScale * rasterization;
        double inkHeightPx = expectedHeight * hostScale * rasterization;
        double centerXPx = slotPixelsW / 2 + ink.CenterX * sample.FontSize * hostScale * rasterization;
        double centerYPx = slotPixelsH / 2 + ink.CenterY * sample.FontSize * hostScale * rasterization;
        double left = centerXPx - inkWidthPx / 2, right = centerXPx + inkWidthPx / 2;
        double top = centerYPx - inkHeightPx / 2, bottom = centerYPx + inkHeightPx / 2;

        // 墨迹整块必须落在槽内：越界说明槽放不下这个字形，会被裁边。
        // 生成的统一排版框（1.2 em）已经把全部轮廓收在锚点 ±0.6 em 内，个别非对称字形会贴到槽边，
        // 因此只要求墨迹不出槽，真正的裁切由下面的尺寸比对（对称裁切会变小）判定。
        require(left >= 0 && right <= actual.Width && top >= 0 && bottom <= actual.Height,
            $"ICON-05 {sample.Label} 字形墨迹顶到槽边（槽不足或锚点偏移）: 预期边界 "
            + $"[{left:0.0},{top:0.0}..{right:0.0},{bottom:0.0}]px / 槽 {actual.Width}×{actual.Height}px（{hostDetail}）");

        double widthDelta = Math.Abs(actual.InkWidth - inkWidthPx);
        double heightDelta = Math.Abs(actual.InkHeight - inkHeightPx);
        require(widthDelta <= EdgeTolerance && heightDelta <= EdgeTolerance,
            $"ICON-05 {sample.Label} 墨迹尺寸与标称字号不符（模板缩图或裁切）: {actual.InkWidth}×{actual.InkHeight}px, "
            + $"预期 {inkWidthPx:0.0}×{inkHeightPx:0.0}px, 对照 {reference.InkWidth}×{reference.InkHeight}px（{hostDetail}）");

        double centerDeltaX = actual.CenterX - centerXPx, centerDeltaY = actual.CenterY - centerYPx;
        require(Math.Abs(centerDeltaX) <= EdgeTolerance && Math.Abs(centerDeltaY) <= EdgeTolerance,
            $"ICON-05 {sample.Label} 墨迹偏离设计锚点: 中心差 ({centerDeltaX:+0.0;-0.0;0},{centerDeltaY:+0.0;-0.0;0})px（{hostDetail}）");

        // 对称裁切：两侧同时贴边且墨迹比预期小，说明槽把字形两头都切掉了，中心却仍可能对得上。
        require(!(actual.TouchesLeft && actual.TouchesRight) || widthDelta <= EdgeTolerance,
            $"ICON-05 {sample.Label} 字形被横向对称裁切: 墨迹 {actual.InkWidth}px 顶满 {actual.Width}px 槽, "
            + $"预期 {inkWidthPx:0.0}px（{hostDetail}）");
        require(!(actual.TouchesTop && actual.TouchesBottom) || heightDelta <= EdgeTolerance,
            $"ICON-05 {sample.Label} 字形被纵向对称裁切: 墨迹 {actual.InkHeight}px 顶满 {actual.Height}px 槽, "
            + $"预期 {inkHeightPx:0.0}px（{hostDetail}）");

        checks.Add(
            $"PASS ICON-05 {sample.Label} {(sample.AsText ? "TextBlock" : "FontIcon")}"
            + $" {sample.FontSize:0.#}pt; {hostDetail}; scale={hostScale:0.###}; "
            + $"ink={actual.InkWidth}×{actual.InkHeight}px (对照 {reference.InkWidth}×{reference.InkHeight}px, "
            + $"预期 {inkWidthPx:0.0}×{inkHeightPx:0.0}px); "
            + $"对照比=({actual.InkWidth / (double)reference.InkWidth:0.000},{actual.InkHeight / (double)reference.InkHeight:0.000}); "
            + $"center delta=({centerDeltaX:+0.0;-0.0;0},{centerDeltaY:+0.0;-0.0;0})px; "
            + $"最小槽内余量={Math.Min(Math.Min(left, actual.Width - right), Math.Min(top, actual.Height - bottom)):0.0}px");
    }

    /// <summary>设置/工具窗口的标题栏与分类图标契约。</summary>
    /// <param name="WindowLabel">报告里的窗口名称。</param>
    /// <param name="TitleGlyph">标题栏字形。</param>
    /// <param name="TitleFontSize">标题栏图标标称字号（DIP）。</param>
    /// <param name="NavFontSize">分类图标标称字号（DIP）。</param>
    /// <param name="NavSlot">分类图标槽边长（DIP），也是字形元素的显式尺寸。</param>
    internal sealed record ChromeExpectations(
        string WindowLabel,
        string TitleGlyph,
        double TitleFontSize,
        double NavFontSize,
        double NavSlot);

    /// <summary>核对设置/工具窗口的标题栏图标与全部分类图标：先验显式尺寸，再回读真实像素。</summary>
    /// <param name="probeHost">窗口根元素。</param>
    /// <param name="titleBar">窗口标题栏控件。</param>
    /// <param name="nav">左侧分类导航。</param>
    /// <param name="expectations">窗口图标契约。</param>
    /// <param name="navItems">分类名称与字形（按导航顺序）。</param>
    /// <param name="require">断言回调。</param>
    /// <param name="checks">报告行收集器。</param>
    /// <returns>任务。</returns>
    internal static async Task CheckWindowChromeAsync(
        Panel probeHost,
        TitleBar titleBar,
        NavigationView nav,
        ChromeExpectations expectations,
        IReadOnlyList<(string Label, string Glyph)> navItems,
        Action<bool, string> require,
        List<string> checks)
    {
        probeHost.UpdateLayout();
        // 标题栏图标由 IconSource 经模板造出元素，等一次合成再取。
        await Task.Delay(150);

        var titleIcon = FindIcon(titleBar, expectations.TitleGlyph);
        require(titleIcon != null,
            $"{expectations.WindowLabel}标题栏没有渲染出图标字形（{expectations.TitleGlyph}）");
        require(Math.Abs(((FontIcon)titleIcon!).FontSize - expectations.TitleFontSize) < 0.01,
            $"{expectations.WindowLabel}标题栏图标字号不是显式的 {expectations.TitleFontSize:0.#}: "
            + $"{(titleIcon as FontIcon)?.FontSize:0.##}");
        await CheckAsync(
            probeHost,
            new IconSample(
                $"{expectations.WindowLabel}标题栏",
                expectations.TitleGlyph,
                expectations.TitleFontSize,
                0,
                false,
                "TitleBar IconSource",
                titleIcon),
            require,
            checks);

        require(nav.MenuItems.Count == navItems.Count,
            $"{expectations.WindowLabel}分类数 {nav.MenuItems.Count} 与图标样本数 {navItems.Count} 不一致");
        for (var index = 0; index < navItems.Count; index++)
        {
            var (label, glyph) = navItems[index];
            require(nav.MenuItems[index] is NavigationViewItem { Icon: FontIcon },
                $"{expectations.WindowLabel}分类「{label}」没有字形图标");
            var icon = (FontIcon)((NavigationViewItem)nav.MenuItems[index]).Icon;
            require(Math.Abs(icon.FontSize - expectations.NavFontSize) < 0.01,
                $"{expectations.WindowLabel}分类「{label}」字号不是显式的 {expectations.NavFontSize:0.#}: {icon.FontSize:0.##}");
            // 模板把图标放进 Viewbox 等比缩放到槽高；显式宽高让 Viewbox 的缩放正好是 1，不再静默缩图。
            require(Math.Abs(icon.Width - expectations.NavSlot) < 0.01 && Math.Abs(icon.Height - expectations.NavSlot) < 0.01,
                $"{expectations.WindowLabel}分类「{label}」没有显式图标尺寸: {icon.Width:0.#}×{icon.Height:0.#}, "
                + $"预期 {expectations.NavSlot:0.#}×{expectations.NavSlot:0.#}");
            require(icon.Glyph == glyph,
                $"{expectations.WindowLabel}分类「{label}」字形不符: {icon.Glyph} != {glyph}");
            await CheckAsync(
                probeHost,
                new IconSample(
                    $"{expectations.WindowLabel}分类「{label}」",
                    glyph,
                    expectations.NavFontSize,
                    expectations.NavSlot,
                    false,
                    "NavigationViewItem IconBox(Viewbox)",
                    icon),
                require,
                checks);
        }
    }

    /// <summary>像素尺寸比较：留 1 像素给取整。</summary>
    /// <param name="actual">实测值。</param>
    /// <param name="expected">预期值。</param>
    /// <returns>是否在容差内。</returns>
    private static bool Matches(double actual, double expected) => Math.Abs(actual - expected) <= 1.0;
}
