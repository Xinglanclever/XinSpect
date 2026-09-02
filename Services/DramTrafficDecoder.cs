namespace XinSpect;

/// <summary>自我驗證的結果。</summary>
public sealed record CounterValidation(bool Passed, string Text);

/// <summary>
/// L3 未命中 → DRAM 實際流量的換算與自我驗證（純函式）。
/// </summary>
/// <remarks>
/// 用的是<b>架構事件</b> LONGEST_LAT_CACHE.MISS／REFERENCE（Intel SDM 列為架構效能事件，
/// 跨世代編碼相同），不是需要逐微架構查表的 OFFCORE_RESPONSE 遮罩——後者若填錯，
/// 換算出來的 GB/s 會是一個看起來完全合理的假數字。
/// <para>
/// 即使如此仍要自我驗證：先跑一段<b>已知會從記憶體讀多少位元組</b>的負載，若計數器算出來的量
/// 與已知值不在同一個量級，就只顯示原始計數、不換算成頻寬。這與能量計的處理方式一致。
/// </para>
/// <para>
/// 界線：這是<b>核心側</b>看到的 L3 未命中（含硬體預取），乘以快取行大小得到位元組。
/// 不包含 DMA／裝置直接發起的記憶體流量，也<b>無法分出逐通道</b>——逐通道要讀 iMC 的
/// 效能監測計數器，而在 Skylake-X 這類平台上那些計數器只走 MMIO，本程式的唯讀路徑（MSR 與
/// PCI 設定空間）到不了。到不了就說到不了，不猜一個分佈出來。
/// </para>
/// </remarks>
public static class DramTrafficDecoder
{
    /// <summary>快取行大小（位元組）。一次 L3 未命中就是一條快取行從記憶體被搬上來。</summary>
    public const int CacheLine = 64;

    /// <summary>自我驗證的容許範圍：量到的位元組必須落在已知值的 0.6 到 2 倍之間。</summary>
    private const double MinRatio = 0.6, MaxRatio = 2.0;

    public static long Bytes(long misses) => misses * CacheLine;

    public static double GbPerSec(long bytes, double seconds)
        => seconds > 0 ? bytes / seconds / (1024.0 * 1024 * 1024) : 0;

    /// <summary>L3 命中率；參照為 0 或未命中大於參照（計數異常）時回 null，不硬算。</summary>
    public static double? HitPercent(long references, long misses)
    {
        if (references <= 0 || misses > references) return null;
        return 100.0 * (references - misses) / references;
    }

    /// <summary>
    /// 拿已知位元組數的負載驗證計數器。通不過就不准把計數換算成頻寬。
    /// </summary>
    public static CounterValidation Validate(long expectedBytes, long countedBytes)
    {
        if (countedBytes <= 0)
            return new(false, "自我驗證未通過：跑了一段已知會讀滿記憶體的負載，計數器卻沒有前進。"
                            + "本平台可能沒有開放這個效能事件（虛擬化環境常見），因此不換算成頻寬。");

        double ratio = (double)countedBytes / expectedBytes;
        string amounts = $"（已知讀取 {Size(expectedBytes)}，計數器算出 {Size(countedBytes)}）";

        if (ratio < MinRatio)
            return new(false, $"自我驗證未通過：計數器算出的量只有已知負載的 {ratio:0.00} 倍{amounts}——"
                            + "偏少到不能當成 DRAM 流量看，因此只顯示原始計數。");

        if (ratio > MaxRatio)
            return new(false, $"自我驗證未通過：計數器算出的量比已知負載多了 {ratio:0.00} 倍{amounts}——"
                            + "這個事件在本平台上顯然把別的東西也算進來了，因此只顯示原始計數。");

        return new(true, $"自我驗證通過：計數器算出的量是已知負載的 {ratio:0.00} 倍{amounts}，"
                       + "在容許範圍內，故可換算成頻寬。");
    }

    /// <summary>流量文字。沒通過驗證就只給原始計數——不給一個看起來合理的 GB/s。</summary>
    public static string TrafficText(long misses, double seconds, bool validated)
    {
        if (!validated)
            return $"{misses:N0} 次 L3 未命中（未通過自我驗證，不換算成頻寬）";

        long bytes = Bytes(misses);
        return $"{Size(bytes)} ・ {GbPerSec(bytes, seconds):0.00} GB/s（{misses:N0} 次未命中 × {CacheLine} 位元組）";
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} 位元組",
    };
}
