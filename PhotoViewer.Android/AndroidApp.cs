using System;
using Android.App;
using Android.Runtime;

namespace PhotoViewer.Android;

/// <summary>
/// Android 进程级 Application。
/// **这里不初始化 Avalonia**：原生壳不应为了启动而创建任何 Avalonia 对象；
/// 旧壳的 Avalonia 由 <see cref="MainActivity"/> 在 <c>OnCreate</c> 前自建。
/// </summary>
[Application]
public sealed class AndroidApp : Application
{
    /// <summary>
    /// Java 侧构造入口（Android 运行时调用）。
    /// </summary>
    /// <param name="javaReference">Java 对象引用。</param>
    /// <param name="transfer">引用所有权。</param>
    public AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }
}
