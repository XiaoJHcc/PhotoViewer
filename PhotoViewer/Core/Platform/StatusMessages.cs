using System.Xml;
using PhotoViewer.Core.Storage;
using XmpCore;

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

    public const string SinglePhotoRefreshed = "无法读取父目录，仅刷新当前照片";

    /// <summary>评分写回失败；部分来源补偿失败时明确数据可能不一致。</summary>
    public static string RatingWriteFailed(string fileName, string? reason = null, bool partial = false)
        => Compose(partial ? "星级写入不完整" : "无法写入星级", fileName, reason);

    /// <summary>屏上已有代理时全分辨率升级失败。</summary>
    public static string FullLoadFailed(string fileName, string? reason = null)
        => Compose("无法解码全分辨率图像", fileName, reason);

    /// <summary>打开文件夹失败。</summary>
    public static string FolderOpenFailed(string folderName, string? reason = null)
        => Compose("无法读取文件夹", folderName, reason);

    /// <summary>打开文件（含选择器、外部打开、拖入）失败。</summary>
    public static string OpenFailed(string? name, string? reason = null)
        => Compose("无法打开", name, reason);

    /// <summary>把底层异常归类为可显示的原因；完整异常仍由调用方写日志。</summary>
    public static string ReasonFor(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            exception = aggregate.InnerExceptions[0];

        return exception switch
        {
            UserFacingException user => user.Message,
            StorageException storage => StorageReason(storage),
            OperationCanceledException => "操作已取消",
            UnauthorizedAccessException => "没有访问权限",
            FileNotFoundException or DirectoryNotFoundException => "文件或文件夹已不存在，或路径不可用",
            PathTooLongException => "文件路径过长",
            XmlException or XmpException => "XMP 元数据格式无效或无法解析",
            InvalidDataException => "文件内容无效或已损坏",
            IOException => "文件读写失败",
            NotSupportedException => "当前文件或存储不支持此操作",
            OutOfMemoryException => "内存不足",
            DllNotFoundException or BadImageFormatException => "所需组件不可用或与当前系统不兼容",
            _ => "发生未预期错误",
        };
    }

    private static string StorageReason(StorageException exception)
    {
        if (exception.Error == StorageError.IoError)
            return exception.InnerException is { } inner ? ReasonFor(inner) : "文件读写失败";
        if (exception.Error == StorageError.AccessDenied && exception.InnerException != null)
            return "没有访问或写入权限";
        if (!string.IsNullOrWhiteSpace(exception.Message)) return exception.Message;

        return exception.Error switch
        {
            StorageError.AccessDenied => "没有访问或写入权限",
            StorageError.AccessRevoked => "访问授权已失效，请重新选择",
            StorageError.ReadOnly => "目标为只读，无法写入",
            StorageError.NotSeekable => "当前存储不支持随机读写",
            StorageError.Unsupported => "当前文件或存储不支持此操作",
            StorageError.ObjectGone => "目标已不存在或无法访问",
            StorageError.ChangedExternally => "文件已被其他程序修改",
            StorageError.InvalidName => "文件名称无效",
            _ => "文件读写失败",
        };
    }

    private static string Compose(string action, string? target, string? reason)
    {
        string subject = string.IsNullOrWhiteSpace(target) ? UnnamedTarget : target.Trim();
        string detail = reason?.Trim() ?? string.Empty;
        return detail.Length > 0 ? $"{action}：{subject}（{detail}）" : $"{action}：{subject}";
    }
}
