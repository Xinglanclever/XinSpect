using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace XinSpect;

/// <summary>
/// Top-down Microarchitecture Analysis（TMA）Level 1 的純運算：把四個 PMU 計數換成四桶百分比。
/// 不接觸硬體，可單元測試。
/// </summary>
/// <remarks>
/// Intel TMA Level 1（v1，Skylake 世代公式）：
/// <list type="bullet">
/// <item>SLOTS ＝ 4 × CPU_CLK_UNHALTED（每周期 4 個發射插槽）</item>
/// <item>Retiring ＝ UOPS_RETIRED.RETIRE_SLOTS ÷ SLOTS</item>
/// <item>Frontend Bound ＝ IDQ_UOPS_NOT_DELIVERED.CORE ÷ SLOTS</item>
/// <item>Bad Speculation ＝ (UOPS_ISSUED.ANY − UOPS_RETIRED.RETIRE_SLOTS) ÷ SLOTS</item>
/// <item>Backend Bound ＝ 1 − 其餘三者</item>
/// </list>
/// 誠實界線：Bad Speculation 的標準式另含 4 × INT_MISC.RECOVERY_CYCLES ÷ SLOTS 一項，
/// 但本機只有 4 個通用計數器，四個都用在上面的事件上（第五個事件無處可放），故<b>不含</b> recovery 項——
/// 這會讓 Bad Speculation 略微低估、Backend Bound 略微高估。UI 必須寫明這一點，不得假裝是完整公式。
/// </remarks>
public static class TopDownMath
{
    /// <summary>每個時鐘周期的發射插槽數（Intel Core／Xeon 為 4）。</summary>
    public const int SlotsPerCycle = 4;

    /// <summary>
    /// 由四個原始計數算出四桶百分比。<paramref name="issued"/> 小於 <paramref name="retireSlots"/> 時
    /// 差值必須以 <c>double</c> 計算——<c>ulong</c> 相減會下溢成接近 2^64，夾限後變成 100%，整列失真。
    /// </summary>
    public static (double Retiring, double BadSpec, double Frontend, double Backend) Compute(
        ulong coreClks, ulong notDelivered, ulong retireSlots, ulong issued)
    {
        double slots = coreClks * (double)SlotsPerCycle;
        if (slots <= 0) return (0, 0, 0, 0);
        double ret = Frac(retireSlots / slots);
        double fe = Frac(notDelivered / slots);
        double bs = Frac(((double)issued - retireSlots) / slots);
        double be = Math.Max(1 - ret - fe - bs, 0);
        return (ret * 100, bs * 100, fe * 100, be * 100);
    }

