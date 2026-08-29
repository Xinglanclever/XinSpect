using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>單一邏輯處理器的 RDT 讀值列。</summary>
public sealed class RdtCoreRow
{
    public RdtCoreRow(int lp, double occupancyMb, double totalBw, double localBw)
    { Lp = lp; OccupancyMb = occupancyMb; TotalBw = totalBw; LocalBw = localBw; }
    public int Lp { get; }
    /// <summary>L3 占用（MB，即時值）；-1＝讀不到。</summary>
    public double OccupancyMb { get; }
    /// <summary>總記憶體頻寬（MB/s，區間差分）；-1＝尚無樣本。</summary>
    public double TotalBw { get; }
    /// <summary>本機記憶體頻寬（MB/s，區間差分）。</summary>
    public double LocalBw { get; }
    public string OccupancyText => OccupancyMb < 0 ? "—" : $"{OccupancyMb:0.0} MB";
    public string TotalBwText => TotalBw < 0 ? "—" : $"{TotalBw:N0} MB/s";
    public string LocalBwText => LocalBw < 0 ? "—" : $"{LocalBw:N0} MB/s";
    public double BarFraction { get; set; }
}

/// <summary>
/// Intel RDT 監測（CMT＋MBM）：Skylake-X 支援。逐核心指派 RMID（PQR_ASSOC 0xC8F，RMID 1 全核一致），
/// 每秒在釘選的執行緒上經 QM_EVTSEL（0xC8D）選事件、QM_CTR（0xC8E）讀計數：
/// 事件 1＝L3 占用（原始值×CPUID 0x0F.1 EBX 的 upscaling＝位元組）、事件 2＝總記憶體頻寬、事件 3＝本機頻寬（差分）。
/// </summary>
/// <remarks>
/// 誠實界線：RMID 全核統一指派，得到「全系統＋逐核心」視角；逐<b>行程</b>歸屬需要核心層 RMID 排程，
/// 使用者態做不到。MSR 寫入經 WinRing0 橋接（使用者已同意；風險聲明見 WinRing0Bridge）。
/// MBM 計數會溢位回繞——差分為負時視為回繞或重置，該秒不計。
/// </remarks>
public sealed class RdtService : ObservableObject, IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, ulong affinityMask);

    private const uint MsrPqrAssoc = 0xC8F;
    private const uint MsrQmEvtsel = 0xC8D;
    private const uint MsrQmCtr = 0xC8E;
    private const uint EventL3Occupancy = 1;
    private const uint EventTotalMbm = 2;
    private const uint EventLocalMbm = 3;

    private WinRing0Bridge? _bridge;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private double _upscaling = 1;
    private int _maxRmid;
    private int[] _lps = [];
    private ulong _processMask;

    // 背景執行緒寫入、UI（Tick）讀取的快照
    private readonly object _lock = new();
    private (int Lp, double Occ, double Total, double Local)[] _snapshot = [];
    private DateTime _snapshotAt = DateTime.MinValue;

    public bool RdtSupported { get; private set; }
    private string _supportText = "尚未檢測";
    public string SupportText { get => _supportText; private set => SetProperty(ref _supportText, value); }

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _status = "尚未啟動。";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ObservableCollection<RdtCoreRow> Rows { get; } = [];

    private string _totalBwText = "—";
    public string TotalBwText { get => _totalBwText; private set => SetProperty(ref _totalBwText, value); }

    public void Start()
    {
        if (IsRunning) return;
        _bridge ??= WinRing0Bridge.Create();
        if (_bridge is null || !_bridge.Available)
        {
            Status = "WinRing0 橋接不可用：" + _bridge?.Error;
            return;
        }
        DetectSupport();
        if (!RdtSupported) { Status = SupportText; return; }

        _cts = new CancellationTokenSource();
        _worker = new Thread(() => WorkerLoop(_cts.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _worker.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        _worker?.Join(3000);
        _worker = null;
        UnassignRmid();
        IsRunning = false;
        Status = "已停止。RMID 指派已歸零。";
    }

    /// <summary>由 UI 每秒呼叫：把背景快照發佈成繫結列（須在 UI 執行緒）。</summary>
    public void Tick()
    {
        if (!IsRunning) return;
        (int, double, double, double)[] snap;
        lock (_lock) snap = _snapshot;
        Rows.Clear();
        double max = snap.Length > 0 ? snap.Max(s => s.Item2) : 0;
        if (max <= 0) max = 1;
        double total = 0;
        foreach (var (lp, occ, totalBw, localBw) in snap)
        {
            if (totalBw > 0) total += totalBw;
            Rows.Add(new RdtCoreRow(lp, occ, totalBw, localBw)
            {
                BarFraction = Math.Clamp(occ / max, 0.02, 1),
            });
        }
        TotalBwText = $"{total:N0} MB/s";
    }

    /// <summary>CPUID 0x0F 支援偵測與參數（upscaling／最大 RMID／MBM 能力）。</summary>
    public void DetectSupport()
    {
        if (!X86Base.IsSupported) { RdtSupported = false; SupportText = "非 x86 平台"; return; }
        var r0 = X86Base.CpuId(0x0F, 0);
        bool cmt = ((uint)r0.Ebx & 0x1) != 0;
        if (!cmt)
        {
            RdtSupported = false;
            SupportText = "此處理器不支援 RDT 快取監測（CPUID 0x0F.0 EBX bit0 = 0）";
            return;
        }
        var r1 = X86Base.CpuId(0x0F, 1);
        var upscaling = (uint)r1.Ebx;
        var maxRmid = (int)((uint)r1.Ecx);
        RdtSupported = true;
        SupportText = $"支援：upscaling {upscaling:0}、最大 RMID {maxRmid}、{_lps.Length} 邏輯處理器";
    }

    private void WorkerLoop(CancellationToken ct)
    {
        _processMask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64();
        _lps = CoreLatencyService.LogicalProcessorsFromMask(_processMask).ToArray();
        DetectSupport();
        if (!RdtSupported || _lps.Length == 0) { _status = SupportText; return; }

        // 指派 RMID 1 給每個核心（PQR_ASSOC 的 RMID 在位 51:32）
        foreach (var lp in _lps)
            _bridge!.WriteMsrPair(MsrPqrAssoc, 0, 1);   // RMID 1（位 51:32 → EAX=0、EDX=1）

        var lastTotal = new ulong[_lps.Length];
        var lastLocal = new ulong[_lps.Length];
        var hasPrev = new bool[_lps.Length];
        var sw = Stopwatch.StartNew();
        double lastSec = 0;

        while (!ct.IsCancellationRequested)
        {
            var snap = new (int, double, double, double)[_lps.Length];
            double elapsed = sw.Elapsed.TotalSeconds;
            double dt = Math.Max(elapsed - lastSec, 0.05);
            lastSec = elapsed;

            for (int i = 0; i < _lps.Length; i++)
            {
                ulong mask = 1UL << _lps[i];
                if (SetThreadAffinityMask(GetCurrentThread(), mask) == IntPtr.Zero)
                { snap[i] = (_lps[i], -1, -1, -1); continue; }

                double occ = -1;
                ulong t = 0, l = 0;
                bool tOk = false, lOk = false;
                try
                {
                    _bridge!.WriteMsrPair(MsrQmEvtsel, EventL3Occupancy, 1);
                    if (_bridge.ReadMsrPair64(MsrQmCtr) is { } occRaw)
                        occ = occRaw * _upscaling / (1024.0 * 1024.0);

                    _bridge.WriteMsrPair(MsrQmEvtsel, EventTotalMbm, 1);
                    tOk = _bridge.ReadMsrPair64(MsrQmCtr) is { } tv && (t = tv) >= 0;

                    _bridge.WriteMsrPair(MsrQmEvtsel, EventLocalMbm, 1);
                    lOk = _bridge.ReadMsrPair64(MsrQmCtr) is { } lv && (l = lv) >= 0;
                }
                catch { }
                finally
                {
                    SetThreadAffinityMask(GetCurrentThread(), _processMask);
                }

                double dBw = -1, lBw = -1;
                if (tOk && hasPrev[i] && t >= lastTotal[i])
                    dBw = (t - lastTotal[i]) * 8.0 / dt / 1e6;
                if (lOk && hasPrev[i] && l >= lastLocal[i])
                    lBw = (l - lastLocal[i]) * 8.0 / dt / 1e6;
                if (tOk) lastTotal[i] = t;
                if (lOk) lastLocal[i] = l;
                hasPrev[i] = true;

                snap[i] = (_lps[i], occ, dBw, lBw);
            }

            lock (_lock)
            {
                _snapshot = snap;
                _snapshotAt = DateTime.Now;
            }
            // 誠實提示：全部讀值為 0 時，最可能是本機 BIOS／平台未開放 RDT 監測
            // （X299 韌體有此開關；本機實測 0xC81 啟用位元寫入後計數仍為 0）
            if (snap.All(s => s.Item2 <= 0 && s.Item3 <= 0) && ct.IsCancellationRequested == false)
                _status = "監測中…（讀值全部為 0：本機 BIOS／平台可能未開放 RDT 監測，資料如實呈現）";
            else
                _status = $"監測中…（{_lps.Length} 核心）";
            Thread.Sleep(1000);
        }
    }

    private void UnassignRmid()
    {
        try
        {
            foreach (var lp in _lps)
                _bridge?.WriteMsrPair(MsrPqrAssoc, 0, 0);   // 歸零
        }
        catch { }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _bridge?.Dispose(); } catch { }
    }
}
