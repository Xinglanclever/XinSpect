using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 效能天花板純解碼層的測試。除了邊界情形，位元解讀一律拿本機（i9-7980XE）實際讀到的原始 MSR 值當輸入——
/// 這樣測到的就不只是「我寫的公式自我一致」，而是「這顆 CPU 真的這樣回答」。不接觸硬體。
/// </summary>
public class CeilingDecoderTests
{
    // 本機實測原始值（皆為 MSR 直讀）
    private const ulong RaplUnitsRaw = 0xA0E03;               // 0x606
    private const ulong PkgLimitRaw = 0x0043FFF800DEFFF8;     // 0x610
    private const ulong PkgInfoRaw = 0x00000B5802180528;      // 0x614
    private const ulong TempTargetRaw = 0x006E0A00;           // 0x1A2
    private const ulong ThermRaw = 0x883C0000;                // 0x19C
    private const ulong ReasonLogged = 0xE0000000;            // 0x64F（只有紀錄位）
    private const ulong ReasonLive = 0xE000E000;              // 0x64F（狀態位同時亮起）

    private static RaplUnits Units => CeilingDecoder.DecodeRaplUnits(RaplUnitsRaw);
    private static PkgPowerInfo Info => CeilingDecoder.DecodePkgPowerInfo(PkgInfoRaw, Units);

    // ===== 0x606 RAPL 單位 =====

    [Fact]
    public void 解本機實測的RAPL單位()
    {
        var u = Units;
        Assert.True(u.Valid);
        Assert.Equal(0.125, u.PowerW, 9);           // 位 3:0 = 3 → 1/2^3 W
        Assert.Equal(1.0 / 16384, u.EnergyJ, 12);   // 位 12:8 = 14 → 1/2^14 J
        Assert.Equal(1.0 / 1024, u.TimeS, 12);      // 位 19:16 = 10 → 1/2^10 秒
    }

    [Fact]
    public void RAPL單位為零時判為無效不當成一比一()
    {
        var u = CeilingDecoder.DecodeRaplUnits(0);
        Assert.False(u.Valid);
        Assert.Equal("—", u.Text);
    }

    // ===== 0x610 封裝功耗上限 =====
    [Fact]
    public void 解本機實測的PL1與PL2()
    {
        var pl1 = CeilingDecoder.DecodePowerLimitHalf((uint)(PkgLimitRaw & 0xFFFFFFFF), Units);
        var pl2 = CeilingDecoder.DecodePowerLimitHalf((uint)(PkgLimitRaw >> 32), Units);

        // 兩者的上限計數都是 0x7FF8 = 32760，乘上 0.125 W 正好是 15 位元能編到的最大值 4095 W
        Assert.Equal(32760, pl1.RawCounts);
        Assert.Equal(4095.0, pl1.Watts, 6);
        Assert.True(pl1.Enabled);
        Assert.False(pl1.Clamped);
        Assert.Equal(56.0, pl1.WindowSec, 6);           // y=15、f=3 → 2^15 × 1.75 × (1/1024) 秒

        Assert.Equal(4095.0, pl2.Watts, 6);
        Assert.True(pl2.Enabled);
        Assert.True(pl2.Clamped);
        Assert.Equal(0.00244140625, pl2.WindowSec, 12);  // y=1、f=1 → 2 × 1.25 × (1/1024) 秒
    }

    [Fact]
    public void 本機的0x610未上鎖()
    {
        Assert.False(CeilingDecoder.PowerLimitLocked(PkgLimitRaw));
        Assert.True(CeilingDecoder.PowerLimitLocked(PkgLimitRaw | (1UL << 63)));
    }

    [Fact]
    public void 時間窗不足一秒改用毫秒()
    {
        Assert.Equal("—", CeilingDecoder.WindowText(0));
        Assert.Equal("2.44 ms", CeilingDecoder.WindowText(0.00244140625));
        Assert.Equal("56 秒", CeilingDecoder.WindowText(56));
    }

    [Fact]
    public void 本機的PL1與PL2都被推到編碼上限故判定沒有功耗牆()
        => Assert.True(CeilingDecoder.PowerWallAbsent(PkgLimitRaw, Units, Info));

