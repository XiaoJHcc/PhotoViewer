using System;
using Avalonia.Input;
using PhotoViewer.Core.Input;

namespace PhotoViewer.Platform;

/// <summary>
/// Avalonia 输入事件与 Core 手势模型之间的边界转换。
/// 键按名称互转(<see cref="GestureKey"/> 成员名与 <see cref="Key"/> 逐一对应);
/// 修饰键数值完全一致,直接强转。
/// </summary>
public static class AvaloniaGestureMapping
{
    /// <summary>
    /// Avalonia 键转平台中立键;未知名称兜底为 <see cref="GestureKey.None"/>。
    /// </summary>
    /// <param name="key">Avalonia 键。</param>
    /// <returns>平台中立键。</returns>
    public static GestureKey ToCore(Key key)
        => Enum.TryParse<GestureKey>(key.ToString(), out var result) ? result : GestureKey.None;

    /// <summary>
    /// 平台中立键转 Avalonia 键;未知名称兜底为 <see cref="Key.None"/>。
    /// </summary>
    /// <param name="key">平台中立键。</param>
    /// <returns>Avalonia 键。</returns>
    public static Key ToAvalonia(GestureKey key)
        => Enum.TryParse<Key>(key.ToString(), out var result) ? result : Key.None;

    /// <summary>
    /// Avalonia 修饰键转平台中立修饰键(数值一致,直接强转)。
    /// </summary>
    /// <param name="mods">Avalonia 修饰键。</param>
    /// <returns>平台中立修饰键。</returns>
    public static GestureModifiers ToCore(KeyModifiers mods) => (GestureModifiers)(int)mods;

    /// <summary>
    /// 平台中立修饰键转 Avalonia 修饰键(数值一致,直接强转)。
    /// </summary>
    /// <param name="mods">平台中立修饰键。</param>
    /// <returns>Avalonia 修饰键。</returns>
    public static KeyModifiers ToAvalonia(GestureModifiers mods) => (KeyModifiers)(int)mods;
}