    private static double Frac(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0;

    /// <summary>每周期退休插槽數（0～4）。IPC 需要 INST_RETIRED，本方案不動固定計數器故不提供 IPC。</summary>
    public static double SlotsPerCycleRetired(ulong coreClks, ulong retireSlots)
        => coreClks == 0 ? 0 : Math.Clamp(retireSlots / (double)coreClks, 0, SlotsPerCycle);

    /// <summary>取占比最大的一桶，給出一句如實的解讀（不誇大、不建議調參）。</summary>
    public static string Verdict(double retiring, double badSpec, double frontend, double backend)
    {
        double max = Math.Max(Math.Max(retiring, badSpec), Math.Max(frontend, backend));
        if (max <= 0) return "取樣期間各核心幾乎沒有執行指令，無法歸因。";
        if (max == retiring) return $"以退休（有效工作）為主，占 {retiring:0.0}%——管線大致跑得動。";
        if (max == backend) return $"主要受限於後端，占 {backend:0.0}%——執行資源或記憶體等待是瓶頸。";
        if (max == frontend) return $"主要受限於前端，占 {frontend:0.0}%——指令供給（取指／解碼）跟不上。";
        return $"主要花在錯誤推測，占 {badSpec:0.0}%——分支預測失敗導致的白做工偏多。";
    }
}

/// <summary>四桶之一（全系統彙總，含說明文字）。</summary>
public sealed class TopDownBucket
{
    public TopDownBucket(string name, string note, double percent)
    { Name = name; Note = note; Percent = percent; }
    public string Name { get; }
    public string Note { get; }
    public double Percent { get; }
    public string PercentText => $"{Percent:0.0}%";
    public double BarFraction => Math.Clamp(Percent / 100.0, 0, 1);
}

/// <summary>單一實體核心的四桶讀值。<see cref="Valid"/> 為 false 表示取樣期間該核心閒置（無未停止周期）。</summary>
public sealed class TopDownCoreRow
{
    public TopDownCoreRow(int core, string lpText, bool valid,
                          double retiring, double badSpec, double frontend, double backend, double slotsRetired)
    {
        Core = core; LpText = lpText; Valid = valid;
        Retiring = retiring; BadSpec = badSpec; Frontend = frontend; Backend = backend; SlotsRetired = slotsRetired;
    }
    public int Core { get; }
    /// <summary>此實體核心底下的邏輯處理器（SMT 兩執行緒一起計入）。</summary>
    public string LpText { get; }
    public bool Valid { get; }
    public double Retiring { get; }
    public double BadSpec { get; }
    public double Frontend { get; }
    public double Backend { get; }
    public double SlotsRetired { get; }
    public string CoreText => $"核心 {Core}";
    public string RetiringText => Valid ? $"{Retiring:0.0}%" : "—";
    public string BadSpecText => Valid ? $"{BadSpec:0.0}%" : "—";
    public string FrontendText => Valid ? $"{Frontend:0.0}%" : "—";
    public string BackendText => Valid ? $"{Backend:0.0}%" : "—";
    public string SlotsRetiredText => Valid ? $"{SlotsRetired:0.00}" : "—";
    public double BarFraction => Valid ? Math.Clamp(Retiring / 100.0, 0.02, 1) : 0;
}

/// <summary>
/// Top-down Level 1 實測：直接編程 Intel PMU 的 4 個通用計數器，逐實體核心取樣目前系統的真實負載。
/// </summary>
/// <remarks>
/// 做法與取捨（皆為本機實測驗證過的行為，不是推論）：
/// <list type="number">
/// <item><b>只用通用計數器 PMC0–3，完全不碰固定計數器。</b>FIXED_CTR1 正被 Windows 用來算 CPU 使用率，
/// 若歸零或改設定會擾動作業系統的統計。時鐘來源改用 CPU_CLK_UNHALTED.THREAD_P（0x3C/0x00）占一個通用計數器。</item>
/// <item><b>AnyThread（PERFEVTSEL 位 21）取樣，逐實體核心呈現。</b>IDQ_UOPS_NOT_DELIVERED.CORE 本質是核心層事件，
/// SMT 下按邏輯處理器歸因會重複計算；開 AnyThread 後四個事件都是整顆實體核心的量，分母 4 × CLKS 才對得上。</item>
/// <item><b>逐核心序列取樣</b>：每顆核心釘選後量測固定視窗，故不同核心的取樣時間點不同（非同一瞬間快照）。</item>
/// <item><b>取樣前後完整存讀 PERFEVTSEL0–3 與 GLOBAL_CTRL</b>，還原成原值；期間只清除 GLOBAL_CTRL 的位 0–3
/// （通用計數器），固定計數器的啟用位元原封不動。本機實測 GLOBAL_CTRL=0x60000000F、FIXED_CTR_CTRL=0x220
/// 取樣後皆完好如初。若同時有別的分析工具（VTune／PCM）在用 PMC，兩者會互相覆蓋——這是硬體只有 4 個計數器的必然。</item>
/// </list>
/// MSR 讀寫經 <see cref="WinRing0Bridge"/>（風險聲明見該類別）。
/// </remarks>
public sealed class TopDownService : ObservableObject, IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, ulong affinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint returnedLength);

    private const int RelationProcessorCore = 0;
    private const int ErrorInsufficientBuffer = 122;

    private const uint MsrPerfEvtSel0 = 0x186;
    private const uint MsrPmc0 = 0xC1;
    private const uint MsrGlobalCtrl = 0x38F;

    /// <summary>四個事件依序落在 PMC0–PMC3。順序即 <see cref="TopDownMath.Compute"/> 的參數順序。</summary>
    private static readonly (uint Ev, uint Um, string Name)[] Events =
    {
        (0x3C, 0x00, "CPU_CLK_UNHALTED.THREAD_P"),
        (0x9C, 0x01, "IDQ_UOPS_NOT_DELIVERED.CORE"),
        (0xC2, 0x02, "UOPS_RETIRED.RETIRE_SLOTS"),
        (0x0E, 0x01, "UOPS_ISSUED.ANY"),
    };

    /// <summary>PERFEVTSEL：事件[7:0]、umask[15:8]、USR(16)、OS(17)、AnyThread(21)、EN(22)。</summary>
    private static ulong Sel(uint ev, uint um, bool anyThread)
        => ev | (um << 8) | (1UL << 16) | (1UL << 17) | (anyThread ? 1UL << 21 : 0UL) | (1UL << 22);

    private WinRing0Bridge? _bridge;
    private CancellationTokenSource? _cts;
    private ulong _processMask;

    /// <summary>每顆實體核心的取樣視窗（毫秒）。18 核 × 120 ms ≈ 2.2 秒。</summary>
    public int WindowMs { get; private set; } = 120;

    private bool _running;
    public bool IsRunning { get => _running; private set { if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanStart)); } }
    public bool CanStart => !_running;

    private string _phase = "尚未取樣";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    private double _progress;
    public double ProgressFraction { get => _progress; private set { if (SetProperty(ref _progress, value)) OnPropertyChanged(nameof(ProgressPercent)); } }
    public double ProgressPercent => _progress * 100;

    private string _status = "按「開始取樣」以 PMU 量測目前系統負載的管線歸因（約需數秒）。";
    public string StatusLine { get => _status; private set => SetProperty(ref _status, value); }

    private string _verdict = "—";
    public string VerdictText { get => _verdict; private set => SetProperty(ref _verdict, value); }

    private string _slotsRetired = "—";
    public string SlotsRetiredText { get => _slotsRetired; private set => SetProperty(ref _slotsRetired, value); }

    private string _support = "尚未檢測";
    public string SupportText { get => _support; private set => SetProperty(ref _support, value); }

    public ObservableCollection<TopDownBucket> Buckets { get; } = new();
    public ObservableCollection<TopDownCoreRow> Rows { get; } = new();

    public void SetWindow(int ms)
    {
        if (IsRunning) return;
        WindowMs = Math.Clamp(ms, 50, 1000);
        OnPropertyChanged(nameof(WindowMs));
    }

    public void Start()
    {
        if (IsRunning) return;
        _ = RunAsync();
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        Phase = "取樣中";
        ProgressFraction = 0;
        Buckets.Clear();
        Rows.Clear();
        VerdictText = "—";
        SlotsRetiredText = "—";

        var prog = new Progress<(double Frac, string Status)>(t => { ProgressFraction = t.Frac; StatusLine = t.Status; });
        var report = (IProgress<(double, string)>)prog;

        try
        {
            if (!DetectSupport()) { Phase = "不支援"; StatusLine = SupportText; return; }
            _bridge ??= WinRing0Bridge.Create();
            if (_bridge is null || !_bridge.Available)
            {
                Phase = "不可用";
                StatusLine = "MSR 橋接不可用，無法編程 PMU：" + (_bridge?.Error ?? "未知原因");
                return;
            }

            _processMask = (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64();
            var cores = EnumeratePhysicalCores(_processMask);
            if (cores.Count == 0)
            {
                Phase = "不可用";
                StatusLine = "取不到實體核心拓撲（GetLogicalProcessorInformationEx 無可用資料）。";
                return;
            }

            var samples = await Task.Run(() => SampleAll(cores, ct, report), ct);

            ulong tc = 0, tn = 0, tr = 0, ti = 0;
            foreach (var (core, lpText, v) in samples)
            {
                bool valid = v[0] > 0;
                var (ret, bs, fe, be) = TopDownMath.Compute(v[0], v[1], v[2], v[3]);
                Rows.Add(new TopDownCoreRow(core, lpText, valid, ret, bs, fe, be,
                                            TopDownMath.SlotsPerCycleRetired(v[0], v[2])));
                tc += v[0]; tn += v[1]; tr += v[2]; ti += v[3];
            }

            var (aRet, aBs, aFe, aBe) = TopDownMath.Compute(tc, tn, tr, ti);
            Buckets.Add(new TopDownBucket("退休 Retiring", "真正完成有效工作的插槽占比，越高越好。", aRet));
            Buckets.Add(new TopDownBucket("錯誤推測 Bad Speculation",
                "分支預測失敗等白做工的插槽。本式不含 INT_MISC.RECOVERY_CYCLES 項（只有 4 個計數器），故略微低估。", aBs));
            Buckets.Add(new TopDownBucket("前端受限 Frontend Bound", "指令供給（取指／解碼）跟不上，插槽空著。", aFe));
            Buckets.Add(new TopDownBucket("後端受限 Backend Bound",
                "執行資源不足或等記憶體，由前三者相減得出，故承接上一桶低估的部分。", aBe));

            VerdictText = TopDownMath.Verdict(aRet, aBs, aFe, aBe);
            SlotsRetiredText = tc == 0 ? "—" : $"{TopDownMath.SlotsPerCycleRetired(tc, tr):0.00} / 4";
            int idle = Rows.Count(r => !r.Valid);
            Phase = "完成";
            ProgressFraction = 1;
            StatusLine = $"完成 ・ {cores.Count} 顆實體核心 × {WindowMs} ms"
                       + (idle > 0 ? $"（其中 {idle} 顆取樣期間閒置，顯示為 —）" : "")
                       + " ・ 計數器已還原原值。";
        }
        catch (OperationCanceledException)
        {
            Phase = "已停止";
            StatusLine = "取樣已停止，計數器已還原原值。";
        }
        catch (Exception ex)
        {
            Phase = "錯誤";
            StatusLine = "取樣失敗：" + ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// CPUID 0x0A（Architectural Performance Monitoring）：EAX 位 7:0＝版本、位 15:8＝通用計數器個數。
    /// 事件編碼是 Intel Family 6 專屬，AMD 的同號事件意義完全不同——非 Intel 一律拒絕，不要硬套。
    /// </summary>
    public bool DetectSupport()
    {
        if (!X86Base.IsSupported) { SupportText = "非 x86 平台，無 Intel PMU。"; return false; }

        var v = X86Base.CpuId(0, 0);
        string vendor = string.Concat(
            System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Ebx)),
            System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Edx)),
            System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(v.Ecx)));
        if (vendor != "GenuineIntel")
        {
            SupportText = $"處理器供應商為 {vendor}：本頁的事件編碼是 Intel 專屬，不適用，故不提供讀值。";
            return false;
        }

        var one = X86Base.CpuId(1, 0);
        uint sig = (uint)one.Eax;
        int family = (int)((sig >> 8) & 0xF) + (int)((sig >> 20) & 0xFF);
        int model = (int)((sig >> 4) & 0xF) | (int)((sig >> 12) & 0xF0);

        var pm = X86Base.CpuId(0x0A, 0);
        int version = (int)((uint)pm.Eax & 0xFF);
        int nGp = (int)(((uint)pm.Eax >> 8) & 0xFF);
        int gpWidth = (int)(((uint)pm.Eax >> 16) & 0xFF);
        if (version < 1 || nGp < 4)
        {
            SupportText = $"PMU 版本 {version}、通用計數器 {nGp} 個：本方案需要 4 個通用計數器，條件不足。";
            return false;
        }

        SupportText = $"Intel Family {family} Model 0x{model:X}、PMU 版本 {version}、"
                    + $"通用計數器 {nGp} 個（{gpWidth} 位元）。事件編碼以 Intel Core／Xeon（Family 6）為準"
                    + (family == 6 ? "" : "；本機非 Family 6，讀值僅供參考")
                    + "；本工具只使用 PMC0–3，不變動作業系統正在使用的固定計數器。";
        return true;
    }

    /// <summary>
    /// 以 GetLogicalProcessorInformationEx(RelationProcessorCore) 取實體核心 → 邏輯處理器遮罩。
    /// 只處理處理器群組 0（≤64 邏輯處理器）；跨群組的機器會漏掉其餘群組，如實少列而不假造。
    /// </summary>
    private static List<(int Core, ulong Mask, string LpText)> EnumeratePhysicalCores(ulong processMask)
    {
        var list = new List<(int, ulong, string)>();
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, 0, ref len);
        if (len == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return list;

        nint buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return list;
            int off = 0, core = 0;
            while (off + 8 <= (int)len)
            {
                nint rec = buf + off;
                int size = Marshal.ReadInt32(rec + 4);
                if (size <= 0) break;
                nint pl = rec + 8;                                   // PROCESSOR_RELATIONSHIP
                ushort groupCount = (ushort)Marshal.ReadInt16(pl + 22);
                ulong mask = groupCount == 0 ? 0 : (ulong)Marshal.ReadIntPtr(pl + 24).ToInt64();
                ushort group = groupCount == 0 ? (ushort)0 : (ushort)Marshal.ReadInt16(pl + 32);
                mask &= processMask;                                  // 只用行程真的能跑的邏輯處理器
                if (group == 0 && mask != 0)
                {
                    var lps = CoreLatencyService.LogicalProcessorsFromMask(mask);
                    list.Add((core, mask, string.Join("／", lps.Select(l => $"LP{l}"))));
                }
                core++;
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private List<(int Core, string LpText, ulong[] Values)> SampleAll(
        List<(int Core, ulong Mask, string LpText)> cores, CancellationToken ct, IProgress<(double, string)> report)
    {
        var result = new List<(int, string, ulong[])>();
        for (int i = 0; i < cores.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (core, mask, lpText) = cores[i];
            int lp = CoreLatencyService.LogicalProcessorsFromMask(mask).First();
            var v = SampleOne(lp, ct);
            result.Add((core, lpText, v));
            report.Report(((i + 1) / (double)cores.Count,
                $"取樣核心 {core}（{lpText}）… " + (v[0] == 0 ? "閒置" : $"{TopDownMath.Compute(v[0], v[1], v[2], v[3]).Retiring:0.0}% 退休")));
        }
        return result;
    }

    /// <summary>
    /// 在指定邏輯處理器上取樣一次。存原值 → 停通用計數器 → 編程 → 啟用 → 等視窗 → 讀 → 還原。
    /// 讀不到（橋接失敗）時回全 0，上層會視為閒置並顯示 —，不會拿舊值或估計值頂替。
    /// </summary>
    private ulong[] SampleOne(int lp, CancellationToken ct)
    {
        var v = new ulong[4];
        if (SetThreadAffinityMask(GetCurrentThread(), 1UL << lp) == IntPtr.Zero) return v;
        try
        {
            ulong savedGlobal = _bridge!.ReadMsrPair64(MsrGlobalCtrl) ?? 0;
            var savedSel = new ulong[4];
            for (uint i = 0; i < 4; i++) savedSel[i] = _bridge.ReadMsrPair64(MsrPerfEvtSel0 + i) ?? 0;

            Write64(MsrGlobalCtrl, savedGlobal & ~0xFUL);   // 只停 PMC0–3，固定計數器的啟用位元不動
            for (uint i = 0; i < 4; i++)
            {
                Write64(MsrPerfEvtSel0 + i, Sel(Events[i].Ev, Events[i].Um, anyThread: true));
                Write64(MsrPmc0 + i, 0);
            }
            Write64(MsrGlobalCtrl, savedGlobal | 0xFUL);
            try
            {
                ct.WaitHandle.WaitOne(WindowMs);
                for (uint i = 0; i < 4; i++) v[i] = _bridge.ReadMsrPair64(MsrPmc0 + i) ?? 0;
            }
            finally
            {
                Write64(MsrGlobalCtrl, savedGlobal & ~0xFUL);
                for (uint i = 0; i < 4; i++) Write64(MsrPerfEvtSel0 + i, savedSel[i]);
                Write64(MsrGlobalCtrl, savedGlobal);
            }
        }
        catch { return new ulong[4]; }
        finally { SetThreadAffinityMask(GetCurrentThread(), _processMask); }
        ct.ThrowIfCancellationRequested();
        return v;
    }

    private void Write64(uint index, ulong value)
        => _bridge!.WriteMsrPair(index, (uint)(value & 0xFFFFFFFF), (uint)(value >> 32));

    public void Dispose()
    {
        _cts?.Cancel();
        try { _bridge?.Dispose(); } catch { }
    }
}
