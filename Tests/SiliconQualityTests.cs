using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 體質評估的純函式測試：迴歸、常態分布、參考線挑選、評定閘門。完全不接觸硬體。
///
/// 這一組存在的主要理由是舊版留下的三個具體錯誤，每一個都寫成一條測試釘死：
/// 單點就敢給分數、手動降壓過的機器會拿到虛高分數、以及不管什麼型號都套同一條參考線。
/// </summary>
public class SiliconQualityTests
{
    private static VfPoint P(int cores, double ghz, double v, double t = 60, double w = 100)
        => new(cores, ghz, v, t, w, 30);

    // ── 迴歸 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 完全共線的點要還原出正確的斜率與截距()
    {
        // V = 0.70 + 0.09·f 上的四個點
        var pts = new[] { P(1, 4.4, 1.096), P(2, 4.2, 1.078), P(4, 4.0, 1.060), P(8, 3.6, 1.024) };
        var fit = SiliconQuality.Fit(pts);

        Assert.Equal(0.09, fit.SlopePerGhz, 6);
        Assert.Equal(0.70, fit.InterceptV, 6);
        Assert.Equal(1.0, fit.R2, 6);
        Assert.Equal(9.0, fit.SlopeMvPer100Mhz, 6);   // 0.09 V/GHz ＝ 9 mV／100 MHz
        Assert.Equal(0.8, fit.SpanGhz, 6);
    }

    [Fact]
    public void 迴歸線必過重心這件事要成立()
    {
        // 這是在重心上比較而不是在端點上比較的整個理由；若不成立，ΔV 的定義就錯了。
        var pts = new[] { P(1, 4.5, 1.130), P(2, 4.3, 1.100), P(4, 4.0, 1.070), P(8, 3.5, 1.010) };
        var fit = SiliconQuality.Fit(pts);
        double atCentroid = fit.InterceptV + fit.SlopePerGhz * fit.MeanFreqGhz;
        Assert.Equal(fit.MeanVoltV, atCentroid, 9);
    }

    [Fact]
    public void 頻率沒有跨距時不硬擬一條線()
    {
        // 倍頻被鎖死的平台：所有階梯都是同一個頻率。此時斜率無從估計，必須回 0 而不是回一個大數。
        var pts = new[] { P(1, 4.0, 1.05), P(2, 4.0, 1.06), P(4, 4.0, 1.07) };
        var fit = SiliconQuality.Fit(pts);
        Assert.Equal(0, fit.SlopePerGhz);
        Assert.Equal(0, fit.SpanGhz, 9);
        Assert.Equal(3, fit.Count);
    }

    [Fact]
    public void 只有兩點時不報殘差與判定係數()
    {
        // 兩點必然完美通過一條線，R² 恆為 1、殘差恆為 0——那不是「擬合得好」，是自由度為零。
        var fit = SiliconQuality.Fit(new[] { P(1, 4.4, 1.10), P(8, 3.6, 1.02) });
        Assert.Equal(0, fit.ResidualV);
        Assert.Equal(0, fit.CentroidSe);
        Assert.Equal(0, fit.Slope95Mv);
    }

    // ── 常態分布與百分位 ────────────────────────────────────────────────────

    [Fact]
    public void 常態累積分布的關鍵點要對()
    {
        Assert.Equal(0.5, SiliconQuality.NormalCdf(0), 6);
        Assert.Equal(0.975, SiliconQuality.NormalCdf(1.96), 4);
        Assert.Equal(0.025, SiliconQuality.NormalCdf(-1.96), 4);
        Assert.Equal(0.8413, SiliconQuality.NormalCdf(1), 4);
    }

    [Fact]
    public void 落差為零就是中位數()
        => Assert.Equal(50, SiliconQuality.Percentile(0, 0.035));

    [Fact]
    public void 同頻電壓越低百分位越高()
    {
        int good = SiliconQuality.Percentile(-0.035, 0.035);    // 低一個 σ
        int bad = SiliconQuality.Percentile(+0.035, 0.035);
        Assert.Equal(84, good);
        Assert.Equal(16, bad);
        Assert.True(good > 50 && bad < 50);
    }

