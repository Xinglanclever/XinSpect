using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 服務就緒時間線（Vol1 第 PO-005 項）：<c>po.svcboot.summary</c>、
/// <c>po.svcboot.trigger</c>——把「開機之後，誰花了多久才站起來」做成時間線。
/// </summary>
/// <remarks>
/// <para>
/// <b>與既有能力的界線：</b><c>boot.duration_ms</c>（WP26）回答的是<b>開機本身多久</b>；
/// <c>sg.boot.summary</c>（SG-001）是導航列，指向「開機為什麼慢」的入口。
/// 本項補的是<b>中間那一層</b>：開機結束之後到各服務開始服務之間的時間差——
/// 使用者感受到的「開完機還要等」通常發生在這裡，而不是在開機階段。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>「已經在跑」與「幾秒才開始跑」是兩件事</b>——服務清單說的是當下狀態，
/// 本項問的是當時花了多久；兩者都不該互相冒充；
/// ② <b>不評級、不裁決慢</b>：某個服務 12 秒才起來，可能是它本來就大、
/// 也可能是磁碟慢、也可能是它依賴的另一個服務慢——<b>時間是事實，原因不是</b>；
/// ③ <b>事件時間是「相對於開機」還是「絕對時間」要分清楚</b>：
/// 事件的 TimeCreated 是絕對時間，服務自己的啟動耗時欄位才是相對量；
/// 兩者混算會得到「開機後 47 年才起來」這種荒謬數字，所以解析時一律明確標示基準；
/// ④ 沒有服務啟動事件時是 <see cref="FactAvailability.NotApplicable"/>
/// （記錄可能被清理、或這台機器沒有觸發型服務），不是 0 秒。
/// </para>
/// </remarks>
public static class ServiceBootFactsService
{
    private const string Cat = "效能觀測";
    private const string Source = "事件記錄 Microsoft-Windows-Service Control Manager／Diagnostics-Performance（唯讀）";

    /// <summary>PO-005 服務就緒時間線摘要。</summary>
    public const string SummaryKey = "po.svcboot.summary";
    /// <summary>PO-005 觸發型服務數。</summary>
    public const string TriggerKey = "po.svcboot.trigger";

    /// <summary>單一服務的啟動量測。<b>只帶識別與時間，不帶服務名</b>（服務名可含組織資訊）。</summary>
    /// <param name="Id">匿名化識別（如「服務 #3」）。</param>
    /// <param name="SecondsFromBoot">相對於開機基準的秒數（明確基準，見服務註解）。</param>
    /// <param name="TriggerStarted">是否為觸發啟動（相對自動啟動）。</param>
    public sealed record ServiceStart(string Id, double SecondsFromBoot, bool TriggerStarted);

    /// <summary>（開機基準時間, 各服務啟動量測）。<paramref name="BootAt"/> 為 null ＝基準取不到。</summary>
    public sealed record BootTimeline(DateTimeOffset? BootAt, IReadOnlyList<ServiceStart> Starts);

    public delegate BootTimeline? Probe();

    // ── 判讀（可單獨測試的純函式）─────────────────────────────────────────

    /// <summary>
    /// 由「開機時間」與「事件的絕對時間」算相對秒數。
    /// <b>負值回 <c>null</c> 而不是 0</b>——事件早於開機時間代表其中一個來源錯了，
    /// 出 0 秒會被讀成「開機瞬間就起來了」。
    /// </summary>
    public static double? SecondsFromBoot(DateTimeOffset bootAt, DateTimeOffset eventAt)
    {
        double s = (eventAt - bootAt).TotalSeconds;
        return s < 0 ? null : s;
    }

