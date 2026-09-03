namespace XinSpect;

/// <summary>遷移分層表的一列。</summary>
public sealed class MigrationHopRow
{
    public required MigrationHop Hop { get; init; }
    public required long Count { get; init; }
    public required double Share { get; init; }      // 0–1，佔全部遷移的比例
    public required double PerSecond { get; init; }

    public string Name => CpuLayout.HopName(Hop);
    public string Cost => CpuLayout.HopCost(Hop);
    public string CountText => $"{Count:N0}";
    public string ShareText => $"{Share * 100:0.#} %";
    public string RateText => $"{PerSecond:N0} / 秒";
}

/// <summary>某個行程的遷移狀況。</summary>
public sealed class MigrationProcessRow
{
    public required string Name { get; init; }
    public required int Pid { get; init; }
    public required long Switches { get; init; }
    public required long Migrations { get; init; }
    public required long CrossLlc { get; init; }

    public string PidText => Pid > 0 ? $"{Pid}" : "—";
    public string SwitchText => $"{Switches:N0}";
    public string MigrationText => $"{Migrations:N0}";
    public string CrossLlcText => $"{CrossLlc:N0}";
    /// <summary>遷移率＝遷移 ÷ 切入次數。這比絕對次數可比，因為忙的行程本來就切得多。</summary>
    public string RatioText => Switches > 0 ? $"{(double)Migrations / Switches * 100:0.#} %" : "—";
}

/// <summary>某顆邏輯處理器被切入的次數。</summary>
public sealed class CpuSwitchRow
{
    public required int Cpu { get; init; }
    public required long Switches { get; init; }
    public required double Share { get; init; }

    public string CpuText => $"CPU {Cpu}";
    public string SwitchText => $"{Switches:N0}";
    public string ShareText => $"{Share * 100:0.#} %";
}

/// <summary>
/// 執行緒遷移的彙整與判讀（純函式，不碰 ETW 也不碰硬體，故可完整測試）。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意不下判決。</b>「遷移多」本身不是缺陷：I/O 密集的工作每次喚醒都可能落在別的核上，
/// 那是排程器在做它該做的事（把工作放到現在有空的核上），不是問題。所以這裡只給分層比例與
/// 每秒次數，不給「你的排程有問題」這種結論——真正值得注意的是<b>跨末級快取與跨 NUMA 的比例</b>，
/// 那兩層才會讓已經拉進快取的資料整批作廢。
/// </para>
/// <para>
/// 另一個刻意的選擇：行程排名用<b>遷移率</b>（遷移 ÷ 切入次數）而不是絕對次數。
/// 絕對次數只會把最忙的行程排在最前面，那是同義反覆，看不出誰特別會彈跳。
/// </para>
/// </remarks>
public static class MigrationAggregator
{
    /// <summary>把 (來源 CPU, 目的 CPU) → 次數 依拓樸分層。</summary>
    public static List<MigrationHopRow> ByHop(
        IReadOnlyDictionary<(int From, int To), long> pairs, CpuLayout layout, double seconds)
    {
        var sums = new Dictionary<MigrationHop, long>();
        foreach (var ((from, to), n) in pairs)
        {
            var hop = layout.Classify(from, to);
            if (hop == MigrationHop.Same) continue;      // 沒換核就不是遷移
            sums[hop] = sums.GetValueOrDefault(hop) + n;
        }

        long total = sums.Values.Sum();
        double secs = seconds > 0 ? seconds : 1;
        return sums.OrderBy(kv => kv.Key)
            .Select(kv => new MigrationHopRow
            {
                Hop = kv.Key,
                Count = kv.Value,
                Share = total > 0 ? (double)kv.Value / total : 0,
                PerSecond = kv.Value / secs,
            })
            .ToList();
    }

    /// <summary>遷移總數（不含「沒換核」）。</summary>
    public static long TotalMigrations(IReadOnlyDictionary<(int From, int To), long> pairs, CpuLayout layout)
        => pairs.Where(kv => layout.Classify(kv.Key.From, kv.Key.To) != MigrationHop.Same).Sum(kv => kv.Value);

    /// <summary>逐顆邏輯處理器的切入次數，由多到少。</summary>
    public static List<CpuSwitchRow> ByCpu(IReadOnlyDictionary<int, long> switchesByCpu)
    {
        long total = switchesByCpu.Values.Sum();
        return switchesByCpu.OrderByDescending(kv => kv.Value)
            .Select(kv => new CpuSwitchRow
            {
                Cpu = kv.Key,
                Switches = kv.Value,
                Share = total > 0 ? (double)kv.Value / total : 0,
            })
            .ToList();
    }

    /// <summary>
    /// 行程排名。依<b>遷移率</b>排序而不是絕對次數；切入次數太少的行程排除，
    /// 否則一個只切了 3 次而其中 2 次換核的背景服務會以 67% 佔住第一名。
    /// </summary>
    public static List<MigrationProcessRow> TopProcesses(
        IEnumerable<MigrationProcessRow> rows, int take = 12, long minSwitches = 200)
        => rows.Where(r => r.Switches >= minSwitches)
               .OrderByDescending(r => (double)r.Migrations / r.Switches)
               .ThenByDescending(r => r.Migrations)
               .Take(take)
               .ToList();

    /// <summary>
    /// 一句話總結。刻意<b>只陳述</b>：說出每秒切換與遷移的量、以及跨末級快取與跨 NUMA 的佔比，
    /// 不說「你的排程有問題」——遷移多寡取決於工作型態，那不是本頁判斷得了的事。
    /// </summary>
    public static string Verdict(long switches, long migrations, double seconds,
                                 IReadOnlyList<MigrationHopRow> hops, CpuLayout layout)
    {
        if (switches == 0)
            return "沒有收到任何上下文切換事件。ETW 的核心追蹤需要系統管理員權限；"
                 + "權限不足時整趟會是空的，那不代表這台機器沒有在切換執行緒。";

        double secs = seconds > 0 ? seconds : 1;
        double ratio = (double)migrations / switches;
        var cross = hops.FirstOrDefault(h => h.Hop == MigrationHop.CrossLlc);
        var numa = hops.FirstOrDefault(h => h.Hop == MigrationHop.CrossNuma);

        var sb = new System.Text.StringBuilder();
        sb.Append($"{secs:0} 秒內收到 {switches:N0} 次上下文切換（每秒 {switches / secs:N0}），");
        sb.Append($"其中 {migrations:N0} 次換了核心（{ratio * 100:0.#} %）。");

        if (cross is not null)
            sb.Append($" 跨末級快取 {cross.Share * 100:0.#} %——那一層連 L3 都要重新拉。");
        else if (layout.LlcCount <= 1)
            sb.Append(" 本機的末級快取只有一片（全部核心共用），所以不會有跨末級快取的遷移"
                    + "——「同末級快取內換核」已經是這台機器上最貴的一層。");

        if (numa is not null && numa.Count > 0)
            sb.Append($" 跨 NUMA {numa.Share * 100:0.#} %，記憶體變成遠端存取。");
        else if (layout.NumaCount <= 1)
            sb.Append(" 本機只有一個 NUMA 節點，所以不會有跨節點的遷移。");

        sb.Append(" 遷移多寡取決於工作型態：I/O 密集的工作每次喚醒都可能落在別的核上，"
                + "那是排程器把工作放到有空的核上，不是缺陷。本頁只給分層數字，不下判決。");
        return sb.ToString();
    }
}
