using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 更新失敗重試鏈（Vol2 第 UF-002 項）：<c>uf.retry.summary</c>、
/// <c>uf.retry.count</c>——同一個 KB 被<b>反覆嘗試</b>的痕跡。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b><c>wu.history.failed</c> 數的是<b>失敗的更新有幾筆</b>——
/// 但那把鍵看不出「同一個 KB 失敗了七次」與「七個不同 KB 各失敗一次」的差別。
/// 前者是<b>卡住</b>（重試鏈），後者是<b>一次性的問題</b>；處置方式完全不同。
/// 本項補的就是這個<b>聚合視角</b>。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>重試不等於失敗</b>——Windows Update 本來就會重試，重試一次成功是正常流程，
/// 只有<b>反覆失敗</b>才值得注意，所以鏈的判準是「失敗次數」不是「嘗試次數」；
/// ② <b>匯總用 KB 編號不用標題</b>：更新標題常含產品名與語言（「適用於 Windows 的安全性更新」），
/// KB 編號才是穩定的識別；但若標題裡沒有 KB 就<b>不硬湊</b>，歸進「無編號」桶並如實計數；
/// ③ 不斷言重試的<b>原因</b>——重試鏈只證明「這件事沒做完」，不證明為什麼。
/// </para>
/// </remarks>
public static class UpdateRetryFactsService
{
    private const string Cat = "系統與軟體";
    private const string Source = "WUA 更新歷史聚合（唯讀）";

    /// <summary>UF-002 重試鏈摘要。</summary>
    public const string SummaryKey = "uf.retry.summary";
    /// <summary>UF-002 卡住的更新數。</summary>
    public const string CountKey = "uf.retry.count";

    /// <summary>判定「反覆失敗」的門檻：同一個識別失敗達這個次數才算卡住。</summary>
    public const int StuckThreshold = 3;

    /// <summary>一條重試鏈：識別（KB 編號或空字串）、失敗次數、嘗試總次數。</summary>
    public sealed record RetryChain(string Id, int Failures, int Attempts);

    // ── 判讀（可單獨測試的純函式）─────────────────────────────────────────

    /// <summary>
    /// 從更新歷史抽出重試鏈。<paramref name="rows"/> 是（識別, 結果碼）序列，
    /// 結果碼 4＝失敗、5＝中止（與 <see cref="WuHistoryEntry"/> 同一組對照）。
    /// </summary>
    public static IReadOnlyList<RetryChain> BuildChains(
        IReadOnlyList<(string Id, uint ResultCode)> rows)
    {
        return rows
            .GroupBy(r => NormalizeId(r.Id), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RetryChain(
                g.Key,
                g.Count(r => r.ResultCode is 4 or 5),
                g.Count()))
            .ToList();
    }

    /// <summary>
    /// 從更新標題抽 KB 編號（<c>KB5034123</c> 這種形式，大小寫不拘）。
    /// 抽不到就回空字串——<b>不猜、不用標題當識別</b>。
    /// </summary>
    public static string ExtractKb(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var m = System.Text.RegularExpressions.Regex.Match(
            title, @"\bKB(\d{6,8})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? "KB" + m.Groups[1].Value : "";
    }

    private static string NormalizeId(string id) => string.IsNullOrWhiteSpace(id) ? "" : id.Trim();

    /// <summary>卡住的鏈＝失敗次數達門檻者。</summary>
    public static IReadOnlyList<RetryChain> Stuck(IReadOnlyList<RetryChain> chains)
        => chains.Where(c => c.Failures >= StuckThreshold).ToList();

    // ── 收集 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 收集重試鏈事實。<paramref name="entries"/> 為 null ＝歷史讀不到（ReadError）。
    /// </summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyList<WuHistoryEntry>? entries)
    {
        if (entries is null)
            return
            [
                Unavailable(at, SummaryKey, "更新失敗重試鏈", FactAvailability.ReadError,
                    "更新歷史讀不到——讀不到就是不猜（不把讀不到當成沒有失敗）"),
                Unavailable(at, CountKey, "卡住的更新數", FactAvailability.ReadError,
                    "更新歷史讀不到——讀不到就是不猜"),
            ];

        var rows = entries.Select(e => (ExtractKb(e.Title), e.ResultCode)).ToList();
        var chains = BuildChains(rows);
        var stuck = Stuck(chains);

        // 沒有任何失敗 → 沒有鏈可言。0 是值：這台機器的更新沒有反覆失敗。
        if (chains.All(c => c.Failures == 0))
            return
            [
                new HardwareFact(SummaryKey, Cat, "更新失敗重試鏈",
                    $"更新歷史 {entries.Count} 筆，沒有任何失敗或中止的記錄——因此沒有重試鏈可談。" +
                    "「沒有失敗」是這把鍵最好的狀態，不是缺席；本項只陳述痕跡，不背書更新是否裝齊。",
                    "條", Source, FactTrustLevel.Derived, false, at, 0),
                new HardwareFact(CountKey, Cat, "卡住的更新數", "0 個（沒有失敗記錄）。",
                    "個", Source, FactTrustLevel.Derived, false, at, 0),
            ];

        string stuckText = stuck.Count == 0
            ? "沒有達到卡住門檻（同一個識別失敗 " + StuckThreshold + " 次）的更新"
            : $"卡住 {stuck.Count} 個：" + string.Join("、", stuck
                .OrderByDescending(s => s.Failures)
                .Select(s => $"{(s.Id.Length == 0 ? "無 KB 編號" : s.Id)}（失敗 {s.Failures}／嘗試 {s.Attempts} 次）"));

        int totalFailures = chains.Sum(c => c.Failures);
        string topText = string.Join("；", chains
            .Where(c => c.Failures > 0)
            .OrderByDescending(c => c.Failures)
            .Take(5)
            .Select(c => $"{(c.Id.Length == 0 ? "無編號" : c.Id)} 失敗 {c.Failures} 次"));

        return
        [
            new HardwareFact(SummaryKey, Cat, "更新失敗重試鏈",
                $"更新歷史 {entries.Count} 筆・失敗或中止共 {totalFailures} 次・{stuckText}。" +
                (topText.Length > 0 ? $"失敗分布：{topText}。" : "") +
                "<b>重試不等於失敗</b>——Windows Update 本來就會重試，重試一次成功是正常流程，" +
                "所以門檻看的是<b>失敗次數</b>不是嘗試次數；只有反覆失敗才叫卡住。" +
                "<b>匯總用 KB 編號不用標題</b>：標題常含產品名與語言，KB 編號才是穩定識別；" +
                "抽不到編號的歸進「無編號」桶並如實計數，不硬湊。" +
                "<b>本項不斷言原因</b>：重試鏈只證明「這件事沒做完」，不證明為什麼沒做完。",
                "條", Source, FactTrustLevel.Derived, false, at, totalFailures),

            new HardwareFact(CountKey, Cat, "卡住的更新數",
                $"{stuck.Count} 個。判準：同一個 KB 編號（或同一組無編號記錄）" +
                $"失敗達 {StuckThreshold} 次。低於門檻的失敗不算卡住——那只證明試過一次沒成。",
                "個", Source, FactTrustLevel.Derived, false, at, stuck.Count),
        ];
    }

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
