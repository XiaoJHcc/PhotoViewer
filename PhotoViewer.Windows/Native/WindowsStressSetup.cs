using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Memory;
using Windows.Graphics;

namespace PhotoViewer.Windows.Native;

internal sealed partial class WindowsMainWindow
{
    private bool _stressFull;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _stressHeartbeat;
    private long _stressHeartbeatAt;
    private double _stressHeartbeatMaxMs;

    internal async Task PrepareStressAsync(string[] args)
    {
        _stressFull = args.Contains("--pv-stress-full");
        await WaitForSmokeAsync(() => _sized && _photo.PhysicalViewport.Width > 1, "压测初始布局完成");
        var window = args.FirstOrDefault(a => a.StartsWith("--pv-stress-window=", StringComparison.Ordinal));
        if (window != null)
        {
            var parts = window["--pv-stress-window=".Length..].Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height)
                || width < 640 || height < 480 || width > 7680 || height > 4320)
                throw new ArgumentException("压测窗口尺寸无效");
            AppWindow.Move(new PointInt32(40, 40));
            AppWindow.Resize(new SizeInt32(width, height));
        }
        await Task.Delay(250);
        _root.UpdateLayout();
        await WaitForSmokeAsync(() => _photo.PhysicalViewport.Width > 1 && _photo.PhysicalViewport.Height > 1,
            "压测物理视口");
        if (args.Contains("--pv-stress-full"))
        {
            BitmapLoader.PrefetchFullWanted = true;
            _session.UpdateViewport((int)_photo.PhysicalViewport.Width, (int)_photo.PhysicalViewport.Height, true);
        }
        _stressHeartbeatAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _stressHeartbeat = DispatcherQueue.CreateTimer();
        _stressHeartbeat.Interval = TimeSpan.FromMilliseconds(16);
        _stressHeartbeat.Tick += (_, _) =>
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _stressHeartbeatMaxMs = Math.Max(_stressHeartbeatMaxMs,
                System.Diagnostics.Stopwatch.GetElapsedTime(_stressHeartbeatAt, now).TotalMilliseconds);
            _stressHeartbeatAt = now;
        };
        _stressHeartbeat.Start();
        NativeStartupLog.Write($"stress: budgetMB={MemoryGovernor.BudgetBytes >> 20} "
            + $"systemMB={MemoryGovernor.GetSystemMemoryInfo().TotalBytes >> 20} "
            + $"window={AppWindow.Size.Width}x{AppWindow.Size.Height} completion=present-return");
    }
}
