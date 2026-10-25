using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>事實總表的一個域（鍵的前綴）與它在這一輪的狀態。</summary>
/// <remarks>
/// <c>Collected</c> 是「本輪事實集裡出現了幾把」——它與 <c>Present</c> 不是同一件事：
/// 出現但讀不到（NotSupported／ReadError…）也是出現。
/// </remarks>
public sealed record FactAtlasDomainRow(
    string Prefix,
    int CatalogKeys,
    int Collected,
    int Present,
    int Unavailable,
    string Example);

/// <summary>事實總表的一列：一把鍵在這一輪的實際狀態。</summary>
/// <remarks><paramref name="Spark"/> 是本工作階段內各輪讀值的小走勢；沒有歷史（少於兩個數值點）為 null，欄位留空。</remarks>
public sealed record FactAtlasKeyRow(
    string Key,
    string Domain,
    string Name,
    string ValueText,
    string StateText,
    string Source,
    bool Collected,
    double[]? Spark = null)
{
    /// <summary>給 XAML 繫結用的走勢包裝（樣板區段的陣列型屬性標記編譯不受支援，照 MetricHistory 慣例包一層）。</summary>
    public Sparkline.SparkSeries? SparkSeries => Spark is { Length: >= 2 } ? new Sparkline.SparkSeries(Spark) : null;
}

/// <summary>動態鍵家族：成員由機器決定、編譯期數不出來的那一群。</summary>
public sealed record FactAtlasFamilyRow(string Prefix, string Reason);

/// <summary>
/// 事實總表（「能力索引」）：把<b>這個建置實作的每一把事實鍵</b>攤成一張可搜尋的表。
///
/// <para>
/// <b>為什麼要有這一頁：</b>本專案的事實鍵已經超過一千把，而它們的出口有三種——
/// 專屬頁面、證據實驗室的原始清單、以及<b>只有匯出檔看得見</b>。
/// 第三種最危險：功能確實做了（服務有 Collect、測試有金標），但使用者在畫面上找不到它，
/// 於是「做了」與「沒做」對他來說一模一樣。這一頁把三種出口收斂成一張表：
/// <b>每一把鍵都在這裡，連同它這一輪的狀態與值。</b>
/// </para>
///
/// <para>
/// <b>分母是編譯期目錄，不是本輪收集結果。</b>鍵的清單來自 <see cref="FactKeyCatalog"/>
/// （由 <c>FactKeyCatalogTests</c> 與原始碼逐鍵比對）。若改用「本輪收集到的事實」當分母，
/// 這一頁會變成「這次掃到什麼就列什麼」——那正好又把「有實作但這次沒跑到」藏起來，
/// 而那個狀態是這一頁存在的理由。所以目錄鍵永遠列出，狀態欄如實寫「本次未收集」。
/// </para>
///
/// <para>
/// <b>「本次未收集」不是「沒有實作」。</b>它最常見的成因是這條路徑需要驅動、需要提權、
/// 或需要使用者按下去（例如假容量驗證、迴路訊號擷取）。這一頁不猜是哪一種，只把差別標出來；
/// 猜一個原因比留白更糟。
/// </para>
/// </summary>
public sealed class FactAtlasService : ObservableObject
{
    /// <summary>本服務事實的分類名稱（快照與報告用）。</summary>
    public const string Category = "事實總表";

    /// <summary>
    /// 逐鍵明細的走勢欄（U10-VZ-0064）。資料來源＝本工作階段內各輪載入的實際讀值
    /// （<see cref="RecordHistory"/>），不是任何事後推估；沒有歷史的鍵該欄如實留空。
    /// </summary>
    public const string SparkKey = "ui.factatlas.spark";

    /// <summary>走勢欄保留的每鍵點數上限（超過丟最舊的——欄寬 90px，再多也畫不出差別）。</summary>
    internal const int SparkCapacity = 32;

    private IReadOnlyList<HardwareFact> _facts = [];
    private string _filter = "";
    private string _domainFilter = "";

    // 走勢歷史：鍵 → 本工作階段各輪的數值讀值（時間序）。
    private readonly Dictionary<string, List<double>> _history = new(StringComparer.Ordinal);
    // 上一輪的數值快照（判斷「換了一輪」用；同一輪反覆按重新載入不重複記點）。
    private Dictionary<string, double>? _lastRound;

    /// <summary>逐域摘要（依鍵數由多到少）。</summary>
    public ObservableCollection<FactAtlasDomainRow> Domains { get; } = [];

    /// <summary>明細列（套用搜尋與域篩選之後）。</summary>
    public ObservableCollection<FactAtlasKeyRow> Keys { get; } = [];