    [Fact]
    public void 百分位夾在一到九十九之間()
    {
        // 分布尾端沒有樣本支撐，宣稱第 0 或第 100 百分位是過度聲明。
        Assert.Equal(99, SiliconQuality.Percentile(-1.0, 0.035));
        Assert.Equal(1, SiliconQuality.Percentile(+1.0, 0.035));
        Assert.Equal(0, SiliconQuality.Percentile(-0.02, 0));   // σ 不明時根本不換算
    }

    [Fact]
    public void 有效切換電容就是動態功耗公式的反解()
    {
        // P ＝ C·V²·f：C ＝ 100 W ÷ (1.0² × 4.0 GHz) ＝ 25 nF
        Assert.Equal(25.0, SiliconQuality.EffectiveCapacitanceNf(100, 1.0, 4.0), 6);
        Assert.Equal(0, SiliconQuality.EffectiveCapacitanceNf(100, 0, 4.0));
    }

    // ── 參考線挑選 ──────────────────────────────────────────────────────────

    private static MicroarchInfo U(int family, int model, CoreKind k = CoreKind.Unknown)
        => MicroarchProfile.Identify(family, model, k);

    [Fact]
    public void 參考線的錨點要能還原出錨點電壓()
    {
        // Skylake-X 那條線的錨點是 1.030 V ＠ 4.0 GHz；V0 是由錨點反推的，代回去必須一致。
        var r = SiliconQuality.ReferenceFor(U(6, 0x55));       // i9-7980XE 這一支
        Assert.True(r.IsKnown);
        Assert.Equal(1.030, r.VoltageAt(4.0), 6);
        Assert.Contains("Skylake-X", r.Name);
    }

    [Fact]
    public void 同一個微架構字串的HEDT與用戶端不能共用一條線()
    {
        // Skylake-X 與 Coffee Lake 的 Uarch 都是「Skylake」。混用會讓整批機器的百分位系統性偏移
        // ——這正是舊版寫死一條 Skylake-X 參考線之後，在任何 14 nm 用戶端機器上發生的事。
        var hedt = SiliconQuality.ReferenceFor(U(6, 0x55));
        var client = SiliconQuality.ReferenceFor(U(6, 0x9E));  // Kaby／Coffee Lake
        Assert.True(hedt.IsKnown && client.IsKnown);
        Assert.NotEqual(hedt.V0, client.V0);
        Assert.True(client.VoltageAt(4.0) > hedt.VoltageAt(4.0));
    }

    [Fact]
    public void 混合架構要按核型分流()
    {
        var big = SiliconQuality.ReferenceFor(U(6, 0x97, CoreKind.Performance), CoreKind.Performance);
        var little = SiliconQuality.ReferenceFor(U(6, 0x97, CoreKind.Efficiency), CoreKind.Efficiency);
        Assert.Contains("Golden Cove", big.Name);
        Assert.Contains("Gracemont", little.Name);
    }

    [Fact]
    public void 伺服器部件與未知型號一律不給參考線()
    {
        // 伺服器部件的頻率區間與供電設計和同微架構的用戶端差太多，硬套只會得到系統性偏移。
        foreach (int model in new[] { 0x8F, 0xCF, 0xAD, 0xAF, 0x6A })
        {
            var r = SiliconQuality.ReferenceFor(U(6, model));
            Assert.False(r.IsKnown);
            Assert.NotEqual("", r.Why);
        }
        // 非 Intel Family 6（含 AMD）認不出來，也不給。
        Assert.False(SiliconQuality.ReferenceFor(U(25, 0x21)).IsKnown);
        Assert.False(SiliconQuality.ReferenceFor(MicroarchProfile.Unknown).IsKnown);
    }

    // ── 綜合評定與四道閘門 ──────────────────────────────────────────────────

    /// <summary>
    /// 一組體質不錯的 Skylake-X 工作點：斜率 90 mV／GHz、整條線比 Skylake-X 參考線低約 40 mV，
    /// 並刻意帶 ±1.5 mV 的殘差——完全共線的假資料會讓標準誤變成 0，測不到信賴區間那一段。
    /// </summary>
    private static SiliconInput GoodRun() => new()
    {
        Uarch = U(6, 0x55),
        Points =
        [
            P(1, 4.4, 1.0255), P(2, 4.3, 1.0185), P(4, 4.2, 1.0070),
            P(8, 4.0, 0.9915), P(12, 3.8, 0.9710), P(18, 3.6, 0.9545),
        ],
        VoltFromMsr = true,
        TempDriftC = 9,
        MaxTempC = 71,
        IdlePowerW = 30,
        VoltSource = "MSR 0x198",
        FreqSource = "MSR 0xE7／0xE8",
    };

