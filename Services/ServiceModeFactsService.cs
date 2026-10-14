using System;
using System.Collections.Generic;
using System.Linq;

namespace XinSpect;

/// <summary>
/// 背景服務模式（Vol2 第 AG-005 項）：<c>ag.service</c>——把「本工具能不能當背景常駐服務跑」
/// 從<b>能力與邊界</b>的角度回答。
/// </summary>
/// <remarks>
/// <para>
/// <b>這一項刻意不建立服務，只陳述「如果要，缺什麼」。</b>目錄標「產品方向需討論」，
/// 這句話是對的：把一個會寫入的硬體工具變成常駐服務，牽涉的是
/// <b>誰在什麼權限下、做多久、留什麼痕跡</b>——不是加一個 <c>while(true)</c>。
/// 本項把那些條件列成可稽核的事實，讓討論有起點；
/// <b>不建立服務、不註冊排程、不寫入任何東西</b>。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① <b>「可以做」與「正在做」是兩件事</b>——本項回答的是前者，後者看實際的
/// 服務／排程註冊狀態（那是既有事實）；
/// ② <b>常駐的成本要說出來</b>：輪詢頻率 × 事實數量 = 每秒的查詢量，
/// 而其中很多事實是「開機以來不變」的（序號、CPU 型號）——常駐第一個要解的就是
/// 「哪些該重讀、哪些該快取」，不然就是拿 CPU 換不變的答案；
/// ③ <b>寫入型能力在服務模式下風險放大</b>：互動模式下使用者看著每次寫入，
/// 服務模式下沒有這個在場。所以本項把「有多少寫入型能力」列出來當前提，
/// 而不是當細節；
/// ④ <b>不宣稱任何授權或合規結論</b>——以服務身分執行、以排程執行、
/// 以互動身分執行是三種不同的權限輪廓，本項只陳述差別不判哪個對。
/// </para>
/// </remarks>
public static class ServiceModeFactsService
{
    private const string Cat = "自動化與引導";
    private const string Source = "本工具自身能力盤點（唯讀；不建立服務、不註冊排程）";

    /// <summary>AG-005 背景服務模式。</summary>
    public const string ServiceKey = "ag.service";

    /// <summary>
    /// 服務模式的前提盤點。
    /// </summary>
    /// <param name="FactCount">本輪事實總數（決定輪詢成本）。</param>
    /// <param name="VolatileFacts">其中會變動者（其餘可快取）。</param>
    /// <param name="WriteCapableActions">需要寫入的能力數（服務模式下風險放大）。</param>
    /// <param name="HeadlessAvailable">有沒有不經 GUI 的輸出路徑（CLI）。</param>
    /// <param name="ConsentGateCount">有同意閘門保護的寫入型能力數。</param>
    public sealed record ServiceModeInputs(
        int FactCount, int VolatileFacts, int WriteCapableActions,
        bool HeadlessAvailable, int ConsentGateCount);

    public delegate ServiceModeInputs? Probe();

    // ── 判讀（可單獨測試的純函式）─────────────────────────────────────────

    /// <summary>
    /// 每秒查詢量的量級估算。<b>回的是量級不是精確值</b>——
    /// 目的是讓「常駐要花多少」變成可以討論的數字，不是宣稱效能。
    /// </summary>
    /// <param name="seconds">輪詢間隔（秒）。</param>
    /// <param name="facts">每輪要讀的事實數。</param>
    public static double ReadsPerSecond(int seconds, int facts)
        => seconds <= 0 ? 0 : (double)facts / seconds;

    /// <summary>可快取的比率（開機以來不變的事實佔比）。</summary>
    public static double CacheableRatio(int facts, int volatileFacts)
        => facts <= 0 ? 0 : Math.Clamp((facts - volatileFacts) / (double)facts, 0, 1);

    // ── 事實 ──────────────────────────────────────────────────────────────

    public static HardwareFact Collect(DateTimeOffset at, Probe? probe = null)
    {
        ServiceModeInputs? s;
        try { s = (probe ?? ReadInputs)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, FactAvailability.ReadError,
                "能力盤點失敗——讀不到就是不猜（不宣稱可以或不可以當服務跑）");

        var v = s;
        double perSecond = ReadsPerSecond(1, v.FactCount);          // 1 秒輪詢＝最壞情況
        double perSecond10 = ReadsPerSecond(10, v.FactCount);       // 10 秒＝合理起點
        double cacheable = CacheableRatio(v.FactCount, v.VolatileFacts);
        int ungated = Math.Max(0, v.WriteCapableActions - v.ConsentGateCount);

        string headless = v.HeadlessAvailable
            ? "有不經 GUI 的輸出路徑（CLI）——服務模式至少有一個可用的出口"
            : "<b>沒有不經 GUI 的輸出路徑</b>——服務模式下讀不到東西等於沒跑，" +
              "這是接服務之前必須先補的一塊";

        string writeText = v.WriteCapableActions == 0
            ? "沒有寫入型能力"
            : $"{v.WriteCapableActions} 項寫入型能力（其中 {v.ConsentGateCount} 項有同意閘門"
              + (ungated > 0
                  ? $"，<b>{ungated} 項沒有</b>——這些在服務模式下會被放大：互動模式有人看著，服務模式沒有"
                  : "，全部都有閘門保護")
              + "）";

        return new HardwareFact(ServiceKey, Cat, "背景服務模式",
            $"本輪事實 {v.FactCount} 項（其中約 {cacheable:P0} 可快取，因為開機以來不變）・" +
            $"{writeText}・{headless}。" +
            $"常駐成本量級：1 秒輪詢約 {perSecond:N0} 次查詢／秒、" +
            $"10 秒輪詢約 {perSecond10:N0} 次／秒。" +
            "<b>本項不建立服務、不註冊排程、不寫入任何東西</b>——它回答的是「如果要，缺什麼」，" +
            "不是「已經有了」。<b>「可以做」與「正在做」是兩件事</b>，" +
            "後者請看既有的服務／排程註冊事實。" +
            "常駐第一個要解的問題是<b>哪些事實該重讀、哪些該快取</b>：" +
            "序號、CPU 型號這類開機以來不變的東西每輪重讀，就是拿 CPU 換同一個答案；" +
            "而寫入型能力在服務模式下風險放大——<b>互動模式有人看著，服務模式沒有</b>。" +
            "以服務身分、排程身分或互動身分執行是三種不同的權限輪廓，" +
            "本項只陳述差別，不判哪個對（那是產品決定）。",
            "項", Source, FactTrustLevel.Reported, false, at, v.FactCount);
    }

    // ── 讀取層 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 真實通路：<b>本版刻意不實作完整的能力盤點</b>——它需要走一遍事實集並分類
    /// 「會變／不變」與「寫入型／唯讀型」，而那個分類本身是有爭議的（
    /// 「溫度算不算會變」取決於用途）。回 <c>null</c> 誠實標示；
    /// 判讀邏輯（<see cref="ReadsPerSecond"/>／<see cref="CacheableRatio"/>）
    /// 已完整實作並被測試釘住，接上盤點器時只換這一個方法。
    /// </summary>
    internal static ServiceModeInputs? ReadInputs() => null;

    private static HardwareFact Unavailable(DateTimeOffset at, FactAvailability availability, string reason)
        => new(ServiceKey, Cat, "背景服務模式", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, availability, reason);
}
