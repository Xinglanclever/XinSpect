namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 體質（矽晶品質）評估的純數學層：不接觸硬體，只吃「一組實測 V/F 工作點」，
// 吐出 V/F 斜率、與世代參考線的落差、族內推估百分位與信賴等級。
// 真正的取樣在 SiliconProbeService；把數學抽出來是為了能單元測試。
//
// 為什麼要重寫舊版。舊版只取「取樣窗內最高頻率 ＋ 當下電壓」一個點，套一條寫死的
// Skylake-X 參考線（1.00 V ＠ 4.0 GHz），再用 score = 60 + ΔV/0.05×12 換成分數。
// 那個做法有四處站不住腳：
//   1. 單點分不出「體質好」與「使用者自己降壓過」——後者只是設定，不是矽晶。
//   2. 一條寫死的參考線套到 Alder Lake 或 AMD 上會系統性偏移，而畫面上仍然是
//      一個看起來合理的分數。這種錯比讀不到值危險。
//   3. 「60 分起跳、每 0.05 V 加 12 分」是憑空的線性映射，沒有可解釋的統計意義，
//      也沒有任何不確定度——同一顆晶片兩次量到 78 與 84 分，使用者無從判斷差異是否顯著。
//   4. 峰值頻率那一點正好是最不該用的點：單核睿頻是整條曲線的最右端，外插誤差最大。
//
// 這一版的作法：多點取樣 → 最小平方擬合 V = a + k·f → 在<b>重心頻率</b> f̄ 上與世代
// 參考線相減得到 ΔV（實測量，附標準誤）→ 以族內離散 σ 換算成推估百分位（推論，附 95%
// 區間）。工作點不足、型號不在參考表內、或使用者已套用電壓覆寫／偏移時，只給實測值、
// 不給百分位——寧可空著，也不給一個看起來合理但是錯的分數。
// ───────────────────────────────────────────────────────────────────────────

/// <summary>一個實測 V/F 工作點：某個「同時活躍核心數」階梯上彙總出來的頻率與電壓。</summary>
/// <remarks>
/// 電壓取該階梯的<b>最小值</b>而不是平均：VF 表給的是「這個倍頻要求多少電壓」，
/// 量到的瞬時值只會因為其它核心的短暫需求、SVID 的過衝或量測時序往上跳，不會往下掉。
/// 取最小值等於取那條曲線的底線，比平均更接近「這顆在這個頻率上真正要多少電壓」。
/// </remarks>
public readonly record struct VfPoint(
    int ActiveCores, double FreqGhz, double VoltV, double? TempC, double? PowerW, int Samples)
{
    public string CoresText => $"{ActiveCores}";
    public string FreqText => $"{FreqGhz:0.000} GHz";
    public string VoltText => $"{VoltV:0.0000} V";
    public string TempText => TempC is double t ? $"{t:0} °C" : "—";
    public string PowerText => PowerW is double p ? $"{p:0.0} W" : "—";
    public string SamplesText => $"{Samples}";
}

/// <summary>一條世代參考 V/F 直線：<c>V = V0 + k·f</c>（f 以 GHz 計），外加族內離散 σ。</summary>
/// <remarks>
/// σ 是「同型號不同個體之間，在同一頻率上所需電壓的標準差」。它決定 ΔV 換算成百分位的
/// 尺度，因此一定要跟結果一起印在畫面上——這是整個推估裡最大的一個假設。
/// 參考線本身是<b>世代級</b>典型值，不是逐型號校準；這也是誤差的主要來源，UI 必須說明。
/// </remarks>
public sealed record VfReference(string Name, double V0, double SlopePerGhz, double SigmaV, string Anchor)
{
    /// <summary>不在表內時的具體原因，供 UI 直說（例如伺服器部件的 VF 行為與用戶端差異太大）。</summary>
    public string Why { get; init; } = "";

    public static VfReference NotInTable(string why) => new("", 0, 0, 0, "") { Why = why };

    public bool IsKnown => SlopePerGhz > 0 && SigmaV > 0;
    public double VoltageAt(double ghz) => V0 + SlopePerGhz * ghz;
    public string LineText => IsKnown
        ? $"V ≈ {V0:0.000} ＋ {SlopePerGhz:0.0000} × f(GHz)（錨點 {Anchor}，σ ＝ {SigmaV * 1000:0} mV）"
        : "—";
}

