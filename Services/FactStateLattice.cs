using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>
/// 事實可用性六態的<b>偏序格</b>與傳播規則。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要一個格：</b>衍生事實的可用性不能隨便挑一個來源的狀態貼上去。例如一條
/// 「微碼修訂版一致」的對帳結論同時依賴兩個來源事實——其中一個讀不到時，
/// 這條結論的可用性該是什麼？若兩邊各寫各的判斷，同一個情形在不同服務裡會得到不同答案，
/// 而使用者看到的是「有些卡片說讀不到、有些說一致」。
/// </para>
/// <para>
/// <b>格的定義（由「最可用」到「最不可用」）：</b>
/// </para>
/// <list type="number">
/// <item><see cref="FactAvailability.Present"/>——確認可用。這是唯一「可以拿去下結論」的狀態。</item>
/// <item><see cref="FactAvailability.Unknown"/>——有值但未確認。比 <c>Present</c> 弱，
/// 但比「完全沒有」強：它至少代表「嘗試過、拿到了東西、但不敢背書」。</item>
/// <item><see cref="FactAvailability.NotApplicable"/>——本機不適用。這是<b>環境事實</b>，
/// 不是缺陷：沒有 BMC 就沒有 IPMI 通路，這件事本身是確定的。所以它比下面三個「不確定」強。</item>
/// <item><see cref="FactAvailability.NotSupported"/>——不支援。也是環境事實，但比
/// <c>NotApplicable</c> 弱一點：可能只是這個版本的實作沒做，換版本或換工具就有。</item>
/// <item><see cref="FactAvailability.InsufficientPrivilege"/>——權限不足。<b>可恢復</b>：
/// 提權後就讀得到，所以比讀取失敗強。</item>
/// <item><see cref="FactAvailability.ReadError"/>——讀取失敗。<b>最弱</b>：試過了、失敗了、
/// 而且不知道為什麼失敗，連「提權就能解決」都不確定。</item>
/// </list>
/// <para>
/// <b>傳播規則（合取）：</b>衍生事實依賴多個來源時，取<b>最弱</b>的那一個。
/// 三個來源都 <c>Present</c> 才是 <c>Present</c>；只要有一個讀不到，
/// 結論就不能宣稱可用。這條規則是單調的——加進更多來源只會讓結果變弱，不會變強。
/// </para>
/// <para>
/// <b>永不靜默塌成一個值：</b>「塌陷」是指把 <c>Unknown</c>／<c>ReadError</c> 當成
/// <c>Present</c> 或 <c>NotApplicable</c> 處理（例如把讀不到的值當 0 帶進計算）。
/// 本類別的 <see cref="Combine"/> 不會產生這種結果，而 <see cref="IsTrustworthy"/> 讓
/// 呼叫端在拿值去下結論之前必須先問一次。
/// </para>
/// </remarks>
public static class FactStateLattice
{
    /// <summary>
    /// 由強到弱的排序（索引越小越可用）。<b>這個順序就是格的定義</b>，
    /// <see cref="Combine"/> 直接取索引最大值的那一個。
    /// </summary>
    private static readonly FactAvailability[] Order =
    [
        FactAvailability.Present,
        FactAvailability.Unknown,
        FactAvailability.NotApplicable,
        FactAvailability.NotSupported,
        FactAvailability.InsufficientPrivilege,
        FactAvailability.ReadError,
    ];

    /// <summary>格上的強弱序（0＝最強）。未知的列舉值排到最弱，不讓新值靜默取得強位置。</summary>
    public static int Rank(FactAvailability state)
    {
        int i = Array.IndexOf(Order, state);
        return i >= 0 ? i : Order.Length;
    }

    /// <summary>取兩個狀態中較弱的一個（合取傳播的基本運算）。</summary>
    public static FactAvailability Weakest(FactAvailability a, FactAvailability b)
        => Rank(a) >= Rank(b) ? a : b;

