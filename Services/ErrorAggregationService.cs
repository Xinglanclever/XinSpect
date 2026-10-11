namespace XinSpect;

/// <summary>錯誤聚合的一列：事實鍵、分類、名稱、六態、原因。</summary>
public sealed record AggregatedError(string Key, string Category, string Name, string Availability, string Reason);

/// <summary>錯誤聚合結果：逐狀態計數與逐項清單。</summary>
public sealed record ErrorAggregation(
    int ReadError, int InsufficientPrivilege, int NotSupported, int Unknown, int Total,
    IReadOnlyList<AggregatedError> Items);

/// <summary>
/// 錯誤聚合（P0-07／UX-010 的資料與 CLI 面）：把全部事實裡非 Present 的三態（讀取失敗／權限不足／
/// 不支援／未知）集中成一張清單，逐項帶原因。
/// <para><b>零錯誤＝空清單（如實「全部 Present」，不是隱藏頁面）；聚合本身不做任何 I/O、不做重試——
/// 重試（唯讀重掃）屬 UI 面，本版未做（界線如實記在 ROUND 文件）。</b></para>
/// </summary>
public static class ErrorAggregationService
{
    public static ErrorAggregation Aggregate(IReadOnlyList<HardwareFact> facts)
    {
        var items = facts
            .Where(f => f.Availability is FactAvailability.ReadError
                or FactAvailability.InsufficientPrivilege
                or FactAvailability.NotSupported
                or FactAvailability.Unknown)
            .Select(f => new AggregatedError(f.Key, f.Category, f.Name, f.Availability.ToString(),
                f.UnavailableReason ?? "（原因未提供）"))
            .ToList();
        return new ErrorAggregation(
            items.Count(i => i.Availability == nameof(FactAvailability.ReadError)),
            items.Count(i => i.Availability == nameof(FactAvailability.InsufficientPrivilege)),
            items.Count(i => i.Availability == nameof(FactAvailability.NotSupported)),
            items.Count(i => i.Availability == nameof(FactAvailability.Unknown)),
            items.Count, items);
    }

    /// <summary>CLI --errors 的 JSON 輸出形狀（summary＋items；零錯誤也如實成列）。</summary>
    public static object ToPayload(ErrorAggregation a, DateTimeOffset at) => new
    {
        generatedAtUtc = at,
        scope = "errors",
        summary = new
        {
            readError = a.ReadError,
            insufficientPrivilege = a.InsufficientPrivilege,
            notSupported = a.NotSupported,
            unknown = a.Unknown,
            total = a.Total,
        },
        items = a.Items,
        note = a.Total == 0
            ? "全部事實 Present——空清單如實顯示，不是隱藏頁面"
            : "三態是環境事實不是錯誤堆疊：讀不到≠沒有、權限不足≠不支援；重試（唯讀重掃）屬 UI 面，本版未做",
    };
}