/// <summary>最小平方擬合 <c>V = a + k·f</c> 的結果，含判定係數、殘差與斜率的標準誤。</summary>
public readonly record struct VfFit(
    int Count, double SlopePerGhz, double InterceptV, double R2,
    double ResidualV, double SlopeSe, double MeanFreqGhz, double MeanVoltV, double SpanGhz)
{
    public bool Ok => Count >= 2 && SpanGhz > 0;

    /// <summary>超頻圈慣用的單位：每 100 MHz 需要多少毫伏。</summary>
    public double SlopeMvPer100Mhz => SlopePerGhz * 100;

    /// <summary>斜率的 95% 半寬（mV／100 MHz）。點數 &lt; 3 時無自由度可估，回 0。</summary>
    public double Slope95Mv => SlopeSe * 1.96 * 100;

    /// <summary>
    /// 重心處擬合值的標準誤。OLS 的迴歸線必過重心 (f̄, V̄)，而且預測值的變異數在 f̄ 最小
    /// ——偏離重心越遠、外插誤差越大。所以「這顆在這一代裡的位置」要在 f̄ 上比，不是在
    /// 端點（單核睿頻）上比；舊版正是拿最右端那一點去比，誤差最大的地方。
    /// </summary>
    public double CentroidSe => Count >= 3 ? ResidualV / Math.Sqrt(Count) : 0;
}

/// <summary>這一次量測有多值得相信。等級直接決定要不要給出百分位。</summary>
public enum SiliconConfidence
{
    /// <summary>條件不足，連實測值都不宜解讀。</summary>
    None = 0,
    /// <summary>只夠報實測值；百分位若給出僅供參考。</summary>
    Low = 1,
    /// <summary>工作點與擬合品質尚可。</summary>
    Medium = 2,
    /// <summary>點數、跨距、擬合品質與溫度條件都合格。</summary>
    High = 3,
}

/// <summary>結果表上的一列：名稱、值、以及「這個數字是怎麼來的／代表什麼」。</summary>
public sealed record SiliconMetric(string Name, string Value, string Note);

/// <summary>一次體質特徵化的完整結論。</summary>
public sealed record SiliconAssessment
{
    public bool Ok { get; init; }
    /// <summary>推估的族內百分位（1–99）；0 代表本次不給百分位。</summary>
    public int Percentile { get; init; }
    public bool HasPercentile => Percentile > 0;
    public string PercentileText { get; init; } = "—";
    /// <summary>不給百分位的原因（HasPercentile 為 false 時才有內容）。</summary>
    public string NoPercentileReason { get; init; } = "";
    public string Grade { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Neutral;
    public string Summary { get; init; } = "";
    public SiliconConfidence Confidence { get; init; }
    public string ConfidenceText { get; init; } = "—";
    public Severity ConfidenceSeverity => Confidence switch
    {
        SiliconConfidence.High => Severity.Good,
        SiliconConfidence.Medium => Severity.Warning,
        SiliconConfidence.Low => Severity.Serious,
        _ => Severity.Neutral,
    };
    /// <summary>方法與出處：每個數字是從哪個介面讀來的。</summary>
    public string MethodText { get; init; } = "";
    public IReadOnlyList<SiliconMetric> Metrics { get; init; } = [];
    public IReadOnlyList<VfPoint> Points { get; init; } = [];
    public IReadOnlyList<string> Caveats { get; init; } = [];
    public string CaveatText => string.Join("\n", Caveats.Select(c => "・" + c));
    public bool HasCaveats => Caveats.Count > 0;
}

/// <summary>交給 <see cref="SiliconQuality.Evaluate"/> 的全部素材：工作點 ＋ 量測條件 ＋ 出處。</summary>
public sealed record SiliconInput
{
    public IReadOnlyList<VfPoint> Points { get; init; } = [];
    public MicroarchInfo Uarch { get; init; } = MicroarchProfile.Unknown;
    public CoreKind Kind { get; init; } = CoreKind.Unknown;
    /// <summary>閒置基線：用來把靜態功耗（漏電）與動態功耗分開。</summary>
    public double? IdlePowerW { get; init; }
    public double? IdleVoltV { get; init; }
    public double? IdleTempC { get; init; }
    /// <summary>整趟量測的溫度漂移（最高 − 最低）。漂移大代表各點不在同一條件下取得。</summary>
    public double TempDriftC { get; init; }
    public double? MaxTempC { get; init; }
    public string VoltSource { get; init; } = "";
    public string FreqSource { get; init; } = "";
    public string PowerSource { get; init; } = "";
    public string TempSource { get; init; } = "";
    /// <summary>電壓讀值是否來自 MSR（比感測器快、可高頻取樣，且是核心自己要求的值）。</summary>
    public bool VoltFromMsr { get; init; }
    /// <summary>使用者是否已套用電壓覆寫／偏移——若是，量到的電壓是設定值而不是矽晶的要求。</summary>
    public bool ManualVoltage { get; init; }
    public string ManualVoltageNote { get; init; } = "";
    /// <summary>原廠全核睿頻（GHz）；0 代表讀不到。用來說明取樣落在原廠曲線的哪一段。</summary>
    public double StockAllCoreGhz { get; init; }
    /// <summary>上面那個值實際對應幾顆核（超過 8 核的部件只讀得到 8 核那一格，就要照實說）。</summary>
    public string StockTurboLabel { get; init; } = "全核";
    public double BaseClockMhz { get; init; }
    public bool Aborted { get; init; }
    public string AbortReason { get; init; } = "";
}

public static class SiliconQuality
{
    // ── 世代參考 V/F 線 ────────────────────────────────────────────────────
    //
    // 每一條寫成 V = V0 + k·f，並在 Anchor 裡留下推出 V0 的錨點，讓看到數字的人可以自己
    // 驗算（V0 ＝ 錨點電壓 − k × 錨點頻率）。這些是<b>世代典型</b>而非逐型號校準值：
    // 同一代裡不同 SKU 的 VF 表本來就有差，這正是本頁誤差的主要來源，UI 會照實說。
    //
    // 刻意不收的部件：伺服器／小核專用線（Rapids、Forest、Ridge、-SP、-DE）。它們的頻率
    // 區間與供電設計和同微架構的用戶端差太多，硬套用戶端的線只會得到系統性偏移的百分位。
    private static VfReference Line(string name, double anchorV, double anchorGhz, double k, double sigma)
        => new(name, anchorV - k * anchorGhz, k, sigma, $"{anchorV:0.000} V ＠ {anchorGhz:0.0} GHz");

