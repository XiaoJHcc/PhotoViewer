using System;
using System.Collections.Generic;
using System.Linq;
using Android.App;
using Android.Content;
using Java.Lang;
using PhotoViewer.Core;
using AndroidUri = Android.Net.Uri;
using SystemUri = System.Uri;

namespace PhotoViewer.Android.Core;

/// <summary>
/// Android 外部打开桥接。
/// 负责把系统 Intent 中的文件 URI 提取出来，并转发给共享层。
/// </summary>
public static class AndroidExternalOpenBridge
{
    /// <summary>
    /// Intent 中解析出的 URI 对。
    /// </summary>
    private sealed class ResolvedIntentUri
    {
        public required AndroidUri PlatformUri { get; init; }

        public required SystemUri ManagedUri { get; init; }
    }

    /// <summary>
    /// 尝试处理 Android Intent，并将可识别的图片请求发布到共享层。
    /// </summary>
    /// <param name="activity">当前 Activity，用于接管 URI 读取权限</param>
    /// <param name="intent">系统传入的 Intent</param>
    public static void PublishFromIntent(Activity activity, Intent? intent)
    {
        if (intent == null)
        {
            return;
        }

        var resolvedUris = ExtractUris(intent);
        if (resolvedUris.Count == 0)
        {
            return;
        }

        foreach (var uri in resolvedUris)
        {
            TryTakeReadPermission(activity, intent, uri.PlatformUri);
        }

        ExternalOpenService.PublishFiles(resolvedUris.Select(uri => uri.ManagedUri), source: $"Android:{intent.Action ?? "Unknown"}");
    }

    /// <summary>
    /// 从 Intent 中提取候选 URI。
    /// 优先读取 ClipData，再兼容 ACTION_VIEW / ACTION_SEND 常见字段。
    /// </summary>
    /// <param name="intent">系统传入的 Intent</param>
    private static List<ResolvedIntentUri> ExtractUris(Intent intent)
    {
        var results = new List<ResolvedIntentUri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUri(AndroidUri? uri)
        {
            if (uri == null)
            {
                return;
            }

            if (!SystemUri.TryCreate(uri.ToString(), UriKind.Absolute, out var managedUri))
            {
                return;
            }

            if (seen.Add(managedUri.AbsoluteUri))
            {
                results.Add(new ResolvedIntentUri
                {
                    PlatformUri = uri,
                    ManagedUri = managedUri
                });
            }
        }

        if (intent.ClipData != null)
        {
            for (var i = 0; i < intent.ClipData.ItemCount; i++)
            {
                AddUri(intent.ClipData.GetItemAt(i)?.Uri);
            }
        }

        AddUri(intent.Data);

#pragma warning disable CS0618
        if (intent.GetParcelableExtra(Intent.ExtraStream) is AndroidUri extraStreamUri)
        {
            AddUri(extraStreamUri);
        }

        if (intent.GetParcelableArrayListExtra(Intent.ExtraStream) is System.Collections.IEnumerable extraStreamList)
        {
            foreach (var item in extraStreamList)
            {
                AddUri(item as AndroidUri);
            }
        }
#pragma warning restore CS0618

        return results;
    }

    /// <summary>
    /// 尝试为内容 URI 接管读权限。
    /// 某些文件管理器会附带一次性授权；若可持久化，则尽量持久化。
    /// </summary>
    /// <param name="activity">当前 Activity</param>
    /// <param name="intent">系统传入的 Intent</param>
    /// <param name="uri">要申请权限的 URI</param>
    private static void TryTakeReadPermission(Activity activity, Intent intent, AndroidUri uri)
    {
        if (!string.Equals(uri.Scheme, ContentResolver.SchemeContent, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var takeFlags = intent.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            if (takeFlags == 0)
            {
                takeFlags = ActivityFlags.GrantReadUriPermission;
            }

            activity.ContentResolver?.TakePersistableUriPermission(uri, takeFlags);
        }
        catch (SecurityException ex)
        {
            System.Console.WriteLine($"TakePersistableUriPermission skipped: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Persist URI permission failed: {ex.Message}");
        }
    }
}




