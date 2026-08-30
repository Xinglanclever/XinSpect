namespace XinSpect;

/// <summary>「效能天花板」的一列證據：每個結論都附上它的 MSR 出處與原始值，方便讀者自己覆核。</summary>
public sealed class CeilingRow
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    /// <summary>原始證據（MSR 位址、原始值、取用的位元範圍）。空字串＝這列不是直接來自某個暫存器。</summary>
    public string Evidence { get; init; } = "";
    public string Note { get; init; } = "";
    public Severity Severity { get; init; }
    public bool HasEvidence => Evidence.Length > 0;
    public bool HasNote => Note.Length > 0;
}

/// <summary>RAPL 的三個換算單位（MSR 0x606 MSR_RAPL_POWER_UNIT）。</summary>
public readonly record struct RaplUnits(double PowerW, double EnergyJ, double TimeS, bool Valid)
{
    public string Text => Valid
        ? $"功耗 {PowerW:0.####} W／能量 {EnergyJ * 1e6:0.###} µJ／時間 {TimeS * 1e3:0.###} ms"
        : "—";
}

/// <summary>一組封裝功耗上限（PL1 或 PL2）。<see cref="RawCounts"/> 保留原始計數，供覆核換算。</summary>
public readonly record struct PowerLimit(double Watts, bool Enabled, bool Clamped, double WindowSec, int RawCounts);

/// <summary>封裝功耗規格（MSR 0x614 MSR_PKG_POWER_INFO）——這是矽本身宣告的，不是規格書抄來的。</summary>
public readonly record struct PkgPowerInfo(double TdpW, double MinW, double MaxW, bool Valid);

/// <summary>THERM_STATUS 的溫度讀數部分。溫度是「低於 TCC 活化點幾度」，故必須先知道 TCC 才能換算。</summary>
public readonly record struct ThermReadout(bool ReadingValid, int DigitalReadout, int ResolutionC, int TempC, bool TempKnown);

/// <summary>限制原因暫存器的一個位元：狀態位在 <c>Bit</c>，其黏滯紀錄位固定在 <c>Bit + 16</c>。</summary>
public readonly record struct LimitReasonBit(int Bit, string Name, string Meaning);

/// <summary>單一量測窗的結果（一種負載型態對應一筆）。</summary>
public sealed class CeilingWindow
{
    public required string Label { get; init; }
    /// <summary>此窗實際持續的秒數（以 Stopwatch 為準，不是要求值）。</summary>
    public double Seconds { get; init; }
    /// <summary>各實體核心有效倍頻（APERF/MPERF × TSC 倍頻）的平均；0＝量不到。</summary>
    public double MeanRatio { get; init; }
    public double MeanMhz { get; init; }
    /// <summary>逐核最高溫（°C）；<see cref="TempKnown"/> 為 false 時無意義。</summary>
    public int MaxCoreTempC { get; init; }
    public int PkgTempC { get; init; }
    public bool TempKnown { get; init; }
    /// <summary>0x611 能量計在此窗內前進的計數（已處理 32 位回繞）。</summary>
    public uint EnergyCounts { get; init; }
    /// <summary>0x613 封裝功耗節流累計器在此窗內前進的計數。</summary>
    public uint ThrottleCounts { get; init; }
    /// <summary>此窗內 0x64F 狀態位（15:0）的聯集——期間任一取樣看到的限制原因。</summary>
    public ulong ReasonStatusUnion { get; init; }
    /// <summary>此窗內「新亮起」的黏滯紀錄位（末值 &amp; ~起始值），即這段期間才發生的限制。</summary>
    public ulong ReasonNewlyLogged { get; init; }
    /// <summary>封裝 THERM_STATUS（0x1B1）在此窗內新亮起的紀錄位。</summary>
    public ulong PkgThermNewlyLogged { get; init; }
    public int Samples { get; init; }

    /// <summary>
    /// 此窗內「醒著」的實體核心數。由 ΔMPERF ÷ (TSC 速率 × 窗長) ≥ 門檻判定——
    /// MPERF 只在核心處於 C0 時前進，故這是量出來的，不是拿負載執行緒數去猜的。
    /// 倍頻表要查「幾顆核心作用中時該給幾倍」，這個數字錯了整段歸因就跟著錯。
    /// </summary>
    public int ActiveCores { get; init; }