    /// <summary>挑一條參考線。查不到、或型號屬刻意不收的類別時回 <see cref="VfReference.NotInTable"/>。</summary>
    public static VfReference ReferenceFor(MicroarchInfo info, CoreKind kind = CoreKind.Unknown)
    {
        if (!info.IsKnown)
            return VfReference.NotInTable("認不出微架構（非 Intel Family 6，或型號不在本程式的對照表內），沒有可用的參考線。");

        string p = info.Product;

        // 用戶端大核裡 Skylake 這一支要先攔下來按 Product 分家：HEDT／伺服器（網格互連、頻率較低）
        // 與 14 nm 用戶端（環網、頻率高得多）共用同一個 Uarch 字串，混在一起會讓兩邊都系統性偏移。
        //
        // 這一支也是 CPUID 分不出 HEDT 與伺服器的地方：model 0x55 同時是 Skylake-X 與
        // Cascade Lake-SP。兩者是同一顆晶片、同一個製程，V(f) 這條線本身可以共用——差的是各自
        // 實際工作的頻率區間，而我們是在實測到的頻率上代入這條線，不是在猜頻率，所以共用是站得住的。
        if (info.Uarch == "Skylake")
            return p.Contains("Skylake-X") || p.Contains("Cascade") || p.Contains("Cooper")
                ? Line("Skylake-X／Cascade Lake（14 nm 網格互連）", 1.030, 4.0, 0.0900, 0.035)
                : Line("Skylake／Kaby／Coffee／Comet Lake（14 nm 用戶端）", 1.230, 4.8, 0.0950, 0.035);

        if (p.Contains("Rapids") || p.Contains("Forest") || p.Contains("Ridge")
            || p.Contains("-SP") || p.Contains("-DE"))
            return VfReference.NotInTable(
                $"{p} 屬伺服器／密度部件，頻率區間與供電設計和同微架構的用戶端差異過大，"
                + "硬套用戶端的參考線會得到系統性偏移的百分位，故不給。");

        // 小核（Atom 系）自成一族：同一顆晶片上的大小核不能拿同一條線比。
        if (kind == CoreKind.Efficiency || info.Uarch is "Goldmont" or "Goldmont Plus" or "Tremont")
            return info.Uarch switch
            {
                "Gracemont" => Line("Gracemont 小核", 1.050, 3.8, 0.0800, 0.035),
                "Crestmont" => Line("Crestmont 小核", 1.030, 3.8, 0.0800, 0.035),
                "Skymont" => Line("Skymont 小核", 1.030, 4.0, 0.0800, 0.035),
                "Tremont" => Line("Tremont 小核", 1.050, 3.3, 0.0850, 0.040),
                _ => VfReference.NotInTable($"{info.Uarch} 小核的 VF 行為缺少可靠的公開參考，故不給百分位。"),
            };

        return info.Uarch switch
        {
            "Nehalem" => Line("Nehalem（45 nm）", 1.250, 3.3, 0.1100, 0.050),
            "Westmere" => Line("Westmere（32 nm）", 1.200, 3.6, 0.1050, 0.045),
            "Sandy Bridge" => Line("Sandy Bridge（32 nm）", 1.150, 3.9, 0.1000, 0.040),
            "Ivy Bridge" => Line("Ivy Bridge（22 nm）", 1.150, 4.0, 0.1050, 0.040),
            "Haswell" => Line("Haswell（22 nm，FIVR）", 1.180, 4.2, 0.1050, 0.045),
            "Broadwell" => Line("Broadwell（14 nm，FIVR）", 1.150, 4.0, 0.1000, 0.040),
            "Palm Cove" => Line("Palm Cove（Cannon Lake，10 nm）", 1.100, 3.5, 0.0950, 0.045),
            "Sunny Cove" => Line("Sunny Cove（Ice Lake，10 nm）", 1.150, 4.0, 0.0950, 0.035),
            "Willow Cove" => Line("Willow Cove（Tiger Lake，10SF）", 1.200, 4.5, 0.0950, 0.035),
            "Cypress Cove" => Line("Cypress Cove（Rocket Lake，14 nm 回移）", 1.320, 4.8, 0.1000, 0.040),
            "Golden Cove" => Line("Golden Cove（Alder Lake 大核，Intel 7）", 1.250, 5.0, 0.0900, 0.035),
            "Raptor Cove" => Line("Raptor Cove（Raptor Lake 大核，Intel 7）", 1.300, 5.4, 0.0900, 0.038),
            "Redwood Cove" => Line("Redwood Cove（Meteor Lake 大核，Intel 4）", 1.150, 4.8, 0.0850, 0.035),
            "Lion Cove" => Line("Lion Cove（Arrow／Lunar Lake 大核）", 1.150, 5.2, 0.0850, 0.035),
            _ => VfReference.NotInTable($"{info.Uarch} 尚無可靠的公開 VF 參考線，故只給實測值、不給百分位。"),
        };
    }

