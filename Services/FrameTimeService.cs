using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace XinSpect;

/// <summary>單一行程的幀時間統計（供行程清單選擇）。</summary>
public sealed class FrameProcessRow
{
    public FrameProcessRow(int pid, string name, long frames) { Pid = pid; Name = name; Frames = frames; }
    public int Pid { get; }
    public string Name { get; }
    public long Frames { get; }
    public string Label => Frames > 0 ? $"{Name}（{Pid}）・{Frames:N0} 幀" : $"{Name}（{Pid}）";
}

/// <summary>選定行程的即時統計（純函式 <see cref="FrameTimeStats.Compute"/> 的產物）。</summary>
public sealed class FrameStatsRow
{
    public string AvgFps { get; set; } = "—";
    public string Low1 { get; set; } = "—";
    public string Low01 { get; set; } = "—";
    public string MedianMs { get; set; } = "—";
    public string MaxMs { get; set; } = "—";
    public string Frames { get; set; } = "0";
}

/// <summary>
/// 幀時間擷取（PresentMon 同源、純 ETW）：訂閱 <c>Microsoft-Windows-Dxgi</c> 的 Present 事件，
/// 拿到<b>任何程式</b>每一幀的 Present 時間戳——不注入目標程序、不掛勾、不碰驅動，反作弊軟體無從區別。
/// 統計：平均 FPS、1%／0.1% Low（最差 1%／0.1% 幀的平均 FPS）、幀時間中位數與最大值。
/// </summary>
/// <remarks>
/// 誠實界線：量的是 DXGI Present 的時間戳間隔。Present 模式（獨佔／翻轉／合成）與 GPU 忙碌時間
/// 本版未解析——解析不了就不顯示。DWM 合成的額外排程抖動可能疊加在間隔上，屬真實觀測的一部分。
/// </remarks>
public sealed class FrameTimeService : ObservableObject, IDisposable
{
    private const string SessionName = "XinSpect-FrameTime";
    private const int DxgiPresentEventId = 42;   // DXGI Present 事件（與 PresentMon 同源）

    private TraceEventSession? _session;
    private Thread? _pump;
    private readonly object _lock = new();
    private readonly Dictionary<int, (string Name, List<double> Timestamps)> _frames = new();   // pid → 秒
    private DateTime _startedAt;

    private string _selectedPid = "";
    /// <summary>目前統計的行程（空字串＝未選）。</summary>
    public string SelectedPid { get => _selectedPid; set { if (SetProperty(ref _selectedPid, value)) RecomputeSelected(); } }

    public ObservableCollection<FrameProcessRow> Processes { get; } = [];
    public FrameStatsRow Stats { get; } = new();

