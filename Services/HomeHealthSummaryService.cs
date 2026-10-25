namespace XinSpect;

/// <summary>
/// 首頁健康摘要卡（U10-UI-0048，追蹤鍵 <c>ui.home.healthcards</c>）的一列資料：
/// 純彙整、零 I/O、零讀取——輸入就是既有事實（<see cref="EvidenceLabService.AllFacts"/>），
/// 本服務不新增任何讀取路徑。
/// </summary>
/// <param name="Total">事實總筆數。</param>
/// <param name="Present">「讀到了且來源與路徑都確認」的筆數。</param>
/// <param name="NotSupported">平台不支援（環境事實，不是錯誤）。</param>
/// <param name="InsufficientPrivilege">有路徑但權限不足（提權後可得）。</param>
/// <param name="ReadError">嘗試過但讀取失敗。</param>
/// <param name="NotApplicable">本機不適用這項查詢。</param>
/// <param name="Unknown">有值但未確認。</param>
/// <param name="ReconcileCount">交叉對帳結論事實的筆數（0＝還沒對過帳，此時「矛盾 0」不是可說的話）。</param>
/// <param name="ContradictionRules">交叉對帳判為「矛盾」的規則名（哪幾條在打架，逐條說名字）。</param>
public sealed record HomeHealthSummary(
    int Total,
    int Present,
    int NotSupported,
    int InsufficientPrivilege,
    int ReadError,
    int NotApplicable,
    int Unknown,
    int ReconcileCount,
    IReadOnlyList<string> ContradictionRules)
{
    /// <summary>讀不到（非 Present）的筆數。每一筆都是一個沒有聲音的來源——
    /// 計數是陳述，不是判決：缺源不扣分、不點燈，只如實說有幾個。</summary>
    public int Unreadable => Total - Present;

    /// <summary>是否已有事實可彙整。沒有就直說「尚未擷取」，不畫一張全零的卡。</summary>
    public bool HasFacts => Total > 0;

    /// <summary>矛盾的規則名清單；沒有矛盾就是空清單（卡上隱藏，不是寫個 0 冒充對過帳）。</summary>
    public string ContradictionText => string.Join("、", ContradictionRules);
}

/// <summary>
/// 首頁健康摘要（U10-UI-0048）的彙整器：把既有事實的六態分布與交叉對帳矛盾
/// 收成一句話說得完的計數，給總覽頁的健康摘要卡用。
/// </summary>
/// <remarks>
/// <b>刻意只陳述、不判決：</b>健康評分（<see cref="HealthReport"/>）已經有一個合成評分的家，
/// 這張卡不合成第二個分數、不給紅綠燈——它陳述「有多少事實、幾成讀到了、
/// 哪些狀態各幾筆、哪幾條對帳規則在矛盾」。矛盾與缺源的判讀在各自的老家
/// （韌體安全頁的判決卡與 <c>--errors</c>），這裡只當指路牌。
/// </remarks>
public static class HomeHealthSummaryService
{
    /// <summary>本功能的追蹤鍵（U10-UI-0048）。以 const 字面寫在原始碼裡，鍵掃描才找得到
    /// （XML 註解裡的鍵掃描不到——第 271 輪教訓）。</summary>
    public const string HomeSummaryKey = "ui.home.healthcards";

    /// <summary>交叉對帳結論事實的鍵前綴（<see cref="EvidenceLabService.EvaluateReconciliation"/> 產生，
    /// 值形如「矛盾：…／一致：…／無法驗證：…」）。刻意不以 *Key* 命名：
    /// 這是判讀用的前綴、不是新的事實鍵，不能讓鍵目錄掃描把它照成一把新鍵。</summary>
    private const string ReconcilePrefix = "reconcile.";

    /// <summary>對帳結論值裡代表矛盾的開頭（與產生端同一個字，不改寫產生端）。</summary>
    private const string ContradictsPrefix = "矛盾";

    public static HomeHealthSummary Aggregate(IReadOnlyList<HardwareFact> facts)
    {
        var rules = new List<string>();
        int reconcile = 0;
        int present = 0, notSupported = 0, noPrivilege = 0, readError = 0, notApplicable = 0, unknown = 0;
        foreach (var f in facts)
        {
            // 矛盾從「同一份事實」裡撈：交叉對帳的結論事實也是事實，不需要第二條輸入通道
            if (f.Key.StartsWith(ReconcilePrefix, StringComparison.Ordinal))
            {
                reconcile++;
                if (f.Value.StartsWith(ContradictsPrefix, StringComparison.Ordinal))
                    rules.Add(f.Name);
            }

            switch (f.Availability)
            {
                case FactAvailability.Present: present++; break;
                case FactAvailability.NotSupported: notSupported++; break;
                case FactAvailability.InsufficientPrivilege: noPrivilege++; break;
                case FactAvailability.ReadError: readError++; break;
                case FactAvailability.NotApplicable: notApplicable++; break;
                default: unknown++; break;
            }
        }
        return new HomeHealthSummary(facts.Count, present, notSupported, noPrivilege, readError, notApplicable, unknown,
            reconcile, [.. rules]);
    }
}
