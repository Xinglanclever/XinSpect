using System.Management;

namespace XinSpect;

/// <summary>
/// 驗機實體證據：① 機箱開啟偵測（SMBIOS Type 3 的 Security Status）② HPA 隱藏容量
/// （ATA IDENTIFY 最大 LBA 對照 OS 可見容量）。二手機「拆過機、容量被縮」的一翻兩瞪眼證據。
/// DCO（Device Configuration Overlay）需廠商私有命令——誠實聲明不實作。
/// </summary>
public static class ChassisFactsService
{
    private const string Category = "主機板";

    /// <summary>SMBIOS Type 3 offset 12 的 Security Status 逐值解碼。</summary>
    public static string DescribeSecurityStatus(byte code) => code switch
    {
        1 => "其他（Other）",
        2 => "未知",
        3 => "無（沒有入侵偵測事件）",
        4 => "寫入保護啟用",
        5 => "機殼曾被開啟（韌體記錄入侵事件）",
        6 => "未實作",
        7 => "未實作",
        8 => "未知",
        9 => "資產追蹤",
        10 => "入侵偵測（租戶防護）",
        _ => $"Security Status 0x{code:X2}（未收錄）",
    };

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<byte[]>?>? smbiosProbe = null)
    {
        var tables = (smbiosProbe ?? new Func<IReadOnlyList<byte[]>?>(FetchSmbiosTables))();
        if (tables is null)
            return [new HardwareFact("chassis.security_status", Category, "機箱開啟偵測", "", "",
                "SMBIOS Type 3（MSSmbs_RawSMBiosTables）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "SMBIOS 資料讀不到——機箱開啟偵測在無 SMBIOS 的環境（如 VM）不可用")];

        foreach (var table in tables)
        {
            if (table.Length < 13 || table[0] != 3) continue;
            byte security = table[12];
            string desc = DescribeSecurityStatus(security);
            bool intrusion = security is 5 or 10;
            string chassisType = table.Length > 5
                ? DescribeChassisType(table[5])
                : "";
            return [new HardwareFact("chassis.security_status", Category, "機箱開啟偵測",
                intrusion ? $"{desc}——拆機的韌體級證據，建議追問來源" : desc,
                "", $"SMBIOS Type 3 Security Status（機箱類型：{chassisType}）",
                FactTrustLevel.Reported, false, at, security)];
        }
        return [new HardwareFact("chassis.security_status", Category, "機箱開啟偵測", "", "",
            "SMBIOS Type 3（MSSmbs_RawSMBiosTables）", FactTrustLevel.Unknown, false, at, null,
            FactAvailability.NotSupported, "SMBIOS 表列裡沒有 Type 3（System Enclosure）結構——如實標")];
    }

    /// <summary>SMBIOS Type 3 offset 5 的 Chassis Type（僅列出常見值；其餘如實標編號）。</summary>
    public static string DescribeChassisType(byte code) => code switch
    {
        1 => "其他", 2 => "未知", 3 => "桌面", 4 => "低桌面", 5 => "披薩盒", 6 => "迷你塔", 7 => "塔式",
        8 => "可攜", 9 => "筆記型", 10 => "筆記型", 11 => "手持", 12 => "對接站", 13 => "一體成型",
        23 => "Rack Mount 機箱", 24 => "Sealed-case PC",
        _ => $"類型 {code}",
    };

    private static IReadOnlyList<byte[]>? FetchSmbiosTables()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT SMBIOSData FROM MSSmbs_RawSMBiosTables");
            foreach (ManagementObject o in searcher.Get())
            {
                if (o["SMBIOSData"] is byte[] data)
                    return SplitSmbiosStructures(data);
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>把整段 SMBIOS 表切開成結構（每個結構＝formatted area＋雙 null＋字串區）。</summary>
    public static List<byte[]> SplitSmbiosStructures(byte[] data)
    {
        var list = new List<byte[]>();
        int i = 0;
        while (i + 4 <= data.Length)
        {
            int len = data[i + 1];
            if (len < 4 || i + len > data.Length) break;
            int j = i + len;
            // 字串區以兩個連續 null 結束
            while (j + 1 < data.Length && !(data[j] == 0 && data[j + 1] == 0)) j++;
            j += 2;
            if (j > data.Length) j = data.Length;
            var structure = new byte[len];
            Array.Copy(data, i, structure, 0, len);
            list.Add(structure);
            i = j;
            if (data[i - 1] == 0 && data[i - 2] == 0 && i + 1 < data.Length && data[i] == 0) break; // 結尾雙 null
        }
        return list;
    }
}

/// <summary>HPA 評比結果。</summary>
public sealed record HpaVerdict(ulong FirmwareTotalLba, ulong OsVisibleSectors, long HiddenSectors, string Summary);

/// <summary>
/// HPA（Host Protected Area）偵測：ATA IDENTIFY 的最大 LBA（韌體聲明的全部容量）對照
/// OS 可見容量——OS 看到的比韌體少，就是有一段容量被藏起來（HPA 作用中）。
/// 竊改／翻新機的直接證據。<b>DCO 需廠商私有命令，誠實聲明不實作</b>（不像 HPA 有公開路徑）。
/// </summary>
public static class HpaFactsService
{
    private const string Category = "儲存裝置";

    public static HpaVerdict Evaluate(ulong firmwareTotalLba, long osVisibleBytes)
    {
        ulong osSectors = osVisibleBytes > 0 ? (ulong)(osVisibleBytes / 512) : 0;
        long hidden = osSectors > 0 && firmwareTotalLba > osSectors
            ? (long)(firmwareTotalLba - osSectors)
            : 0;
        string summary = hidden > 0
            ? $"HPA 作用中：韌體可見 {firmwareTotalLba} 磁區、OS 只見 {osSectors}——隱藏 {hidden} 磁區（約 {hidden * 512.0 / 1_000_000_000:F1} GB）"
            : $"容量一致（韌體 {firmwareTotalLba} 磁區＝OS {osSectors} 磁區），無 HPA 跡象";
        return new HpaVerdict(firmwareTotalLba, osSectors, hidden, summary);
    }

    public sealed record HpaDrive(string Label, ulong FirmwareTotalLba, long OsVisibleBytes);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<HpaDrive>?>? probe = null)
    {
        var drives = (probe ?? new Func<IReadOnlyList<HpaDrive>?>(CollectDisks))();
        if (drives is null)
            return [new HardwareFact("hpa.dco", Category, "HPA／DCO 隱藏容量", "", "",
                "ATA IDENTIFY＋WMI Win32_DiskDrive", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError,
                "磁碟列舉失敗——HPA 對照讀不到；DCO（Device Configuration Overlay）需廠商私有命令，刻意不實作")];

        var facts = new List<HardwareFact>();
        for (int i = 0; i < drives.Count; i++)
        {
            var (label, fwLba, osBytes) = (drives[i].Label, drives[i].FirmwareTotalLba, drives[i].OsVisibleBytes);
            var v = Evaluate(fwLba, osBytes);
            facts.Add(new HardwareFact($"hpa.{i}", Category, $"HPA 隱藏容量（{label}）",
                v.Summary, "", "ATA IDENTIFY 最大 LBA vs Win32_DiskDrive.Size",
                FactTrustLevel.Derived, false, at, v.HiddenSectors));
        }
        facts.Add(new HardwareFact("hpa.dco", Category, "DCO 隱藏容量", "", "",
            "ATA 廠商私有命令", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
            "DCO 需廠商私有命令（無公開 usermode 通路）——刻意不實作，不猜"));
        return facts;
    }

    /// <summary>生產收集：以 Win32_DiskDrive.Index 對齊 PhysicalDrive{i}，取 IDENTIFY 最大 LBA 對照 WMI Size；NVMe／讀不到的盤跳過。</summary>
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
                list.Add(new HpaDrive($"PhysicalDrive{index}（{busName}）", idf.TotalLba, osBytes));
            }
            return list;
        }
        catch { return null; }
    }
}
