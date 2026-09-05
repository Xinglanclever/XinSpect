namespace XinSpect;

/// <summary>
/// NVMe SMART／Health Information（log page 0x02）的型別化快照。
/// </summary>
/// <remarks>
/// 顯示用的資料列與驗機規則需要的是同一份數值，差別只在前者要格式化、後者要做算術。
/// 讓兩邊各自從位移取值，就是 1.9.1-B1 那個 bug 的成因（同一個檔案裡兩套位移，一套對一套錯，
/// 而且錯的那套帶了三個發佈版）。所以位移只在這裡出現一次，兩邊都從這個快照拿。
/// </remarks>
public readonly record struct NvmeHealthSnapshot(
    byte CriticalWarning,
    ushort CompositeTempKelvin,
    byte AvailableSparePercent,
    byte SpareThresholdPercent,
    byte PercentageUsed,
    ulong DataUnitsRead,
    ulong DataUnitsWritten,
    ulong HostReadCommands,
    ulong HostWriteCommands,
    ulong PowerCycles,
    ulong PowerOnHours,
    ulong UnsafeShutdowns,
    ulong MediaErrors,
    ulong ErrorLogEntries)
{
    /// <summary>綜合溫度（°C）；讀不到時為 null——不要拿 -273 這種換算殘渣充數。</summary>
    public int? CompositeTempCelsius => CompositeTempKelvin > 0 ? CompositeTempKelvin - 273 : null;

    public double DataWrittenGiB => NvmeHealth.DataUnitsToGiB(DataUnitsWritten);
    public double DataReadGiB => NvmeHealth.DataUnitsToGiB(DataUnitsRead);
}

/// <summary>健康紀錄（512 位元組）→ <see cref="NvmeHealthSnapshot"/>。純函式，零硬體相依。</summary>
public static class NvmeHealth
{
    public const int LogSize = 512;

    /// <summary>
    /// 解出整份快照；長度不足回 <c>null</c>。
    /// **不要在長度不足時回一組零**——那會讓「讀不到」與「全新的碟」看起來一模一樣。
    /// </summary>
    public static NvmeHealthSnapshot? Decode(byte[] log)
    {
        if (log is null || log.Length < LogSize) return null;

        return new NvmeHealthSnapshot(
            CriticalWarning: log[NvmeLogDecoder.OffCriticalWarning],
            CompositeTempKelvin: NvmeLogDecoder.Le16(log, NvmeLogDecoder.OffCompositeTemp),
            AvailableSparePercent: log[NvmeLogDecoder.OffAvailableSpare],
            SpareThresholdPercent: log[NvmeLogDecoder.OffSpareThreshold],
            PercentageUsed: log[NvmeLogDecoder.OffPercentageUsed],
            DataUnitsRead: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffDataUnitsRead),
            DataUnitsWritten: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffDataUnitsWritten),
            HostReadCommands: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffHostReadCommands),
            HostWriteCommands: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffHostWriteCommands),
            PowerCycles: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffPowerCycles),
            PowerOnHours: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffPowerOnHours),
            UnsafeShutdowns: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffUnsafeShutdowns),
            MediaErrors: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffMediaErrors),
            ErrorLogEntries: NvmeLogDecoder.Counter128Low(log, NvmeLogDecoder.OffErrorLogEntries));
    }

    /// <summary>
    /// Data Units 換算 GiB：規格定義一個單位＝1000 × 512 位元組（不是 1024 × 512）。
    /// </summary>
    public static double DataUnitsToGiB(ulong units) => units * 1000.0 * 512 / (1024.0 * 1024 * 1024);
}
