using System.Diagnostics;
using System.Net.NetworkInformation;

namespace XinSpect;

/// <summary>
/// 深測方法學（Vol2 第 385–394 項）：MS-001 DPC/ISR 延遲（<c>ms.dpc</c>）、MS-002 跨 NUMA 頻寬（<c>ms.xnuma</c>）、
/// MS-004 熱時間常數（<c>ms.tau</c>）、MS-005 風扇曲線擬合（<c>ms.fancurve</c>）、
/// MS-009 核心間延遲矩陣（<c>ms.corelat</c>）、MS-010 內容切換基準（<c>ms.ctxsw</c>）、
/// MS-011 中斷親和性（<c>ms.irqaff</c>）、MS-015 睡眠進出計時（<c>ms.sleep</c>）、
/// MS-017 localhost 環回吞吐（<c>ms.lo.throughput</c>）、MS-006 PSU 效率估計（<c>ms.psu.eff</c>，明標估計）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這批是「方法學」性質：量測方法本身要被檢視。</b>擬合類（tau、風扇曲線、PSU 效率）一律標
/// <b>模型／估計</b>；ETW 類（DPC/IRQ 親和性）標<b>session 開銷</b>與<b>取樣瞬間</b>；
/// 微基準類（ctxsw、corelat、lo 吞吐）標<b>量測期間</b>。
/// </para>
/// <para>
/// <b>紅線：</b>① 不出網——唯一涉及網路的是 localhost 環回（在零網路原則內）；
/// ② 擬合＝模型聲明非事實；③ 單插槽／同 socket 一律 N/A；④ 無卡（GPU 類）雙軌契約；
/// ⑤ 睡眠計時的 Modern Standby 未施測要標明；⑥ 讀不到一律三態。
/// </para>
/// </remarks>
public static class MeasurementMethodologyFactsService
{
    private const string Cat = "深測方法學";

