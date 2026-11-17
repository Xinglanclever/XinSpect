using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

// Injected records only. No EventLog, firmware, hardware, WMI or UI execution.
public sealed class WheaMemoryPublicationTests
{
    private static IReadOnlyList<WheaMemoryRecord> Records(int count) =>
        Enumerable.Range(0, count).Select(i => new WheaMemoryRecord(19,
            DateTimeOffset.UnixEpoch.AddSeconds(i), null, "injected candidate")).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealTimeoutRevokesLateSuccessOrFailureWithoutStoppingWorkerCleanup(bool failLate)
    {
        var svc = new EvidenceLabService();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cleaned = new ManualResetEventSlim();
        Thread? sourceWorker = null;
        try
        {
            EvidenceCollection.MeasureWheaMemory(svc, () =>
            {
                sourceWorker = Thread.CurrentThread;
                entered.Set();
                try
                {
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test release");
                    if (failLate) throw new IOException("private old failure");
                    return Records(1);
                }
                finally { cleaned.Set(); }
            }, 100);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(Assert.Single(svc.Steps).Succeeded);
            var timeout = Assert.Single(svc.WheaMemoryFacts);
            Assert.Equal(FactAvailability.ReadError, timeout.Availability);
            Assert.Contains("逾時", timeout.UnavailableReason!);
            Assert.False(cleaned.IsSet); // outer timeout did not stop source or run cleanup early
            svc.LoadWheaMemory(Records(2));
            var current = svc.WheaMemoryFacts;
            int lateNotifications = 0;
            svc.PropertyChanged += (_, _) => lateNotifications++;
            release.Set();
            Assert.True(cleaned.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(sourceWorker);
            Assert.True(sourceWorker!.Join(TimeSpan.FromSeconds(5)));
            Assert.Same(current, svc.WheaMemoryFacts);
            Assert.Equal(0, lateNotifications);
            Assert.DoesNotContain(svc.WheaMemoryFacts, f => f.Availability == FactAvailability.ReadError);
        }
        finally
        {
            release.Set();
            if (sourceWorker is not null) Assert.True(sourceWorker.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void ExpiredCommitPublishesTimeoutEvenBeforeOuterTimeoutCallback()
    {
        var svc = new EvidenceLabService();
        svc.LoadWheaMemory(Records(2));
        var lease = svc.BeginWheaMemoryCollection(1, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetElapsedTime(lease.Started).TotalMilliseconds >= 2,
            TimeSpan.FromSeconds(5)));
        Assert.False(svc.CollectWheaMemory(lease, () => Records(1)));
        var timeout = Assert.Single(svc.WheaMemoryFacts);
        Assert.Equal(FactAvailability.ReadError, timeout.Availability);
        Assert.Contains("逾時", timeout.UnavailableReason!);
        var committed = svc.WheaMemoryFacts;
        svc.RevokeWheaMemoryCollection(lease, timedOut: true);
        Assert.Same(committed, svc.WheaMemoryFacts);
    }

    [Fact]
    public void CompletedLeaseCannotBeOverwrittenByDelayedTimeoutCallback()
    {
        var svc = new EvidenceLabService();
        var lease = svc.BeginWheaMemoryCollection(0, CancellationToken.None);
        Assert.True(svc.CollectWheaMemory(lease, () => Records(2)));
        var expected = svc.WheaMemoryFacts;
        svc.RevokeWheaMemoryCollection(lease, timedOut: true);
        Assert.Same(expected, svc.WheaMemoryFacts);
        Assert.Throws<NotSupportedException>(() => ((IList<HardwareFact>)expected).Clear());
    }

    [Fact]
    public void SupersededLeaseCannotPublishSuccessFailureOrTimeout()
    {
        var svc = new EvidenceLabService();
        var old = svc.BeginWheaMemoryCollection(0, CancellationToken.None);
        svc.LoadWheaMemory(Records(2));
        var expected = svc.WheaMemoryFacts;
        svc.CollectWheaMemory(old, () => Records(1));
        svc.CollectWheaMemory(old, () => throw new IOException("old"));
        svc.RevokeWheaMemoryCollection(old, timedOut: true);
        Assert.Same(expected, svc.WheaMemoryFacts);
    }

    [Fact]
    public void CurrentSourceFailureHasReadErrorWithoutPrivateExceptionMessage()
    {
        var svc = new EvidenceLabService();
        var lease = svc.BeginWheaMemoryCollection(0, CancellationToken.None);
        svc.CollectWheaMemory(lease, () => throw new IOException("private path"));
        var failure = Assert.Single(svc.WheaMemoryFacts);
        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Null(failure.NumericValue);
        Assert.Contains("IOException", failure.UnavailableReason!);
        Assert.DoesNotContain("private path", failure.UnavailableReason!);
    }

    [Fact]
    public void CanceledLeaseCannotPublishOrNotifyAndDoesNotCancelInjectedSource()
    {
        var svc = new EvidenceLabService();
        svc.LoadWheaMemory(Records(2));
        var previous = svc.WheaMemoryFacts;
        using var cancellation = new CancellationTokenSource();
        var lease = svc.BeginWheaMemoryCollection(0, cancellation.Token);
        int notices = 0;
        bool sourceCompleted = false;
        svc.PropertyChanged += (_, _) => notices++;
        Assert.Throws<OperationCanceledException>(() => svc.CollectWheaMemory(lease, () =>
        {
            cancellation.Cancel();
            sourceCompleted = true;
            return Records(1);
        }));
        svc.RevokeWheaMemoryCollection(lease, timedOut: true);
        Assert.True(sourceCompleted);
        Assert.Same(previous, svc.WheaMemoryFacts);
        Assert.Equal(0, notices);
    }

    [Fact]
    public void MeasuredSourceFailureRecordsFailedStep()
    {
        var svc = new EvidenceLabService();
        EvidenceCollection.MeasureWheaMemory(svc, () => throw new IOException("injected"), 0);
        Assert.False(Assert.Single(svc.Steps).Succeeded);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(svc.WheaMemoryFacts).Availability);
    }

    [Fact]
    public void NotificationRunsOutsideGateAndMayStartNewGeneration()
    {
        var svc = new EvidenceLabService();
        bool reentered = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EvidenceLabService.WheaMemoryFacts) || reentered) return;
            reentered = true;
            svc.LoadWheaMemory(Records(2));
        };
        svc.LoadWheaMemory(Records(1));
        Assert.True(reentered);
        Assert.DoesNotContain(svc.WheaMemoryFacts, f => f.Availability == FactAvailability.ReadError);
    }
}