    /// <summary>成功取得 MPERF／APERF 差分的實體核心數（分母）。小於總核心數表示有核心讀取失敗。</summary>
    public int CoresMeasured { get; init; }

    public string LoadNote { get; init; } = "";

    public string CoresText => CoresMeasured > 0 ? $"{ActiveCores} / {CoresMeasured}" : "—";

    public double EnergyRateCps => Seconds > 0 ? EnergyCounts / Seconds : 0;
    public string RatioText => MeanRatio > 0 ? $"{MeanRatio:0.00}×TSC" : "—";
    public string MhzText => MeanMhz > 0 ? $"{MeanMhz:0} MHz" : "—";
    public string TempText => TempKnown ? $"{MaxCoreTempC} °C（封裝 {PkgTempC} °C）" : "—";
}

/// <summary>
/// 「效能天花板」的純解碼層：只做位元解讀與換算，不接觸硬體，故可完整單元測試。
/// 涵蓋 MSR 0x606（RAPL 單位）、0x610（PL1／PL2）、0x614（封裝功耗規格）、0x1A2（溫度目標）、
/// 0x19C／0x1B1（THERM_STATUS）、0x64F（限制原因）與 32 位累計器的回繞安全差分。
/// </summary>
/// <remarks>
/// 誠實界線：<b>只解讀官方文件記載的位元</b>。文件沒寫的位元不編名字，只如實說「未列於文件」並附原始值。
/// 溫度一律是「TCC 活化點減去數位讀數」，讀不到 TCC 就不換算成攝氏，而不是套 100 或 105 進去。
/// </remarks>
public static class CeilingDecoder
{
    /// <summary>解 MSR 0x606：位 3:0＝功耗單位指數、位 12:8＝能量單位指數、位 19:16＝時間單位指數（皆為 1/2^n）。</summary>
    public static RaplUnits DecodeRaplUnits(ulong v)
    {
        if (v == 0) return new RaplUnits(0, 0, 0, false);
        return new RaplUnits(
            1.0 / (1u << (int)(v & 0xF)),
            1.0 / (1u << (int)((v >> 8) & 0x1F)),
            1.0 / (1u << (int)((v >> 16) & 0xF)),
            true);
    }

    /// <summary>
    /// 解 MSR 0x610 的<b>半個暫存器</b>。PL1 在低 32 位、PL2 在高 32 位，兩者位元佈局完全相同：
    /// 位 14:0＝上限計數、位 15＝啟用、位 16＝clamp、位 21:17＝時間窗指數 y、位 23:22＝小數段 f。
    /// 時間窗＝2^y × (1 + f/4) × 時間單位。
    /// </summary>
    public static PowerLimit DecodePowerLimitHalf(uint half, RaplUnits u)
    {
        int raw = (int)(half & 0x7FFF);
        int y = (int)((half >> 17) & 0x1F);
        int f = (int)((half >> 22) & 0x3);
        return new PowerLimit(
            raw * u.PowerW,
            (half & (1u << 15)) != 0,
            (half & (1u << 16)) != 0,
            (1u << y) * (1 + f / 4.0) * u.TimeS,
            raw);
    }

    /// <summary>MSR 0x610 位 63＝鎖定位。為 1 時連 BIOS 都要重開機才能再改，也意味著本工具即使想寫也寫不進去。</summary>
    public static bool PowerLimitLocked(ulong v) => (v & (1UL << 63)) != 0;

    /// <summary>時間窗的人話寫法（不足 1 秒改用毫秒，避免出現「0.00 秒」）。</summary>
    public static string WindowText(double sec)
        => sec <= 0 ? "—" : sec < 1 ? $"{sec * 1e3:0.##} ms" : $"{sec:0.##} 秒";