    // ── 統計 ────────────────────────────────────────────────────────────────

    /// <summary>對工作點做 V 對 f 的最小平方擬合。點數不足或頻率無跨距時斜率回 0（不硬算）。</summary>
    public static VfFit Fit(IReadOnlyList<VfPoint> pts)
    {
        int n = pts.Count;
        if (n == 0) return default;

        double fbar = pts.Average(x => x.FreqGhz), vbar = pts.Average(x => x.VoltV);
        double span = pts.Max(x => x.FreqGhz) - pts.Min(x => x.FreqGhz);
        double sff = pts.Sum(x => (x.FreqGhz - fbar) * (x.FreqGhz - fbar));
        if (n < 2 || sff <= 1e-9)
            return new VfFit(n, 0, vbar, 0, 0, 0, fbar, vbar, span);

        double sfv = pts.Sum(x => (x.FreqGhz - fbar) * (x.VoltV - vbar));
        double svv = pts.Sum(x => (x.VoltV - vbar) * (x.VoltV - vbar));
        double k = sfv / sff;
        double a = vbar - k * fbar;
        double ssRes = pts.Sum(x => { double r = x.VoltV - (a + k * x.FreqGhz); return r * r; });
        // 殘差標準差要用 n−2 的自由度（估了斜率與截距兩個參數）；n＝2 時完美通過兩點，無殘差可估。
        double resid = n > 2 ? Math.Sqrt(ssRes / (n - 2)) : 0;
        double se = n > 2 ? resid / Math.Sqrt(sff) : 0;
        double r2 = svv > 1e-12 ? Math.Clamp(1 - ssRes / svv, 0, 1) : 0;
        return new VfFit(n, k, a, r2, resid, se, fbar, vbar, span);
    }

