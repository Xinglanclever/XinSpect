using System.Management;

namespace XinSpect;

/// <summary>實體磁碟的介面/媒體類型。</summary>
public enum DiskKind { Unknown, NvmeSsd, SataSsd, Hdd }

/// <summary>單一實體磁碟的靜態資訊 + HDD 的 S.M.A.R.T. 磁區健康（由 WMI 讀取，開機一次）。</summary>
public sealed class PhysicalDiskInfo
{
    public int Index { get; set; } = -1;
    public string Model { get; set; } = "";
    public long SizeBytes { get; set; }
    public DiskKind Kind { get; set; } = DiskKind.Unknown;
    public Severity HealthSeverity { get; set; } = Severity.Neutral;
    public string HealthDetail { get; set; } = "";

    // ── 深度靜態資訊（Win32_DiskDrive / MSFT_PhysicalDisk）─────────────────
    public string SerialNumber { get; set; } = "—";
    public string Firmware { get; set; } = "—";
    public string InterfaceType { get; set; } = "—";
    public string BusText { get; set; } = "—";
    public int Partitions { get; set; }
    public int BytesPerSector { get; set; }
    public string MediaType { get; set; } = "—";

    public string CountText => Index >= 0 ? $"磁碟 {Index}" : "—";
    public string PartitionsText => Partitions > 0 ? $"{Partitions} 個分割" : "—";
    public string SectorText => BytesPerSector > 0 ? $"{BytesPerSector} 位元組" : "—";
    public Brand Brand => Brands.Resolve(Model);

    public string TypeText => Kind switch
    {
        DiskKind.NvmeSsd => "NVMe SSD",
        DiskKind.SataSsd => "SATA SSD",
        DiskKind.Hdd => "機械硬碟",
        _ => "儲存裝置",
    };

    public string CapacityText
    {
        get
        {
            if (SizeBytes <= 0) return "—";
            double gb = SizeBytes / 1_000_000_000.0;   // 廠商標示採十進位（10^9）
            return gb >= 1000 ? $"{gb / 1000.0:0.##} TB" : $"{gb:0} GB";
        }
    }
}

/// <summary>
/// 以 WMI 讀取實體磁碟的容量/類型（LHM 不提供），並對 HDD 讀取 S.M.A.R.T. 磁區狀態。
/// 資料變動極慢（容量為靜態、耗損以年計），故僅於開機時查詢一次；查詢較慢，須於背景執行緒呼叫。
/// 全程以 try/catch 降級：任何一段查詢失敗（權限/機型不支援）都只讓對應欄位留白，不拋出。
/// </summary>
public static class DiskInfoService
{
    public static List<PhysicalDiskInfo> Query()
    {
        var list = new List<PhysicalDiskInfo>();

        // 1) 實體磁碟類型（匯流排/媒體/轉速）— root\Microsoft\Windows\Storage，鍵為磁碟編號
        var physByIndex = new Dictionary<int, (int bus, int media, long spindle)>();
        foreach (var o in Query(@"root\Microsoft\Windows\Storage",
                     "SELECT DeviceId, BusType, MediaType, SpindleSpeed FROM MSFT_PhysicalDisk"))
            if (int.TryParse(Str(o, "DeviceId"), out int di))
                physByIndex[di] = (I(o, "BusType"), I(o, "MediaType"), L(o, "SpindleSpeed"));

        // 2) HDD 的 S.M.A.R.T.（best-effort，通常需系統權限）— root\WMI，以 InstanceName 對映 PNP
        var predictFail = new Dictionary<string, bool>();
        foreach (var o in Query(@"root\WMI",
                     "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus"))
            predictFail[Str(o, "InstanceName").ToUpperInvariant()] = B(o, "PredictFailure");

        var predictData = new Dictionary<string, byte[]>();
        foreach (var o in Query(@"root\WMI",
                     "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictData"))
            if (o["VendorSpecific"] is byte[] raw)
                predictData[Str(o, "InstanceName").ToUpperInvariant()] = raw;

        // 3) 以 Win32_DiskDrive 為主軸（型號/容量/PNP 皆完整，且不需提權）
        foreach (var o in Query(null,
                     "SELECT Index, Model, Size, InterfaceType, PNPDeviceID, SerialNumber, FirmwareRevision, " +
                     "Partitions, BytesPerSector, MediaType FROM Win32_DiskDrive"))
        {
            var info = new PhysicalDiskInfo
            {
                Index = I(o, "Index"),
                Model = Str(o, "Model"),
                SizeBytes = L(o, "Size"),
                InterfaceType = Norm(Str(o, "InterfaceType")),
                SerialNumber = Norm(Str(o, "SerialNumber")),
                Firmware = Norm(Str(o, "FirmwareRevision")),
                Partitions = I(o, "Partitions"),
                BytesPerSector = I(o, "BytesPerSector"),
                MediaType = Norm(Str(o, "MediaType")),
            };
            physByIndex.TryGetValue(info.Index, out var phys);
            info.Kind = Classify(phys.bus, phys.media, phys.spindle, Str(o, "InterfaceType"));
            info.BusText = BusZh(phys.bus);

            if (info.Kind == DiskKind.Hdd)
            {
                string pnp = Str(o, "PNPDeviceID").ToUpperInvariant();
                bool fail = Lookup(predictFail, pnp);
                byte[]? data = Lookup(predictData, pnp);
                (info.HealthSeverity, info.HealthDetail) = HddHealth(fail, data);
            }

            list.Add(info);
        }

        return list;
    }

