namespace XinSpect;

/// <summary>一條頻寬量測結果（某個核心數下的某種存取型態）。</summary>
public sealed class MemBandwidthRow
{
    public MemBandwidthRow(string kernel, int threads, double gbps, double barFraction, string note)
    {
        Kernel = kernel; Threads = threads; Gbps = gbps; BarFraction = barFraction; Note = note;
    }
    /// <summary>存取型態（讀取／複製／相加／三元運算…）。</summary>
    public string Kernel { get; }
    public int Threads { get; }
    public double Gbps { get; }
    /// <summary>長條長度：以本輪最高頻寬為滿格。</summary>
    public double BarFraction { get; }
    /// <summary>對理論上限的比例文字，無法對照時為「—」。</summary>
    public string Note { get; }

    public string ThreadsText => $"{Threads} 執行緒";
    public string GbpsText => MemBandwidthMath.FormatGbps(Gbps);
}

/// <summary>一條負載延遲結果（施加 N 個頻寬壓力執行緒時的延遲與同時達成的頻寬）。</summary>
public sealed class LoadedLatencyRow
{
    public LoadedLatencyRow(int loaders, double gbps, double latencyNs, double barFraction)
    {
        Loaders = loaders; Gbps = gbps; LatencyNs = latencyNs; BarFraction = barFraction;
    }
    public int Loaders { get; }
    public double Gbps { get; }
    public double LatencyNs { get; }
    public double BarFraction { get; }

    public string LoadText => Loaders == 0 ? "無負載" : $"{Loaders} 執行緒施壓";
    public string GbpsText => Loaders == 0 ? "—" : MemBandwidthMath.FormatGbps(Gbps);
    public string LatencyText => LatencyNs > 0 ? $"{LatencyNs:0.0} ns" : "—";
}

/// <summary>
/// 記憶體頻寬與負載延遲的純函式部分：理論上限推算、達成率、執行緒階梯、判讀。
/// </summary>
/// <remarks>
/// <para>
/// 理論上限的算法：一支 DDR 模組對外是 64 位元（8 位元組）資料匯流排，所以
/// <b>每支模組的上限 ＝ 實際運行的 MT/s × 8 位元組</b>（DDR4-3200 → 25.6 GB/s）。
/// DDR5 把它切成兩個 32 位元子通道，合計仍是 64 位元，這條算式照樣成立。
/// </para>
/// <para>
/// 誠實界線：①WMI 只說得出「插了幾支、跑在幾 MT/s」，<b>說不出主機板實際開了幾個通道</b>，
/// 所以整機上限是在「每支模組各佔一個通道」的假設下算的——這個假設在每通道一支的機器上成立，
/// 在同一通道插兩支的機器上會高估，呈現時一律寫明是假設；②STREAM 這類測試永遠拿不到 100%，
/// 更新（refresh）、讀寫轉向、控制器排程都要吃掉一部分，實務上 60–85% 就是正常；
/// ③所以「達成率偏低」只是值得查，不是故障判決。
/// </para>
/// </remarks>
public static class MemBandwidthMath
{
    /// <summary>一支 64 位元模組在該 MT/s 下的理論上限（GB/s）。速度不明回 0。</summary>
    public static double PerModulePeakGbps(int mtPerSecond)
        => mtPerSecond <= 0 ? 0 : mtPerSecond * 8.0 / 1000.0;

    /// <summary>整機理論上限（GB/s）＝每支上限 × 模組數（假設每支各佔一個通道）。</summary>
    public static double AssumedPeakGbps(int mtPerSecond, int modules)
        => modules <= 0 ? 0 : PerModulePeakGbps(mtPerSecond) * modules;

    /// <summary>把「搬了幾位元組、花了幾秒」換成 GB/s（十進位 GB，與 STREAM 一致）。</summary>
    public static double Gbps(double bytesMoved, double seconds)
        => seconds <= 0 || bytesMoved <= 0 ? 0 : bytesMoved / seconds / 1e9;

    /// <summary>達成率（實測 ÷ 理論上限）。上限不明回 0，不硬掰。</summary>
    public static double Efficiency(double measuredGbps, double peakGbps)
        => peakGbps <= 0 || measuredGbps <= 0 ? 0 : measuredGbps / peakGbps;

    /// <summary>
    /// 執行緒階梯：1、2、4、8…到邏輯處理器數，最後補上邏輯處理器數本身（若不是 2 的次方）。
    /// </summary>
    public static int[] ThreadLadder(int logicalCount)
    {
        if (logicalCount <= 1) return [1];
        var list = new List<int>();
        for (int t = 1; t < logicalCount; t *= 2) list.Add(t);
        list.Add(logicalCount);
        return [.. list];
    }

    /// <summary>GB/s 的顯示文字；量不到時是「—」而不是 0.00。</summary>
    public static string FormatGbps(double gbps)
        => gbps <= 0 ? "—" : $"{gbps:0.00} GB/s";

