using System.Xml;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;

namespace CoreContractCheck;

internal static class StatusMessageChecks
{
    public static void Run(Checks c)
    {
        c.Section("浮窗错误文案归因");
        c.Equal("打开失败映射权限原因", "无法打开：a.jpg（没有访问权限）",
            StatusMessages.OpenFailed("a.jpg", StatusMessages.ReasonFor(new UnauthorizedAccessException("Access denied"))));
        c.Equal("文件夹失败映射丢失目标", "无法读取文件夹：照片（文件或文件夹已不存在，或路径不可用）",
            StatusMessages.FolderOpenFailed("照片", StatusMessages.ReasonFor(new DirectoryNotFoundException("Could not find directory"))));
        c.Equal("保留类型化存储原因", "无法打开：a.jpg（a.jpg 为只读文件）",
            StatusMessages.OpenFailed("a.jpg", StatusMessages.ReasonFor(new StorageException(StorageError.AccessDenied, "a.jpg 为只读文件"))));
        c.Equal("Full 失败包含对象与解码原因", "无法解码全分辨率图像：a.jpg（文件内容无效或已损坏）",
            StatusMessages.FullLoadFailed("a.jpg", StatusMessages.ReasonFor(new InvalidDataException("invalid image"))));
        c.Equal("XMP 错误不泄露解析器原文", "XMP 元数据格式无效或无法解析",
            StatusMessages.ReasonFor(new XmlException("Unexpected token at line 1")));
        c.Equal("未知异常不暴露类型和内部消息", "发生未预期错误",
            StatusMessages.ReasonFor(new InvalidOperationException("CLR internal failure")));
        c.Equal("保留解码器明确给出的用户原因", "系统不支持该 RAW 的格式或相机机型",
            StatusMessages.ReasonFor(new UserFacingException("系统不支持该 RAW 的格式或相机机型")));
        c.Equal("无原因时保持对象与动作", "无法写入星级：a.jpg",
            StatusMessages.RatingWriteFailed("a.jpg"));
        c.Equal("部分写入保留独立动作", "星级写入不完整：a.jpg",
            StatusMessages.RatingWriteFailed("a.jpg", partial: true));
    }
}
