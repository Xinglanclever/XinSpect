using System.Management;

namespace XinSpect;

/// <summary>
/// 電池分析：透過 WMI 讀取電池設計容量、目前滿充容量、耗損率、電量與化學類型；
/// 並可呼叫 powercfg 產生官方完整電池報告（HTML）。純本機、無第三方相依。
/// 桌機／伺服器等無電池環境會回報「未偵測到電池」而非出錯。
/// </summary>
public sealed class BatteryInfo
{
    public bool Present { get; set; }
    public string Message { get; set; } = "";
    public string Name { get; set; } = "";
    public string Chemistry { get; set; } = "";
    public int ChargePercent { get; set; }
    public string StatusText { get; set; } = "";
    public long DesignCapacity { get; set; }   // mWh
    public long FullCapacity { get; set; }      // mWh
    public int CycleCount { get; set; }

    public double WearPercent =>
        DesignCapacity > 0 && FullCapacity > 0
            ? Math.Max(0, (1.0 - (double)FullCapacity / DesignCapacity) * 100.0) : 0;

    public string DesignText => DesignCapacity > 0 ? $"{DesignCapacity:N0} mWh" : "—";
    public string FullText => FullCapacity > 0 ? $"{FullCapacity:N0} mWh" : "—";
    public string WearText => DesignCapacity > 0 && FullCapacity > 0 ? $"{WearPercent:0.0}%" : "—";
    public string CycleText => CycleCount > 0 ? CycleCount.ToString() : "—（多數電池不回報）";
}

public sealed class BatteryService
{
    public BatteryInfo Read()
    {
        var info = new BatteryInfo();
        try
        {
            using var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Battery");
            var bat = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (bat is null)
            {
                info.Present = false;
                info.Message = "未偵測到電池（桌機或伺服器環境）。電池分析僅適用於筆記型電腦或平板。";
                return info;
            }
            info.Present = true;
            info.Name = bat["Name"]?.ToString() ?? "電池";
            info.ChargePercent = ToInt(bat["EstimatedChargeRemaining"]);
            info.Chemistry = ChemistryText(ToInt(bat["Chemistry"]));
            info.StatusText = StatusText(ToInt(bat["BatteryStatus"]));
        }
        catch (Exception ex)
        {
            info.Present = false;
            info.Message = "讀取電池資訊失敗：" + ex.Message;
            return info;
        }

        // 設計容量 / 滿充容量（root\WMI，部分機型不提供）
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryStaticData");
            var o = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (o?["DesignedCapacity"] is not null) info.DesignCapacity = ToLong(o["DesignedCapacity"]);
        }
        catch { /* 略過：非致命 */ }
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryFullChargedCapacity");
            var o = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (o?["FullChargedCapacity"] is not null) info.FullCapacity = ToLong(o["FullChargedCapacity"]);
        }
        catch { /* 略過 */ }
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM BatteryCycleCount");
            var o = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (o?["CycleCount"] is not null) info.CycleCount = ToInt(o["CycleCount"]);
        }
        catch { /* 略過 */ }

        return info;
    }

    private static int ToInt(object? o) { try { return o is null ? 0 : Convert.ToInt32(o); } catch { return 0; } }
    private static long ToLong(object? o) { try { return o is null ? 0 : Convert.ToInt64(o); } catch { return 0; } }

    private static string StatusText(int s) => s switch
    {
        1 => "放電中", 2 => "接上電源", 3 => "已充飽", 4 => "低電量", 5 => "危急",
        6 => "充電中", 7 => "充電中（高）", 8 => "充電中（低）", 9 => "充電中（危急）",
        10 => "未知", 11 => "部分充電", _ => "—",
    };

    private static string ChemistryText(int c) => c switch
    {
        1 => "其他", 2 => "未知", 3 => "鉛酸", 4 => "鎳鎘（NiCd）", 5 => "鎳氫（NiMH）",
        6 => "鋰離子（Li-ion）", 7 => "鋅空氣", 8 => "鋰聚合物（Li-poly）", _ => "—",
    };
}
