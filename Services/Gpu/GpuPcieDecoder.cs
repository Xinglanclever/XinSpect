namespace XinSpect;

/// <summary>
/// 顯示卡 PCIe 流量與鏈路的換算與判讀（純函式）。
/// </summary>
/// <remarks>
/// 流量來自 NVML（<c>nvmlDeviceGetPcieThroughput</c>，驅動自己量的最近約 20 毫秒平均，單位 KB/s），
/// 鏈路容量則由目前世代與寬度推算。兩者<b>不可混為一談</b>：把流量講成「PCIe 滿載」是錯的，
/// 要除以容量才是佔用率；而世代或寬度讀不到時就不算佔用率，不拿一個猜的分母去除。
/// <para>
/// 與「PCIe 鏈路」那一頁的分工：那一頁讀 PCI 設定空間、看的是<b>所有</b>裝置協商到的速度與寬度；
/// 這裡只有顯示卡，但多了「真的跑了多少」——那是設定空間讀不到的東西。
/// </para>
/// </remarks>
public static class GpuPcieDecoder
{
    /// <summary>
    /// 每通道有效頻寬（MB/s）。Gen1/2 是 8b/10b 編碼，Gen3 起是 128b/130b；
    /// 這些是規格值，不是量到的。
    /// </summary>
    private static double? PerLaneMbPerSec(uint gen) => gen switch
    {
        1 => 250,
        2 => 500,
        3 => 985,
        4 => 1969,
        5 => 3938,
        6 => 7877,
        _ => null,
    };

    /// <summary>鏈路容量（GB/s）；世代或寬度讀不到（0）時回 null。</summary>
    public static double? LinkCapacityGbPerSec(uint gen, uint width)
    {
        if (width == 0) return null;
        if (PerLaneMbPerSec(gen) is not { } perLane) return null;
        return perLane * width / 1024.0;
    }

    /// <summary>KB/s → 文字；null 代表讀不到。</summary>
    public static string RateText(uint? kbPerSec)
    {
        if (kbPerSec is not { } kb) return "—";
        if (kb < 1024) return $"{kb} KB/s";
        if (kb < 1024 * 1024) return $"{kb / 1024.0:0.00} MB/s";
        return $"{kb / (1024.0 * 1024):0.00} GB/s";
    }

    /// <summary>傳送＋接收佔鏈路容量的百分比；容量算不出來時回 null。</summary>
    public static double? UtilizationPercent(uint txKbPerSec, uint rxKbPerSec, uint gen, uint width)
    {
        if (LinkCapacityGbPerSec(gen, width) is not { } capGb || capGb <= 0) return null;
        double usedGb = (txKbPerSec + (double)rxKbPerSec) / (1024.0 * 1024);
        return 100.0 * usedGb / capGb;
    }

    /// <summary>目前鏈路對能力的判讀。</summary>
    public static (string Text, Severity Severity) JudgeLink(uint curGen, uint curWidth, uint maxGen, uint maxWidth)
    {
        if (curGen == 0 || curWidth == 0 || maxGen == 0 || maxWidth == 0)
            return ("讀不到目前或最大的 PCIe 鏈路資訊（NVML 未提供），因此不下判斷。", Severity.Neutral);

        if (curWidth < maxWidth)
            return ($"寬度只協商到 x{curWidth}，這張卡支援 x{maxWidth}——通常是插槽本身只有那麼多通道、"
                  + "主機板分流（bifurcation）把通道讓給了別的插槽，或金手指／插槽接觸不良。"
                  + "寬度不足不會自己恢復，是實實在在少了頻寬。", Severity.Serious);

        if (curGen < maxGen)
            return ($"目前是 Gen{curGen}，這張卡支援 Gen{maxGen}。低世代在<b>閒置</b>時是正常的——"
                  + "驅動會降速省電，一有負載就會拉回去。若在滿載時仍停在低世代，才要懷疑線路品質、"
                  + "BIOS 設定或轉接卡。", Severity.Warning);

        return ($"與能力相符：Gen{curGen} x{curWidth}。", Severity.Good);
    }
}
