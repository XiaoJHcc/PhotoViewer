using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using PhotoViewer.Core.Platform;

namespace PhotoViewer.Windows.Native;

/// <summary>
/// 单实例注册与二次激活接线。
/// 主实例独占一个固定键并把 <see cref="AppInstance.Activated"/> 转发来的激活参数转成同一
/// <see cref="OpenRequestChannel"/> 的打开请求；二次启动在启动早期把激活转发给主实例后退出。
/// 只做进程内实例管理，不改注册表：非打包构建没有文件关联注册，系统"双击图片打开"需另配注册表或安装包。
/// </summary>
internal static class WindowsSingleInstance
{
    /// <summary>主实例键；同一用户下同键只允许一个实例。</summary>
    private const string InstanceKey = "PhotoViewer.Desktop.Main";

    private static readonly object SyncRoot = new();

    /// <summary>主实例对象；键注册后一直持有，进程退出时由系统回收。</summary>
    private static AppInstance? s_primary;

    /// <summary>激活请求的投递目标（启动期通道，界面就绪前的请求在通道里排队）。</summary>
    private static OpenRequestChannel? s_channel;

    /// <summary>主窗口就绪后的前置动作。</summary>
    private static Action? s_foreground;

    /// <summary>前置动作订阅之前到达的"无路径再次启动"。</summary>
    private static bool s_foregroundPending;

    /// <summary>
    /// 在创建任何窗口之前决定本进程身份：主实例继续启动并订阅转发激活；
    /// 二次实例把<b>本次</b>激活转发给主实例后返回 false，调用方应直接退出。
    /// 实例 API 不可用时降级为多实例继续启动，不因单实例机制失败而无法看图。
    /// </summary>
    /// <param name="channel">启动期打开请求通道。</param>
    /// <returns>本进程是否作为主实例继续启动。</returns>
    internal static bool TryBecomePrimary(OpenRequestChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (instance.IsCurrent)
            {
                s_channel = channel;
                s_primary = instance;
                instance.Activated += OnActivated;
                return true;
            }

            RedirectToPrimary(activation, instance);
            return false;
        }
        catch (Exception exception)
        {
            NativeStartupLog.Write("single instance unavailable: " + exception.Message);
            return true;
        }
    }

    /// <summary>
    /// 主窗口就绪后接管前置动作；订阅之前到达的无路径激活在这里补发一次，
    /// 避免窗口还没建好时点第二次图标什么也不发生。
    /// </summary>
    /// <param name="foreground">前置主窗口的动作（内部自行回到 UI 线程）。</param>
    internal static void AttachForeground(Action foreground)
    {
        ArgumentNullException.ThrowIfNull(foreground);

        bool pending;
        lock (SyncRoot)
        {
            s_foreground = foreground;
            pending = s_foregroundPending;
            s_foregroundPending = false;
        }

        if (pending) foreground();
    }

    /// <summary>
    /// 二次实例：把本次激活转发给主实例。转发在后台线程完成，当前 STA 线程用
    /// <c>CoWaitForMultipleObjects</c> 等待，避免阻塞 COM 消息泵造成转发死锁。
    /// </summary>
    private static void RedirectToPrimary(AppActivationArguments activation, AppInstance primary)
    {
        var completed = new Semaphore(0, 1);
        var redirect = Task.Run(async () =>
        {
            try
            {
                await primary.RedirectActivationToAsync(activation);
            }
            catch (Exception exception)
            {
                NativeStartupLog.Write("activation redirect failed: " + exception.Message);
            }
            finally
            {
                completed.Release();
            }
        });

        var started = Stopwatch.StartNew();
        var handles = new[] { completed.SafeWaitHandle.DangerousGetHandle() };
        var signaled = CoWaitForMultipleObjects(CoWaitDefault, RedirectTimeoutMs, 1, handles, out _) == 0;
        if (!signaled)
        {
            var remaining = Math.Max(0, (int)RedirectTimeoutMs - (int)started.ElapsedMilliseconds);
            signaled = completed.WaitOne(remaining);
        }

        if (signaled) completed.Dispose();
        else
        {
            NativeStartupLog.Write("activation redirect timed out; secondary instance will exit");
            _ = redirect.ContinueWith(_ => completed.Dispose(), TaskScheduler.Default);
        }

        // 转发本身只把激活交给主实例；窗口是否被抢到前台由系统焦点规则决定，这里补一次显式前置。
        try
        {
            using var process = Process.GetProcessById((int)primary.ProcessId);
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (Exception exception)
        {
            NativeStartupLog.Write("primary window foreground failed: " + exception.Message);
        }
    }

    /// <summary>
    /// 主实例收到转发激活：有可打开项就投进统一通道（共享协调器按"一批取首个可打开目标"处理），
    /// 没有可打开项（例如只再次启动 EXE）只请求前置主窗口。
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        try
        {
            var items = WindowsOpenPaths.FromActivation(args);
            if (items.Count == 0)
            {
                RequestForeground();
                return;
            }

            if (s_channel?.Publish(new OpenRequest(items, OpenRequestSource.SystemOpen)) != true)
            {
                NativeStartupLog.Write("activation dropped: open request channel closed");
            }
        }
        catch (Exception exception)
        {
            NativeStartupLog.Write("activation handling failed: " + exception);
        }
    }

    /// <summary>请求前置主窗口；窗口未就绪时挂起，等 <see cref="AttachForeground"/> 补发。</summary>
    private static void RequestForeground()
    {
        Action? foreground;
        lock (SyncRoot)
        {
            foreground = s_foreground;
            if (foreground == null)
            {
                s_foregroundPending = true;
            }
        }

        foreground?.Invoke();
    }

    /// <summary>
    /// 退出前解绑激活订阅并注销主实例键：避免窗口已关、进程将退时还有激活被转发过来
    /// （系统只保证进程终止时回收键，这里提前注销缩小那段窗口）。
    /// </summary>
    internal static void Shutdown()
    {
        lock (SyncRoot)
        {
            s_foreground = null;
            s_foregroundPending = false;
            s_channel = null;
        }

        if (s_primary is not { } primary)
        {
            return;
        }

        s_primary = null;
        try
        {
            primary.Activated -= OnActivated;
            primary.UnregisterKey();
        }
        catch (Exception exception)
        {
            NativeStartupLog.Write("single instance shutdown failed: " + exception.Message);
        }
    }

    /// <summary><c>CoWaitForMultipleObjects</c> 的默认标志（只等待，不派发调用）。</summary>
    private const uint CoWaitDefault = 0;

    private const uint RedirectTimeoutMs = 10000;

    /// <summary>在 STA 上可安全等待句柄完成（内部派发 COM 调用）。</summary>
    /// <param name="flags">等待标志。</param>
    /// <param name="milliseconds">超时毫秒数。</param>
    /// <param name="handleCount">句柄数。</param>
    /// <param name="handles">句柄数组。</param>
    /// <param name="index">完成句柄的索引。</param>
    /// <returns>HRESULT；0 表示等待完成。</returns>
    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(
        uint flags,
        uint milliseconds,
        uint handleCount,
        IntPtr[] handles,
        out uint index);

    /// <summary>把窗口带到前台（系统可能因前台锁拒绝，属尽力而为）。</summary>
    /// <param name="windowHandle">目标窗口句柄。</param>
    /// <returns>是否成功。</returns>
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
