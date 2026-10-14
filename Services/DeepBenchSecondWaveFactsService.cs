using System.Management;

namespace XinSpect;

/// <summary>
/// 深度測試第二波（Vol2 第 333–342 項）：TU-002 溫度響應矩陣（<c>tu.thermresp</c>）、
/// TU-003 功率-頻率擬合（<c>tu.pfreq</c>）、TU-004 Vdroop（<c>tu.vdroop</c>，推測級）、
/// TU-005 記憶體保持（<c>tu.memhold</c>，同意閘門）、TU-006 電池放電曲線（<c>tu.battery</c>）、
/// TU-007 UPS 轉換（<c>tu.ups</c>，同意閘門）、TU-009 封包損失趨勢（<c>hist.pktloss</c>）、
/// TU-011 原始資料包（<c>tu.rawpack</c>，匿名化）、TU-012 多機對照矩陣（<c>tu.matrix</c>）、
/// TU-010 夜間自跑（<c>tu.nightly</c>，產品方向待決）。
/// </summary>
/// <remarks>
/// <para>
/// <b>統計復用：</b>擬合走 <see cref="TrendSentinel.Slope"/>（Theil–Sen）與
/// <see cref="TrendSentinel.CorrelationShiftBetweenHalves"/>（相關性漂移），不另造統計。
/// </para>
/// <para>
/// <b>紅線：</b>① 需主動量測／捕獲的項（噪音頻譜、記憶體保持、UPS 轉換、抖動）一律<b>同意閘門</b>，
/// 未注入探針＝回 NotApplicable，絕不自動執行；② 擬合＝<b>模型聲明</b>不是事實；
/// ③ Vdroop 標<b>推測級</b>；④ 原始資料包<b>只建構不落檔、只遮蔽版</b>；⑤ 無電池／無 UPS／單機一律 N/A；
/// ⑥ 不列序號、路徑、程式名稱。
/// </para>
/// </remarks>
public static class DeepBenchSecondWaveFactsService
{
    private const string Cat = "深測第二波";
    private const string CatNet = "網路";

    public const string ThermRespKey = "tu.thermresp";
    public const string PFreqKey = "tu.pfreq";
    public const string VdroopKey = "tu.vdroop";
    public const string MemHoldKey = "tu.memhold";
    public const string BatteryKey = "tu.battery";
    public const string UpsKey = "tu.ups";
    public const string PktLossKey = "hist.pktloss";
    public const string RawPackKey = "tu.rawpack";
    public const string MatrixKey = "tu.matrix";
    public const string NightlyKey = "tu.nightly";
    /// <summary>TU-008：本機抖動量測（opt-in，白名單目標）——與 ms.ping 同一套白名單政策。</summary>
    public const string JitterKey = "tu.jitter";

    // ──────────────────────────── 探針接縫 ────────────────────────────