    /// <summary>
    /// 合取傳播：多個來源事實的可用性 → 衍生事實的可用性（取最弱者）。
    /// 空集合回 <see cref="FactAvailability.Unknown"/>——沒有任何來源的結論不該宣稱可用，
    /// 但也不該說成「讀取失敗」（那會誤導成「試過了」）。
    /// </summary>
    public static FactAvailability Combine(params FactAvailability[] sources)
        => Combine((IReadOnlyList<FactAvailability>)sources);

    /// <summary>合取傳播（清單版本）。</summary>
    public static FactAvailability Combine(IReadOnlyList<FactAvailability> sources)
    {
        if (sources.Count == 0) return FactAvailability.Unknown;
        var weakest = sources[0];
        for (int i = 1; i < sources.Count; i++)
            weakest = Weakest(weakest, sources[i]);
        return weakest;
    }

    /// <summary>
    /// 這個狀態能不能拿去下結論。<b>只有 <see cref="FactAvailability.Present"/> 可以。</b>
    /// </summary>
    /// <remarks>
    /// 這個方法的存在是為了讓呼叫端在計算之前必須先問一次——把「讀不到」當 0 帶進算式，
    /// 是所有誠實工具最常見的塌陷方式，而它不會產生任何錯誤訊息。
    /// </remarks>
    public static bool IsTrustworthy(FactAvailability state) => state == FactAvailability.Present;

    /// <summary>這個狀態是不是「有東西但不敢背書」（<c>Unknown</c>）。</summary>
    public static bool IsUnconfirmed(FactAvailability state) => state == FactAvailability.Unknown;

    /// <summary>
    /// 這個狀態是不是「本機環境本來就沒有這項」（<c>NotApplicable</c>／<c>NotSupported</c>）。
    /// 這兩者不是缺陷，報告上不該與讀取失敗混為一談。
    /// </summary>
    public static bool IsEnvironmental(FactAvailability state)
        => state is FactAvailability.NotApplicable or FactAvailability.NotSupported;

    /// <summary>這個狀態是不是「試過但沒拿到」（<c>InsufficientPrivilege</c>／<c>ReadError</c>）。</summary>
    public static bool IsBlocked(FactAvailability state)
        => state is FactAvailability.InsufficientPrivilege or FactAvailability.ReadError;

    /// <summary>
    /// 這個狀態是不是「可望恢復」——提權、換版本或修正後就可能變成可用。
    /// <c>ReadError</c> 不列入：它連失敗原因都不確定。
    /// </summary>
    public static bool IsRecoverable(FactAvailability state)
        => state is FactAvailability.InsufficientPrivilege or FactAvailability.NotSupported;

    /// <summary>格的完整順序（由強到弱），供測試與說明使用。</summary>
    public static IReadOnlyList<FactAvailability> LatticeOrder => Order;

    /// <summary>狀態的中文說明（與 <c>EvidenceLabService.UnavailableText</c> 同一口徑）。</summary>
    public static string Describe(FactAvailability state) => state switch
    {
        FactAvailability.Present => "可用",
        FactAvailability.Unknown => "未確認",
        FactAvailability.NotApplicable => "不適用",
        FactAvailability.NotSupported => "不支援",
        FactAvailability.InsufficientPrivilege => "讀不到",
        FactAvailability.ReadError => "讀取失敗",
        _ => "未收錄的狀態",
    };

    /// <summary>
    /// 報告用的說明句：把狀態與「這代表什麼」一起講，避免使用者把不適用讀成故障、
    /// 或把未確認讀成已確認。
    /// </summary>
    public static string Explain(FactAvailability state) => state switch
    {
        FactAvailability.Present => "讀到了且來源確認——可以拿去下結論。",
        FactAvailability.Unknown => "有值但未確認（快取殘留、來源可疑或多來源尚未仲裁）——不要當成已確認的事實使用。",
        FactAvailability.NotApplicable => "本機環境本來就沒有這項，不是故障。",
        FactAvailability.NotSupported => "這一版的實作沒有這條路徑，不代表硬體沒有這個能力。",
        FactAvailability.InsufficientPrivilege => "權限不足——提權後可得。",
        FactAvailability.ReadError => "嘗試過但失敗，且失敗原因未確認。",
        _ => "未收錄的狀態——請先確認它的語意再使用。",
    };
}
