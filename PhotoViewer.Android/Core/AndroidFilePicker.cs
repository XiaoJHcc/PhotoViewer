using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using PhotoViewer.Core.Storage;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// SAF 文件/文件夹选择器：<c>ACTION_OPEN_DOCUMENT_TREE</c> 选文件夹、<c>ACTION_OPEN_DOCUMENT</c> 选单文件；
/// 结果 Uri 在返回时按 Intent 标志取得持久读/写授权，单文件再在后台读取真实显示名/属性后构造存储引用。
/// 等待中的请求是实例状态：新请求、Activity 销毁或重新绑定都会把旧任务按取消收口，调用方不会永久等待；
/// 后台属性查询完成时按等待项身份做代际检查，被取代的旧结果不会复活新请求。
/// </summary>
public sealed class AndroidFilePicker : IFilePickerService
{
    private const int FolderRequestCode = 0x5056;
    /// <summary>单文件请求码；验收可直接用 <see cref="HandleActivityResult"/> 投递结果，产品路径不引用。</summary>
    internal const int FileRequestCode = 0x5057;

    private TaskCompletionSource<IFolderRef?>? _folderPending;
    private TaskCompletionSource<IFileRef?>? _filePending;
    private Activity _activity;

    /// <summary>绑定宿主 Activity。</summary>
    /// <param name="activity">原生壳 Activity。</param>
    public AndroidFilePicker(Activity activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
    }

    /// <summary>当前绑定的宿主 Activity（诊断用）。</summary>
    internal Activity Activity => _activity;

    /// <summary>触屏端主入口为目录选择（空首屏与「打开」都进入文件夹选择器）。</summary>
    public bool PreferFolderPicker => true;

    /// <summary>
    /// 宿主 Activity 重建（字体/语言等会导致重建）后指向新实例；旧请求按取消收口。
    /// </summary>
    /// <param name="activity">新的宿主 Activity。</param>
    internal void Rebind(Activity activity)
    {
        if (activity == null || ReferenceEquals(activity, _activity)) return;
        CancelPending();
        _activity = activity;
    }

