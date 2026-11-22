using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// FILTER-07：桌面宽形态筛选控件的三段 1:1:1 / 单胶囊判定、行布局整组居中、
/// 列布局固定单胶囊与相似聚类开关位置。
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
        c.Equal("顶栏每段建议宽 120", 120d, WideFilterLayout.PreferredSegmentWidth);

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
            Near(plain.Control.X, 320) && Near(plain.Control.Y, 10)
            && Near(plain.Control.Width, 360) && Near(plain.Control.Height, 44),
            Rect(plain.Control));
        c.True("行布局三段从左到右首尾相接，等宽且每段取建议宽 120",
            Near(plain.Sort.X, 0) && Near(plain.Sort.Width, 120)
            && Near(plain.Order.X, 120) && Near(plain.Order.Width, 120)
            && Near(plain.Rating.X, 240) && Near(plain.Rating.Width, 120),
            $"{Rect(plain.Sort)} {Rect(plain.Order)} {Rect(plain.Rating)}");
        c.True("行布局无开关时开关矩形为空", IsEmpty(plain.Toggle), Rect(plain.Toggle));
        c.Equal("行布局主轴长度为 44 + 2×10", 64d, plain.MainLength);

        var withToggle = WideFilterLayout.Compute(
            row: true, contentWidth: 1000, cardWidth: 96, leadingInset: 0,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("行布局含开关时整组（含间距）居中",
            Near(withToggle.Control.X, 271) && Near(withToggle.Control.Width, 360), Rect(withToggle.Control));
        c.True("开关紧随控件并保持 8 点间距",
            Near(withToggle.Toggle.X, 639) && Near(withToggle.Toggle.Y, 10)
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

        var contentDriven = WideFilterLayout.Compute(
            row: true, contentWidth: 1000, cardWidth: 96, leadingInset: 0,
            sortWidth: 120, orderWidth: 40, ratingWidth: 80, capsuleWidth: 96,
            includeToggle: false, toggleWidth: 90);
        c.True("内容需求小于建议宽时三段仍各为 120",
            Near(contentDriven.Control.Width, 360) && Near(contentDriven.Sort.Width, 120)
            && Near(contentDriven.Order.Width, 120) && Near(contentDriven.Rating.Width, 120),
            Rect(contentDriven.Control));

        var longRating = WideFilterLayout.Compute(
            row: true, contentWidth: 1000, cardWidth: 96, leadingInset: 0,
            sortWidth: 80, orderWidth: 40, ratingWidth: 150, capsuleWidth: 96,
            includeToggle: false, toggleWidth: 90);
        c.True("单段内容超过 120 时三段同步加宽且仍保持 1:1:1",
            Near(longRating.Control.Width, 450) && Near(longRating.Sort.Width, 150)
            && Near(longRating.Order.Width, 150) && Near(longRating.Rating.Width, 150),
            Rect(longRating.Control));
    }

    private static void RunColumn(Checks c)
    {
        var capsule = WideFilterLayout.Compute(
            row: false, contentWidth: 116, cardWidth: 96, leadingInset: 42,
            sortWidth: 28, orderWidth: 28, ratingWidth: 28, capsuleWidth: 96,
            includeToggle: false, toggleWidth: 90);
        c.True("列布局即使三段自然宽放得下也固定为单胶囊", capsule.Mode == FilterControlMode.Capsule,
            capsule.Mode.ToString());
        c.True("列布局控件居中于内容区并贴在让位之后",
            Near(capsule.Control.X, 10) && Near(capsule.Control.Y, 42)
            && Near(capsule.Control.Width, 96) && Near(capsule.Control.Height, 44),
            Rect(capsule.Control));
        c.True("列布局单胶囊的三段矩形均为空",
            IsEmpty(capsule.Sort) && IsEmpty(capsule.Order) && IsEmpty(capsule.Rating),
            $"{Rect(capsule.Sort)} {Rect(capsule.Order)} {Rect(capsule.Rating)}");
        c.True("列布局无开关时开关矩形为空", IsEmpty(capsule.Toggle), Rect(capsule.Toggle));
        c.Equal("列布局无开关时主轴长度为让位 + 高 + 外边距", 96d, capsule.MainLength);

        var withToggle = WideFilterLayout.Compute(
            row: false, contentWidth: 116, cardWidth: 96, leadingInset: 42,
            sortWidth: 90, orderWidth: 90, ratingWidth: 90, capsuleWidth: 96,
            includeToggle: true, toggleWidth: 90);
        c.True("列布局开关在单胶囊下方同宽",
            Near(withToggle.Toggle.X, 10) && Near(withToggle.Toggle.Y, 94)
            && Near(withToggle.Toggle.Width, 96) && Near(withToggle.Toggle.Height, 44),
            Rect(withToggle.Toggle));
        c.Equal("列布局含开关时主轴长度加上 8 + 44", 148d, withToggle.MainLength);

        var smallCard = WideFilterLayout.Compute(
            row: false, contentWidth: 64, cardWidth: 64, leadingInset: 0,
            sortWidth: 20, orderWidth: 20, ratingWidth: 20, capsuleWidth: 64,
            includeToggle: false, toggleWidth: 64);
        c.True("小档卡片列宽 64 时仍为同宽单胶囊",
            smallCard.Mode == FilterControlMode.Capsule
            && Near(smallCard.Control.X, 0) && Near(smallCard.Control.Width, 64),
            $"{smallCard.Mode} {Rect(smallCard.Control)}");
    }

    private static bool IsEmpty(LayoutRect rect)
        => Math.Abs(rect.X) <= Epsilon && Math.Abs(rect.Y) <= Epsilon
            && Math.Abs(rect.Width) <= Epsilon && Math.Abs(rect.Height) <= Epsilon;

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Rect(LayoutRect rect)
        => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##}×{rect.Height:0.##})";
}
