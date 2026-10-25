using System.Globalization;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 感測器儀表板頁（U10-VZ-0057／<c>ui.dashboard.sensors</c>）彙整器的守門：
/// ATX 軌配對、逐日異常折算、核溫滾動緩衝與歷史序列抽取全是純搬運——
/// 用合成資料直接驗證；<b>本服務的契約是「不新增任何讀取」</b>，
/// 測試不需要任何感測器或歷史倉的真實來源。
/// </summary>
public class SensorDashboardServiceTests
{
    /// <summary>解開可空讀值：先釘 NotNull 再取值。</summary>
    private static double V(double? v)
    {
        Assert.NotNull(v);
        return v!.Value;
    }

    /// <summary>解開可空讀值：先釘 NotNull 再取值。</summary>
    private static double RailsVolts(SensorDashboardService.RailReading r) => V(r.Volts);

    [Fact]
    public void 追蹤鍵_以U10目錄正式鍵為準()
        => Assert.Equal("ui.dashboard.sensors", SensorDashboardService.DashboardFactKey);

    // ── 軌配對與電壓還原 ────────────────────────────────────────────────────

    [Fact]
    public void 收軌_只收以軌名回報的列_量不到的軌照實留null()
    {
        var rails = SensorDashboardService.CollectRails(
        [
            ("+12V", 12.05),
            ("+5V", null),              // 有軌名、沒讀值：留著，畫「沒有讀值」
            ("CPU VCore", 1.2),         // 不是 ATX 軌：不收
            ("Voltage #3", 3.3),        // 沒有軌名：不猜，不收
        ]);

        Assert.Equal(2, rails.Count);
        Assert.Equal(12.0, rails[0].Nominal, 9);
        Assert.Equal(12.05, RailsVolts(rails[0]), 9);
        Assert.Equal(5.0, rails[1].Nominal, 9);
        Assert.Null(rails[1].Volts);
    }

    [Fact]
    public void 收軌_沒有任何軌_回空清單_畫面誠實說沒有()
        => Assert.Empty(SensorDashboardService.CollectRails([("CPU VCore", 1.2), ("GPU SOH", 100.0)]));

    [Theory]
    [InlineData("12.096 V", 12.096)]
    [InlineData("3,29 V", 3.29)]        // 逗號小數點（發佈文字跟著執行緒文化走）
    [InlineData("11.98", 11.98)]
    [InlineData("—", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("V 12", null)]          // 數字不在開頭：還原不了，不猜
    public void 還原電壓_從顯示文字還原數值_還原不了就null不冒充0(string? text, double? expected)
    {
        var v = SensorDashboardService.ParseVolts(text);
        if (expected is { } e) Assert.Equal(e, V(v), 6);
        else Assert.Null(v);
    }

    // ── 核溫滾動緩衝 ────────────────────────────────────────────────────────

    [Fact]
    public void 核溫緩衝_新核心第一次出現_歷史欄補缺格_不冒充低溫()
    {
        var buf = new SensorDashboardService.CoreHeatBuffer(4);
        buf.Push([("C0", (double?)50.0)]);          // 第一拍：只有 C0
        buf.Push([("C0", (double?)50.0), ("C1", (double?)60.0)]);   // 第二拍：C1 晚到，C0 照常在場

        var rows = buf.Rows;
        Assert.Equal(["C0", "C1"], rows.Select(r => r.Label).ToArray());

        var c0 = rows[0].Values;
        Assert.Equal(2, c0.Length);                  // 已接兩拍：緩衝只持有接過的欄（補齊到欄數由熱圖的 Compress 做）
        Assert.Equal(50.0, V(c0[0]), 9);
        Assert.Equal(50.0, V(c0[1]), 9);

        var c1 = rows[1].Values;
        Assert.Equal(2, c1.Length);
        Assert.Null(c1[0]);                          // C1 在第一拍缺席＝缺格，不是 0 °C
        Assert.Equal(60.0, V(c1[1]), 9);
    }

    [Fact]
    public void 核溫緩衝_超過欄數_丟最舊的欄()
    {
        var buf = new SensorDashboardService.CoreHeatBuffer(3);
        for (int i = 0; i < 5; i++)
            buf.Push([("C0", (double?)i)]);

        var row = buf.Rows[0];
        Assert.Equal(3, row.Values.Length);
        Assert.Equal(2.0, V(row.Values[0]), 9);         // 0、1 已被擠出
        Assert.Equal(4.0, V(row.Values[^1]), 9);
    }

    [Fact]
    public void 核溫緩衝_某拍缺席的核心_補缺格()
    {
        var buf = new SensorDashboardService.CoreHeatBuffer(3);
        buf.Push([("C0", (double?)50.0), ("C1", (double?)60.0)]);
        buf.Push([("C0", (double?)51.0)]);           // C1 這一拍沒有讀值

        Assert.Null(buf.Rows[1].Values[^1]);
        Assert.Equal(51.0, V(buf.Rows[0].Values[^1]), 9);
    }

    [Fact]
    public void 核溫緩衝_列序穩定_重複標籤不產生第二列()
    {
        var buf = new SensorDashboardService.CoreHeatBuffer(2);
        buf.Push([("C0", (double?)50.0), ("C0", (double?)99.0)]);   // 同名重複：不收
        buf.Push([("C0", (double?)51.0)]);

        var rows = buf.Rows;
        Assert.Single(rows);
        Assert.Equal(50.0, V(rows[0].Values[0]), 9);    // 第一筆有效，重複的那筆沒進來
    }

    // ── 歷史序列抽取 ────────────────────────────────────────────────────────

    private static HistorySeries Series(double[] avg, int metric)
    {
        const int M = HistoryMetrics.Count;
        int n = avg.Length;
        var times = Enumerable.Range(0, n).Select(i => DateTime.UtcNow.AddMinutes(i - n)).ToArray();
        var a = new float[n * M];
        var lo = new float[n * M];
        var hi = new float[n * M];
        for (int i = 0; i < n; i++)
        {
            a[i * M + metric] = (float)avg[i];
            lo[i * M + metric] = (float)avg[i];
            hi[i * M + metric] = (float)avg[i];
        }
        return new HistorySeries { Times = times, Avg = a, Min = lo, Max = hi, SecondLevel = false };
    }

    [Fact]
    public void 歷史抽取_逐點取出平均值()
    {
        var pts = SensorDashboardService.MetricPoints(Series([10, 20, 30], HistoryMetrics.CpuTemp), HistoryMetrics.CpuTemp);
        Assert.Equal(new double[] { 10, 20, 30 }, pts);
    }

    [Fact]
    public void 歷史抽取_整段沒讀值的指標_回空陣列_不把0串成量測()
    {
        // 只有 CPU 溫度有值：顯示卡溫度整段皆 0＝從未量到
        var series = Series([40, 41, 42], HistoryMetrics.CpuTemp);
        Assert.Empty(SensorDashboardService.MetricPoints(series, HistoryMetrics.GpuTemp));
    }
}
