namespace XinSpect;

/// <summary>
/// 延遲直方圖的一格：<c>[FromUs, ToUs)</c> 這段區間裡有幾筆。
/// <para>
/// <paramref name="Total"/> 是整份樣本數、<paramref name="Max"/> 是最多的那一格——版面上的
/// 「佔比」對的是總數，「長條長度」對的是最大格（不然全部長條都短到看不出形狀）。
/// </para>
/// </summary>
public readonly record struct LatencyBucket(double FromUs, double ToUs, int Count, int Total, int Max)
{
    /// <summary>這一格佔全部樣本的百分比。</summary>
    public double SharePercent => Total > 0 ? 100.0 * Count / Total : 0;

    /// <summary>長條長度（0–100）：以最多的那一格為滿格。</summary>
    public double BarPercent => Max > 0 ? 100.0 * Count / Max : 0;

    /// <summary>版面上的佔比標示。</summary>
    public string ShareText => Total > 0 ? $"{SharePercent:0.0}%" : "—";

    /// <summary>版面上的區間標示。微秒與毫秒分開寫，不要讓人自己換算。</summary>
    public string RangeText =>
        FromUs <= 0 ? $"< {Edge(ToUs)}"
        : double.IsPositiveInfinity(ToUs) ? $"≥ {Edge(FromUs)}"
        : ToUs <= 1000 ? $"{FromUs:N0}–{ToUs:N0} µs"
        : $"{FromUs / 1000:0.##}–{ToUs / 1000:0.##} ms";

    private static string Edge(double us) => us < 1000 ? $"{us:N0} µs" : $"{us / 1000:0.##} ms";
}

/// <summary>
/// 一次隨機 4K 測試的延遲分佈：逐筆延遲的百分位與直方圖。
/// <para>
/// 隨機測試是 QD1 逐次同步 I/O——<b>每一筆的延遲本來就量到了</b>，過去被平均成 IOPS 之後丟掉。
/// 這裡把逐筆的數字留下來：全部樣本都留，百分位是排序後取最近排名的<b>那一筆真實樣本</b>，
/// 不內插、不抽樣、不套任何分佈模型。平均值看不出的停頓，尾端百分位看得出來。
/// </para>
/// <para>
/// 每一筆都含兩次計時器讀取的成本，<b>沒有扣掉</b>——扣了就變成估的。對照量級：NVMe 的
/// 4K 隨機讀取約數十微秒，而一次計時器讀取是數十奈秒。
/// </para>
/// </summary>
public sealed class DiskLatencyStats
{
    /// <summary>直方圖的區間邊界（微秒）；1-2-5 階梯，一路涵蓋 NVMe 到機械硬碟的範圍。</summary>
    private static readonly double[] Edges =
    [
        0, 10, 20, 50, 100, 200, 500, 1_000, 2_000, 5_000,
        10_000, 20_000, 50_000, 100_000, double.PositiveInfinity,
    ];

    /// <summary>樣本數＝這段時間預算裡真的做完的 I/O 筆數。</summary>
    public int Count { get; init; }

    public double MinUs { get; init; }
    public double P50Us { get; init; }
    public double P99Us { get; init; }
    public double P999Us { get; init; }
    public double MaxUs { get; init; }

    /// <summary>直方圖；頭尾的空區間已砍掉，中間的空區間留著。</summary>
    public IReadOnlyList<LatencyBucket> Buckets { get; init; } = [];

    public bool HasData => Count > 0;
    public string P50Text => Text(P50Us);
    public string P99Text => Text(P99Us);
    public string P999Text => Text(P999Us);
    public string MaxText => Text(MaxUs);

    /// <summary>一句話交代這次量到什麼。沒有樣本就直說沒有，不填 0 上去。</summary>
    public string SummaryText => Count == 0
        ? "沒有樣本"
        : $"{Count:N0} 筆 ・ 中位數 {P50Text} ・ p99 {P99Text} ・ p99.9 {P999Text} ・ 最慢 {MaxText}";

    private string Text(double us) =>
        Count == 0 ? "—" : us < 1000 ? $"{us:N1} µs" : $"{us / 1000:N2} ms";

    /// <summary>把逐筆的計時器刻數換算成微秒並算出分佈。空清單就回一個「沒有樣本」的實例。</summary>
    public static DiskLatencyStats FromTicks(IReadOnlyList<long> ticks, long ticksPerSecond)
    {
        if (ticks.Count == 0 || ticksPerSecond <= 0) return new DiskLatencyStats();

        double scale = 1_000_000.0 / ticksPerSecond;
        var us = new double[ticks.Count];
        for (int i = 0; i < us.Length; i++) us[i] = Math.Max(0, ticks[i]) * scale;
        Array.Sort(us);

        return new DiskLatencyStats
        {
            Count = us.Length,
            MinUs = us[0],
            P50Us = Rank(us, 0.5),
            P99Us = Rank(us, 0.99),
            P999Us = Rank(us, 0.999),
            MaxUs = us[^1],
            Buckets = Histogram(us),
        };
    }

    /// <summary>最近排名法：第 ⌈p×N⌉ 筆。回的是真的量到過的那一筆，不是內插出來的數字。</summary>
    private static double Rank(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

    /// <summary>
    /// 逐格計數，只砍掉頭尾的空格——中間的空格要留著，快慢兩群之間的那段空白本身就是要看的東西。
    /// </summary>
    private static List<LatencyBucket> Histogram(double[] sorted)
    {
        var counts = new int[Edges.Length - 1];
        int b = 0;
        foreach (double v in sorted)   // 已排序，格號只會往前走
        {
            while (b < counts.Length - 1 && v >= Edges[b + 1]) b++;
            counts[b]++;
        }

        int first = Array.FindIndex(counts, c => c > 0);
        int last = Array.FindLastIndex(counts, c => c > 0);
        int max = counts.Max();
        var list = new List<LatencyBucket>(last - first + 1);
        for (int i = first; i <= last; i++)
            list.Add(new LatencyBucket(Edges[i], Edges[i + 1], counts[i], sorted.Length, max));
        return list;
    }
}
