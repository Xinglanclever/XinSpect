using System.Management;

namespace XinSpect;

/// <summary>
/// 儲存與情境收口（Vol2 第 313–322 項）：CM-021 儲存磨損／TBW（<c>st.wear</c>）、
/// ST-002 SMART 掉電歷史（<c>st.powerloss</c>）、ST-001 NVMe sanitize 能力（<c>st.sanitize.cap</c>）、
/// ST-001 NVMe 錯誤列舉（<c>st.nvme.err</c>）、ST-001 NVMe 補充能力（<c>st.nvme.extra</c>）、
/// SC-005 風扇軸承哨兵（<c>sc.fanbearing</c>）、SC-008 溫度分位數基線（<c>sc.tbaseline</c>）、
/// SC-003 開機階段（<c>ms.boot.summary</c>）、SC-006 換零件對照（<c>sc.swap</c>）、
/// BL-003 壽命預估整合（<c>bl.life</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>來源與界線：</b>儲存面重用 <see cref="NvmeHealth"/>（Identify／Health Log 純解碼）與
/// <see cref="StorageSmartService"/>（SMART／NVMe IOCTL 唯讀通路）；情境面重用既有量測服務的結果。
/// <b>全程唯讀</b>——不發 sanitize 命令、不清 SMART、不送 SMART 命令以外任何寫入。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 磨損／TBW 是<b>估算</b>不是斷言——以 PercentageUsed 為主、TBW 為輔，標明估計；
/// ② 掉電計數是裝置自報的<b>累計值</b>，非本工具的觀察，沒有「多久掉一次」的推論；
/// ③ sanitize 只讀<b>能力位</b>，永不發命令；
/// ④ 查無不代填、讀不到一律三態並附原因；
/// ⑤ 不列磁碟序號、路徑或事件內容。
/// </para>
/// </remarks>
public static class StorageAndScenarioReconciliationFactsService
{
    private const string CatStorage = "儲存";
    private const string CatScenario = "情境";
    private const string CatBaseline = "自基線";

    // CM-021 儲存磨損／TBW
    public const string WearKey = "st.wear.summary";
    // ST-002 SMART 掉電歷史
    public const string PowerLossKey = "st.powerloss";
    // ST-001 NVMe sanitize 能力
    public const string SanitizeKey = "st.sanitize.cap";
    // ST-001 NVMe 錯誤列舉
    public const string NvmeErrorKey = "st.nvme.err";
    // ST-001 NVMe 補充能力
    public const string NvmeExtraKey = "st.nvme.extra";
    // SC-005 風扇軸承哨兵
    public const string FanBearingKey = "sc.fanbearing";
    // SC-008 溫度分位數基線
    public const string ThermalBaselineKey = "sc.tbaseline";
    // SC-003 開機階段
    public const string BootPhasesKey = "ms.boot.summary";
    // SC-006 換零件對照
    public const string SwapKey = "sc.swap";
    // BL-003 壽命預估整合
    public const string LifeKey = "bl.life";

    // ──────────────────────────── 探針接縫（測試注入） ────────────────────────────

    /// <summary>單顆磁碟的磨損快照：PercentageUsed（0–255）＋已寫入 GiB＋通電小時。</summary>
    public readonly record struct WearSample(byte PercentageUsed, double WrittenGiB, long PowerOnHours, byte? AvailableSparePercent);