    /// <summary>
    /// 把一組功耗上限講成人話。判定「等於沒有牆」的依據<b>不是我猜的門檻</b>，
    /// 而是拿它跟封裝自己宣告的最大功耗（0x614）比：上限比矽自己說的極限還高出五成，這條牆就不可能被撞到。
    /// </summary>
    public static CeilingRow DescribePowerLimit(string name, PowerLimit pl, PkgPowerInfo info, string evidence)
    {
        if (!pl.Enabled)
            return new CeilingRow
            {
                Name = name, Value = "未啟用", Evidence = evidence, Severity = Severity.Good,
                Note = $"啟用位為 0，這條牆不存在（暫存器裡仍寫著 {pl.Watts:0} W，但不生效）。",
            };

        bool unlimited = NoWall(pl, info);
        string clamp = pl.Clamped
            ? "clamp = 1：必要時允許降到基頻以下來守住這個上限。"
            : "clamp = 0：守上限時不會降到基頻以下。";

        if (unlimited)
            return new CeilingRow
            {
                Name = name, Value = $"{pl.Watts:0} W／{WindowText(pl.WindowSec)}", Evidence = evidence,
                Severity = Severity.Good,
                Note = "這個上限"
                     + (info.Valid ? $"超過封裝自己宣告的最大功耗 {info.MaxW:0} W 的一倍半" : "高達千瓦等級")
                     + "——BIOS 把它推到編碼上限了，等於沒有功耗牆。" + clamp,
            };

        return new CeilingRow
        {
            Name = name, Value = $"{pl.Watts:0} W／{WindowText(pl.WindowSec)}", Evidence = evidence,
            Severity = info.Valid && pl.Watts < info.TdpW ? Severity.Warning : Severity.Neutral,
            Note = (info.Valid && pl.Watts < info.TdpW
                       ? $"⚠ 這個上限低於封裝宣告的 TDP {info.TdpW:0} W——長時間全核負載會被壓在標稱效能之下。"
                       : "這是實際會生效的上限；超過此功耗達一個時間窗，封裝就會被降頻。")
                 + clamp,
        };
    }

    /// <summary>
    /// 單一功耗上限是不是「等於不存在」：啟用位為 0，或被推到遠高於封裝自己宣告的最大功耗（0x614）。
    /// 門檻只寫在這裡，卡片文字與最終判決共用同一份判斷，免得畫面說「沒有功耗牆」而判決說有。
    /// </summary>
    private static bool NoWall(PowerLimit pl, PkgPowerInfo info)
        => !pl.Enabled || (info.Valid ? pl.Watts > info.MaxW * 1.5 : pl.Watts >= 1000);

    /// <summary>
    /// PL1 與 PL2 是否<b>都</b>等於不存在。<paramref name="pkgLimitRaw"/> 為 0（讀取失敗）時回 false——
    /// 讀不到不等於沒有牆，這裡寧可什麼都不宣稱。
    /// </summary>
    public static bool PowerWallAbsent(ulong pkgLimitRaw, RaplUnits u, PkgPowerInfo info)
        => pkgLimitRaw != 0
        && NoWall(DecodePowerLimitHalf((uint)(pkgLimitRaw & 0xFFFFFFFF), u), info)
        && NoWall(DecodePowerLimitHalf((uint)(pkgLimitRaw >> 32), u), info);

    /// <summary>解 MSR 0x614：位 14:0＝TDP、位 30:16＝最小功耗、位 46:32＝最大功耗（單位皆為功耗單位）。</summary>
    public static PkgPowerInfo DecodePkgPowerInfo(ulong v, RaplUnits u)
        => v == 0 || !u.Valid
            ? new PkgPowerInfo(0, 0, 0, false)
            : new PkgPowerInfo((v & 0x7FFF) * u.PowerW, ((v >> 16) & 0x7FFF) * u.PowerW,
                              ((v >> 32) & 0x7FFF) * u.PowerW, true);

    /// <summary>
    /// 解 MSR 0x1A2：位 23:16＝TCC 活化溫度（°C）、位 29:24＝TCC 偏移（使用者／BIOS 可調的提前量）。
    /// 實際開始節流的溫度＝活化溫度 − 偏移。
    /// </summary>
    public static (int TccC, int OffsetC, int ThrottleAtC, bool Valid) DecodeTemperatureTarget(ulong v)
    {
        int tcc = (int)((v >> 16) & 0xFF);
        int off = (int)((v >> 24) & 0x3F);
        return tcc == 0 ? (0, 0, 0, false) : (tcc, off, tcc - off, true);
    }

    /// <summary>
    /// 解 THERM_STATUS 的溫度部分（0x19C 逐核／0x1B1 封裝共用佈局）：
    /// 位 31＝讀值有效、位 22:16＝數位讀數（<b>低於</b> TCC 活化點幾度）、位 30:27＝解析度（°C）。
    /// TCC 未知時不換算攝氏——寧可顯示「—」，也不套規格書上的 100 或 105。
    /// </summary>
    public static ThermReadout DecodeThermReadout(ulong v, int tccC)
    {
        bool valid = (v & (1UL << 31)) != 0;
        int readout = (int)((v >> 16) & 0x7F);
        int res = (int)((v >> 27) & 0xF);
        bool known = valid && tccC > 0 && v != 0;
        return new ThermReadout(valid, readout, res, known ? tccC - readout : 0, known);
    }