    /// <summary>標準常態分布的累積機率。用 Abramowitz &amp; Stegun 7.1.26 的 erf 近似（絕對誤差 &lt; 1.5e-7）。</summary>
    public static double NormalCdf(double z)
    {
        double x = Math.Abs(z) / Math.Sqrt(2);
        double t = 1.0 / (1.0 + 0.3275911 * x);
        double erf = 1.0 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t
                            - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + (z < 0 ? -erf : erf));
    }

    /// <summary>
    /// 把「同頻電壓落差 ΔV」換成族內百分位。ΔV 為負（比世代典型省電）→ 百分位高。
    /// 夾在 1–99：族內分布的尾端本來就沒有樣本支撐，宣稱第 0 或第 100 百分位是過度聲明。
    /// </summary>
    public static int Percentile(double deltaV, double sigmaV)
        => sigmaV <= 0 ? 0 : Math.Clamp((int)Math.Round(NormalCdf(-deltaV / sigmaV) * 100), 1, 99);

    /// <summary>
    /// 有效切換電容 <c>C ＝ P ÷ (V² · f)</c>。P 用瓦、V 用伏、f 用 GHz 時結果的單位剛好是 nF。
    /// 這是 CMOS 動態功耗 <c>P ＝ C·V²·f</c> 的反解，量的是「這顆晶片每個週期要翻多少電荷」。
    /// </summary>
    public static double EffectiveCapacitanceNf(double powerW, double voltV, double freqGhz)
        => voltV > 0 && freqGhz > 0 ? powerW / (voltV * voltV * freqGhz) : 0;

    /// <summary>
    /// 封裝功耗讀值可不可信。判準沿用「效能天花板」那一頁被實機逼出來的做法：拿閒置窗當基線，
    /// 若負載讓封裝明顯升溫（≥8 °C）而功耗讀值卻幾乎沒動（不到基線的 1.15 倍），那就是這個平台的
    /// 封裝能量計不反映真實功耗。本機這一級（Skylake-X）正是如此——閒置與全核滿載只差幾個百分點，
    /// 硬換算會得出「18 核滿載 1.2 W」，連帶讓有效切換電容整個失真。
    /// 電壓與頻率走的是另外幾顆 MSR，不受這件事影響，所以只需要把功耗衍生的那兩列拿掉。
    /// </summary>
    public static (bool Ok, string Note) ValidatePower(double? idleW, double? loadW,
                                                       double? idleC, double? loadC, string source)
    {
        if (idleW is not double i || loadW is not double l || i <= 0 || l <= 0)
            return (false, "");                     // 沒有成對讀值就不談可信度，也不必多嘴
        double ratio = l / i;
        if (ratio >= 1.15) return (true, "");

        bool landed = idleC is double a && loadC is double b && b - a >= 8;
        string why = landed
            ? $"負載讓封裝從 {idleC:0} °C 升到 {loadC:0} °C，功耗讀值卻只從 {i:0.0} W 變成 {l:0.0} W"
              + $"（{ratio:0.00} 倍，幾乎沒動）"
            : $"功耗讀值從閒置 {i:0.0} W 到滿載 {l:0.0} W 只變成 {ratio:0.00} 倍，封裝溫度也沒有明顯上升";
        return (false, $"封裝功耗讀值（{source}）未通過驗證：{why}。這個平台的封裝能量計不反映真實功耗，"
                     + "硬換算會得出荒謬的瓦數，因此本次不列「有效切換電容」與「閒置封裝功耗」"
                     + "——那兩項完全建立在瓦數之上。電壓與頻率走的是另外幾顆 MSR，不受影響。");
    }