    [Fact]
    public void 讀不到0x610時不宣稱沒有功耗牆()
        => Assert.False(CeilingDecoder.PowerWallAbsent(0, Units, Info));

    [Fact]
    public void 真的設在TDP的功耗上限不算沒有牆()
    {
        // 兩半都寫 0x528 = 1320 計數（× 0.125 W = 165 W，正是本機 TDP）並設起啟用位
        const ulong half = 0x8528;
        Assert.False(CeilingDecoder.PowerWallAbsent(half | (half << 32), Units, Info));
    }

    // ===== 0x614 封裝功耗規格 =====

    [Fact]
    public void 解本機實測的封裝功耗規格()
    {
        var i = Info;
        Assert.True(i.Valid);
        Assert.Equal(165.0, i.TdpW, 6);   // 0x528 = 1320 計數
        Assert.Equal(67.0, i.MinW, 6);    // 0x218 = 536
        Assert.Equal(363.0, i.MaxW, 6);   // 0xB58 = 2904
    }

    [Fact]
    public void 封裝功耗規格在讀值為零或單位無效時判為無效()
    {
        Assert.False(CeilingDecoder.DecodePkgPowerInfo(0, Units).Valid);
        Assert.False(CeilingDecoder.DecodePkgPowerInfo(PkgInfoRaw, new RaplUnits(0, 0, 0, false)).Valid);
    }

    [Fact]
    public void 推到編碼上限的功耗牆講成人話時說等於沒有功耗牆()
    {
        var pl = CeilingDecoder.DecodePowerLimitHalf((uint)(PkgLimitRaw & 0xFFFFFFFF), Units);
        var row = CeilingDecoder.DescribePowerLimit("PL1", pl, Info, "MSR 0x610 位 31:0");
        Assert.Equal(Severity.Good, row.Severity);
        Assert.Contains("等於沒有功耗牆", row.Note);
        Assert.Contains("363", row.Note);            // 門檻拿封裝自己宣告的最大功耗來比，不是我猜的
        Assert.True(row.HasEvidence);
    }

    [Fact]
    public void 低於TDP的功耗上限被標成警示()
    {
        var pl = CeilingDecoder.DecodePowerLimitHalf(0x8320, Units);   // 0x320 = 800 計數 → 100 W
        var row = CeilingDecoder.DescribePowerLimit("PL1", pl, Info, "MSR 0x610 位 31:0");
        Assert.Equal(Severity.Warning, row.Severity);
        Assert.Contains("低於封裝宣告的 TDP", row.Note);
    }

    [Fact]
    public void 啟用位為零的功耗上限直接說這條牆不存在()
    {
        var pl = CeilingDecoder.DecodePowerLimitHalf(0x0528, Units);   // 沒有位 15
        var row = CeilingDecoder.DescribePowerLimit("PL2", pl, Info, "MSR 0x610 位 63:32");
        Assert.Equal("未啟用", row.Value);
        Assert.Equal(Severity.Good, row.Severity);
    }

    // ===== 0x1A2 溫度目標 =====

    [Fact]
    public void 解本機實測的溫度目標()
    {
        var (tcc, off, at, valid) = CeilingDecoder.DecodeTemperatureTarget(TempTargetRaw);
        Assert.True(valid);
        Assert.Equal(110, tcc);
        Assert.Equal(0, off);
        Assert.Equal(110, at);
    }

    [Fact]
    public void TCC偏移會讓節流點提前()
    {
        // 位 29:24 填 5 → 活化點 110 °C、偏移 5 °C、實際開始節流 105 °C
        var (tcc, off, at, valid) = CeilingDecoder.DecodeTemperatureTarget(TempTargetRaw | (5UL << 24));
        Assert.True(valid);
        Assert.Equal(110, tcc);
        Assert.Equal(5, off);
        Assert.Equal(105, at);
    }

    [Fact]
    public void 讀不到TCC時溫度目標判為無效()
        => Assert.False(CeilingDecoder.DecodeTemperatureTarget(0).Valid);

    // ===== 0x19C／0x1B1 THERM_STATUS =====

