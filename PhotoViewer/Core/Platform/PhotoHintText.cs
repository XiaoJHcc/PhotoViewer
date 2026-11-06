using System.Globalization;

namespace PhotoViewer.Core.Platform;

/// <summary>
/// 主图区空态/加载/失败提示文案（STATE-01/STATE-02/IMAGE-10）：四端共用同一份标题，
/// 平台差异只在空态标题（桌面含拖放提示、触屏只提示打开文件夹）。
/// 解码失败详情取会话 <see cref="Image.PhotoLoadSession.Error"/>；加载与空态不带详情。
/// </summary>
public static class PhotoHintText
{
    /// <summary>桌面空态标题。</summary>
    public const string EmptyDesktopTitle = "点击打开文件 或 拖拽图片到此处";
    /// <summary>触屏空态标题。</summary>
    public const string EmptyTouchTitle = "点击打开文件夹";
    /// <summary>解码失败标题；具体原因放在详情行。</summary>
    public const string LoadErrorTitle = "无法打开该图片";
    /// <summary>加载提示标题（只在共享会话越过延迟门槛后显示）。</summary>
    public const string LoadingTitle = "正在载入…";

    /// <summary>
    /// 按失败 → 空态 → 加载的优先级给出提示标题与详情。
    /// </summary>
    /// <param name="hasFile">当前是否有照片。</param>
    /// <param name="error">解码失败原因；空串/空白表示没有失败。</param>
    /// <param name="loadingVisible">共享会话的延迟加载态是否已可见。</param>
    /// <param name="touch">是否触屏平台（只影响空态标题）。</param>
    /// <returns>标题与详情；都不显示时两项均为空串。</returns>
    public static (string Title, string Detail) Resolve(bool hasFile, string? error, bool loadingVisible, bool touch)
    {
        if (!string.IsNullOrWhiteSpace(error)) return (LoadErrorTitle, error);
        if (!hasFile) return (touch ? EmptyTouchTitle : EmptyDesktopTitle, string.Empty);
        if (loadingVisible) return (LoadingTitle, string.Empty);
        return (string.Empty, string.Empty);
    }
}

/// <summary>FILM-05/MENU-05 拍摄时间格式：桌面悬停提示与触屏卡片菜单标题共用。</summary>
public static class ShotTimeText
{
    /// <summary>完整拍摄时间格式（含时区偏移）。</summary>
    public const string Format = "yyyy-MM-dd HH:mm:ss zzz";

    /// <summary>按 <see cref="Format"/> 格式化；没有拍摄时间时返回 null。</summary>
    /// <param name="photoDate">拍摄时间。</param>
    /// <returns>格式化文本；null 输入返回 null。</returns>
    public static string? FormatShotTime(DateTimeOffset? photoDate)
        => photoDate?.ToString(Format, CultureInfo.InvariantCulture);
}
