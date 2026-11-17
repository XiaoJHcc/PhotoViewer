using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// FILTER-07：移动端宽形态「筛选 / 更多」两段合体的宽度与分段、行布局整组居中、
/// 列布局合体与相似聚类开关靠内容末端对齐，以及筛选控件主轴长度。
/// </summary>
internal static class MobileFilterLayoutChecks
{
    private const double Epsilon = 1e-6;

    public static void Run(Checks c)
    {
        c.Section("移动端宽形态筛选合体几何");
        c.Equal("合体高取玻璃按钮边长 44", 44d, MobileFilterLayout.Segment);
        c.Equal("合体宽与大档卡片外框同宽 96", 96d, MobileFilterLayout.GroupWidth);
        c.Equal("两段各 48", 48d, MobileFilterLayout.SegmentWidth);

        var row = MobileFilterLayout.Compute(row: true, contentWidth: 1000, leadingInset: 0, toggleWidth: 0);
        c.True("行布局无开关时合体在 1000 内容内居中",
            Near(row.Control.X, 452) && Near(row.Control.Y, 10)
            && Near(row.Control.Width, 96) && Near(row.Control.Height, 44), Rect(row.Control));
        c.True("两段沿主轴首尾相接",
            Near(row.Filter.X, 0) && Near(row.Filter.Width, 48)
            && Near(row.More.X, 48) && Near(row.More.Width, 48), $"{Rect(row.Filter)} {Rect(row.More)}");
        c.True("行布局无开关时开关矩形为空", IsEmpty(row.Toggle), Rect(row.Toggle));
        c.Equal("行布局主轴长度为 44 + 2×10", 64d, row.MainLength);

        var rowToggle = MobileFilterLayout.Compute(row: true, contentWidth: 1000, leadingInset: 0, toggleWidth: 90);
        c.True("行布局含开关时整组（含 8 点间距）居中，开关紧随合体",
            Near(rowToggle.Control.X, 403) && Near(rowToggle.Toggle.X, 507)
            && Near(rowToggle.Toggle.Width, 90) && Near(rowToggle.Toggle.Y, 10),
            $"{Rect(rowToggle.Control)} {Rect(rowToggle.Toggle)}");

        var narrow = MobileFilterLayout.Compute(row: true, contentWidth: 150, leadingInset: 0, toggleWidth: 90);
        c.True("行布局整组超出内容宽时从外边距起排", Near(narrow.Control.X, 10), Rect(narrow.Control));

        var column = MobileFilterLayout.Compute(row: false, contentWidth: 116, leadingInset: 34, toggleWidth: 96,
            contentTrailing: 106);
        c.True("列布局合体右缘落在内容末端，顶边取前导让位",
            Near(column.Control.MaxX, 106) && Near(column.Control.Y, 34) && Near(column.Control.Width, 96),
            Rect(column.Control));
        c.True("列布局开关同宽同末端、在合体下方隔 8",
            Near(column.Toggle.MaxX, 106) && Near(column.Toggle.Width, 96)
            && Near(column.Toggle.Y, column.Control.MaxY + 8), Rect(column.Toggle));
        c.Equal("列布局主轴长度 = 让位 + 合体 + 间距 + 开关 + 外边距", 34d + 44 + 8 + 44 + 10, column.MainLength);

        var implicitTrailing = MobileFilterLayout.Compute(row: false, contentWidth: 116, leadingInset: 10, toggleWidth: 0);
        c.True("列布局未给内容末端时取内容宽减外边距", Near(implicitTrailing.Control.MaxX, 106),
            Rect(implicitTrailing.Control));
        c.Equal("列布局无开关时主轴长度 = 让位 + 合体 + 外边距", 64d, implicitTrailing.MainLength);
    }

    private static bool IsEmpty(LayoutRect rect) => rect.Width <= 0 && rect.Height <= 0;

    private static bool Near(double first, double second) => Math.Abs(first - second) <= Epsilon;

    private static string Rect(LayoutRect rect) => $"({rect.X:0.##},{rect.Y:0.##},{rect.Width:0.##},{rect.Height:0.##})";
}
