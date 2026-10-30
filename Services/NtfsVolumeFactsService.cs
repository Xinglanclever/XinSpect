using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>卷冊層查詢的結果：輸出緩衝與 Win32 錯誤碼。Data 為 null 時 Error 有值。</summary>
public sealed record VolumeProbeResult(byte[]? Data, uint Error);

/// <summary>系統卷冊（C:）的注入式查詢接縫：生產走 CreateFileW＋DeviceIoControl，測試餵假結果。</summary>
public interface IVolumeProbe
{
    /// <summary>對系統卷冊發出一個 FSCTL，回輸出緩衝與錯誤碼。開檔失敗回 (null, 開檔錯誤碼)。</summary>
    VolumeProbeResult Query(uint fsctl, uint minOutputBytes);
}

/// <summary>生產通路：唯讀開啟 \\.\C: 後 DeviceIoControl，用完即關。</summary>
public sealed class SystemVolumeProbe : IVolumeProbe
{
    public const uint GenericRead = 0x80000000;
    public const uint ShareReadWrite = 0x00000003;
    public const uint OpenExisting = 3;
    public const uint InvalidHandle = 0xFFFFFFFF;

    /// <summary>FSCTL_GET_NTFS_VOLUME_DATA＝CTL_CODE(FILE_DEVICE_FILE_SYSTEM,25,METHOD_BUFFERED,FILE_ANY_ACCESS)。</summary>
    public const uint FsctlGetNtfsVolumeData = 0x00090064;