    // ── 綜合評定 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 信賴等級。三組門檻分別回答三個問題：點夠不夠多、線擬得準不準、各點是不是在同一條件下取得。
    /// 電壓來源也算進去——走感測器（約 1 秒一筆）時有效取樣數少一個數量級，不該給「高」。
    /// </summary>
    public static SiliconConfidence Grade(SiliconInput input, VfFit fit)
    {
        if (fit.Count < 2 || fit.SpanGhz < 0.05) return SiliconConfidence.None;
        if (fit.Count >= 5 && fit.SpanGhz >= 0.40 && fit.R2 >= 0.90
            && input.TempDriftC <= 15 && input.VoltFromMsr && !input.Aborted)
            return SiliconConfidence.High;
        if (fit.Count >= 4 && fit.SpanGhz >= 0.25 && fit.R2 >= 0.75 && input.TempDriftC <= 25)
            return SiliconConfidence.Medium;
        return SiliconConfidence.Low;
    }

    private static string ConfidenceLabel(SiliconConfidence c, VfFit fit, SiliconInput input) => c switch
    {
        SiliconConfidence.High => $"高（{fit.Count} 點、跨距 {fit.SpanGhz * 1000:0} MHz、R² {fit.R2:0.00}、溫漂 {input.TempDriftC:0} °C）",
        SiliconConfidence.Medium => $"中（{fit.Count} 點、跨距 {fit.SpanGhz * 1000:0} MHz、R² {fit.R2:0.00}）",
        SiliconConfidence.Low => $"低（{fit.Count} 點、跨距 {fit.SpanGhz * 1000:0} MHz、R² {fit.R2:0.00}）",
        _ => "不足",
    };

    private static (string Grade, Severity Sev) GradeOf(int pct) => pct switch
    {
        >= 90 => ("體質優異", Severity.Good),
        >= 75 => ("體質良好", Severity.Good),
        >= 40 => ("體質中等", Severity.Neutral),
        >= 20 => ("體質偏弱", Severity.Warning),
        > 0 => ("體質明顯偏弱", Severity.Serious),
        _ => ("僅實測值", Severity.Neutral),
    };

    /// <summary>把量測條件與工作點組成一份可以直接顯示的結論。純函式，可單元測試。</summary>
    public static SiliconAssessment Evaluate(SiliconInput input)
    {
        string method = Method(input);
        var pts = input.Points.OrderByDescending(p => p.FreqGhz).ToList();
        if (pts.Count == 0)
            return new SiliconAssessment
            {
                Ok = false,
                Grade = "取樣失敗",
                Severity = Severity.Warning,
                ConfidenceText = "不足",
                Summary = string.IsNullOrEmpty(input.AbortReason)
                    ? "沒有取到任何有效的 V/F 工作點：電壓或頻率讀不到。電壓需要 MSR 存取（系統管理員）或可用的感測器來源。"
                    : input.AbortReason,
                MethodText = method,
            };

        var fit = Fit(pts);
        var reference = ReferenceFor(input.Uarch, input.Kind);
        var conf = Grade(input, fit);
        var top = pts.OrderByDescending(p => p.ActiveCores).First();
        var power = ValidatePower(input.IdlePowerW, top.PowerW, input.IdleTempC, top.TempC,
                                  Src(input.PowerSource));
        double dv = reference.IsKnown ? fit.MeanVoltV - reference.VoltageAt(fit.MeanFreqGhz) : 0;
        double se = fit.CentroidSe;

        // 百分位的四道閘：有參考線、點數夠、頻率有跨距、而且電壓沒有被使用者手動改過。
        string block =
            !reference.IsKnown ? reference.Why
            : input.ManualVoltage ? input.ManualVoltageNote
            : fit.Count < 3 ? $"只取到 {fit.Count} 個工作點（需要 3 個以上才能擬合並估殘差），無法判斷落差是體質還是量測雜訊。"
            : fit.SpanGhz < 0.10 ? $"頻率跨距只有 {fit.SpanGhz * 1000:0} MHz：這個平台在負載階梯下幾乎不換頻（倍頻已固定或被鎖定），撐不起一條 V/F 線。"
            : conf == SiliconConfidence.None ? "量測條件不足。"
            : "";
        int pct = block.Length == 0 ? Percentile(dv, reference.SigmaV) : 0;
        var (grade, sev) = GradeOf(pct);

        // 百分位的 95% 區間由重心擬合值的標準誤推出：ΔV 越大百分位越低，故上下界要對調。
        string pctText = "—";
        if (pct > 0)
        {
            pctText = $"第 {pct} 百分位";
            if (se > 0)
            {
                int lo = Percentile(dv + 1.96 * se, reference.SigmaV);
                int hi = Percentile(dv - 1.96 * se, reference.SigmaV);
                if (hi > lo) pctText += $"（95% 區間 {lo}–{hi}）";
            }
        }

        return new SiliconAssessment
        {
            Ok = true,
            Percentile = pct,
            PercentileText = pctText,
            NoPercentileReason = block,
            Grade = grade + (pct > 0 ? "（推估）" : "（不給百分位）"),
            Severity = sev,
            Confidence = conf,
            ConfidenceText = ConfidenceLabel(conf, fit, input),
            Summary = Summary(input, fit, reference, dv, se, pct, block),
            MethodText = method,
            Metrics = BuildMetrics(input, fit, reference, dv, se, power.Ok),
            Points = pts,
            Caveats = BuildCaveats(input, fit, reference, power.Note),
        };
    }

