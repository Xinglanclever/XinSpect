using System.Management;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 3 的機箱安全狀態。此靜態欄位不是機箱入侵歷史。
/// </summary>
public static class ChassisFactsService
{
    private const string Category = "主機板";

    /// <summary>SMBIOS Type 3 offset 12 的 Security Status 逐值解碼。</summary>
    public static string DescribeSecurityStatus(byte code) => code switch
    {
        1 => "其他（Other）",
        2 => "未知",
        3 => "無（None）",
        4 => "外部介面已鎖定",
        5 => "外部介面已啟用",
        _ => $"Security Status 0x{code:X2}（未收錄）",
    };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<byte[]>?>? smbiosProbe = null)
    {
        var tables = (smbiosProbe ?? new Func<IReadOnlyList<byte[]>?>(FetchSmbiosTables))();
        if (tables is null)
            return [new HardwareFact("chassis.security_status", Category, "機箱安全狀態", "", "",
                "SMBIOS Type 3", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "SMBIOS 資料讀不到，無法取得機箱安全狀態")];

        foreach (var table in tables)
        {
            if (table.Length < 13 || table[0] != 3 || table[1] < 13 || table[1] > table.Length) continue;
            byte security = table[12];
            string desc = DescribeSecurityStatus(security);
            string chassisType = table.Length > 5
                ? DescribeChassisType(table[5])
                : "";
            return [new HardwareFact("chassis.security_status", Category, "機箱安全狀態",
                $"{desc}；此欄位不記錄機箱是否曾開啟",
                "", $"SMBIOS Type 3 Security Status（機箱類型：{chassisType}）",
                FactTrustLevel.Reported, false, at, security)];
        }
        return [new HardwareFact("chassis.security_status", Category, "機箱安全狀態", "", "",
            "SMBIOS Type 3", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotSupported, "SMBIOS 表列裡沒有 Type 3（System Enclosure）結構——如實標")];
    }

    /// <summary>SMBIOS Type 3 offset 5 的 Chassis Type（僅列出常見值；其餘如實標編號）。</summary>
    public static string DescribeChassisType(byte code) => SmbiosService.ChassisTypeName((byte)(code & 0x7F));

    private static IReadOnlyList<byte[]>? FetchSmbiosTables()
    {
        try
        {
            var structs = new SmbiosService().Structs;
            return structs.Count == 0 ? null : structs.Select(s => s.Data).ToArray();
        }
        catch { return null; }
    }

    /// <summary>把整段 SMBIOS 表切開成結構（每個結構＝formatted area＋雙 null＋字串區）。</summary>
    public static List<byte[]> SplitSmbiosStructures(byte[] data)
    {
        return SmbiosParser.Parse(data).Select(s => s.Data).ToList();
    }
}

/// <summary>容量對照結果；HiddenSectors 僅為正向磁區差值，不代表已確認的 HPA 大小。</summary>
public sealed record HpaVerdict(ulong FirmwareTotalLba, ulong OsVisibleSectors, long HiddenSectors, string Summary);

/// <summary>
/// 比較 ATA IDENTIFY 當前可定址容量與 OS 容量。
/// 未讀取 READ NATIVE MAX ADDRESS/EXT，不能確認或排除 HPA；DCO 亦未探測。
/// </summary>
public static class HpaFactsService
{
    private const string Category = "儲存裝置";

    public static HpaVerdict Evaluate(ulong firmwareTotalLba, long osVisibleBytes, long logicalSectorBytes = 512)
    {
        if (firmwareTotalLba == 0 || osVisibleBytes <= 0 || logicalSectorBytes < 512)
            return new HpaVerdict(firmwareTotalLba, 0, 0, "容量資料讀不到或無效，不能確認 HPA");
        ulong osSectors = (ulong)(osVisibleBytes / logicalSectorBytes);
        long hidden = osSectors > 0 && firmwareTotalLba > osSectors
            ? (long)Math.Min(firmwareTotalLba - osSectors, (ulong)long.MaxValue)
            : 0;
        string summary = firmwareTotalLba == osSectors && osVisibleBytes % logicalSectorBytes == 0
            ? $"容量一致（IDENTIFY 與 OS 皆為 {osSectors} 磁區，每磁區 {logicalSectorBytes} bytes）；未讀取 native maximum，不能排除 HPA"
            : $"容量差異：IDENTIFY {firmwareTotalLba} 磁區、OS {osVisibleBytes} bytes（每磁區 {logicalSectorBytes} bytes）；來源差異不能確認 HPA，需另讀 native maximum";
        return new HpaVerdict(firmwareTotalLba, osSectors, hidden, summary);
    }

    public sealed record HpaDrive(string Label, ulong FirmwareTotalLba, long OsVisibleBytes, long LogicalSectorBytes = 512);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<HpaDrive>?>? probe = null)
    {
        var drives = (probe ?? new Func<IReadOnlyList<HpaDrive>?>(CollectDisks))();
        if (drives is null)
            return [new HardwareFact("hpa.dco", Category, "HPA／DCO 隱藏容量", "", "",
                "ATA IDENTIFY＋WMI Win32_DiskDrive", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError,
                "磁碟列舉失敗，無法對照容量；HPA native maximum 與 DCO 均未探測")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < drives.Count; i++)
        {
            var (label, fwLba, osBytes) = (drives[i].Label, drives[i].FirmwareTotalLba, drives[i].OsVisibleBytes);
            var v = Evaluate(fwLba, osBytes, drives[i].LogicalSectorBytes);
            bool valid = fwLba > 0 && osBytes > 0 && drives[i].LogicalSectorBytes >= 512;
            facts.Add(new HardwareFact($"hpa.{i}", Category, $"ATA／OS 容量對照（{label}）",
                v.Summary, "", "ATA IDENTIFY 當前可定址 LBA vs Win32_DiskDrive.Size",
                valid ? FactTrustLevel.Derived : FactTrustLevel.Unknown, false, at, null,
                valid ? FactAvailability.Present : FactAvailability.Unknown,
                valid ? null : "容量資料缺失或磁區大小無效"));
        }
        facts.Add(new HardwareFact("hpa.dco", Category, "DCO 隱藏容量", "", "",
            "ATA DEVICE CONFIGURATION IDENTIFY（未執行）", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
            "尚未實作 DCO 探測，不能確認或排除 DCO"));
        return facts;
    }

    /// <summary>以 Win32_DiskDrive.Index 對齊 PhysicalDrive{i}，取 IDENTIFY 當前容量對照 WMI Size；NVMe／讀不到的盤跳過。</summary>
    private static IReadOnlyList<HpaDrive>? CollectDisks()
    {
        try
        {
            var byIndex = new Dictionary<int, long>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Index, Size FROM Win32_DiskDrive"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    if (o["Index"] is null || o["Size"] is null) continue;
                    byIndex[Convert.ToInt32(o["Index"])] = Convert.ToInt64(o["Size"]);
                }
            }

            var list = new List<HpaDrive>();
            foreach (var (index, osBytes) in byIndex.OrderBy(kv => kv.Key))
            {
                if (StorageSmartService.TryGetBusType(index, out string busName) == 0) continue;
                if (busName.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) continue; // NVMe 無 ATA IDENTIFY，不比對
                if (StorageSmartService.TryReadAtaIdentify(index) is not { } idfBytes) continue;
                if (AtaIdentify.Decode(idfBytes) is not { } idf) continue;
                list.Add(new HpaDrive($"PhysicalDrive{index}（{busName}）", idf.TotalLba, osBytes, idf.LogicalSectorBytes));
            }
            return list;
        }
        catch { return null; }
    }
}
