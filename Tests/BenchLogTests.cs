using System.IO;
using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 跑分期間的實機條件：溫度／頻率區間的累加與描述。
/// </summary>
/// <remarks>
/// 這裡的重點全在「沒讀到」與「讀到 0」的分野：沒有溫度感測器時整段不得提溫度，
/// 更不能以 0 °C 充數——那會讓人以為量到了一個荒謬的低溫。
/// </remarks>
public class BenchConditionsTests
{
    [Fact]
    public void NoSamples_TextIsEmpty()
    {
        var c = new BenchConditions();
        Assert.Equal("", c.Text());
        Assert.Equal(0, c.SampleCount);
        Assert.Null(c.MinTempC);
        Assert.Null(c.MaxTempC);
    }

    [Fact]
    public void NoSensorReadings_TextStaysEmpty()
    {
        var c = new BenchConditions();
        for (int i = 0; i < 10; i++) c.Sample(null, 0);
        Assert.Equal(10, c.SampleCount);
        Assert.Equal("", c.Text());          // 沒溫度也沒頻率 → 整行略過，不印空區間
        Assert.Null(c.MaxTempC);
        Assert.Equal(0, c.MaxClockMHz);
    }

    [Fact]
    public void TemperatureOnly_MentionsTempNotClock()
    {
        var c = new BenchConditions();
        c.Sample(62, 0);
        c.Sample(78, 0);
        string t = c.Text();
        Assert.Contains("期間溫度 62–78 °C", t);
        Assert.DoesNotContain("頻率", t);
    }

    [Fact]
    public void ClockOnly_MentionsClockNotTemp()
    {
        var c = new BenchConditions();
        c.Sample(null, 4200);
        c.Sample(null, 4600);
        string t = c.Text();
        Assert.Contains("頻率 4200–4600 MHz", t);
        Assert.DoesNotContain("溫度", t);
    }

    [Fact]
    public void NarrowRange_CollapsesToSingleValue()
    {
        var c = new BenchConditions();
        c.Sample(70.2, 4000);
        c.Sample(70.6, 4020);          // 溫差 < 1 °C、頻差 < 50 MHz → 不假裝有區間
        string t = c.Text();
        Assert.Contains("期間溫度 71 °C", t);
        Assert.Contains("頻率 4020 MHz", t);
        Assert.DoesNotContain("–", t);
    }

    [Fact]
    public void NonFiniteReadings_AreIgnored()
    {
        var c = new BenchConditions();
        c.Sample(double.NaN, double.PositiveInfinity);
        c.Sample(65, 3800);
        Assert.Equal(65, c.MinTempC);
        Assert.Equal(65, c.MaxTempC);
        Assert.Equal(3800, c.MinClockMHz);
        Assert.Equal(3800, c.MaxClockMHz);
    }

    [Fact]
    public void StableClock_IsNotFlaggedAsVaried()
    {
        var c = new BenchConditions();
        for (int i = 0; i < 8; i++) c.Sample(70, 4500 + i);
        Assert.False(c.ClockVaried);
        Assert.DoesNotContain("變動", c.Text());
    }

    [Fact]
    public void ClockSaggingOverATenth_IsFlagged()
    {
        var c = new BenchConditions();
        for (int i = 0; i < 4; i++) c.Sample(85, 4600);
        c.Sample(95, 3200);            // 3200 / 4600 ≈ 0.70 → 逾一成
        Assert.True(c.ClockVaried);
        Assert.Contains("期間頻率變動逾一成", c.Text());
    }

    [Fact]
    public void TooFewSamples_NeverFlagsVariation()
    {
        var c = new BenchConditions();
        c.Sample(80, 4600);
        c.Sample(90, 2000);            // 掉得很兇，但只有兩拍，還不足以下判斷
        Assert.False(c.ClockVaried);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var c = new BenchConditions();
        c.Sample(70, 4000);
        c.Reset();
        Assert.Equal(0, c.SampleCount);
        Assert.Null(c.MaxTempC);
        Assert.Equal(0, c.MaxClockMHz);
        Assert.Equal("", c.Text());
    }
}

