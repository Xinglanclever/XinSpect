namespace XinSpect;

/// <summary>
/// 情境包第三波（Vol1 第 SC-001、SC-002、SC-003、SC-010 項）：
/// 二手驗機精靈（<c>sc.verifycheck.summary</c>）、該不該修摘要（<c>sc.verdict.summary</c>）、
/// 效能為什麼慢（<c>sc.slow.summary</c>）、供電穩嗎（<c>sc.pcurve.summary</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>這四項都是「聚合既有事實、不出新判決」。</b>它們的價值不在於算得多深，
/// 而在於把散在幾十把鍵裡的訊號收攏成一句人話，並且<b>說清楚那句話的效力邊界</b>。
/// </para>
/// <para>
/// <b>紅線（逐項寫進值與測試）：</b>
/// ① 驗機精靈<b>不修復、不「優化」</b>，缺來源列的項目要顯示成灰而不是綠；
/// ② 該不該修只在既有 Warning／Critical 上聚合，<b>不給分數、不下「正品／翻新」結論</b>
///    （與 MachineVerdict 同一個政策）；
/// ③ 效能慢的證據鏈是<b>排除法不是因果鏈</b>——列出各環節的量測值，不宣稱誰是元凶；
/// ④ 供電曲線是<b>觀察不是預測</b>，樣本不足一律 NotApplicable。
/// </para>
/// </remarks>
public static class ScenarioThirdWaveFactsService
{
    private const string Cat = "情境";

    public const string VerifyCheckKey = "sc.verifycheck.summary";
    public const string VerdictKey = "sc.verdict.summary";
    public const string SlowKey = "sc.slow.summary";
    public const string PowerCurveKey = "sc.pcurve.summary";

    /// <summary>稱得上「曲線」的最少樣本數。</summary>
    public const int MinCurveSamples = 12;

    // ── 探針接縫 ──────────────────────────────────────────────────────────

    /// <summary>（可檢查項, 相符, 矛盾, 讀不到, 未納入）——二手驗機精靈的收攏結果。</summary>
    public sealed record VerifyCheckState(int Checked, int Match, int Conflict, int Unread, int Excluded);

    /// <summary>（警告數, 嚴重數, 資訊數, 最嚴重的三個項目名）。</summary>
    public sealed record VerdictState(int Warning, int Critical, int Info, IReadOnlyList<string> TopItems);

    /// <summary>（頻率偏離 %, DPC 延遲 us, 磁碟延遲 ms, 最高溫 °C, 各項是否可讀）。</summary>
    public sealed record SlowState(double? FreqDropPercent, double? DpcMicros, double? DiskMs,
        double? MaxTempC, IReadOnlyList<string> UnreadStages);

    /// <summary>（電壓序列 mV, 功率序列 W, 每點秒數）。</summary>
    public sealed record PowerCurveSamples(IReadOnlyList<double> VoltageMv, IReadOnlyList<double> PowerW, double SecondsPerStep);

    public delegate VerifyCheckState? VerifyCheckProbe();
    public delegate VerdictState? VerdictProbe();
    public delegate SlowState? SlowProbe();
    public delegate PowerCurveSamples? PowerCurveProbe();

