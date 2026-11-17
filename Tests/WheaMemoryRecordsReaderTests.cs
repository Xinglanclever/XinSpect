using System.Diagnostics.Eventing.Reader;
using Xunit;

namespace XinSpect.Tests;

public class WheaMemoryRecordsReaderTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static WheaMemoryEvent Event(int id = 19, DateTimeOffset? at = null) => new(id, at ?? At);

    private static WheaMemoryReadResult Read(IEnumerable<WheaMemoryEvent> events) =>
        WheaMemoryRecordsReader.ReadResult(() => events, At, () => TimeSpan.Zero);

    [Fact]
    public void QueryFiltersCandidatesAndWindowBeforeEnumerationAndRequestsNewestFirst()
    {
        var query = WheaMemoryRecordsReader.CreateQuery(At);
        Assert.Equal("System", WheaMemoryRecordsReader.LogPath);
        Assert.True(query.ReverseDirection);
        Assert.False(query.TolerateQueryErrors);
        string xpath = WheaMemoryRecordsReader.BuildXPath(At);
        Assert.Contains("Provider[@Name='Microsoft-Windows-WHEA-Logger']", xpath);
        Assert.Equal(new[] { 1, 17, 18, 19, 23, 46, 47 }, WheaMemoryRecordsReader.CandidateEventIds);
        Assert.DoesNotContain("Version", xpath); // Unknown versions must remain countable candidates.
        foreach (int id in new[] { 1, 17, 18, 19, 23, 46, 47 })
            Assert.Contains("EventID=" + id, xpath);
        Assert.Contains("@SystemTime >= '" + At.AddDays(-WheaMemoryRecordsReader.WindowDays).UtcDateTime.ToString("O") + "'", xpath);
        Assert.Contains("@SystemTime <= '2026-11-17T12:00:00.0000000Z'", xpath);
    }

    [Fact]
    public void AddedCandidateIdsShareExistingWindowAndCandidateCap()
    {
        var result = Read(Enumerable.Repeat(Event(999), 100)
            .Concat([Event(1, At.AddDays(-WheaMemoryRecordsReader.WindowDays - 1)), Event(18, At.AddTicks(1))])
            .Concat(Enumerable.Range(0, 65).Select(i => Event(i % 2 == 0 ? 1 : 18))));
        Assert.Equal(64, result.Count);
        Assert.Equal(WheaMemoryReadStatus.Truncated, result.Status);
        Assert.Equal(32, result.Count(r => r.EventId == 1));
        Assert.Equal(32, result.Count(r => r.EventId == 18));
    }
    [Fact]
    public void NativeTimeoutPreservesTheTimeoutStatus()
    {
        var result = WheaMemoryRecordsReader.ReadResult(() => throw new NativeTimeout(), At);
        Assert.Equal(WheaMemoryReadStatus.TimedOut, result.Status);
        Assert.Empty(result);
    }

    private sealed class NativeTimeout() : EventLogException(1460);

    [Fact]
    public void OldEventDoesNotHideNewerEventsAfterClockReordering()
    {
        var result = Read([Event(at: At.AddDays(-WheaMemoryRecordsReader.WindowDays - 1)), Event()]);
        Assert.Equal(WheaMemoryReadStatus.Complete, result.Status);
        Assert.Single(result);
        Assert.Equal(At, result[0].RecordedAt);
    }

    [Fact]
    public void WindowIsInclusiveAndRejectsFutureEvents()
    {
        var start = At.AddDays(-WheaMemoryRecordsReader.WindowDays);
        var result = Read([Event(at: start.AddTicks(-1)), Event(at: start), Event(), Event(at: At.AddTicks(1))]);
        Assert.Equal(2, result.Count);
        Assert.Equal(start, result.WindowStartUtc);
        Assert.Equal(At, result.WindowEndUtc);
    }

    [Fact]
    public void NoiseDoesNotConsumeThe64CandidateSlots()
    {
        var result = Read(Enumerable.Repeat(Event(999), 100).Concat(Enumerable.Repeat(Event(), 64)));
        Assert.Equal(64, result.Count);
        Assert.Equal(WheaMemoryReadStatus.Complete, result.Status);
    }

    [Theory]
    [InlineData(63, WheaMemoryReadStatus.Complete)]
    [InlineData(64, WheaMemoryReadStatus.Complete)]
    [InlineData(65, WheaMemoryReadStatus.Truncated)]
    public void LookaheadDistinguishesExactCapFromTruncation(int count, WheaMemoryReadStatus expected)
    {
        var result = Read(Enumerable.Repeat(Event(), count));
        Assert.Equal(Math.Min(count, 64), result.Count);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == WheaMemoryReadStatus.Complete, result.Reason is null);
    }

    [Fact]
    public void EmptySuccessfulQueryIsDistinctFromFailure()
    {
        var empty = Read([]);
        var failed = WheaMemoryRecordsReader.ReadResult(() => throw new InvalidOperationException("private detail"), At);
        Assert.Empty(empty);
        Assert.Empty(failed);
        Assert.Equal(WheaMemoryReadStatus.Complete, empty.Status);
        Assert.Equal(WheaMemoryReadStatus.ReadError, failed.Status);
        Assert.DoesNotContain("private detail", failed.Reason);
    }

    [Fact]
    public void MidEnumerationFailurePreservesCollectedEvidence()
    {
        var result = Read(Partial());
        Assert.Single(result);
        Assert.Equal(WheaMemoryReadStatus.ReadError, result.Status);
        Assert.NotNull(result.Reason);

        static IEnumerable<WheaMemoryEvent> Partial()
        {
            yield return Event();
            throw new EventLogException("injected failure");
        }
    }

    [Fact]
    public void AccessDeniedIsNotAnEmptySuccessfulQuery()
    {
        var result = WheaMemoryRecordsReader.ReadResult(() => throw new UnauthorizedAccessException(), At);
        Assert.Equal(WheaMemoryReadStatus.InsufficientPrivilege, result.Status);
        Assert.Empty(result);
    }

    [Fact]
    public void MissingTimestampDoesNotBecomeCurrentTime()
    {
        var result = Read([new WheaMemoryEvent(19, null)]);
        Assert.Empty(result);
        Assert.Equal(WheaMemoryReadStatus.ReadError, result.Status);
    }

    [Fact]
    public void NativeReadTimeoutRetainsTimeoutStatus()
    {
        var result = WheaMemoryRecordsReader.ReadResult(() => throw new TimeoutException(), At);
        Assert.Equal(WheaMemoryReadStatus.TimedOut, result.Status);
    }

    [Fact]
    public void LateEnumerationResultIsNotAccepted()
    {
        int ticks = 0;
        var result = WheaMemoryRecordsReader.ReadResult(() => new[] { Event() }, At,
            () => ticks++ == 0 ? TimeSpan.Zero : WheaMemoryRecordsReader.ReadBudget);
        Assert.Empty(result);
        Assert.Equal(WheaMemoryReadStatus.TimedOut, result.Status);
    }

    [Fact]
    public void UnboundedNoiseHasAnExplicitScanBudget()
    {
        var result = Read(Enumerable.Repeat(Event(999), WheaMemoryRecordsReader.ScanCap + 1));
        Assert.Empty(result);
        Assert.Equal(WheaMemoryReadStatus.Truncated, result.Status);
    }

    [Fact]
    public void EnumerationNeverClaimsToDecodeCperSections()
    {
        var result = Read([Event()]);
        Assert.Null(result[0].Section);
        Assert.Equal(WheaMemoryRecordsReader.BlobNotParsed, result[0].Why);
        IReadOnlyList<WheaMemoryRecord> compatible = result;
        Assert.Same(result[0], compatible[0]);
    }

    [Fact]
    public void EarlyTruncationDisposesTheEventEnumerator()
    {
        bool disposed = false;
        var result = Read(Events());
        Assert.Equal(WheaMemoryReadStatus.Truncated, result.Status);
        Assert.True(disposed);

        IEnumerable<WheaMemoryEvent> Events()
        {
            try
            {
                for (int i = 0; i < 100; i++) yield return Event();
            }
            finally { disposed = true; }
        }
    }
}
