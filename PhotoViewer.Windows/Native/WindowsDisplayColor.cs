using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DirectN;
using Microsoft.UI.Dispatching;

namespace PhotoViewer.Windows.Native;

internal sealed class WindowsDisplayColor : IDisposable
{
    internal static WindowsDisplayTransform Current { get; private set; } = WindowsDisplayTransform.Srgb;
    internal static event Action? Changed;
    private readonly IntPtr _window;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private string? _key;
    private bool _refreshing;
    private bool _rerun;
    private bool _disposed;
    internal string Status { get; private set; } = "initializing";
    internal bool IsReady => _key != null;

    internal WindowsDisplayColor(IntPtr window, DispatcherQueue queue)
    {
        _window = window;
        _queue = queue;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => Refresh();

    internal async void Refresh()
    {
        if (_disposed) return;
        if (_refreshing) { _rerun = true; return; }
        _refreshing = true;
        try
        {
            var state = await Task.Run(() => Query(_window));
            Status = state.Description;
            if (_disposed || state.Key == _key) return;
            var transform = state.SystemManaged ? WindowsDisplayTransform.ForSystemManaged(state.SdrWhiteScale)
                : state.Profile is { } profile
                    ? await Task.Run(() => WindowsDisplayTransform.FromProfile(profile, state.Description))
                    : WindowsDisplayTransform.Srgb;
            if (_disposed) return;
            _key = state.Key;
            Current = transform;
            NativeStartupLog.Write("display color: " + state.Description);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            _key = null;
            Status = "sRGB fallback: " + ex.Message;
            if (!ReferenceEquals(Current, WindowsDisplayTransform.Srgb))
            {
                Current = WindowsDisplayTransform.Srgb;
                Changed?.Invoke();
            }
            NativeStartupLog.Fail("display color", ex);
        }
        finally
        {
            _refreshing = false;
            if (_rerun && !_disposed)
            {
                _rerun = false;
                _queue.TryEnqueue(Refresh);
            }
        }
    }

    private static State Query(IntPtr window)
    {
        var monitor = MonitorForWindow(window);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new State("no-monitor", "sRGB fallback: monitor unavailable", false, null);
        bool advanced = false;
        string mode = "SDR";
        string? whiteNote = null;
        float sdrWhiteScale = 1;
        bool known = false;
        try
        {
            foreach (var path in DisplayConfig.Query(QDC.QDC_ONLY_ACTIVE_PATHS))
            {
                if (!string.Equals(DisplayConfig.GetSourceName(path).viewGdiDeviceName, info.Device, StringComparison.OrdinalIgnoreCase)) continue;
                var header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = (DISPLAYCONFIG_DEVICE_INFO)15, size = (uint)Marshal.SizeOf<AdvancedInfo2>(),
                    adapterId = path.targetInfo.adapterId, id = path.targetInfo.id,
                };
                var v2 = new AdvancedInfo2 { Header = header };
                if (GetAdvancedInfo2(ref v2) == 0)
                {
                    known = true;
                    // v2 的 ActiveColorMode：0 = SDR、1 = WCG(ACM)、2 = HDR；未识别的非 0 值一律按"系统托管"处理，
                    // 免得同时再叠一层显示器 ICC（双 ICC）。
                    advanced = v2.ActiveColorMode != 0;
                    mode = v2.ActiveColorMode switch
                    {
                        0 => "SDR",
                        1 => "WCG/ACM",
                        2 => "HDR",
                        _ => "system-managed (mode " + v2.ActiveColorMode + ")",
                    };
                    // 只有 HDR 才需要 SDR 白电平缩放；WCG/ACM 的 SDR 白仍是参考白，不乘。
                    if (v2.ActiveColorMode == 2)
                    {
                        (sdrWhiteScale, whiteNote) = ReadSdrWhiteScale(path);
                    }
                    else
                    {
                        whiteNote = v2.ActiveColorMode switch
                        {
                            0 => null,
                            1 => "SDR white not scaled (WCG/ACM)",
                            _ => "unknown ActiveColorMode; SDR white scale 1 (not guessed)",
                        };
                    }
                }
                else
                {
                    var v1 = new AdvancedInfo1 { Header = header };
                    v1.Header.type = (DISPLAYCONFIG_DEVICE_INFO)9;
                    v1.Header.size = (uint)Marshal.SizeOf<AdvancedInfo1>();
                    if (GetAdvancedInfo1(ref v1) == 0)
                    {
                        known = true;
                        // v1 只有"高级颜色是否启用"这一位可用；wideColorEnforced 不可靠，不据此判断。
                        advanced = (v1.Flags & 2) != 0;
                        mode = advanced ? "Advanced Color (v1)" : "SDR";
                        // v1 区分不出 HDR 与 WCG：白电平不猜，按 1 处理并在日志里说明。
                        whiteNote = advanced ? "HDR/WCG indistinguishable on v1; SDR white scale 1 (not guessed)" : null;
                    }
                }
                if (known) break;
            }
        }
        catch (Exception ex)
        {
            mode = "unknown: " + ex.GetType().Name;
        }
        if (!known)
            return new State(info.Device + ":unknown", info.Device + ": sRGB fallback; Advanced Color status unavailable (no ICC baking)", false, null);
        if (advanced)
        {
            var description = info.Device + ": " + mode + "; system-managed scRGB; ICC baking disabled"
                + (string.IsNullOrEmpty(whiteNote) ? string.Empty : "; " + whiteNote);
            var key = info.Device + ":" + mode + ":w" + sdrWhiteScale.ToString("0.###", CultureInfo.InvariantCulture);
            return new State(key, description, true, null, sdrWhiteScale);
        }
        var dc = CreateDC("DISPLAY", info.Device, null, IntPtr.Zero);
        if (dc == IntPtr.Zero)
            return new State(info.Device + ":no-dc", info.Device + ": SDR; sRGB fallback; no display DC", false, null);
        try
        {
            uint length = 32768;
            var name = new StringBuilder((int)length);
            if (!GetICMProfile(dc, ref length, name) || !File.Exists(name.ToString()))
                return new State(info.Device + ":no-icc", info.Device + ": SDR; sRGB fallback; no display ICC", false, null);
            var bytes = File.ReadAllBytes(name.ToString());
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new State(info.Device + ":" + hash, info.Device + ": SDR; application ICC=" + name + "; SHA256=" + hash, false, bytes);
        }
        finally { DeleteDC(dc); }
    }

