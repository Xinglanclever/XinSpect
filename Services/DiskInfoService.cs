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

    /// <summary>總覽「詳細資訊」用的單行摘要：型號、容量、介面類型。</summary>
    public string DisplayText => Model.Length > 0 ? $"{Model}（{CapacityText}・{TypeText}）" : "—";
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

    /// <summary>系統磁碟區所在的實體磁碟；<see cref="Inferred"/> 為 true 時是容量比對的推測。</summary>
    public sealed record SystemDiskLocation(int Index, string Model, DiskKind Kind, bool Inferred, string Evidence);

    /// <summary>
    /// 找出系統磁碟區（預設 <c>C:</c>）實際落在哪一顆實體磁碟。
    /// <para>
    /// <b>不能假設「磁碟 0 就是系統碟」。</b>那條假設讓一台系統裝在 NVMe（磁碟 3）、資料在
    /// 磁碟 0 機械硬碟的機器被判成「系統安裝在機械硬碟上」，還據此建議換系統碟——報告自己
    /// 讀到 C: 1,117 GB，而磁碟 0 只有 931 GB，兩個數字當場對不上。
    /// </para>
    /// <para>
    /// 第一手來源是 WMI 的關聯查詢（邏輯磁碟 → 分割區 → 實體磁碟），不需要提權；
    /// 查不到才退成容量比對，而且只有在<b>唯一一顆</b>磁碟放得進該磁碟區時才採用，
    /// 並把 <see cref="SystemDiskLocation.Inferred"/> 標成 true 讓上層照實說明。
    /// </para>
    /// </summary>
    public static SystemDiskLocation? LocateSystemDisk(IReadOnlyList<PhysicalDiskInfo>? disks = null,
        double volumeTotalBytes = 0, string letter = "C")
    {
        disks ??= Query();
        string id = letter.TrimEnd(':').ToUpperInvariant() + ":";

        try
        {
            foreach (var part in Query(null,
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID=\"{id}\"}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
            {
                string partId = Str(part, "DeviceID");
                if (partId.Length == 0) continue;

                foreach (var drv in Query(null,
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID=\"{partId}\"}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                {
                    int idx = I(drv, "Index");
                    var hit = disks.FirstOrDefault(d => d.Index == idx);
                    string model = hit is { Model.Length: > 0 } ? hit.Model : Str(drv, "Model");
                    var kind = hit?.Kind ?? Classify(0, 0, 0, Str(drv, "InterfaceType"));
                    return new SystemDiskLocation(idx, model, kind, false, $"{id} 的分割區掛在磁碟 {idx}");
                }
            }
        }
        catch { /* 關聯查詢在某些機型／權限下不可用，退回容量比對 */ }

        return LocateByCapacity(disks, volumeTotalBytes);
    }

    /// <summary>容量比對的後備判定：只有唯一一顆磁碟放得進該磁碟區時才回它，否則回 null（不猜）。</summary>
    internal static SystemDiskLocation? LocateByCapacity(IReadOnlyList<PhysicalDiskInfo> disks, double volumeTotalBytes)
    {
        if (volumeTotalBytes <= 0) return null;
        var fits = disks.Where(d => d.SizeBytes >= volumeTotalBytes).OrderBy(d => d.SizeBytes).ToList();
        if (fits.Count != 1) return null;

        var only = fits[0];
        return new SystemDiskLocation(only.Index, only.Model, only.Kind, true,
            $"磁碟區容量 {volumeTotalBytes / 1_000_000_000.0:0} GB 只有「{only.CountText} {only.Model}」放得進，"
            + "分割區關聯查不到，這是容量比對的推測");
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

    /// <summary>
    /// 匯流排名稱。<b>對照表不在這裡</b>——它與 <see cref="StorageSmartService.BusTypeName"/> 是同一件事，
    /// 原本兩份各寫一份、兩份都從列舉中段位移一格（同一顆碟在兩個畫面標成不同匯流排），
    /// 2026-10-19 收斂成一份（論述見 <c>GATE-LOG-266</c>）。這裡只保留 0 的顯示習慣「—」。
    /// </summary>
    private static string BusZh(int bus) => bus <= 0 ? "—" : StorageSmartService.BusTypeName((uint)bus);
}
