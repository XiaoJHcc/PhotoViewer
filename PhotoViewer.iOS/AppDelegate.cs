using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using PhotoViewer.Core;
using PhotoViewer.iOS.Core;
using ReactiveUI;
using System;
// 新增：libdispatch 回调与 AOT 固定
using ObjCRuntime;
using System.Runtime.InteropServices;

namespace PhotoViewer.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    // ========== 新增：GCD 内存压力监控 ==========
    // libdispatch 导入
    [DllImport("libSystem.dylib")]
    private static extern IntPtr dispatch_source_create(IntPtr type, IntPtr handle, nuint mask, IntPtr queue);
    [DllImport("libSystem.dylib")]
    private static extern void dispatch_resume(IntPtr obj);
    [DllImport("libSystem.dylib")]
    private static extern void dispatch_set_context(IntPtr obj, IntPtr context);
    [DllImport("libSystem.dylib")]
    private static extern void dispatch_source_set_event_handler_f(IntPtr source, DispatchFunction handler);
    [DllImport("libSystem.dylib")]
    private static extern IntPtr dispatch_get_global_queue(int identifier, nuint flags);
    // dispatch_source_type_memorypressure 常量
    [DllImport("libSystem.dylib")]
    private static extern IntPtr dispatch_source_type_memorypressure();

    // 事件掩码
    private const nuint DISPATCH_MEMORYPRESSURE_WARN = 0x02;
    private const nuint DISPATCH_MEMORYPRESSURE_CRITICAL = 0x04;

    // 回调签名
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DispatchFunction(IntPtr context);

    // 保持引用，防止被 GC 回收
    private IntPtr _memWarnSource;
    private IntPtr _memCriticalSource;
    private GCHandle _warnCtx;
    private GCHandle _criticalCtx;
    private bool _usingDispatchMP;

    // 静态回调（AOT 友好）
    [MonoPInvokeCallback(typeof(DispatchFunction))]
    private static void OnMemoryPressureWarn(IntPtr context)
    {
        var handle = (GCHandle)context;
        if (handle.Target is AppDelegate self)
            self.OnMemoryPressure(level: 1);
    }

    [MonoPInvokeCallback(typeof(DispatchFunction))]
    private static void OnMemoryPressureCritical(IntPtr context)
    {
        var handle = (GCHandle)context;
        if (handle.Target is AppDelegate self)
            self.OnMemoryPressure(level: 2);
    }

    // 实例级处理：1 级不动作；2 级执行清理+上报
    private void OnMemoryPressure(int level)
    {
        if (level < 2) return; // 1 级留空
        ExecuteLevel2Cleanup();
    }

    // 统一的 2 级处理逻辑（原有代码收敛）
    private static void ExecuteLevel2Cleanup()
    {
        // 1) 记录清理前缓存大小
        var before = BitmapLoader.CurrentCacheSize;

        // 2) 通知 BitmapLoader 进行快速精简（目标 50% 上限）
        var (_, after) = BitmapLoader.TrimOnMemoryWarning(0.5);

        // 3) 记录到 MemoryBudget（iOS 侧存档 MB 值）
        iOSMemoryBudget.RecordMemoryWarningCacheMB(before);

        // 4) 广播给 UI：更新 MemoryBudgetInfo
        var evt = new BitmapLoader.MemoryWarningEvent(
            beforeMB: before / (1024 * 1024),
            afterMB:  after / (1024 * 1024),
            time:     DateTimeOffset.Now
        );
        MessageBus.Current.SendMessage(evt);
    }

    // 创建并启动 memory pressure 监听；失败则回退到 UIApplication 告警
    private void SetupMemoryPressureMonitoring()
    {
        try
        {
            var type = dispatch_source_type_memorypressure();
            var queue = dispatch_get_global_queue(0, 0);

            // WARN 源（1 级）
            _memWarnSource = dispatch_source_create(type, IntPtr.Zero, DISPATCH_MEMORYPRESSURE_WARN, queue);
            if (_memWarnSource != IntPtr.Zero)
            {
                _warnCtx = GCHandle.Alloc(this);
                dispatch_set_context(_memWarnSource, (IntPtr)_warnCtx);
                dispatch_source_set_event_handler_f(_memWarnSource, OnMemoryPressureWarn);
                dispatch_resume(_memWarnSource);
            }

            // CRITICAL 源（2 级）
            _memCriticalSource = dispatch_source_create(type, IntPtr.Zero, DISPATCH_MEMORYPRESSURE_CRITICAL, queue);
            if (_memCriticalSource != IntPtr.Zero)
            {
                _criticalCtx = GCHandle.Alloc(this);
                dispatch_set_context(_memCriticalSource, (IntPtr)_criticalCtx);
                dispatch_source_set_event_handler_f(_memCriticalSource, OnMemoryPressureCritical);
                dispatch_resume(_memCriticalSource);
            }

            _usingDispatchMP = _memWarnSource != IntPtr.Zero || _memCriticalSource != IntPtr.Zero;
        }
        catch
        {
            _usingDispatchMP = false;
        }

        // 回退：若无法使用分级内存压力，则监听 UIApplication 告警（视作 2 级）
        if (!_usingDispatchMP)
        {
            UIApplication.Notifications.ObserveDidReceiveMemoryWarning((_, __) =>
            {
                ExecuteLevel2Cleanup();
            });
        }
    }
    // ========== 新增结束 ==========

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI()
            .AfterSetup(_ => 
            {
                HeifLoader.Initialize(new iOSHeifDecoder());
                MemoryBudget.Initialize(new iOSMemoryBudget());

                // 替换：使用分级内存压力监听；仅在 2 级执行清理与上报
                SetupMemoryPressureMonitoring();
            });
    }
}