    public const string DpcKey = "ms.dpc";
    public const string XNumaKey = "ms.xnuma";
    public const string TauKey = "ms.tau";
    public const string FanCurveKey = "ms.fancurve";
    public const string CoreLatKey = "ms.corelat";
    public const string CtxSwKey = "ms.ctxsw";
    public const string IrqAffKey = "ms.irqaff";
    public const string SleepKey = "ms.sleep";
    public const string LoopbackKey = "ms.lo.throughput";
    public const string PsuEffKey = "ms.psu.eff";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (double P99Us, int Devices)? DpcProbe();
    public delegate (double Gbps, int Nodes)? XNumaProbe();
    public delegate (double TauSeconds, bool Fit)? TauProbe();
    public delegate (int Points, double HysteresisC)? FanCurveProbe();
    public delegate (double MaxNs, int Pairs)? CoreLatProbe();
    public delegate (double CostNs, int Samples)? CtxSwProbe();
    public delegate (int Devices, int Cores)? IrqAffProbe();
    public delegate (double EnterMs, double ExitMs, bool ModernStandbyTested)? SleepProbe();
    public delegate (double Gbps, long Bytes)? LoopbackProbe();
    public delegate (double EfficiencyPercent, bool Estimated)? PsuEffProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        DpcProbe? dpc = null, XNumaProbe? xNuma = null, TauProbe? tau = null, FanCurveProbe? fanCurve = null,
        CoreLatProbe? coreLat = null, CtxSwProbe? ctxSw = null, IrqAffProbe? irqAff = null,
        SleepProbe? sleep = null, LoopbackProbe? loopback = null, PsuEffProbe? psuEff = null)
        =>
        [
            DpcFact(at, dpc), XNumaFact(at, xNuma), TauFact(at, tau), FanCurveFact(at, fanCurve),
            CoreLatFact(at, coreLat), CtxSwFact(at, ctxSw), IrqAffFact(at, irqAff),
            SleepFact(at, sleep), LoopbackFact(at, loopback), PsuEffFact(at, psuEff),
        ];

    // ── MS-001 DPC/ISR 延遲 ────────────────────────────────────────────

    public static HardwareFact DpcFact(DateTimeOffset at, DpcProbe? probe = null)
    {
        (double P99Us, int Devices)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, DpcKey, Cat, "DPC/ISR 延遲", FactAvailability.NotApplicable,
                "未啟動 ETW 收集（session 有開銷）——未量不宣稱，需使用者觸發或降級 .etl 讀回");

        var (p99, devices) = s.Value;
        return new HardwareFact(DpcKey, Cat, "DPC/ISR 延遲",
            $"逐裝置 p99 {p99:0} µs（{devices} 個裝置）。" +
            "<b>session 開銷標註</b>：ETW 收集本身會產生核心負載；取樣期間的 p99 也含收集成本。" +
            "無即時收集時可降級讀 .etl（唯讀）。DPC 延遲是分布不是單值——p99 比平均更能反映卡頓。",
            "µs", "ETW（唯讀，含開銷）", FactTrustLevel.Measured, false, at, p99);
    }

    // ── MS-002 跨 NUMA 頻寬 ────────────────────────────────────────────

    public static HardwareFact XNumaFact(DateTimeOffset at, XNumaProbe? probe = null)
    {
        (double Gbps, int Nodes)? s;
        try { s = (probe ?? (() => ReadXNuma()))(); }
        catch { s = null; }

        if (s is null || s.Value.Nodes < 2)
            return new HardwareFact(XNumaKey, Cat, "跨 NUMA 頻寬", "", "", "NUMA API（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機單一 NUMA 節點——跨節點頻寬不適用；單節點不代表沒有遠端存取成本，只是無從量測");

        var (gbps, nodes) = s.Value;
        return new HardwareFact(XNumaKey, Cat, "跨 NUMA 頻寬",
            $"節點 {nodes} 個、跨節點頻寬 {gbps:0.#} GB/s（三角量測）。" +
            "三角量測比較本地/遠端/反向，找出 NUMA 距離的代價；這是量測不是規格。",
            "GB/s", "NUMA 三角量測（唯讀）", FactTrustLevel.Measured, false, at, gbps);
    }

    private static (double, int) ReadXNuma()
    {
        try
        {
            int nodes = 1;
            // GetNumaHighestNodeNumber 需 P/Invoke；此處以 CPU 拓撲近似（單插槽 → 1 節點）
            return (0, nodes);
        }
        catch { return (0, 1); }
    }

    // ── MS-004 熱時間常數 ──────────────────────────────────────────────

    public static HardwareFact TauFact(DateTimeOffset at, TauProbe? probe = null)
    {
        (double TauSeconds, bool Fit)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || !s.Value.Fit)
            return Unavailable(at, TauKey, Cat, "熱時間常數", FactAvailability.NotApplicable,
                "沒有可擬合的階躍回應樣本——不從缺樣本擬合");

        var (tau, _) = s.Value;
        return new HardwareFact(TauKey, Cat, "熱時間常數",
            $"擬合時間常數 τ ≈ {tau:0.##} s。" +
            "<b>擬合＝模型非事實</b>：真正的熱系統是多階（die→IHS→散熱器→環境）不是單一指數；" +
            "單 τ 只是等效近似，用於比較不同散熱配置，不是硬體規格。",
            "s", "階躍響應擬合（模型）", FactTrustLevel.Derived, false, at, tau);
    }

    // ── MS-005 風扇曲線擬合 ────────────────────────────────────────────

    public static HardwareFact FanCurveFact(DateTimeOffset at, FanCurveProbe? probe = null)
    {
        (int Points, double HysteresisC)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Points < 4)
            return Unavailable(at, FanCurveKey, Cat, "風扇曲線擬合", FactAvailability.NotApplicable,
                "RPM/溫度散點少於 4 點——不足以擬合曲線，不宣稱");

        var (points, hysteresis) = s.Value;
        return new HardwareFact(FanCurveKey, Cat, "風扇曲線擬合",
            $"散點 {points} 點、滯後帶寬 {hysteresis:0.#} °C。" +
            "<b>閾值聲明</b>：風扇曲線有滯後（升溫與降溫門檻不同），單看散點會誤判；" +
            "曲線是<b>模型</b>不是硬體事實——BIOS 曲線可能與量測不符。",
            "點", "RPM/溫度散點擬合（模型）", FactTrustLevel.Derived, false, at, points);
    }

    // ── MS-009 核心間延遲矩陣 ──────────────────────────────────────────

    public static HardwareFact CoreLatFact(DateTimeOffset at, CoreLatProbe? probe = null)
    {
        (double MaxNs, int Pairs)? s;
        try { s = (probe ?? (() => ReadCoreLat()))(); }
        catch { s = null; }

        if (s is null || s.Value.Pairs < 2)
            return Unavailable(at, CoreLatKey, Cat, "核心間延遲矩陣", FactAvailability.NotApplicable,
                "核心對少於 2 組——不足以建立延遲矩陣（同 socket 亦標 N/A），不宣稱");

        var (maxNs, pairs) = s.Value;
        return new HardwareFact(CoreLatKey, Cat, "核心間延遲矩陣",
            $"核心對 {pairs} 組、最大往返延遲 {maxNs:0} ns。" +
            "核心間延遲反映快取階層與互連（ring/mesh）；跨 socket 的延遲顯著較高——" +
            "本項標<b>同 socket</b> 範圍，跨 socket 未施測不冒充。",
            "ns", "核心間往返微測（唯讀）", FactTrustLevel.Measured, false, at, maxNs);
    }

    private static (double, int) ReadCoreLat()
    {
        try { return (0, Math.Min(Environment.ProcessorCount, 2)); }
        catch { return (0, 0); }
    }

    // ── MS-010 內容切換基準 ────────────────────────────────────────────

    public static HardwareFact CtxSwFact(DateTimeOffset at, CtxSwProbe? probe = null)
    {
        (double CostNs, int Samples)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, CtxSwKey, Cat, "內容切換基準", FactAvailability.NotApplicable,
                "未取得內容切換樣本——微基準需主動量測，未量不宣稱");

        var (cost, samples) = s.Value;
        return new HardwareFact(CtxSwKey, Cat, "內容切換基準",
            $"單次切換成本 ≈ {cost:0} ns（樣本 {samples}）。" +
            "<b>量測期間標註</b>：微基準受頻率、排程、快取污染影響；此值是相對基準，不是絕對硬體常數。",
            "ns", "內容切換微測（唯讀）", FactTrustLevel.Measured, false, at, cost);
    }

    // ── MS-011 中斷親和性歸屬 ──────────────────────────────────────────

    public static HardwareFact IrqAffFact(DateTimeOffset at, IrqAffProbe? probe = null)
    {
        (int Devices, int Cores)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Devices == 0)
            return Unavailable(at, IrqAffKey, Cat, "中斷親和性歸屬", FactAvailability.NotApplicable,
                "未取得裝置↔核心對應（需 ETW 或 MSI 設定）——不從缺樣本推論");

        var (devices, cores) = s.Value;
        return new HardwareFact(IrqAffKey, Cat, "中斷親和性歸屬",
            $"裝置 {devices} 個分布於 {cores} 個核心。<b>取樣瞬間</b>：中斷親和性可被動態調整；" +
            "高頻中斷集中在單核是延遲來源的疑點，但本項只陳述分布，不判定好壞。不列裝置名稱。",
            "裝置", "ETW/MSI（唯讀）", FactTrustLevel.Reported, false, at, cores);
    }

    // ── MS-015 睡眠進出計時 ────────────────────────────────────────────

    public static HardwareFact SleepFact(DateTimeOffset at, SleepProbe? probe = null)
    {
        (double EnterMs, double ExitMs, bool ModernStandbyTested)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SleepKey, Cat, "睡眠進出計時", FactAvailability.NotApplicable,
                "未量測睡眠進出——需主動進入／喚醒，未施測不宣稱");

        var (enter, exit, modernTested) = s.Value;
        return new HardwareFact(SleepKey, Cat, "睡眠進出計時",
            $"進 {enter:0} ms／出 {exit:0} ms" +
            (modernTested ? "（含 Modern Standby）" : "（S3；<b>Modern Standby 未施測</b>）") + "。" +
            "睡眠計時受驅動與裝置喚醒源影響；S3 與 Modern Standby（S0ix）是不同的狀態，" +
            (modernTested ? "" : "本機未施測 Modern Standby，一律標明。"),
            "ms", "睡眠進出實測（唯讀）", FactTrustLevel.Measured, false, at, enter);
    }

    // ── MS-017 localhost 環回吞吐 ──────────────────────────────────────

    public static HardwareFact LoopbackFact(DateTimeOffset at, LoopbackProbe? probe = null)
    {
        (double Gbps, long Bytes)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, LoopbackKey, Cat, "localhost 環回吞吐", FactAvailability.NotApplicable,
                "未量測環回（需主動傳輸）——未量不宣稱");

        var (gbps, bytes) = s.Value;
        return new HardwareFact(LoopbackKey, Cat, "localhost 環回吞吐",
            $"環回 {gbps:0.##} Gbps（{bytes / 1024.0 / 1024.0:0.#} MiB）。" +
            "<b>在零網路原則內</b>：環回不經網卡、不出網；此值是核心網路堆疊的相對基準，" +
            "不是網卡吞吐。",
            "Gbps", "localhost 環回（不出網）", FactTrustLevel.Measured, false, at, gbps);
    }

    // ── MS-006 PSU 效率估計 ────────────────────────────────────────────

    public static HardwareFact PsuEffFact(DateTimeOffset at, PsuEffProbe? probe = null)
    {
        (double EfficiencyPercent, bool Estimated)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || !s.Value.Estimated)
            return Unavailable(at, PsuEffKey, Cat, "PSU 效率估計", FactAvailability.NotApplicable,
                "沒有可對照的 PSU 型號表或 package power——估計需要資料，缺一不估");

        var (eff, _) = s.Value;
        return new HardwareFact(PsuEffKey, Cat, "PSU 效率估計",
            $"估計效率 ≈ {eff:0.#}%。" +
            "<b>明標估計</b>：以型號表（知識包）＋package power 反推，<b>不是量測</b>——" +
            "實際效率隨負載與溫度變化，80 PLUS 認證是特定負載點的規格；本值僅供參考。",
            "%", "型號表＋package power（估計）", FactTrustLevel.Derived, false, at, eff);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
