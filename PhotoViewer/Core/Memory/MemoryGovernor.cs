using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using PhotoViewer.Core.Image;
using PhotoViewer.Core.Settings;

namespace PhotoViewer.Core.Memory;

/// <summary>
/// 统一内存管家(静态门面 + 平台注入预算源)。
/// 职责:client 登记、压力状态机(周期探测 + OS 事件双源 + 迟滞)、解码准入口、压力事件快照日志。
///
/// 准入口优先级语义(2026-10-12 审查裁定):
/// - P0(当前图)豁免准入,永远放行,但仍记账保持可见;
/// - P1/P2/P3 预算不足时直接拒绝(不排队,防后台解码饿死/堵死 P0);
/// - Warning 起拒绝 P2/P3,Critical 拒绝一切非 P0;批量索引走 P3,被拒绝即自然暂停。
/// </summary>
public static class MemoryGovernor
{
    // ---- 进入阈值(可用内存占预算百分比);退出阈值 = 进入 + 迟滞带宽 ----
    /// <summary>Caution 进入阈值(%)。高级调节项,后续进设置页。</summary>
    public static double CautionEnterPercent { get; set; } = 40;
    /// <summary>Warning 进入阈值(%)。</summary>
    public static double WarningEnterPercent { get; set; } = 25;
    /// <summary>Critical 进入阈值(%)。</summary>
    public static double CriticalEnterPercent { get; set; } = 15;
    private const double HysteresisPercent = 5;

