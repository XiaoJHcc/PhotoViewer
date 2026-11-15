using PhotoViewer.Shared;

namespace CoreContractCheck;

/// <summary>
/// 宽形态浮层分区几何不变量（SHELL-01、SHELL-11）：栏位背景铺到窗口边缘、栏位内容收在布局安全区内、
/// 主图分区满幅；行布局上下、列布局左右；分析栏与控制栏相邻时消除内部 1 点分隔线、背景取连续并集；
/// 横屏主屏不为底部手势条保留安全区。
/// </summary>
internal static class ShellPaneLayoutChecks
{
    private const double Width = 1032;
    private const double Height = 1376;
    private const double Exact = .01;

    public static void Run(Checks checks)
    {
        checks.Section("宽形态浮层分区（SHELL-01/11）");
        SafeInsets(checks);
        Row(checks);
        Column(checks);
        Joined(checks);
        ZeroPanes(checks);
        FileContentOverride(checks);
    }

    private static void SafeInsets(Checks checks)
    {
        var safe = new EdgeInsets(12, 24, 8, 20);
        var landscape = ShellPaneLayout.SafeInsets(safe, Height, Width);
        checks.True("横屏布局安全区去掉底部手势条余量",
            Near(landscape.Bottom, 0) && Near(landscape.Top, 24) && Near(landscape.Left, 12) && Near(landscape.Right, 8),
            landscape.ToString());
        var portrait = ShellPaneLayout.SafeInsets(safe, Width, Height);
        checks.True("竖屏布局安全区保持系统四边", portrait == safe, portrait.ToString());
    }

    private static void Row(Checks checks)
    {
        var safe = new EdgeInsets(12, 24, 8, 20);
        var panes = ShellPaneLayout.Partition(true, Width, Height, safe, 224, 85, 0);
        checks.True("行布局文件栏背景铺满顶边与两侧",
            Near(panes.FileBar.X, 0) && Near(panes.FileBar.Y, 0) && Near(panes.FileBar.Width, Width)
            && Near(panes.FileBar.MaxY, safe.Top + 223), panes.FileBar.ToString());
        checks.True("行布局文件栏内容收在左右安全区内",
            Near(panes.FileContent.X, safe.Left) && Near(panes.FileContent.Y, safe.Top)
            && Near(panes.FileContent.Width, Width - safe.Left - safe.Right), panes.FileContent.ToString());
        checks.True("行布局主图分区满幅并按安全区顶边下移、夹在两栏之间",
            Near(panes.Photo.X, 0) && Near(panes.Photo.Y, safe.Top + 224) && Near(panes.Photo.Width, Width)
            && Near(panes.Photo.MaxY, Height - safe.Bottom - 85), panes.Photo.ToString());
        checks.True("行布局控制栏内容收在底部安全区内",
            Near(panes.ControlContent.MaxY, Height - safe.Bottom)
            && Near(panes.ControlContent.X, safe.Left) && Near(panes.ControlContent.Width, Width - safe.Left - safe.Right),
            panes.ControlContent.ToString());
        checks.True("行布局工具区背景从分隔线铺到窗口底边",
            Near(panes.ControlBackground.Y, panes.ControlDivider.Y) && Near(panes.ControlBackground.MaxY, Height)
            && Near(panes.ControlBackground.Width, Width), panes.ControlBackground.ToString());
        checks.True("行布局分区首尾相接（文件分隔线 → 主图 → 控制分隔线）",
            Near(panes.FileDivider.MaxY, panes.Photo.Y) && Near(panes.ControlDivider.Y, panes.Photo.MaxY),
            $"{panes.FileDivider} / {panes.Photo} / {panes.ControlDivider}");
    }

    private static void Column(Checks checks)
    {
        var safe = new EdgeInsets(12, 24, 8, 20);
        var panes = ShellPaneLayout.Partition(false, Width, Height, safe, 117, 85, 0);
        checks.True("列布局文件栏背景纵贯窗口且贴左边",
            Near(panes.FileBar.X, 0) && Near(panes.FileBar.Y, 0) && Near(panes.FileBar.Height, Height),
            panes.FileBar.ToString());
        checks.True("列布局文件栏内容纵贯窗口（顶部/底部安全区不推列表）",
            Near(panes.FileContent.Y, 0) && Near(panes.FileContent.Height, Height)
            && Near(panes.FileContent.X, safe.Left), panes.FileContent.ToString());
        checks.True("列布局控制栏内容纵贯窗口并贴右边",
            Near(panes.ControlContent.Y, 0) && Near(panes.ControlContent.Height, Height)
            && Near(panes.ControlContent.MaxX, Width), panes.ControlContent.ToString());
        checks.True("列布局主图分区满幅并夹在左右栏之间",
            Near(panes.Photo.Y, 0) && Near(panes.Photo.Height, Height)
            && Near(panes.Photo.X, 117) && Near(panes.Photo.Width, Width - 117 - 85), panes.Photo.ToString());
    }

