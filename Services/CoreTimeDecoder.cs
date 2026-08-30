namespace XinSpect;

/// <summary>
/// 單一邏輯處理器的一次時間取樣（單位皆為 100 奈秒刻，累計值，自開機起算）。
/// </summary>
/// <remarks>
/// 欄位語意取自 <c>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION</c>，有兩個極容易搞錯的地方：
/// <list type="bullet">
/// <item><b><see cref="Kernel"/> 已經包含 <see cref="Idle"/></b>。Windows 把閒置執行緒算在核心模式裡，
/// 直接把 Kernel 當「核心模式忙碌時間」會把閒置也算成忙碌。</item>
/// <item><b><see cref="Dpc"/> 與 <see cref="Interrupt"/> 是 <see cref="Kernel"/> 的子集</b>，不是另外兩塊。
/// 五個數字相加不等於 100%，硬要相加就是重複計算。</item>
/// </list>
/// </remarks>
public readonly record struct CoreTimeSample(
    long Idle, long Kernel, long User, long Dpc, long Interrupt, uint InterruptCount);

/// <summary>逐邏輯處理器的時間歸因（兩次取樣的差值換算成百分比）。</summary>
public sealed class CoreTimeRow
{
    public required string Name { get; init; }
    /// <summary>閒置佔比（%）。</summary>
    public required double IdlePercent { get; init; }
    /// <summary>使用者模式佔比（%）。</summary>
    public required double UserPercent { get; init; }
    /// <summary>核心模式佔比（%），<b>已扣掉閒置</b>。</summary>
    public required double KernelPercent { get; init; }
    /// <summary>DPC 佔比（%）；屬於核心模式的一部分，不與其並列相加。</summary>
    public required double DpcPercent { get; init; }
    /// <summary>硬體中斷服務常式佔比（%）；同樣屬於核心模式的一部分。</summary>
    public required double InterruptPercent { get; init; }
    /// <summary>每秒中斷次數（由 InterruptCount 差值與實測秒數換算）。</summary>
    public required double InterruptsPerSecond { get; init; }
    /// <summary>忙碌佔比（%）＝100 − 閒置。</summary>
    public double BusyPercent => 100.0 - IdlePercent;

    public string BusyText => $"{BusyPercent:0.0}%";
    public string UserText => $"{UserPercent:0.0}%";
    public string KernelText => $"{KernelPercent:0.0}%";
    public string DpcText => $"{DpcPercent:0.00}%";
    public string InterruptText => $"{InterruptPercent:0.00}%";
    public string InterruptRateText => $"{InterruptsPerSecond:N0} 次/秒";

    /// <summary>
    /// 這顆邏輯處理器的中斷負擔嚴重度（供顏色繫結）：0＝正常、1＝偏高、2＝異常。
    /// 門檻是經驗值而非硬體規範，故只用於著色與排序，文字說明不會據此宣稱故障。
    /// </summary>
    public int Severity => (DpcPercent + InterruptPercent) switch
    {
        >= 10.0 => 2,
        >= 3.0 => 1,
        _ => 0,
    };
}

