using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using PhotoViewer.Core.Storage;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// SAF 文件/文件夹选择器（A2）：<c>ACTION_OPEN_DOCUMENT_TREE</c> 选文件夹、<c>ACTION_OPEN_DOCUMENT</c> 选单文件；
/// 结果 Uri 立刻 <c>takePersistableUriPermission(Read|Write)</c>，返回原生存储引用。
/// 单实例走 <c>StartActivityForResult</c>，由宿主 Activity 的 <see cref="HandleActivityResult"/> 收口。
/// </summary>
public sealed class AndroidFilePicker : IFilePickerService
{
    private const int FolderRequestCode = 0x5056;
    private const int FileRequestCode = 0x5057;
    private static TaskCompletionSource<IFolderRef?>? _folderPending;
    private static TaskCompletionSource<IFileRef?>? _filePending;
    private readonly Activity _activity;

    /// <summary>绑定宿主 Activity。</summary>
    /// <param name="activity">原生壳 Activity。</param>
    public AndroidFilePicker(Activity activity) => _activity = activity;

    /// <summary>弹出文件夹选择器。</summary>
    /// <param name="title">标题。</param>
    /// <returns>用户选择的文件夹；取消返回 null。</returns>
    public Task<IFolderRef?> PickFolderAsync(string title)
    {
        CancelPending();
        _folderPending = new TaskCompletionSource<IFolderRef?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission
                | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
            _activity.StartActivityForResult(intent, FolderRequestCode);
            Console.WriteLine($"[Native] SAF 文件夹选择器已打开：{title}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] SAF 文件夹选择器失败：{exception.Message}");
            _folderPending.TrySetResult(null);
        }
        return _folderPending.Task;
    }

    /// <summary>弹出单文件选择器。</summary>
    /// <param name="title">标题。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>用户选择的文件；取消返回 null。</returns>
    public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        CancelPending();
        _filePending = new TaskCompletionSource<IFileRef?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/*");
            intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission
                | ActivityFlags.GrantPersistableUriPermission);
            _activity.StartActivityForResult(intent, FileRequestCode);
            Console.WriteLine($"[Native] SAF 单文件选择器已打开：{title}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] SAF 单文件选择器失败：{exception.Message}");
            _filePending.TrySetResult(null);
        }
        return _filePending.Task;
    }

    /// <summary>
    /// 宿主 Activity 的 <c>OnActivityResult</c> 转发；成功时保留持久读/写权限。
    /// </summary>
    /// <param name="requestCode">请求码。</param>
    /// <param name="resultCode">结果码。</param>
    /// <param name="data">返回 Intent。</param>
    /// <returns>是否由本选择器消费。</returns>
    public static bool HandleActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != FolderRequestCode && requestCode != FileRequestCode) return false;
        var uri = data?.Data;
        bool success = resultCode == Result.Ok && uri != null;
        if (success)
        {
            try
            {
                var resolver = Application.Context.ContentResolver!;
                int flags = (int)(data!.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission));
                resolver.TakePersistableUriPermission(uri!, (ActivityFlags)(flags == 0
                    ? (int)ActivityFlags.GrantReadUriPermission
                    : flags));
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[Native] SAF 持久授权失败（{uri}）：{exception.Message}");
            }
        }
        if (requestCode == FolderRequestCode)
        {
            IFolderRef? folder = success ? new AndroidFolderRef(Application.Context.ContentResolver!, uri!, null) : null;
            _folderPending?.TrySetResult(folder);
            _folderPending = null;
            Console.WriteLine($"[Native] SAF 文件夹选择结果：{(folder == null ? "取消" : folder.Name)}");
        }
        else
        {
            IFileRef? file = success
                ? new AndroidFileRef(Application.Context.ContentResolver!, uri!, null, uri!.LastPathSegment, null, null)
                : null;
            _filePending?.TrySetResult(file);
            _filePending = null;
            Console.WriteLine($"[Native] SAF 文件选择结果：{(file == null ? "取消" : file.Name)}");
        }
        return true;
    }

    /// <summary>新请求覆盖旧请求：旧任务按取消收口，避免调用方永久等待。</summary>
    private static void CancelPending()
    {
        _folderPending?.TrySetResult(null);
        _filePending?.TrySetResult(null);
        _folderPending = null;
        _filePending = null;
    }
}