    /// <summary>THERM_STATUS 的八組「狀態位／紀錄位」配對。紀錄位固定在狀態位 + 1。</summary>
    public static readonly (int Bit, string Name)[] ThermPairs =
    [
        (0,  "熱狀態（已達 TCC 活化溫度）"),
        (2,  "PROCHOT#／FORCEPR# 事件"),
        (4,  "臨界溫度（必須立刻降溫）"),
        (6,  "溫度門檻 #1"),
        (8,  "溫度門檻 #2"),
        (10, "功耗限制"),
        (12, "電流限制"),
        (14, "跨網域限制"),
    ];

    /// <summary>
    /// 把 THERM_STATUS 的八組配對列成證據。紀錄位是「只會被設起、不會自己清掉」的黏滯位，
    /// 所以「曾經發生過」涵蓋的是整個開機期間——本工具<b>不寫入</b>，故不會去清它。
    /// </summary>
    public static List<CeilingRow> DescribeThermPairs(ulong v, string label)
    {
        var rows = new List<CeilingRow>(ThermPairs.Length);
        foreach (var (bit, name) in ThermPairs)
        {
            bool active = (v & (1UL << bit)) != 0;
            bool logged = (v & (1UL << (bit + 1))) != 0;
            bool critical = bit == 4;
            rows.Add(new CeilingRow
            {
                Name = name,
                Value = active ? "現在正在發生" : logged ? "曾經發生過" : "從未",
                Evidence = $"{label} 位 {bit}／{bit + 1}",
                Severity = active ? (critical ? Severity.Critical : Severity.Serious)
                         : logged ? (critical ? Severity.Serious : Severity.Warning)
                         : Severity.Good,
                Note = active ? "" : logged ? "自開機以來至少發生過一次；狀態位已回到 0，表示現在沒在發生。" : "",
            });
        }
        return rows;
    }

    /// <summary>
    /// 讀值可信度的守門員：整個暫存器為 0（連溫度讀數欄都是 0）比較像是<b>沒真的讀到</b>，
    /// 而不是「一切正常、從未觸發」。不加這一層，一個壞掉的讀取路徑會被誤讀成滿分健康報告。
    /// </summary>
    public static CeilingRow? ThermSanity(ulong v, string label)
        => v != 0 ? null : new CeilingRow
        {
            Name = "⚠ 讀值可信度",
            Value = "整個暫存器為 0",
            Evidence = $"{label} = 0x0",
            Severity = Severity.Warning,
            Note = "連溫度讀數與「讀值有效」位都是 0——這更像是沒真的讀到這顆暫存器，"
                 + "而不是「從未觸發」。上面那幾列請當成無資料，不要當成健康證明。",
        };

    /// <summary>
    /// 限制原因暫存器（0x64F／0x690 MSR_CORE_PERF_LIMIT_REASONS）中<b>官方有名字</b>的位元。
    /// 位 2、3、7、14、15（及其紀錄位 18、19、23、30、31）Intel 未公開用途，故此表不收，
    /// 由 <see cref="UndocumentedText"/> 如實回報「有動但沒名字」。
    /// </summary>
    public static readonly LimitReasonBit[] LimitReasons =
    [
        new(0,  "PROCHOT#", "外部或內部的過熱訊號被拉低，這是最粗暴的一種降頻。"),
        new(1,  "熱狀態", "核心自己的溫度到了 TCC 活化點。"),
        new(4,  "內顯", "內建顯示核心的限制連動到 CPU 核心（無內顯的平台不會出現）。"),
        new(5,  "自動 HWP", "硬體自主 P-state 決定不給更高頻，通常源於能效偏好設定。"),
        new(6,  "VR 過熱警報", "供電模組（VRM）自己過熱，不是 CPU 過熱——這條要往主機板供電散熱查。"),
        new(8,  "電流上限", "瞬時電流超過 IccMax／EDP 設定，AVX 重載最常撞到這面牆。"),
        new(9,  "核心功耗", "核心網域自己的功耗上限（與封裝級 PL1／PL2 不同層）。"),
        new(10, "封裝功耗 PL1", "長時間功耗上限生效中——這是最常見的「跑久就掉頻」。"),
        new(11, "封裝功耗 PL2", "短時衝刺功耗上限生效中。"),
        new(12, "多核渦輪上限", "作用中核心數對應的倍頻表上限——這不是故障，是規格。"),
        new(13, "渦輪切換衰減", "頻率切換過於頻繁時的抑制機制，屬正常運作。"),
    ];

