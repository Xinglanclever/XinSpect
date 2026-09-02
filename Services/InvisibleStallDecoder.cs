namespace XinSpect;

/// <summary>隱形停頓的判決。</summary>
public sealed class InvisibleStallVerdict
{
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required Severity Severity { get; init; }
}

/// <summary>
/// 隱形停頓的換算與判讀（純函式）：SMI 次數與 C-state 駐留。
/// </summary>
/// <remarks>
/// 兩條界線寫在這裡，實作與文案都不得越過：
/// <list type="number">
/// <item><b>SMI 只數得出次數，數不出時間。</b>系統管理中斷發生在 SMM——作業系統與效能計數器
/// 都看不見的模式，沒有任何暫存器記錄它待了多久。把次數乘上「每次大概幾十微秒」就是編造，
/// 所以本頁只談頻率，不談損失了多少毫秒。</item>
/// <item><b>駐留計數器以 TSC 為刻度。</b>百分比必須除以同一段取樣窗的 TSC 差值；
/// 差值為 0（或計數器沒前進）時算不出來就說算不出來，不要填 0%——
/// 「真的沒進過那個狀態」與「這個 MSR 沒實作」是兩件不同的事。</item>
/// </list>
/// </remarks>
public static class InvisibleStallDecoder
{
    /// <summary>每秒超過這麼多次 SMI 就算頻繁（經驗值，不是規格）。</summary>
    private const double BusySmiPerSecond = 50;

    /// <summary>每秒超過這麼多次就值得注意。</summary>
    private const double NoticeSmiPerSecond = 1;

    /// <summary>駐留刻度 → 百分比；TSC 沒有前進時回 null。</summary>
    public static double? Percent(ulong residencyDelta, ulong tscDelta)
        => tscDelta == 0 ? null : 100.0 * residencyDelta / tscDelta;

    /// <summary>
    /// 駐留文字。<paramref name="residencyDelta"/> 為 <c>null</c> 代表這個 MSR 讀不到——
    /// 顯示「未實作或未開放」而不是 0%，兩者的意思完全不同。
    /// </summary>
    public static string ResidencyText(ulong? residencyDelta, ulong tscDelta)
    {
        if (residencyDelta is not { } d) return "未實作或未開放";
        if (Percent(d, tscDelta) is not { } p) return "算不出來（TSC 沒有前進）";
        if (p > 100) return $"100%（原始值超過上限，計數器可能回捲或跨封裝，不採用）";
        return $"{p:0.00}%";
    }

    /// <summary>
    /// 一句判決。<paramref name="deepestPackagePercent"/> 為 <c>null</c> 表示封裝駐留讀不到，
    /// 此時不對省電狀態下任何結論。
    /// </summary>
    public static InvisibleStallVerdict Judge(
        ulong smiDelta, double seconds, ulong smiTotal, double? deepestPackagePercent)
    {
        if (seconds <= 0)
            return new InvisibleStallVerdict
            {
                Headline = "尚未量測",
                Severity = Severity.Neutral,
                Detail = "按下量測後會取兩次樣（相隔約一秒），用差值算出 SMI 頻率與各 C-state 的駐留比例。",
            };

        double perSec = smiDelta / seconds;
        string pkg = deepestPackagePercent switch
        {
            null => "封裝層的 C-state 駐留在這台機器上讀不到（MSR 未實作或未開放），因此本頁不對省電狀態下結論。",
            <= 0.5 => "這段時間裡封裝幾乎沒有進入深層省電狀態——閒著卻醒著，代表有東西定期把它叫起來"
                    + "（高頻計時器、輪詢式驅動、或本程式自己的量測）。那是實實在在的耗電與發熱，"
                    + "但在工作管理員上看不出來。",
            _ => $"封裝有 {deepestPackagePercent:0.0}% 的時間待在深層省電狀態，這部分是正常的。",
        };

        // SMI 的三段判讀。無論哪一段都不准把次數換算成時間。
        if (smiDelta == 0)
            return new InvisibleStallVerdict
            {
                Headline = "這段時間沒有 SMI",
                Severity = deepestPackagePercent is <= 0.5 ? Severity.Warning : Severity.Good,
                Detail = $"自開機以來累計 {smiTotal:N0} 次系統管理中斷，但這一秒內沒有新增。{pkg}",
            };

        if (perSec >= BusySmiPerSecond)
            return new InvisibleStallVerdict
            {
                Headline = $"SMI 很頻繁：每秒 {perSec:0.#} 次",
                Severity = Severity.Serious,
                Detail = $"這一段量到 {smiDelta:N0} 次、自開機累計 {smiTotal:N0} 次。SMI 由韌體處理，"
                       + "發生時整顆核心離開作業系統的視野——**每一次待了多久，硬體沒有留下紀錄，量不到**，"
                       + "所以這裡只給頻率，不給損失時間。這個量級通常來自韌體的週期性工作："
                       + "風扇與溫度控制、USB 傳統模擬、TPM、伺服器板的管理控制器。"
                       + $"音訊爆音與幀時間尖峰若查不出驅動來源，這是下一個該看的地方。{pkg}",
            };

        if (perSec >= NoticeSmiPerSecond)
            return new InvisibleStallVerdict
            {
                Headline = $"有少量 SMI：每秒 {perSec:0.#} 次",
                Severity = Severity.Warning,
                Detail = $"這一段量到 {smiDelta:N0} 次、自開機累計 {smiTotal:N0} 次。"
                       + "偶發的 SMI 是正常的（韌體本來就有事要做）；每一次的耗時硬體沒有紀錄，量不到。"
                       + $"{pkg}",
            };

        return new InvisibleStallVerdict
        {
            Headline = "SMI 很少",
            Severity = Severity.Good,
            Detail = $"這一段量到 {smiDelta:N0} 次（每秒 {perSec:0.##} 次）、自開機累計 {smiTotal:N0} 次。{pkg}",
        };
    }
}