    // ── 收集 ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        VerifyCheckProbe? verifyCheck = null,
        VerdictProbe? verdict = null,
        SlowProbe? slow = null,
        PowerCurveProbe? powerCurve = null)
        =>
        [
            VerifyCheckFact(at, verifyCheck),
            VerdictFact(at, verdict),
            SlowFact(at, slow),
            PowerCurveFact(at, powerCurve),
        ];

    // ── SC-001 二手驗機精靈 ───────────────────────────────────────────────

    public static HardwareFact VerifyCheckFact(DateTimeOffset at, VerifyCheckProbe? probe = null)
    {
        VerifyCheckState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, VerifyCheckKey, "二手驗機精靈", FactAvailability.NotApplicable,
                "沒有可收攏的檢查結果——精靈聚合既有規則，沒有規則跑過就沒有摘要");

        var v = s;
        int total = v.Match + v.Conflict + v.Unread;
        string headline = v.Conflict > 0
            ? $"<b>矛盾 {v.Conflict} 條</b>——這些要一條條看，每條都可能有正當成因"
            : v.Unread > 0
                ? "沒有矛盾，但有些項目讀不到——<b>讀不到不是通過</b>"
                : "讀得到的項目彼此對得上";
        string excluded = v.Excluded == 0
            ? ""
            : $"・未納入 {v.Excluded} 項（缺來源，在清單上顯示為灰）";

        return new HardwareFact(VerifyCheckKey, Cat, "二手驗機精靈",
            $"檢查 {v.Checked} 項・相符 {v.Match}・矛盾 {v.Conflict}・讀不到 {v.Unread}{excluded}。{headline}。" +
            "<b>唯讀、不修復、不「優化」</b>：精靈只把既有檢查跑一遍並收攏，" +
            "不對機器做任何變更，也不會為了讓結果好看而調整任何設定。" +
            "缺來源的項目<b>顯示成灰不是綠</b>——沒檢查到與檢查通過是兩件事。",
            "項", "既有驗機規則聚合（唯讀）", FactTrustLevel.Derived, false, at, v.Conflict);
    }

    // ── SC-002 該不該修摘要 ───────────────────────────────────────────────

    public static HardwareFact VerdictFact(DateTimeOffset at, VerdictProbe? probe = null)
    {
        VerdictState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, VerdictKey, "該不該修摘要", FactAvailability.NotApplicable,
                "沒有 Warning／Critical 等級的既有判定——沒有訊號就沒有摘要");

        var v = s;
        if (v.Warning + v.Critical + v.Info == 0)
            return Unavailable(at, VerdictKey, "該不該修摘要", FactAvailability.NotApplicable,
                "所有既有判定都是等級以下（沒有值得聚合的訊號）——不適用，不是「一切正常」的判決");

        string top = v.TopItems.Count == 0 ? "" : $"最需要看的：{string.Join("、", v.TopItems.Take(3))}（依等級排序）";
        string tone = v.Critical > 0
            ? "有嚴重等級的項目"
            : v.Warning > 0 ? "有警告等級的項目" : "只有資訊等級的項目";

        return new HardwareFact(VerdictKey, Cat, "該不該修摘要",
            $"嚴重 {v.Critical}・警告 {v.Warning}・資訊 {v.Info}。{tone}。{top}" +
            "<b>聚合既有判定，不新增判決</b>：這一項只把已經算出來的 Warning／Critical 排個序，" +
            "沒有自己的判斷邏輯——所以它不會「比你手上的規則更懂」。" +
            "也<b>不給分數、不下「正品／翻新」結論</b>（與整機驗機報告同一個政策）：" +
            "分數會讓人停止思考每一條的成因。",
            "條", "既有 Warning／Critical 判定（聚合）", FactTrustLevel.Derived, false, at, v.Warning + v.Critical);
    }

    // ── SC-003 效能為什麼慢 ───────────────────────────────────────────────

    public static HardwareFact SlowFact(DateTimeOffset at, SlowProbe? probe = null)
    {
        SlowState? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        if (s is null)
            return Unavailable(at, SlowKey, "效能為什麼慢", FactAvailability.NotApplicable,
                "沒有可用的量測樣本——證據鏈要資料，不憑感覺列原因");

        var v = s;
        var stages = new List<string>(4);
        if (v.FreqDropPercent is { } f) stages.Add($"頻率偏離標稱 {f:0.#}%");
        else stages.Add("頻率不可讀");
        if (v.DpcMicros is { } d) stages.Add($"DPC 延遲 {d:0.#} µs");
        else stages.Add("DPC 不可讀");
        if (v.DiskMs is { } k) stages.Add($"磁碟延遲 {k:0.##} ms");
        else stages.Add("磁碟不可讀");
        if (v.MaxTempC is { } t) stages.Add($"最高溫 {t:0.#} °C");
        else stages.Add("溫度不可讀");

        string unread = v.UnreadStages.Count == 0
            ? "四段全部有量測值"
            : $"<b>{v.UnreadStages.Count} 段讀不到</b>（{string.Join("、", v.UnreadStages)}）——" +
              "讀不到的環節不能被當成「沒問題」，證據鏈少了那一段就拼不完整";

        return new HardwareFact(SlowKey, Cat, "效能為什麼慢",
            $"證據鏈：{string.Join("、", stages)}。{unread}。" +
            "<b>這是排除法不是因果鏈</b>：本項並列各環節的量測值，讓你看到「哪幾段偏離正常範圍」，" +
            "但不宣稱誰是元凶——頻率高溫、磁碟慢、DPC 高可以是三個獨立的問題，" +
            "也可能互相影響；要定因果需要受控實驗，本工具不做（那是修復行為的前置）。",
            "段", "既有量測事實並列（唯讀）", FactTrustLevel.Derived, false, at, stages.Count);
    }

    // ── SC-010 供電穩嗎 ───────────────────────────────────────────────────

    public static HardwareFact PowerCurveFact(DateTimeOffset at, PowerCurveProbe? probe = null)
    {
        PowerCurveSamples? s;
        try { s = (probe ?? (() => null))(); }
        catch { s = null; }

        int n = s is null ? 0 : Math.Min(s.VoltageMv.Count, s.PowerW.Count);
        if (s is null || n < MinCurveSamples)
            return Unavailable(at, PowerCurveKey, "供電穩嗎", FactAvailability.NotApplicable,
                s is null
                    ? "沒有電壓／功率歷史樣本——不適用（曲線要資料）"
                    : $"配對樣本只有 {n} 點，未達 {MinCurveSamples} 點門檻——樣本不足就不畫曲線");

        var volts = s.VoltageMv.Take(n).ToList();
        var watts = s.PowerW.Take(n).ToList();
        var vEst = TrendSentinel.Slope(volts);
        var wEst = TrendSentinel.Slope(watts);

        double vMin = volts.Min(), vMax = volts.Max();
        double vSpread = vMax - vMin;
        // 標稱電壓的粗估：取眾數區間（中位數）當基準；離散度用相對值表達。
        double vMedian = volts.OrderBy(x => x).ElementAt(n / 2);
        double pct = vMedian > 0 ? 100.0 * vSpread / vMedian : 0;

        string verdict = pct <= 5
            ? $"電壓波動 {pct:0.#}%（≤5%）——期間供電穩定"
            : pct <= 10
                ? $"電壓波動 {pct:0.#}%——略高於 5%，值得留意但不是異常"
                : $"<b>電壓波動 {pct:0.#}%（>10%）</b>——期間電壓起伏明顯，可能是負載變化大或供電不穩";

        return new HardwareFact(PowerCurveKey, Cat, "供電穩嗎",
            $"樣本 {n} 點（{s.SecondsPerStep:0.#} 秒／點）・電壓 {vMin:0}–{vMax:0} mV（波動 {pct:0.#}%）・" +
            $"功率中位 {watts.OrderBy(x => x).ElementAt(n / 2):0.#} W。" +
            $"電壓斜率 {vEst.SlopePerStep:0.####}／步{(vEst.SlopeExcludesZero ? "（趨勢站得住腳）" : "（含零，看不出方向）")}・" +
            $"功率斜率 {wEst.SlopePerStep:0.####}／步{(wEst.SlopeExcludesZero ? "（趨勢站得住腳）" : "（含零，看不出方向）")}。" +
            $"結論：{verdict}。" +
            "<b>觀察不是預測</b>：這是取樣期間的實際起伏，不是「這顆電源還能撐多久」的判斷——" +
            "電源的衰退要跨月跨季的資料才談得上，而感測器讀值本身也有 ±2% 的誤差，" +
            "5% 的門檻已經在量測精度邊緣，所以 10% 才算明顯。",
            "mV", "既有電壓／功率歷史（穩健統計）", FactTrustLevel.Derived, false, at, pct);
    }

    // ── helper ────────────────────────────────────────────────────────────

    private static HardwareFact Unavailable(DateTimeOffset at, string key, string name,
        FactAvailability availability, string reason)
        => new(key, Cat, name, "", "", "本機唯讀探測", FactTrustLevel.Unknown, false, at, null, availability, reason);
}