    /// <summary>狀態位（15:0）中設起的官方名稱。</summary>
    public static List<string> ActiveNames(ulong v)
        => [.. LimitReasons.Where(r => (v & (1UL << r.Bit)) != 0).Select(r => r.Name)];

    /// <summary>紀錄位（31:16）中設起的官方名稱。紀錄位＝狀態位 + 16。</summary>
    public static List<string> LoggedNames(ulong v)
        => [.. LimitReasons.Where(r => (v & (1UL << (r.Bit + 16))) != 0).Select(r => r.Name)];

    /// <summary>把 15:0 之中「有動但官方沒給名字」的位元如實列出，不替 Intel 發明術語。</summary>
    public static string UndocumentedText(ulong v)
    {
        var known = LimitReasons.Select(r => r.Bit).ToHashSet();
        var odd = Enumerable.Range(0, 16).Where(b => (v & (1UL << b)) != 0 && !known.Contains(b)).ToList();
        return odd.Count == 0
            ? ""
            : $"另有位 {string.Join("、", odd)} 為 1，Intel 未公開其用途，故不翻譯也不當成節流證據。";
    }

    /// <summary>位 12（多核渦輪上限）與位 13（切換衰減）屬正常運作，不該染成警示色。</summary>
    public static bool IsBenign(int bit) => bit is 12 or 13;

    /// <summary>
    /// 開機至今的限制原因總表。只列曾經動過的，其餘併成一列講清楚有幾項從未觸發——
    /// 十一列全部攤開只會讓真正動過的那一兩項被淹掉。
    /// </summary>
    public static List<CeilingRow> DescribeReasonRows(ulong v)
    {
        var rows = new List<CeilingRow>();
        int quiet = 0;
        foreach (var r in LimitReasons)
        {
            bool active = (v & (1UL << r.Bit)) != 0;
            bool logged = (v & (1UL << (r.Bit + 16))) != 0;
            if (!active && !logged) { quiet++; continue; }
            rows.Add(new CeilingRow
            {
                Name = r.Name,
                Value = active ? "現在正在限制" : "曾經限制過",
                Evidence = $"位 {r.Bit}（狀態）／位 {r.Bit + 16}（紀錄）",
                Severity = IsBenign(r.Bit) ? Severity.Neutral
                         : active ? Severity.Serious : Severity.Warning,
                Note = r.Meaning,
            });
        }
        if (quiet > 0)
            rows.Add(new CeilingRow
            {
                Name = $"其餘 {quiet} 項原因",
                Value = "自開機以來從未觸發",
                Severity = Severity.Good,
                Note = "紀錄位是黏滯的（設起後不會自己歸零），所以「從未」涵蓋整個開機期間。本工具不寫入，不會去清它。",
            });
        return rows;
    }

    /// <summary>32 位累計器的回繞安全差分。強制轉成 <c>uint</c> 相減，單次回繞也能得到正確增量。</summary>
    public static uint Delta32(ulong start, ulong end) => unchecked((uint)end - (uint)start);

    /// <summary>能量計數 → 瓦數。秒數或單位無效時回 0，不回 NaN。</summary>
    public static double Watts(uint counts, double energyJ, double seconds)
        => seconds <= 0 || energyJ <= 0 ? 0 : counts * energyJ / seconds;

    /// <summary>0x613 的節流累計計數 → 秒數（乘以 RAPL 時間單位）。</summary>
    public static double ThrottledSeconds(uint counts, double timeUnitS)
        => timeUnitS <= 0 ? 0 : counts * timeUnitS;

    /// <summary>能量計要被信任，至少得比基線快這麼多倍——低於此值就當它沒在反映功耗。</summary>
    private const double EnergyRateTrustFactor = 1.15;

    /// <summary>負載算不算真的壓上去，以封裝升溫幾度為準。</summary>
    private const int LoadLandedDeltaC = 8;