/// <summary>
/// 同項目、同設定的重複量測統計：離散度與重複性評語。
/// </summary>
public class BenchStatsTests
{
    [Fact]
    public void NoRuns_SaysSo()
    {
        Assert.Equal("本機尚無同設定的量測紀錄", BenchStats.None.Text);
        Assert.Equal("—", BenchStats.None.Repeatability);
    }

    [Fact]
    public void SingleRun_DoesNotPretendToBeRepresentative()
    {
        var s = new BenchStats(1, 1000, 1000, 1000, "#,0", "kN/s");
        Assert.Equal("—", s.Repeatability);          // 一次還看不出重複性
        Assert.Contains("僅 1 次量測", s.Text);
        Assert.Contains("建議至少測 3 次", s.Text);
    }

    [Fact]
    public void TightSpread_IsGood()
    {
        var s = new BenchStats(3, 990, 1010, 1000, "#,0", "kN/s");
        Assert.Equal(2.0, s.SpreadPercent, 3);
        Assert.Equal("重複性良好", s.Repeatability);
    }

    [Fact]
    public void ModerateSpread_IsAcceptable()
    {
        var s = new BenchStats(3, 950, 1000, 1000, "#,0", "kN/s");
        Assert.Equal(5.0, s.SpreadPercent, 3);
        Assert.Equal("重複性尚可", s.Repeatability);
    }

    [Fact]
    public void WideSpread_AdvisesRetest()
    {
        var s = new BenchStats(4, 800, 1100, 1000, "#,0", "kN/s");
        Assert.Equal(30.0, s.SpreadPercent, 3);
        Assert.Contains("離散偏大", s.Repeatability);
    }

    [Fact]
    public void Text_CarriesUnitAndRange()
    {
        var s = new BenchStats(3, 1234, 1300, 1267, "#,0", "kN/s");
        Assert.Contains("本機同設定 3 次", s.Text);
        Assert.Contains("1,234", s.Text);
        Assert.Contains("kN/s", s.Text);
    }

    [Fact]
    public void ZeroMean_DoesNotDivideByZero()
    {
        var s = new BenchStats(2, 0, 0, 0, "#,0", "");
        Assert.Equal(0, s.SpreadPercent);
    }
}

