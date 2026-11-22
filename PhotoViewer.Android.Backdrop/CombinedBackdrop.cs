using System;
using System.Collections.Generic;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// 多个取样源按顺序叠加，对应上游 <c>backdrops/CombinedBackdrop.kt</c>：每个源用同一份
/// 上下文绘制，后画的盖在先画的上面；坐标相关性与任一源相同。
/// </summary>
/// <remarks>
/// <para>
/// 上游为 2 个、3 个与可变参数分别提供组合，这里用可变参数一份实现。组合只负责叠加，
/// 不改变各源自己的录制、效果链与层变换。
/// </para>
/// <para>
/// 本类不持有离屏效果层。带效果链时由消费者的 <see cref="BackdropEffectScope"/> 把组合结果
/// 整体录进它自己的按消费者尺寸的效果节点，效果作用在合成结果上（与上游把 <c>renderEffect</c>
/// 挂在玻璃元素自己的 <c>GraphicsLayer</c> 同序），不是逐源分别加效果。
/// </para>
/// </remarks>
public sealed class CombinedBackdrop : IBackdrop, IBackdropContentVersion
{
    private readonly IBackdrop[] _backdrops;

    /// <summary>按给定顺序组合取样源。</summary>
    /// <param name="backdrops">取样源，按绘制顺序排列；不得为空或含 <c>null</c>。</param>
    public CombinedBackdrop(params IBackdrop[] backdrops)
    {
        ArgumentNullException.ThrowIfNull(backdrops);
        if (backdrops.Length == 0)
        {
            throw new ArgumentException("至少需要一个取样源。", nameof(backdrops));
        }

        foreach (var backdrop in backdrops)
        {
            ArgumentNullException.ThrowIfNull(backdrop, nameof(backdrops));
        }

        _backdrops = [.. backdrops];
    }

    /// <summary>组合中的取样源，按绘制顺序。</summary>
    public IReadOnlyList<IBackdrop> Backdrops => _backdrops;

    /// <inheritdoc />
    public bool IsCoordinatesDependent
    {
        get
        {
            foreach (var backdrop in _backdrops)
            {
                if (backdrop.IsCoordinatesDependent) return true;
            }

            return false;
        }
    }

    /// <summary>组合内容代际：所有子源都提供稳定代际时取和，否则不可缓存。</summary>
    long IBackdropContentVersion.ContentVersion
    {
        get
        {
            long version = 0;
            foreach (var backdrop in _backdrops)
            {
                if (backdrop is not IBackdropContentVersion child) return -1L;

                var childVersion = child.ContentVersion;
                if (childVersion < 0L) return -1L;
                version = unchecked(version + childVersion);
            }

            return version;
        }
    }

    /// <inheritdoc />
    public void DrawBackdrop(Gfx.Canvas canvas, in BackdropDrawContext context)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        foreach (var backdrop in _backdrops)
        {
            backdrop.DrawBackdrop(canvas, context);
        }
    }
}
