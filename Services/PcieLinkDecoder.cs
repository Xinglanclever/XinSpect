namespace XinSpect;

/// <summary>一條 PCIe 裝置的鏈路實況（目前）對能力（最大）。</summary>
public sealed class PcieLinkRow
{
    public PcieLinkRow(string name, string location, string kind,
                       int curSpeed, int curWidth, int maxSpeed, int maxWidth,
                       string verdict, int severity)
    {
        Name = name; Location = location; Kind = kind;
        CurSpeed = curSpeed; CurWidth = curWidth; MaxSpeed = maxSpeed; MaxWidth = maxWidth;
        Verdict = verdict; Severity = severity;
    }
    public string Name { get; }
    /// <summary>bus:device.function（PCI 位置，和裝置管理員顯示的一致）。</summary>
    public string Location { get; }
    /// <summary>裝置／通訊埠類別（端點、根埠、下游埠…）。</summary>
    public string Kind { get; }
    public int CurSpeed { get; }
    public int CurWidth { get; }
    public int MaxSpeed { get; }
    public int MaxWidth { get; }
    public string Verdict { get; }
    /// <summary>0＝與能力相符、1＝速度低於能力（可能只是省電）、2＝寬度低於能力（通常是插槽／分流問題）。</summary>
    public int Severity { get; }

    public string CurrentText => PcieLinkDecoder.LinkText(CurSpeed, CurWidth);
    public string CapableText => PcieLinkDecoder.LinkText(MaxSpeed, MaxWidth);
    /// <summary>目前頻寬佔能力頻寬的比例（畫長條用；能力為 0 時回 0）。</summary>
    public double BarFraction => PcieLinkDecoder.BandwidthFraction(CurSpeed, CurWidth, MaxSpeed, MaxWidth);
}

/// <summary>
/// PCIe 設定空間的鏈路欄位解讀（純函式，單元測試涵蓋）。位移依 PCI Express Base Spec：
/// PCIe 能力結構（Cap ID 0x10）內 <b>+0x0C＝Link Capabilities</b>（速度 bits 3:0、寬度 bits 9:4）、
/// <b>+0x12＝Link Status</b>（目前速度 bits 3:0、協商寬度 bits 9:4）。
/// </summary>
public static class PcieLinkDecoder
{
    /// <summary>PCIe 能力結構的能力 ID。</summary>
    public const byte PcieCapId = 0x10;

    /// <summary>Link Capabilities（+0x0C）→（最大速度代碼，最大寬度）。</summary>
    public static (int Speed, int Width) DecodeLinkCap(uint linkCap)
        => ((int)(linkCap & 0xF), (int)((linkCap >> 4) & 0x3F));

    /// <summary>Link Status（+0x12 的 16 位）→（目前速度代碼，協商寬度）。</summary>
    public static (int Speed, int Width) DecodeLinkStatus(ushort linkStatus)
        => (linkStatus & 0xF, (linkStatus >> 4) & 0x3F);

    /// <summary>速度代碼 → 世代名稱。1＝2.5、2＝5、3＝8、4＝16、5＝32、6＝64 GT/s。</summary>
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
    public static double GtPerSecond(int code) => code switch
    {
        1 => 2.5, 2 => 5, 3 => 8, 4 => 16, 5 => 32, 6 => 64, _ => 0,
    };

    /// <summary>「Gen4 x16」這樣的一句話；資料不足時回「—」。</summary>
    public static string LinkText(int speed, int width)
        => speed <= 0 || width <= 0 ? "—" : $"{SpeedName(speed)} x{width}";

    /// <summary>單向理論頻寬（GB/s）。Gen1／2 為 8b/10b 編碼、Gen3 起為 128b/130b。</summary>
    public static double BandwidthGbps(int speed, int width)
    {
        double gt = GtPerSecond(speed);
        if (gt <= 0 || width <= 0) return 0;
        double efficiency = speed <= 2 ? 8.0 / 10 : 128.0 / 130;
        return gt * width * efficiency / 8;
    }

    /// <summary>目前頻寬 ÷ 能力頻寬，夾在 0–1（畫長條用）。</summary>
    public static double BandwidthFraction(int curSpeed, int curWidth, int maxSpeed, int maxWidth)
    {
        double cap = BandwidthGbps(maxSpeed, maxWidth);
        if (cap <= 0) return 0;
        return Math.Clamp(BandwidthGbps(curSpeed, curWidth) / cap, 0, 1);
    }

    /// <summary>PCIe 能力暫存器（+0x02）bits 7:4 → 裝置／通訊埠類別。</summary>
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

    /// <summary>
    /// 目前對能力的判讀。<b>0＝相符、1＝速度較低、2＝寬度較低</b>。
    /// </summary>
    /// <remarks>
    /// 這裡最容易犯的錯是把「閒置降速」講成故障：顯示卡與 NVMe 在沒事做的時候會把鏈路降到 Gen1
    /// 省電，負載一來才升回去——所以速度較低時只提醒、並明說要在負載中重量一次。
    /// 寬度不一樣：協商寬度通常在開機時就定了，x16 的卡跑在 x4 幾乎都是插槽走線、M.2 佔用通道
    /// 或 BIOS 分流設定造成的，那是真的損失。
    /// </remarks>
    public static (string Text, int Severity) Judge(int curSpeed, int curWidth, int maxSpeed, int maxWidth)
    {
        if (maxSpeed <= 0 || maxWidth <= 0) return ("—（裝置沒回報鏈路能力）", 0);
        if (curSpeed <= 0 || curWidth <= 0) return ("—（讀不到目前鏈路狀態）", 0);

        if (curWidth < maxWidth)
            return ($"⚠ 寬度只有 x{curWidth}，這張卡本身支援 x{maxWidth}——多半是插槽走線、M.2 佔用通道或 BIOS 分流設定，通常不會自己恢復。", 2);

        if (curSpeed < maxSpeed)
            return ($"目前 {SpeedName(curSpeed)}，能力 {SpeedName(maxSpeed)}——顯示卡與 SSD 閒置時會自己降到 Gen1 省電，這不是故障；要確認請在負載中（跑遊戲或大量讀寫）再量一次。", 1);

        return ($"已達裝置能力（{LinkText(maxSpeed, maxWidth)}）。", 0);
    }

    /// <summary>整體結論。沒有任何裝置時要說「沒讀到」，不能說「一切正常」。</summary>
    public static string Summarize(IReadOnlyList<PcieLinkRow> rows)
    {
        if (rows.Count == 0)
            return "沒有讀到任何帶 PCIe 能力結構的裝置——這通常是權限不足或 PCI 設定空間讀取不可用，不代表機器沒有 PCIe 裝置。";

        int narrow = rows.Count(r => r.Severity == 2);
        int slow = rows.Count(r => r.Severity == 1);
        if (narrow > 0)
            return $"共 {rows.Count} 條鏈路：{narrow} 條的寬度低於裝置能力（值得查），{slow} 條速度較低（多半是閒置降速）。";
        if (slow > 0)
            return $"共 {rows.Count} 條鏈路，寬度全部與能力相符；{slow} 條目前速度較低——閒置降速是正常行為，在負載中重量一次就會升回去。";
        return $"共 {rows.Count} 條鏈路，速度與寬度全部與裝置能力相符。";
    }
}
