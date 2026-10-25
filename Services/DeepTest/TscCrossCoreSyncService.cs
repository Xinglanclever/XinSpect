using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// U10-TS-0024「TSC 跨核同步檢查」：逐核取樣 TSC（MSR 0x10），把各核的 TSC 外推到同一
/// QPC 參考時刻，算最大偏移（skew）。判別碼家族前綴：<see cref="SkewCorePrefix"/>。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要量：DeepBench 的延遲／頻率量測都以「時基穩定」為前提。Invariant TSC 位元
/// （TimerFoundationService）只保證頻率不隨 C-state 變動，<b>不保證各核的 TSC 計數值彼此對齊</b>——
/// 跨核對讀才看得出 skew。
/// </para>
/// <para>
/// 選路（如實）：usermode 沒有 rdtsc 通路——<c>Stopwatch</c>（QPC）是全域單一時鐘，讀不到
/// 「某一核的 TSC 值」。倉庫既有的跨核讀取通路是 WinRing0 橋的 <c>ReadMsrPair64(0x10)</c>
/// （CeilingService 的 <c>MeasureTscHz</c> 同款）：本服務以 <see cref="CpuAffinity.Pinned"/>
/// （SetThreadGroupAffinity，本行程自己的執行緒、唯讀 MSR）逐核取樣，QPC 夾在 MSR 讀取前後
/// 取中點，抵銷橋接呼叫本身的延遲。<b>未提供讀取通路時如實回 NotAvailable，
/// 不改用親和性取樣冒充</b>（親和性只能釘核，讀不到 per-core TSC 值）。
/// </para>
/// <para>
/// 誠實界線：①<b>使用者明示觸發才跑</b>（掛在 DeepBench 入口模式），不自動跑；
/// ②skew 是量測值不是判決——QPC 中點夾取只抵銷線性延遲，MSR 讀取的抖動會留在數字裡，
/// 值如實標「含取樣延遲誤差」；③量測預算寫進常數（<see cref="SampleIntervalMs"/>／
/// <see cref="TotalBudgetMs"/>），超過即取前段核並標註截斷；④跨核 TSC 讀取必須逐核進行
/// （不同核可能不同群組），取樣為序列式，不是平行。
/// </para>
/// </remarks>
public static class TscCrossCoreSyncService
{
    private const string Category = "處理器";
    public const string SkewCorePrefix = "ts.tsc.skew.core.";
    public const string SkewMaxKey = "ts.tsc.skew.max";
    public const string Source = "WinRing0 MSR 0x10（唯讀）＋ QPC 中點夾取；逐核 SetThreadGroupAffinity 釘選";

    /// <summary>每核取樣間隔（毫秒）：兩次取樣隔這麼久，讓頻率項與偏移項可分離。</summary>
    public const int SampleIntervalMs = 100;

    /// <summary>取樣總預算（毫秒）。超過即取前段核並標註截斷。</summary>
    public const int TotalBudgetMs = 5000;

    /// <summary>一次取樣：QPC 中點（Stopwatch 刻）與該核 TSC 值。</summary>
    public sealed record TscSample(double QpcMid, ulong Tsc);

    /// <summary>一核的取樣對：核的全域索引、顯示標籤、兩次取樣。</summary>
    public sealed record CoreTscSample(int GlobalIndex, string Label, TscSample First, TscSample Second);

