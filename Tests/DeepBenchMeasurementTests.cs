using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

public class DeepBenchMeasurementTests
{
    [Fact]
    public void 空樣本與非有限值不被偽造成成功樣本()
    {
        var empty = DeepBenchMeasurementStatistics.FromSamples([]);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, empty.InvalidSampleCount);
        Assert.Equal(DeepBenchConfidence.Insufficient, empty.Confidence);
        Assert.Contains("無有效樣本", empty.SummaryText, StringComparison.Ordinal);

        var invalid = DeepBenchMeasurementStatistics.FromSamples([double.NaN, double.PositiveInfinity, double.NegativeInfinity]);
        Assert.Equal(0, invalid.Count);
        Assert.Equal(3, invalid.InvalidSampleCount);
        Assert.Equal(DeepBenchConfidence.Insufficient, invalid.Confidence);
    }

    [Fact]
    public void 單一樣本與零平均不可信()
    {
        var one = DeepBenchMeasurementStatistics.FromSamples([123]);
        Assert.Equal(1, one.Count);
        Assert.Equal(DeepBenchConfidence.Insufficient, one.Confidence);

        var zero = DeepBenchMeasurementStatistics.FromSamples([0, 0, 0, 0, 0]);
        Assert.Equal(5, zero.Count);
        Assert.Equal(DeepBenchConfidence.Insufficient, zero.Confidence);
    }

    [Fact]
    public void 百分位採最近排名且離群值用IQR計算()
    {
        double[] samples = [1, 2, 3, 4, 5, 6, 7, 8, 9, 100];
        var stats = DeepBenchMeasurementStatistics.FromSamples(samples);

        Assert.Equal(5, stats.Median);
        Assert.Equal(100, stats.P95);
        Assert.Equal(100, stats.Max);
        Assert.Equal(1, stats.OutlierCount);
        // p99 門檻（≥100 樣本，第 5 部）：10 筆不足——NaN＋標注，不照算
        // 〔修復 v3.21〕語意裁定（v3.20 輪已留檔）：門檻標在 p99 本身（P99Insufficient＋NaN＋
        // SummaryText），Confidence 單一事實來源維持 CV 分級——百科 L2186 成文的是「部分百分位
        // 標 Insufficient」；managed 鏡像稽核（tight-8→High）與 ViewModel／整合報告三面佐證。
        Assert.Equal(DeepBenchConfidence.Low, stats.Confidence);   // 10 筆單調升 CV≈197% → Low；門檻由下一列的 P99Insufficient 釘
        Assert.True(stats.P99Insufficient);
        Assert.True(double.IsNaN(stats.P99));
        Assert.Contains("p99 樣本不足", stats.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void p99在樣本數達門檻時照算()
    {
        // 100 筆 1 + 1 筆 1000：達門檻，最近排名 p99 取第 ceil(0.99*101)=100 筆
        double[] samples = Enumerable.Repeat(1.0, 100).Append(1000).ToArray();
        var stats = DeepBenchMeasurementStatistics.FromSamples(samples);
        Assert.False(stats.P99Insufficient);
        Assert.Equal(1, stats.P99);
        Assert.DoesNotContain("p99 樣本不足", stats.SummaryText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 99.0, 100.0, 101.0, 100.5, 99.5, 100.2, 100.8 }, DeepBenchConfidence.High)]
    [InlineData(new[] { 92.0, 100.0, 108.0, 101.0, 99.0 }, DeepBenchConfidence.Medium)]
    [InlineData(new[] { 80.0, 100.0, 120.0, 95.0, 105.0 }, DeepBenchConfidence.Low)]
    public void CV門檻決定可信度(double[] samples, DeepBenchConfidence expected)
    {
        Assert.Equal(expected, DeepBenchMeasurementStatistics.FromSamples(samples).Confidence);
    }

    [Fact]
    public void 跨配置指標判定為混池且索引軸不算配置()
    {
        // kernel×threads 每點都是不同量測配置：池化平均無意義。
        var ladder = new DeepBenchMetric(
            "memory.stream.bandwidth", "STREAM bandwidth", "GB/s", true, "managed arrays",
            [30, 210],
            [
                new DeepBenchMetricPoint(30, new Dictionary<string, string> { ["kernel"] = "讀取", ["threads"] = "1" }, [30]),
                new DeepBenchMetricPoint(210, new Dictionary<string, string> { ["kernel"] = "三元運算", ["threads"] = "8" }, [210]),
            ]);
        Assert.True(DeepBenchMeasurementStatistics.PoolsDistinctConfigurations(ladder));

        // round 軸只是同配置重複輪的編號：池化統計有效。
        var rounds = new DeepBenchMetric(
            "cpu.sha256.throughput", "SHA-256 throughput", "MiB/s", true, "rounds=2",
            [990, 1010],
            [
                new DeepBenchMetricPoint(990, new Dictionary<string, string> { ["round"] = "1" }, [990]),
                new DeepBenchMetricPoint(1010, new Dictionary<string, string> { ["round"] = "2" }, [1010]),
            ]);
        Assert.False(DeepBenchMeasurementStatistics.PoolsDistinctConfigurations(rounds));

        // 單點與無點位都無從判定跨配置，維持池化路徑。
        var single = rounds with { Points = [rounds.Points[0]] };
        Assert.False(DeepBenchMeasurementStatistics.PoolsDistinctConfigurations(single));
        var noPoints = rounds with { Points = [] };
        Assert.False(DeepBenchMeasurementStatistics.PoolsDistinctConfigurations(noPoints));
    }

    [Fact]
    public void 軸描述採穩定排序且空軸有明示()
    {
        var axes = new Dictionary<string, string> { ["threads"] = "8", ["kernel"] = "讀取" };
        Assert.Equal("kernel=讀取 threads=8", DeepBenchMeasurementStatistics.DescribeAxes(axes));
        Assert.Equal("無軸", DeepBenchMeasurementStatistics.DescribeAxes(new Dictionary<string, string>()));
    }

    [Fact]
    public void 模型Json往返保留樣本與軸()
    {
        var result = new DeepBenchTestResult(
            "memory.cache-latency", Guid.NewGuid(), DeepBenchRunProfile.Full,
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 1, 0, 1, 0, DateTimeKind.Utc),
            "Full ladder",
            [new DeepBenchMetric(
                "memory.cache.latency", "L1 latency", "ns", false, "1 KB",
                [1.1, 1.2, 1.3],
                [new DeepBenchMetricPoint(1.2, new Dictionary<string, string> { ["level"] = "L1" }, [1.1, 1.2, 1.3])])],
            ["背景負載未控制"], ["使用者模式量測"], DeepBenchFailureKind.None, null);

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string json = JsonSerializer.Serialize(result, options);
        var round = JsonSerializer.Deserialize<DeepBenchTestResult>(json, options);

        Assert.NotNull(round);
        Assert.Equal(result.SessionId, round!.SessionId);
        Assert.Equal(result.Metrics[0].Samples, round.Metrics[0].Samples);
        Assert.Equal("L1", round.Metrics[0].Points[0].Axes["level"]);
        Assert.Equal("使用者模式量測", round.Limitations[0]);
    }
}
