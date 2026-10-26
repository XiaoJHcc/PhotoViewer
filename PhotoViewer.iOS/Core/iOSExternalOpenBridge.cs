using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using PhotoViewer.Core.Platform;
using PhotoViewer.Core.Storage;
using PhotoViewer.ViewModels.Main;

namespace PhotoViewer.iOS.Core;

/// <summary>
/// iOS 外部打开桥接（实例级，随应用生命周期唯一）。
/// 负责把 openURL / scene 多 context / 拖入的一批 URL 按原顺序转成本地存储引用，
/// 再投递到共享 <see cref="OpenRequestChannel"/>；来源区分为系统打开、分享、拖入与自动化。
/// 存在性探测在系统授予的安全作用域内完成（外部文件在作用域外不可见，先探测会把目标全部丢掉），
/// 探测结束后立即归还作用域；长期访问权限由打开会话的存储租约
/// （<see cref="iOSStorageAccessSessionFactory"/>）按次持有、结束即归还。
/// </summary>
public sealed class iOSExternalOpenBridge
{
    private readonly OpenRequestChannel _channel;
    private readonly ISecurityScopeAccess _scope;
    private OpenCoordinator? _coordinator;

    /// <summary>绑定打开请求通道；通道可在界面就绪前接收请求并排队。</summary>
    /// <param name="channel">共享打开请求通道。</param>
    public iOSExternalOpenBridge(OpenRequestChannel channel)
        : this(channel, PlatformSecurityScopeAccess.Instance)
    {
    }

    /// <summary>绑定打开请求通道并注入系统作用域调用面（验收替身用）。</summary>
    /// <param name="channel">共享打开请求通道。</param>
    /// <param name="scope">系统作用域调用面。</param>
    internal iOSExternalOpenBridge(OpenRequestChannel channel, ISecurityScopeAccess scope)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    }

    /// <summary>接线共享协调器，供验收路径等待本批处理完成。</summary>
    /// <param name="coordinator">主视图模型的外部打开协调器。</param>
    public void Attach(OpenCoordinator coordinator)
        => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>把单个系统 URL 投递为一次打开请求。</summary>
    /// <param name="url">系统传入的文件 URL；null 时不做任何投递。</param>
    /// <param name="source">请求来源。</param>
    /// <returns>成功投递至少一项时返回 true。</returns>
    public bool Publish(NSUrl? url, OpenRequestSource source)
        => Publish(url == null ? [] : [url], source);

    /// <summary>把一批系统 URL 合并为一次打开请求，顺序与去重后的入参一致。</summary>
    /// <param name="urls">系统传入的文件 URL 集合。</param>
    /// <param name="source">请求来源。</param>
    /// <returns>成功投递至少一项时返回 true。</returns>
    public bool Publish(IEnumerable<NSUrl> urls, OpenRequestSource source)
    {
        var items = Resolve(urls);
        return items.Count > 0 && _channel.Publish(new OpenRequest(items, source));
    }

    /// <summary>
    /// 投递并等待本批处理完成（验收用，不做固定延时猜测）。
    /// 协调器仍按共享规则串行处理，一批只取首个可打开目标。
    /// </summary>
    /// <param name="urls">系统传入的文件 URL 集合。</param>
    /// <param name="source">请求来源。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>投递成功并处理完成时返回 true；没有可打开项时返回 false。</returns>
    public async Task<bool> PublishAndWaitAsync(
        IEnumerable<NSUrl> urls,
        OpenRequestSource source,
        CancellationToken cancellationToken = default)
    {
        var items = Resolve(urls);
        if (items.Count == 0) return false;
        var coordinator = _coordinator
            ?? throw new InvalidOperationException("外部打开协调器尚未接线");
        await coordinator.PublishAndWaitAsync(items, source, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 把系统 URL 按原顺序转成本地引用：目录建文件夹引用、文件建文件引用，
    /// 非文件 URL、不存在目标与重复路径忽略。外部目标保留系统交回的原 URL，
    /// 沙盒内的目标继续使用共享本地引用。
    /// </summary>
    /// <param name="urls">系统传入的文件 URL 集合。</param>
    /// <returns>按入参顺序排列的引用；无可打开项时为空。</returns>
    public IReadOnlyList<IStorageItemRef> Resolve(IEnumerable<NSUrl> urls)
    {
        var items = new List<IStorageItemRef>();
        var seen = new HashSet<StorageItemId>();
        foreach (var url in urls)
        {
            if (url == null || !url.IsFileUrl) continue;
            if (!iOSStorageRefs.TryResolve(url, _scope, out var target) || !seen.Add(target.StorageId)) continue;
            items.Add(iOSStorageRefs.Create(url, target, _scope));
        }

        return items;
    }

}