    [Fact]
    public void 解本機實測的THERM_STATUS溫度讀數()
    {
        var t = CeilingDecoder.DecodeThermReadout(ThermRaw, 110);
        Assert.True(t.ReadingValid);
        Assert.Equal(60, t.DigitalReadout);   // 低於活化點 60 度
        Assert.Equal(1, t.ResolutionC);
        Assert.Equal(50, t.TempC);            // 110 − 60
        Assert.True(t.TempKnown);
    }

    [Fact]
    public void TCC未知時不把數位讀數硬換成攝氏()
    {
        var t = CeilingDecoder.DecodeThermReadout(ThermRaw, 0);
        Assert.True(t.ReadingValid);
        Assert.False(t.TempKnown);
        Assert.Equal(0, t.TempC);
    }

    [Fact]
    public void 整個暫存器為零時溫度不可信且會多一列警示()
    {
        Assert.False(CeilingDecoder.DecodeThermReadout(0, 110).TempKnown);
        Assert.Null(CeilingDecoder.ThermSanity(ThermRaw, "MSR 0x19C"));
        var row = CeilingDecoder.ThermSanity(0, "MSR 0x19C");
        Assert.NotNull(row);
        Assert.Equal(Severity.Warning, row!.Severity);
        Assert.Contains("沒真的讀到", row.Note);
    }

    [Fact]
    public void 熱狀態配對分別對應現在正在發生與曾經發生過()
    {
        var rows = CeilingDecoder.DescribeThermPairs(0, "MSR 0x19C");
        Assert.Equal(CeilingDecoder.ThermPairs.Length, rows.Count);
        Assert.All(rows, r => Assert.Equal("從未", r.Value));

        // 位 0 = 狀態位、位 1 = 紀錄位
        Assert.Equal("現在正在發生", CeilingDecoder.DescribeThermPairs(0b11, "x")[0].Value);
        var loggedOnly = CeilingDecoder.DescribeThermPairs(0b10, "x")[0];
        Assert.Equal("曾經發生過", loggedOnly.Value);
        Assert.Equal(Severity.Warning, loggedOnly.Severity);
    }

    [Fact]
    public void 臨界溫度比其他熱事件更嚴重()
    {
        // ThermPairs 第三組是位 4（臨界溫度）
        Assert.Equal(Severity.Critical, CeilingDecoder.DescribeThermPairs(1UL << 4, "x")[2].Severity);
        Assert.Equal(Severity.Serious, CeilingDecoder.DescribeThermPairs(1UL << 0, "x")[0].Severity);
    }

    // ===== 0x64F 限制原因 =====

    [Fact]
    public void 本機的0x64F只有紀錄位亮著故狀態名單為空()
    {
        Assert.Empty(CeilingDecoder.ActiveNames(ReasonLogged));
        var logged = CeilingDecoder.LoggedNames(ReasonLogged);   // 位 29 = 位 13 的紀錄位
        Assert.Single(logged);
        Assert.Equal("渦輪切換衰減", logged[0]);
        Assert.Equal("", CeilingDecoder.UndocumentedText(ReasonLogged));
    }

    [Fact]
    public void 狀態位亮起時同時如實報出未列於文件的位元()
    {
        var active = CeilingDecoder.ActiveNames(ReasonLive);
        Assert.Single(active);
        Assert.Equal("渦輪切換衰減", active[0]);
        // 位 14、15 Intel 未公開用途：如實列出，不替它發明名字
        var text = CeilingDecoder.UndocumentedText(ReasonLive);
        Assert.Contains("位 14、15", text);
        Assert.Contains("未公開", text);
    }

    [Fact]
    public void 限制原因表不收未列於文件的位元()
    {
        var bits = CeilingDecoder.LimitReasons.Select(r => r.Bit).ToHashSet();
        foreach (int b in new[] { 2, 3, 7, 14, 15 }) Assert.DoesNotContain(b, bits);
        Assert.Equal(11, CeilingDecoder.LimitReasons.Length);
    }

    [Fact]
    public void 多核渦輪上限與切換衰減屬正常運作不算故障()
    {
        Assert.True(CeilingDecoder.IsBenign(12));
        Assert.True(CeilingDecoder.IsBenign(13));
        Assert.False(CeilingDecoder.IsBenign(1));
        Assert.False(CeilingDecoder.IsBenign(10));
    }

