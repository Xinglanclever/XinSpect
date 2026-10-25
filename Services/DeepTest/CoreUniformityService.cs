using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// U10-TS-0026「逐核一致性測試（弱核偵測）」：把同一個小運算逐核輪跑（每核釘選），
/// 比各核吞吐與中位數的偏差，偏差超過門檻的核列為「疑似被限流」。判別碼家族前綴：
/// <see cref="UniformityPrefix"/>。
/// </summary>
/// <remarks>
/// <para>
/// 量的是：<b>負載面</b>的一致性——同一條運算鏈在各核上跑出來的吞吐差。
/// 既有的一致性檢查都在<b>頻率面</b>（PlatformReconciliationFactsService 的逐核對稱性
/// APERF/MPERF 跨幅、PowerPolicyService 的限頻顆數），負載面逐核輪跑全庫此前 0 命中——
/// 這是本服務的差異面。
/// </para>
/// <para>
/// 誠實界線：①<b>使用者明示觸發才跑</b>（掛在 DeepBench 入口模式），不自動跑；
/// ②<b>門檻是啟發式</b>（<see cref="DeviationThresholdFraction"/>，±15%）——超門檻的核
/// 值裡標「疑似」，<b>不判決</b>：背景中斷、SMT 兄弟核搶佔、暖機不足都會造成偏差，
/// 單一輪的吞吐差不足以定罪；③量的是本行程的運算吞吐，不是頻率判決（頻率面既有檢查
/// 見上）；④每核取樣時長有預算上限（<see cref="PerCoreBudgetMs"/>），工作量隨預算
/// 自適應截斷並標註；⑤逐核釘選用 <see cref="CpuAffinity.Pinned"/>（本行程自己的執行緒、
/// 唯讀運算），取不到拓撲時如實回報而非退回猜一個核數。
/// </para>
/// </remarks>
public static class CoreUniformityService
{
    private const string Category = "處理器";
    public const string UniformityPrefix = "ts.core.uniformity.";
    public const string Source = "同一運算鏈逐核釘選輪跑（SetThreadGroupAffinity；本行程自己的執行緒，唯讀運算）";

    /// <summary>偏差門檻（啟發式）：與中位數偏差超過此比例的核列「疑似被限流」。</summary>
    public const double DeviationThresholdFraction = 0.15;

    /// <summary>每核取樣預算（毫秒）。工作量不足預算時照實跑完，超過即停並標註截斷。</summary>
    public const int PerCoreBudgetMs = 200;

    /// <summary>一核的取樣：核的全域索引、顯示標籤、運算次數、耗時（Stopwatch 刻）、是否截斷。</summary>
    public sealed record CoreSample(int GlobalIndex, string Label, long Operations, long Ticks, bool Truncated);

    /// <summary>收集逐核一致性。測試以注入的樣本取代真實負載，測試不跑長負載。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<CoreSample>>? probe = null)
    {
        try
        {
            IReadOnlyList<CoreSample> samples = probe is { } p ? p() : RealCollect();
            return BuildFacts(at, samples);
        }
        catch (Exception ex)
        {
            return [new HardwareFact(UniformityPrefix + "probe", Category, "逐核一致性", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "量測失敗：" + ex.Message)];
        }
    }

