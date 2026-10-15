namespace XinSpect;

/// <summary>
/// 來源能力矩陣（ULTIMATE-1000 階段一 P1-011）。
///
/// <para>
/// <b>要解決的問題：</b>使用者看到某個欄位是空的，沒辦法分辨
/// 「這台機器沒有這項硬體」、「這條來源不讀這一欄」、還是「讀了但失敗」。
/// 第一種是環境事實、第二種是能力邊界、第三種才是錯誤。
/// 來源能力矩陣就是把「這條來源承諾能讀哪些欄位」寫成資料，
/// 讓那三種情形在輸出裡分得開。
/// </para>
///
/// <para>
/// <b>宣告的能力與實際讀到的落差要看得見。</b>宣告了卻讀不到＝缺陷；
/// 沒宣告卻讀到了＝宣告不實。兩者都由 <see cref="Reconcile"/> 指出來。
/// </para>
/// </summary>
public sealed class SourceCapabilityMatrix
{
    private readonly Dictionary<string, HashSet<string>> _declared = new(StringComparer.Ordinal);

    /// <summary>宣告一條來源能讀哪些欄位（以事實鍵前綴表示，例如 <c>st.nvme.</c>）。</summary>
    public SourceCapabilityMatrix Declare(string source, params string[] fieldPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!_declared.TryGetValue(source, out var set))
            _declared[source] = set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string p in fieldPrefixes)
            if (!string.IsNullOrWhiteSpace(p))
                set.Add(p);
        return this;
    }

    /// <summary>這條來源宣告了哪些欄位。未宣告的來源回空集合，不丟例外。</summary>
    public IReadOnlyCollection<string> DeclaredFor(string source) =>
        _declared.TryGetValue(source, out var set) ? set : [];

    public IReadOnlyCollection<string> Sources => _declared.Keys;

    /// <summary>
    /// 對帳：這一輪「實際讀到的事實」與「宣告的能力」比對。
    /// 回傳的問題為空＝宣告與實際一致。
    /// </summary>
    public IReadOnlyList<string> Reconcile(IReadOnlyDictionary<string, IReadOnlyList<string>> producedKeys)
    {
        var problems = new List<string>();
        foreach (var (source, keys) in producedKeys)
        {
            if (!_declared.TryGetValue(source, out var declared))
            {
                problems.Add($"{source}：沒有宣告任何能力，卻生產了 {keys.Count} 把鍵");
                continue;
            }
            foreach (string key in keys)
            {
                if (!declared.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                    problems.Add($"{source}：生產了未宣告的鍵 {key}");
            }
        }
        foreach (var (source, declared) in _declared)
        {
            if (!producedKeys.ContainsKey(source))
                problems.Add($"{source}：宣告了 {declared.Count} 個欄位，但這一輪沒有任何生產紀錄");
        }
        return problems;
    }

    /// <summary>產生 <c>src.capability</c> 的事實。</summary>
    public HardwareFact ToFact(DateTimeOffset at, IReadOnlyList<string> problems)
    {
        if (_declared.Count == 0)
            return new HardwareFact("src.capability", "來源接縫", "來源能力矩陣",
                "沒有任何來源宣告能力", "條", "來源能力矩陣", FactTrustLevel.Derived, false, at,
                0, FactAvailability.NotApplicable, "本次沒有登記任何來源能力");

        int total = _declared.Values.Sum(s => s.Count);
        string value = problems.Count == 0
            ? $"{_declared.Count} 條來源、{total} 個宣告欄位，與實際生產一致"
            : $"{_declared.Count} 條來源、{total} 個宣告欄位，{problems.Count} 項對不上";

        return new HardwareFact("src.capability", "來源接縫", "來源能力矩陣", value, "條",
            "來源能力矩陣", FactTrustLevel.Derived, false, at,
            _declared.Count,
            problems.Count == 0 ? FactAvailability.Present : FactAvailability.ReadError,
            problems.Count == 0 ? null : string.Join("；", problems.Take(5)));
    }
}
