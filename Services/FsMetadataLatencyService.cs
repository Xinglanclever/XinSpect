using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>單一 metadata 操作的一次取樣：操作名、逐筆計時器刻數。</summary>
public sealed record FsLatencySample(string Operation, IReadOnlyList<long> Ticks);

/// <summary>
/// 檔案系統 metadata 微延遲（Vol 1 ST-010）：<c>create</c>／<c>stat</c>／<c>delete</c> 三種
/// <b>中繼資料</b>操作的 p50／p99 延遲。
/// </summary>
/// <remarks>
/// <para>
/// <b>量的是中繼資料，不是資料。</b>create 建一個空檔案、stat 問一個檔案的屬性、delete 刪掉它——
/// 三者都不搬資料。這正是它們的價值：使用者抱怨「這個資料夾開起來很慢」時，慢的通常是這一層，
/// 而不是循序讀寫頻寬（那是 ST-003 那類在量的）。兩者是不同的問題，數字不可互相推論。
/// </para>
/// <para>
/// <b>誠實界線一：create 與 delete 需要寫入，因此整組在同意閘門之後。</b>未注入探針＝不執行＝
/// <see cref="FactAvailability.NotApplicable"/>，<b>不是 0 µs</b>。0 代表「量到瞬間完成」，
/// 與「沒量」是完全不同的兩件事——這個區別在本專案是紅線（同 <c>ms.ping</c>／<c>tu.jitter</c>）。
/// 唯讀的 stat 也一併不跑：單獨跑一項會給出一個不對稱、看不懂的數字。
/// </para>
/// <para>
/// <b>誠實界線二：量到的數字含本程式自己的計時成本，且不扣掉。</b>扣了就變成估的。
/// 對照量級寫在事實值裡：一次 <c>QueryPerformanceCounter</c> 是數十奈秒，而 metadata 操作是
/// 微秒到毫秒級——兩個量級差得夠遠，所以不扣是誠實的；<b>若哪天操作縮到同一量級，這個決定要重評</b>。
/// </para>
/// <para>
/// <b>誠實界線三：這是冷／熱混在一起的樣本，不宣稱是哪一種。</b>第一次碰到的目錄、防毒即時掃描、
/// 快取命中與否都會改變結果。所以值裡<b>報樣本數與觀測窗</b>，並明說這不是可以跨機器比大小的基準；
/// p99 只在樣本足夠時才有意義（樣本太少時尾端就是雜訊）。
/// </para>
/// <para>
/// <b>誠實界線四：不指定路徑。</b>量測發生在探針指定的位置（本機是程式自己的暫存資料夾），
/// 事實值裡不報路徑——路徑可能含使用者名稱或專案代號，而這幾個數字不需要路徑才讀得懂。
/// </para>
/// </remarks>
public static class FsMetadataLatencyService
{
    public const string Category = "儲存";

    /// <summary>三種操作合起來的摘要（樣本數、p50／p99）。</summary>
    public const string SummaryKey = "ms.fslat.summary";
    /// <summary>逐操作的明細。</summary>
    public const string DetailKey = "ms.fslat.detail";

    /// <summary>要量哪三種操作。<b>順序固定</b>，報表不因執行順序而變。</summary>
    public static readonly string[] Operations = ["create", "stat", "delete"];

    /// <summary>低於這個樣本數時 p99 只是雜訊，值裡如實降級說明。</summary>
    public const int MinSamplesForP99 = 50;

    /// <summary>單一操作的樣本上限（越多越準，但這是微延遲微測，不是壓力測試）。</summary>
    public const int MaxSamplesPerOperation = 2000;

