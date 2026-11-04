using PhotoViewer.Core.Input;

namespace CoreContractCheck;

/// <summary>
/// TOOLBAR-04：命令悬停提示与读屏名称的格式契约（各端提示、读屏名称、Web 参考壳共用同一文案）。
/// </summary>
internal static class TooltipTextChecks
{
    public static void Run(Checks c)
    {
        c.Section("命令提示与读屏名称格式");
        c.Equal("无绑定只显示命令名", "下一张", CommandTooltipText.Format("下一张", null, null));
        c.Equal("仅主绑定用全角括号", "下一张（→）", CommandTooltipText.Format("下一张", "→", null));
        c.Equal("仅次绑定省略主槽", "下一张（F）", CommandTooltipText.Format("下一张", null, "F"));
        c.Equal("主次绑定同列并用斜杠分隔", "下一张（→ / 滚轮下）",
            CommandTooltipText.Format("下一张", "→", "滚轮下"));
        c.Equal("主次相同只显示一次", "同名绑定（Q）", CommandTooltipText.Format("同名绑定", "Q", "Q"));
        c.Equal("空白绑定等同未设置", "缩放适应", CommandTooltipText.Format("缩放适应", " ", "  "));
        c.Equal("主槽空白时次绑定顶到主位", "缩放适应（F）", CommandTooltipText.Format("缩放适应", "", " F "));

        string both = CommandTooltipText.Format("下一张", "→", "滚轮下");
        c.True("命令名与绑定之间不用点号分隔",
            !both.Contains('·') && !both.Contains('.') && !both.Contains(" · "),
            both);
        c.True("提示不出现未设置占位",
            !CommandTooltipText.Format("下一张", null, "F").Contains("未设置", StringComparison.Ordinal),
            CommandTooltipText.Format("下一张", null, "F"));
        c.Equal("命令名原样保留（含自身括号）", "放大（预设）（⌘+ / F）",
            CommandTooltipText.Format("放大（预设）", "⌘+", "F"));
    }
}
