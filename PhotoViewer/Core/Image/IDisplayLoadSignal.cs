using System;
using PhotoViewer.Core.Storage;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 主图"上屏完成"信号源：旧壳 <c>ImageViewModel</c> 与原生 <see cref="PhotoLoadSession"/> 各实现一份，
/// 压测驱动器（P1-M5）据此计时与判读代理档，不再依赖任一壳的具体类型。
/// </summary>
public interface IDisplayLoadSignal
{
    /// <summary>当前上屏图是否代理档。</summary>
    bool CurrentIsProxy { get; }

    /// <summary>当前上屏产物的像素编码（无图时为 null）；压测据此把编码协商结果写进逐张记录。</summary>
    PixelEncoding? CurrentEncoding { get; }

    /// <summary>
    /// 订阅一次上屏完成（回调参数为该次上屏的文件引用），返回退订句柄。
    /// 回调在 UI 线程触发；句柄 Dispose 后退订，可安全重复 Dispose。
    /// </summary>
    /// <param name="handler">上屏完成回调。</param>
    /// <returns>退订句柄。</returns>
    IDisposable Subscribe(Action<IFileRef> handler);
}

/// <summary>把退订动作包成句柄（订阅方标准写法：<c>using var subscription = signal.Subscribe(...)</c>）。</summary>
internal sealed class DelegateSubscription : IDisposable
{
    private readonly Action _unsubscribe;
    private bool _disposed;

    /// <summary>绑定退订动作。</summary>
    /// <param name="unsubscribe">退订动作，仅在首次 Dispose 时执行。</param>
    public DelegateSubscription(Action unsubscribe) => _unsubscribe = unsubscribe;

    /// <summary>退订（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _unsubscribe();
    }
}
