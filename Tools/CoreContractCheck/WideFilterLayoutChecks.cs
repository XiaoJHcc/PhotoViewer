using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// FILTER-07：宽形态筛选控件的三段合体 / 单胶囊判定、行布局整组居中、列布局卡片列宽与余量均分、
/// 相似聚类开关位置与筛选控件主轴长度。
/// </summary>
internal static class WideFilterLayoutChecks
{
    private const double Epsilon = 1e-6;

    public static void Run(Checks c)
    {
        c.Section("宽形态筛选控件几何");
        c.Equal("控件高度取玻璃按钮边长 44", 44d, WideFilterLayout.Height);
        c.Equal("控件与开关间距 8", 8d, WideFilterLayout.Gap);
        c.Equal("行布局外边距 10", 10d, WideFilterLayout.Margin);

        RunRow(c);
        RunColumn(c);
    }

    private static void RunRow(Checks c)
    {
        var plain = WideFilterLayout.Compute(
            row: true, contentWidth: 1000, cardWidth: 96, leadingInset: 0,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: false, toggleWidth: 90);
        c.True("行布局三段放得下时为三合体", plain.Mode == FilterControlMode.Segmented,
            plain.Mode.ToString());
        c.True("行布局无开关时三段合体在 1000 内容内居中",
            Near(plain.Control.X, 365) && Near(plain.Control.Y, 10)
            && Near(plain.Control.Width, 270) && Near(plain.Control.Height, 44),
            Rect(plain.Control));
        c.True("行布局三段从左到右首尾相接",
            Near(plain.Sort.X, 0) && Near(plain.Sort.Width, 90)
            && Near(plain.Order.X, 90) && Near(plain.Order.Width, 90)
            && Near(plain.Rating.X, 180) && Near(plain.Rating.Width, 90),
            $"{Rect(plain.Sort)} {Rect(plain.Order)} {Rect(plain.Rating)}");
        c.True("行布局无开关时开关矩形为空", IsEmpty(plain.Toggle), Rect(plain.Toggle));
        c.Equal("行布局主轴长度为 44 + 2×10", 64d, plain.MainLength);

        var withToggle = WideFilterLayout.Compute(
            row: true, contentWidth: 1000, cardWidth: 96, leadingInset: 0,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("行布局含开关时整组（含间距）居中",
            Near(withToggle.Control.X, 316) && Near(withToggle.Control.Width, 270), Rect(withToggle.Control));
        c.True("开关紧随控件并保持 8 点间距",
            Near(withToggle.Toggle.X, 594) && Near(withToggle.Toggle.Y, 10)
            && Near(withToggle.Toggle.Width, 90) && Near(withToggle.Toggle.Height, 44),
            Rect(withToggle.Toggle));
        c.Equal("行布局含开关时主轴长度不变", 64d, withToggle.MainLength);

        var overflow = WideFilterLayout.Compute(
            row: true, contentWidth: 250, cardWidth: 96, leadingInset: 0,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("行布局三段放不下时改单胶囊", overflow.Mode == FilterControlMode.Capsule,
            overflow.Mode.ToString());
        c.True("行布局单胶囊宽取胶囊宽、整组仍居中",
            Near(overflow.Control.X, 28) && Near(overflow.Control.Width, 96), Rect(overflow.Control));
        c.True("单胶囊时三段矩形为空",
            IsEmpty(overflow.Sort) && IsEmpty(overflow.Order) && IsEmpty(overflow.Rating),
            $"{Rect(overflow.Sort)} {Rect(overflow.Order)} {Rect(overflow.Rating)}");
        c.True("行布局单胶囊时开关紧随其后",
            Near(overflow.Toggle.X, 132) && Near(overflow.Toggle.Width, 90), Rect(overflow.Toggle));

        var cramped = WideFilterLayout.Compute(
            row: true, contentWidth: 150, cardWidth: 96, leadingInset: 0,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("内容区连胶囊加开关都放不下时从外边距起排",
            Near(cramped.Control.X, 10) && Near(cramped.Control.Width, 96), Rect(cramped.Control));
    }

    private static void RunColumn(Checks c)
    {
        var segmented = WideFilterLayout.Compute(
            row: false, contentWidth: 116, cardWidth: 96, leadingInset: 42,
            sortWidth: 28, orderWidth: 28, ratingWidth: 28, capsuleWidth: 96,
            includeToggle: false, toggleWidth: 90);
        c.True("列布局三段放得下时为三合体", segmented.Mode == FilterControlMode.Segmented,
            segmented.Mode.ToString());
        c.True("列布局控件居中于内容区并贴在让位之后",
            Near(segmented.Control.X, 10) && Near(segmented.Control.Y, 42)
            && Near(segmented.Control.Width, 96) && Near(segmented.Control.Height, 44),
            Rect(segmented.Control));
        c.True("列布局余量 12 均分到三段（每段 32）",
            Near(segmented.Sort.Width, 32) && Near(segmented.Order.Width, 32) && Near(segmented.Rating.Width, 32),
            $"{segmented.Sort.Width}/{segmented.Order.Width}/{segmented.Rating.Width}");
        c.True("列布局三段铺满控件宽且保持顺序",
            Near(segmented.Sort.X, 0) && Near(segmented.Order.X, 32) && Near(segmented.Rating.X, 64)
            && Near(segmented.Rating.MaxX, 96),
            $"{Rect(segmented.Sort)} {Rect(segmented.Order)} {Rect(segmented.Rating)}");
        c.True("列布局无开关时开关矩形为空", IsEmpty(segmented.Toggle), Rect(segmented.Toggle));
        c.Equal("列布局无开关时主轴长度为让位 + 高 + 外边距", 96d, segmented.MainLength);

        var withToggle = WideFilterLayout.Compute(
            row: false, contentWidth: 116, cardWidth: 96, leadingInset: 42,
            sortWidth: 28, orderWidth: 28, ratingWidth: 28, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("列布局开关在控件下方同宽",
            Near(withToggle.Toggle.X, 10) && Near(withToggle.Toggle.Y, 94)
            && Near(withToggle.Toggle.Width, 96) && Near(withToggle.Toggle.Height, 44),
            Rect(withToggle.Toggle));
        c.Equal("列布局含开关时主轴长度加上 8 + 44", 148d, withToggle.MainLength);

        var capsule = WideFilterLayout.Compute(
            row: false, contentWidth: 116, cardWidth: 96, leadingInset: 42,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("列布局三段放不下时改单胶囊", capsule.Mode == FilterControlMode.Capsule,
            capsule.Mode.ToString());
        c.True("列布局单胶囊仍为卡片列宽",
            Near(capsule.Control.X, 10) && Near(capsule.Control.Width, 96), Rect(capsule.Control));
        c.True("列布局单胶囊时三段矩形为空", IsEmpty(capsule.Sort) && IsEmpty(capsule.Order) && IsEmpty(capsule.Rating));
        c.True("列布局单胶囊时开关仍在下方同宽",
            Near(capsule.Toggle.Y, 94) && Near(capsule.Toggle.Width, 96), Rect(capsule.Toggle));
        c.Equal("列布局单胶囊主轴长度与三合体一致", 148d, capsule.MainLength);

        var smallCard = WideFilterLayout.Compute(
            row: false, contentWidth: 64, cardWidth: 64, leadingInset: 0,
            sortWidth: 20, orderWidth: 20, ratingWidth: 20, capsuleWidth: 64,
            includeToggle: false, toggleWidth: 64);
        c.True("小档卡片列宽 64 时三段仍可三合体并铺满",
            smallCard.Mode == FilterControlMode.Segmented
            && Near(smallCard.Control.X, 0) && Near(smallCard.Rating.MaxX, 64),
            $"{smallCard.Mode} {Rect(smallCard.Control)} {Rect(smallCard.Rating)}");
    }

    private static bool IsEmpty(LayoutRect rect)
        => Math.Abs(rect.X) <= Epsilon && Math.Abs(rect.Y) <= Epsilon
            && Math.Abs(rect.Width) <= Epsilon && Math.Abs(rect.Height) <= Epsilon;

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Rect(LayoutRect rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";
}
