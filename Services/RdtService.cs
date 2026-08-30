using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>單一邏輯處理器的 RDT 讀值列。</summary>
public sealed class RdtCoreRow
{
    public RdtCoreRow(ProcessorRef lp, double occupancyMb, double totalBw, double localBw, bool multiGroup = false)
    { Ref = lp; OccupancyMb = occupancyMb; TotalBw = totalBw; LocalBw = localBw; MultiGroup = multiGroup; }

    /// <summary>此列對應的邏輯處理器（含處理器群組）。</summary>
    public ProcessorRef Ref { get; }
    /// <summary>是否為多群組機器（決定標籤要不要標明群組）。</summary>
    public bool MultiGroup { get; }
    public int Lp => Ref.Index;
    /// <summary>顯示用標籤；多群組時標明群組，否則兩個群組的「LP0」會長得一模一樣。</summary>
    public string LpText => Ref.Label(MultiGroup);
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
/// <para>
/// 本機（36 LP／CPUID 0x0F.0 EBX=0x8F、0x0F.1 upscaling=73728、maxRmid=143、events=0x7）實測：
/// 逐核心釘選後寫 PQR_ASSOC 會落地（回讀 0x1_0000_0000，RMID 欄位＝1），QM_CTR 也沒有回 Error／
/// Unavailable 旗標，但三個事件在相隔一秒的兩次取樣中都是 0——即計數器有效卻不遞增。
/// 這是平台／韌體未開放 RDT 監測的樣子，如實顯示 0，不用估計值頂替。
/// </para>
/// </remarks>
public sealed class RdtService : ObservableObject, IDisposable
{
    private const uint MsrPqrAssoc = 0xC8F;
    private const uint MsrQmEvtsel = 0xC8D;
    private const uint MsrQmCtr = 0xC8E;
    private const uint EventL3Occupancy = 1;
    private const uint EventTotalMbm = 2;
    private const uint EventLocalMbm = 3;
    /// <summary>本工具統一指派的 RMID。0 是預設／未分類，故用 1。</summary>
    private const int UsedRmid = 1;

    private WinRing0Bridge? _bridge;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private double _upscaling = 1;
    private int _maxRmid;
    private bool _hasOccupancy, _hasTotalBw, _hasLocalBw;
    private ProcessorRef[] _lps = [];
    private bool _multiGroup;