    /// <summary>
    /// HDR 下的 SDR 白电平倍数（<c>SDRWhiteLevel / 1000</c>）：0 或读取失败时按 1 处理并把原因写进日志说明，不猜。
    /// </summary>
    /// <param name="path">目标显示路径（HDR 模式已确认）。</param>
    /// <returns>白电平倍数与写进描述的说明文本。</returns>
    private static (float Scale, string Note) ReadSdrWhiteScale(DISPLAYCONFIG_PATH_INFO path)
    {
        try
        {
            uint level = DisplayConfig.GetSdrWhiteLevel(path).SDRWhiteLevel;
            if (level == 0) return (1, "SDR white level reports 0; SDR white scale 1");
            float scale = level / 1000f;
            if (!float.IsFinite(scale) || scale <= 0) return (1, "SDR white level invalid; SDR white scale 1");
            return (scale, "SDR white scale " + scale.ToString("0.###", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            return (1, "SDR white level unavailable (" + ex.GetType().Name + "); SDR white scale 1");
        }
    }

    /// <summary>
    /// 按**窗口中心点**选显示器，与 DXGI 按窗口中心挑输出保持一致（跨屏/部分遮挡时窗口句柄的 monitor 可能不同）；
    /// 拿不到窗口矩形时退回按句柄。进程 PerMonitorV2，矩形即物理像素。
    /// </summary>
    /// <param name="window">窗口句柄。</param>
    /// <returns>显示器句柄；失败返回 <see cref="IntPtr.Zero"/>。</returns>
    private static IntPtr MonitorForWindow(IntPtr window)
    {
        if (window != IntPtr.Zero && GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
        {
            var center = new MonitorPoint
            {
                X = rect.Left + (rect.Right - rect.Left) / 2,
                Y = rect.Top + (rect.Bottom - rect.Top) / 2,
            };
            var monitor = MonitorFromPoint(center, 2);
            if (monitor != IntPtr.Zero) return monitor;
        }
        return MonitorFromWindow(window, 2);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }

    private sealed record State(string Key, string Description, bool SystemManaged, byte[]? Profile, float SdrWhiteScale = 1);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedInfo1 { public DISPLAYCONFIG_DEVICE_INFO_HEADER Header; public uint Flags, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedInfo2 { public DISPLAYCONFIG_DEVICE_INFO_HEADER Header; public uint Flags, Encoding, Bits, ActiveColorMode; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(MonitorPoint point, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedInfo1(ref AdvancedInfo1 info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetAdvancedInfo2(ref AdvancedInfo2 info);
    [DllImport("gdi32.dll", EntryPoint = "CreateDCW", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initialization);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetICMProfileW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetICMProfile(IntPtr dc, ref uint size, StringBuilder profile);
}