/// <summary>
/// 跑分紀錄簿：本機歷次成績的落地、統計與「與上次相比」。
/// </summary>
/// <remarks>
/// 這是曦覽唯一承認的跑分基準（不內建任何別台機器的參考分數），
/// 因此它的正確性直接決定了畫面上「較上次快 3%」這句話能不能信。
/// 每個測試都用自己的暫存夾，不碰使用者真正的 %APPDATA%。
/// </remarks>
public class BenchLogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存夾清不掉不算失敗 */ }
    }

    private static BenchRun Run(string kind, string config, double score,
                                bool higher = true, string unit = "kN/s", DateTime? utc = null)
        => new()
        {
            Kind = kind, Title = "測試項目", Config = config, Score = score, Unit = unit,
            HigherIsBetter = higher, Format = "#,0", UtcTime = utc ?? DateTime.UtcNow,
        };

    [Fact]
    public void EmptyLog_HasNoStatsAndNoDelta()
    {
        var log = new BenchLog(_dir);
        Assert.Equal(0, log.Count);
        Assert.Equal(0, log.Stats("chess.multi", "16 執行緒").Count);
        Assert.Equal("", log.DeltaText("chess.multi", "16 執行緒"));
        Assert.Empty(log.Recent);
    }

    [Fact]
    public void FirstRun_SaysItIsTheFirst()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", 12000));
        Assert.Equal("本機首次同設定量測", log.DeltaText("chess.multi", "16 執行緒"));
    }

    [Fact]
    public void HigherIsBetter_ImprovementReadsAsFaster()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", 10000));
        log.Add(Run("chess.multi", "16 執行緒", 11000));
        Assert.Equal("較上次同設定快 10.0%", log.DeltaText("chess.multi", "16 執行緒"));
    }

    [Fact]
    public void HigherIsBetter_RegressionReadsAsSlower()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", 10000));
        log.Add(Run("chess.multi", "16 執行緒", 9000));
        Assert.Equal("較上次同設定慢 10.0%", log.DeltaText("chess.multi", "16 執行緒"));
    }

    [Fact]
    public void LowerIsBetter_ShorterTimeReadsAsFaster()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("superpi", "100,000 位", 10.0, higher: false, unit: "秒"));
        log.Add(Run("superpi", "100,000 位", 8.0, higher: false, unit: "秒"));
        Assert.Equal("較上次同設定快 20.0%", log.DeltaText("superpi", "100,000 位"));
    }

    [Fact]
    public void LowerIsBetter_LongerTimeReadsAsSlower()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("superpi", "100,000 位", 8.0, higher: false, unit: "秒"));
        log.Add(Run("superpi", "100,000 位", 10.0, higher: false, unit: "秒"));
        Assert.Equal("較上次同設定慢 25.0%", log.DeltaText("superpi", "100,000 位"));
    }

    [Fact]
    public void WithinOnePercent_IsCalledEquivalent()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", 10000));
        log.Add(Run("chess.multi", "16 執行緒", 10050));      // 0.5%：跑分本有的波動，不當成進步
        string t = log.DeltaText("chess.multi", "16 執行緒");
        Assert.Contains("相當", t);
        Assert.DoesNotContain("快", t);
        Assert.DoesNotContain("慢", t);
    }

    [Fact]
    public void DifferentConfig_IsNotComparedAcross()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", 10000));
        log.Add(Run("chess.multi", "64 執行緒", 30000));
        // 設定不同的成績本就不該相比：64 執行緒那筆仍是它自己設定下的首次
        Assert.Equal("本機首次同設定量測", log.DeltaText("chess.multi", "64 執行緒"));
        Assert.Equal(1, log.Stats("chess.multi", "16 執行緒").Count);
        Assert.Equal(1, log.Stats("chess.multi", "64 執行緒").Count);
    }

    [Fact]
    public void DifferentKind_IsNotComparedAcross()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.single", "深度 4", 800));
        log.Add(Run("chess.multi", "深度 4", 12000));
        Assert.Equal(1, log.Stats("chess.single", "深度 4").Count);
        Assert.Equal(1, log.Stats("chess.multi", "深度 4").Count);
    }

    [Fact]
    public void Stats_AggregatesSameConfigOnly()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("bench.composite", "30 秒", 1000, unit: "分"));
        log.Add(Run("bench.composite", "30 秒", 1200, unit: "分"));
        log.Add(Run("bench.composite", "30 秒", 1100, unit: "分"));
        log.Add(Run("bench.composite", "60 秒", 5000, unit: "分"));

        var s = log.Stats("bench.composite", "30 秒");
        Assert.Equal(3, s.Count);
        Assert.Equal(1000, s.Min);
        Assert.Equal(1200, s.Max);
        Assert.Equal(1100, s.Mean, 6);
        Assert.Equal("分", s.Unit);
    }

    [Fact]
    public void NonFiniteScore_IsRejected()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", double.NaN));
        log.Add(Run("chess.multi", "16 執行緒", double.PositiveInfinity));
        Assert.Equal(0, log.Count);          // 那不是量測結果
    }

    [Fact]
    public void EmptyKind_IsRejected()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("", "16 執行緒", 10000));
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void Recent_IsNewestFirstAndCapped()
    {
        var log = new BenchLog(_dir);
        for (int i = 1; i <= BenchLog.RecentShown + 5; i++)
            log.Add(Run("chess.multi", "16 執行緒", i * 100));

        Assert.Equal(BenchLog.RecentShown, log.Recent.Count);
        Assert.Equal((BenchLog.RecentShown + 5) * 100, log.Recent[0].Score);   // 新的在最前
        Assert.True(log.Recent[0].Score > log.Recent[1].Score);
    }

    [Fact]
    public void RecentOf_FiltersByKindPrefix()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.single", "深度 4", 800));
        log.Add(Run("bench.composite", "30 秒", 1000, unit: "分"));
        log.Add(Run("chess.multi", "深度 4", 12000));

        var chess = log.RecentOf("chess.");
        Assert.Equal(2, chess.Count);
        Assert.Equal("chess.multi", chess[0].Kind);      // 新到舊
        Assert.Equal("chess.single", chess[1].Kind);
        Assert.Single(log.RecentOf("bench."));
        Assert.Empty(log.RecentOf("disk."));
    }

    [Fact]
    public void RecentOf_HonoursMaxCount()
    {
        var log = new BenchLog(_dir);
        for (int i = 0; i < 10; i++) log.Add(Run("chess.multi", "16 執行緒", 100 + i));
        Assert.Equal(3, log.RecentOf("chess.", 3).Count);
    }

    [Fact]
    public void ExceedingMaxRuns_DropsOldest()
    {
        var log = new BenchLog(_dir);
        for (int i = 1; i <= BenchLog.MaxRuns + 10; i++)
            log.Add(Run("chess.multi", "16 執行緒", i));

        Assert.Equal(BenchLog.MaxRuns, log.Count);
        var s = log.Stats("chess.multi", "16 執行緒");
        Assert.Equal(BenchLog.MaxRuns, s.Count);
        Assert.Equal(11, s.Min);                          // 前 10 筆已被丟棄
        Assert.Equal(BenchLog.MaxRuns + 10, s.Max);
    }

    [Fact]
    public void RunsSurviveReload()
    {
        var utc = new DateTime(2026, 8, 29, 3, 4, 5, DateTimeKind.Utc);
        var first = new BenchLog(_dir);
        first.Add(new BenchRun
        {
            Kind = "chess.multi", Title = "象棋 多執行緒", Config = "16 執行緒 ・ 10 秒 ・ 深度 4",
            Score = 12345.6, Unit = "kN/s", HigherIsBetter = true, Format = "#,0",
            UtcTime = utc, Conditions = "期間溫度 62–78 °C ・ 頻率 4200–4600 MHz",
        });

        var again = new BenchLog(_dir);
        Assert.Equal(1, again.Count);
        var r = again.Recent[0];
        Assert.Equal("chess.multi", r.Kind);
        Assert.Equal("象棋 多執行緒", r.Title);
        Assert.Equal("16 執行緒 ・ 10 秒 ・ 深度 4", r.Config);
        Assert.Equal(12345.6, r.Score, 6);
        Assert.Equal(utc, r.UtcTime);
        Assert.True(r.HasConditions);
        Assert.Contains("62–78 °C", r.Conditions);
        Assert.Contains("kN/s", r.ScoreText);
    }

    [Fact]
    public void DeltaAcrossSessions_ComparesWithThePersistedRun()
    {
        new BenchLog(_dir).Add(Run("chess.multi", "16 執行緒", 10000));
        var next = new BenchLog(_dir);
        next.Add(Run("chess.multi", "16 執行緒", 11000));
        Assert.Equal("較上次同設定快 10.0%", next.DeltaText("chess.multi", "16 執行緒"));
    }

    [Fact]
    public void CorruptFile_IsTreatedAsNoHistory()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bench-history.json"), "這不是跑分紀錄");
        var log = new BenchLog(_dir);
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Recent);
    }

    [Fact]
    public void Clear_EmptiesListAndDeletesFile()
    {
        var log = new BenchLog(_dir);
        log.Add(Run("chess.multi", "16 執行緒", 10000));
        Assert.True(File.Exists(log.FilePath));

        log.Clear();
        Assert.Equal(0, log.Count);
        Assert.Empty(log.Recent);
        Assert.False(File.Exists(log.FilePath));
        Assert.Equal("", log.DeltaText("chess.multi", "16 執行緒"));
    }

    [Fact]
    public void Updated_FiresOnAddAndClear()
    {
        var log = new BenchLog(_dir);
        int n = 0;
        log.Updated += () => n++;
        log.Add(Run("chess.multi", "16 執行緒", 10000));
        log.Clear();
        Assert.Equal(2, n);
    }

    [Fact]
    public void ScoreText_UsesInvariantFormatting()
    {
        var r = new BenchRun { Kind = "k", Score = 1234567.891, Format = "#,0", Unit = "kN/s" };
        Assert.Equal("1,234,568 kN/s", r.ScoreText);

        var t = new BenchRun { Kind = "k", Score = 8.5, Format = "0.000", Unit = "秒" };
        Assert.Equal("8.500 秒", t.ScoreText);
    }

    [Fact]
    public void NoConditions_IsReportedAsAbsent()
    {
        var r = new BenchRun { Kind = "k", Score = 1 };
        Assert.False(r.HasConditions);
        Assert.Equal("", r.Conditions);
    }
}
