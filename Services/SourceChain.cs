namespace XinSpect;

/// <summary>
/// 來源優先序與取代鏈（ULTIMATE-1000 階段一 P1-007）。
///
/// <para>
/// <b>要解決的問題：</b>同一件事常常有多條來源（例如 CPU 頻率可以從 MSR、從
/// 效能計數器、從 WMI 拿到）。目前各服務各自決定用哪一條，於是「為什麼這台機器
/// 用了備援來源」在輸出裡看不出來。
/// </para>
///
/// <para>
/// <b>這一項要的是：裁決鏈可以被宣告，而且裁決過程可以被看見。</b>
/// 不是「自動選最好的」——自動選會變成一個隱藏判斷，而隱藏判斷正是本專案
/// 最不想要的東西（陳述非判決）。
/// </para>
///
/// <para>
/// 鏈的形狀是 <b>主力 → 備援 → 三態</b>：一路試下去，全都不行時回三態，
/// <b>不回預設值</b>。每一段的結果都留在 <see cref="SourceChainOutcome.Attempts"/> 裡。
/// </para>
/// </summary>
public sealed class SourceChain<T>
{
    private readonly List<(string Name, Func<T?> Read, Func<SourceAvailability>? Availability)> _links = [];

    /// <summary>鏈上目前有幾段。</summary>
    public int LinkCount => _links.Count;

    /// <summary>
    /// 加一段。順序就是優先序（先加的先試）。
    /// <paramref name="availability"/> 可省略——省略時直接試讀，讀不到就算這一段失敗。
    /// </summary>
    public SourceChain<T> Then(string name, Func<T?> read, Func<SourceAvailability>? availability = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        _links.Add((name, read, availability));
        return this;
    }

    /// <summary>
    /// 依序嘗試各段，回傳第一段拿到的值，並留下每一段的嘗試紀錄。
    ///
    /// <para>
    /// <b>先問可用性再讀。</b>不可用的來源會被跳過並記錄原因，而不是「試了才知道」——
    /// 這樣紀錄才能區分「這台機器沒有這條來源」與「有這條來源但這次讀失敗」。
    /// </para>
    /// </summary>
    public SourceChainOutcome<T> Resolve()
    {
        var attempts = new List<SourceAttempt>();
        foreach (var (name, read, availability) in _links)
        {
            if (availability is not null)
            {
                SourceAvailability a;
                try { a = availability(); }
                catch (Exception ex)
                {
                    attempts.Add(new SourceAttempt(name, false, null, $"可用性查詢失敗：{ex.GetType().Name}"));
                    continue;
                }
                if (!a.Available)
                {
                    attempts.Add(new SourceAttempt(name, false, null, a.Reason));
                    continue;
                }
            }

            try
            {
                T? value = read();
                if (value is null)
                {
                    attempts.Add(new SourceAttempt(name, false, null, "讀取回傳空值"));
                    continue;
                }
                attempts.Add(new SourceAttempt(name, true, value, null));
                return new SourceChainOutcome<T>(value, name, attempts);
            }
            catch (Exception ex)
            {
                attempts.Add(new SourceAttempt(name, false, null, ex.GetType().Name));
            }
        }

        return new SourceChainOutcome<T>(default, null, attempts);
    }
}

/// <summary>鏈上一段的嘗試紀錄。<see cref="Succeeded"/> 為 false 時 <see cref="Reason"/> 必填。</summary>
public sealed record SourceAttempt(string SourceName, bool Succeeded, object? Value, string? Reason);

/// <summary>
/// 裁決的結果。<see cref="WinningSource"/> 為 null 表示整條鏈都沒成功——
/// 這時呼叫端<b>必須</b>輸出三態，不得用預設值頂替。
/// </summary>
public sealed record SourceChainOutcome<T>(
    T? Value,
    string? WinningSource,
    IReadOnlyList<SourceAttempt> Attempts)
{
    public bool Resolved => WinningSource is not null;

    /// <summary>
    /// 產生 <c>src.chain</c> 的事實。內容是「用了哪一段、其餘為什麼沒用」，
    /// 不是「值是什麼」——值由呼叫端自己輸出。
    /// </summary>
    public HardwareFact ToFact(DateTimeOffset at, string subject)
    {
        string value;
        FactAvailability availability;
        string? reason;

        if (Resolved)
        {
            var skipped = Attempts.Where(a => !a.Succeeded).ToList();
            value = skipped.Count == 0
                ? $"{subject}：使用主力來源「{WinningSource}」"
                : $"{subject}：使用「{WinningSource}」；{string.Join("、", skipped.Select(s => $"{s.SourceName}不可用（{s.Reason}）"))}";
            availability = FactAvailability.Present;
            reason = null;
        }
        else if (Attempts.Count == 0)
        {
            value = $"{subject}：沒有可用的來源";
            availability = FactAvailability.NotApplicable;
            reason = "這條鏈沒有註冊任何來源";
        }
        else
        {
            value = $"{subject}：全部 {Attempts.Count} 段都不可用";
            availability = FactAvailability.ReadError;
            reason = string.Join("、", Attempts.Select(a => $"{a.SourceName}：{a.Reason}"));
        }

        return new HardwareFact("src.chain", "來源接縫", $"來源裁決：{subject}",
            value, "", "來源裁決鏈", FactTrustLevel.Derived, false, at,
            Resolved ? Attempts.Count(a => a.Succeeded) : 0, availability, reason);
    }
}
