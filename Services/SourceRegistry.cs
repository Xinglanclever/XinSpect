namespace XinSpect;

/// <summary>
/// 來源列舉與去重（ULTIMATE-1000 階段一 P1-006）。
///
/// <para>
/// <b>為什麼同一來源不得註冊兩次：</b>註冊兩次的來源會被讀兩次，於是同一把事實鍵
/// 在同一次掃描裡出現兩筆。本專案的 <c>FactKeyCatalog</c> 釘住「目錄不得有重複鍵」，
/// 但那是<b>靜態</b>的清單；執行期出現重複，目錄看不出來。
/// </para>
///
/// <para>
/// 這裡的「同一來源」以 <see cref="IReadOnlyScanner.BackendName"/> 認定，
/// <b>不</b>以型別認定——同一個型別可以合法地用不同參數註冊兩次
/// （例如兩個 SPD 通道），但名稱必須不同；名稱相同就是真的重複。
/// </para>
/// </summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, IReadOnlyScanner> _sources = new(StringComparer.Ordinal);
    private readonly List<string> _rejected = [];

    /// <summary>目前已註冊的來源數。這就是 <c>src.registry.count</c> 的值。</summary>
    public int Count => _sources.Count;

    /// <summary>被拒的註冊（名稱重複），供掃描紀錄指名。刻意保留而不只是計數。</summary>
    public IReadOnlyList<string> Rejected => _rejected;

    /// <summary>
    /// 註冊一個來源。名稱重複時<b>拒絕後來的</b>並記錄下來，不靜默覆蓋。
    ///
    /// <para>
    /// 為什麼是「拒絕後來的」而不是「後來的覆蓋前面的」：覆蓋會讓「哪一支在跑」
    /// 取決於註冊順序，而註冊順序散落在各處載入程式裡——那正是本專案吃過虧的
    /// 「同一件事多套命名空間」的溫床。拒絕並記錄，才看得到衝突。
    /// </para>
    /// </summary>
    public bool Register(IReadOnlyScanner source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string name = source.BackendName;
        if (string.IsNullOrWhiteSpace(name))
        {
            _rejected.Add("（未命名來源）");
            return false;
        }
        if (_sources.ContainsKey(name))
        {
            _rejected.Add(name);
            return false;
        }
        _sources[name] = source;
        return true;
    }

    /// <summary>依註冊順序列出來源。順序穩定是為了讓掃描順序可重現。</summary>
    public IReadOnlyList<IReadOnlyScanner> Sources => _sources.Values.ToList();

    /// <summary>
    /// 列舉後的自我檢查：回傳的問題為空＝這一輪的註冊是乾淨的。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (_rejected.Count > 0)
            problems.Add($"有 {_rejected.Count} 個來源因名稱重複被拒：{string.Join("、", _rejected)}");
        var dupes = _sources.Values.GroupBy(s => s.BackendName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
            problems.Add($"註冊表中出現重複名稱：{string.Join("、", dupes)}");
        return problems;
    }

    /// <summary>產生 <c>src.registry.count</c> 的事實。</summary>
    public HardwareFact ToFact(DateTimeOffset at)
    {
        if (Count == 0)
            return new HardwareFact("src.registry.count", "來源接縫", "已註冊來源數",
                "沒有任何來源被註冊", "個", "來源註冊表", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "本次載入沒有註冊任何來源");

        string value = _rejected.Count == 0
            ? $"{Count} 個來源"
            : $"{Count} 個來源（{_rejected.Count} 個因重複被拒）";
        return new HardwareFact("src.registry.count", "來源接縫", "已註冊來源數",
            value, "個", "來源註冊表", FactTrustLevel.Derived, false, at,
            Count, _rejected.Count == 0 ? FactAvailability.Present : FactAvailability.ReadError,
            _rejected.Count == 0 ? null : $"重複來源：{string.Join("、", _rejected)}");
    }
}