    /// <summary>Normal 恢复后的渐进观察期(期间预取窗口保持收窄,打破告警→回填循环)。</summary>
    public static TimeSpan RecoveryHold { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>周期探测间隔(默认 2s)。</summary>
    public static TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(2);

    private static readonly object _gate = new();
    private static readonly List<IMemoryClient> _clients = new();
    private static IMemoryBudgetProvider? _provider;
    private static Timer? _timer;
    private static PressureLevel _level = PressureLevel.Normal;
    private static DateTimeOffset _returnedToNormalAt = DateTimeOffset.MinValue;
    private static long _reservedBytes;
    private static string? _logPath;
    private static long _lastRefusalLogTicks;

    /// <summary>当前压力级别。</summary>
    public static PressureLevel CurrentLevel
    {
        get { lock (_gate) return _level; }
    }

    /// <summary>压力级别变化事件。回调在线程池线程,订阅方自行封送 UI。</summary>
    public static event Action<PressureLevel>? LevelChanged;

    /// <summary>Normal 恢复后的渐进观察期是否仍在持续。</summary>
    public static bool InRecoveryHold
    {
        get
        {
            lock (_gate)
                return _level == PressureLevel.Normal
                    && _returnedToNormalAt != DateTimeOffset.MinValue
                    && DateTimeOffset.UtcNow - _returnedToNormalAt < RecoveryHold;
        }
    }

    /// <summary>在途解码预留总额(字节)。</summary>
    public static long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    /// <summary>当前预算(字节);未注入预算源时为 0。</summary>
    public static long BudgetBytes
    {
        get
        {
            IMemoryBudgetProvider? provider;
            lock (_gate) provider = _provider;
            try { return provider?.GetBudgetBytes() ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>当前进程驻留(字节,预算源同口径);口径不适用或未注入时为 0(未知)。</summary>
    public static long FootprintBytes
    {
        get
        {
            IMemoryBudgetProvider? provider;
            lock (_gate) provider = _provider;
            try { return provider?.GetFootprintBytes() ?? 0; }
            catch { return 0; }
        }
    }

    /// <summary>每轮压力探测结束(含 Initialize 的首轮)后触发,供缓存按最新 footprint 重算容量。回调在线程池线程。</summary>
    public static event Action? Probed;

    /// <summary>
    /// 注入平台预算源并启动周期探测(2s,带水位迟滞防抖)。重复调用以最后一次为准。
    /// </summary>
    public static void Initialize(IMemoryBudgetProvider provider)
    {
        lock (_gate)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            var settingsPath = SettingsPathHelper.GetDefaultPath("PhotoViewer");
            _logPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "memory-events.jsonl");
            _timer?.Dispose();
            _timer = new Timer(_ => SafeEvaluate(), null, ProbeInterval, ProbeInterval);
        }
        EvaluateNow();
        // 启动即落一条上限快照:真机据此确认预算是否真按实测(而非回退值)生效。
        WriteSnapshot("init", "startup");
    }

    /// <summary>登记内存客户端。重复登记同一实例无效。</summary>
    public static void Register(IMemoryClient client)
    {
        lock (_gate)
        {
            if (!_clients.Contains(client)) _clients.Add(client);
        }
    }

    /// <summary>注销内存客户端。</summary>
    public static void Unregister(IMemoryClient client)
    {
        lock (_gate) _clients.Remove(client);
    }

    /// <summary>
    /// 解码准入申请。获批返回 <see cref="AdmissionPermit"/>(解码完成后应 <see cref="AdmissionPermit.SetActual"/>
    /// 按实际字节复核,using 结束归还);拒绝返回 false(调用方跳过或降级,不得排队等待)。
    /// </summary>
    /// <param name="bytes">预估解码字节数。</param>
    /// <param name="priority">解码优先级。</param>
    /// <param name="permit">获批时的许可凭证。</param>
    public static bool TryAcquire(long bytes, DecodePriority priority, out AdmissionPermit? permit)
    {
        permit = null;
        if (bytes <= 0) bytes = 1;

        // 未初始化(工具/测试进程):全部放行,不记账
        IMemoryBudgetProvider? provider;
        lock (_gate) provider = _provider;
        if (provider == null)
        {
            permit = new AdmissionPermit(0);
            return true;
        }

        if (priority == DecodePriority.P0_Current)
        {
            // P0 豁免准入:永远放行,但记账(在途内存统一可见)
            Interlocked.Add(ref _reservedBytes, bytes);
            permit = new AdmissionPermit(bytes);
            return true;
        }

        var level = CurrentLevel;
        long budget = provider.GetBudgetBytes();
        long available = provider.GetAvailableBytes();

        // 级别拒绝:Warning 起 P2/P3 一律拒绝;Critical 拒绝一切非 P0
        if (level == PressureLevel.Critical ||
            (level >= PressureLevel.Warning && priority >= DecodePriority.P2_Prefetch))
        {
            RefusalLog(bytes, priority, level, available, budget, "level-reject");
            return false;
        }

        // 级别定预留上限:Caution 25%,其余(P1 在 Warning)10%,Normal 50%
        double capRatio = level switch
        {
            PressureLevel.Normal => 0.50,
            PressureLevel.Caution => 0.25,
            _ => 0.10,
        };
        long cap = (long)(budget * capRatio);
        long reserved = Interlocked.Read(ref _reservedBytes);

        // 双重闸门:总额不超预留上限;单次不超当前可用的一半(避免单张解码吃掉大半余量)
        bool granted = reserved + bytes <= cap
            && (available == long.MaxValue || bytes <= available / 2);
        if (!granted)
        {
            RefusalLog(bytes, priority, level, available, budget, "cap-reject");
            return false;
        }

        Interlocked.Add(ref _reservedBytes, bytes);
        permit = new AdmissionPermit(bytes);
        return true;
    }

    /// <summary>
    /// OS 内存事件直报(iOS DidReceiveMemoryWarning / Android OnTrimMemory):只升不降,立即生效并落快照。
    /// </summary>
    public static void ReportOsEvent(PressureLevel level)
    {
        if (level <= CurrentLevel) return;
        SetLevel(level, "os-event");
    }

    /// <summary>立即执行一次压力评估(周期探测与 OS 事件共用此入口)。</summary>
    public static void EvaluateNow()
    {
        IMemoryBudgetProvider? provider;
        lock (_gate) provider = _provider;
        if (provider == null) return;

        long budget = provider.GetBudgetBytes();
        long available = provider.GetAvailableBytes();
        if (budget > 0 && available != long.MaxValue)
        {
            double availPercent = 100.0 * available / budget;
            var current = CurrentLevel;
            var target = EvaluateTarget(availPercent, current);
            if (target != current)
                SetLevel(target, "probe");
        }

        // 每轮探测都通知容量推导(预算/驻留会随缓存与系统水位漂移,2s 粒度足够)
        try { Probed?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[MemoryGovernor] Probed subscriber failed: {ex.Message}"); }
    }

    /// <summary>
    /// 压力状态机:恶化按进入阈值立即抬级;好转逐级退(退出阈值 = 进入 + 5% 迟滞),一次评估只退一步。
    /// </summary>
    private static PressureLevel EvaluateTarget(double availPercent, PressureLevel current)
    {
        if (availPercent < CriticalEnterPercent) return PressureLevel.Critical;
        if (availPercent < WarningEnterPercent) return PressureLevel.Warning > current ? PressureLevel.Warning : StepDown(availPercent, current);
        if (availPercent < CautionEnterPercent) return PressureLevel.Caution > current ? PressureLevel.Caution : StepDown(availPercent, current);
        return StepDown(availPercent, current);
    }

    private static PressureLevel StepDown(double availPercent, PressureLevel current) => current switch
    {
        PressureLevel.Critical => availPercent >= CriticalEnterPercent + HysteresisPercent ? PressureLevel.Warning : current,
        PressureLevel.Warning => availPercent >= WarningEnterPercent + HysteresisPercent ? PressureLevel.Caution : current,
        PressureLevel.Caution => availPercent >= CautionEnterPercent + HysteresisPercent ? PressureLevel.Normal : current,
        _ => current,
    };

    private static void SetLevel(PressureLevel next, string reason)
    {
        PressureLevel prev;
        lock (_gate)
        {
            prev = _level;
            if (prev == next) return;
            _level = next;
            if (next == PressureLevel.Normal)
                _returnedToNormalAt = DateTimeOffset.UtcNow;
        }
        WriteSnapshot($"level:{prev}->{next}", reason);

        // 升级级联:对全部登记 client 执行压力修剪(契约:Trim 不阻塞;降级不触发)
        if (next > prev && next > PressureLevel.Normal)
        {
            IMemoryClient[] clients;
            lock (_gate) clients = _clients.ToArray();
            foreach (var client in clients)
            {
                try { client.Trim(next); }
                catch (Exception ex) { Console.WriteLine($"[MemoryGovernor] client {client.Name} Trim({next}) failed: {ex.Message}"); }
            }
        }

        try { LevelChanged?.Invoke(next); }
        catch (Exception ex) { Console.WriteLine($"[MemoryGovernor] LevelChanged subscriber failed: {ex.Message}"); }
    }

    private static void SafeEvaluate()
    {
        try { EvaluateNow(); }
        catch (Exception ex) { Console.WriteLine($"[MemoryGovernor] probe failed: {ex.Message}"); }
    }

    // ---- 账本 ----

    internal static void AdjustReserved(long delta) => Interlocked.Add(ref _reservedBytes, delta);

    // ---- 快照日志 ----

    private static void RefusalLog(long bytes, DecodePriority priority, PressureLevel level, long available, long budget, string reason)
    {
        // 拒批节流:每秒最多一条(预取被拒是常态,不刷屏)
        long now = DateTimeOffset.UtcNow.Ticks;
        long last = Interlocked.Read(ref _lastRefusalLogTicks);
        if (now - last < TimeSpan.TicksPerSecond) return;
        if (Interlocked.CompareExchange(ref _lastRefusalLogTicks, now, last) != last) return;
        WriteSnapshot($"refuse:{priority}:{reason}", $"level={level}, avail={available / 1048576}MB, budget={budget / 1048576}MB");
    }

    /// <summary>
    /// 写一条压力事件快照(JSONL:时间/事件/级别/可用/预算/在途预留/各 client 占用/近期解码统计)。
    /// 供闪退现场回放;写盘失败静默(日志不是关键路径)。
    /// </summary>
    private static void WriteSnapshot(string evt, string reason)
    {
        try
        {
            string? path;
            IMemoryClient[] clients;
            lock (_gate)
            {
                path = _logPath;
                clients = _clients.ToArray();
            }
            if (path == null) return;

            var (missCount, missP50, missMean) = PerfTrace.RecentMissStats();
            var sb = new StringBuilder(512);
            sb.Append("{\"ts\":\"").Append(DateTimeOffset.UtcNow.ToString("o")).Append('"');
            sb.Append(",\"evt\":\"").Append(evt).Append('"');
            sb.Append(",\"reason\":\"").Append(reason.Replace("\"", "'")).Append('"');
            sb.Append(",\"level\":\"").Append(CurrentLevel).Append('"');
            if (_provider != null)
            {
                sb.Append(",\"availMB\":").Append(_provider.GetAvailableBytes() / 1048576);
                sb.Append(",\"budgetMB\":").Append(_provider.GetBudgetBytes() / 1048576);
                sb.Append(",\"footprintMB\":").Append(_provider.GetFootprintBytes() / 1048576);
            }
            sb.Append(",\"reservedMB\":").Append(ReservedBytes / 1048576);
            sb.Append(",\"decodeMiss\":{ \"n\":").Append(missCount)
              .Append(",\"p50ms\":").Append((long)missP50)
              .Append(",\"meanms\":").Append((long)missMean).Append('}');
            sb.Append(",\"clients\":[");
            for (int i = 0; i < clients.Length; i++)
            {
                if (i > 0) sb.Append(',');
                long fp = 0;
                try { fp = clients[i].FootprintBytes; } catch { /* 单个 client 读数失败不影响快照 */ }
                sb.Append("{\"").Append(clients[i].Name).Append("\":").Append(fp / 1048576).Append('}');
            }
            sb.Append("]}");
            File.AppendAllText(path, sb.ToString() + Environment.NewLine);
        }
        catch
        {
            // 日志不是关键路径,静默
        }
    }

    /// <summary>
    /// 解码准入凭证。获批即计入在途账本;解码完成后按实际字节 <see cref="SetActual"/> 复核,
    /// Dispose 归还。幂等。
    /// </summary>
    public sealed class AdmissionPermit : IDisposable
    {
        private long _reserved;
        private int _disposed;

        internal AdmissionPermit(long reserved) => _reserved = reserved;

        /// <summary>解码完成后按实际产物字节数校正账本(多退少补)。</summary>
        public void SetActual(long actualBytes)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            long delta = actualBytes - Interlocked.Exchange(ref _reserved, actualBytes);
            if (delta != 0) AdjustReserved(delta);
        }

        /// <summary>归还在途预留。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            long r = Interlocked.Exchange(ref _reserved, 0);
            if (r != 0) AdjustReserved(-r);
        }
    }
}