    [Fact]
    public void 限制原因總表把從未觸發的併成一列()
    {
        var rows = CeilingDecoder.DescribeReasonRows(ReasonLogged);
        Assert.Equal(2, rows.Count);
        Assert.Equal("曾經限制過", rows[0].Value);
        Assert.Equal(Severity.Neutral, rows[0].Severity);      // 位 13 屬正常運作，不染警示色
        Assert.Contains("其餘 10 項原因", rows[1].Name);
        Assert.Equal(Severity.Good, rows[1].Severity);
    }

    [Fact]
    public void 全部原因都沒動過時只剩一列彙總()
    {
        var rows = CeilingDecoder.DescribeReasonRows(0);
        Assert.Single(rows);
        Assert.Contains($"其餘 {CeilingDecoder.LimitReasons.Length} 項原因", rows[0].Name);
    }

    [Fact]
    public void 現在正在限制比曾經限制過嚴重()
    {
        var now = CeilingDecoder.DescribeReasonRows(1UL << 10)[0];   // 封裝功耗 PL1 的狀態位
        Assert.Equal("現在正在限制", now.Value);
        Assert.Equal(Severity.Serious, now.Severity);
        Assert.Equal(Severity.Warning, CeilingDecoder.DescribeReasonRows(1UL << 26)[0].Severity);
    }

    // ===== 累計器與換算 =====

    [Fact]
    public void 三十二位累計器回繞時差分仍正確()
    {
        Assert.Equal(0u, CeilingDecoder.Delta32(5, 5));
        Assert.Equal(10u, CeilingDecoder.Delta32(5, 15));
        Assert.Equal(32u, CeilingDecoder.Delta32(0xFFFFFFF0, 0x00000010));      // 跨過一次回繞
        Assert.Equal(4u, CeilingDecoder.Delta32(0x1_00000005, 0x2_00000009));   // 高 32 位不參與
    }

    [Fact]
    public void 能量與節流換算在單位無效時回零而不是NaN()
    {
        Assert.Equal(0.0, CeilingDecoder.Watts(1000, 0, 5));
        Assert.Equal(0.0, CeilingDecoder.Watts(1000, Units.EnergyJ, 0));
        Assert.Equal(0.0, CeilingDecoder.ThrottledSeconds(1000, 0));
    }

    [Fact]
    public void 能量計數換算瓦數()
        // 每秒 16384 計數 × (1/16384 J) = 1 W
        => Assert.Equal(1.0, CeilingDecoder.Watts(16384, Units.EnergyJ, 1), 9);

    [Fact]
    public void 節流計數乘上時間單位得到秒數()
        => Assert.Equal(0.9765625, CeilingDecoder.ThrottledSeconds(1000, Units.TimeS), 9);

    // ===== 能量計自我驗證 =====

    [Fact]
    public void 能量計速率明顯上升時通過驗證()
    {
        var (ok, text) = CeilingDecoder.ValidateEnergyCounter(20_000, 60_000, 45, 85, true);
        Assert.True(ok);
        Assert.Contains("通過驗證", text);
        Assert.Contains("3.00 倍", text);
    }

    [Fact]
    public void 本機實測的能量計不隨功耗變化故不通過驗證()
    {
        // 實測：閒置 46 °C 每秒約 21700 計數，全核 AVX2 85 °C 約 23700——溫度升了 39 度，計數幾乎沒動
        var (ok, text) = CeilingDecoder.ValidateEnergyCounter(21_700, 23_700, 46, 85, true);
        Assert.False(ok);
        Assert.Contains("未通過驗證", text);
        Assert.Contains("不換算成瓦", text);
    }

    [Fact]
    public void 負載沒讓封裝升溫時老實說無法驗證()
    {
        var (ok, text) = CeilingDecoder.ValidateEnergyCounter(21_700, 22_000, 46, 48, true);
        Assert.False(ok);
        Assert.Contains("無法驗證", text);
    }

    [Fact]
    public void 能量計完全沒前進時判定本平台沒有能量計()
    {
        var (ok, text) = CeilingDecoder.ValidateEnergyCounter(0, 0, 46, 85, true);
        Assert.False(ok);
        Assert.Contains("沒有可用的封裝能量計", text);
    }

    // ===== 倍頻表查表 =====