    /// <summary>FSCTL_QUERY_USN_JOURNAL＝CTL_CODE(FILE_DEVICE_FILE_SYSTEM,61,METHOD_BUFFERED,FILE_ANY_ACCESS)。</summary>
    public const uint FsctlQueryUsnJournal = 0x000900F4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern uint CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(uint handle, uint code, IntPtr inBuf, uint inSize,
        byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(uint handle);

    /// <summary>查詢目標：系統卷冊。多卷冊屬明示觸發（值裡明說）。</summary>
    public static string SystemVolume => @"\\.\C:";

    public VolumeProbeResult Query(uint fsctl, uint minOutputBytes)
    {
        uint handle = CreateFileW(SystemVolume, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == InvalidHandle)
            return new VolumeProbeResult(null, (uint)new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode);
        try
        {
            var buf = new byte[Math.Max(minOutputBytes, 256)];
            if (!DeviceIoControl(handle, fsctl, IntPtr.Zero, 0, buf, (uint)buf.Length, out uint returned, IntPtr.Zero))
                return new VolumeProbeResult(null, (uint)new Win32Exception(Marshal.GetLastWin32Error()).NativeErrorCode);
            return new VolumeProbeResult(buf[..(int)returned], 0);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}

/// <summary>
/// NTFS 卷冊層事實服務（Vol 2 批次 B／FS-006 $MFT 統計＋FS-014 USN 篩選器狀態）。
/// <para>
/// 對系統卷冊（C:）發兩個<b>唯讀</b> FSCTL（winioctl.h 出處已核），解析交
/// <see cref="NtfsVolumeDecoder"/>。<b>界線</b>：MFT 記錄數是長度÷記錄大小的估算（沒有逐條解析）；
/// USN 只報日誌的存在性與規模（內容讀回屬 FS-005 明示觸發，本版未做）；只掃系統碟（多卷冊屬明示觸發）；
/// 卷序號是機器識別資訊，刻意不列入值。
/// </para>
/// </summary>
public static class NtfsVolumeFactsService
{
    public const string Category = "檔案鑑識";
    public const string MftKey = "fs.mft.stat";
    public const string UsnKey = "fs.usn.state";

    /// <summary>ERROR_JOURNAL_DELETE_IN_PROGRESS（winerror.h）。</summary>
    public const uint ErrorJournalDeleteInProgress = 1178;

    /// <summary>ERROR_JOURNAL_NOT_ACTIVE（winerror.h）。</summary>
    public const uint ErrorJournalNotActive = 1179;

    private const string Source = "系統卷冊 FSCTL（唯讀）＋金標解碼器";

    public static IReadOnlyList<HardwareFact> CollectForThisMachine(DateTimeOffset at, IVolumeProbe? probe = null) =>
        Collect(at, probe ?? new SystemVolumeProbe());

    /// <summary>
    /// 同 <see cref="CollectForThisMachine(DateTimeOffset, IVolumeProbe)"/>，但把<b>這一次</b>解析到的
    /// 卷冊資料交出去，讓開機磁區那一側（<see cref="NtfsBootSectorFactsService"/>）不必再發一次 FSCTL。
    /// </summary>
    /// <remarks>
    /// 為什麼要這個 <c>out</c>：對帳的兩邊必須是<b>同一輪的同一份資料</b>。
    /// 若兩支服務各發一次 <c>FSCTL_GET_NTFS_VOLUME_DATA</c>，兩次之間卷冊可能剛好被掛載／卸載，
    /// 於是「不一致」可能只是兩次讀取之間世界變了——那是自己製造的假訊號。
    /// </remarks>
    public static IReadOnlyList<HardwareFact> CollectForThisMachine(
        DateTimeOffset at, IVolumeProbe? probe, out NtfsVolumeData? driverSide) =>
        Collect(at, probe ?? new SystemVolumeProbe(), out driverSide);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IVolumeProbe probe) =>
        Collect(at, probe, out _);

    public static IReadOnlyList<HardwareFact> Collect(
        DateTimeOffset at, IVolumeProbe probe, out NtfsVolumeData? driverSide)
    {
        var mft = CollectMft(at, probe, out driverSide);
        var usn = CollectUsn(at, probe);
        return mft.Concat(usn).ToList();
    }

    private static IReadOnlyList<HardwareFact> CollectMft(
        DateTimeOffset at, IVolumeProbe probe, out NtfsVolumeData? driverSide)
    {
        driverSide = null;
        var r = probe.Query(SystemVolumeProbe.FsctlGetNtfsVolumeData, NtfsVolumeDecoder.VolumeDataBytes);
        if (r.Data is null)
            return [MftUnavailable(at, r.Error, "MFT 卷冊資料讀取失敗")];

        var v = NtfsVolumeDecoder.ParseVolumeData(r.Data);
        if (v is null)
            return [MftUnavailable(at, 0, $"輸出僅 {r.Data.Length} 位元組（< {NtfsVolumeDecoder.VolumeDataBytes}）——不猜欄位")];
        driverSide = v;

        long? records = NtfsVolumeDecoder.EstimateRecords(v);
        double usedPct = v.TotalClusters > 0 ? 100.0 * (v.TotalClusters - v.FreeClusters) / v.TotalClusters : 0;
        string value = $"系統碟 MFT 有效長度 {v.MftValidDataLength / 1048576.0:N1} MiB・記錄大小 {v.BytesPerFileRecordSegment} B"
            + "・記錄數≈" + (records is { } rec ? rec.ToString("N0") + "（長÷大小，估算未逐條解析）" : "無法估算（記錄大小為 0——如實）")
            + $"・叢 總 {v.TotalClusters:N0}／自由 {v.FreeClusters:N0}（已用 {usedPct:F1}%）"
            + $"・扇區 {v.BytesPerSector} B／簇 {v.BytesPerCluster} B・卷序號刻意不列（機器識別資訊）"
            + "・界線：統計是卷冊層事實，記錄數是估算；只掃系統碟（多卷冊屬明示觸發）；MFT 逐條解析屬明示觸發，本版未做";
        return
        [
            new HardwareFact(MftKey, Category, "NTFS MFT 卷冊統計", value, "條", Source,
                FactTrustLevel.Derived, false, at, records),
        ];
    }

    private static HardwareFact MftUnavailable(DateTimeOffset at, uint error, string why) =>
        new(MftKey, Category, "NTFS MFT 卷冊統計", "", "條", Source,
            FactTrustLevel.Unknown, false, at, null,
            error == 5 ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError,
            why + (error == 0 ? "" : "（Win32 錯誤 " + error + "）") + "——讀不到不等於沒有");

    private static IReadOnlyList<HardwareFact> CollectUsn(DateTimeOffset at, IVolumeProbe probe)
    {
        var r = probe.Query(SystemVolumeProbe.FsctlQueryUsnJournal, 48);
        if (r.Data is null)
        {
            if (r.Error == ErrorJournalNotActive)
                return
                [
                    new HardwareFact(UsnKey, Category, "USN 變更日誌狀態", "", "", Source,
                        FactTrustLevel.Derived, false, at, null, FactAvailability.NotApplicable,
                        "系統碟 USN 日誌未啟用（錯誤 " + ErrorJournalNotActive + " ERROR_JOURNAL_NOT_ACTIVE）——如實；"
                        + "不是「沒有檔案變更」"),
                ];
            if (r.Error == ErrorJournalDeleteInProgress)
                return
                [
                    new HardwareFact(UsnKey, Category, "USN 變更日誌狀態", "", "", Source,
                        FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                        "USN 日誌刪除進行中（錯誤 " + ErrorJournalDeleteInProgress + "）——過渡狀態，重查才知結果"),
                ];
            return
            [
                new HardwareFact(UsnKey, Category, "USN 變更日誌狀態", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null,
                    r.Error == 5 ? FactAvailability.InsufficientPrivilege : FactAvailability.ReadError,
                    "USN 日誌查詢失敗（Win32 錯誤 " + r.Error + "）——讀不到不等於沒有"),
            ];
        }

        var u = NtfsVolumeDecoder.ParseUsnJournal(r.Data);
        if (u is null)
            return
            [
                new HardwareFact(UsnKey, Category, "USN 變更日誌狀態", "", "", Source,
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError,
                    $"輸出僅 {r.Data.Length} 位元組（< 48）——不猜欄位"),
            ];

        long span = u.NextUsn - u.FirstUsn;
        string value = $"系統碟 USN 日誌啟用（{u.Version}）：ID 0x{u.UsnJournalId:X}・FirstUsn {u.FirstUsn}・NextUsn {u.NextUsn}"
            + "・跨度≈" + (span / 1048576.0).ToString("N1") + " MiB（Next−First）"
            + "・上限 " + (u.MaximumSize / 1048576.0).ToString("N0") + " MiB・配置增量 " + (u.AllocationDelta / 1048576.0).ToString("N0") + " MiB"
            + (u.MaxUsn is { } m ? "・MaxUsn " + m : "")
            + "・界線：這是日誌的存在性與規模，不是內容；內容讀回屬 FS-005 明示觸發，本版未做";
        return
        [
            new HardwareFact(UsnKey, Category, "USN 變更日誌狀態", value, "", Source,
                FactTrustLevel.Derived, false, at, span),
        ];
    }
}
