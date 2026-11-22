using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Gfx = Android.Graphics;

namespace PhotoViewer.Android.Backdrop;

/// <summary>
/// AGSL 运行时着色器，对应上游 <c>RuntimeShader.kt</c> 的 <c>RuntimeShader</c> 接口与
/// <c>AndroidRuntimeShader</c>；API 33（Tiramisu）起可用。
/// </summary>
/// <remarks>
/// 包装平台 <see cref="Gfx.RuntimeShader"/>，保留上游全部 uniform 重载；着色器字符串由调用方
/// 逐字给出（本库的常量在 <see cref="BackdropShaders"/>）。低于 API 33 时构造会抛
/// <see cref="PlatformNotSupportedException"/>，调用方先用
/// <see cref="BackdropCapabilities.IsRuntimeShaderSupported"/> 分级。
/// </remarks>
public sealed class BackdropRuntimeShader : IDisposable
{
    private bool _disposed;

    /// <summary>编译并包装一段 AGSL。</summary>
    /// <param name="shaderString">AGSL 着色器源码。</param>
    [SupportedOSPlatform("android33.0")]
    public BackdropRuntimeShader(string shaderString)
    {
        ArgumentException.ThrowIfNullOrEmpty(shaderString);
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            throw new PlatformNotSupportedException("RuntimeShader 需要 API 33（Tiramisu）及以上。");
        }

        PlatformShader = new Gfx.RuntimeShader(shaderString);
    }

    /// <summary>平台着色器；既可作为 <see cref="Gfx.Paint.Shader"/>，也可用作运行时效果输入。</summary>
    [SupportedOSPlatform("android33.0")]
    public Gfx.RuntimeShader PlatformShader { get; }

    /// <summary>单个 float uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value">值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetFloatUniform(string name, float value) => PlatformShader.SetFloatUniform(name, value);

    /// <summary>两个 float uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetFloatUniform(string name, float value1, float value2) =>
        PlatformShader.SetFloatUniform(name, value1, value2);

    /// <summary>三个 float uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    /// <param name="value3">第三个值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetFloatUniform(string name, float value1, float value2, float value3) =>
        PlatformShader.SetFloatUniform(name, value1, value2, value3);

    /// <summary>四个 float uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    /// <param name="value3">第三个值。</param>
    /// <param name="value4">第四个值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetFloatUniform(string name, float value1, float value2, float value3, float value4) =>
        PlatformShader.SetFloatUniform(name, value1, value2, value3, value4);

    /// <summary>float 数组 uniform（float2 / float4 等）。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="values">值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetFloatUniform(string name, float[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PlatformShader.SetFloatUniform(name, values);
    }

    /// <summary>单个 int uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value">值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetIntUniform(string name, int value) => PlatformShader.SetIntUniform(name, value);

    /// <summary>两个 int uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetIntUniform(string name, int value1, int value2) =>
        PlatformShader.SetIntUniform(name, value1, value2);

    /// <summary>三个 int uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    /// <param name="value3">第三个值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetIntUniform(string name, int value1, int value2, int value3) =>
        PlatformShader.SetIntUniform(name, value1, value2, value3);

    /// <summary>四个 int uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="value1">第一个值。</param>
    /// <param name="value2">第二个值。</param>
    /// <param name="value3">第三个值。</param>
    /// <param name="value4">第四个值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetIntUniform(string name, int value1, int value2, int value3, int value4) =>
        PlatformShader.SetIntUniform(name, value1, value2, value3, value4);

    /// <summary>int 数组 uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="values">值。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetIntUniform(string name, int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PlatformShader.SetIntUniform(name, values);
    }

    /// <summary><c>layout(color) uniform half4</c> 颜色 uniform。</summary>
    /// <param name="name">uniform 名。</param>
    /// <param name="color">颜色。</param>
    [SupportedOSPlatform("android33.0")]
    public void SetColorUniform(string name, Gfx.Color color) => PlatformShader.SetColorUniform(name, color);

    /// <summary>释放平台着色器。</summary>
    [SupportedOSPlatform("android33.0")]
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PlatformShader.Dispose();
    }
}

/// <summary>
/// 按 key 复用着色器，对应上游 <c>RuntimeShaderCache.kt</c>。
/// </summary>
/// <remarks>
/// 一个组件（或一个绘制器）持有一份缓存：uniform 是着色器对象上的状态，同一帧内多个组件
/// 共用同一个着色器实例会让后设的 uniform 覆盖先前的绘制。上游同样按绘制节点各自持有缓存。
/// </remarks>
public sealed class BackdropRuntimeShaderCache
{
    private readonly Dictionary<string, BackdropRuntimeShader> _shaders = new(StringComparer.Ordinal);

    /// <summary>取（或首次编译）指定 key 的着色器。</summary>
    /// <param name="key">缓存键。</param>
    /// <param name="shaderString">AGSL 源码；同一 key 的后续调用忽略该参数。</param>
    /// <returns>着色器实例。</returns>
    [SupportedOSPlatform("android33.0")]
    public BackdropRuntimeShader Obtain(string key, string shaderString)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(shaderString);
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            throw new PlatformNotSupportedException("RuntimeShader 需要 API 33（Tiramisu）及以上。");
        }

        if (_shaders.TryGetValue(key, out var shader)) return shader;

        shader = new BackdropRuntimeShader(shaderString);
        _shaders.Add(key, shader);
        return shader;
    }

    /// <summary>丢弃全部缓存；平台着色器留给 GC，与上游 <c>clear()</c> 一致。</summary>
    public void Clear() => _shaders.Clear();
}
