using Xunit;

namespace XinSpect.Tests;

public class HardwareRandomBenchAdapterTests
{
    [Fact]
    public void Quick與Full使用固定分段與輪數()
    {
        (int RdrandValues, int RdSeedValues, int Rounds) quick =
            HardwareRandomBenchAdapter.GetWorkload(DeepBenchRunProfile.Quick);
        (int RdrandValues, int RdSeedValues, int Rounds) full =
            HardwareRandomBenchAdapter.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal((500_000, 100_000, 3), quick);
        Assert.Equal((1_000_000, 200_000, 7), full);
    }

    [Fact]
    public async Task 成功量測保留RDRAND與RDSEED原始樣本()
    {
        var engine = new FakeHardwareRandomEngine();
        var service = new HardwareRandomBenchAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        Assert.Equal(2, engine.Calls.Count);
        Assert.Equal(HardwareRandomSource.Rdrand, engine.Calls[0].Source);
        Assert.Equal(HardwareRandomSource.RdSeed, engine.Calls[1].Source);
        Assert.Equal(
        [
            "cpu.rdrand.throughput-mvals",
            "cpu.rdrand.retry-ratio",
            "cpu.rdseed.throughput-mvals",
            "cpu.rdseed.retry-ratio"
        ], result.Metrics.Select(metric => metric.Id));

        AssertDoubles(result.Metrics[0].Samples, 25, 12.5, 6.25);
        AssertDoubles(result.Metrics[1].Samples, 0.000004, 0.000008, 0.000016);
        AssertDoubles(result.Metrics[2].Samples, 2, 1, 2.0 / 3);
        AssertDoubles(result.Metrics[3].Samples, 0.00004, 0.00008, 0.00012);

        string limitations = string.Join('\n', result.Limitations);
        Assert.Contains("不是熵源品質認證", limitations, StringComparison.Ordinal);
        Assert.Contains("不是密碼學安全性評分", limitations, StringComparison.Ordinal);
        Assert.Contains("retry", limitations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 任一硬體亂數不支援時整項不啟動()
    {
        var engine = new FakeHardwareRandomEngine { RdrandSupported = false };
        var service = new HardwareRandomBenchAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unsupported, result.FailureKind);
        Assert.Empty(engine.Calls);
        Assert.Contains("RDRAND", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 無效量測樣本整項拒收()
    {
        var engine = new FakeHardwareRandomEngine
        {
            RdrandSamples =
            [
            new(500_000, 2, double.NaN),
                new(500_000, 2, 0.02),
                new(500_000, 2, 0.02)
            ]
        };
        var service = new HardwareRandomBenchAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.Unstable, result.FailureKind);
        Assert.Contains("非有限或非正數", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 亂數引擎失敗時不合成結果()
    {
        var engine = new FakeHardwareRandomEngine
        {
            Throw = new InvalidOperationException("entropy exhausted")
        };
        var service = new HardwareRandomBenchAdapter(engine);

        DeepBenchTestResult result = await service.RunAsync(CreateContext(), CancellationToken.None);

        Assert.Equal(DeepBenchFailureKind.PlatformError, result.FailureKind);
        Assert.Contains("entropy exhausted", result.Error, StringComparison.Ordinal);
    }

    private static DeepBenchRunContext CreateContext() =>
        new(Guid.NewGuid(), DeepBenchRunProfile.Quick, new Progress<DeepBenchProgress>());

    private static void AssertDoubles(IReadOnlyList<double> actual, params double[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Math.Abs(expected[i] - actual[i]) <= Math.Max(1e-12, Math.Abs(expected[i]) * 1e-12),
                $"index {i}: expected {expected[i]}, actual {actual[i]}");
        }
    }

    private sealed class FakeHardwareRandomEngine : IHardwareRandomEngine
    {
        public bool RdrandSupported { get; set; } = true;
        public bool RdSeedSupported { get; set; } = true;
        public List<HardwareRandomCall> Calls { get; } = [];
        public InvalidOperationException? Throw;
        public List<HardwareRandomSample> RdrandSamples { get; set; } =
        [
            new(500_000, 2, 0.02),
            new(500_000, 4, 0.04),
            new(500_000, 8, 0.08)
        ];
        public List<HardwareRandomSample> RdSeedSamples { get; set; } =
        [
            new(100_000, 4, 0.05),
            new(100_000, 8, 0.10),
            new(100_000, 12, 0.15)
        ];

        public Task<HardwareRandomMeasurement> MeasureAsync(
            HardwareRandomSource source,
            int valuesPerRound,
            int rounds,
            CancellationToken cancellationToken)
        {
            Calls.Add(new HardwareRandomCall(source, valuesPerRound, rounds));
            if (Throw is not null) throw Throw;

            List<HardwareRandomSample> samples = source == HardwareRandomSource.Rdrand
                ? RdrandSamples
                : RdSeedSamples;
            return Task.FromResult(new HardwareRandomMeasurement(samples.Take(rounds).ToArray()));
        }
    }

    private sealed record HardwareRandomCall(HardwareRandomSource Source, int ValuesPerRound, int Rounds);
}
