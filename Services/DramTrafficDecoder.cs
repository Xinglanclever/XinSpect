namespace XinSpect;

/// <summary>工作集量級檢查的結果，不代表精度校準。</summary>
public sealed record CounterValidation(bool Passed, string Text);

/// <summary>
/// LLC 未命中的等效位元組換算與量級檢查（純函式）。
/// </summary>
/// <remarks>
/// 用的是<b>架構事件</b> LONGEST_LAT_CACHE.MISS／REFERENCE（Intel SDM 列為架構效能事件，
/// 跨世代編碼相同），不是需要逐微架構查表的 OFFCORE_RESPONSE 遮罩——後者若填錯，
/// 換算出來的 GB/s 會是一個看起來完全合理的假數字。
/// <para>
/// 對照工作集大小僅為量級檢查，不能當作感測校準或 DRAM 傳輸的獨立驗證。
/// </para>
/// <para>
/// 每事件 64 位元組只是等效換算，不確認資料供應位置、預取範圍或全機 DRAM 交易。
/// 本功能沒有 iMC 計數，因此不能給逐通道分佈或完整裝置流量。
/// </para>
/// </remarks>
public static class DramTrafficDecoder
{
    /// <summary>等效換算採用的快取行大小（位元組）。</summary>
    public const int CacheLine = 64;

    /// <summary>自我驗證的容許範圍：量到的位元組必須落在已知值的 0.6 到 2 倍之間。</summary>
    private const double MinRatio = 0.6, MaxRatio = 2.0;

    public static long Bytes(long misses) => checked(misses * CacheLine);

    public static double GbPerSec(long bytes, double seconds)
        => seconds > 0 ? bytes / seconds / (1024.0 * 1024 * 1024) : 0;

    /// <summary>L3 命中率；參照為 0 或未命中大於參照（計數異常）時回 null，不硬算。</summary>
    public static double? HitPercent(long references, long misses)
    {
        if (references <= 0 || misses < 0 || misses > references) return null;
        return 100.0 * (references - misses) / references;
    }

    /// <summary>
    /// 拿已知位元組數的負載驗證計數器。通不過就不准把計數換算成頻寬。
    /// </summary>
    public static CounterValidation Validate(long expectedBytes, long countedBytes)
    {
        if (expectedBytes <= 0)
            return new(false, "量級檢查不可用：工作集大小必須大於零。");
        if (countedBytes <= 0)
            return new(false, "量級檢查未通過：讀取工作集時，計數器卻沒有前進。"
                            + "本平台可能沒有開放這個效能事件（虛擬化環境常見），因此不換算成頻寬。");

        double ratio = (double)countedBytes / expectedBytes;
        string amounts = $"（已知讀取 {Size(expectedBytes)}，計數器算出 {Size(countedBytes)}）";

        if (ratio < MinRatio)
            return new(false, $"量級檢查未通過：計數器算出的量只有工作集的 {ratio:0.00} 倍{amounts}，"
                            + "偏少到不能當成 DRAM 流量看，因此只顯示原始計數。");

        if (ratio > MaxRatio)
            return new(false, $"量級檢查未通過：計數器算出的量比工作集多了 {ratio:0.00} 倍{amounts}，"
                            + "這個事件在本平台上顯然把別的東西也算進來了，因此只顯示原始計數。");

        return new(true, $"量級檢查通過：等效量是工作集的 {ratio:0.00} 倍{amounts}，"
                       + "可列等效速率；此檢查不是精度校準，也不能證實 DRAM 實際流量。");
    }

    /// <summary>流量文字。沒通過驗證就只給原始計數——不給一個看起來合理的 GB/s。</summary>
    public static string TrafficText(long misses, double seconds, bool validated)
    {
        if (!validated)
            return $"{misses:N0} 次 LLC 未命中（未通過量級檢查，不換算成速率）";

        long bytes = Bytes(misses);
        return $"等效 {Size(bytes)} ・ {GbPerSec(bytes, seconds):0.00} GiB/s（{misses:N0} 次 LLC 未命中 × {CacheLine} 位元組；非 DRAM 實測）";
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GiB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MiB",
        >= 1024 => $"{bytes / 1024.0:0.#} KiB",
        _ => $"{bytes} 位元組",
    };
}