    [Fact]
    public void 條件齊全時給出百分位與高信賴度()
    {
        var a = SiliconQuality.Evaluate(GoodRun());
        Assert.True(a.Ok);
        Assert.Equal(SiliconConfidence.High, a.Confidence);
        Assert.True(a.HasPercentile);
        Assert.True(a.Percentile > 50, $"同頻電壓低於參考線卻只給第 {a.Percentile} 百分位");
        Assert.Contains("95% 區間", a.PercentileText);
        Assert.Equal("", a.NoPercentileReason);
        Assert.Contains("重心工作點", string.Join("｜", a.Metrics.Select(m => m.Name)));
    }

    [Fact]
    public void 手動改過電壓就不給百分位()
    {
        // 舊版最大的漏洞：手動降壓過的機器會拿到虛高的體質分數，而畫面上看不出來。
        // 量到的是「設定值」而不是「晶片自己要求的值」，所以只能給實測量。
        var a = SiliconQuality.Evaluate(GoodRun() with
        {
            ManualVoltage = true,
            ManualVoltageNote = "偵測到手動電壓設定，本次不給百分位。",
        });
        Assert.True(a.Ok);
        Assert.False(a.HasPercentile);
        Assert.Contains("不給百分位", a.NoPercentileReason);
        Assert.Contains("不給百分位", a.Grade);
        // 但 ΔV 與斜率仍然要照給——它們是有效的實測量。
        var names = a.Metrics.Select(m => m.Name).ToList();
        Assert.Contains(names, n => n.Contains("ΔV"));
        Assert.Contains(names, n => n.Contains("斜率"));
    }

    [Fact]
    public void 單點或兩點不給百分位()
    {
        // 舊版就是拿單一峰值點去換算分數。單點分不出「體質好」與「量測雜訊」，也估不出殘差。
        var one = SiliconQuality.Evaluate(GoodRun() with { Points = [P(1, 4.4, 1.056)] });
        Assert.False(one.HasPercentile);
        Assert.Contains("工作點", one.NoPercentileReason);

        var two = SiliconQuality.Evaluate(GoodRun() with { Points = [P(1, 4.4, 1.056), P(18, 3.6, 0.984)] });
        Assert.False(two.HasPercentile);
    }

    [Fact]
    public void 頻率跨距太小不給百分位()
    {
        var a = SiliconQuality.Evaluate(GoodRun() with
        {
            Points = [P(1, 4.00, 1.020), P(2, 4.01, 1.021), P(4, 4.02, 1.022), P(8, 4.03, 1.023)],
        });
        Assert.False(a.HasPercentile);
        Assert.Contains("跨距", a.NoPercentileReason);
    }

    [Fact]
    public void 型號不在參考表內時只給實測值()
    {
        var a = SiliconQuality.Evaluate(GoodRun() with { Uarch = MicroarchProfile.Unknown });
        Assert.True(a.Ok);
        Assert.False(a.HasPercentile);
        // 沒有參考線就沒有 ΔV 可談，但斜率與擬合品質仍是自足的實測量。
        var names = a.Metrics.Select(m => m.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("ΔV"));
        Assert.Contains(names, n => n.Contains("斜率"));
    }

    [Fact]
    public void 完全沒有工作點時如實說取樣失敗而不給任何數字()
    {
        var a = SiliconQuality.Evaluate(new SiliconInput { Uarch = U(6, 0x55) });
        Assert.False(a.Ok);
        Assert.False(a.HasPercentile);
        Assert.Equal("取樣失敗", a.Grade);
        Assert.Empty(a.Metrics);
    }

    [Fact]
    public void 電壓走感測器時信賴度不給高()
    {
        // 感測器約一秒一筆，有效取樣數少一個數量級，不該和 MSR 高頻取樣同等看待。
        var a = SiliconQuality.Evaluate(GoodRun() with { VoltFromMsr = false });
        Assert.True(a.Confidence < SiliconConfidence.High);
        Assert.Contains("感測器", a.CaveatText);
    }