    /// <summary>弹出文件夹选择器。</summary>
    /// <param name="title">标题。</param>
    /// <returns>用户选择的文件夹；取消返回 null。</returns>
    public Task<IFolderRef?> PickFolderAsync(string title)
    {
        CancelPending();
        var pending = new TaskCompletionSource<IFolderRef?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _folderPending = pending;
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
            if (ReferenceEquals(_folderPending, pending)) _folderPending = null;
            pending.TrySetResult(null);
        }
        return pending.Task;
    }

    /// <summary>弹出单文件选择器。</summary>
    /// <param name="title">标题。</param>
    /// <param name="filters">类型过滤项。</param>
    /// <returns>用户选择的文件；取消返回 null。</returns>
    public Task<IFileRef?> PickFileAsync(string title, IReadOnlyList<FilePickFilter> filters)
    {
        CancelPending();
        var pending = new TaskCompletionSource<IFileRef?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _filePending = pending;
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
            if (ReferenceEquals(_filePending, pending)) _filePending = null;
            pending.TrySetResult(null);
        }
        return pending.Task;
    }

    /// <summary>
    /// 宿主 Activity 的 <c>OnActivityResult</c> 转发；只处理本选择器发出的请求。
    /// </summary>
    /// <param name="requestCode">请求码。</param>
    /// <param name="resultCode">结果码。</param>
    /// <param name="data">返回 Intent。</param>
    /// <returns>是否由本选择器消费。</returns>
    public bool HandleActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode == FolderRequestCode)
        {
            CompleteFolderRequest(resultCode, data);
            return true;
        }
        if (requestCode == FileRequestCode)
        {
            CompleteFileRequest(resultCode, data);
            return true;
        }
        return false;
    }

    private void CompleteFolderRequest(Result resultCode, Intent? data)
    {
        var pending = _folderPending;
        _folderPending = null;
        if (pending == null)
        {
            Console.WriteLine($"[Native] SAF 选择结果没有等待中的请求（request={FolderRequestCode}），已忽略");
            return;
        }

        if (!TryGetResultUri(resultCode, data, out var resolver, out var uri))
        {
            pending.TrySetResult(null);
            return;
        }

        try
        {
            var folder = new AndroidFolderRef(resolver!, uri!, null);
            pending.TrySetResult(folder);
            Console.WriteLine($"[Native] SAF 文件夹选择结果：{folder.Name}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] SAF 选择结果解析失败：{exception.Message}");
            pending.TrySetResult(null);
        }
    }

    private void CompleteFileRequest(Result resultCode, Intent? data)
    {
        var pending = _filePending;
        if (pending == null)
        {
            Console.WriteLine($"[Native] SAF 选择结果没有等待中的请求（request={FileRequestCode}），已忽略");
            return;
        }

        if (!TryGetResultUri(resultCode, data, out var resolver, out var uri))
        {
            TryCompleteFileRequest(pending, null);
            return;
        }

        // 取得授权期间可能已被新请求/取消/销毁取代：不再发起后台查询（结果也无人接收）。
        if (!ReferenceEquals(_filePending, pending)) return;

        // 显示名/属性是 binder 查询：交给后台完成，UI 线程只保留等待项身份。
        _ = ResolveFileResultAsync(resolver!, uri!, pending);
    }

    /// <summary>
    /// 后台读完真实属性后完成请求（与外部打开共用 <see cref="AndroidFileRef.CreateForContentUriAsync"/>）：
    /// 数字 documentId 的 Uri 末段不是文件名，按扩展名判类型的打开链会直接丢弃。
    /// 期间被新请求、<see cref="CancelPending"/> 或 Activity 销毁取代时丢弃结果，旧结果不会复活新请求。
    /// </summary>
    private async Task ResolveFileResultAsync(ContentResolver resolver, AndroidUri uri,
        TaskCompletionSource<IFileRef?> pending)
    {
        IFileRef? file = null;
        try
        {
            file = await AndroidFileRef.CreateForContentUriAsync(resolver, uri).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Native] SAF 选择结果属性读取失败：{exception.Message}");
        }

        if (!TryCompleteFileRequest(pending, file))
            Console.WriteLine("[Native] SAF 文件选择结果已过期，丢弃");
    }

    /// <summary>
    /// 仅当该等待项仍是当前请求时落结果并清空等待项；返回是否真正完成（代际检查）。
    /// 与 <see cref="CancelPending"/> 并发时以先完成者为准，后到的一方不得覆盖。
    /// </summary>
    private bool TryCompleteFileRequest(TaskCompletionSource<IFileRef?> pending, IFileRef? file)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _filePending, null, pending), pending)) return false;
        if (file != null) Console.WriteLine($"[Native] SAF 文件选择结果：{file.Name}");
        return pending.TrySetResult(file);
    }

    private bool TryGetResultUri(Result resultCode, Intent? data, out ContentResolver? resolver, out AndroidUri? uri)
    {
        resolver = null;
        uri = data?.Data;
        if (resultCode != Result.Ok || uri == null)
        {
            Console.WriteLine("[Native] SAF 选择结果：取消");
            return false;
        }

        resolver = _activity.ContentResolver ?? Application.Context.ContentResolver;
        if (resolver == null)
        {
            Console.Error.WriteLine("[Native] SAF 选择结果无法取得 ContentResolver");
            return false;
        }

        TakePersistablePermission(resolver, data!, uri);
        return true;
    }

    /// <summary>新请求或 Activity 销毁时把等待中的任务按取消收口。</summary>
    public void CancelPending()
    {
        var folder = _folderPending;
        var file = Interlocked.Exchange(ref _filePending, null);
        _folderPending = null;
        folder?.TrySetResult(null);
        file?.TrySetResult(null);
    }

    /// <summary>
    /// 验收用：注册一个等待中的单文件请求（不启动系统选择器），随后用
    /// <see cref="HandleActivityResult"/> 直接投递结果；产品路径不引用。
    /// </summary>
    /// <returns>该请求的完成任务；被新请求或 <see cref="CancelPending"/> 取代时完成为 null。</returns>
    internal Task<IFileRef?> BeginFileRequestForChecks()
    {
        CancelPending();
        var pending = new TaskCompletionSource<IFileRef?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _filePending = pending;
        return pending.Task;
    }

    /// <summary>
    /// 按返回 Intent 的标志取得持久授权。没有 <c>GrantPersistableUriPermission</c> 时只保留本次会话的临时授权，
    /// 不伪造持久权限；失败只记录（临时授权仍可读）。
    /// </summary>
    private static void TakePersistablePermission(ContentResolver resolver, Intent data, AndroidUri uri)
    {
        try
        {
            int flags = (int)(data.Flags & (ActivityFlags.GrantReadUriPermission
                | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission));
            if ((flags & (int)ActivityFlags.GrantPersistableUriPermission) == 0)
            {
                Console.WriteLine($"[Native] SAF 返回未带持久授权标志，保留临时授权：{uri}");
                return;
            }

            int take = flags & ~(int)ActivityFlags.GrantPersistableUriPermission;
            if ((take & ((int)ActivityFlags.GrantReadUriPermission | (int)ActivityFlags.GrantWriteUriPermission)) == 0)
                take = (int)ActivityFlags.GrantReadUriPermission;
            resolver.TakePersistableUriPermission(uri, (ActivityFlags)take);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] SAF 持久授权失败（{uri}）：{exception.Message}");
        }
    }
}
