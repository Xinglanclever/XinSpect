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

/// <summary>USN 日誌查詢結果；保留 V0/V1/V2 共用的七個 8-byte 欄位（含 MaxUsn）。</summary>
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
/// 欄序照原文）；<see cref="UsnJournalInfo"/> 對準本機 Windows SDK 10.0.28000.0：
/// USN_JOURNAL_DATA_V0＝56 bytes，V1＝64 bytes（新增兩個 WORD，含結構尾端對齊），V2＝80 bytes。
/// 三版前 56 bytes 相同；只輸出共用欄位，版本延伸欄位不在本模型內。
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

    /// <summary>SDK USN_JOURNAL_DATA_V0 的完整最小長度；MaxUsn 自 V0 即存在。</summary>
    public const int UsnJournalV0Bytes = 56;
    public const int UsnJournalV1Bytes = 64;
    public const int UsnJournalV2Bytes = 80;

    /// <summary>只接受完整已知 SDK 結構長度；截斷、部分延伸及未知版型回 null，不猜版本。</summary>
    [SpecRef("Windows SDK 10.0.28000.0 um/winioctl.h:12233-12274 USN_JOURNAL_DATA_V0/V1/V2："
        + "V0=56 bytes，JournalID@0/First@8/Next@16/LowestValid@24/MaxUsn@32/MaximumSize@40/AllocationDelta@48；"
        + "V1=64 bytes，MinSupportedMajorVersion@56/MaxSupportedMajorVersion@58（尾端對齊）；"
        + "V2=80 bytes，另有Flags@60/RangeTrackChunkSize@64/RangeTrackFileSizeThreshold@72。"
        + "本模型僅輸出三版共用的56-byte前綴；小端序，完整已知長度以外拒解")]
    public static UsnJournalInfo? ParseUsnJournal(byte[] out_)
    {
        string? version = out_.Length switch
        {
            UsnJournalV0Bytes => "V0",
            UsnJournalV1Bytes => "V1",
            UsnJournalV2Bytes => "V2",
            _ => null
        };
        if (version is null) return null;
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(out_.AsSpan(0));
        long first = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(8));
        long next = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(16));
        long lowest = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(24));
        long maxUsn = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(32));
        long maxSize = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(40));
        long allocationDelta = BinaryPrimitives.ReadInt64LittleEndian(out_.AsSpan(48));
        return new UsnJournalInfo(version, id, first, next, lowest, maxUsn, maxSize, allocationDelta);
    }
}
