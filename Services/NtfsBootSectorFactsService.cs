using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// 開機磁區讀取的結果。<c>Data</c> 為 null 時看 <c>Error</c>：非 0 是 Win32 錯誤碼，
/// 0 且 <c>BytesRead</c> 小於 512 則是<b>短讀</b>——兩者要分開講，因為「打不開」與「讀不滿」
/// 的處置完全不同。
/// </summary>
public sealed record BootSectorReadResult(byte[]? Data, uint Error, int BytesRead = 0);

/// <summary>
/// 系統卷冊開機磁區的注入式讀取接縫：生產路徑唯讀開啟 <c>\\.\C:</c> 後讀位移 0 的 512 位元組，
/// 測試餵合成位元組，不碰磁碟。
/// </summary>
public interface IBootSectorProbe
{
    BootSectorReadResult Read();
}

/// <summary>
/// 生產通路：唯讀開啟 <c>\\.\C:</c>，從位移 0 讀一個磁區，用完即關。
/// </summary>
/// <remarks>
/// 與 <see cref="SystemVolumeProbe"/> 一樣只碰系統碟（多卷冊屬明示觸發），
/// 但走的是 <c>ReadFile</c> 不是 <c>DeviceIoControl</c>——這一條要的是<b>磁碟上寫的字</b>，
/// 不是驅動對它的看法。
/// </remarks>
public sealed class SystemBootSectorProbe : IBootSectorProbe
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    /// <summary>查詢目標：系統卷冊。與 <see cref="SystemVolumeProbe.SystemVolume"/> 同一個裝置。</summary>
    public static string SystemVolume => @"\\.\C:";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint bytesToRead,
        out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public BootSectorReadResult Read()
    {
        IntPtr handle = CreateFileW(SystemVolume, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
            return new BootSectorReadResult(null, (uint)new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode);
        try
        {
            var buffer = new byte[NtfsBootSectorDecoder.SectorBytes];
            if (!ReadFile(handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
                return new BootSectorReadResult(null, (uint)new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode);
            return read == buffer.Length
                ? new BootSectorReadResult(buffer, 0, (int)read)
                : new BootSectorReadResult(null, 0, (int)read);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>
/// NTFS 開機磁區的 BPB 事實（U10 第三圈 FS-0001）與 MFT／MFTMirr 起點事實（FS-0002）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這兩把鍵存在的理由是同一個：既有事實只有「驅動怎麼說」這一邊。</b>
/// <see cref="NtfsVolumeFactsService"/> 讀的是 <c>FSCTL_GET_NTFS_VOLUME_DATA</c>——
/// 那是 Windows 解析後的看法；而開機磁區是<b>磁碟自己寫的字</b>。
/// 兩者是同一件事的兩個來源，於是「一致」才有意義、「不一致」才抓得到。
/// </para>
/// <para>
/// <b>界線（寫在值裡，不是寫在這裡就算）：</b>只讀磁區 0——<b>備份開機磁區在卷尾，本版未讀</b>，
/// 所以本服務只能說「開機磁區這樣說」，不能說「兩個開機磁區一致」；
/// 只讀 BPB，<b>不讀 MFT 內容</b>（那是逐條解析，屬明示觸發）；
/// <b>卷序號刻意不列</b>（與 <c>fs.mft.stat</c> 同一條界線：機器識別資訊）——
/// 但仍會拿它跟驅動端比對，只印「一致／不一致」不印值。
/// </para>
/// </remarks>
public static class NtfsBootSectorFactsService
{
    public const string Category = "檔案鑑識";
    public const string BpbKey = "fs.ntfs.bpb";
    public const string MftLayoutKey = "fs.ntfs.mft.layout";

    private const string Source = "系統卷冊開機磁區（磁區 0，唯讀）＋NTFS BPB 解碼";

    /// <summary>沒有驅動端資料可比對時，MFT 位置那把鍵的理由。</summary>
    private const string NoDriverSide = "驅動端 FSCTL_GET_NTFS_VOLUME_DATA 這一輪讀不到，"
        + "只有開機磁區一邊——單邊不叫對帳，所以這裡不給「一致」也不給「不一致」";

    public static IReadOnlyList<HardwareFact> CollectForThisMachine(
        DateTimeOffset at, IBootSectorProbe? probe = null, NtfsVolumeData? driverSide = null) =>
        Collect(at, (probe ?? new SystemBootSectorProbe()).Read(), driverSide);

    public static IReadOnlyList<HardwareFact> Collect(
        DateTimeOffset at, BootSectorReadResult read, NtfsVolumeData? driverSide)
    {
        if (read.Data is null)
        {
            return read.Error != 0
                ? [
                    Unreadable(BpbKey, "NTFS 開機磁區 BPB", at, read.Error),
                    Unreadable(MftLayoutKey, "NTFS MFT／MFTMirr 起點", at, read.Error),
                ]
                : [
                    ShortRead(BpbKey, "NTFS 開機磁區 BPB", at, read.BytesRead),
                    ShortRead(MftLayoutKey, "NTFS MFT／MFTMirr 起點", at, read.BytesRead),
                ];
        }

        var info = NtfsBootSectorDecoder.Parse(read.Data);
        if (info is null)
            return
            [
                TooShort(BpbKey, "NTFS 開機磁區 BPB", at, read.Data.Length),
                TooShort(MftLayoutKey, "NTFS MFT／MFTMirr 起點", at, read.Data.Length),
            ];

        if (!info.OemIdValid)
            return
            [
                NotNtfs(BpbKey, "NTFS 開機磁區 BPB", at, info.OemId),
                NotNtfs(MftLayoutKey, "NTFS MFT／MFTMirr 起點", at, info.OemId),
            ];

        return [BpbFact(info, at), MftLayoutFact(info, driverSide, at)];
    }

    // ── FS-0001：BPB 逐欄 ────────────────────────────────────────────────

    private static HardwareFact BpbFact(NtfsBootSectorInfo b, DateTimeOffset at)
    {
        string cluster = b.BytesPerCluster is { } c
            ? $"{c} B"
            : $"算不出來（每扇區 {b.BytesPerSector} B×每叢 {b.SectorsPerCluster} 扇區不是合法幾何——**不乘**）";
        long? mftRecord = b.BytesPerMftRecord;
        string mftRecordText = mftRecord is { } mr
            ? $"{mr} B（號誌欄 {b.ClustersPerMftRecord}）"
            : $"算不出來（號誌欄 {b.ClustersPerMftRecord} 不在 2^9–2^16 也不對應叢大小——**不乘**）";
        string indexBuffer = b.BytesPerIndexBuffer is { } ib
            ? $"{ib} B（號誌欄 {b.ClustersPerIndexBuffer}）"
            : $"未宣告／算不出來（號誌欄 {b.ClustersPerIndexBuffer}）";

        string value =
            $"系統碟開機磁區（磁區 0，{NtfsBootSectorDecoder.SectorBytes} 位元組）：OEM `{b.OemId}`"
            + $"・每扇區 {b.BytesPerSector} B・每叢 {b.SectorsPerCluster} 扇區＝{cluster}"
            + $"・保留扇區 {b.ReservedSectors}・媒體描述符 0x{b.MediaDescriptor:X2}"
            + $"・每磁軌 {b.SectorsPerTrack} 扇區／磁頭 {b.Heads}（歷史欄位，不代表現況）"
            + $"・隱藏扇區 {b.HiddenSectors:N0}"
            + $"・總扇區 {b.TotalSectors:N0}"
            + $"・每 MFT 記錄 {mftRecordText}・每索引緩衝區 {indexBuffer}"
            + $"・結尾標記 {(b.EndMarkerValid ? "0xAA55（在）" : "**不是 0xAA55**——這不是一個完整的開機磁區，以下欄位照樣列出但可信度打折")}"
            + "・界線：只讀磁區 0（**備份開機磁區在卷尾，本版未讀**，所以這裡不能說「兩個開機磁區一致」）"
            + "・0x50 的校驗和欄本機各 NTFS 卷皆為 0，本版**不宣稱**它是哪一種校驗"
            + "・卷序號刻意不列（機器識別資訊，與 fs.mft.stat 同一條界線）";

        return new HardwareFact(BpbKey, Category, "NTFS 開機磁區 BPB（磁區 0）", value, "扇區",
            Source, FactTrustLevel.Measured, false, at, (double)b.TotalSectors);
    }

    // ── FS-0002：MFT／MFTMirr 起點＋與驅動端對帳 ─────────────────────────

    private static HardwareFact MftLayoutFact(NtfsBootSectorInfo b, NtfsVolumeData? driver, DateTimeOffset at)
    {
        string mftVerdict = driver is null
            ? string.Empty
            : (long)b.MftLcn == driver.MftStartLcn ? "一致" : "**不一致**";
        string mirrVerdict = driver is null
            ? string.Empty
            : (long)b.MftMirrLcn == driver.Mft2StartLcn ? "一致" : "**不一致**";
        string mftPair = driver is null
            ? b.MftLcn.ToString("N0")
            : b.MftLcn.ToString("N0") + "／驅動 " + driver.MftStartLcn.ToString("N0") + "（" + mftVerdict + "）";
        string mirrPair = driver is null
            ? b.MftMirrLcn.ToString("N0")
            : b.MftMirrLcn.ToString("N0") + "／驅動 " + driver.Mft2StartLcn.ToString("N0") + "（" + mirrVerdict + "）";

        // 卷內自洽：MFT 與 MFTMirr 都必須落在「這顆卷冊的叢數」之內。
        // 叢數取自開機磁區自己的總扇區÷每叢扇區——用同一個來源驗同一個來源，
        // 所以這條只能抓「自己內部矛盾」，抓不到「兩個來源一起錯」。
        string bounds;
        if (b.TotalSectors == 0 || b.BytesPerCluster is not { } bpc)
        {
            bounds = "叢數算不出來（總扇區 0 或幾何不合法）——不判在不在卷內";
        }
        else
        {
            long bytesPerCluster = (long)b.BytesPerCluster!.Value;
            ulong totalClusters = b.TotalSectors * (ulong)b.BytesPerSector / (ulong)bytesPerCluster;
            bool mftInside = b.MftLcn < totalClusters;
            bool mirrInside = b.MftMirrLcn < totalClusters;
            bounds = $"叢數 {totalClusters:N0}：MFT 起始 {(mftInside ? "在卷內" : "**在卷外**")}、"
                + $"MFTMirr 起始 {(mirrInside ? "在卷內" : "**在卷外**")}";
        }

        string serial = driver is null
            ? "無法比對（驅動端讀不到）"
            : b.VolumeSerialNumber == driver.VolumeSerialNumber
                ? "一致（值不列）"
                : "**不一致**（值不列）";

        string value = $"系統碟 MFT 起始 LCN：{mftPair}；MFTMirr 起始 LCN：{mirrPair}"
            + $"・卷序號兩來源：{serial}"
            + $"・卷內自洽：{bounds}"
            + "・界線：LCN 是叢號不是位元組位移（換算需乘每叢位元組，兩者都列在 fs.ntfs.bpb）；"
            + "兩個來源一致不代表 MFT 內容正確——那只證明「開機磁區與驅動對同一組起點有共識」，"
            + "內容正確性要靠逐條解析（屬明示觸發，本版未做）";

        if (driver is null)
            return new HardwareFact(MftLayoutKey, Category, "NTFS MFT／MFTMirr 起點", value, "LCN",
                Source, FactTrustLevel.Derived, false, at, (double)b.MftLcn,
                FactAvailability.Unknown, NoDriverSide);

        return new HardwareFact(MftLayoutKey, Category, "NTFS MFT／MFTMirr 起點", value, "LCN",
            Source, FactTrustLevel.Measured, false, at, (double)b.MftLcn);
    }

    // ── 非 Present 的三種情形 ────────────────────────────────────────────

    private static HardwareFact Unreadable(string key, string name, DateTimeOffset at, uint error) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null,
            error == 5 ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError,
            $"開機磁區讀不到（Win32 錯誤 {error}）——讀不到不等於沒有；"
            + "本鍵需要對系統卷冊的唯讀開啟權（管理員），沒有就是 InsufficientPrivilege 而不是「卷冊不存在」");

    private static HardwareFact TooShort(string key, string name, DateTimeOffset at, int bytes) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null,
            FactAvailability.ReadError,
            $"只讀到 {bytes} 位元組（< {NtfsBootSectorDecoder.SectorBytes}）——不猜欄位。"
            + "短讀在正常碟上不該發生，出現時先懷疑讀取路徑而不是卷冊");

    private static HardwareFact ShortRead(string key, string name, DateTimeOffset at, int bytes) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null,
            FactAvailability.ReadError,
            $"短讀：要求 {NtfsBootSectorDecoder.SectorBytes} 位元組、實際拿到 {bytes} 位元組（Win32 沒報錯）——"
            + "「打不開」與「讀不滿」是兩件事，這裡是後者；不拿不足的位元組去猜欄位");

    private static HardwareFact NotNtfs(string key, string name, DateTimeOffset at, string oemId) =>
        new(key, Category, name, "", "", Source, FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotApplicable,
            $"開機磁區的 OEM ID 是 `{oemId}`，不是 `NTFS    `——這兩把鍵只描述 NTFS 卷冊；"
            + "系統碟不是 NTFS 時它們不適用，而不是「讀不到」");
}
