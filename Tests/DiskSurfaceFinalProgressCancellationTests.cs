using Xunit;

namespace XinSpect.Tests;

/// <summary>Injected synchronous reads/progress only; no native disk access.</summary>
public sealed class DiskSurfaceFinalProgressCancellationTests
{
    [Theory]
    [InlineData(512L, 512, 0, 1)]
    [InlineData(17L * 512, 512, 0, 17)]
    [InlineData(2048L, 512, 1, 1)]
    public void SyncFinalProgressCancellationDoesNotReturnCompletedScan(
        long totalBytes, int blockSize, int maxBlocks, int expectedReads)
    {
        using var cancel = new CancellationTokenSource();
        int reads = 0;
        var progress = new CancelAtCompletion(cancel);

        var error = Assert.ThrowsAny<OperationCanceledException>(() =>
            DiskSurfaceScanService.ReadBlocks("fake", totalBytes, blockSize, maxBlocks,
                (_, _, count) => { reads++; return (true, count); }, progress, cancel.Token));

        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Equal(expectedReads, reads);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public void SyncFinalProgressWithoutCancellationStillReturnsResult()
    {
        var progress = new CollectProgress();
        var result = DiskSurfaceScanService.ReadBlocks("fake", 512, 512, 0,
            (_, _, count) => (true, count), progress);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalBlocks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(new[] { 100 }, progress.Values);
    }

    private sealed class CancelAtCompletion(CancellationTokenSource cancel) : IProgress<int>
    {
        internal List<int> Values { get; } = [];
        public void Report(int value)
        {
            Values.Add(value);
            if (value == 100) cancel.Cancel();
        }
    }

    private sealed class CollectProgress : IProgress<int>
    {
        internal List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
}