using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 自基線學習與異常評分（BL-001／BL-004／BL-007／BL-012）的守門。
/// 歷史序列全部合成——真正的歷史倉要跑一小時才有分鐘級樣本，而這裡要驗的是數學與三態。
/// </summary>
public class BaselineLearningTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 4, 0, 0, TimeSpan.Zero);

    /// <summary>合成歷史序列：每個指定指標一串值，min／avg／max 相同（無區間彙整）。</summary>
    private static HistorySeries Series(Dictionary<int, double[]> metrics)
    {
        int n = metrics.Values.Max(v => v.Length);
        var times = new DateTime[n];
        var avg = new float[n * HistoryMetrics.Count];
        var lo = new float[n * HistoryMetrics.Count];
        var hi = new float[n * HistoryMetrics.Count];
        for (int i = 0; i < n; i++) times[i] = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
        foreach (var (metric, values) in metrics)
            for (int i = 0; i < values.Length; i++)
            {
                avg[i * HistoryMetrics.Count + metric] = (float)values[i];
                lo[i * HistoryMetrics.Count + metric] = (float)values[i];
                hi[i * HistoryMetrics.Count + metric] = (float)values[i];
            }
        return new HistorySeries { Times = times, Avg = avg, Min = lo, Max = hi };
    }

    private static double[] Repeat(double value, int count)
    {
        var a = new double[count];
        Array.Fill(a, value);
        return a;
    }

    // ── BL-001 模型 ──────────────────────────────────────────────────────

    [Fact]
    public void 基線模型_平均中位數與樣本數都算得出來()
    {
        var series = Series(new() { [HistoryMetrics.CpuTemp] = [40, 50, 60] });
        var model = BaselineLearningService.BuildModel(series);

        var b = Assert.Single(model);
        Assert.Equal(HistoryMetrics.CpuTemp, b.Metric);
        Assert.Equal("處理器溫度", b.Title);
        Assert.Equal(3, b.Samples);
        Assert.Equal(50, b.Mean, 3);
        Assert.Equal(50, b.P50, 3);
        Assert.Equal(Math.Sqrt(200.0 / 3), b.StdDev, 3);   // 母體標準差
    }

    [Fact]
    public void 整段皆零的指標_不進模型()
    {
        // 磁碟紀錄裡「沒讀到」一律是 0；整段皆 0 代表本機沒有這個感測器，不該被當成量到 0
        var series = Series(new() { [HistoryMetrics.CpuTemp] = [55, 55, 55], [HistoryMetrics.GpuTemp] = [0, 0, 0] });
        var model = BaselineLearningService.BuildModel(series);

        Assert.Single(model);
        Assert.Equal(HistoryMetrics.CpuTemp, model[0].Metric);
    }

    [Fact]
    public void 沒有歷史時_如實說還沒有資料而不是給空基線()
    {
        var facts = BaselineLearningService.Collect(At, HistorySeries.Empty, () => []);
        var baseline = facts.Single(f => f.Key == BaselineLearningService.BaselineKey);

        Assert.Equal(FactAvailability.NotSupported, baseline.Availability);
        Assert.Contains("還沒有資料", baseline.UnavailableReason ?? "");
    }

    [Fact]
    public void 樣本不足時_如實標示而不是給半條基線()
    {
        var series = Series(new() { [HistoryMetrics.CpuTemp] = [50, 50, 50] });
        var facts = BaselineLearningService.Collect(At, series, () => []);
        var baseline = facts.Single(f => f.Key == BaselineLearningService.BaselineKey);

        Assert.Contains("樣本不足", baseline.Value);
        Assert.Contains("不給半條基線", baseline.Value);
    }

    [Fact]
    public void 基線值要列出逐項模型與資料品質說明()
    {
        var series = Series(new() { [HistoryMetrics.CpuTemp] = Repeat(50, 40) });
        var facts = BaselineLearningService.Collect(At, series, () => []);
        var baseline = facts.Single(f => f.Key == BaselineLearningService.BaselineKey);

        Assert.Contains("樣本 40 點", baseline.Value);
        Assert.Contains("處理器溫度 中位 50", baseline.Value);
        Assert.Contains("沒讀到", baseline.Value);   // 0 的表示法要說清楚
    }

    // ── BL-004 異常評分 ──────────────────────────────────────────────────

    [Fact]
    public void 異常評分_以最新一點對基線算最大偏差()
    {
        // 39 筆 50.0、最後一筆 90.0：平均 51、母體標準差 √39、z = 39/√39 = √39 ≈ 6.245
        var values = Repeat(50, 40);
        values[^1] = 90;
        var facts = BaselineLearningService.Collect(At, Series(new() { [HistoryMetrics.CpuTemp] = values }), () => []);
        var score = facts.Single(f => f.Key == BaselineLearningService.ScoreKey);

        Assert.Equal(Math.Sqrt(39), score.NumericValue!.Value, 2);
        Assert.Contains("處理器溫度", score.Value);
        Assert.Contains("6.2σ", score.Value);
        Assert.Contains("分數不是診斷", score.Value);
    }

    [Fact]
    public void 沒有可用基線時_說無法評分而不是給零分()
    {
        var facts = BaselineLearningService.Collect(At, Series(new() { [HistoryMetrics.CpuTemp] = [50, 50] }), () => []);
        var score = facts.Single(f => f.Key == BaselineLearningService.ScoreKey);

        Assert.Equal(FactAvailability.NotSupported, score.Availability);
        Assert.Contains("無法評分", score.UnavailableReason ?? "");
    }

    [Fact]
    public void 標準差為零時_偏差視為零而不是除以零()
    {
        var b = new MetricBaseline(0, "處理器負載", "%", 40, 50, 0, 50, 50);
        Assert.Equal(0, BaselineLearningService.ZScore(90, b));
    }

    // ── BL-007 事件標註 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("System", 41, EventAnnotationLayer.UnexpectedShutdown)]
    [InlineData("System", 6008, EventAnnotationLayer.UnexpectedShutdown)]
    [InlineData("System", 1001, EventAnnotationLayer.BugCheck)]
    [InlineData("System", 4101, EventAnnotationLayer.DisplayTimeout)]
    [InlineData("Microsoft-Windows-WHEA-Logger/Operational", 17, EventAnnotationLayer.Whea)]
    public void 事件分類_四類歸類正確(string channel, int id, string expected)
    {
        Assert.Equal(expected, EventAnnotationLayer.Classify(new AnnotatedEvent(At, channel, id, "")));
    }

    [Fact]
    public void 無法歸類的事件_不產標註也不給其他這種空話()
    {
        var annotations = EventAnnotationLayer.FromEvents(
        [
            new AnnotatedEvent(At, "System", 12345, "隨便一則"),
            new AnnotatedEvent(At.AddMinutes(1), "System", 41, "系統未正常關機"),
        ]);

        var only = Assert.Single(annotations);
        Assert.Equal(EventAnnotationLayer.UnexpectedShutdown, only.Kind);
        Assert.Contains("系統未正常關機", only.Label);
    }

    [Fact]
    public void 事件事實_四類計數與最近筆數都要寫出來()
    {
        var probe = () => (IReadOnlyList<AnnotatedEvent>)
        [
            new AnnotatedEvent(At.AddHours(-1), "System", 41, "非正常關機"),
            new AnnotatedEvent(At.AddHours(-2), "System", 1001, "藍屏"),
            new AnnotatedEvent(At.AddHours(-3), "Microsoft-Windows-WHEA-Logger/Operational", 17, "更正過的硬體錯誤"),
            new AnnotatedEvent(At.AddHours(-4), "System", 4101, "顯示驅動無回應"),
            new AnnotatedEvent(At.AddHours(-5), "System", 999, "不相關"),
        ];
        var facts = FreezeDiagnosisFactsService.Collect(At, probe);
        var f = facts.Single(x => x.Key == FreezeDiagnosisFactsService.SummaryKey);

        Assert.Contains("非預期關機 1 次", f.Value);
        Assert.Contains("藍屏 1 次", f.Value);
        Assert.Contains("WHEA 硬體錯誤 1 次", f.Value);
        Assert.Contains("顯示驅動逾時（TDR） 1 次", f.Value);
        Assert.Contains("可標註 4 筆", f.Value);
        Assert.Contains("觀察不是診斷", f.Value);
        Assert.Equal(4, f.NumericValue);
    }

    [Fact]
    public void 事件記錄讀不到_標讀取錯誤不畫成零次()
    {
        var facts = FreezeDiagnosisFactsService.Collect(At, () => null);
        var f = facts.Single(x => x.Key == FreezeDiagnosisFactsService.SummaryKey);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Equal("", f.Value);
    }

    [Fact]
    public void 都沒有事件時_如實說都沒有並提醒記錄可能被清理()
    {
        var facts = FreezeDiagnosisFactsService.Collect(At, () => []);
        var f = facts.Single(x => x.Key == FreezeDiagnosisFactsService.SummaryKey);

        Assert.Contains("四類事件都沒有", f.Value);
        Assert.Contains("記錄可能被清理", f.Value);
    }
}