    [Fact]
    public void 倍頻表取門檻大於等於作用核心數之中最小的那一組()
    {
        List<(int Cores, int Ratio)> g = [(2, 44), (4, 42), (8, 40)];
        Assert.Equal(42, CeilingDecoder.ApplicableTurboRatio(g, 4));   // 剛好落在門檻上
        Assert.Equal(42, CeilingDecoder.ApplicableTurboRatio(g, 3));   // 3 顆核心仍受 4 核那一格管
        Assert.Equal(44, CeilingDecoder.ApplicableTurboRatio(g, 1));
    }

    [Fact]
    public void 作用核心數超出倍頻表時退回門檻最大的那一組()
        => Assert.Equal(40, CeilingDecoder.ApplicableTurboRatio([(2, 44), (4, 42), (8, 40)], 18));

    [Fact]
    public void 讀不到倍頻表時回零而不是猜一個值()
        => Assert.Equal(0, CeilingDecoder.ApplicableTurboRatio([], 18));

    [Fact]
    public void 本機的倍頻表每一格都是四十二倍()
    {
        // 實測 0x1AD 全等於 0x2A2A2A2A2A2A2A2A：單核與 18 核全開都是 42×
        List<(int Cores, int Ratio)> g =
            [(2, 42), (4, 42), (8, 42), (12, 42), (16, 42), (18, 42), (24, 42), (28, 42)];
        Assert.Equal(42, CeilingDecoder.ApplicableTurboRatio(g, 18));
        Assert.Equal(42, CeilingDecoder.ApplicableTurboRatio(g, 1));
    }

    // ===== 量測窗的顯示欄位 =====

    [Fact]
    public void 作用核心數以量得到的分母呈現()
    {
        Assert.Equal("—", new CeilingWindow { Label = "x" }.CoresText);
        Assert.Equal("14 / 18", new CeilingWindow { Label = "x", ActiveCores = 14, CoresMeasured = 18 }.CoresText);
    }

    [Fact]
    public void 量不到倍頻或溫度時顯示破折號而不是零()
    {
        var w = new CeilingWindow { Label = "x" };
        Assert.Equal("—", w.RatioText);
        Assert.Equal("—", w.MhzText);
        Assert.Equal("—", w.TempText);
        Assert.Equal(0.0, w.EnergyRateCps);
    }

    [Fact]
    public void 能量速率以實際窗長為分母()
        => Assert.Equal(2000.0, new CeilingWindow { Label = "x", EnergyCounts = 5000, Seconds = 2.5 }.EnergyRateCps, 6);

    // ===== 判決 =====

    /// <summary>一份「什麼牆都沒撞到」的證據；各測試只改動自己關心的那一項。</summary>
    private static CeilingDecoder.CeilingEvidence Evidence(
        double achieved = 42.0, int target = 42, IReadOnlyList<string>? reasons = null,
        int maxTemp = 85, double throttledSec = 0, double avxDrop = 0) => new()
    {
        TargetRatio = target, AchievedRatio = achieved, BclkMhz = 100, ActiveCores = 18,
        MaxTempC = maxTemp, ThrottleAtC = 110, TempKnown = true,
        ThrottledSec = throttledSec, WindowSec = 5,
        NewReasons = reasons ?? [], PowerWallDisabled = true,
        AvxRatioDrop = avxDrop, WidestVectorLabel = avxDrop > 0 ? "AVX-512 512 位元浮點（全核）" : "",
    };

