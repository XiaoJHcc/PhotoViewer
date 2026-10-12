using System.Collections.Generic;
using System.Threading.Tasks;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Storage;

/// <summary>
/// 文件选择器的类型过滤项(显示名 + 扩展名列表,如 ["*.jpg"])。
/// </summary>
public sealed class FilePickFilter
{
    /// <summary>
    /// 创建过滤项。
    /// </summary>
    /// <param name="displayName">显示名。</param>
    /// <param name="extensions">扩展名模式(含通配符,如 *.jpg)。</param>
    public FilePickFilter(string displayName, IReadOnlyList<string> extensions)
    {
        DisplayName = displayName;
        Extensions = extensions;
    }

    /// <summary>显示名。</summary>
    public string DisplayName { get; }

    /// <summary>扩展名模式。</summary>
    public IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// 是否为"所有图片"综合项(平台实现用泛型图片类型:Apple UTI public.image / MIME image/*)。
    /// </summary>
    public bool IsGenericImage { get; init; }
}

/// <summary>
/// 平台文件选择器接口。由 UI 层实现,ViewModel 经 <see cref="FilePicker"/> 门面调用。
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// 弹出文件夹选择器。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <returns>用户选择的文件夹;取消返回 null。</returns>
    Task<IFolderRef?> PickFolderAsync(string title);

    /// <summary>
    /// 弹出单文件选择器。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>用户选择的文件;取消返回 null。</returns>
    Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters);
}

/// <summary>
/// 文件选择器门面。启动期由 UI 层经 <see cref="Initialize"/> 注入实现。
/// </summary>
public static class FilePicker
{
    private static IFilePickerService? _impl;

    /// <summary>
    /// 注入 UI 层的文件选择器实现。
    /// </summary>
    /// <param name="impl">选择器实现。</param>
    public static void Initialize(IFilePickerService impl)
    {
        _impl = impl ?? throw new System.ArgumentNullException(nameof(impl));
    }

    /// <summary>当前注入的实现;未初始化时抛出异常(暴露接线遗漏)。</summary>
    private static IFilePickerService Impl
        => _impl ?? throw new System.InvalidOperationException("FilePicker 未初始化:请在应用启动时调用 FilePicker.Initialize。");

    /// <summary>
    /// 弹出文件夹选择器。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <returns>用户选择的文件夹;取消返回 null。</returns>
    public static Task<IFolderRef?> PickFolderAsync(string title) => Impl.PickFolderAsync(title);

    /// <summary>
    /// 弹出单文件选择器。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>用户选择的文件;取消返回 null。</returns>
    public static Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
        => Impl.PickFileAsync(title, filters);
}