    // InstanceName 通常為「<PNPDeviceID>_0」；以雙向前綴比對容錯
    private static TVal? Lookup<TVal>(Dictionary<string, TVal> map, string pnp)
    {
        if (pnp.Length == 0) return default;
        foreach (var kv in map)
            if (kv.Key.StartsWith(pnp, StringComparison.Ordinal) || pnp.StartsWith(kv.Key, StringComparison.Ordinal))
                return kv.Value;
        return default;
    }

    private static DiskKind Classify(int bus, int media, long spindle, string iface)
    {
        if (bus == 17) return DiskKind.NvmeSsd;                       // BusType 17 = NVMe
        if (media == 4) return DiskKind.SataSsd;                      // MediaType 4 = SSD（非 NVMe）
        if (media == 3) return DiskKind.Hdd;                          // MediaType 3 = HDD
        if (spindle > 0 && spindle != 0xFFFFFFFF) return DiskKind.Hdd; // 有轉速 = 機械
        if (spindle == 0 && iface.Contains("SCSI", StringComparison.OrdinalIgnoreCase)) return DiskKind.SataSsd;
        return DiskKind.Unknown;
    }

    private static (Severity, string) HddHealth(bool predictFail, byte[]? data)
    {
        if (predictFail) return (Severity.Critical, "S.M.A.R.T. 預測即將故障");
        if (data is { Length: >= 362 })
        {
            int realloc = Attr(data, 0x05);   // 重新配置磁區數
            int pending = Attr(data, 0xC5);   // 待對映磁區數
            int uncorr = Attr(data, 0xC6);    // 無法修正磁區數
            if (uncorr > 0) return (Severity.Serious, $"無法修正磁區 {uncorr}");
            if (pending > 0) return (Severity.Warning, $"待對映磁區 {pending}");
            if (realloc > 0) return (Severity.Warning, $"重新配置磁區 {realloc}");
            return (Severity.Good, "S.M.A.R.T. 正常");
        }
        return (Severity.Neutral, "");
    }

    // ATA SMART READ DATA：offset 2 起，每筆 12 bytes（最多 30 筆）；原始值在該筆 +5..+10（小端）
    private static int Attr(byte[] d, int id)
    {
        for (int off = 2; off + 12 <= d.Length && off <= 2 + 29 * 12; off += 12)
        {
            if (d[off] != id) continue;
            long raw = 0;
            for (int k = 0; k < 6; k++) raw |= (long)d[off + 5 + k] << (8 * k);
            return raw > int.MaxValue ? int.MaxValue : (int)raw;
        }
        return 0;
    }

    // ---- WMI 小工具（比照 SystemInfoService 慣例，全程降級不拋出）----

    private static IEnumerable<ManagementBaseObject> Query(string? ns, string wql)
    {
        ManagementObjectCollection col;
        try
        {
            if (ns is null)
            {
                using var s = new ManagementObjectSearcher(wql);
                col = s.Get();
            }
            else
            {
                var scope = new ManagementScope(ns);
                scope.Connect();
                using var s = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
                col = s.Get();
            }
        }
        catch { yield break; }
        // 逐一釋放每個 WMI 物件與集合，避免累積未受管 COM 控制代碼（比照 SystemInfoService.Query）。
        try
        {
            foreach (ManagementBaseObject o in col)
            {
                try { yield return o; }
                finally { o.Dispose(); }
            }
        }
        finally { col.Dispose(); }
    }

    private static string Str(ManagementBaseObject o, string p) { try { return o[p]?.ToString()?.Trim() ?? ""; } catch { return ""; } }
    private static long L(ManagementBaseObject o, string p) { try { return o[p] is { } v ? Convert.ToInt64(v) : 0; } catch { return 0; } }
    private static int I(ManagementBaseObject o, string p) { try { return o[p] is { } v ? Convert.ToInt32(v) : 0; } catch { return 0; } }
    private static bool B(ManagementBaseObject o, string p) { try { return o[p] is bool b && b; } catch { return false; } }

    /// <summary>去除 WMI 常見的空白 / 佔位值，統一回退為「—」。</summary>
    private static string Norm(string v)
    {
        v = v.Trim();
        return v.Length == 0 || v == "0" ? "—" : v;
    }

    private static string BusZh(int bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "SSA", 7 => "Fibre Channel",
        8 => "USB", 9 => "RAID", 10 => "iSCSI", 11 => "SATA", 12 => "SAS", 13 => "SD", 14 => "MMC",
        17 => "NVMe", _ => bus > 0 ? $"匯流排 {bus}" : "—"
    };
}
