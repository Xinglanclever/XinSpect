namespace XinSpect;

/// <summary>
/// 來源可用性查詢接縫（ULTIMATE-1000 階段一 P1-005）。
///
/// <para>
/// <b>為什麼「現在可不可用」要是一個獨立問題：</b>本專案有一大類缺陷是
/// 「來源不可用時回了一個看起來成功的值」。要避免它，就不能只在讀取時才知道
/// 來源能不能用——必須能在<b>讀取之前</b>問一次，而且那個答案要能說明
/// 「不可用的原因是什麼」。
/// </para>
///
/// <para>
/// <b>可用性 ≠ 六態。</b>六態是「事實的狀態」（讀完之後），可用性是「來源的狀態」
/// （讀之前）。兩者刻意分開：一個來源可以「可用」而這次讀取失敗（裝置拔了），
/// 也可以「不可用」但仍有上次的快取值（那筆事實是 <see cref="FactAvailability.Unknown"/>，
/// 不是 <see cref="FactAvailability.Present"/>）。
/// </para>
/// </summary>
public interface ISourceAvailability
{
    /// <summary>來源名稱。與 <see cref="IReadOnlyScanner.BackendName"/> 同一組命名。</summary>
    string SourceName { get; }

    /// <summary>
    /// 現在可不可用。<b>不得拋出例外</b>——查詢可用性本身就不該成為新的失敗源。
    /// </summary>
    SourceAvailability QueryAvailability();
}

/// <summary>
/// 一個來源當下的可用性。這是<b>來源</b>的狀態，不是事實的狀態。
/// </summary>
/// <param name="Available">現在能不能讀。</param>
/// <param name="Reason">為什麼是這個答案。不可用時必填。</param>
/// <param name="NeedsPrivilege">是否「提權後可得」——這一項要能被使用者看見，因為它可以被解決。</param>
public sealed record SourceAvailability(
    bool Available,
    string Reason,
    bool NeedsPrivilege = false)
{
    public static SourceAvailability Yes(string reason) => new(true, reason);

    /// <summary>不可用，但這是環境事實（例如本機沒有這顆晶片），不是錯誤。</summary>
    public static SourceAvailability NotSupported(string reason) => new(false, reason);

    /// <summary>不可用，而且是因為權限不足——提權後可得，這一類要明說。</summary>
    public static SourceAvailability Privilege(string reason) => new(false, reason, NeedsPrivilege: true);

    /// <summary>不可用，且是暫時性的（裝置忙碌、驅動未載入）。</summary>
    public static SourceAvailability TemporarilyUnavailable(string reason) => new(false, reason);

    /// <summary>對應的事實可用性。可用 → <see cref="FactAvailability.Present"/>；其餘逐一對映。</summary>
    public FactAvailability ToFactAvailability() =>
        Available ? FactAvailability.Present
                  : NeedsPrivilege ? FactAvailability.InsufficientPrivilege
                                   : FactAvailability.NotSupported;
}

/// <summary>
/// 把「來源可用性」變成事實鍵 <c>src.available</c>（P1-005 的輸出鍵）。
///
/// <para>
/// 注意這一項<b>不是</b>把可用性當成 <c>Present</c>／<c>ReadError</c> 來回報，
/// 而是把「來源自己說它可不可用」這件事本身當成一筆陳述輸出。
/// 兩者的差別在於：前者會讓「來源不可用」看起來像「讀取失敗」，
/// 而後者讓它看起來像「來源說它現在不能讀」——後者才是實情。
/// </para>
/// </summary>
public static class SourceAvailabilityFact
{
    public const string Key = "src.available";

    public static HardwareFact ToFact(DateTimeOffset at, ISourceAvailability source)
    {
        SourceAvailability a;
        try
        {
            a = source.QueryAvailability();
        }
        catch (Exception ex)
        {
            // 查詢可用性本身失敗：這仍然是「不可用」，但要說明失敗的是查詢這件事。
            return new HardwareFact(Key, "來源接縫", $"來源可用性：{source.SourceName}",
                "可用性查詢失敗", "", "來源可用性接縫", FactTrustLevel.Derived, false, at,
                null, FactAvailability.ReadError, ex.GetType().Name);
        }

        string value = a.Available
            ? "可用"
            : a.NeedsPrivilege ? "不可用（權限不足，提權後可得）"
                               : "不可用";
        return new HardwareFact(Key, "來源接縫", $"來源可用性：{source.SourceName}",
            value, "", "來源可用性接縫", FactTrustLevel.Derived, false, at,
            a.Available ? 1 : 0, a.ToFactAvailability(), a.Available ? null : a.Reason);
    }
}
