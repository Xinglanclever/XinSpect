namespace XinSpect;

/// <summary>一筆中斷事件裡我們用得到的東西：哪一支驅動、落在哪顆邏輯處理器。</summary>
public readonly record struct IsrSample(string Module, int Cpu);

/// <summary>某顆邏輯處理器上的中斷統計。</summary>
public sealed class CpuInterruptRow
{
    public required int Cpu { get; init; }
    public required long Count { get; init; }
    /// <summary>這顆核上事件最多的驅動。</summary>
    public required string TopModule { get; init; }
    /// <summary>該驅動佔這顆核上所有中斷的百分比。</summary>
    public required double TopSharePercent { get; init; }
    /// <summary>佔全機中斷的百分比。</summary>
    public required double OfAllPercent { get; init; }

    public string Name => $"CPU {Cpu}";
    public string CountText => $"{Count:N0} 次";
    public string Text => $"最多來自 {TopModule}（佔這顆核的 {TopSharePercent:0.#}%）";
    /// <summary>畫長條用：相對於最忙那顆核的比例。</summary>
    public double BarFraction { get; set; }
}

/// <summary>某一支驅動的中斷分佈。</summary>
public sealed class ModuleAffinityRow
{
    public required string Module { get; init; }
    public required long Count { get; init; }
    public required int TopCpu { get; init; }
    public required double TopSharePercent { get; init; }
    /// <summary>這支驅動的中斷出現過的核心數。</summary>
    public required int CpuCount { get; init; }

    public string CountText => $"{Count:N0} 次";

    /// <summary>集中或分散——這一句決定使用者要不要去動中斷親和性。</summary>
    public string SpreadText => TopSharePercent >= 90
        ? $"集中在 CPU {TopCpu}（{TopSharePercent:0.#}%）"
        : $"分散在 {CpuCount} 顆核，最多的是 CPU {TopCpu}（{TopSharePercent:0.#}%）";
}

/// <summary>
/// 中斷落在哪顆核心的彙整與判讀（純函式）。
/// </summary>
/// <remarks>
/// ETW 的 ISR 事件帶的是<b>中斷服務常式的位址</b>，解析後得到驅動映像名——不是裝置實例。
/// 一支驅動可能服務好幾個裝置（同型號的兩張網卡就是同一支 .sys），所以這裡一律說「驅動」，
/// 不假裝知道是哪一張卡。
/// </remarks>
public static class InterruptAffinityAggregator
{
    /// <summary>單一核心吃下這麼高比例的中斷才算「被打爆」。</summary>
    private const double ConcentratedPercent = 40;

    public static List<CpuInterruptRow> ByCpu(IEnumerable<IsrSample> samples)
    {
        var perCpu = new Dictionary<int, Dictionary<string, long>>();
        long total = 0;
        foreach (var s in samples)
        {
            if (!perCpu.TryGetValue(s.Cpu, out var mods)) perCpu[s.Cpu] = mods = [];
            mods[s.Module] = mods.GetValueOrDefault(s.Module) + 1;
            total++;
        }
        if (total == 0) return [];

        var rows = new List<CpuInterruptRow>();
        foreach (var (cpu, mods) in perCpu)
        {
            long count = mods.Values.Sum();
            var top = mods.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First();
            rows.Add(new CpuInterruptRow
            {
                Cpu = cpu,
                Count = count,
                TopModule = top.Key,
                TopSharePercent = 100.0 * top.Value / count,
                OfAllPercent = 100.0 * count / total,
            });
        }

        rows = [.. rows.OrderByDescending(r => r.Count).ThenBy(r => r.Cpu)];
        long max = rows[0].Count;
        foreach (var r in rows) r.BarFraction = max > 0 ? (double)r.Count / max : 0;
        return rows;
    }

    public static List<ModuleAffinityRow> ByModule(IEnumerable<IsrSample> samples, int top = 20)
    {
        var perModule = new Dictionary<string, Dictionary<int, long>>();
        foreach (var s in samples)
        {
            if (!perModule.TryGetValue(s.Module, out var cpus)) perModule[s.Module] = cpus = [];
            cpus[s.Cpu] = cpus.GetValueOrDefault(s.Cpu) + 1;
        }

        var rows = new List<ModuleAffinityRow>();
        foreach (var (module, cpus) in perModule)
        {
            long count = cpus.Values.Sum();
            var best = cpus.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
            rows.Add(new ModuleAffinityRow
            {
                Module = module,
                Count = count,
                TopCpu = best.Key,
                TopSharePercent = 100.0 * best.Value / count,
                CpuCount = cpus.Count,
            });
        }

        return [.. rows.OrderByDescending(r => r.Count)
                       .ThenBy(r => r.Module, StringComparer.OrdinalIgnoreCase)
                       .Take(Math.Max(top, 0))];
    }

    /// <summary>
    /// 一句判決。只有在真的有一顆核吃下明顯偏高的比例時才點名它，分佈平均就說平均——
    /// 不為了看起來有結論而編一個「有問題的核」。
    /// </summary>
    public static string Verdict(IReadOnlyList<CpuInterruptRow> cpus, IReadOnlyList<ModuleAffinityRow> modules)
    {
        if (cpus.Count == 0) return "這段時間沒有量到中斷事件（系統非常安靜，或 ETW 沒有回報 ISR）。";

        var busiest = cpus[0];
        if (cpus.Count == 1)
            return $"全部中斷都落在 {busiest.Name}，最多來自 {busiest.TopModule}"
                 + $"（佔這顆核的 {busiest.TopSharePercent:0.#}%）。只有一顆核在收中斷，"
                 + "這在單一裝置持續活動時是正常的。";

        if (busiest.OfAllPercent < ConcentratedPercent)
            return $"中斷分佈平均：最忙的 {busiest.Name} 也只佔全機的 {busiest.OfAllPercent:0.#}%，"
                 + $"共 {cpus.Count} 顆核在收中斷。沒有哪一顆核被單獨壓住。";

        return $"{busiest.Name} 被打爆：它吃下全機 {busiest.OfAllPercent:0.#}% 的中斷，"
             + $"其中 {busiest.TopSharePercent:0.#}% 來自 {busiest.TopModule}。"
             + "中斷與它的 DPC 在同一顆核上排隊，那顆核的其他工作會被推遲——"
             + "這是「明明還有很多核閒著卻很卡」的一種成因。";
    }
}
