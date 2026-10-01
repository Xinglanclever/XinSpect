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
        Assert.Equal(100, stats.P99);
        Assert.Equal(100, stats.Max);
        Assert.Equal(1, stats.OutlierCount);
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