    private static string Method(SiliconInput input)
        => "方法：用 1／2／4… 顆實體核心的負載階梯，讓處理器自己走過睿頻表的各段——"
         + "全程唯讀，不寫入任何倍頻或電壓。逐段取「該段站得住的最低電壓」與「有效頻率」湊成一個工作點，"
         + "再以最小平方擬合 V ＝ a ＋ k·f，在重心頻率上與世代參考線相減。\n"
         + $"出處：頻率 {Src(input.FreqSource)}；電壓 {Src(input.VoltSource)}；"
         + $"功耗 {Src(input.PowerSource)}；溫度 {Src(input.TempSource)}。";

    private static string Src(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string Summary(SiliconInput input, VfFit fit, VfReference r,
                                  double dv, double se, int pct, string block)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"在重心工作點 {fit.MeanFreqGhz:0.000} GHz 上，這顆要 {fit.MeanVoltV:0.0000} V");
        if (r.IsKnown)
        {
            string dir = dv < 0 ? "低" : "高";
            sb.Append($"，較 {r.Name} 的世代典型{dir} {Math.Abs(dv) * 1000:0.0} mV");
            if (se > 0) sb.Append($"（±{se * 1000:0.0} mV）");
        }
        sb.Append('。');
        if (fit.SlopePerGhz > 0)
            sb.Append($" V/F 斜率 {fit.SlopeMvPer100Mhz:0.0} mV／100 MHz");
        if (fit.Slope95Mv > 0) sb.Append($"（±{fit.Slope95Mv:0.0}）");
        if (fit.SlopePerGhz > 0) sb.Append("——這是往上加頻率的電壓代價，越平越好。");
        if (pct > 0)
            sb.Append($" 以族內離散 σ ＝ {r.SigmaV * 1000:0} mV 推估，落在同代同頻分布的第 {pct} 百分位。");
        else if (block.Length > 0)
            sb.Append(" 本次不給百分位：" + block);
        if (input.Aborted && input.AbortReason.Length > 0)
            sb.Append(' ').Append(input.AbortReason);
        return sb.ToString();
    }

    private static List<SiliconMetric> BuildMetrics(SiliconInput input, VfFit fit, VfReference r,
                                                    double dv, double se, bool powerOk)
    {
        var m = new List<SiliconMetric>
        {
            new("重心工作點 f̄ ／ V̄",
                $"{fit.MeanFreqGhz:0.000} GHz ／ {fit.MeanVoltV:0.0000} V",
                "迴歸線必過重心，而且預測誤差在這一點最小——比較就在這裡做，不在端點做。"),
        };

        if (r.IsKnown)
            m.Add(new("同頻電壓落差 ΔV",
                $"{dv * 1000:+0.0;-0.0;0.0} mV" + (se > 0 ? $" ± {se * 1000:0.0}" : ""),
                "實測量（不是推估）：負值代表同一頻率下所需電壓低於世代典型，也就是體質好。"));

        if (fit.SlopePerGhz > 0)
            m.Add(new("V/F 斜率 k",
                $"{fit.SlopeMvPer100Mhz:0.0} mV／100 MHz" + (fit.Slope95Mv > 0 ? $"（95% ±{fit.Slope95Mv:0.0}）" : ""),
                "曲線陡度。越平代表往上拉頻率的電壓代價越小，超頻空間也越大。"));

        m.Add(new("擬合品質",
            $"R² {fit.R2:0.000} ／ {fit.Count} 點 ／ 跨距 {fit.SpanGhz * 1000:0} MHz"
            + (fit.ResidualV > 0 ? $" ／ 殘差 {fit.ResidualV * 1000:0.0} mV" : ""),
            "R² 低或跨距小，代表這些點撐不起一條線——此時斜率與百分位都不該當真。"));

        // 有效切換電容：扣掉閒置功耗才是動態部分。取全核（最多活躍核心）那一階最具代表性。
        // 只有在封裝功耗讀值通過驗證時才列——瓦數不可信的平台上，這個數字會荒謬到誤導人。
        var top = input.Points.OrderByDescending(p => p.ActiveCores).FirstOrDefault();
        if (powerOk && top.PowerW is double pw && pw > 0 && top.VoltV > 0 && top.FreqGhz > 0)
        {
            double dyn = input.IdlePowerW is double ip && ip > 0 && ip < pw ? pw - ip : pw;
            double nf = EffectiveCapacitanceNf(dyn, top.VoltV, top.FreqGhz);
            m.Add(new("有效切換電容 C_eff",
                $"{nf:0.0} nF（{top.ActiveCores} 核 ／ {dyn:0.0} W 動態）",
                "由 P ＝ C·V²·f 反解。已扣掉閒置功耗，但仍含負載下的漏電，屬上限值；同代之間越小越省電。"));
        }

        if (powerOk && input.IdlePowerW is double idle && idle > 0)
            m.Add(new("閒置封裝功耗",
                $"{idle:0.0} W"
                + (input.IdleVoltV is double iv ? $" ＠ {iv:0.000} V" : "")
                + (input.IdleTempC is double it ? $"、{it:0} °C" : ""),
                "漏電的代理指標：同代同溫下越低通常漏電越小。Uncore 與 I/O 也算在裡面，只能粗看。"));

        m.Add(new("溫度條件",
            (input.MaxTempC is double mt ? $"最高 {mt:0} °C" : "—") + $" ／ 整趟漂移 {input.TempDriftC:0} °C",
            "漏電隨溫度上升，溫漂大就代表各點不是在同一條件下取得，斜率會被拉偏。"));

        if (input.StockAllCoreGhz > 0)
            m.Add(new($"原廠{input.StockTurboLabel}睿頻",
                $"{input.StockAllCoreGhz:0.000} GHz"
                + (input.BaseClockMhz > 0 ? $"（外頻 {input.BaseClockMhz:0.0} MHz 實測）" : ""),
                "由 MSR 0xCE／0x1AD 讀出的原廠倍頻表，用來判斷取樣是否落在原廠曲線範圍內。"));

        m.Add(new("參考線", r.IsKnown ? r.LineText : "—",
            r.IsKnown ? r.Name : r.Why));
        return m;
    }

    private static List<string> BuildCaveats(SiliconInput input, VfFit fit, VfReference r, string powerNote)
    {
        var c = new List<string>
        {
            "這不是 ASUS SP 或 Intel 官方的分箱值。那些是 BIOS 直接讀晶片內建 VF 表算出來的；"
            + "本頁是從處理器實際走到的工作點反推曲線位置，兩者不可互相換算。",
        };
        if (powerNote.Length > 0) c.Add(powerNote);
        if (r.IsKnown)
            c.Add($"參考線「{r.Name}」是世代典型值，不是逐型號校準。同一代不同 SKU 的 VF 表本來就有差，"
                + "這是本頁最大的誤差來源；ΔV 是實測量，百分位是套上假設分布之後的推論。");
        else if (r.Why.Length > 0)
            c.Add(r.Why);

        if (input.ManualVoltage && input.ManualVoltageNote.Length > 0)
            c.Add(input.ManualVoltageNote);
        if (!input.VoltFromMsr)
            c.Add("電壓走感測器（更新率約 1 秒），有效取樣數比走 MSR 少一個數量級，"
                + "斜率與 ΔV 的不確定度都會偏大。以系統管理員身分執行可改走 MSR 0x198。");
        if (input.TempDriftC > 20)
            c.Add($"整趟溫度漂移 {input.TempDriftC:0} °C：各點不在同一熱條件下取得，"
                + "而漏電隨溫度上升，斜率會被系統性拉偏。冷機後重測會更一致。");
        if (fit.Count is >= 2 and < 3)
            c.Add($"只有 {fit.Count} 個工作點：兩點必然完美通過一條線，R² 與殘差都沒有意義。");
        if (input.Aborted && input.AbortReason.Length > 0)
            c.Add(input.AbortReason);
        return c;
    }
}
