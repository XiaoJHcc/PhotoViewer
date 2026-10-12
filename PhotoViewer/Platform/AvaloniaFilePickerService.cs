using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Platform;

/// <summary>
/// Avalonia 实现的文件选择器,经当前主窗口的 <see cref="IStorageProvider"/> 弹出系统选择器。
/// </summary>
public sealed class AvaloniaFilePickerService : IFilePickerService
{
    /// <summary>
    /// 弹出文件夹选择器。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <returns>用户选择的文件夹;取消返回 null。</returns>
    public async Task<IFolderRef?> PickFolderAsync(string title)
    {
        var storageProvider = App.GetCurrentTopLevel()?.StorageProvider;
        if (storageProvider == null) return null;

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? new AvaloniaFolderRef(folders[0]) : null;
    }

    /// <summary>
    /// 弹出单文件选择器。
    /// </summary>
    /// <param name="title">标题。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>用户选择的文件;取消返回 null。</returns>
    public async Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        var storageProvider = App.GetCurrentTopLevel()?.StorageProvider;
        if (storageProvider == null) return null;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = BuildFileTypes(filters),
            AllowMultiple = false
        });

        return files.Count > 0 ? new AvaloniaFileRef(files[0]) : null;
    }

    /// <summary>把平台中立过滤项映射为 Avalonia 的 FilePickerFileType(含 Apple UTI / MIME 推导)。</summary>
    private static List<FilePickerFileType> BuildFileTypes(IReadOnlyList<FilePickFilter> filters)
    {
        var fileTypes = new List<FilePickerFileType>();

        foreach (var filter in filters)
        {
            var fileType = new FilePickerFileType(filter.DisplayName)
            {
                Patterns = filter.Extensions,
                AppleUniformTypeIdentifiers = filter.IsGenericImage
                    ? new[] { "public.image" }
                    : GetAppleTypeIdentifiers(filter.Extensions),
                MimeTypes = filter.IsGenericImage
                    ? new[] { "image/*" }
                    : GetMimeTypes(filter.Extensions),
            };
            fileTypes.Add(fileType);
        }

        return fileTypes;
    }

    /// <summary>
    /// 根据扩展名获取对应的 Apple UTI。
    /// </summary>
    private static string[] GetAppleTypeIdentifiers(IReadOnlyList<string> extensions)
    {
        var identifiers = new List<string>();
        foreach (var ext in extensions)
        {
            var identifier = ext.ToLowerInvariant().TrimStart('*') switch
            {
                ".jpg" or ".jpeg" => "public.jpeg",
                ".png" => "public.png",
                ".tiff" or ".tif" => "public.tiff",
                ".webp" => "public.webp",
                ".bmp" => "com.microsoft.bmp",
                ".gif" => "com.compuserve.gif",
                _ => "public.image"
            };

            if (!identifiers.Contains(identifier))
            {
                identifiers.Add(identifier);
            }
        }
        return identifiers.ToArray();
    }

    /// <summary>
    /// 根据扩展名获取对应的 MIME 类型。
    /// </summary>
    private static string[] GetMimeTypes(IReadOnlyList<string> extensions)
    {
        var mimeTypes = new List<string>();
        foreach (var ext in extensions)
        {
            var mimeType = ext.ToLowerInvariant().TrimStart('*') switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".tiff" or ".tif" => "image/tiff",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => "image/*"
            };

            if (!mimeTypes.Contains(mimeType))
            {
                mimeTypes.Add(mimeType);
            }
        }
        return mimeTypes.ToArray();
    }
}
