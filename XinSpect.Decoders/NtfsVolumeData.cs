using System.Buffers.Binary;

namespace XinSpect;

/// <summary>FSCTL_GET_NTFS_VOLUME_DATA（v1，96 位元組）的欄位。</summary>
public sealed record NtfsVolumeData(
    ulong VolumeSerialNumber,
    long NumberSectors,
    long TotalClusters,
    long FreeClusters,
    uint BytesPerSector,
    uint BytesPerCluster,
    uint BytesPerFileRecordSegment,
    uint ClustersPerFileRecordSegment,
    long MftValidDataLength,
    long MftStartLcn,
    long Mft2StartLcn,
    long MftZoneStart,
    long MftZoneEnd);

/// <summary>USN 日誌查詢結果。Version：V0（48 位元組）／V1（56＋，含 MaxUsn）。</summary>
public sealed record UsnJournalInfo(
    string Version,
    ulong UsnJournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long? MaxUsn,
    long MaximumSize,
    long AllocationDelta);

/// <summary>
/// 卷冊層 NTFS 資料解碼器（Vol 2 批次 B／FS-006＋FS-014）。純位元組解析，不碰裝置。
/// <para>
/// 版面對準 Windows SDK <c>winioctl.h</c>（tpn 鏡像，10.0.16299，2026-10-11 抓取）：
/// <see cref="NtfsVolumeData"/> ＝ NTFS_VOLUME_DATA_BUFFER v1（5×LARGE_INTEGER＋4×DWORD＋5×LARGE_INTEGER＝96 位元組，
/// 欄序照原文）；<see cref="UsnJournalInfo"/> ＝ USN_JOURNAL_DATA_V0（48）與 V1（56＋：第 32 位元組起多一個 MaxUsn，
/// MaximumSize 往後移 8）。新於 V1 的版型以 V1 字首解析並在 Version 裡註明。
/// </para>
/// <para><b>界線</b>：記錄數是 MftValidDataLength ÷ BytesPerFileRecordSegment 的<b>估算</b>——沒有逐條解析 MFT；
/// USN 跨度是 FirstUsn→NextUsn 的差值（日誌存在性與規模），不是內容。</para>
/// </summary>
public static class NtfsVolumeDecoder
{
    /// <summary>NTFS_VOLUME_DATA_BUFFER v1 的位元組長度。</summary>
    public const int VolumeDataBytes = 96;

    /// <summary>解析 FSCTL_GET_NTFS_VOLUME_DATA 輸出。長度不足回 null（呼叫端如實標讀取錯誤）。</summary>
    [SpecRef("Windows SDK winioctl.h（tpn 鏡像 10.0.16299，2026-10-11 抓取）NTFS_VOLUME_DATA_BUFFER："
        + "VolumeSerialNumber i64／NumberSectors i64／TotalClusters i64／FreeClusters i64／TotalReserved i64／"
        + "BytesPerSector u32／BytesPerCluster u32／BytesPerFileRecordSegment u32／ClustersPerFileRecordSegment u32／"
        + "MftValidDataLength i64／MftStartLcn i64／Mft2StartLcn i64／MftZoneStart i64／MftZoneEnd i64＝96 位元組；"
        + "小端序。界線＝欄位序照原文，不猜延伸緩衝區")]
    public static NtfsVolumeData? ParseVolumeData(byte[] out_)
    {
        if (out_.Length < VolumeDataBytes) return null;
        int i = 0;
        ulong serial = BinaryPrimitives.ReadUInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long sectors = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long totalClusters = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long freeClusters = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        i += 8; // TotalReserved（本輪不顯示，如實略過）
        uint bytesPerSector = BinaryPrimitives.ReadUInt32LittleEndian(out_.AsSpan(i)); i += 4;
        uint bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(out_.AsSpan(i)); i += 4;
        uint bytesPerFr = BinaryPrimitives.ReadUInt32LittleEndian(out_.AsSpan(i)); i += 4;
        uint clustersPerFr = BinaryPrimitives.ReadUInt32LittleEndian(out_.AsSpan(i)); i += 4;
        long mftLen = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long mftStart = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long mft2Start = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long zoneStart = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long zoneEnd = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i));
        return new NtfsVolumeData(serial, sectors, totalClusters, freeClusters, bytesPerSector, bytesPerCluster,
            bytesPerFr, clustersPerFr, mftLen, mftStart, mft2Start, zoneStart, zoneEnd);
    }

    /// <summary>MFT 記錄數估算（長度÷記錄大小；除零回 null）。估算不是解析——界線在呼叫端值裡講明。</summary>
    [SpecRef("同 NTFS_VOLUME_DATA_BUFFER：MftValidDataLength／BytesPerFileRecordSegment 的商＝記錄數上界估算"
        + "（winioctl.h 原文欄義；無逐條解析）")]
    public static long? EstimateRecords(NtfsVolumeData v) =>
        v.BytesPerFileRecordSegment == 0 ? null : v.MftValidDataLength / v.BytesPerFileRecordSegment;

    /// <summary>解析 FSCTL_QUERY_USN_JOURNAL 輸出：48 位元組＝V0，≥56＝V1 字首（第 32 位元組多 MaxUsn）。不足回 null。</summary>
    [SpecRef("Windows SDK winioctl.h（tpn 鏡像 10.0.16299，2026-10-11 抓取）USN_JOURNAL_DATA_V0（48：JournalID/First/Next/"
        + "LowestValid/MaximumSize/AllocationDelta 六個 8 位元組欄）與 USN_JOURNAL_DATA_V1（56＋：LowestValid 後多 MaxUsn，"
        + "MaximumSize／AllocationDelta 往後移 8）；小端序")]
    public static UsnJournalInfo? ParseUsnJournal(byte[] out_)
    {
        if (out_.Length < 48) return null;
        int i = 0;
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long first = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long next = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long lowest = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        if (out_.Length >= 56)
        {
            long maxUsn = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
            long max = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
            long delta = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i));
            string ver = out_.Length == 56 ? "V1"
                : out_.Length >= 80 ? "V2"   // V2＝V1 七個欄位＋Flags＋RangeTrack 兩欄（winioctl.h 原文字首一致）
                : "V1 字首（輸出 " + out_.Length + " 位元組）";
            return new UsnJournalInfo(ver, id, first, next, lowest, maxUsn, max, delta);
        }
        long maxSize = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i)); i += 8;
        long alloc = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(i));
        return new UsnJournalInfo("V0", id, first, next, lowest, null, maxSize, alloc);
    }
}
