
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

// ── QS-004 單位換算層（補完）────────────────────────────────────────────

/// <summary>
/// 單位換算層：把常見單位正規化到一組基準（bytes／Hz／s／°C／W／V／A），
/// <b>只做有明確定義的換算</b>——未知單位一律回 false，不猜、不近似。
/// </summary>
public static class UnitConverter
{
    /// <summary>單位 → （基準單位, 相對基準的倍率）。</summary>
    private static readonly IReadOnlyDictionary<string, (string Canonical, double Factor)> Map =
        new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = ("B", 1), ["byte"] = ("B", 1),
            ["KiB"] = ("B", 1024), ["MiB"] = ("B", 1024d * 1024), ["GiB"] = ("B", 1024d * 1024 * 1024),
            ["TiB"] = ("B", 1024d * 1024 * 1024 * 1024),
            ["KB"] = ("B", 1000), ["MB"] = ("B", 1000000), ["GB"] = ("B", 1000000000),
            ["Hz"] = ("Hz", 1), ["kHz"] = ("Hz", 1000), ["MHz"] = ("Hz", 1000000), ["GHz"] = ("Hz", 1000000000),
            ["ms"] = ("s", 0.001), ["us"] = ("s", 0.000001), ["ns"] = ("s", 0.000000001), ["s"] = ("s", 1),
            ["°C"] = ("°C", 1), ["C"] = ("°C", 1),
            ["W"] = ("W", 1), ["mW"] = ("W", 0.001),
            ["V"] = ("V", 1), ["mV"] = ("V", 0.001),
            ["A"] = ("A", 1), ["mA"] = ("A", 0.001),
        };

    /// <summary>能不能換算這個單位（空字串／未知單位都回 false）。</summary>
    public static bool IsKnown(string? unit) => unit is not null && Map.ContainsKey(unit.Trim());

    /// <summary>單位對應的基準單位；未知回 null。</summary>
    public static string? CanonicalUnit(string? unit) =>
        unit is not null && Map.TryGetValue(unit.Trim(), out var m) ? m.Canonical : null;

    /// <summary>同基準的單位互換（例如 MiB→GiB）；不同基準或未知單位回 false。</summary>
    public static bool TryConvert(double value, string? from, string? to, out double result)
    {
        result = double.NaN;
        if (from is null || to is null) return false;
        if (!Map.TryGetValue(from.Trim(), out var f) || !Map.TryGetValue(to.Trim(), out var t)) return false;
        if (!string.Equals(f.Canonical, t.Canonical, StringComparison.Ordinal)) return false;
        result = value * f.Factor / t.Factor;
        return true;
    }

    /// <summary>正規化到基準單位（回 null＝這個單位沒定義，不猜）。</summary>
    public static (double Value, string Unit)? Normalize(double value, string? unit)
    {
        if (unit is null || !Map.TryGetValue(unit.Trim(), out var m)) return null;
        return (value * m.Factor, m.Canonical);
    }

    /// <summary>攝氏／華氏互換（溫度不走倍率表，是仿射換算；支援 C／F 兩種）。</summary>
    public static bool TryConvertTemperature(double value, char from, char to, out double result)
    {
        result = double.NaN;
        char f = char.ToUpperInvariant(from), t = char.ToUpperInvariant(to);
        if ((f != 'C' && f != 'F') || (t != 'C' && t != 'F')) return false;
        result = (f, t) switch
        {
            ('C', 'C') or ('F', 'F') => value,
            ('C', 'F') => value * 9 / 5 + 32,
            _ => (value - 32) * 5 / 9,
        };
        return true;
    }
}

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
        var unknownUnits = units.Where(u => !UnitConverter.IsKnown(u.Split('×')[0])).Select(u => u.Split('×')[0]).ToList();
        int knownCount = units.Count - unknownUnits.Count;

        return new HardwareFact(UnitKey, CatQs, "單位轉換層",
            $"有數值 {numeric.Count} 項、其中未標單位 {missing.Count} 項" +
            (missing.Count > 0 ? $"（前幾個：{string.Join("、", missing.Take(8))}）" : "") +
            $"；已標單位：{string.Join("、", units.Take(10))}" +
            (units.Count > 10 ? $"…等 {units.Count} 種" : "") +
            $"；可換算單位 {knownCount} 種" + (unknownUnits.Count > 0 ? $"、未定義換算 {string.Join("、", unknownUnits.Take(6))}" : "") +
            "。界線：換算只做有明確定義的單位（倍率或仿射），未知單位回「不支援」而不是猜一個數字；" +
            "數值少了單位就無法安全比較——這正是本項要抓的。",
            "項", "事實集的 Unit 欄位", FactTrustLevel.Derived, false, at, missing.Count,
            missing.Count > 0 ? FactAvailability.Unknown : FactAvailability.Present,
            missing.Count > 0 ? $"{missing.Count} 個數值事實沒有標單位——查詢端無法安全比較，這裡如實列出" : null);
    }

    /// <summary>
    /// QS-006 的查詢索引：鍵 → 域前綴（第一段）分桶，供查詢端先用域縮小候選集。
    /// 純記憶體、可重建；回傳的字典是不可變視圖（呼叫端不該改它）。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildPrefixIndex(IEnumerable<string> keys)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string key in keys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            int dot = key.IndexOf('.');
            string domain = dot > 0 ? key[..dot] : key;
            if (!index.TryGetValue(domain, out var bucket)) index[domain] = bucket = [];
            bucket.Add(key);
        }
        return index.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>用索引查一個域（回傳依 Ordinal 排序，查不到回空清單——不是 null）。</summary>
    public static IReadOnlyList<string> LookupDomain(IReadOnlyDictionary<string, IReadOnlyList<string>> index, string domain)
        => index.TryGetValue(domain, out var list)
            ? list.OrderBy(k => k, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>QS-006：以域前綴建立查詢索引的統計（含建索引耗時——量的是索引本身，不含硬體讀取）。</summary>
    public static HardwareFact IndexFact(DateTimeOffset at, IReadOnlyList<HardwareFact> facts)
    {
        if (facts.Count == 0)
            return new HardwareFact(IndexKey, CatQs, "查詢索引",
                "", "", "事實鍵的域前綴", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "事實集是空的——索引沒有東西可建");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var index = BuildPrefixIndex(facts.Select(f => f.Key));
        sw.Stop();
        var domains = index.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        return new HardwareFact(IndexKey, CatQs, "查詢索引",
            $"{facts.Count} 把鍵、{index.Count} 個域前綴・建索引 {sw.Elapsed.TotalMilliseconds:F3} ms；最大域：" +
            string.Join("、", domains.Take(8).Select(kv => $"{kv.Key}×{kv.Value.Count}")) +
            "。界線：索引是查詢加速用（鍵的第一段分域），不是新的資料來源；沒有基準測試就沒有「快」的宣稱。",
            "域", "事實鍵的域前綴（建索引，不查硬體）", FactTrustLevel.Derived, false, at, index.Count);
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