/// <summary>
/// 逐核心時間歸因的純計算（單元測試涵蓋，不呼叫任何 API）。
/// </summary>
public static class CoreTimeDecoder
{
    /// <summary>
    /// 兩次取樣求差並換算百分比。分母取「核心＋使用者」的差值——這才是這顆邏輯處理器
    /// 在區間內被記帳的總刻數；用牆鐘秒數換算會因為記帳精度而湊不滿 100%。
    /// </summary>
    /// <param name="lp">邏輯處理器編號。</param>
    /// <param name="prev">前一次取樣。</param>
    /// <param name="cur">本次取樣。</param>
    /// <param name="seconds">兩次取樣間的實測秒數（僅用於中斷次數換算）。</param>
    /// <returns>差值有效時回傳一列；分母為零或負（取樣顛倒／計數器回捲）時回 null，由呼叫端照實說。</returns>
    public static CoreTimeRow? Diff(int lp, CoreTimeSample prev, CoreTimeSample cur, double seconds)
    {
        long dIdle = cur.Idle - prev.Idle;
        long dKernel = cur.Kernel - prev.Kernel;
        long dUser = cur.User - prev.User;
        long dDpc = cur.Dpc - prev.Dpc;
        long dInt = cur.Interrupt - prev.Interrupt;

        long total = dKernel + dUser;           // Kernel 已含 Idle，故這就是全部刻數
        if (total <= 0) return null;
        if (dIdle < 0 || dKernel < 0 || dUser < 0) return null;

        double idle = 100.0 * dIdle / total;
        double kernelNet = 100.0 * (dKernel - dIdle) / total;
        double user = 100.0 * dUser / total;

        // 中斷次數是 32 位元累計值，會回捲；回捲的區間不猜次數，回報 0 而不是一個天文數字
        double rate = 0;
        if (seconds > 0 && cur.InterruptCount >= prev.InterruptCount)
            rate = (cur.InterruptCount - prev.InterruptCount) / seconds;

        return new CoreTimeRow
        {
            Name = $"CPU {lp}",
            IdlePercent = Clamp(idle),
            UserPercent = Clamp(user),
            KernelPercent = Clamp(kernelNet),
            DpcPercent = Clamp(100.0 * dDpc / total),
            InterruptPercent = Clamp(100.0 * dInt / total),
            InterruptsPerSecond = rate,
        };
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 100 ? 100 : v;

    /// <summary>
    /// 逐核閒置週期數（<c>SystemProcessorIdleCycleTime</c>）→ 說明文字。
    /// 這個值的單位是 TSC 週期而不是時間刻，和上面那張表<b>不是同一把尺</b>：
    /// 它反映的是「閒置時晶片在哪個頻率上空轉」，故不能拿去和百分比互推。
    /// </summary>
    public static string DescribeIdleCycles(ulong[]? prev, ulong[]? cur, double seconds)
    {
        if (prev is null || cur is null || prev.Length == 0 || prev.Length != cur.Length)
            return "—（閒置週期計數器讀不到，或兩次取樣的處理器數不一致）";
        if (seconds <= 0) return "—（取樣區間為零）";

        ulong sum = 0;
        int regressed = 0;
        for (int i = 0; i < cur.Length; i++)
        {
            if (cur[i] < prev[i]) { regressed++; continue; }
            sum += cur[i] - prev[i];
        }
        if (regressed == cur.Length) return "—（所有計數器都回退，取樣不可信，不做推論）";

        double perLpPerSec = sum / (double)cur.Length / seconds;
        string note = regressed > 0 ? $"（{regressed} 顆的計數器回退，已排除）" : "";
        return $"平均每顆邏輯處理器每秒閒置 {perLpPerSec / 1e6:N1} 百萬週期{note}；"
             + "單位是 TSC 週期而非時間，反映閒置時的實際時脈，不能與上表的百分比互相換算。";
    }

    /// <summary>
    /// 全機彙總：忙碌最高、DPC 最高、中斷最高的那幾顆。這是這張表真正有用的地方——
    /// 平均值會把單顆被中斷打爆的情況抹平。
    /// </summary>
    public static string Summarize(IReadOnlyList<CoreTimeRow> rows)
    {
        if (rows.Count == 0) return "—（沒有任何有效的逐核差值）";

        var busiest = rows.OrderByDescending(r => r.BusyPercent).First();
        var dpc = rows.OrderByDescending(r => r.DpcPercent).First();
        var isr = rows.OrderByDescending(r => r.InterruptPercent).First();
        double avgBusy = rows.Average(r => r.BusyPercent);

        string s = $"{rows.Count} 顆邏輯處理器平均忙碌 {avgBusy:0.0}%；"
                 + $"最忙 {busiest.Name}（{busiest.BusyPercent:0.0}%）・"
                 + $"DPC 最高 {dpc.Name}（{dpc.DpcPercent:0.00}%）・"
                 + $"中斷最高 {isr.Name}（{isr.InterruptPercent:0.00}%）。";

        double worst = dpc.DpcPercent + isr.InterruptPercent;
        if (worst >= 10.0)
            s += "⚠ 單顆的 DPC＋中斷合計已超過一成，通常代表某個驅動的中斷處理過長——"
               + "可用「DPC／ISR 延遲排行」卡片找出是哪個驅動。";
        return s;
    }

    /// <summary>這張表的讀法說明（固定文字，繫結用）。誤讀這五個數字的代價是把閒置當忙碌。</summary>
    public const string ReadingNotice =
        "讀法：核心模式已扣掉閒置（Windows 原始資料把閒置算在核心模式裡）；"
        + "DPC 與中斷是核心模式的子集，不是另外兩塊——五個數字相加不會是 100%，相加就是重複計算。"
        + "忙碌＝100 − 閒置。中斷次數是硬體中斷進入次數，不含軟體 DPC 排入。";

    /// <summary>
    /// 計數器精度說明。這五個時間值以時鐘刻為單位前進（預設約 15.6 毫秒／每秒 64 刻），
    /// 所以一秒的取樣裡最小可分辨的步進約 1.6 個百分點——本機實測的數字確實全是 1.5625% 的倍數。
    /// 明說這件事，才不會有人以為那些整齊的跳動是曦覽在四捨五入或造假。
    /// </summary>
    public const string ResolutionNotice =
        "精度：這些時間值以作業系統的時鐘刻為單位累加（預設約 15.6 毫秒，每秒 64 刻），"
        + "因此一秒的取樣裡最小可分辨的步進約 1.6 個百分點，表上的百分比會呈離散跳動——"
        + "那是計數器本身的精度，不是這裡在四捨五入。中斷次數不受此限，是逐次累計的真實計數。";

    /// <summary>超過 64 顆邏輯處理器時的誠實說明（本機 36 顆，未觸發此路徑）。</summary>
    public static string GroupNotice(int enumerated, int reported)
        => enumerated >= reported
            ? ""
            : $"⚠ 只列到 {enumerated} 顆，系統回報 {reported} 顆："
            + "本卡片走的是單一處理器群組的查詢，超過 64 顆的機器需逐群組查詢，該路徑本機無法驗證故未實作。";
}
