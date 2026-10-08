namespace XinSpect;

/// <summary>
/// PCIe 鏈路的速度／寬度代碼對照（純函式）。
/// </summary>
/// <remarks>
/// 原本這些只有主專案的 <c>PcieLinkDecoder</c> 提供，而那個檔案還帶著 WPF 繫結用的列型別，
/// 因此放不進無 WPF 依賴的純解碼器類別庫。鏈路代碼的對照是規格常數、與 UI 無關，
/// 移到這裡之後突變測試與 SpecRef 覆蓋檢查都涵蓋得到；主專案的 <c>PcieLinkDecoder</c>
/// 保留原有轉呼叫，呼叫端不受影響。
/// </remarks>
public static class PcieLink
{
    /// <summary>PCIe 能力結構的能力 ID。</summary>
    public const byte PcieCapId = 0x10;

    /// <summary>速度代碼 → 世代名稱。1＝2.5、2＝5、3＝8、4＝16、5＝32、6＝64 GT/s。</summary>
    [SpecRef("PCI Express Base Specification, Link Capabilities Register bits 3:0（Maximum Link Speed）：1＝2.5 GT/s、2＝5.0 GT/s、3＝8.0 GT/s、4＝16.0 GT/s、5＝32.0 GT/s、6＝64.0 GT/s；Link Status Register bits 3:0（Current Link Speed）同編碼。")]
    public static string SpeedName(int code) => code switch
    {
        0 => "—",
        1 => "Gen1",
        2 => "Gen2",
        3 => "Gen3",
        4 => "Gen4",
        5 => "Gen5",
        6 => "Gen6",
        _ => $"代碼 {code}",
    };

    /// <summary>速度代碼 → 每條通道的 GT/s（不認得的代碼回 0，不硬掰）。</summary>
    [SpecRef("PCI Express Base Specification 各世代鏈路速率：Gen1 2.5 GT/s、Gen2 5.0、Gen3 8.0、Gen4 16.0、Gen5 32.0、Gen6 64.0 GT/s；不認得的代碼回 0 而不外推。")]
    public static double GtPerSecond(int code) => code switch
    {
        1 => 2.5, 2 => 5, 3 => 8, 4 => 16, 5 => 32, 6 => 64, _ => 0,
    };

    /// <summary>「Gen4 x16」這樣的一句話；資料不足時回「—」。</summary>
    [SpecRef("組合 Link Capabilities／Link Status 的速度與寬度欄位為慣用表示法（Gen 世代 × 通道數）；任一欄位為 0 即資料不足，回「—」而不猜。")]
    public static string LinkText(int speed, int width)
        => speed <= 0 || width <= 0 ? "—" : $"{SpeedName(speed)} x{width}";

    /// <summary>單向理論頻寬（GB/s）。Gen1／2 為 8b/10b 編碼、Gen3 起為 128b/130b。</summary>
    [SpecRef("PCI Express Base Specification §4.2.2（編碼方式）：Gen1／Gen2 採 8b/10b（效率 0.8）、Gen3 起採 128b/130b（效率 128/130）；單向頻寬 ＝ 每通道 GT/s × 通道數 × 編碼效率 ÷ 8。")]
    public static double BandwidthGbps(int speed, int width)
    {
        double gt = GtPerSecond(speed);
        if (gt <= 0 || width <= 0) return 0;
        double efficiency = speed <= 2 ? 8.0 / 10 : 128.0 / 130;
        return gt * width * efficiency / 8;
    }

    /// <summary>目前頻寬 ÷ 能力頻寬，夾在 0–1（畫長條用）。</summary>
    [SpecRef("比值為 BandwidthGbps 的推導值，非規格常數；能力為 0 時回 0 且不除零。")]
    public static double BandwidthFraction(int curSpeed, int curWidth, int maxSpeed, int maxWidth)
    {
        double cap = BandwidthGbps(maxSpeed, maxWidth);
        if (cap <= 0) return 0;
        return Math.Clamp(BandwidthGbps(curSpeed, curWidth) / cap, 0, 1);
    }

    /// <summary>PCIe 能力暫存器（+0x02）bits 7:4 → 裝置／通訊埠類別。</summary>
    [SpecRef("PCI Express Base Specification, PCI Express Capabilities Register bits 7:4（Device/Port Type）：0h＝PCI Express Endpoint、1h＝Legacy PCI Express Endpoint、4h＝Root Port of PCI Express Root Complex、5h＝Upstream Port of PCI Express Switch、6h＝Downstream Port of PCI Express Switch、7h／8h＝PCI Express to PCI/PCI-X Bridge 及其反向、9h＝Root Complex Integrated Endpoint、Ah＝Root Complex Event Collector。")]
    public static string PortTypeName(int code) => code switch
    {
        0x0 => "端點",
        0x1 => "舊式端點",
        0x4 => "根埠",
        0x5 => "交換器上游埠",
        0x6 => "交換器下游埠",
        0x7 => "PCIe→PCI 橋接",
        0x8 => "PCI→PCIe 橋接",
        0x9 => "根複合體整合端點",
        0xA => "根複合體事件收集器",
        _ => $"類別 {code}",
    };

    /// <summary>Link Capabilities（+0x0C）→（最大速度代碼，最大寬度）。</summary>
    [SpecRef("PCI Express Base Specification, Link Capabilities Register（PCIe 能力結構 +0x0C）：bits 3:0 ＝ Maximum Link Speed、bits 9:4 ＝ Maximum Link Width（寬度以 2 的次方編碼，欄位值即通道數代碼）。")]
    public static (int Speed, int Width) DecodeLinkCap(uint linkCap)
        => ((int)(linkCap & 0xF), (int)((linkCap >> 4) & 0x3F));

    /// <summary>Link Status（+0x12 的 16 位）→（目前速度代碼，協商寬度）。</summary>
    [SpecRef("PCI Express Base Specification, Link Status Register（PCIe 能力結構 +0x12）：bits 3:0 ＝ Current Link Speed、bits 9:4 ＝ Negotiated Link Width。")]
    public static (int Speed, int Width) DecodeLinkStatus(ushort linkStatus)
        => (linkStatus & 0xF, (linkStatus >> 4) & 0x3F);
}