    [Fact]
    public void 溫度漂移過大要在前提裡明講()
    {
        var a = SiliconQuality.Evaluate(GoodRun() with { TempDriftC = 34 });
        Assert.Contains("溫度漂移", a.CaveatText);
        Assert.True(a.Confidence < SiliconConfidence.High);
    }

    [Fact]
    public void 前提裡一定要說清楚這不是官方分箱值()
    {
        var a = SiliconQuality.Evaluate(GoodRun());
        Assert.Contains("SP", a.CaveatText);
        Assert.Contains("世代典型", a.CaveatText);
    }

    [Fact]
    public void 評定用語不帶玩笑話()
    {
        // 舊版的評語是「I will set the sea ablaze！」與「建議放棄治療？」。
        var samples = new[]
        {
            SiliconQuality.Evaluate(GoodRun()),
            SiliconQuality.Evaluate(GoodRun() with
            {
                Points = [P(1, 4.4, 1.30), P(2, 4.3, 1.29), P(4, 4.2, 1.28), P(8, 4.0, 1.26), P(18, 3.6, 1.22)],
            }),
        };
        foreach (var a in samples)
        {
            Assert.DoesNotContain("放棄治療", a.Grade + a.Summary);
            Assert.DoesNotContain("ablaze", a.Grade + a.Summary);
            Assert.Contains("體質", a.Grade);
        }
        // 同頻電壓明顯高於參考線的那一組必須落在分布下半部
        Assert.True(samples[1].Percentile < 50, $"高電壓卻給了第 {samples[1].Percentile} 百分位");
    }

    // ── 功耗讀值的自我驗證 ──────────────────────────────────────────────────

    [Fact]
    public void 功耗從閒置到滿載有明顯上升就算通過()
    {
        var (ok, note) = SiliconQuality.ValidatePower(30, 180, 38, 74, "MSR 0x611");
        Assert.True(ok);
        Assert.Equal("", note);
    }

    [Fact]
    public void 封裝升溫卻幾乎不動的能量計要被判定不可信()
    {
        // 本機這一級（Skylake-X）實測就是這樣：閒置與全核滿載的計數只差幾個百分點。
        // 硬換算會得出「18 核滿載 1.2 W」，連帶讓有效切換電容整個失真。
        var (ok, note) = SiliconQuality.ValidatePower(95, 99, 40, 75, "MSR 0x611");
        Assert.False(ok);
        Assert.Contains("未通過驗證", note);
        Assert.Contains("40 °C 升到 75 °C", note);
        Assert.Contains("不受影響", note);       // 要說清楚 V/F 那一半仍然有效
    }

    [Fact]
    public void 沒有成對功耗讀值時不談可信度也不多嘴()
    {
        Assert.Equal((false, ""), SiliconQuality.ValidatePower(null, 180, 38, 74, "感測器"));
        Assert.Equal((false, ""), SiliconQuality.ValidatePower(30, null, 38, 74, "感測器"));
        Assert.Equal((false, ""), SiliconQuality.ValidatePower(0, 0, null, null, "感測器"));
    }

    [Fact]
    public void 功耗不可信時不列有效切換電容與閒置功耗()
    {
        var bad = GoodRun() with
        {
            IdlePowerW = 95,
            IdleTempC = 40,
            Points = [.. GoodRun().Points.Select(p => p with { PowerW = 99, TempC = 75 })],
            PowerSource = "MSR 0x611 封裝能量計",
        };
        var a = SiliconQuality.Evaluate(bad);
        var names = a.Metrics.Select(m => m.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("C_eff"));
        Assert.DoesNotContain(names, n => n.Contains("閒置封裝功耗"));
        Assert.Contains("未通過驗證", a.CaveatText);
        // 但 V/F 那一半照給——它們走的是另外幾顆 MSR
        Assert.True(a.HasPercentile);
        Assert.Contains(names, n => n.Contains("斜率"));
    }

    [Fact]
    public void 功耗可信時就要列出有效切換電容()
    {
        var a = SiliconQuality.Evaluate(GoodRun() with
        {
            IdlePowerW = 30,
            IdleTempC = 38,
            Points = [.. GoodRun().Points.Select(p => p with { PowerW = 180, TempC = 74 })],
        });
        var names = a.Metrics.Select(m => m.Name).ToList();
        Assert.Contains(names, n => n.Contains("C_eff"));
        Assert.Contains(names, n => n.Contains("閒置封裝功耗"));
    }
}
