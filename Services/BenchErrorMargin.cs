namespace XinSpect;

/// <summary>
/// 跑分誤差值計算：從歷次同設定成績推算標準差、變異係數（CV%）與 95% 信賴區間。
/// </summary>
/// <remarks>
/// 一個分數不附帶誤差範圍就像量體溫不講幾度——它看起來精確到個位數，
/// 但同一台機器連跑三次就會不一樣。這裡把「同設定量了幾次、每次差多少」
/// 壓成一行「±X%」或「±X.XX 秒」，讓使用者一眼看出數字有多穩。
/// 可信度分三檔：CV &lt; 2% 為高、2–8% 為中、&gt; 8% 為低（對齊既有的重複性門檻）。
/// 不足兩次量測時直說「尚無法估算」，不假裝那個數字有代表性。
/// </remarks>
public static class BenchErrorMargin
{
    /// <summary>CV% 門檻：低於此為「高」可信度。</summary>
    private const double CvHighThreshold = 2.0;
    /// <summary>CV% 門檻：低於此為「中」可信度。</summary>
    private const double CvMediumThreshold = 8.0;

    /// <summary>
    /// 格式化誤差值：依值的量級回傳「±2.3%」（百分比模式）或「±0.15 秒」（絕對值模式）。
    /// </summary>
    /// <param name="values">同設定的歷次量測值。</param>
    /// <param name="unit">單位（如「秒」「分」），用於絕對值模式。為空或 null 時一律用百分比。</param>
    /// <param name="format">數值格式字串（如 "0.000"）；為空時預設 "#,0"。</param>
    /// <returns>誤差範圍文字；不足兩次時回傳空字串。</returns>
    public static string FormatMargin(IReadOnlyList<double> values, string? unit = null, string? format = null)
    {
        if (values is null || values.Count < 2) return "";

        double mean = Mean(values);
        if (mean == 0) return "";

        double sd = StdDev(values, mean);
        double cv = sd / Math.Abs(mean) * 100;

        // 百分比模式：適用於大數值（綜合分數、kN/s）或沒有單位的場合
        // 絕對值模式：適用於耗時類（秒），使用者更直覺「±0.05 秒」而非「±0.3%」
        bool useAbsolute = !string.IsNullOrEmpty(unit) && IsTimeUnit(unit);
        if (useAbsolute)
        {
            string fmt = string.IsNullOrEmpty(format) ? "0.000" : format;
            return $"±{BenchFormat.Value(sd, fmt, unit)}";
        }
        return $"±{cv:0.0}%";
    }

    /// <summary>
    /// 格式化 95% 信賴區間：平均值 ± t·(s/√n) 的上下界。
    /// </summary>
    /// <param name="values">同設定的歷次量測值。</param>
    /// <param name="format">數值格式字串。</param>
    /// <param name="unit">單位。</param>
    /// <returns>信賴區間文字；不足兩次時回傳空字串。</returns>
    public static string FormatConfidenceInterval(IReadOnlyList<double> values, string? format = null, string? unit = null)
    {
        if (values is null || values.Count < 2) return "";

        double mean = Mean(values);
        double sd = StdDev(values, mean);
        double t = TValue95(values.Count - 1);
        double margin = t * sd / Math.Sqrt(values.Count);

        string fmt = string.IsNullOrEmpty(format) ? "#,0" : format;
        string lo = BenchFormat.Value(mean - margin, fmt, null);
        string hi = BenchFormat.Value(mean + margin, fmt, null);
        string u = string.IsNullOrEmpty(unit) ? "" : " " + unit;
        return $"95% 信賴區間：{lo}–{hi}{u}";
    }

    /// <summary>
    /// 依 CV% 判定可信度等級：高（&lt; 2%）、中（2–8%）、低（&gt; 8%）。
    /// </summary>
    /// <param name="values">同設定的歷次量測值。</param>
    /// <returns>「高」/「中」/「低」；不足兩次時回傳空字串。</returns>
    public static string FormatConfidence(IReadOnlyList<double> values)
    {
        if (values is null || values.Count < 2) return "";

        double mean = Mean(values);
        if (mean == 0) return "";

        double cv = CvPercent(values, mean);
        return cv < CvHighThreshold ? "高"
             : cv < CvMediumThreshold ? "中"
             : "低";
    }

    /// <summary>變異係數 CV%。</summary>
    public static double CvPercent(IReadOnlyList<double> values, double? precomputedMean = null)
    {
        if (values is null || values.Count < 2) return 0;
        double mean = precomputedMean ?? Mean(values);
        if (mean == 0) return 0;
        return StdDev(values, mean) / Math.Abs(mean) * 100;
    }

    /// <summary>算術平均。</summary>
    internal static double Mean(IReadOnlyList<double> values)
    {
        double sum = 0;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return values.Count > 0 ? sum / values.Count : 0;
    }

    /// <summary>母體標準差（分母 n−1，即樣本標準差）。</summary>
    internal static double StdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count < 2) return 0;
        double ss = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double d = values[i] - mean;
            ss += d * d;
        }
        return Math.Sqrt(ss / (values.Count - 1));
    }

    /// <summary>
    /// 95% 雙尾 t 分布臨界值（自由度 1–29 查表，30+ 近似 1.96）。
    /// </summary>
    private static double TValue95(int df)
    {
        if (df <= 0) return 12.706;
        ReadOnlySpan<double> table =
        [
            12.706, 4.303, 3.182, 2.776, 2.571,   //  1– 5
             2.447, 2.365, 2.306, 2.262, 2.228,   //  6–10
             2.201, 2.179, 2.160, 2.145, 2.131,   // 11–15
             2.120, 2.110, 2.101, 2.093, 2.086,   // 16–20
             2.080, 2.074, 2.069, 2.064, 2.060,   // 21–25
             2.056, 2.052, 2.048, 2.045,          // 26–29
        ];
        return df <= table.Length ? table[df - 1] : 1.96;
    }

    /// <summary>是否為耗時單位（用絕對值模式較直覺）。</summary>
    private static bool IsTimeUnit(string unit)
        => unit is "秒" or "ms" or "s" or "µs" or "ns";
}
