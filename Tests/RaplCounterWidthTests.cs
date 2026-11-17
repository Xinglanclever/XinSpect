using System.Diagnostics;
using Xunit;
namespace XinSpect.Tests;

// Real probe dataflow; readers/time/delays are synthetic and the sole worker only waits for stop.
public sealed class RaplCounterWidthTests
{
    [Theory]
    [InlineData(100UL, 132UL, 300UL, 396UL, RaplGateVerdict.Trusted)]
    [InlineData(0xFFFFFFF0UL, 0x10UL, 300UL, 396UL, RaplGateVerdict.Trusted)]
    [InlineData(100UL, 132UL, 0xFFFFFFF0UL, 0x10UL, RaplGateVerdict.NotResponding)]
    public async Task BaselineAndLoadWrapUseTheir32BitCountsBeforeVerdict(
        ulong baseStart, ulong baseEnd, ulong loadStart, ulong loadEnd, RaplGateVerdict expected)
    {
        var readings = new Queue<ulong>(new[] { baseStart, baseEnd, loadStart, loadEnd });
        long ticks = 0;
        Thread? worker = null;
        var probe = new RaplEnergyProbe(() => true, () => 1, (_, _) => Task.CompletedTask,
            (token, _) => worker = new Thread(() => token.WaitHandle.WaitOne()) { IsBackground = true },
            timestamp: () => Interlocked.Add(ref ticks, Stopwatch.Frequency * 3));
        var result = await probe.RunAsync(() => readings.Dequeue(), () => 50, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(expected, result.Verdict);
        Assert.Equal(loadEnd, result.LastRawCount);
        Assert.Empty(readings);
        Assert.NotNull(worker);
        Assert.False(worker!.IsAlive);
    }
}
