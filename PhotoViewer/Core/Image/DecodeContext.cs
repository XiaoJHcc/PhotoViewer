using System;
using System.Threading;

namespace PhotoViewer.Core.Image;

/// <summary>
/// 一次解码请求的"票面":优先级与"沿当前图并行"标记。平台解码器在排队时读它决定通道,
/// 票面可被后来的请求提升——在途去重把 P0 请求并进一条后台解码时,那条解码就地升为 P0,
/// 不必按后台通道排完再让 P0 等(见 <see cref="DecodeContext.CreateTicket"/>)。
/// </summary>
public sealed class DecodeTicket
{
    private int _priority;
    private int _alongside;

    internal DecodeTicket(DecodePriority priority, bool alongside)
    {
        _priority = (int)priority;
        _alongside = alongside ? 1 : 0;
    }

    /// <summary>当前优先级(只会被提升,不会降级)。</summary>
    public DecodePriority Priority => (DecodePriority)Volatile.Read(ref _priority);

    /// <summary>是否"沿当前图并行"(翻页方向上即将上屏的预取)。</summary>
    public bool Alongside => Volatile.Read(ref _alongside) != 0;

    /// <summary>提升票面:优先级只升不降,<paramref name="alongside"/> 只置位不清除。</summary>
    /// <param name="priority">请求方声明的优先级。</param>
    /// <param name="alongside">请求方所在通道是否可沿 P0 并行。</param>
    internal void Promote(DecodePriority priority, bool alongside)
    {
        if (alongside) Volatile.Write(ref _alongside, 1);
        int current;
        while ((int)priority < (current = Volatile.Read(ref _priority)))
        {
            if (Interlocked.CompareExchange(ref _priority, (int)priority, current) == current) return;
        }
    }
}

/// <summary>
/// 解码优先级的**流动**上下文(<see cref="AsyncLocal{T}"/>):<see cref="BitmapLoader"/> 在请求的解码段
/// 内声明该请求的优先级,<see cref="DecodeScheduler"/> 在"可沿 P0 并行"通道里声明 Alongside,
/// 平台解码器据此决定排队通道,不必给 <c>DecodeAsync</c> 契约加参数。
/// </summary>
/// <remarks>
/// 只影响排队与并发,不影响产物:没有声明上下文的解码(缩略图等)一律按普通后台。
/// 上下文随执行流进入 <see cref="System.Threading.Tasks.Task.Run(System.Action)"/> 等线程池工作项,
/// 内层声明只会提升,不会把 Alongside 抹成 false。
/// </remarks>
public static class DecodeContext
{
    private static readonly AsyncLocal<DecodeTicket?> Ambient = new();

    /// <summary>当前执行流的票面;null = 调用方没有声明优先级。</summary>
    public static DecodeTicket? Current => Ambient.Value;

    /// <summary>当前执行流的优先级(未声明按 P2 待)。</summary>
    public static DecodePriority Priority => Ambient.Value?.Priority ?? DecodePriority.P2_Prefetch;

    /// <summary>当前执行流是否"沿当前图并行"。</summary>
    public static bool Alongside => Ambient.Value?.Alongside ?? false;

    /// <summary>
    /// 声明一段带优先级的解码(可嵌套):优先级取更优先者,Alongside 只置位。
    /// </summary>
    /// <param name="priority">本段解码的优先级。</param>
    /// <param name="alongside">本段解码是否可沿 P0 并行。</param>
    /// <returns>释放即恢复外层票面。</returns>
    public static Scope Enter(DecodePriority priority, bool alongside = false)
    {
        var ambient = Ambient.Value;
        return Push(new DecodeTicket(
            ambient is null || priority < ambient.Priority ? priority : ambient.Priority,
            alongside || (ambient?.Alongside ?? false)));
    }

    /// <summary>
    /// 进入指定票面的解码(同一条在途解码的所有请求共享票面,后来者的 P0 提升对解码本身即时生效)。
    /// </summary>
    /// <param name="ticket">解码票面。</param>
    /// <returns>释放即恢复外层票面。</returns>
    public static Scope Enter(DecodeTicket ticket) => Push(ticket);

    /// <summary>
    /// 为一条可能在途被复用的解码建票面:继承当前执行流的 Alongside(P0 不继承),
    /// 后续 <see cref="DecodeTicket.Promote"/> 由合并到同一条解码的请求调用。
    /// </summary>
    /// <param name="priority">发起该解码的请求优先级。</param>
    /// <returns>票面。</returns>
    internal static DecodeTicket CreateTicket(DecodePriority priority)
    {
        var ambient = Ambient.Value;
        return new DecodeTicket(
            ambient is null || priority < ambient.Priority ? priority : ambient.Priority,
            priority != DecodePriority.P0_Current && (ambient?.Alongside ?? false));
    }

    private static Scope Push(DecodeTicket ticket)
    {
        var previous = Ambient.Value;
        Ambient.Value = ticket;
        return new Scope(previous);
    }

    /// <summary>一段票面作用域;释放时恢复进入前的值。</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly DecodeTicket? _previous;

        internal Scope(DecodeTicket? previous) => _previous = previous;

        /// <inheritdoc/>
        public void Dispose() => Ambient.Value = _previous;
    }
}
