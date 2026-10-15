namespace XinSpect;

/// <summary>
/// 離線輸入源掛載接縫（ULTIMATE-1000 階段一 P1-009）。
///
/// <para>
/// 映像檔、事件日誌、記憶體傾印這三種離線輸入，目前在本專案裡各走各的路徑。
/// 這一項要的是：<b>它們以同一種方式被宣告與掛載</b>，於是「這份報告是從哪來的」
/// 在輸出裡有一致的答案，而「這份輸入是活的還是死的」不會被混淆。
/// </para>
///
/// <para>
/// <b>關鍵區分：離線來源不是「讀不到」。</b>掛載失敗要說「掛載失敗」，
/// 掛上了但裡面沒有資料要說「沒有資料」——兩者都不是 <c>ReadError</c> 的通用說法。
/// 這一項把 <see cref="MountKind"/> 與 <see cref="MountState"/> 分開，
/// 就是為了不讓這兩件事塌成一個。
/// </para>
/// </summary>
public enum MountKind
{
    /// <summary>不是離線來源，而是活著的機器。</summary>
    Live,
    /// <summary>磁碟／分割區映像（.dd／.img／.vhd…）。</summary>
    DiskImage,
    /// <summary>事件日誌（.evtx）或其匯出。</summary>
    EventLog,
    /// <summary>記憶體傾印（.dmp／.raw）。</summary>
    MemoryDump,
    /// <summary>上傳的單一檔案，語意未定。</summary>
    LooseFile,
}

/// <summary>掛載的狀態。刻意不與 <see cref="FactAvailability"/> 合併——這是「掛載」的狀態。</summary>
public enum MountState
{
    /// <summary>掛上了或本來就是活的來源。</summary>
    Mounted,
    /// <summary>檔案不存在或路徑不可達。</summary>
    NotFound,
    /// <summary>檔案在，但格式不是這一種（例如把 .dmp 當 .img 掛）。</summary>
    KindMismatch,
    /// <summary>格式對但內容壞掉。</summary>
    Corrupt,
    /// <summary>路徑存在但讀不到（權限或鎖定）。</summary>
    Unreadable,
}

/// <summary>
/// 一個離線輸入源的掛載紀錄。
/// </summary>
/// <param name="Path">來源路徑。<b>輸出時必須去識別化</b>——路徑含使用者名稱。</param>
/// <param name="Kind">這一來源被認定成哪一種。</param>
/// <param name="State">掛載狀態。</param>
/// <param name="Detail">狀態的原因；<see cref="MountState.Mounted"/> 時可為 null。</param>
public sealed record MountRecord(string Path, MountKind Kind, MountState State, string? Detail)
{
    public bool IsUsable => State == MountState.Mounted;

    /// <summary>對應的事實可用性。掛載失敗<b>不</b>一律當成 ReadError。</summary>
    public FactAvailability ToFactAvailability() => State switch
    {
        MountState.Mounted => FactAvailability.Present,
        MountState.NotFound => FactAvailability.NotApplicable,     // 沒有這份輸入，不是錯誤
        MountState.KindMismatch => FactAvailability.NotSupported,  // 這條路徑不支援這種來源
        MountState.Unreadable => FactAvailability.InsufficientPrivilege,
        _ => FactAvailability.ReadError,
    };
}

/// <summary>
/// 掛載接縫。<b>這一項不做實際的映像解析</b>——解析是各域的事。
/// 它只回答「這份輸入是什麼、掛不掛得上」，讓上層能決定要不要往下走。
/// </summary>
public interface IOfflineInputMount
{
    /// <summary>來源路徑。呼叫端負責去識別化。</summary>
    string Path { get; }

    /// <summary>宣稱的種類。</summary>
    MountKind DeclaredKind { get; }

    /// <summary>嘗試掛載。<b>不得拋出例外</b>；失敗要變成 <see cref="MountState"/>。</summary>
    MountRecord Mount();
}

/// <summary>
/// 掛載紀錄的彙總：<c>off.mount.kind</c> 的產生器。
/// </summary>
public static class OfflineMountReport
{
    public const string Key = "off.mount.kind";

    public static HardwareFact ToFact(DateTimeOffset at, IReadOnlyList<MountRecord> mounts)
    {
        if (mounts.Count == 0)
            return new HardwareFact(Key, "離線輸入", "離線來源掛載",
                "沒有任何離線來源", "個", "掛載接縫", FactTrustLevel.Reported, false, at,
                0, FactAvailability.NotApplicable, "本次沒有掛載任何離線輸入");

        var usable = mounts.Where(m => m.IsUsable).ToList();
        var byKind = mounts.GroupBy(m => m.Kind)
            .Select(g => $"{Describe(g.Key)} {g.Count()}")
            .ToList();

        string value = usable.Count == mounts.Count
            ? $"全部掛載成功（{string.Join("、", byKind)}）"
            : $"{usable.Count}/{mounts.Count} 掛載成功（{string.Join("、", byKind)}）";

        // 只要有掛不上的，就有東西沒被看過——那必須說出來，不能只報成功的那部分。
        string? reason = usable.Count == mounts.Count
            ? null
            : string.Join("；", mounts.Where(m => !m.IsUsable)
                .Select(m => $"{Describe(m.Kind)}：{Describe(m.State)}{(m.Detail is null ? "" : $"（{m.Detail}）")}"));

        return new HardwareFact(Key, "離線輸入", "離線來源掛載", value, "個",
            "掛載接縫", FactTrustLevel.Reported, false, at,
            usable.Count,
            usable.Count == mounts.Count ? FactAvailability.Present : FactAvailability.ReadError,
            reason);
    }

    public static string Describe(MountKind k) => k switch
    {
        MountKind.Live => "活的機器",
        MountKind.DiskImage => "磁碟映像",
        MountKind.EventLog => "事件日誌",
        MountKind.MemoryDump => "記憶體傾印",
        _ => "未定檔案",
    };

    public static string Describe(MountState s) => s switch
    {
        MountState.Mounted => "已掛載",
        MountState.NotFound => "找不到",
        MountState.KindMismatch => "種類不符",
        MountState.Corrupt => "內容損壞",
        _ => "讀不到",
    };
}