    [Fact]
    public void 判決在量不到有效時脈時不做任何歸因()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(Evidence(achieved: 0));
        Assert.Equal(Severity.Neutral, sev);
        Assert.Contains("量不到有效時脈", head);
        Assert.Contains("不做任何判決", detail);
    }

    [Fact]
    public void 判決在讀不到倍頻表時只報實測值不宣稱有缺口()
    {
        var (sev, head, _) = CeilingDecoder.Verdict(Evidence(target: 0));
        Assert.Equal(Severity.Neutral, sev);
        Assert.Contains("沒有可比的目標值", head);
        Assert.Contains("42.0×", head);
    }

    [Fact]
    public void 本機實測的結論是沒有撞到任何硬體天花板()
    {
        // 倍頻表 42×、實測 42×、最高 85 °C（節流點 110）、PL1／PL2 都推到編碼上限
        var (sev, head, detail) = CeilingDecoder.Verdict(Evidence());
        Assert.Equal(Severity.Good, sev);
        Assert.Contains("沒有撞到任何硬體天花板", head);
        Assert.Contains("還有 25 °C", detail);
        Assert.Contains("功耗牆等於不存在", detail);
    }

    [Fact]
    public void 差距在量測誤差內仍算已經到頂()
        => Assert.Equal(Severity.Good, CeilingDecoder.Verdict(Evidence(achieved: 41.6)).Sev);

    [Fact]
    public void 溫度貼到節流點就判溫度牆()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(Evidence(achieved: 35, maxTemp: 109));
        Assert.Equal(Severity.Serious, sev);
        Assert.StartsWith("溫度牆：", head);
        Assert.Contains("缺口 7.0×", head);
        Assert.Contains("700 MHz", head);
        Assert.Contains("往散熱查", detail);
    }

    [Fact]
    public void 熱位元新亮起時即使溫度沒貼牆也判溫度牆()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(
            Evidence(achieved: 35, maxTemp: 70, reasons: ["熱狀態"]));
        Assert.Equal(Severity.Serious, sev);
        Assert.StartsWith("溫度牆：", head);
        Assert.Contains("這是硬證據", detail);
    }

    [Fact]
    public void 節流累計時間大於零就判功耗牆()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(
            Evidence(achieved: 35, maxTemp: 70, throttledSec: 1.25));
        Assert.Equal(Severity.Serious, sev);
        Assert.StartsWith("功耗牆：", head);
        Assert.Contains("1.250 秒", detail);
        Assert.Contains("25%", detail);
    }

    [Fact]
    public void 電流上限與供電模組過熱分開歸因()
    {
        var amps = CeilingDecoder.Verdict(Evidence(achieved: 35, maxTemp: 70, reasons: ["電流上限"]));
        Assert.StartsWith("電流牆", amps.Headline);
        Assert.Contains("IccMax", amps.Detail);

        var vr = CeilingDecoder.Verdict(Evidence(achieved: 35, maxTemp: 70, reasons: ["VR 過熱警報"]));
        Assert.StartsWith("供電模組過熱", vr.Headline);
        Assert.Contains("不是 CPU 過熱", vr.Detail);
    }

    [Fact]
    public void 自動HWP屬電源政策問題故只給警示()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(
            Evidence(achieved: 35, maxTemp: 70, reasons: ["自動 HWP"]));
        Assert.Equal(Severity.Warning, sev);
        Assert.StartsWith("硬體自主 P-state", head);
        Assert.Contains("不是散熱或供電問題", detail);
    }

    [Fact]
    public void 多核渦輪上限是規格不是故障故判中性()
    {
        var (sev, head, _) = CeilingDecoder.Verdict(
            Evidence(achieved: 35, maxTemp: 70, reasons: ["多核渦輪上限"]));
        Assert.Equal(Severity.Neutral, sev);
        Assert.StartsWith("就是倍頻表本身", head);
    }

    [Fact]
    public void 沒有任何原因位元亮起但向量負載掉頻時歸因為授權降頻()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(Evidence(achieved: 35, maxTemp: 70, avxDrop: 4));
        Assert.Equal(Severity.Warning, sev);
        Assert.StartsWith("向量指令授權降頻", head);
        Assert.Contains("設計行為，不是故障", detail);
    }

    [Fact]
    public void 找不到硬體證據時就說找不到並把讀者導向電源政策()
    {
        var (sev, head, detail) = CeilingDecoder.Verdict(Evidence(achieved: 35, maxTemp: 70));
        Assert.Equal(Severity.Warning, sev);
        Assert.StartsWith("找不到硬體天花板", head);
        Assert.Contains("電源政策實況", detail);
        Assert.DoesNotContain("溫度牆", head);
    }

    [Fact]
    public void 界線宣告明確說出全程唯讀且會自己製造負載()
    {
        Assert.Contains("全程唯讀", CeilingDecoder.ScopeNotice);
        Assert.Contains("不清除任何黏滯紀錄位", CeilingDecoder.ScopeNotice);
        Assert.Contains("自己製造 CPU 負載", CeilingDecoder.ScopeNotice);
    }
}
