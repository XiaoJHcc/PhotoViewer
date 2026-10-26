using System.Runtime.InteropServices;

namespace PhotoViewer.Core.Storage;

/// <summary>
/// 本地路径的存储身份规范化：身份取文件系统认得的实际拼写，<see cref="IStorageItemRef.Path"/> 仍是调用方给的地址。
/// <para>Windows 只做大小写归一（NTFS 默认不区分大小写）；macOS 用 <c>realpath(3)</c> 取实际路径，
/// 所以在默认的不区分大小写卷上 <c>a.jpg</c> / <c>A.JPG</c> 指向同一身份，
/// 而在区分大小写卷上两个各自存在的文件仍是两个身份。</para>
/// <para>iOS 不做这层探测：外部目标的引用在系统安全作用域之外建立，探测结果会随作用域是否生效而变，
/// 同一文件可能得到两个身份；因此 iOS 保持原来的词法身份（作用域只在打开会话里成对持有）。</para>
/// <para>探测只在建立引用时发生一次，<see cref="IStorageItemRef.StorageId"/> 读取不再访问文件系统。</para>
/// </summary>
internal static class LocalStorageIdentity
{
    private const string FilePrefix = "file:";

    // macOS 的 PATH_MAX；realpath(3) 需要能容纳整条路径的输出缓冲。
    private const int MaxPathLength = 1024;

    private static bool s_pathProbeAvailable = true;

    /// <summary>把已规范成绝对路径的本地路径转成 <c>file:</c> 身份值。</summary>
    /// <param name="fullPath">绝对路径。</param>
    /// <returns>可用于身份比较的字符串。</returns>
    internal static string CreateValueFromFullPath(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return FilePrefix + fullPath.ToUpperInvariant();
        }

        if (!OperatingSystem.IsMacOS())
        {
            return FilePrefix + fullPath;
        }

        return FilePrefix + ResolveApplePath(fullPath);
    }

    /// <summary>
    /// 取文件系统实际拼写的绝对路径；无法判定时退回给定路径，绝不让身份查询失败影响调用方。
    /// </summary>
    /// <param name="fullPath">绝对路径。</param>
    /// <returns>文件系统认得的路径，或原路径。</returns>
    private static string ResolveApplePath(string fullPath)
    {
        if (!s_pathProbeAvailable)
        {
            return fullPath;
        }

        try
        {
            // realpath 会一并解出最终项的符号链接；链接本身要保持自身身份，所以最终项是链接时只规范父目录。
            if (!IsSymbolicLink(fullPath) && TryRealPath(fullPath, out var canonical))
            {
                return canonical;
            }

            // 最终项是符号链接或尚不存在：只规范父目录，最终项保留调用方拼写。
            // 这样既不把链接身份并到目标上，也不把尚未创建的文件当成同目录里另一个大小写写法不同的文件。
            if (Path.GetDirectoryName(fullPath) is not { } parent ||
                !TryRealPath(parent, out var canonicalParent))
            {
                return fullPath;
            }

            var name = Path.GetFileName(fullPath);
            return name.Length == 0 ? canonicalParent : Path.Combine(canonicalParent, name);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 平台没有可用的路径探测面时退回词法路径，身份仍然稳定。
            s_pathProbeAvailable = false;
            return fullPath;
        }
        catch (Exception)
        {
            // 身份只是比较用的键：任何探测异常都不该让引用构造失败。
            return fullPath;
        }
    }

    /// <summary>最终项本身是符号链接（含悬空链接）；父目录里的链接不在此判定。</summary>
    /// <param name="path">绝对路径。</param>
    /// <returns>最终项是符号链接时返回 true。</returns>
    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget != null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryRealPath(string path, out string canonical)
    {
        canonical = string.Empty;
        var buffer = Marshal.AllocHGlobal(MaxPathLength);
        try
        {
            Marshal.Copy(new byte[MaxPathLength], 0, buffer, MaxPathLength);
            if (RealPath(path, buffer) == IntPtr.Zero)
            {
                return false;
            }

            var resolved = Marshal.PtrToStringUTF8(buffer);
            if (string.IsNullOrEmpty(resolved) || !Path.IsPathFullyQualified(resolved))
            {
                return false;
            }

            canonical = resolved;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr resolvedPath);
}