    /// <summary>
    /// <b>能量計自我驗證</b>——本頁最重要的一道防線。
    /// RAPL 的 0x611 在某些平台（含本機實測的 Skylake-X）雖然讀得到、也在前進，
    /// 但速率<b>完全不隨真實功耗改變</b>：閒置 46 °C 與全核 AVX2 85 °C 量到的每秒計數只差 4%。
    /// 若照著 0x606 的能量單位硬換算，會得到「18 核滿載 1.2 W」這種荒謬數字，
    /// 而且它會被使用者當成真的。所以這裡先拿基線窗與負載窗互相比對：
    /// 溫度明顯上升卻換不出更高的能量速率，就判定不可信，整頁不顯示瓦數。
    /// </summary>
    public static (bool Trustworthy, string Text) ValidateEnergyCounter(
        double baseRateCps, double loadRateCps, int baseTempC, int loadTempC, bool tempKnown)
    {
        if (baseRateCps <= 0 && loadRateCps <= 0)
            return (false, "0x611 在兩個量測窗內都沒有前進：本平台沒有可用的封裝能量計，故不顯示功耗。");

        double ratio = baseRateCps > 0 ? loadRateCps / baseRateCps : double.PositiveInfinity;
        bool landed = tempKnown && loadTempC - baseTempC >= LoadLandedDeltaC;

        if (ratio >= EnergyRateTrustFactor)
            return (true, $"能量計通過驗證：負載時每秒計數是基線的 {ratio:0.00} 倍"
                        + (tempKnown ? $"（封裝溫度 {baseTempC} → {loadTempC} °C）" : "")
                        + "，確實隨功耗變化，因此下面的瓦數可用。");

        if (landed)
            return (false, $"⚠ 能量計未通過驗證：負載讓封裝從 {baseTempC} °C 升到 {loadTempC} °C，"
                         + $"0x611 的每秒計數卻只變成基線的 {ratio:0.00} 倍（幾乎沒動）。"
                         + "本平台的封裝能量計不反映真實功耗，硬換算會得出荒謬的低瓦數，"
                         + "所以本頁只顯示原始計數，不換算成瓦。");

        return (false, $"能量計無法驗證：負載期間封裝溫度沒有明顯上升"
                     + (tempKnown ? $"（{baseTempC} → {loadTempC} °C）" : "（溫度讀不到）")
                     + "，無從判斷計數變化是不是功耗造成的。保守起見不顯示瓦數。");
    }

    /// <summary>
    /// 目前作用中核心數對應的倍頻上限。取「門檻 ≥ 作用核心數」之中門檻最小的那一組；
    /// 全部門檻都比作用核心數小（例如倍頻表只列到 8 核卻有 18 核在跑）時，取門檻最大的那一組。
    /// </summary>
    public static int ApplicableTurboRatio(IReadOnlyList<(int Cores, int Ratio)> groups, int activeCores)
    {
        if (groups.Count == 0) return 0;
        int best = 0, bestCores = int.MaxValue;
        foreach (var (c, r) in groups)
            if (c >= activeCores && c < bestCores) { best = r; bestCores = c; }
        if (best > 0) return best;
        int maxCores = 0;
        foreach (var (c, r) in groups)
            if (c > maxCores) { maxCores = c; best = r; }
        return best;
    }

    /// <summary>差距在此範圍內（以倍頻計，約 50 MHz）就算「已經到頂」，不當成缺口。</summary>
    private const double RatioTolerance = 0.5;

    /// <summary>判決所需的全部證據。全部由實測與 MSR 直讀而來，沒有任何一項來自規格書。</summary>
    public sealed record CeilingEvidence
    {
        /// <summary>倍頻表在目前作用中核心數下允許的倍頻（0＝讀不到倍頻表）。</summary>
        public int TargetRatio { get; init; }
        /// <summary>最重負載窗實測到的平均倍頻（有效時脈 ÷ BCLK）。</summary>
        public double AchievedRatio { get; init; }
        public double BclkMhz { get; init; }
        public int ActiveCores { get; init; }
        public int MaxTempC { get; init; }
        public int ThrottleAtC { get; init; }
        public bool TempKnown { get; init; }
        public double ThrottledSec { get; init; }
        public double WindowSec { get; init; }
        /// <summary>負載期間<b>新亮起</b>的限制原因名稱（末值 &amp; ~起始值解出來的）。</summary>
        public IReadOnlyList<string> NewReasons { get; init; } = [];
        /// <summary>PL1／PL2 是否都被推到編碼上限，等於沒有功耗牆。</summary>
        public bool PowerWallDisabled { get; init; }
        /// <summary>整數負載與最寬向量負載之間的倍頻落差（＞0 表示存在向量降頻）。</summary>
        public double AvxRatioDrop { get; init; }
        public string WidestVectorLabel { get; init; } = "";
    }

