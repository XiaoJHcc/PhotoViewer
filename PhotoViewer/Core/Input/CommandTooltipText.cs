namespace PhotoViewer.Core.Input;

/// <summary>
/// 命令悬停提示与读屏名称（TOOLBAR-04）：统一为 <c>名称（主 / 次）</c>。
/// 空槽省略、主次相同只显示一次、两个槽都无绑定时只输出名称；命令名与绑定之间不使用点号分隔。
/// 键名格式化（苹果修饰键符号、可读键名、滚轮方向）仍由各端负责，本类只拼装格式。
/// </summary>
public static class CommandTooltipText
{
    /// <summary>主/次绑定之间的分隔符。</summary>
    private const string SlotSeparator = " / ";

    /// <summary>按提交顺序拼出提示文案；格式化后的主次绑定可为空串或 null。</summary>
    /// <param name="name">业务命令名，例如「下一张」。</param>
    /// <param name="primary">已格式化的主绑定文本，未设置时为 null 或空。</param>
    /// <param name="secondary">已格式化的次绑定文本，未设置时为 null 或空。</param>
    /// <returns>可直接用于悬停提示与读屏名称的文案。</returns>
    public static string Format(string name, string? primary, string? secondary)
    {
        string first = Normalize(primary);
        string second = Normalize(secondary);
        if (string.Equals(first, second, StringComparison.Ordinal)) second = string.Empty;
        if (first.Length == 0)
        {
            first = second;
            second = string.Empty;
        }

        if (first.Length == 0) return name;
        return $"{name}（{(second.Length == 0 ? first : first + SlotSeparator + second)}）";
    }

    /// <summary>空白绑定等同于未设置，避免各端用空串拼出空槽。</summary>
    private static string Normalize(string? slot) => slot?.Trim() ?? string.Empty;
}
