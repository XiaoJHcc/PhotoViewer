using Foundation;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// 系统安全作用域的调用面。
/// 系统授予的访问权限挂在<b>系统交回的那个 <see cref="NSUrl"/> 实例</b>上：按路径重建的 URL 不带沙盒扩展，
/// <see cref="NSUrl.StartAccessingSecurityScopedResource"/> 会直接返回 false。因此一次访问必须用同一 URL 实例
/// 成对 Start/Stop。单独的接口只为验收注入可观察替身；产品路径只走 <see cref="PlatformSecurityScopeAccess"/>。
/// </summary>
internal interface ISecurityScopeAccess
{
    /// <summary>启动一次系统作用域；返回 false 表示该 URL 不需要或未获得系统作用域。</summary>
    /// <param name="url">系统授予的原 URL。</param>
    /// <returns>是否真正启动了作用域（只有启动过才需要配对 Stop）。</returns>
    bool Start(NSUrl url);

    /// <summary>结束一次系统作用域。</summary>
    /// <param name="url">启动作用域时使用的同一 URL 实例。</param>
    void Stop(NSUrl url);
}

/// <summary>平台实现：直接调用 <see cref="NSUrl"/> 的安全作用域方法，不做任何路径重建。</summary>
internal sealed class PlatformSecurityScopeAccess : ISecurityScopeAccess
{
    /// <summary>无状态实现，进程内共用一份。</summary>
    internal static PlatformSecurityScopeAccess Instance { get; } = new();

    /// <summary>启动系统作用域。</summary>
    /// <param name="url">系统授予的原 URL。</param>
    /// <returns>系统是否授予了作用域。</returns>
    public bool Start(NSUrl url) => url.StartAccessingSecurityScopedResource();

    /// <summary>结束系统作用域。</summary>
    /// <param name="url">启动作用域时使用的同一 URL 实例。</param>
    public void Stop(NSUrl url) => url.StopAccessingSecurityScopedResource();
}

/// <summary>在有效作用域内执行探测的辅助方法（存在性、目录可枚举性等）。</summary>
internal static class iOSSecurityScope
{
    /// <summary>
    /// 启动 URL 的作用域后执行一次探测，随后按启动结果成对结束作用域。
    /// 探测在作用域失效（文件/目录不可见、越权）时抛出的异常按"不可访问"处理，不向上扩散。
    /// </summary>
    /// <param name="scope">系统作用域调用面。</param>
    /// <param name="url">系统授予的原 URL。</param>
    /// <param name="probe">作用域内的探测；返回 true 表示目标可用。</param>
    /// <param name="started">是否真正启动了系统作用域（供调用方判断 URL 是否携带授权）。</param>
    /// <returns>探测结果；启动或探测失败都为 false。</returns>
    internal static bool TryProbe(ISecurityScopeAccess scope, NSUrl url, Func<bool> probe, out bool started)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(probe);

        started = false;
        try
        {
            started = scope.Start(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS 作用域启动失败：{ex.Message}");
            return false;
        }

        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StorageAccess] iOS 作用域内探测失败：{ex.Message}");
            return false;
        }
        finally
        {
            if (started)
            {
                try
                {
                    scope.Stop(url);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StorageAccess] iOS 作用域结束失败：{ex.Message}");
                }
            }
        }
    }
}
