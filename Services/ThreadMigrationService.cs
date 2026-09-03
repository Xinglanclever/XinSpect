using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace XinSpect;

/// <summary>
/// 執行緒遷移與排程落點：執行緒在核心之間彈跳得多兇，以及每一次彈跳丟掉了哪一層快取。
/// </summary>
/// <remarks>
/// <para>
/// 這一頁補的是「明明還有很多核閒著卻很卡」的另一半。一半是中斷全落在同一顆核上——那在
/// 「DPC 延遲與中斷歸屬」已經看得到；另一半是執行緒一直被搬來搬去，每次搬動都把已經拉進
/// L1／L2 的資料留在原地，跨 CCX 或跨 NUMA 時連 L3 都得重新拉。後者在工作管理員裡完全看不見：
/// 使用率一樣、頻率一樣，就是慢。
/// </para>
/// <para>
/// 機制：ETW 的核心上下文切換事件（<c>ContextSwitch</c> 關鍵字），零驅動安裝，但<b>需要
/// 系統管理員權限</b>。每一筆事件記著「哪一條執行緒被切到哪一顆 CPU 上」；把每條執行緒上一次
/// 落在哪顆核記下來，換了就是一次遷移，再用拓樸把它分層。
/// </para>
/// <para>
/// 三個刻意的設計決定：
/// <list type="bullet">
/// <item><b>邊收邊累加，不留原始事件。</b>上下文切換是全系統最高頻的事件之一，忙碌的機器每秒
/// 數十萬筆；留全量只會把記憶體吃光。這一課「DPC 延遲」那一頁已經上過。</item>
/// <item><b>扣掉自己。</b>量測本身就是一條在跑的執行緒，會製造切換也會被遷移。本程式自己的
/// 行程一律不計入，否則就是把觀察者算進觀察結果。</item>
/// <item><b>只支援單一處理器群組（≤64 邏輯處理器）。</b>多群組機器上事件裡的處理器編號與
/// 群組相對編號的對應關係另有一套規則，本程式沒有實機驗證過，所以直接拒絕而不是給一個可能錯的數字。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ThreadMigrationService : ObservableObject, IDisposable
{
    // ── 拓樸（GetLogicalProcessorInformationEx）─────────────────────────────
    private const int RelationProcessorCore = 0, RelationNumaNode = 1, RelationCache = 2;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    // 邊收邊累加：三張計數表，沒有一筆原始事件被留下來
    private readonly Dictionary<(int From, int To), long> _pairs = [];
    private readonly Dictionary<int, long> _byCpu = [];
    private readonly Dictionary<int, (string Name, long Switches, long Migrations, long CrossLlc)> _byProc = [];
    private readonly Dictionary<int, int> _lastCpu = [];      // 執行緒 → 上次落在哪顆 CPU

    private long _switches, _migrations;
    private DateTime _startedAt;
    private int _ownPid;

    private CpuLayout _layout = CpuLayout.Empty;

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

    private string _status =
        "按「開始量測」訂閱核心上下文切換事件（ETW，零驅動安裝，需要系統管理員權限）。全程唯讀。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private string _topologyText = "—";
    /// <summary>本機拓樸摘要：幾顆邏輯處理器、幾片末級快取、幾個 NUMA 節點。</summary>
    public string TopologyText { get => _topologyText; private set => SetProperty(ref _topologyText, value); }

    private string _headline = "—";
    /// <summary>大字：每秒遷移次數。</summary>
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }

    private string _headlineUnit = "尚無結果";
    public string HeadlineUnit { get => _headlineUnit; private set => SetProperty(ref _headlineUnit, value); }

    public ObservableCollection<MigrationHopRow> Hops { get; } = [];
    public ObservableCollection<MigrationProcessRow> Processes { get; } = [];
    public ObservableCollection<CpuSwitchRow> ByCpu { get; } = [];

    public bool HasResult => Hops.Count > 0 || ByCpu.Count > 0;

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
        try { _session?.Dispose(); } catch { /* 已經收掉了 */ }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // 量的是「執行緒被搬了幾次」，而動畫每一幀重繪都會製造上下文切換與遷移
        // ——不停下來就是把觀察行為本身算進觀察結果。
        using var quiet = Motion.Suspend();

        IsRunning = true;
        Phase = "量測中";
        ProgressFraction = 0;
        Hops.Clear(); Processes.Clear(); ByCpu.Clear();
        OnPropertyChanged(nameof(HasResult));
        Headline = "—";
        HeadlineUnit = "量測中";
        lock (_lock)
        {
            _pairs.Clear(); _byCpu.Clear(); _byProc.Clear(); _lastCpu.Clear();
            _switches = 0; _migrations = 0;
        }
        _ownPid = Environment.ProcessId;
        _startedAt = DateTime.Now;

        try
        {
            if (CpuAffinity.IsMultiGroup)
                throw new InvalidOperationException(
                    "本機有多個處理器群組（超過 64 個邏輯處理器）。事件裡的處理器編號在多群組下"
                    + "與群組相對編號的對應另有一套規則，本程式沒有實機驗證過，"
                    + "所以不量——給一個可能錯的分層比不給更糟。");

            _layout = await Task.Run(BuildLayout);
            TopologyText = _layout.IsKnown
                ? $"{_layout.Count} 個邏輯處理器 ・ {_layout.CoreOf.Where(c => c >= 0).Distinct().Count()} 顆實體核心 "
                  + $"・ {_layout.LlcCount} 片末級快取 ・ {_layout.NumaCount} 個 NUMA 節點"
                : "讀不到處理器拓樸，無法分層。";
            if (!_layout.IsKnown)
                throw new InvalidOperationException(
                    "讀不到處理器拓樸（實體核心與快取歸屬）。沒有拓樸，遷移次數就只是一個沒有量綱的"
                    + "數字：在 SMT 兄弟之間跳和跨 NUMA 跳是完全不同的兩件事，所以不量。");

            _session = new TraceEventSession("XinSpect-Migration", TraceEventSessionOptions.Create);
            try { _session.EnableKernelProvider(KernelTraceEventParser.Keywords.ContextSwitch); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "核心上下文切換追蹤無法啟用：" + ex.Message
                    + "（ETW 的核心提供者需要系統管理員權限）");
            }

            var pump = Task.Run(() =>
            {
                var source = _session!.Source;
                source.Kernel.ThreadCSwitch += OnCSwitch;
                source.Process();
            }, CancellationToken.None);

            while (DateTime.Now - _startedAt < TimeSpan.FromSeconds(DurationSec) && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, CancellationToken.None);
                double sec = (DateTime.Now - _startedAt).TotalSeconds;
                ProgressFraction = sec / DurationSec;
                long sw, mg;
                lock (_lock) { sw = _switches; mg = _migrations; }
                Status = $"量測中…（{Math.Min(sec, DurationSec):0} / {DurationSec} 秒）"
                       + $"・已收 {sw:N0} 次切換、{mg:N0} 次遷移";
            }

            Phase = "彙整";
            try { _session.Dispose(); } catch { }
            try { await pump.WaitAsync(TimeSpan.FromSeconds(30)); } catch { /* 收尾逾時不影響已累計的數字 */ }

            Publish((DateTime.Now - _startedAt).TotalSeconds);
            Phase = "完成";
            ProgressFraction = 1;
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            Status = "已停止量測。";
        }
        catch (Exception ex)
        {
            Phase = "無法量測";
            Status = ex.Message;
            HeadlineUnit = "無法量測";
            Diag.Swallow("執行緒遷移量測", ex, "本頁顯示無法量測的原因");
        }
        finally
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// 一筆上下文切換。這個回呼在最忙的機器上每秒會被叫數十萬次，所以裡面只能做
    /// 字典查詢與加法——任何配置或格式化都會讓取樣自己變成負載。
    /// </summary>
    private void OnCSwitch(Microsoft.Diagnostics.Tracing.Parsers.Kernel.CSwitchTraceData e)
    {
        int pid = e.NewProcessID;
        if (pid == _ownPid || pid <= 0) return;      // 扣掉自己：觀察者不算進觀察結果

        int cpu = e.ProcessorNumber;
        int tid = e.NewThreadID;

        lock (_lock)
        {
            _switches++;
            _byCpu[cpu] = _byCpu.GetValueOrDefault(cpu) + 1;

            var p = _byProc.GetValueOrDefault(pid);
            if (p.Name is null) p = (e.NewProcessName ?? $"PID {pid}", 0, 0, 0);
            p.Switches++;

            if (_lastCpu.TryGetValue(tid, out int prev) && prev != cpu)
            {
                _migrations++;
                var key = (prev, cpu);
                _pairs[key] = _pairs.GetValueOrDefault(key) + 1;
                p.Migrations++;
                if (_layout.Classify(prev, cpu) is MigrationHop.CrossLlc or MigrationHop.CrossNuma)
                    p.CrossLlc++;
            }
            _lastCpu[tid] = cpu;
            _byProc[pid] = p;
        }
    }

    private void Publish(double seconds)
    {
        Dictionary<(int From, int To), long> pairs;
        Dictionary<int, long> byCpu;
        Dictionary<int, (string Name, long Switches, long Migrations, long CrossLlc)> byProc;
        long switches, migrations;
        lock (_lock)
        {
            pairs = new(_pairs);
            byCpu = new(_byCpu);
            byProc = new(_byProc);
            switches = _switches;
            migrations = _migrations;
        }

        var hops = MigrationAggregator.ByHop(pairs, _layout, seconds);
        foreach (var h in hops) Hops.Add(h);

        foreach (var r in MigrationAggregator.ByCpu(byCpu)) ByCpu.Add(r);

        var procRows = byProc.Select(kv => new MigrationProcessRow
        {
            Name = kv.Value.Name,
            Pid = kv.Key,
            Switches = kv.Value.Switches,
            Migrations = kv.Value.Migrations,
            CrossLlc = kv.Value.CrossLlc,
        });
        foreach (var r in MigrationAggregator.TopProcesses(procRows)) Processes.Add(r);

        double secs = seconds > 0 ? seconds : 1;
        Headline = migrations > 0 ? $"{migrations / secs:N0}" : "—";
        HeadlineUnit = migrations > 0 ? "次遷移／秒" : "沒有收到遷移";
        Status = MigrationAggregator.Verdict(switches, migrations, secs, hops, _layout);
        OnPropertyChanged(nameof(HasResult));
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _cts?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 拓樸：每顆邏輯處理器屬於哪顆實體核心、哪片末級快取、哪個 NUMA 節點
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 以 <c>GetLogicalProcessorInformationEx</c> 建出 <see cref="CpuLayout"/>。
    /// </summary>
    /// <remarks>
    /// 只處理單一群組（呼叫端已先擋掉多群組），所以遮罩就是全域的邏輯處理器編號位元圖。
    /// 末級快取取「本機出現過的最高快取層級」而不是寫死 L3：有些平台的最後一層是 L2（Atom 系的
    /// 模組共用 L2），寫死 L3 在那些機器上會一片都分不出來。
    /// </remarks>
    private static CpuLayout BuildLayout()
    {
        int n = CpuAffinity.TotalLogicalProcessors;
        if (n <= 0) return CpuLayout.Empty;

        var coreOf = Enumerable.Repeat(-1, n).ToArray();
        var llcOf = Enumerable.Repeat(-1, n).ToArray();
        var numaOf = Enumerable.Repeat(-1, n).ToArray();

        // 實體核心：一筆一顆核，遮罩裡的位元就是它的 SMT 執行緒
        int core = 0;
        foreach (var (mask, _) in Records(RelationProcessorCore, 24))
        {
            foreach (int lp in Bits(mask, n)) coreOf[lp] = core;
            core++;
        }

        // NUMA 節點：節點號在聯集起點，遮罩在 +24
        foreach (var (mask, rec) in Records(RelationNumaNode, 24))
        {
            int node = Marshal.ReadInt32(rec + 8);
            foreach (int lp in Bits(mask, n)) numaOf[lp] = node;
        }

        // 快取：先掃一遍找出最高層級，再只認那一層
        int topLevel = 0;
        foreach (var (_, rec) in Records(RelationCache, 32))
            topLevel = Math.Max(topLevel, Marshal.ReadByte(rec + 8));

        if (topLevel > 0)
        {
            int slice = 0;
            foreach (var (mask, rec) in Records(RelationCache, 32))
            {
                if (Marshal.ReadByte(rec + 8) != topLevel) continue;
                foreach (int lp in Bits(mask, n)) llcOf[lp] = slice;
                slice++;
            }
        }

        return new CpuLayout(coreOf, llcOf, numaOf);
    }

    /// <summary>
    /// 走一趟 <c>GetLogicalProcessorInformationEx</c>，回傳每一筆的（群組遮罩、記錄起點）。
    /// <paramref name="maskOffset"/> 是 GROUP_AFFINITY 在聯集裡的位移（處理器／NUMA 是 24、快取是 32）。
    /// </summary>
    private static IEnumerable<(ulong Mask, nint Rec)> Records(int relationship, int maskOffset)
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(relationship, 0, ref len);
        if (len == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) yield break;

        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationship, buf, ref len)) yield break;
            int off = 0;
            while (off + 8 <= (int)len)
            {
                nint rec = buf + off;
                int size = Marshal.ReadInt32(rec + 4);
                if (size <= 0) break;
                ulong mask = (ulong)Marshal.ReadIntPtr(rec + 8 + maskOffset).ToInt64();
                yield return (mask, rec);
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static IEnumerable<int> Bits(ulong mask, int max)
    {
        for (int i = 0; i < 64 && i < max; i++)
            if ((mask & (1UL << i)) != 0) yield return i;
    }
}