    private double[] _intervals = [];
    /// <summary>選定行程的幀間隔（ms，時間順序），供曲線圖呈現。</summary>
    public double[] Intervals { get => _intervals; private set { if (SetProperty(ref _intervals, value)) OnPropertyChanged(nameof(HasData)); } }
    public bool HasData => _intervals.Length > 1;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _status = "按「開始監測」訂閱 DXGI Present 事件（ETW，不注入任何程序）。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create);
            // 兩個來源並訂：DXGI#42（行程級 Present）與 Dwm-Core#40（DWM 組合呈現）。
            // 實測（Server 2025）：某些堆疊上 DXGI#42 不發，Present 事件只從 DWM 來——兩個都訂才誠實。
            _session.EnableProvider("Microsoft-Windows-Dxgi", TraceEventLevel.Verbose, ulong.MaxValue);
            _session.EnableProvider("Microsoft-Windows-Dwm-Core", TraceEventLevel.Verbose, ulong.MaxValue);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            Status = "無法啟動 ETW 工作階段：" + ex.Message + "（名稱衝突或權限不足）";
            return;
        }

        lock (_lock) { _frames.Clear(); }
        Processes.Clear();
        Stats.AvgFps = Stats.Low1 = Stats.Low01 = Stats.MedianMs = Stats.MaxMs = "—";
        Stats.Frames = "0";
        Intervals = [];
        SelectedPid = "";
        _startedAt = DateTime.Now;

        _pump = new Thread(Pump) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _pump.Start();
        IsRunning = true;
        Status = "監測中…（跑個遊戲或任何會畫面的程式，統計每秒更新）";
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _session?.Dispose(); } catch { }
        _session = null;
        _pump?.Join(2000);
        _pump = null;
        Status = "已停止。統計保留在畫面上。";
        RecomputeSelected();
    }

    private void Pump()
    {
        try
        {
            var source = _session!.Source;
            source.Dynamic.All += e =>
            {
                // DXGI#42：應用程式層 Present（歸屬呼叫的行程）；Dwm-Core#40：DWM 組合呈現（歸屬系統組合節奏）
                bool isDxgi = e.ProviderName == "Microsoft-Windows-Dxgi" && e.ID == (TraceEventID)DxgiPresentEventId;
                bool isDwm = e.ProviderName == "Microsoft-Windows-Dwm-Core" && e.ID == (TraceEventID)40;
                if (!isDxgi && !isDwm) return;
                int pid = isDxgi ? e.ProcessID : -1;
                lock (_lock)
                {
                    var ts = (e.TimeStamp - _startedAt).TotalSeconds;
                    if (!_frames.TryGetValue(pid, out var entry))
                        _frames[pid] = entry = (isDwm ? "系統組合（DWM）" : e.ProcessName ?? $"PID {e.ProcessID}", []);
                    entry.Timestamps.Add(ts);
                }
            };
            source.Process();
        }
        catch (ObjectDisposedException) { /* Stop() 釋放 session，正常收尾 */ }
        catch (Exception ex)
        {
            BeginOnUi(() => Status = "ETW 事件流中斷：" + ex.Message);
        }
    }

    /// <summary>由 UI 每秒呼叫：更新行程清單與選定行程的統計。</summary>
    public void Tick()
    {
        if (!IsRunning) return;
        bool changed;
        List<(int Pid, string Name, long Frames)> snapshot;
        lock (_lock)
        {
            snapshot = _frames.Select(kv => (kv.Key, kv.Value.Name, (long)kv.Value.Timestamps.Count)).ToList();
            changed = Processes.Count != snapshot.Count ||
                      snapshot.Any(s => !Processes.Any(p => p.Pid == s.Item1 && p.Frames == s.Item3));
        }
        if (changed)
        {
            var top = snapshot.OrderByDescending(s => s.Item3).ToList();
            Processes.Clear();
            foreach (var s in top) Processes.Add(new FrameProcessRow(s.Item1, s.Item2, s.Item3));
            if (SelectedPid.Length == 0 && top.Count > 0)
                SelectedPid = top[0].Item1.ToString();   // 預設選幀數最多的（多半就是遊戲）
        }
        RecomputeSelected();
    }

    private void RecomputeSelected()
    {
        if (!int.TryParse(SelectedPid, out int pid)) return;
        List<double> ts;
        long frames;
        lock (_lock)
        {
            if (!_frames.TryGetValue(pid, out var entry)) { Intervals = []; return; }
            ts = entry.Timestamps;
            frames = ts.Count;
        }
        var stats = FrameTimeStats.Compute(ts);
        Stats.AvgFps = stats.AvgFps is null ? "—" : $"{stats.AvgFps:0.0}";
        Stats.Low1 = stats.Low1 is null ? "—" : $"{stats.Low1:0.0}";
        Stats.Low01 = stats.Low01 is null ? "—" : $"{stats.Low01:0.0}";
        Stats.MedianMs = $"{stats.MedianIntervalMs:0.00}";
        Stats.MaxMs = $"{stats.MaxIntervalMs:0.00}";
        Stats.Frames = $"{frames:N0}";
        Intervals = FrameTimeStats.IntervalsMs(ts);
        OnPropertyChanged(nameof(HasData));
    }

    /// <summary>把狀態更新切回 UI 執行緒（ETW 泵執行緒用）。</summary>
    private static void BeginOnUi(Action a)
    {
        var win = Application.Current?.MainWindow;
        win?.Dispatcher.BeginInvoke(a);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
    }
}

/// <summary>幀時間統計的純函式（單元測試涵蓋）。</summary>
public static class FrameTimeStats
{
    /// <summary>幀間隔（ms，時間順序）。少於兩幀回空陣列。</summary>
    public static double[] IntervalsMs(IReadOnlyList<double> timestampsSec)
    {
        if (timestampsSec.Count < 2) return [];
        var r = new double[timestampsSec.Count - 1];
        for (int i = 1; i < timestampsSec.Count; i++)
            r[i - 1] = (timestampsSec[i] - timestampsSec[i - 1]) * 1000.0;
        return r;
    }

    /// <summary>
    /// 統計：平均 FPS＝(幀數−1)/觀測時長；1%／0.1% Low＝最差 1%／0.1% 幀（依逐幀 FPS 由低到高）的平均 FPS。
    /// 幀數太少（&lt;8）時 Low 值回 null——樣本不足以宣稱百分位。
    /// </summary>
    public static (double? AvgFps, double? Low1, double? Low01, double MedianIntervalMs, double MaxIntervalMs)
        Compute(IReadOnlyList<double> timestampsSec)
    {
        var intervals = IntervalsMs(timestampsSec);
        if (intervals.Length == 0) return (null, null, null, 0, 0);
        var sorted = intervals.OrderBy(x => x).ToArray();
        double median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
        double span = timestampsSec[^1] - timestampsSec[0];
        double? avg = span > 0 ? (intervals.Length) / span : null;
        double? low1 = LowPercent(intervals, 0.01);
        double? low01 = LowPercent(intervals, 0.001);
        return (avg, low1, low01, median, sorted[^1]);
    }

    /// <summary>最差 fraction 比例幀（依逐幀 FPS 升冪）的平均 FPS；樣本太少回 null。</summary>
    public static double? LowPercent(double[] intervalsMs, double fraction)
    {
        int n = (int)Math.Ceiling(intervalsMs.Length * fraction);
        if (n < 1 || intervalsMs.Length < 8) return null;
        var fps = intervalsMs.Select(ms => ms > 0 ? 1000.0 / ms : 0.0).OrderBy(x => x).ToArray();
        double sum = 0;
        for (int i = 0; i < n; i++) sum += fps[i];
        return sum / n;
    }
}
