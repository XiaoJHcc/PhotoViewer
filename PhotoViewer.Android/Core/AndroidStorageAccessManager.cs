using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using PhotoViewer.Core.Storage;
using AndroidUri = Android.Net.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 存储访问实现：SAF 的持久授权在选择器返回时已取得，这里做真实校验并补取遗漏的授权。
/// 不通过时返回 typed <see cref="StorageException"/>，不伪造权限。
/// </summary>
public sealed class AndroidStorageAccessManager
{
    private readonly ContentResolver _resolver;

    /// <summary>使用应用级 ContentResolver。</summary>
    public AndroidStorageAccessManager()
        : this(Application.Context.ContentResolver!)
    {
    }

    /// <summary>使用指定 ContentResolver（验收/宿主注入）。</summary>
    /// <param name="resolver">内容解析器。</param>
    public AndroidStorageAccessManager(ContentResolver resolver) =>
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>
    /// 保留访问权限：已有持久读授权时不再动作；否则按读+写尝试取得持久授权（失败回落只读）。
    /// 临时授权（例如 ACTION_VIEW 打开的外部文件）仍可继续使用，但若连读权限都不存在则抛
    /// <see cref="StorageError.AccessDenied"/>。
    /// </summary>
    /// <param name="item">目标存储项。</param>
    public void Retain(IStorageItemRef item)
    {
        if (item == null) return;
        if (item.TryGetLocalPath() != null) return;

        var uri = ResolveUri(item);
        if (uri == null) return;
        if (HasPersistedRead(uri)) return;

        TakePersistable(uri);
        if (!HasReadAccess(uri))
            throw new StorageException(StorageError.AccessDenied, $"无权访问 {item.Name}");
    }

    /// <summary>
    /// 取真实的 Android 文档 Uri，避免经 <see cref="Uri.AbsoluteUri"/> 重新序列化把 authority 变成小写
    /// （Provider 的 authority 大小写敏感，变小写后会解析不到 Provider 或授权）。
    /// </summary>
    private static AndroidUri? ResolveUri(IStorageItemRef item)
    {
        var uri = item switch
        {
            AndroidFileRef file => file.DocumentUri,
            AndroidFolderRef folder => folder.DocumentUri,
            _ => ParseContentUri(item.Path),
        };
        // 只有 content:// 走授权校验；file:// 与本地路径不参与 URI 授权。
        return uri != null
            && string.Equals(uri.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static AndroidUri? ParseContentUri(Uri path)
    {
        if (!string.Equals(path.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return AndroidUri.Parse(path.AbsoluteUri);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool HasReadAccess(AndroidUri uri)
    {
        try
        {
            var context = Application.Context;
            if (context == null) return false;
            var result = context.CheckUriPermission(
                uri, global::Android.OS.Process.MyPid(), global::Android.OS.Process.MyUid(),
                ActivityFlags.GrantReadUriPermission);
            if (result == Permission.Granted) return true;

            // 同一应用内的 Provider 由同 UID 直接访问，不受 URI 授权约束；
            // CheckUriPermission 对“未显式授权的自家 Provider”会返回 Denied，这里按 Provider 归属补正。
            var provider = context.PackageManager?.ResolveContentProvider(
                uri.Authority!, PackageInfoFlags.MatchDefaultOnly);
            int ownUid = global::Android.OS.Process.MyUid();
            bool sameApp = provider != null
                && (string.Equals(provider.PackageName, context.PackageName, StringComparison.Ordinal)
                    || provider.ApplicationInfo?.Uid == ownUid);
            if (!sameApp)
                Console.WriteLine($"[Native] SAF 授权校验拒绝：{uri} provider={provider?.PackageName ?? "null"} "
                    + $"providerUid={provider?.ApplicationInfo?.Uid.ToString() ?? "null"} "
                    + $"own={context.PackageName}/{ownUid}");
            return sameApp;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] SAF 授权校验失败（{uri}）：{exception.Message}");
            return false;
        }
    }

    private bool HasPersistedRead(AndroidUri uri)
    {
        try
        {
            string target = uri.ToString()!;
            foreach (var permission in _resolver.PersistedUriPermissions)
            {
                if (permission?.Uri == null || !permission.IsReadPermission) continue;
                string persisted = permission.Uri.ToString()!;
                if (string.Equals(persisted, target, StringComparison.Ordinal)) return true;
                string prefix = persisted.EndsWith('/') ? persisted : persisted + "/";
                if (target.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Native] 读取 SAF 持久授权列表失败：{exception.Message}");
        }
        return false;
    }

    private void TakePersistable(AndroidUri uri)
    {
        try
        {
            _resolver.TakePersistableUriPermission(
                uri, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            return;
        }
        catch (Exception writeFailure)
        {
            try
            {
                _resolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
                return;
            }
            catch (Exception readFailure)
            {
                Console.WriteLine(
                    $"[Native] SAF 持久授权失败（{uri}）：{readFailure.Message}（读写授权：{writeFailure.Message}）");
            }
        }
    }

}