    /// <summary>
    /// 收成時間線摘要。回傳（樣本數, 最快, 最慢, 中位數, 觸發型數）。
    /// <b>用中位數不用平均</b>：少數極慢的服務會把平均值拉走，
    /// 而使用者感受到的是「大部分服務幾秒起來」。
    /// </summary>
    public static (int Count, double? Fastest, double? Slowest, double? Median, int Triggers)
        Summarize(IReadOnlyList<ServiceStart> starts)
    {
        if (starts.Count == 0) return (0, null, null, null, 0);

        var sorted = starts.Select(s => s.SecondsFromBoot).OrderBy(x => x).ToList();
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        return (starts.Count, sorted[0], sorted[^1], median, starts.Count(s => s.TriggerStarted));
    }

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, Probe? probe = null)
    {
        BootTimeline? t;
        try { t = (probe ?? ReadTimeline)(); }
        catch { t = null; }

        if (t is null)
            return
            [
                Unavailable(at, SummaryKey, "服務就緒時間線", FactAvailability.ReadError,
                    "服務啟動事件查詢失敗——讀不到就是不猜（不畫成 0 個服務）"),
                Unavailable(at, TriggerKey, "觸發型服務數", FactAvailability.ReadError,
                    "服務啟動事件查詢失敗——讀不到就是不猜"),
            ];

        var v = t;
        var (count, fastest, slowest, median, triggers) = Summarize(v.Starts);

        if (count == 0)
            return
            [
                new HardwareFact(SummaryKey, Cat, "服務就緒時間線",
                    "沒有服務啟動量測——<b>不適用</b>，不是「0 秒就緒」。" +
                    "可能原因：服務啟動事件記錄被清理、這台機器沒有觸發型服務、" +
                    "或事件通道未啟用。沒有樣本就沒有時間線可談。",
                    "個", Source, FactTrustLevel.Reported, false, at, 0),
                new HardwareFact(TriggerKey, Cat, "觸發型服務數",
                    "0 個（前提是沒有樣本可比——不是「沒有觸發型服務」）。",
                    "個", Source, FactTrustLevel.Reported, false, at, 0),
            ];

        string basis = v.BootAt is { } b
            ? $"時間基準：{b:yyyy-MM-dd HH:mm:ss} UTC（開機時間）"
            : "<b>時間基準取不到</b>——相對秒數不可靠，請看事件的絕對時間";

        return
        [
            new HardwareFact(SummaryKey, Cat, "服務就緒時間線",
                $"量測 {count} 個服務・最快 {fastest:0.0} 秒、中位 {median:0.0} 秒、最慢 {slowest:0.0} 秒" +
                $"（皆自開機起算）・{basis}。" +
                "<b>「已經在跑」與「幾秒才開始跑」是兩件事</b>：服務清單給的是當下狀態，" +
                "本項給的是當時花了多久——兩者都不該互相冒充。" +
                "<b>不評級、不裁決慢</b>：某個服務 12 秒才起來，可能是它本來就大、" +
                "可能是磁碟慢、也可能是它依賴的另一個服務慢——<b>時間是事實，原因不是</b>。" +
                "<b>用中位數不用平均</b>：少數極慢的服務會把平均值拉走，" +
                "而使用者感受到的是「大部分服務幾秒起來」。" +
                "不列服務名稱（可含組織資訊），逐項明細以匿名序號表示。",
                "秒", Source, FactTrustLevel.Reported, false, at, median),

            new HardwareFact(TriggerKey, Cat, "觸發型服務數",
                $"{triggers} 個（共 {count} 個量測中）。" +
                "<b>觸發型與自動型起跑點不同</b>：自動型服務在開機階段就被啟動，" +
                "觸發型則等到有事件（裝置到來、連線請求）才起身——" +
                "把兩者的秒數直接比大小，比的是蘋果和橘子。" +
                "本項把它們分開計數，但不宣稱哪一種比較好：" +
                "觸發型是<b>刻意的</b>設計（少佔開機時間），慢不是它的缺陷。",
                "個", Source, FactTrustLevel.Reported, false, at, triggers),
        ];
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 真實通路：<b>本版刻意不實作</b>——它需要同時讀兩個事件通道
    /// （Service Control Manager 的啟動事件與開機時間基準）並做時間對齊，
    /// 而對齊的基準選擇會直接影響數字（見紅線③）。
    /// 回 <c>null</c> 誠實標示；判讀邏輯（<see cref="SecondsFromBoot"/>／
    /// <see cref="Summarize"/>）已完整實作並被測試釘住。
    /// </summary>
    internal static BootTimeline? ReadTimeline() => null;

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", Source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
