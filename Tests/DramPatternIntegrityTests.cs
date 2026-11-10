using Xunit;

namespace XinSpect.Tests;

public sealed class DramPatternIntegrityTests
{
    [Fact]
    public void 圖樣包含補數與地址相依值並能逐位元組往返()
    {
        var result = new ManagedDramPatternIntegrityEngine().Run(1024 * 1024, null, CancellationToken.None);

        Assert.Equal(6, result.PatternsPassed);
        Assert.Equal(0, result.Mismatches);
        Assert.Equal(6L * 1024 * 1024, result.BytesTested);
        Assert.Equal(0xAA, DramPatternIntegrityService.PatternByte(2, 0));
        Assert.Equal(0x55, DramPatternIntegrityService.PatternByte(2, 1));
        Assert.NotEqual(DramPatternIntegrityService.PatternByte(3, 1), DramPatternIntegrityService.PatternByte(3, 2));
    }

    [Fact]
    public void 緩衝區上限固定在16MiB且支援取消()
    {
        var result = new ManagedDramPatternIntegrityEngine().Run(int.MaxValue, null, CancellationToken.None);
        Assert.Equal(6L * DramPatternIntegrityService.MaxBufferBytes, result.BytesTested);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new ManagedDramPatternIntegrityEngine().Run(1024, null, cts.Token));
    }
}