    /// <summary>純函式：逐核樣本 → 事實集（逐核吞吐＋偏差標記＋spread 總覽）。</summary>
    public static IReadOnlyList<HardwareFact> BuildFacts(DateTimeOffset at, IReadOnlyList<CoreSample> samples)
    {
        var facts = new List<HardwareFact>();
        var measured = new List<(CoreSample Sample, double Mops)>();
        var errors = new List<(CoreSample Sample, string Reason)>();
        foreach (CoreSample s in samples)
        {
            if (s.Operations <= 0 || s.Ticks <= 0)
            {
                errors.Add((s, "運算量或耗時非正值——該核樣本不可信，不推算"));
                continue;
            }
            double mops = s.Operations / (s.Ticks / (double)Stopwatch.Frequency) / 1e6;
            measured.Add((s, mops));
        }
        var sorted = measured.Select(x => x.Mops).OrderBy(v => v).ToList();
        double median = Median(sorted);

        foreach (var (s, mops) in measured)
        {
            string key = $"{UniformityPrefix}core.{s.GlobalIndex}";
            string truncation = s.Truncated ? $"（達 {PerCoreBudgetMs} ms 預算即停）" : "";
            double dev = median > 0 ? (mops - median) / median : 0;
            string deviation = Math.Abs(dev) <= DeviationThresholdFraction
                ? $"與中位數偏差 {dev * 100:+0.#;-0.#}%（門檻內）"
                : $"與中位數 {median:0.##} MOPS 偏差 {dev * 100:+0.#;-0.#}%，超過啟發式門檻 ±{DeviationThresholdFraction * 100:0}%：疑似被限流" +
                  "（單輪吞吐差不構成判決；背景中斷、SMT 兄弟核搶佔、暖機不足都會造成偏差，需複測確認）";
            facts.Add(new HardwareFact(key, Category, $"逐核吞吐 {s.Label}",
                $"{mops:0.##} MOPS（{s.Operations} 次運算）{truncation}；{deviation}",
                "MOPS", Source, FactTrustLevel.Measured, false, at, mops, FactAvailability.Present, null));
        }
        foreach (var (s, reason) in errors)
        {
            facts.Add(new HardwareFact($"{UniformityPrefix}core.{s.GlobalIndex}", Category,
                $"逐核吞吐 {s.Label}", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, reason));
        }
        if (measured.Count == 0)
        {
            facts.Add(new HardwareFact(UniformityPrefix + "spread", Category, "逐核一致性", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有任何核完成取樣——取不到拓撲或全部釘核失敗，不以單核或猜測冒充"));
            return facts;
        }
        if (median <= 0)
        {
            facts.Add(new HardwareFact(UniformityPrefix + "spread", Category, "逐核一致性", "", "", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                "中位數非正值——無法計算偏差"));
            return facts;
        }

        double spread = (sorted.Max() - sorted.Min()) / median;
        facts.Add(new HardwareFact(UniformityPrefix + "spread", Category, "逐核一致性",
            $"{measured.Count} 核輪跑同一運算鏈：中位數 {median:0.##} MOPS、最快/最慢跨幅 {spread * 100:0.#}%（中位數基準）；" +
            $"偏差超過 ±{DeviationThresholdFraction * 100:0}% 的核在逐核鍵標「疑似」，本鍵不列名、不判決",
            "%", Source, FactTrustLevel.Measured, false, at, spread * 100, FactAvailability.Present, null));
        return facts;
    }

    /// <summary>純函式：有序樣本的中位數（偶數取中間兩值平均）。</summary>
    public static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // ── 真實負載（使用者明示觸發才會走到）────────────────────────────────────

    private static IReadOnlyList<CoreSample> RealCollect()
    {
        bool multiGroup = CpuAffinity.IsMultiGroup;
        var groupSizes = CpuAffinity.GroupSizes();
        var cores = CpuAffinity.PhysicalCores(multiGroup,
            multiGroup ? 0UL : (ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64());
        var samples = new List<CoreSample>();
        foreach (var (core, first, _) in cores)
        {
            using var pin = CpuAffinity.Pinned(first);
            if (!pin.Ok) continue;
            samples.Add(Measure(CpuAffinity.Global(first, groupSizes) ?? core,
                first.Label(multiGroup)));
        }
        return samples;
    }

    private static CoreSample Measure(int globalIndex, string label)
    {
        const int chunk = 1 << 16;
        long operations = 0;
        bool truncated = false;
        double acc = Warmup();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < PerCoreBudgetMs)
        {
            for (int i = 0; i < chunk; i++) acc += Math.Sqrt(acc + i);
            operations += chunk;
            truncated = true;
        }
        sw.Stop();
        // 讀回累加器防止 JIT 消除整條鏈；讀回本身不改變運算計數的語意
        operations += acc == 1234.5 ? 1 : 0;
        return new CoreSample(globalIndex, label, operations, sw.ElapsedTicks, truncated);
    }

    /// <summary>暖機：讓 JIT 先編譯運算鏈（不計時），回傳累加器種值。</summary>
    private static double Warmup()
    {
        double acc = 1.0;
        for (int i = 0; i < (1 << 12); i++) acc += Math.Sqrt(acc + i);
        return acc;
    }
}
