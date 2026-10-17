using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 工具窗口自有的文件夹/CSV 保存选择器。WinRT 选择器在非打包应用里必须先绑定窗口句柄,
/// 这里绑定工具窗口(而不是主窗口),选择器才会相对工具窗口弹出。
/// </summary>
internal static class WindowsToolsDialogs
{
    /// <summary>选择照片数据统计要扫描的文件夹。</summary>
    /// <param name="windowHandle">工具窗口句柄提供者</param>
    /// <returns>文件夹本地路径;取消时返回 null</returns>
    public static async Task<string?> PickFolderAsync(Func<IntPtr> windowHandle)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>选择照片数据统计 CSV 的输出位置。</summary>
    /// <param name="windowHandle">工具窗口句柄提供者</param>
    /// <returns>CSV 本地路径;取消时返回 null</returns>
    public static async Task<string?> PickCsvAsync(Func<IntPtr> windowHandle)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "照片数据统计",
        };
        picker.FileTypeChoices.Add("CSV 文件", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