    /// <summary>
    /// 負載延遲的施壓等級：0（無負載）、1、2、4…到「邏輯處理器數 − 1」——
    /// 一定要留一個核心給量延遲的那條執行緒，否則量到的是排程等待不是記憶體延遲。
    /// </summary>
    public static int[] LoadLadder(int logicalCount)
    {
        var list = new List<int> { 0 };
        int max = Math.Max(0, logicalCount - 1);
        for (int t = 1; t < max; t *= 2) list.Add(t);
        if (max >= 1) list.Add(max);
        return [.. list];
    }

    /// <summary>對理論上限的比例文字（給每一列用）；上限不明回「—」。</summary>
    public static string EfficiencyNote(double measuredGbps, double peakGbps)
    {
        double eff = Efficiency(measuredGbps, peakGbps);
        return eff <= 0 ? "—" : $"{eff:0%}";
    }

    /// <summary>
    /// 判讀：<b>0＝正常、1＝達成率偏低值得查、2＝疑似實際只跑單通道</b>。
    /// </summary>
    /// <remarks>
    /// 抓「插錯插槽」的方法：插了兩支以上，但實測最高頻寬幾乎就是<i>一支</i>模組的理論上限——
    /// 雙通道正常會落在單支上限的 1.4 倍上下，擠在同一通道則永遠過不去單支的天花板。
    /// </remarks>
    public static (string Text, int Severity) Judge(double bestGbps, int mtPerSecond, int modules)
    {
        if (bestGbps <= 0) return ("—（沒有量到頻寬）", 0);

        double perModule = PerModulePeakGbps(mtPerSecond);
        if (perModule <= 0)
            return ($"實測最高 {FormatGbps(bestGbps)}。SMBIOS 沒回報記憶體實際運行速度，無法算出理論上限，所以這裡只給實測值，不做達成率判讀。", 0);

        double peak = AssumedPeakGbps(mtPerSecond, modules);
        double eff = Efficiency(bestGbps, peak);

        if (modules >= 2 && bestGbps < perModule * 1.15)
            return ($"⚠ 實測最高 {FormatGbps(bestGbps)}，幾乎就是「單支」模組的理論上限（{FormatGbps(perModule)}）——"
                  + $"插了 {modules} 支卻只有一支的頻寬，通常是插槽插錯（主機板手冊多半要求先插 A2／B2 那一組），"
                  + "少見的情況是主機板或 CPU 只支援單通道。這是實測推論，不是主機板回報的通道數。", 2);

        if (eff > 0 && eff < 0.45)
            return ($"實測最高 {FormatGbps(bestGbps)}，約為理論上限 {FormatGbps(peak)} 的 {eff:0%}——偏低值得查（背景負載、省電模式、單面／單 rank 模組都會壓低）。"
                  + "理論上限是在「每支模組各佔一個通道」的假設下算的。", 1);

        return ($"實測最高 {FormatGbps(bestGbps)}，約為理論上限 {FormatGbps(peak)} 的 {eff:0%}。"
              + "這類測試拿不到 100% 是正常的（更新、讀寫轉向、控制器排程都要吃掉一部分），60–85% 屬正常範圍。", 0);
    }

    /// <summary>負載延遲的結論：無負載與滿載的延遲差距，是「一有人搶記憶體就卡」的量化證據。</summary>
    public static string SummarizeLoaded(IReadOnlyList<LoadedLatencyRow> rows)
    {
        if (rows.Count == 0)
            return "尚未量測負載延遲。";

        var idle = rows.FirstOrDefault(r => r.Loaders == 0);
        var loaded = rows.Where(r => r.Loaders > 0 && r.LatencyNs > 0).ToList();
        if (idle is null || idle.LatencyNs <= 0 || loaded.Count == 0)
            return "負載延遲資料不足（無負載或滿載其中一邊沒量到），不做對照。";

        var worst = loaded.OrderByDescending(r => r.LatencyNs).First();
        double ratio = worst.LatencyNs / idle.LatencyNs;
        string head = $"無負載 {idle.LatencyNs:0.0} ns → {worst.LoadText}時 {worst.LatencyNs:0.0} ns（{ratio:0.0} 倍，"
                    + $"當時達成 {FormatGbps(worst.Gbps)}）。";

        // 倍數本身不是故障：記憶體被塞滿時延遲一定會漲，這裡只說明它代表什麼
        return ratio >= 3
            ? head + "延遲在重壓下漲到三倍以上，代表記憶體子系統一被吃滿，其他工作的每一次存取都要排隊——"
                   + "這正是「跑大型編譯或轉檔時整台機器都變鈍」的來源。想改善要往通道數與頻率去，不是往 CPU。"
            : head + "壓力下延遲上升是必然的（排隊而已），這個幅度屬於常見範圍。";
    }
}