    /// <summary>
    /// 逐核取樣並算跨核 skew。測試以注入的樣本與頻率取代真實取樣，測試不真的跨核取樣。
    /// </summary>
    /// <param name="readTsc">
    /// 在「已釘到目標核」的當下讀一次 TSC 的委派（真實路徑傳
    /// <c>() =&gt; bridge.ReadMsrPair64(0x10)</c>）。null 時如實回 NotAvailable。
    /// </param>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<ulong?>? readTsc = null,
        IReadOnlyList<ProcessorRef>? processors = null,
        double? qpcFrequency = null,
        int? sampleIntervalMs = null)
    {
        if (readTsc is null)
        {
            return [new HardwareFact(SkewMaxKey, Category, "TSC 跨核同步", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "未提供跨核 TSC 讀取通路（WinRing0 MSR 橋）——usermode 無 rdtsc，親和性釘核讀不到 per-core TSC 值，不冒充量測")];
        }
        List<ProcessorRef> targets = [.. processors ?? CpuAffinity.AllLogicalProcessors()];
        int interval = sampleIntervalMs ?? SampleIntervalMs;
        int maxCores = Math.Max(1, TotalBudgetMs / Math.Max(1, 2 * interval));
        bool truncated = targets.Count > maxCores;
        if (truncated) targets = targets.Take(maxCores).ToList();

        var samples = new List<CoreTscSample>();
        foreach (ProcessorRef p in targets)
        {
            using var pin = CpuAffinity.Pinned(p);
            if (!pin.Ok)
            {
                continue;   // 釘核失敗的核記缺席，由 BuildFacts 的缺核清單呈現
            }
            if (!Sample(readTsc, out TscSample first)) continue;
            Thread.Sleep(interval);
            if (!Sample(readTsc, out TscSample second)) continue;
            samples.Add(new CoreTscSample(CpuAffinity.Global(p, CpuAffinity.GroupSizes()) ?? p.Index,
                p.Label(CpuAffinity.IsMultiGroup), first, second));
        }
        return BuildFacts(at, samples, qpcFrequency ?? Stopwatch.Frequency, truncated,
            (processors ?? CpuAffinity.AllLogicalProcessors()).Count);
    }

    /// <summary>純函式：逐核取樣集 → 事實集（逐核頻率＋最大偏移）。</summary>
    public static IReadOnlyList<HardwareFact> BuildFacts(DateTimeOffset at,
        IReadOnlyList<CoreTscSample> samples, double qpcFrequency,
        bool truncated = false, int totalProcessorCount = 0)
    {
        var facts = new List<HardwareFact>();
        var atRef = new List<(CoreTscSample Sample, double TscAtRef, double Hz)>();
        foreach (CoreTscSample s in samples)
        {
            string coreKey = $"{SkewCorePrefix}{s.GlobalIndex}";
            double dtQpc = (s.Second.QpcMid - s.First.QpcMid) / qpcFrequency;
            if (dtQpc <= 0 || s.Second.Tsc <= s.First.Tsc)
            {
                facts.Add(new HardwareFact(coreKey, Category, $"TSC 逐核取樣 {s.Label}", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    "取樣次序異常（TSC 或 QPC 未前進）——該核樣本不可信"));
                continue;
            }
            double hz = (s.Second.Tsc - s.First.Tsc) / dtQpc;
            atRef.Add((s, s.Second.Tsc, hz));
            facts.Add(new HardwareFact(coreKey, Category, $"TSC 逐核取樣 {s.Label}",
                $"{hz / 1e6:0.###} MHz（取樣窗 {dtQpc * 1000:0.#} ms）", "MHz", Source,
                FactTrustLevel.Measured, false, at, hz / 1e6, FactAvailability.Present, null));
        }

        double maxHz = atRef.Count == 0 ? 0 : atRef.Max(x => x.Hz);
        // 外推到同一參考時刻（最晚的第二取樣點），比各核 TSC 對齊後的跨度。
        // QPC 刻先除以頻率換成秒，再乘以該核的 TSC Hz——刻與赫茲不能直接相乘。
        double refQpc = atRef.Count == 0 ? 0 : atRef.Max(x => x.Sample.Second.QpcMid);
        var aligned = atRef
            .Select(x => x.TscAtRef + (refQpc - x.Sample.Second.QpcMid) / qpcFrequency * x.Hz)
            .ToList();

        if (atRef.Count < 2)
        {
            facts.Add(new HardwareFact(SkewMaxKey, Category, "TSC 跨核同步", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                atRef.Count == 0
                    ? "沒有任何核完成取樣——無法計算跨核偏移"
                    : "只有 1 個核完成取樣——跨核偏移需要至少 2 核，不以單核冒充"));
            return facts;
        }

        double skewTicks = aligned.Max() - aligned.Min();
        double skewNs = skewTicks / maxHz * 1e9;
        string truncation = truncated
            ? $"（核數超過 {TotalBudgetMs} ms 預算，取樣前 {samples.Count} 核——未涵蓋全部 {(totalProcessorCount == 0 ? samples.Count : totalProcessorCount)} 個邏輯處理器）"
            : "";
        facts.Add(new HardwareFact(SkewMaxKey, Category, "TSC 跨核同步",
            $"最大偏移 {skewNs:0} ns（{atRef.Count} 核外推至同一 QPC 參考時刻；含取樣延遲誤差——QPC 中點夾取只抵銷線性部分，不判決時基可否信賴）{truncation}",
            "ns", Source, FactTrustLevel.Measured, false, at, skewNs, FactAvailability.Present, null));
        return facts;
    }

    private static bool Sample(Func<ulong?> readTsc, out TscSample sample)
    {
        long a = Stopwatch.GetTimestamp();
        ulong? v = readTsc();
        long b = Stopwatch.GetTimestamp();
        if (v is not { } tsc)
        {
            sample = default!;
            return false;
        }
        sample = new TscSample((a + b) / 2.0, tsc);
        return true;
    }
}
