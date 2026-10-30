using System.Text;

namespace XinSpect;

/// <summary>
/// NTFS 卷冊開機磁區（磁區 0，512 位元組）的 BPB 欄位（U10 第三圈 FS-0001／FS-0002）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有這個解碼器：</b>既有的 <see cref="NtfsVolumeDecoder"/> 讀的是<b>驅動端</b>的
/// <c>FSCTL_GET_NTFS_VOLUME_DATA</c>——那是「Windows 認為這個卷冊長什麼樣」。開機磁區是
/// <b>磁碟上自己寫的字</b>。兩者是同一件事的兩個來源，而這個專案要的正是「兩邊說的是不是同一件事」。
/// </para>
/// <para>
/// <b>欄位位移的出處</b>：Microsoft 文件「NTFS Volume Boot Record」的 BPB 與 NTFS 延伸欄位，
/// 逐欄位移寫在 <see cref="Parse"/> 的 <c>[SpecRef]</c> 裡。前 11 位元組是 x86 跳躍指令，
/// 0x54 起是開機碼——<b>兩者都不解</b>（那不是結構，是程式）。
/// </para>
/// </remarks>
public sealed record NtfsBootSectorInfo(
    string OemId,
    ushort BytesPerSector,
    byte SectorsPerCluster,
    ushort ReservedSectors,
    byte MediaDescriptor,
    ushort SectorsPerTrack,
    ushort Heads,
    uint HiddenSectors,
    ulong TotalSectors,
    ulong MftLcn,
    ulong MftMirrLcn,
    sbyte ClustersPerMftRecord,
    sbyte ClustersPerIndexBuffer,
    ulong VolumeSerialNumber,
    uint ChecksumField,
    bool EndMarkerValid)
{
    /// <summary>OEM ID 必須是 <c>"NTFS    "</c>（<c>NTFS</c> 加四個空白，共 8 位元組）。</summary>
    public bool OemIdValid => OemId == "NTFS    ";

    /// <summary>
    /// 幾何是否說得通：每扇區位元組是 512/1024/2048/4096（2 的冪），每叢扇區是 1–128（2 的冪）。
    /// 說不通就<b>不給</b>每叢位元組，而不是照乘。
    /// </summary>
    public bool GeometryValid =>
        BytesPerSector is 512 or 1024 or 2048 or 4096
        && SectorsPerCluster != 0
        && (SectorsPerCluster & (SectorsPerCluster - 1)) == 0
        && SectorsPerCluster <= 128;

    /// <summary>每叢位元組＝每扇區位元組×每叢扇區。幾何說不通時回 <c>null</c>。</summary>
    public uint? BytesPerCluster => GeometryValid ? (uint)BytesPerSector * SectorsPerCluster : null;

    /// <summary>
    /// 每筆 MFT 記錄的位元組數。<b>NTFS 的號誌約定</b>：正值＝「幾個叢」，
    /// 負值＝「2 的幾次方位元組」（<c>-10</c> 就是 1024 B）。兩者都不是就回 <c>null</c>。
    /// </summary>
    public long? BytesPerMftRecord => SignedSizeToBytes(ClustersPerMftRecord);

    /// <summary>每筆索引緩衝區的位元組數，號誌約定同 <see cref="BytesPerMftRecord"/>。</summary>
    public long? BytesPerIndexBuffer => SignedSizeToBytes(ClustersPerIndexBuffer);

    private long? SignedSizeToBytes(sbyte signed)
    {
        if (signed > 0) return BytesPerCluster is { } cluster ? (long)signed * cluster : null;
        if (signed == 0) return null;
        int exponent = -signed;
        // 合理範圍 512 B（2^9）到 64 KiB（2^16）；超出範圍當成「欄位不是這個意思」，不硬算。
        return exponent is >= 9 and <= 16 ? 1L << exponent : null;
    }
}

/// <summary>NTFS 開機磁區解碼器。純位元組解析，不碰裝置、不讀 MFT。</summary>
public static class NtfsBootSectorDecoder
{
    /// <summary>開機磁區固定 512 位元組（<c>BytesPerSector</c> 說的是磁區大小，不是這筆讀取的長度）。</summary>
    public const int SectorBytes = 512;

    /// <summary>OEM ID 的位移與長度（<c>"NTFS    "</c>）。</summary>
    public const int OemIdOffset = 0x03;
    public const int OemIdLength = 8;

    /// <summary>解析開機磁區。不足 512 位元組回 <c>null</c>（呼叫端如實標讀取錯誤，不猜欄位）。</summary>
    [SpecRef("Microsoft Docs「NTFS Volume Boot Record」（BPB＋NTFS 延伸欄位）逐欄位移："
        + "0x03 OEM ID 8B／0x0B u16 每扇區位元組／0x0D u8 每叢扇區／0x0E u16 保留扇區／"
        + "0x15 u8 媒體描述符／0x18 u16 每磁軌扇區／0x1A u16 磁頭數／0x1C u32 隱藏扇區／"
        + "0x24 u32 0x00800080（NTFS 標記）／0x28 u64 總扇區／0x30 u64 MFT 起始 LCN／"
        + "0x38 u64 MFTMirr 起始 LCN／0x40 i8 每 MFT 記錄叢數（負值＝2^|n| 位元組）／"
        + "0x41 i8 每索引緩衝區叢數／0x48 u64 卷序號／0x50 u32 校驗和欄／0x1FE u16 0xAA55 結尾標記；小端序。"
        + "界線＝只解 BPB，0x54 起的開機碼不解析；0x50 的校驗規則本版不宣稱（本機五個 NTFS 卷都是 0）")]
    public static NtfsBootSectorInfo? Parse(byte[] sector)
    {
        if (sector.Length < SectorBytes) return null;

        string oem = Encoding.ASCII.GetString(sector, OemIdOffset, OemIdLength);
        ushort bytesPerSector = BinaryPrimitivesReadU16(sector, 0x0B);
        byte sectorsPerCluster = sector[0x0D];
        ushort reservedSectors = BinaryPrimitivesReadU16(sector, 0x0E);
        byte media = sector[0x15];
        ushort sectorsPerTrack = BinaryPrimitivesReadU16(sector, 0x18);
        ushort heads = BinaryPrimitivesReadU16(sector, 0x1A);
        uint hidden = BinaryPrimitivesReadU32(sector, 0x1C);
        ulong totalSectors = BinaryPrimitivesReadU64(sector, 0x28);
        ulong mftLcn = BinaryPrimitivesReadU64(sector, 0x30);
        ulong mftMirrLcn = BinaryPrimitivesReadU64(sector, 0x38);
        sbyte clustersPerMft = unchecked((sbyte)sector[0x40]);
        sbyte clustersPerIndex = unchecked((sbyte)sector[0x41]);
        ulong serial = BinaryPrimitivesReadU64(sector, 0x48);
        uint checksum = BinaryPrimitivesReadU32(sector, 0x50);
        bool endMarker = BinaryPrimitivesReadU16(sector, 0x1FE) == 0xAA55;

        return new NtfsBootSectorInfo(oem, bytesPerSector, sectorsPerCluster, reservedSectors, media,
            sectorsPerTrack, heads, hidden, totalSectors, mftLcn, mftMirrLcn, clustersPerMft,
            clustersPerIndex, serial, checksum, endMarker);
    }

    private static ushort BinaryPrimitivesReadU16(byte[] b, int at) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));

    private static uint BinaryPrimitivesReadU32(byte[] b, int at) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));

    private static ulong BinaryPrimitivesReadU64(byte[] b, int at) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at, 8));
}