    private static bool Has(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++) if (names[i] == name) return true;
        return false;
    }

    /// <summary>
    /// 判決：目標倍頻與實測倍頻的差距，歸因到具體證據上。
    /// 找不到任何硬體證據時<b>就說找不到</b>，而不是硬挑一個原因湊出結論——
    /// 這種情況下缺口通常在作業系統的電源政策，本頁會把讀者導去該卡片而不是重複讀一遍。
    /// </summary>
    public static (Severity Sev, string Headline, string Detail) Verdict(CeilingEvidence e)
    {
        if (e.AchievedRatio <= 0)
            return (Severity.Neutral, "量不到有效時脈",
                "APERF/MPERF 差分沒有取到有效值（釘選核心失敗或計數器沒前進），因此不做任何判決。");

        double mhz = e.AchievedRatio * e.BclkMhz;
        string measured = $"實測 {e.AchievedRatio:0.0}×"
                        + (e.BclkMhz > 0 ? $"（約 {mhz:0} MHz）" : "")
                        + $"、{e.ActiveCores} 顆核心在跑";

        if (e.TargetRatio <= 0)
            return (Severity.Neutral, $"沒有可比的目標值：{measured}",
                "讀不到倍頻表（MSR 0x1AD），無法算出「這顆 CPU 在這個核心數下本來該跑多少」，"
                + "所以只報實測值，不宣稱有沒有缺口。");

        double gap = e.TargetRatio - e.AchievedRatio;
        string avx = e.AvxRatioDrop >= RatioTolerance
            ? $" 另外，{e.WidestVectorLabel}負載比整數負載低了 {e.AvxRatioDrop:0.0}×"
              + $"（約 {e.AvxRatioDrop * e.BclkMhz:0} MHz）——這是向量指令的授權降頻，寫在矽裡，任何暫存器都讀不到，只能像這樣量出來。"
            : "";

        if (gap <= RatioTolerance)
            return (Severity.Good, $"沒有撞到任何硬體天花板：目標 {e.TargetRatio}×、{measured}",
                $"倍頻表在 {e.ActiveCores} 顆核心作用時允許 {e.TargetRatio}×，實測已經拿到 {e.AchievedRatio:0.0}×，"
                + "差距在量測誤差內。"
                + (e.TempKnown ? $" 最高核心溫度 {e.MaxTempC} °C，距離節流點 {e.ThrottleAtC} °C 還有 {e.ThrottleAtC - e.MaxTempC} °C。" : "")
                + (e.PowerWallDisabled ? " PL1／PL2 都被推到編碼上限，功耗牆等於不存在。" : "")
                + " 也就是說：這顆 CPU 現在給了你它被允許的全部。" + avx);

        // 有缺口：依證據強度排序歸因，第一個成立的就是主因。
        string shortfall = $"缺口 {gap:0.0}×"
                         + (e.BclkMhz > 0 ? $"（約 {gap * e.BclkMhz:0} MHz、{gap / e.TargetRatio * 100:0.#}%）" : "");
        string head = $"目標 {e.TargetRatio}×、{measured} → {shortfall}";

        bool thermal = Has(e.NewReasons, "熱狀態") || Has(e.NewReasons, "PROCHOT#")
                    || (e.TempKnown && e.ThrottleAtC > 0 && e.MaxTempC >= e.ThrottleAtC - 2);
        if (thermal)
            return (Severity.Serious, "溫度牆：" + head,
                (e.TempKnown ? $"最高核心溫度 {e.MaxTempC} °C，節流點是 {e.ThrottleAtC} °C。" : "")
                + (Has(e.NewReasons, "熱狀態") || Has(e.NewReasons, "PROCHOT#")
                    ? "限制原因暫存器在這次負載期間新亮起了熱相關的位元，這是硬證據。"
                    : "溫度已經貼到節流點，雖然這次負載期間沒抓到新亮起的熱位元（取樣間隔可能錯過）。")
                + " 往散熱查：風扇曲線、散熱器接觸、機殼風道、矽脂。" + avx);

        bool power = e.ThrottledSec > 0 || Has(e.NewReasons, "封裝功耗 PL1")
                  || Has(e.NewReasons, "封裝功耗 PL2") || Has(e.NewReasons, "核心功耗");
        if (power)
            return (Severity.Serious, "功耗牆：" + head,
                (e.ThrottledSec > 0 && e.WindowSec > 0
                    ? $"0x613 顯示這段 {e.WindowSec:0.0} 秒裡有 {e.ThrottledSec:0.000} 秒"
                      + $"（{e.ThrottledSec / e.WindowSec * 100:0.#}%）處於封裝功耗節流。"
                    : "限制原因暫存器在這次負載期間新亮起了功耗相關的位元。")
                + " 往 BIOS 的 PL1／PL2 與長／短時功耗上限查；上面那張卡片有目前生效的數字。" + avx);

        if (Has(e.NewReasons, "電流上限"))
            return (Severity.Serious, "電流牆（IccMax／EDP）：" + head,
                "限制原因暫存器新亮起了電流上限位元。這面牆與溫度無關，"
                + "常見於 AVX 重載：瞬時電流碰到 IccMax／EDP 設定就直接降頻。往 BIOS 的電流限制設定查。" + avx);

        if (Has(e.NewReasons, "VR 過熱警報"))
            return (Severity.Serious, "供電模組過熱：" + head,
                "亮起的是 VR 過熱警報，不是 CPU 過熱——主機板的供電區（VRM）自己太燙。"
                + "往供電散熱片、機殼上方氣流、以及主機板供電規格查。" + avx);

        if (Has(e.NewReasons, "自動 HWP"))
            return (Severity.Warning, "硬體自主 P-state 壓著：" + head,
                "限制原因是自動 HWP：硬體自己決定不給更高頻，通常源於能效偏好（EPP／EPB）或作業系統的電源政策。"
                + "這不是散熱或供電問題，往電源計劃與 BIOS 的能效設定查。" + avx);

        if (Has(e.NewReasons, "多核渦輪上限"))
            return (Severity.Neutral, "就是倍頻表本身：" + head,
                "新亮起的是「多核渦輪上限」——意思是限制你的正是倍頻表這一格，不是溫度也不是功耗。"
                + "若目標值與實測值仍有落差，通常是量測窗涵蓋了升頻過程。" + avx);

        if (e.AvxRatioDrop >= RatioTolerance)
            return (Severity.Warning, "向量指令授權降頻：" + head,
                $"沒有任何限制原因位元亮起，但{e.WidestVectorLabel}負載比整數負載低了 {e.AvxRatioDrop:0.0}×。"
                + "寬向量指令會讓核心切到較低的頻率授權等級，這是設計行為，不是故障，"
                + "也是為什麼跑 AVX-512 的實測頻率永遠達不到規格表上的渦輪頻率。");

        return (Severity.Warning, "找不到硬體天花板：" + head,
            "溫度沒貼牆、0x613 沒有累計節流時間、限制原因暫存器在這次負載期間也沒有新亮起任何位元——"
            + "這個缺口不是這顆 CPU 的硬體限制造成的。接下來該看作業系統那一側："
            + "電源計劃的最大處理器狀態、核心停放、以及有沒有其他行程搶走了時間片。"
            + "「電源政策實況」卡片有那些數字，本頁不重複讀一遍。" + avx);
    }

    /// <summary>本頁的界線宣告。放在最上面，讓人在看數字之前先知道這些數字是怎麼來的、以及本頁不做什麼。</summary>
    public const string ScopeNotice =
        "本頁全程唯讀：只讀 MSR、不寫入任何一位元，也不清除任何黏滯紀錄位（清除需要寫入，寫入會毀掉別的工具的證據）。"
        + "量測期間本頁會自己製造 CPU 負載——這是唯一的辦法，因為溫度牆、功耗牆與向量降頻在閒置時完全看不見。"
        + "負載執行緒跑一般優先權並留給取樣執行緒喘息空間；停止後立刻結束。"
        + "所有結論都附上 MSR 位址與原始值，任何一句話你都可以自己覆核。";
}