    /// <summary>動態鍵家族（成員由機器決定）。</summary>
    public ObservableCollection<FactAtlasFamilyRow> Families { get; } = [];

    /// <summary>域下拉的選項：空字串代表「全部」。</summary>
    public ObservableCollection<string> DomainOptions { get; } = [];

    /// <summary>一行結論。</summary>
    public string Summary { get; private set; } = "尚未載入。";

    /// <summary>搜尋與篩選的補充說明（例如「篩選後 37／1034 把」）。</summary>
    public string FilterNote { get; private set; } = "";

    /// <summary>目錄裡有、但這一輪事實集沒出現的鍵數——<b>不是</b>未實作的項數。</summary>
    public int AbsentCount { get; private set; }

    /// <summary>搜尋字串（比對鍵、名稱、值與來源，不分大小寫）。</summary>
    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value ?? "")) Rebuild(); }
    }

    /// <summary>域篩選（空字串＝全部）。</summary>
    public string DomainFilter
    {
        get => _domainFilter;
        set { if (SetProperty(ref _domainFilter, value ?? "")) Rebuild(); }
    }

    /// <summary>
    /// 載入這一輪的事實集。<b>可以傳空的集合</b>——那代表「這一輪什麼都沒收集到」，
    /// 畫面會把每一把鍵標成「本次未收集」，而不是列出一張空表假裝沒有鍵。
    /// </summary>
    public void Load(IReadOnlyList<HardwareFact>? facts)
    {
        _facts = facts ?? [];
        RecordHistory(_facts);

        var byKey = new Dictionary<string, HardwareFact>(StringComparer.Ordinal);
        foreach (var f in _facts)
            byKey[f.Key] = f;   // 同鍵多筆取最後（管線上每鍵唯一）

        Domains.Clear();
        DomainOptions.Clear();
        DomainOptions.Add("");
        AbsentCount = 0;

        var rows = new List<FactAtlasDomainRow>();
        foreach (var group in FactKeyCatalog.Keys.GroupBy(PrefixOf, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int collected = 0, present = 0;
            string example = "";
            foreach (var key in group)
            {
                if (!byKey.TryGetValue(key, out var f)) continue;
                collected++;
                if (f.Availability == FactAvailability.Present)
                {
                    present++;
                    // 例子取第一把讀到值且值不是空的——空值當例子等於沒有例子。
                    if (example.Length == 0 && !string.IsNullOrWhiteSpace(f.Value))
                        example = $"{key}＝{Shorten(f.Value)}";
                }
            }
            int total = group.Count();
            AbsentCount += total - collected;
            rows.Add(new FactAtlasDomainRow(group.Key, total, collected, present, collected - present, example));
            DomainOptions.Add(group.Key);
        }

        foreach (var r in rows.OrderByDescending(r => r.CatalogKeys).ThenBy(r => r.Prefix, StringComparer.Ordinal))
            Domains.Add(r);

        Families.Clear();
        foreach (var kv in FactKeyDynamicCatalog.Families.OrderBy(k => k.Key, StringComparer.Ordinal))
            Families.Add(new FactAtlasFamilyRow(kv.Key, kv.Value));

        Summary = string.Create(CultureInfo.InvariantCulture,
            $"目錄 {FactKeyCatalog.Keys.Count} 把鍵、{FactKeyDynamicCatalog.Families.Count} 個動態家族；"
            + $"本輪事實集 {_facts.Count} 筆、命中目錄鍵 {_facts.Count(f => IsCatalogKey(f.Key))} 把；"
            + $"目錄裡有 {AbsentCount} 把鍵這一輪沒有出現（未收集，不等於未實作）。");

        Rebuild();
    }

    /// <summary>重算明細列（搜尋、域篩選變動時呼叫）。</summary>
    public void Rebuild()
    {
        var byKey = new Dictionary<string, HardwareFact>(StringComparer.Ordinal);
        foreach (var f in _facts)
            byKey[f.Key] = f;

        string q = _filter.Trim();
        string domain = _domainFilter;
        int matched = 0;

        Keys.Clear();
        foreach (var key in FactKeyCatalog.Keys)
        {
            string dom = PrefixOf(key);
            if (domain.Length > 0 && !string.Equals(dom, domain, StringComparison.Ordinal)) continue;

            bool has = byKey.TryGetValue(key, out var fact);
            if (q.Length > 0 && !Matches(key, dom, q, has ? fact : null)) continue;

            matched++;
            Keys.Add(Row(key, dom, has ? fact : null, _history.TryGetValue(key, out var h) && h.Count >= 2 ? [.. h] : null));
        }

        FilterNote = q.Length == 0 && domain.Length == 0
            ? $"顯示全部 {Keys.Count} 把鍵。"
            : $"篩選後 {matched}／{FactKeyCatalog.Keys.Count} 把鍵"
              + (domain.Length > 0 ? $"（域：{domain}）" : "")
              + (q.Length > 0 ? $"（搜尋：{q}）" : "");
        OnPropertyChanged(nameof(FilterNote));
    }

    /// <summary>鍵的域＝第一個小圓點之前那一段（<c>ac.dmar.summary</c> → <c>ac</c>）。</summary>
    public static string PrefixOf(string key)
    {
        int dot = key.IndexOf('.');
        return dot > 0 ? key[..dot] : key;
    }

    /// <summary>目錄鍵的查詢集合（只建一次；目錄是編譯期常數）。</summary>
    private static readonly HashSet<string> CatalogKeys =
        new(FactKeyCatalog.Keys, StringComparer.Ordinal);

    /// <summary>這把鍵是否在編譯期目錄裡（動態家族的成員不在）。</summary>
    public static bool IsCatalogKey(string key) => CatalogKeys.Contains(key);

    private static bool Matches(string key, string domain, string q, HardwareFact? fact)
        => key.Contains(q, StringComparison.OrdinalIgnoreCase)
        || domain.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (fact is not null
            && (fact.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || fact.Value.Contains(q, StringComparison.OrdinalIgnoreCase)
                || fact.Source.Contains(q, StringComparison.OrdinalIgnoreCase)));

    private static FactAtlasKeyRow Row(string key, string domain, HardwareFact? fact, double[]? spark)
    {
        if (fact is null)
            return new FactAtlasKeyRow(key, domain, "", "—", "本次未收集", "", false, spark);

        string state = StateText(fact.Availability);
        string value = fact.Availability == FactAvailability.Present
            ? (string.IsNullOrWhiteSpace(fact.Value) ? "（空值）" : fact.Value)
            : "—";
        string name = fact.Name;
        if (fact.Availability != FactAvailability.Present && !string.IsNullOrWhiteSpace(fact.UnavailableReason))
            name = $"{name}　—　{fact.UnavailableReason}";

        return new FactAtlasKeyRow(key, domain, name, value, state, fact.Source, true, spark);
    }

    /// <summary>
    /// 記錄本輪的數值讀值（走勢欄的資料來源）。
    ///
    /// <para>
    /// <b>只記真的讀到、且是數字的鍵</b>：優先用管線已認定的 <see cref="HardwareFact.NumericValue"/>，
    /// 沒有才對值文字做不變文化嚴格解析——值帶單位或文字的不硬拆，讀不到的不補上一輪舊值。
    /// 每一輪記一點；與上一輪完全相同的快照（同一輪反覆按「重新載入本輪事實」）不重複記，
    /// 否則按十次按鈕會把一輪畫成十點的假走勢。點數達上限丟最舊的。
    /// </para>
    /// </summary>
    private void RecordHistory(IReadOnlyList<HardwareFact> facts)
    {
        var round = new Dictionary<string, double>(facts.Count, StringComparer.Ordinal);
        foreach (var f in facts)
        {
            if (f.Availability != FactAvailability.Present) continue;
            double? v = f.NumericValue;
            if (v is null && !string.IsNullOrWhiteSpace(f.Value)
                && double.TryParse(f.Value.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                v = parsed;
            if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) continue;
            round[f.Key] = v.Value;   // 同鍵多筆取最後（與 Load 的 byKey 同一口徑）
        }

        if (_lastRound is not null && round.Count == _lastRound.Count)
        {
            bool same = true;
            foreach (var (k, v) in round)
            {
                if (!_lastRound.TryGetValue(k, out double p) || p != v) { same = false; break; }
            }
            if (same) return;   // 同一輪，不重複記點
        }
        _lastRound = round;

        foreach (var (key, v) in round)
        {
            if (!_history.TryGetValue(key, out var list))
                _history[key] = list = new List<double>(SparkCapacity);
            list.Add(v);
            if (list.Count > SparkCapacity) list.RemoveRange(0, list.Count - SparkCapacity);
        }
    }

    /// <summary>三態的中文說法。刻意與畫面上其他地方一致，不在這裡另立一套詞。</summary>
    public static string StateText(FactAvailability a) => a switch
    {
        FactAvailability.Present => "讀到",
        FactAvailability.NotSupported => "平台不支援",
        FactAvailability.InsufficientPrivilege => "權限不足",
        FactAvailability.ReadError => "讀取失敗",
        FactAvailability.NotApplicable => "本機不適用",
        FactAvailability.Unknown => "未確認",
        _ => a.ToString(),
    };

    private static string Shorten(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= 80 ? s : s[..80] + "…";
    }
}
