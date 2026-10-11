
using System.Globalization;

namespace XinSpect;

/// <summary>
/// 查詢與智慧層事實（Vol2 推進第 57–66 項的 QS 四項＋SE-014 分級）：
/// QS-004 單位轉換層、QS-005 懸浮說明覆蓋、QS-006 查詢索引、QS-009 鍵集 diff、SE-014 安全摘要分級。
/// </summary>
/// <remarks>
/// <para><b>QS 四項都在回答「這份事實集好不好查」</b>：有沒有標單位、有沒有說明、索引長什麼樣、
/// 跟上一份比多了少了哪些鍵——它們談的是<b>查詢品質</b>，不是硬體品質。</para>
/// <para><b>SE-014 的分級不是安全背書</b>：它只把「矛盾／待確認／未知」的數量攤成三色計數，
/// 讓紅黃灰一眼可見；判定理由仍在各條對帳規則與事實裡。</para>
/// </remarks>
public static class QueryInsightFactsService
{
    private const string CatQs = "查詢與智慧層";

    public const string UnitKey = "qs.unit";
    public const string HelpKey = "qs.help";
    public const string IndexKey = "qs.index";
    public const string KeyDiffKey = "qs.keydiff";
    public const string GradeKey = "se.grade";

    /// <summary>鍵集差異：三個集合都排序，讓結果可重現（時間膠囊比較與查詢都復用它）。</summary>
    public sealed record KeySetDiffResult(
        IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Common)
    {
        public int AddedCount => Added.Count;
        public int RemovedCount => Removed.Count;
        public int CommonCount => Common.Count;
    }