    public delegate (IReadOnlyList<double> Load, double TempC)? ThermRespProbe();
    public delegate (IReadOnlyList<double> FreqMhz, IReadOnlyList<double> Watts)? PFreqProbe();
    public delegate (IReadOnlyList<double> Vcore, IReadOnlyList<double> Load)? VdroopProbe();
    public delegate (double RetainedMb, double TotalMb)? MemHoldProbe();
    public delegate (int DischargeMinutes, double StartPct, double EndPct)? BatteryProbe();
    public delegate (int Transfers, double OnBatteryMinutes)? UpsProbe();
    public delegate IReadOnlyList<double>? PktLossProbe();
    public delegate (int FactCount, int SensitiveCount)? RawPackProbe();
    public delegate (int Machines, int Metrics)? MatrixProbe();
    public delegate bool? NightlyProbe();
    /// <summary>（目標, 樣本數, p50 ms, p95 ms, 抖動 ms, 損失率）——<b>目標由使用者指定，未指定＝不出網</b>。</summary>
    public delegate (string Target, int Samples, double P50Ms, double P95Ms, double JitterMs, double LossRate)? JitterProbe();

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        ThermRespProbe? thermResp = null,
        PFreqProbe? pFreq = null,
        VdroopProbe? vdroop = null,
        MemHoldProbe? memHold = null,
        BatteryProbe? battery = null,
        UpsProbe? ups = null,
        PktLossProbe? pktLoss = null,
        RawPackProbe? rawPack = null,
        MatrixProbe? matrix = null,
        NightlyProbe? nightly = null,
        JitterProbe? jitter = null)
        =>
        [
            ThermRespFact(at, thermResp),
            PFreqFact(at, pFreq),
            VdroopFact(at, vdroop),
            MemHoldFact(at, memHold),
            BatteryFact(at, battery),
            UpsFact(at, ups),
            PktLossFact(at, pktLoss),
            RawPackFact(at, rawPack),
            MatrixFact(at, matrix),
            NightlyFact(at, nightly),
            JitterFact(at, jitter),
        ];

    // ── TU-002 溫度響應矩陣 ────────────────────────────────────────────

    public static HardwareFact ThermRespFact(DateTimeOffset at, ThermRespProbe? probe = null)
    {
        (IReadOnlyList<double> Load, double TempC)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Value.Load.Count < 8)
            return Unavailable(at, ThermRespKey, Cat, "溫度響應矩陣", FactAvailability.NotApplicable,
                "負載-溫度配對樣本少於 8 點——不足以建立響應矩陣，不宣稱");

        var (load, temp) = s.Value;
        // 單一溫度讀值只能與負載序列的趨勢做粗粒度對照：以 Theil–Sen 斜率描述負載趨勢方向，
        // 溫度是這個時間窗的量測值。真正的「響應矩陣」需要配對的（負載, 溫度）序列。
        double loadSlope = TrendSentinel.Slope(load).SlopePerStep;
        return new HardwareFact(ThermRespKey, Cat, "溫度響應矩陣",
            $"樣本 {load.Count} 點、負載趨勢 {loadSlope:0.###}/步、觀測溫度 {temp:0.#} °C。" +
            "這是<b>模型聲明</b>不是事實：散熱、環境溫度、風扇曲線都會改變響應；" +
            "相關性突然脫鉤（CorrelationShift）比單純斜率更有意義，需跨多輪比對。",
            "°C/步", "感測器量測＋Theil–Sen（模型）", FactTrustLevel.Derived, false, at, temp);
    }

    // ── TU-003 功率-頻率擬合 ──────────────────────────────────────────

    public static HardwareFact PFreqFact(DateTimeOffset at, PFreqProbe? probe = null)
    {
        (IReadOnlyList<double> FreqMhz, IReadOnlyList<double> Watts)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Value.FreqMhz.Count < 8)
            return Unavailable(at, PFreqKey, Cat, "功率-頻率擬合", FactAvailability.NotApplicable,
                "頻率-功耗配對樣本少於 8 點——不足以擬合，不宣稱");

        var (freq, watts) = s.Value;
        var corr = TrendSentinel.CorrelationShiftBetweenHalves(freq, watts);
        return new HardwareFact(PFreqKey, Cat, "功率-頻率擬合",
            $"樣本 {freq.Count} 點、相關係數前半 {corr.RawCorrelation:0.###}／後半 {corr.SmoothCorrelation:0.###}。" +
            "擬合結果是<b>模型聲明</b>不是硬體事實：電壓曲線、溫度節流、功耗牆都會讓同一頻率對應不同功耗；" +
            "前後半段相關性變化代表「同樣頻率不再吃同樣電」，值得留意但不是判決。",
            "", "感測器量測＋相關性（模型）", FactTrustLevel.Derived, false, at, corr.SmoothCorrelation);
    }

    // ── TU-004 Vdroop ─────────────────────────────────────────────────

    public static HardwareFact VdroopFact(DateTimeOffset at, VdroopProbe? probe = null)
    {
        (IReadOnlyList<double> Vcore, IReadOnlyList<double> Load)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null || s.Value.Vcore.Count < 8)
            return Unavailable(at, VdroopKey, Cat, "Vdroop 觀測", FactAvailability.NotApplicable,
                "電壓-負載樣本少於 8 點——不足以觀測 droop，不宣稱");

        var (vcore, load) = s.Value;
        double slope = TrendSentinel.Slope(vcore).SlopePerStep;
        return new HardwareFact(VdroopKey, Cat, "Vdroop 觀測",
            $"電壓序列斜率 {slope:0.####} V/步（{vcore.Count} 點）。" +
            "<b>推測級</b>：Vdroop 是負載瞬變時電壓下陷，需要高頻取樣才量得到；" +
            "本觀測的低頻樣本只反映趨勢，不足以判定 Vdroop 幅度或穩定度。",
            "V/步", "感測器取樣（推測級）", FactTrustLevel.Measured, false, at, slope);
    }

    // ── TU-005 記憶體保持（同意閘門）──────────────────────────────────

    public static HardwareFact MemHoldFact(DateTimeOffset at, MemHoldProbe? probe = null)
    {
        (double RetainedMb, double TotalMb)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(MemHoldKey, Cat, "記憶體保持測試", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "需主動 suspend/resume 並驗證記憶體內容——屬同意閘門量測，未同意永不執行");

        var (retained, total) = s.Value;
        return new HardwareFact(MemHoldKey, Cat, "記憶體保持測試",
            $"保持 {retained:0.#} MB／共 {total:0.#} MB。這是<b>使用者明確觸發</b>測試的結果；" +
            "內容不符不代表記憶體故障（可能有 ECC 修正或已重配置），需與錯誤記錄交叉判讀。",
            "MB", "記憶體寫入／讀回（同意閘門）", FactTrustLevel.Measured, false, at, retained);
    }

    // ── TU-006 電池放電曲線 ────────────────────────────────────────────

    public static HardwareFact BatteryFact(DateTimeOffset at, BatteryProbe? probe = null)
    {
        (int DischargeMinutes, double StartPct, double EndPct)? s;
        try { s = (probe ?? ReadBattery)(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(BatteryKey, Cat, "電池放電曲線", "", "", "Win32_Battery（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "本機沒有電池（桌機／伺服器）——電池分析不適用，未使用不代表有問題");

        var (minutes, start, end) = s.Value;
        return new HardwareFact(BatteryKey, Cat, "電池放電曲線",
            $"放電 {minutes} 分鐘（{start:0}%→{end:0}%）。" +
            "這是時間窗內的觀察，不是電池設計容量；放電速率隨負載變化，單段曲線不足以推算健康度。",
            "分鐘", "Win32_Battery（唯讀）", FactTrustLevel.Reported, false, at, minutes);
    }

    private static (int, double, double)? ReadBattery()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT EstimatedChargeRemaining FROM Win32_Battery");
            var o = s.Get().Cast<ManagementObject>().FirstOrDefault();
            if (o is null) return null;   // 無電池 → N/A
            double pct = Convert.ToDouble(o["EstimatedChargeRemaining"] ?? 0.0);
            return (0, pct, pct);          // 單次讀值沒有放電區間
        }
        catch { return null; }
    }

    // ── TU-007 UPS 轉換（同意閘門）────────────────────────────────────

    public static HardwareFact UpsFact(DateTimeOffset at, UpsProbe? probe = null)
    {
        (int Transfers, double OnBatteryMinutes)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(UpsKey, Cat, "UPS 轉換測試", "", "", "Win32_Battery／UPS（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "需實際切斷市電驗證轉換——屬同意閘門且會影響供電，未同意永不執行");

        var (transfers, minutes) = s.Value;
        return new HardwareFact(UpsKey, Cat, "UPS 轉換測試",
            $"轉換 {transfers} 次、電池供電 {minutes:0.#} 分鐘。" +
            "這是使用者明確觸發的結果；轉換次數多不代表 UPS 有問題，要看是否與實際停電對應。",
            "次", "UPS 量測（同意閘門）", FactTrustLevel.Measured, false, at, transfers);
    }

    // ── TU-008 本機抖動量測（opt-in 白名單）─────────────────────────────

    /// <summary>
    /// 抖動量測：對<b>使用者指定</b>的目標量測延遲分布。
    /// <para>
    /// 與 <c>ms.ping</c>（MS-007）<b>同一套白名單政策</b>：目標由使用者輸入，
    /// 不自動掃描、不預設目標；未注入探針＝<b>絕不出網</b>。
    /// 差別在量的東西：<c>ms.ping</c> 報中位與抖動，本項另外報 <b>p95 與損失率</b>——
    /// 因為「網路能不能用」的感受取決於尾端與掉包，不是平均值。
    /// </para>
    /// <para>
    /// <b>紅線：</b>① 未指定目標＝NotApplicable（不是「抖動 0 ms」）；
    /// ② <b>抖動不是本機效能</b>——它同時反映路徑、對端與無線環境，
    /// 所以值裡不寫「網路品質良好」這類結論；③ <b>樣本數要報</b>：
    /// 五個樣本算出來的 p95 幾乎沒有意義，數字要與樣本量一起看；
    /// ④ 抖動定義寫明（相鄰樣本差的平均絕對值），免得與對端工具比數字時定義不同。
    /// </para>
    /// </summary>
    public static HardwareFact JitterFact(DateTimeOffset at, JitterProbe? probe = null)
    {
        (string Target, int Samples, double P50Ms, double P95Ms, double JitterMs, double LossRate)? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return new HardwareFact(JitterKey, CatNet, "本機抖動量測", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "opt-in 白名單模式：需使用者明確指定目標才出網——未指定＝絕不出網。" +
                "未指定時不報「抖動 0 ms」：0 代表量到很穩，與「沒量」是完全不同的兩件事");

        var (target, samples, p50, p95, jitter, loss) = s.Value;
        // 樣本太少時 p95 幾乎沒有統計意義，這件事要在值裡看得見。
        string sampleNote = samples < MinJitterSamples
            ? $"<b>樣本僅 {samples} 個（低於 {MinJitterSamples}）——p95 與抖動的統計意義有限</b>，只作方向參考"
            : $"樣本 {samples} 個";

        return new HardwareFact(JitterKey, CatNet, "本機抖動量測",
            $"目標「{target}」・{sampleNote}・p50 {p50:0.#} ms・p95 {p95:0.#} ms・" +
            $"抖動 {jitter:0.#} ms（相鄰樣本差的平均絕對值）・損失 {loss:P1}。" +
            "<b>使用者指定才出網</b>：與 ms.ping 同一套白名單政策，目標由使用者輸入，" +
            "不自動掃描、不預設目標。" +
            "<b>抖動不是本機效能</b>——它同時反映路徑、對端與無線環境，" +
            "所以本項不寫「網路品質良好」這類結論，只出數字。" +
            "<b>為什麼看 p95 與損失率不只平均</b>：能不能用取決於尾端與掉包——" +
            "平均 20 ms 但 p95 200 ms 的線路，視訊會議會斷，而平均值看起來很漂亮。" +
            "抖動的定義這裡寫明了，因為不同工具定義不同，跨工具比數字會得出錯的結論。",
            "ms", "使用者指定的目標（opt-in 出網，白名單）", FactTrustLevel.Measured, false, at, jitter);
    }

    /// <summary>低於這個樣本數時 p95 幾乎沒有統計意義（值裡會標明）。</summary>
    public const int MinJitterSamples = 20;

    // ── TU-009 封包損失趨勢 ────────────────────────────────────────────

    public static HardwareFact PktLossFact(DateTimeOffset at, PktLossProbe? probe = null)
    {
        IReadOnlyList<double>? series;
        try { series = (probe ?? ReadPktLoss)(); }
        catch { series = null; }

        if (series is null)
            return Unavailable(at, PktLossKey, CatNet, "封包損失趨勢", FactAvailability.ReadError,
                "介面封包計數不可讀——讀不到就是不猜");

        if (series.Count == 0)
            return new HardwareFact(PktLossKey, CatNet, "封包損失趨勢",
                "沒有可用的封包損失樣本——0 個樣本不是 0% 損失（未量到不等於沒有）。",
                "%", "介面計數（唯讀）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotApplicable, "沒有樣本：不把『沒量到』寫成『沒損失』");

        double max = series.Max();
        return new HardwareFact(PktLossKey, CatNet, "封包損失趨勢",
            $"樣本 {series.Count} 點、最大損失 {max:0.##}%。" +
            "介面計數是裝置自開機以來累計，取下差分才是速率；少量損失在無線環境是常態——" +
            "本項不列介面名稱或 IP。",
            "%", "介面計數（唯讀，去識別）", FactTrustLevel.Measured, false, at, max);
    }

    private static IReadOnlyList<double>? ReadPktLoss()
    {
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT PacketsReceivedDiscarded, PacketsReceivedUnknown FROM Win32_PerfRawData_Tcpip_NetworkInterface");
            var list = new List<double>();
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    double disc = Convert.ToDouble(o["PacketsReceivedDiscarded"] ?? 0.0);
                    double unk = Convert.ToDouble(o["PacketsReceivedUnknown"] ?? 0.0);
                    double total = disc + unk;
                    list.Add(total <= 0 ? 0 : 100.0 * disc / total);
                }
            return list;
        }
        catch { return null; }
    }

    // ── TU-011 原始資料包（匿名化）────────────────────────────────────

    public static HardwareFact RawPackFact(DateTimeOffset at, RawPackProbe? probe = null)
    {
        (int FactCount, int SensitiveCount)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.FactCount == 0)
            return new HardwareFact(RawPackKey, Cat, "深測原始資料包", "", "", "匿名化建構（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "沒有可打包的樣本——不建構空資料包");

        var (total, sensitive) = s.Value;
        return new HardwareFact(RawPackKey, Cat, "深測原始資料包",
            $"可匯出 {total} 筆樣本，其中 {sensitive} 筆敏感值（序號／路徑／身分鍵）已<b>遮蔽</b>。" +
            "本項<b>只建構不落檔、只出遮蔽版</b>——上傳通路依資料主權原則不實作。",
            "筆", "匿名化建構（唯讀）", FactTrustLevel.Reported, false, at, total);
    }

    // ── TU-012 多機對照矩陣 ───────────────────────────────────────────

    public static HardwareFact MatrixFact(DateTimeOffset at, MatrixProbe? probe = null)
    {
        (int Machines, int Metrics)? s;
        try { s = (probe ?? (() => (0, 0)))(); }
        catch { s = null; }

        if (s is null || s.Value.Machines < 2)
            return new HardwareFact(MatrixKey, Cat, "多機對照矩陣", "", "", "時間膠囊並排（唯讀）",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotApplicable,
                "少於兩台機器的快照——對照需要至少兩台，單機不假裝有矩陣");

        var (machines, metrics) = s.Value;
        return new HardwareFact(MatrixKey, Cat, "多機對照矩陣",
            $"可對照 {machines} 台機器 × {metrics} 項指標。" +
            "並排是<b>呈現</b>不是排名——不同機器規格不同時，數字高低不代表好壞；" +
            "機器識別只用匿名雜湊，不列序號或主機名。",
            "項", "時間膠囊並排（唯讀）", FactTrustLevel.Derived, false, at, machines);
    }

    // ── TU-010 夜間自跑（產品方向待決）────────────────────────────────

    public static HardwareFact NightlyFact(DateTimeOffset at, NightlyProbe? probe = null)
    {
        bool? enabled;
        try { enabled = (probe ?? (() => null))(); }
        catch { enabled = null; }

        if (enabled is null)
            return new HardwareFact(NightlyKey, Cat, "深測夜間自跑", "", "", "—",
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "夜間常駐自跑是產品方向決定（背景負載、排程、寫入），本版明文不做——不假裝有");

        return new HardwareFact(NightlyKey, Cat, "深測夜間自跑",
            enabled.Value ? "已啟用夜間自跑（由使用者明確設定）。" : "夜間自跑未啟用。",
            "", "排程設定（唯讀）", FactTrustLevel.Reported, false, at, enabled.Value ? 1 : 0);
    }

    // ──────────────────────────── helper ────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string category, string name,
        FactAvailability availability, string reason)
        => new(key, category, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
