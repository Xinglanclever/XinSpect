using Xunit;

namespace XinSpect.Tests;

public class DeepBenchCryptoTests
{
    [Fact]
    public async Task Quick深測保留AES與SHA原始樣本與單位()
    {
        var result = await new CryptoMicrobenchService().RunAsync(CreateContext(DeepBenchRunProfile.Quick), CancellationToken.None);

        Assert.Equal("cpu.aes-sha", result.TestId);
        Assert.Equal(DeepBenchFailureKind.None, result.FailureKind);
        var metrics = result.Metrics.ToDictionary(metric => metric.Id, metric => metric);
        Assert.Equal(["cpu.aes.cbc.throughput", "cpu.sha256.throughput"], metrics.Keys.Order(StringComparer.Ordinal));
        Assert.All(metrics.Values, metric =>
        {
            Assert.Equal("MiB/s", metric.Unit);
            Assert.True(metric.HigherIsBetter);
            Assert.Equal(2, metric.Samples.Count);
            Assert.All(metric.Samples, sample => Assert.True(double.IsFinite(sample) && sample > 0));
        });
        Assert.Contains("AES-256-CBC / PKCS7", result.Configuration, StringComparison.Ordinal);
        Assert.Contains("SHA-256", result.Configuration, StringComparison.Ordinal);
        Assert.Contains(result.Limitations, limitation => limitation.Contains(".NET crypto API measurement", StringComparison.Ordinal));
    }

    [Fact]
    public void 工作負載遵循Quick與Full設定()
    {
        var quick = CryptoMicrobenchService.GetWorkload(DeepBenchRunProfile.Quick);
        var full = CryptoMicrobenchService.GetWorkload(DeepBenchRunProfile.Full);

        Assert.Equal((2, 2 * 1024 * 1024, 2, 4 * 1024 * 1024), (quick.AesRounds, quick.AesBytesPerRound, quick.ShaRounds, quick.ShaBytesPerRound));
        Assert.Equal((5, 16 * 1024 * 1024, 5, 32 * 1024 * 1024), (full.AesRounds, full.AesBytesPerRound, full.ShaRounds, full.ShaBytesPerRound));
    }

    [Fact]
    public void AES先完成來回驗證再計時()
    {
        byte[] plain = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        byte[] key = new byte[32];
        byte[] iv = new byte[16];

        Assert.True(CryptoMicrobenchService.VerifyRoundTrip(plain, key, iv));
    }

    [Fact]
    public async Task 取消前不配置或計時任何工作負載()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await new CryptoMicrobenchService().RunAsync(CreateContext(DeepBenchRunProfile.Full), cts.Token);

        Assert.Equal(DeepBenchFailureKind.Cancelled, result.FailureKind);
        Assert.Empty(result.Metrics);
        Assert.Contains("取消", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void 非有限吞吐樣本會被計數而不是改成零()
    {
        var (samples, invalidCount) = CryptoMicrobenchService.FiniteSamples([1.25, double.NaN, 2.5, double.PositiveInfinity, 3.75]);

        Assert.Equal([1.25, 2.5, 3.75], samples);
        Assert.Equal(2, invalidCount);
    }

    private static DeepBenchRunContext CreateContext(DeepBenchRunProfile profile) =>
        new(Guid.NewGuid(), profile, new Progress<DeepBenchProgress>());
}
