using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace XinSpect;

/// <summary>單一核心模組的 DPC／ISR 頻次列。</summary>
public sealed class DpcRow
{
    public DpcRow(string module, string kind, long count, double barFraction)
    { Module = module; Kind = kind; Count = count; BarFraction = barFraction; }
    public string Module { get; }
    public string Kind { get; }
    public long Count { get; }
    public string CountText => $"{Count:N0}";
    public double BarFraction { get; }
}

/// <summary>
/// DPC／ISR 頻次排行（純 ETW kernel tracer，零驅動安裝、零注入）：訂閱核心的 DPC／ISR 事件，
/// 依<b>發生頻次</b>排出最忙碌的驅動模組，並畫出每秒總量的時間分佈。
/// DPC 風暴與音訊爆音、輸入停頓、串流掉幀高度相關；哪支驅動最常打斷 CPU，排行榜直接指出。
/// </summary>
/// <remarks>
/// 誠實界線（重要）：經典 ETW 的 DPC／ISR 事件只帶「常式指標＋時間戳」，
/// <b>不包含單次執行時長</b>——LatencyMon 的微秒級時長排行需要它自己的核心驅動。
/// 本頁提供的是頻次與時間分佈（量得到的），時長排行做不了就不做。
/// </remarks>
public sealed class DpcLatencyService : ObservableObject, IDisposable
{
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumDeviceDrivers(IntPtr[]? drivers, uint bufSize, out uint needed);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDeviceDriverBaseNameW(IntPtr imageBase, StringBuilder name, int len);

    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private Thread? _pump;
    private readonly object _lock = new();
    private readonly Dictionary<(string Module, string Kind), long> _counts = new();
    private readonly List<double> _rateSamples = [];
    private DateTime _startedAt;
    private Dictionary<ulong, string> _kernelModules = [];

    private int _durationSec = 15;
    /// <summary>量測時長（秒），5–60。</summary>
    public int DurationSec { get => _durationSec; set => SetProperty(ref _durationSec, Math.Clamp(value, 5, 60)); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未量測";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始量測」訂閱核心 DPC／ISR 事件（ETW，零驅動安裝）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>每秒的 DPC＋ISR 總量時間分佈（供圖表呈現系統被打斷的節奏）。</summary>
    public double[] RatePerSecond { get; private set; } = [];

    public ObservableCollection<DpcRow> Rows { get; } = [];

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        try { _session?.Dispose(); } catch { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IsRunning = true;
        Phase = "量測中";
        ProgressFraction = 0;
        Rows.Clear();
        RatePerSecond = [];
        lock (_lock) { _counts.Clear(); _rateSamples.Clear(); }
        _startedAt = DateTime.Now;

        try
        {
            _kernelModules = await Task.Run(LoadKernelModules);

            _session = new TraceEventSession("XinSpect-Dpc", TraceEventSessionOptions.Create);
            try
            {
                _session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("核心 DPC／ISR 追蹤無法啟用：" + ex.Message);
            }

            var pump = Task.Run(() =>
            {
                var source = _session!.Source;
                source.Kernel.PerfInfoDPC += e => Count("DPC", e.Routine, source);
                source.Kernel.PerfInfoISR += e => Count("ISR", e.Routine, source);
                source.Process();
            }, CancellationToken.None);

            while (DateTime.Now - _startedAt < TimeSpan.FromSeconds(DurationSec) && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, CancellationToken.None);
                ProgressFraction = (DateTime.Now - _startedAt).TotalSeconds / DurationSec;
                Status = $"量測中…（{Math.Min((DateTime.Now - _startedAt).TotalSeconds, DurationSec):0} / {DurationSec} 秒）";
            }

            Phase = "彙整";
            try { _session.Dispose(); } catch { }
            try { await pump.WaitAsync(TimeSpan.FromSeconds(2000)); } catch { }

            var rows = await Task.Run(BuildRows);
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            Phase = "完成";
            ProgressFraction = 1;
            Status = rows.Count > 0
                ? $"完成 ・ 榜首 {rows[0].Module}（{rows[0].Kind}，{rows[0].CountText} 次）。頻次排行反映「誰最常打斷 CPU」；單次時長需核心驅動，零驅動做不了。"
                : "完成 ・ 這段時間沒有量到 DPC／ISR 事件（系統非常安靜）。";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            Status = "已停止量測。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            Status = "量測失敗：" + ex.Message;
        }
        finally
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            IsRunning = false;
        }
    }

    private void Count(string kind, ulong routine, TraceEventDispatcher source)
    {
        lock (_lock)
        {
            var module = ResolveModule(routine);
            var key = (module, kind);
            _counts[key] = _counts.TryGetValue(key, out var c) ? c + 1 : 1;
            double sec = (DateTime.Now - _startedAt).TotalSeconds;
            if (_rateSamples.Count == 0 || sec - _rateSamples.Count > 0 || _rateSamples.Count <= (int)sec)
            {
                // 每秒一格：把目前秒數的格子補滿
                while (_rateSamples.Count < (int)sec + 1) _rateSamples.Add(0);
                _rateSamples[(int)sec]++;
            }
        }
    }

    private string ResolveModule(ulong routine)
    {
        // 模組實際長度拿不到（EnumDeviceDrivers 給基底），以「最高不超過常式的基底」近似歸屬；
        // 相鄰模組的極端情況可能錯置，但排行層級通常仍正確。
        foreach (var kv in _kernelModules.OrderByDescending(kv => kv.Key))
            if (routine >= kv.Key)
                return kv.Value;
        return $"0x{routine:X}";
    }

    /// <summary>列舉核心驅動模組（基底位址→名稱）。管理員權限下位址準確。</summary>
    private static Dictionary<ulong, string> LoadKernelModules()
    {
        var map = new Dictionary<ulong, string>();
        try
        {
            uint needed = 0;
            if (!EnumDeviceDrivers(null, 0, out needed) || needed == 0) return map;
            int count = (int)(needed / (uint)IntPtr.Size);
            var drivers = new IntPtr[count + 1];
            if (!EnumDeviceDrivers(drivers, needed, out _)) return map;
            for (int i = 0; i < count; i++)
            {
                if (drivers[i] == IntPtr.Zero) continue;
                var sb = new StringBuilder(256);
                GetDeviceDriverBaseNameW(drivers[i], sb, 256);
                map[(ulong)drivers[i]] = sb.ToString();
            }
        }
        catch { /* 模組對映失敗時以位址呈現 */ }
        return map;
    }

    private List<DpcRow> BuildRows()
    {
        List<DpcRow> rows;
        lock (_lock)
        {
            rows = _counts.Select(kv => new DpcRow(kv.Key.Module, kv.Key.Kind, kv.Value, 0)).ToList();
            RatePerSecond = _rateSamples.ToArray();
        }
        long max = rows.Count > 0 ? rows.Max(r => r.Count) : 0;
        if (max <= 0) max = 1;
        return rows.OrderByDescending(r => r.Count)
                   .Select(r => new DpcRow(r.Module, r.Kind, r.Count, Math.Clamp(r.Count / (double)max, 0.02, 1)))
                   .Take(20)
                   .ToList();
    }

    public void Dispose() { try { _session?.Dispose(); } catch { } }
}