    public delegate IReadOnlyList<WearSample>? WearProbe();
    public delegate (ulong UnsafeShutdowns, ulong PowerCycles)? PowerLossProbe();
    public delegate bool? SanitizeProbe();
    public delegate bool? NvmePresentProbe();
    /// <summary>（風扇 RPM 序列, 對應溫度序列）——至少兩點才判讀趨勢。</summary>
    public delegate (IReadOnlyList<double> Rpm, IReadOnlyList<double> TempC)? FanProbe();
    /// <summary>歷史溫度序列（°C）。</summary>
    public delegate IReadOnlyList<double>? TempSeriesProbe();
    /// <summary>（開機階段名稱, 毫秒）序列。</summary>
    public delegate IReadOnlyList<(string Phase, double Ms)>? BootPhaseProbe();
    /// <summary>兩份快照的相異鍵數（換零件對照：由時間膠囊差異推導）。</summary>
    public delegate (int Changed, int Added, int Removed)? SwapProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        WearProbe? wear = null,
        PowerLossProbe? powerLoss = null,
        SanitizeProbe? sanitize = null,
        FanProbe? fan = null,
        TempSeriesProbe? temps = null,
        BootPhaseProbe? boot = null,
        SwapProbe? swap = null)
    {
        var facts = new List<HardwareFact>(10)
        {
            WearFact(at, wear),
            PowerLossFact(at, powerLoss),
            SanitizeFact(at, sanitize),
            NvmeErrorFact(at, wear),
            NvmeExtraFact(at, wear),
            FanBearingFact(at, fan),
            ThermalBaselineFact(at, temps),
            BootPhasesFact(at, boot),
            SwapFact(at, swap),
        };
        facts.Add(LifeFact(at, facts));
        return facts;
    }

    // ──────────────────────────── CM-021 儲存磨損／TBW ────────────────────────────

    public static HardwareFact WearFact(DateTimeOffset at, WearProbe? probe = null)
    {
        IReadOnlyList<WearSample>? rows;
        try { rows = (probe ?? ReadWear)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, WearKey, CatStorage, "儲存磨損／TBW", FactAvailability.ReadError,
                "NVMe／SMART 磨損讀值不可用——讀不到就是不猜");
        if (rows.Count == 0)
            return Unavailable(at, WearKey, CatStorage, "儲存磨損／TBW", FactAvailability.NotApplicable,
                "沒有可讀磨損的磁碟——不適用不代表健康");

        byte maxUsed = rows.Max(r => r.PercentageUsed);
        double totalWritten = rows.Sum(r => r.WrittenGiB);
        byte? minSpare = rows.Where(r => r.AvailableSparePercent is not null)
            .Select(r => r.AvailableSparePercent!.Value).DefaultIfEmpty().Min();
        string spareText = minSpare is { } sp ? $"，最低可用備用 {sp}%" : "";

        return new HardwareFact(WearKey, CatStorage, "儲存磨損／TBW",
            $"磁碟 {rows.Count} 顆、最高磨損 {maxUsed}%、累計寫入 {totalWritten:0.#} GiB{spareText}。" +
            "磨損百分比是<b>裝置自報的估計</b>，不是本工具的量測；TBW 未達 100% 也不代表不會故障——" +
            "磨損是趨勢不是壽命判決。",
            "%", "NVMe Health Log／SMART（唯讀）", FactTrustLevel.Reported, false, at, maxUsed);
    }

    private static IReadOnlyList<WearSample>? ReadWear()
    {
        try
        {
            var list = new List<WearSample>();
            for (int i = 0; i < 16; i++)
            {
                var h = StorageSmartService.TryReadNvmeHealth(i);
                if (h is not { } s) continue;
                list.Add(new WearSample(s.PercentageUsed, s.DataWrittenGiB,
                    (long)s.PowerOnHours, s.AvailableSparePercent));
            }
            return list;
        }
        catch { return null; }
    }

    // ──────────────────────────── ST-002 SMART 掉電歷史 ────────────────────────────

    public static HardwareFact PowerLossFact(DateTimeOffset at, PowerLossProbe? probe = null)
    {
        (ulong UnsafeShutdowns, ulong PowerCycles)? s;
        try { s = (probe ?? ReadPowerLoss)(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, PowerLossKey, CatStorage, "非預期斷電計數", FactAvailability.ReadError,
                "NVMe 掉電計數不可讀——讀不到就是不猜，不畫成 0 次");

        var v = s.Value;
        double ratio = v.PowerCycles > 0 ? 100.0 * v.UnsafeShutdowns / v.PowerCycles : 0;
        return new HardwareFact(PowerLossKey, CatStorage, "非預期斷電計數",
            $"非預期斷電 {v.UnsafeShutdowns} 次／通電循環 {v.PowerCycles} 次（{(v.PowerCycles > 0 ? $"{ratio:0.#}%" : "比例不可算")}）。" +
            "這是<b>裝置自開機以來累計</b>的自報值，不是本工具的觀察；沒有「多久掉一次」的推論——" +
            "累計值無法還原發生時間。",
            "次", "NVMe Health Log（唯讀）", FactTrustLevel.Reported, false, at, (double)v.UnsafeShutdowns);
    }

    private static (ulong, ulong)? ReadPowerLoss()
    {
        try
        {
            ulong unsafeSum = 0, cycleSum = 0; bool any = false;
            for (int i = 0; i < 16; i++)
            {
                var h = StorageSmartService.TryReadNvmeHealth(i);
                if (h is not { } s) continue;
                unsafeSum += s.UnsafeShutdowns; cycleSum += s.PowerCycles; any = true;
            }
            return any ? (unsafeSum, cycleSum) : null;
        }
        catch { return null; }
    }

    // ──────────────────────────── ST-001 NVMe sanitize 能力 ────────────────────────────

    public static HardwareFact SanitizeFact(DateTimeOffset at, SanitizeProbe? probe = null)
    {
        bool? capable;
        try { capable = (probe ?? ReadSanitize)(); }
        catch { capable = null; }

        if (capable is null)
            return new HardwareFact(SanitizeKey, CatStorage, "NVMe Sanitize 能力", "", "",
                "NVMe Identify Controller（唯讀）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "未對準 Identify 位移出處前不出值——查無不代填");

        return new HardwareFact(SanitizeKey, CatStorage, "NVMe Sanitize 能力",
            capable.Value
                ? "控制器宣告支援 sanitize。<b>本工具永不發 sanitize 命令</b>——它會抹除整顆碟；" +
                  "這裡只陳述能力位，不是建議、也不會執行。"
                : "控制器未宣告 sanitize 能力（或本機無 NVMe）。本工具永不發 sanitize 命令。",
            "", "NVMe Identify Controller（唯讀）", FactTrustLevel.Reported, false, at, capable.Value ? 1 : 0);
    }

    /// <summary>
    /// Identify Controller 的 SANICAP 欄位（ONCS 附近）位移未經真實傾印核對前，刻意回 null——
    /// 「未對準出處前不出值」是本專案紅線，寧可 NotApplicable 也不猜位元組。
    /// </summary>
    private static bool? ReadSanitize() => null;

    // ──────────────────────────── ST-001 NVMe 錯誤列舉 ────────────────────────────

    public static HardwareFact NvmeErrorFact(DateTimeOffset at, WearProbe? probe = null)
    {
        IReadOnlyList<WearSample>? rows;
        try { rows = (probe ?? ReadWear)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, NvmeErrorKey, CatStorage, "NVMe 錯誤列舉", FactAvailability.ReadError,
                "NVMe 健康紀錄不可讀——讀不到就是不猜");

        // 以「可用備用低於門檻」作為需要留意的代理指標；不單獨宣稱「有錯誤」。
        int lowSpare = rows.Count(r => r.AvailableSparePercent is { } sp && sp <= 10);
        return new HardwareFact(NvmeErrorKey, CatStorage, "NVMe 錯誤列舉",
            lowSpare > 0
                ? $"{lowSpare} 顆磁碟的可用備用空間 ≤ 10%——這是<b>需留意</b>的訊號，不是故障判決（細節看磁碟健康頁）。"
                : "目前沒有磁碟可用備用 ≤ 10%。讀不到錯誤記錄時不畫成「無錯誤」——沒量到不等於沒有。",
            "顆", "NVMe Health Log（唯讀）", FactTrustLevel.Reported, false, at, lowSpare);
    }

    // ──────────────────────────── ST-001 NVMe 補充能力 ────────────────────────────

    public static HardwareFact NvmeExtraFact(DateTimeOffset at, WearProbe? probe = null)
    {
        IReadOnlyList<WearSample>? rows;
        try { rows = (probe ?? ReadWear)(); }
        catch { rows = null; }

        if (rows is null)
            return Unavailable(at, NvmeExtraKey, CatStorage, "NVMe 補充能力", FactAvailability.ReadError,
                "NVMe 健康紀錄不可讀——讀不到就是不猜");

        double totalHours = rows.Sum(r => r.PowerOnHours);
        double avgHours = rows.Count > 0 ? totalHours / rows.Count : 0;
        return new HardwareFact(NvmeExtraKey, CatStorage, "NVMe 補充能力",
            $"平均通電 {avgHours:0} 小時（{rows.Count} 顆）。PLP／原子寫／LBA 格式等 Identify 欄位須先對準" +
            "真實傾印位移才出值——本版查無出處的欄位一律留白，不猜。",
            "小時", "NVMe Health Log（唯讀）", FactTrustLevel.Reported, false, at, avgHours);
    }

    // ──────────────────────────── SC-005 風扇軸承哨兵 ────────────────────────────

    public static HardwareFact FanBearingFact(DateTimeOffset at, FanProbe? probe = null)
    {
        (IReadOnlyList<double> Rpm, IReadOnlyList<double> TempC)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Value.Rpm.Count < 2)
            return Unavailable(at, FanBearingKey, CatScenario, "風扇軸承哨兵", FactAvailability.NotApplicable,
                "風扇 RPM 與溫度的配對樣本少於兩點——不足以判讀關係漂移，不從單點宣稱");

        var (rpm, temp) = s.Value;
        // 軸承退化會讓「同溫下 RPM 偏高」——以 RPM 自身序列的漂移做哨兵（單序列 Theil–Sen 斜率）；
        // 溫度序列只用來確認配對樣本足夠。斜率為正＝轉速隨時間上升的傾向。
        double slope = TrendSentinel.Slope(rpm).SlopePerStep;
        return new HardwareFact(FanBearingKey, CatScenario, "風扇軸承哨兵",
            $"RPM 漂移斜率 {slope:0.###}（{rpm.Count} 點、配對溫度 {temp.Count} 點）。上升代表同樣條件下需要更高轉速——" +
            "<b>退化訊號不是判決</b>：積灰、室溫、進氣受阻都會有同樣表現；需跨多輪比對才談得上趨勢。",
            "RPM/步", "既有哨兵統計（唯讀）", FactTrustLevel.Derived, false, at, slope);
    }

    // ──────────────────────────── SC-008 溫度分位數基線 ────────────────────────────

    public static HardwareFact ThermalBaselineFact(DateTimeOffset at, TempSeriesProbe? probe = null)
    {
        IReadOnlyList<double>? series;
        try { series = (probe ?? (() => null))(); }
        catch { series = null; }

        if (series is null || series.Count < 8)
            return Unavailable(at, ThermalBaselineKey, CatScenario, "溫度分位數基線", FactAvailability.NotApplicable,
                "溫度歷史樣本不足（需 ≥8 點）——樣本量不足以建立季節基線，不宣稱");

        var sorted = series.OrderBy(x => x).ToList();
        double Median(IReadOnlyList<double> a) => a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2;
        double p25 = sorted[(int)(sorted.Count * 0.25)], p50 = Median(sorted), p75 = sorted[(int)(sorted.Count * 0.75)];
        return new HardwareFact(ThermalBaselineKey, CatScenario, "溫度分位數基線",
            $"樣本 {series.Count} 點：P25 {p25:0.#}／中位 {p50:0.#}／P75 {p75:0.#} °C。" +
            "<b>基線樣本量已聲明</b>；本機與季節的基線需要長時間累積——樣本少時分位數不穩定，不做紅黃判決。",
            "°C", "歷史溫度序列（唯讀）", FactTrustLevel.Derived, false, at, p50);
    }

    // ──────────────────────────── SC-003 開機階段 ────────────────────────────

    public static HardwareFact BootPhasesFact(DateTimeOffset at, BootPhaseProbe? probe = null)
    {
        IReadOnlyList<(string Phase, double Ms)>? rows;
        try { rows = (probe ?? ReadBootPhases)(); }
        catch { rows = null; }

        if (rows is null || rows.Count == 0)
            return Unavailable(at, BootPhasesKey, CatScenario, "開機階段", FactAvailability.ReadError,
                "開機階段事件（Diagnostics-Performance 100）不可讀——讀不到就是不猜");

        double total = rows.Sum(r => r.Ms);
        var slowest = rows.OrderByDescending(r => r.Ms).First();
        return new HardwareFact(BootPhasesKey, CatScenario, "開機階段",
            $"階段 {rows.Count} 個、合計 {total:0} ms、最耗時「{slowest.Phase}」{slowest.Ms:0} ms。" +
            "開機階段是<b>一次開機的觀察</b>不是現況：驅動更新、硬體變更都會改變它；單次慢不代表每次慢。",
            "ms", "Diagnostics-Performance 事件（唯讀）", FactTrustLevel.Measured, false, at, total);
    }

    private static IReadOnlyList<(string, double)>? ReadBootPhases()
    {
        // 只有「最近一次開機耗時」一項可讀（BootTimingFactsService），沒有分階段事件來源；
        // 若讀得到就用單一階段呈現，讀不到回 null（不假造階段）。
        uint? ms = BootTimingFactsService.FetchLastBootDuration();
        if (ms is not { } v) return null;
        return [("最近一次開機", v)];
    }

    // ──────────────────────────── SC-006 換零件對照 ────────────────────────────

    public static HardwareFact SwapFact(DateTimeOffset at, SwapProbe? probe = null)
    {
        (int Changed, int Added, int Removed)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SwapKey, CatScenario, "換零件對照", FactAvailability.NotApplicable,
                "沒有可比較的兩份快照——換零件對照需要時間膠囊前後各一份，缺一不判");

        var v = s.Value;
        return new HardwareFact(SwapKey, CatScenario, "換零件對照",
            $"與上次快照相比：變更 {v.Changed}、新增 {v.Added}、移除 {v.Removed} 項。" +
            "<b>事實變了不等於換了零件</b>——BIOS 更新、驅動更新、熱插拔都會讓事實改變；" +
            "要談「換零件」需人工比對變更的鍵，本列只陳述差異計數。",
            "項", "時間膠囊差異（唯讀）", FactTrustLevel.Derived, false, at, v.Changed);
    }

    // ──────────────────────────── BL-003 壽命預估整合 ────────────────────────────

    public static HardwareFact LifeFact(DateTimeOffset at, IReadOnlyList<HardwareFact> prior)
    {
        var wear = prior.FirstOrDefault(f => f.Key == WearKey);
        if (wear is null || wear.Availability != FactAvailability.Present)
            return Unavailable(at, LifeKey, CatBaseline, "壽命預估", FactAvailability.NotApplicable,
                "沒有可用的磨損事實——估壽命前先要有磨損資料，缺一不估");

        // 以「磨損 % ÷ 通電小時」估到達 100% 的剩餘小時；這是<b>線性外推</b>，明標估計。
        double used = wear.NumericValue ?? 0;
        if (used <= 0)
            return new HardwareFact(LifeKey, CatBaseline, "壽命預估",
                "磨損讀值為 0——<b>沒有足夠資訊外推</b>。0% 不是「壽命無限」，只是還沒開始可觀測。",
                "", "磨損事實外推（估計）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "磨損為 0，外推無意義");

        return new HardwareFact(LifeKey, CatBaseline, "壽命預估",
            $"目前最高磨損 {used:0}%。TBW／DWPD 外推需要「寫入量」與「通電時間」的歷史斜率，" +
            "本版只整合磨損現值——<b>這是估計不是預言</b>：寫入速率會變、磨損非線性、快閃壽命有離散效應。",
            "%", "磨損事實外推（估計）", FactTrustLevel.Derived, false, at, used);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
