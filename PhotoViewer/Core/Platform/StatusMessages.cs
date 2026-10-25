namespace PhotoViewer.Core.Platform;

/// <summary>
/// 操作结果提示条文案（STATE-03）：统一为 <c>动作：对象（原因）</c>，原因不可得时省略括号。四端只负责显示。
/// </summary>
public static class StatusMessages
{
    /// <summary>选择器或外部打开没有可用对象名时的占位。</summary>
    public const string UnnamedTarget = "所选项目";

    /// <summary>内存压力自动关闭雾天增强。</summary>
    public const string EnhanceClosedForMemory = "内存不足，已关闭雾天增强";

    /// <summary>评分写回失败；部分来源补偿失败时明确数据可能不一致。</summary>
    public static string RatingWriteFailed(string fileName, string? reason = null, bool partial = false)
        => Compose(partial ? "星级写入不完整" : "无法写入星级", fileName, reason);

    /// <summary>屏上已有代理时全分辨率升级失败。</summary>
    public static string FullLoadFailed(string fileName) => Compose("无法载入全分辨率", fileName, null);

    /// <summary>打开文件夹失败。</summary>
    public static string FolderOpenFailed(string folderName, string? reason = null)
        => Compose("无法读取文件夹", folderName, reason);

    /// <summary>打开文件（含选择器、外部打开、拖入）失败。</summary>
    public static string OpenFailed(string? name, string? reason = null)
        => Compose("无法打开", name, reason);

    private static string Compose(string action, string? target, string? reason)
    {
        string subject = string.IsNullOrWhiteSpace(target) ? UnnamedTarget : target.Trim();
        string detail = reason?.Trim() ?? string.Empty;
        return detail.Length > 0 ? $"{action}：{subject}（{detail}）" : $"{action}：{subject}";
    }
}