    // 背景執行緒寫入、UI（Tick）讀取的快照
    private readonly object _lock = new();
    private (ProcessorRef Lp, double Occ, double Total, double Local)[] _snapshot = [];
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
        // 先取得核心清單，DetectSupport 的說明文字才不會顯示「0 邏輯處理器」。
        _lps = CpuAffinity.AllLogicalProcessors().ToArray();
        _multiGroup = CpuAffinity.IsMultiGroup;
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
        // 背景執行緒直接寫 _status 欄位（不能在非 UI 執行緒發通知），所以由這裡代為通知。
        OnPropertyChanged(nameof(Status));
        (ProcessorRef, double, double, double)[] snap;
        lock (_lock) snap = _snapshot;
        Rows.Clear();
        double max = snap.Length > 0 ? snap.Max(s => s.Item2) : 0;
        if (max <= 0) max = 1;
        double total = 0;
        foreach (var (lp, occ, totalBw, localBw) in snap)
        {
            if (totalBw > 0) total += totalBw;
            Rows.Add(new RdtCoreRow(lp, occ, totalBw, localBw, _multiGroup)
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
        // EDX bit0＝L3 占用、bit1＝總頻寬、bit2＝本機頻寬。能力要如實列出，不能假設三者都有。
        var edx = (uint)r1.Edx;
        bool hasOcc = (edx & 0x1) != 0, hasTotal = (edx & 0x2) != 0, hasLocal = (edx & 0x4) != 0;

        _upscaling = upscaling == 0 ? 1 : upscaling;   // 0 視為不可用，退回 1 並在文字上標明
        _maxRmid = maxRmid;
        _hasOccupancy = hasOcc;
        _hasTotalBw = hasTotal;
        _hasLocalBw = hasLocal;

        if (maxRmid < UsedRmid)
        {
            RdtSupported = false;
            SupportText = $"最大 RMID 為 {maxRmid}，不足以使用 RMID {UsedRmid}（CPUID 0x0F.1 ECX）";
            return;
        }

        RdtSupported = true;
        string events = string.Join("／", new[]
        {
            hasOcc ? "L3 占用" : null,
            hasTotal ? "總頻寬" : null,
            hasLocal ? "本機頻寬" : null,
        }.Where(s => s is not null));
        SupportText = $"支援：upscaling {_upscaling:0} bytes、最大 RMID {maxRmid}、"
                    + $"可用事件 {(events.Length == 0 ? "（無）" : events)}、{_lps.Length} 邏輯處理器"
                    + (_multiGroup ? $"（跨 {CpuAffinity.GroupCount} 個處理器群組，此路徑未在實機驗證過）" : "")
                    + (upscaling == 0 ? "（CPUID 回報 upscaling 為 0，占用值不可信）" : "");
    }

    /// <summary>
    /// 讀 QM_CTR（0xC8E）：位 63＝Error（RMID 或事件無效）、位 62＝Unavailable（本次無資料），
    /// 資料在位 61:0。任一旗標成立即回 null——**不得把錯誤碼當成讀值**。
    /// </summary>
    private ulong? ReadQmCtr()
    {
        if (_bridge!.ReadMsrPair64(MsrQmCtr) is not { } raw) return null;
        if ((raw & (1UL << 63)) != 0) return null;   // Error
        if ((raw & (1UL << 62)) != 0) return null;   // Unavailable
        return raw & 0x3FFF_FFFF_FFFF_FFFFUL;
    }

    private void WorkerLoop(CancellationToken ct)
    {
        _lps = CpuAffinity.AllLogicalProcessors().ToArray();
        _multiGroup = CpuAffinity.IsMultiGroup;
        DetectSupport();
        if (!RdtSupported || _lps.Length == 0) { _status = SupportText; return; }

        // 指派 RMID 給每個邏輯處理器（PQR_ASSOC 的 RMID 在位 51:32）。
        // MSR 寫入只作用於「當下執行的那顆核心」，所以每寫一次都必須先把自己釘到該核心上，
        // 否則只有恰好排到的那一顆被指派，其餘核心讀出來永遠是 0。
        // 釘選走 CpuAffinity：離開 using 範圍時以「原本的群組親和性」還原，
        // 而不是塞回一份自己記的遮罩——後者在多群組機器上會把工作執行緒鎖死在群組 0。
        foreach (var lp in _lps)
        {
            using var pin = CpuAffinity.Pinned(lp);
            if (!pin.Ok) { Diag.Swallow("RDT 指派 RMID 釘選", null, $"{lp.Label(true)} 未指派到 RMID，該核讀值會是 0"); continue; }
            try { _bridge!.WriteMsrPair(MsrPqrAssoc, 0, UsedRmid); }   // EAX=0、EDX=RMID（位 51:32）
            catch (Exception ex) { Diag.Swallow("RDT 寫入 PQR_ASSOC", ex, $"{lp.Label(true)} 未指派到 RMID，該核讀值會是 0"); }
        }

        var lastTotal = new ulong[_lps.Length];
        var lastLocal = new ulong[_lps.Length];
        var hasPrev = new bool[_lps.Length];
        var sw = Stopwatch.StartNew();
        double lastSec = 0;

        while (!ct.IsCancellationRequested)
        {
            var snap = new (ProcessorRef, double, double, double)[_lps.Length];
            double elapsed = sw.Elapsed.TotalSeconds;
            double dt = Math.Max(elapsed - lastSec, 0.05);
            lastSec = elapsed;

            for (int i = 0; i < _lps.Length; i++)
            {
                double occ = -1;
                ulong t = 0, l = 0;
                bool tOk = false, lOk = false;

                using (var pin = CpuAffinity.Pinned(_lps[i]))
                {
                    if (!pin.Ok) { snap[i] = (_lps[i], -1, -1, -1); continue; }
                    try
                    {
                        if (_hasOccupancy)
                        {
                            _bridge!.WriteMsrPair(MsrQmEvtsel, EventL3Occupancy, UsedRmid);
                            if (ReadQmCtr() is { } occRaw)
                                occ = occRaw * _upscaling / (1024.0 * 1024.0);
                        }

                        if (_hasTotalBw)
                        {
                            _bridge!.WriteMsrPair(MsrQmEvtsel, EventTotalMbm, UsedRmid);
                            if (ReadQmCtr() is { } tv) { t = tv; tOk = true; }
                        }

                        if (_hasLocalBw)
                        {
                            _bridge!.WriteMsrPair(MsrQmEvtsel, EventLocalMbm, UsedRmid);
                            if (ReadQmCtr() is { } lv) { l = lv; lOk = true; }
                        }
                    }
                    catch (Exception ex)
                    {
                        Diag.Swallow("RDT 取樣", ex, $"{_lps[i].Label(true)} 本次讀值顯示 —");
                    }
                }

                // MBM 計數的單位同樣要乘 upscaling 才是位元組；差分為負＝回繞或重置，該秒不計。
                double dBw = -1, lBw = -1;
                if (tOk && hasPrev[i] && t >= lastTotal[i])
                    dBw = (t - lastTotal[i]) * _upscaling / dt / (1024.0 * 1024.0);
                if (lOk && hasPrev[i] && l >= lastLocal[i])
                    lBw = (l - lastLocal[i]) * _upscaling / dt / (1024.0 * 1024.0);

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
            // 誠實提示：全部讀值為 0／讀不到時，最可能是本機 BIOS／平台未開放 RDT 監測。
            // 註：監測（CMT／MBM）不需要任何全域啟用位元——IA32_L3_QOS_CFG（0xC81）bit0 是 L3 CAT 的
            // CDP 啟用，與監測無關，不要為了「讓數字動起來」去寫它。
            if (snap.All(s => s.Item2 <= 0 && s.Item3 <= 0) && !ct.IsCancellationRequested)
                _status = "監測中…（讀值全部為 0：本機 BIOS／平台可能未開放 RDT 監測，資料如實呈現）";

            else
                _status = $"監測中…（{_lps.Length} 核心）";
            Thread.Sleep(1000);
        }
    }

    /// <summary>把每個核心的 RMID 歸零。同樣必須逐核心釘選，否則只會清掉一顆。</summary>
    private void UnassignRmid()
    {
        if (_bridge is null) return;
        foreach (var lp in _lps)
        {
            using var pin = CpuAffinity.Pinned(lp);
            if (!pin.Ok) { Diag.Swallow("RDT 歸零 RMID 釘選", null, $"{lp.Label(true)} 的 RMID 仍指向 {UsedRmid}"); continue; }
            try { _bridge.WriteMsrPair(MsrPqrAssoc, 0, 0); }
            catch (Exception ex) { Diag.Swallow("RDT 歸零 PQR_ASSOC", ex, $"{lp.Label(true)} 的 RMID 仍指向 {UsedRmid}"); }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _bridge?.Dispose(); } catch (Exception ex) { Diag.Swallow("RDT 橋接釋放", ex, "無；程式即將結束"); }
    }
}