    private static void Joined(Checks checks)
    {
        var safe = new EdgeInsets(0, 0, 0, 0);
        var column = ShellPaneLayout.Partition(false, Width, Height, safe, 117, 85, 64);
        checks.True("列布局分析栏与控制栏相邻时消除内部 1 点分隔线",
            Near(column.ControlDivider.Width, 0) && Near(column.ControlDivider.Height, 0), column.ControlDivider.ToString());
        checks.True("列布局工具区背景取分析栏到窗口右缘的连续并集",
            Near(column.ControlBackground.X, column.Analysis.X)
            && Near(column.ControlBackground.Width, Width - column.Analysis.X), column.ControlBackground.ToString());
        checks.True("列布局相邻时控制内容宽度含原分隔线区域",
            Near(column.ControlContent.Width, 84) && Near(column.ControlContent.MaxX, Width), column.ControlContent.ToString());

        var row = ShellPaneLayout.Partition(true, Width, Height, safe, 224, 85, 64);
        checks.True("行布局分析栏与控制栏相邻时同样消除内部隔线",
            Near(row.ControlDivider.Width, 0) && Near(row.ControlDivider.Height, 0), row.ControlDivider.ToString());
        checks.True("行布局工具区背景取分析栏到窗口底边的连续并集",
            Near(row.ControlBackground.Y, row.Analysis.Y) && Near(row.ControlBackground.MaxY, Height),
            row.ControlBackground.ToString());
        checks.True("行布局相邻时主图吸收两侧栏位后的剩余高度（相接处只留一条分隔线）",
            Near(row.Photo.Height, Height - 224 - 64 - 84), row.Photo.ToString());
    }

    private static void ZeroPanes(Checks checks)
    {
        var safe = new EdgeInsets(12, 24, 8, 20);
        var row = ShellPaneLayout.Partition(true, Width, Height, safe, 224, 0, 0);
        checks.True("行布局控制栏隐藏时控制分隔线、工具区背景与内容归零",
            row.ControlDivider == default && row.ControlBackground == default && row.ControlContent == default,
            $"{row.ControlDivider} / {row.ControlBackground} / {row.ControlContent}");
        checks.True("行布局控制栏隐藏后主图占到安全区底边",
            Near(row.Photo.MaxY, Height - safe.Bottom), row.Photo.ToString());

        var column = ShellPaneLayout.Partition(false, Width, Height, safe, 117, 0, 0);
        checks.True("列布局控制栏隐藏时控制栏矩形归零、主图贴到窗口右缘",
            column.ControlDivider == default && column.ControlBackground == default
            && column.ControlContent == default && Near(column.Photo.MaxX, Width),
            $"{column.ControlDivider} / {column.ControlBackground} / {column.ControlContent} / {column.Photo}");

        var rowAnalysis = ShellPaneLayout.Partition(true, Width, Height, safe, 224, 0, 64);
        checks.True("行布局仅有分析栏时分析区贴住安全区底边、工具区背景铺到窗口底边",
            Near(rowAnalysis.Analysis.Y, Height - safe.Bottom - 64)
            && Near(rowAnalysis.Analysis.MaxY, Height - safe.Bottom)
            && Near(rowAnalysis.ControlBackground.Y, rowAnalysis.Analysis.Y)
            && Near(rowAnalysis.ControlBackground.MaxY, Height)
            && rowAnalysis.ControlContent == default && rowAnalysis.ControlDivider == default,
            $"{rowAnalysis.Analysis} / {rowAnalysis.ControlBackground}");

        var columnAnalysis = ShellPaneLayout.Partition(false, Width, Height, safe, 117, 0, 64);
        checks.True("列布局仅有分析栏时工具区背景从分析栏铺到窗口右缘",
            Near(columnAnalysis.Analysis.X, Width - 64)
            && Near(columnAnalysis.ControlBackground.X, columnAnalysis.Analysis.X)
            && Near(columnAnalysis.ControlBackground.Width, 64)
            && columnAnalysis.ControlContent == default && columnAnalysis.ControlDivider == default,
            $"{columnAnalysis.Analysis} / {columnAnalysis.ControlBackground}");
    }

    private static void FileContentOverride(Checks checks)
    {
        var safe = new EdgeInsets(62, 0, 0, 0);
        var panes = ShellPaneLayout.Partition(false, Width, Height, safe, 168, 0, 0, 62, 106);
        checks.True("列布局文件栏内容可显式给起点与宽度（胶片条从安全区前导之后起排）",
            Near(panes.FileContent.X, 62) && Near(panes.FileContent.Width, 106)
            && Near(panes.FileContent.Height, Height), panes.FileContent.ToString());
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= Exact;
}