    /// <summary>QS-009 的純函式：兩個鍵集的差（不分大小寫，輸出以 Ordinal 排序）。</summary>
    public static KeySetDiffResult DiffKeySets(IEnumerable<string> before, IEnumerable<string> after)
    {
        var b = new HashSet<string>(before ?? [], StringComparer.OrdinalIgnoreCase);
        var a = new HashSet<string>(after ?? [], StringComparer.OrdinalIgnoreCase);
        return new KeySetDiffResult(
            a.Where(k => !b.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            b.Where(k => !a.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            a.Where(b.Contains).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    // ── 尾端彙總（吃前面各組事實，輸入不含自己）──────────────────────────

    public static IReadOnlyList<HardwareFact> CollectDerived(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
        => [UnitFact(at, facts), IndexFact(at, facts), GradeFact(at, facts)];

    /// <summary>QS-004：有數值的事實有沒有標單位；標了哪些單位。</summary>
    public static HardwareFact UnitFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        if (facts.Count == 0)
            return new HardwareFact(UnitKey, CatQs, "單位轉換層",
                "", "", "事實集的 Unit 欄位", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "事實集是空的——沒有東西可以檢查單位（不是「全部都有標」）");

        var numeric = facts.Where(f => f.NumericValue is not null).ToList();
        var missing = numeric.Where(f => string.IsNullOrWhiteSpace(f.Unit)).Select(f => f.Key)
            .Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var units = facts.Where(f => !string.IsNullOrWhiteSpace(f.Unit))
            .GroupBy(f => f.Unit, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}×{g.Count()}").ToList();

        return new HardwareFact(UnitKey, CatQs, "單位轉換層",
            $"有數值 {numeric.Count} 項、其中未標單位 {missing.Count} 項" +
            (missing.Count > 0 ? $"（前幾個：{string.Join("、", missing.Take(8))}）" : "") +
            $"；已標單位：{string.Join("、", units.Take(10))}" +
            (units.Count > 10 ? $"…等 {units.Count} 種" : "") +
            "。界線：本項只檢「有沒有標」，不做跨單位換算（換算要有明確的來源與精度，不能靠猜）。",
            "項", "事實集的 Unit 欄位", FactTrustLevel.Derived, false, at, missing.Count,
            missing.Count > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            missing.Count > 0 ? $"{missing.Count} 個數值事實沒有標單位——查詢端無法安全比較，這裡如實列出" : null);
    }

    /// <summary>QS-006：以域前綴建立查詢索引的統計（鍵的第一段）。</summary>
    public static HardwareFact IndexFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        if (facts.Count == 0)
            return new HardwareFact(IndexKey, CatQs, "查詢索引",
                "", "", "事實鍵的域前綴", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "事實集是空的——索引沒有東西可建");

        var domains = facts.Select(f => f.Key.Split('.')[0])
            .GroupBy(d => d, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        return new HardwareFact(IndexKey, CatQs, "查詢索引",
            $"{facts.Count} 把鍵、{domains.Count} 個域前綴；最大域：" +
            string.Join("、", domains.Take(8).Select(g => $"{g.Key}×{g.Count()}")) +
            "。界線：索引是查詢加速用（依鍵的第一段分域），不是新的資料來源。",
            "域", "事實鍵的域前綴（建索引，不查硬體）", FactTrustLevel.Derived, false, at, domains.Count);
    }

    /// <summary>SE-014：把矛盾／待確認／未知的數量攤成三色計數——分級不是安全背書。</summary>
    public static HardwareFact GradeFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        int red = facts.Count(f => f.Category == "交叉對帳" && f.Value.StartsWith("矛盾", StringComparison.Ordinal));
        int gray = facts.Count(f => f.Availability is FactAvailability.Unknown or FactAvailability.ReadError);
        int yellow = facts.Count(f => f.Availability is FactAvailability.NotSupported
            or FactAvailability.InsufficientPrivilege or FactAvailability.NotApplicable);
        int green = facts.Count(f => f.Availability == FactAvailability.Present);

        var state = red > 0 ? FactAvailability.Unknown : FactAvailability.Present;
        return new HardwareFact(GradeKey, "進階安全", "安全摘要分級",
            $"紅（對帳矛盾）{red} 條・黃（不支援／缺權限／不適用）{yellow} 條・灰（未知／讀取失敗）{gray} 條・可採信 {green} 項。" +
            "界線：這是計數彙總，不是安全評分，也不是背書——紅黃灰各自的原因在對應的事實與對帳規則裡。",
            "條", "全部事實的可用性＋對帳矛盾", FactTrustLevel.Derived, false, at, red,
            state, red > 0 ? $"{red} 條對帳矛盾待人工判斷（矛盾是事實，不是判決）" : null);
    }

    // ── 需要外部來源的項 ────────────────────────────────────────────────

    /// <summary>QS-005 懸浮說明覆蓋：說明目錄（HelpCatalog）與頁面清單（PageRegistry）的覆蓋率。</summary>
    public static HardwareFact HelpFact(DateTimeOffset at,
        Func<IReadOnlyCollection<string>>? helpKeys = null,
        Func<IReadOnlyList<string>>? pageKeys = null)
    {
        IReadOnlyCollection<string>? help;
        try { help = (helpKeys ?? (() => HelpCatalog.All.Keys.ToList()))(); }
        catch { help = null; }

        if (help is null)
            return new HardwareFact(HelpKey, CatQs, "懸浮說明覆蓋",
                "", "", "HelpCatalog", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "說明目錄讀不到——不把「讀不到」讀成「沒有說明」");

        var pages = (pageKeys ?? (() => PageRegistry.Pages.Select(p => p.Key).ToList()))();
        var prefixes = help.Select(k => k.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        var uncovered = pages.Where(p => !prefixes.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        return new HardwareFact(HelpKey, CatQs, "懸浮說明覆蓋",
            $"說明條目 {help.Count} 條、頁面 {pages.Count} 個；沒有頁面級說明的 {uncovered.Count} 個" +
            (uncovered.Count > 0 ? $"（{string.Join("、", uncovered.Take(10))}）" : "") +
            "。界線：覆蓋率只講「有沒有文案」，不講文案寫得好不好。",
            "條", "HelpCatalog 鍵前綴 ↔ PageRegistry 頁面鍵", FactTrustLevel.Derived, false, at, uncovered.Count);
    }

    /// <summary>独立載入的兩把鍵：說明覆蓋（QS-005）與鍵集 diff（QS-009）。
    /// 「上一份」只有做過時間膠囊比較之後才存在，沒做過就如實不適用。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        IReadOnlyCollection<string>? previousKeys = null,
        IReadOnlyCollection<string>? currentKeys = null)
        => [HelpFact(at), KeyDiffFact(at, previousKeys, currentKeys)];

    public static HardwareFact KeyDiffFact(DateTimeOffset at, IReadOnlyCollection<string>? previousKeys,
        IReadOnlyCollection<string>? currentKeys)
    {
        const string source = "時間膠囊比較（上一份鍵集 vs 這一份）";
        if (previousKeys is null)
            return new HardwareFact(KeyDiffKey, CatQs, "鍵集 diff", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本輪沒有可比較的上一份鍵集——鍵集 diff 只在做過時間膠囊比較後才有意義（工具已就緘，單純沒有資料）");
        if (previousKeys.Count == 0 || currentKeys is null || currentKeys.Count == 0)
            return new HardwareFact(KeyDiffKey, CatQs, "鍵集 diff", "", "", source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.Unknown,
                "有一邊的鍵集是空的——這樣的比較不会有意義（空集合可能是沒載入，不是真的一個欄位都沒有）");

        var d = DiffKeySets(previousKeys, currentKeys);
        return new HardwareFact(KeyDiffKey, CatQs, "鍵集 diff",
            $"上一份 {previousKeys.Count} 把鍵 vs 這一份 {currentKeys.Count} 把：新增 {d.AddedCount}・消失 {d.RemovedCount}・兩邊都有 {d.CommonCount}。"
            + (d.Added.Count > 0 ? "；新增：" + string.Join("、", d.Added.Take(8)) : "")
            + (d.Removed.Count > 0 ? "；消失：" + string.Join("、", d.Removed.Take(8)) : "")
            + " 。界線：鍵集差異講的是「哪些欄位存在變了」，不是硬體本身變了（值的差異看快照比較的變更清單）。",
            "把", source + " × KeySetDiff（純函式）", FactTrustLevel.Derived, false, at,
            d.AddedCount + d.RemovedCount);
    }
}