    /// <summary>
    /// 由一組取樣算出事實。<paramref name="samples"/> 為 <c>null</c>＝未注入探針（沒量）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<FsLatencySample>? samples = null, long ticksPerSecond = 0)
    {
        if (samples is null || ticksPerSecond <= 0)
            return [Unavailable(at, SummaryKey, "檔案系統 metadata 微延遲 摘要", NotMeasuredReason),
                    Unavailable(at, DetailKey, "檔案系統 metadata 微延遲 逐操作", NotMeasuredReason)];

        // 逐操作算分佈；同一個操作出現多次就合併（探針可能分好幾批注入）
        var rows = new List<(string Op, DiskLatencyStats Stats)>();
        foreach (string op in Operations)
        {
            var ticks = samples.Where(s => s.Operation == op)
                .SelectMany(s => s.Ticks).ToList();
            if (ticks.Count == 0) continue;
            rows.Add((op, DiskLatencyStats.FromTicks(ticks, ticksPerSecond)));
        }

        if (rows.Count == 0)
            return [Unavailable(at, SummaryKey, "檔案系統 metadata 微延遲 摘要",
                        "取樣清單是空的——沒有量到任何一筆，不以 0 µs 冒充"),
                    Unavailable(at, DetailKey, "檔案系統 metadata 微延遲 逐操作",
                        "取樣清單是空的——沒有量到任何一筆")];

        int total = rows.Sum(r => r.Stats.Count);
        double worstP99 = rows.Max(r => r.Stats.P99Us);
        string worstOp = rows.First(r => r.Stats.P99Us == worstP99).Op;

        string summary = $"共 {total} 筆（{string.Join("、", rows.Select(r => $"{r.Op} {r.Stats.Count}"))}）；"
            + $"最高的 p99 在 {worstOp}：{Us(worstP99)}。"
            + "<b>量的是中繼資料不是資料</b>——create／stat／delete 都不搬檔案內容，"
            + "所以這組數字與循序讀寫頻寬是兩個不同的問題，不可互相推論。"
            + "<b>這是冷熱混合的樣本</b>：第一次碰到的目錄、防毒即時掃描、快取命中與否都會改變結果，"
            + "因此不是可以跨機器比大小的基準。"
            + $"<b>量到的數字含計時成本且不扣</b>——一次 QueryPerformanceCounter 是數十奈秒，"
            + "metadata 操作是微秒到毫秒級，差得夠遠所以不扣是誠實的；若哪天操作縮到同一量級，這個決定要重評。";

        var detail = new List<string>();
        foreach (var (op, stats) in rows)
        {
            string tail = stats.Count < MinSamplesForP99
                ? $"<b>樣本只有 {stats.Count} 筆，p99 在此只是雜訊</b>，只作方向參考"
                : $"p99 {Us(stats.P99Us)}";
            detail.Add($"{op}：{stats.Count} 筆，p50 {Us(stats.P50Us)}、{tail}、最慢 {Us(stats.MaxUs)}");
        }

        var facts = new List<HardwareFact>
        {
            new(SummaryKey, Category, "檔案系統 metadata 微延遲 摘要", summary, "µs",
                "本機暫存資料夾上的 metadata 微測（同意閘門後才執行；不報路徑）",
                FactTrustLevel.Measured, false, at, worstP99),
            new(DetailKey, Category, "檔案系統 metadata 微延遲 逐操作",
                string.Join("；", detail) + "。<b>不指定路徑</b>：量測位置是探針指定的暫存資料夾，"
                + "路徑可能含使用者名稱或專案代號，而這幾個數字不需要路徑才讀得懂。",
                "µs", "本機暫存資料夾上的 metadata 微測（唯讀彙總，不揭露路徑）",
                FactTrustLevel.Measured, false, at, total),
        };
        return facts;
    }

    /// <summary>未量測的共同理由。<b>抽成具名常數</b>，兩把鍵與測試看到的是同一句話。</summary>
    public const string NotMeasuredReason =
        "同意閘門未同意（create／delete 需要寫入）——未執行不等於 0 µs；"
        + "0 代表「量到瞬間完成」，與「沒量」是完全不同的兩件事";

    /// <summary>微秒的可讀寫法：低於 1 ms 用 µs，否則換算成 ms。</summary>
    public static string Us(double us) => us < 1000 ? $"{us:N1} µs" : $"{us / 1000:N2} ms";

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name, string reason)
        => new(key, Category, name, "", "", "檔案系統 metadata 微測（opt-in；不報路徑）",
            FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable, reason);
}
