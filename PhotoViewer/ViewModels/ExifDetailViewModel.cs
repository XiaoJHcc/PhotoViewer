using System.Collections.Generic;
using PhotoViewer.Core;
using ReactiveUI;

namespace PhotoViewer.ViewModels;

/// <summary>
/// EXIF 详情页 ViewModel，持有当前图片的全部元数据分组信息
/// </summary>
public class ExifDetailViewModel : ReactiveObject
{
    /// <summary>当前文件名</summary>
    public string FileName { get; }

    /// <summary>全部元数据分组（按 EXIF 目录分段）</summary>
    public List<MetadataGroup> Groups { get; }

    public ExifDetailViewModel(ExifData exifData)
    {
        FileName = System.IO.Path.GetFileName(exifData.FilePath);
        Groups = exifData.AllMetadata;
    }
}
