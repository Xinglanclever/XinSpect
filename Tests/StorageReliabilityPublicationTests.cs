using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

// Injected records only. No EventLog, firmware, hardware, WMI or UI execution.
public sealed class StorageReliabilityPublicationTests
{
    private static readonly StorageReliabilityRaw Blank = new(
        DeviceId: 0, Wear: null, Temperature: null, TemperatureMax: null, PowerOnHours: null,
        StartStopCycleCount: null, StartStopCycleCountMax: null, LoadUnloadCycleCount: null,
        LoadUnloadCycleCountMax: null, ReadErrorsTotal: null, ReadErrorsCorrected: null,
        ReadErrorsUncorrected: null, WriteErrorsTotal: null, WriteErrorsCorrected: null,
        WriteErrorsUncorrected: null, ReadLatencyMax: null, WriteLatencyMax: null,
        FlushLatencyMax: null, ManufactureDate: null);

    private sealed class InjectedSource(Func<IReadOnlyList<StorageReliabilityRaw>> read) : IStorageReliabilitySource
    {
        public bool Available { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public IReadOnlyList<StorageReliabilityRaw> Read() => read();
    }
    private static InjectedSource Source(long wear) => new(() => [Blank with { Wear = wear }]);

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
            EvidenceCollection.MeasureStorageReliability(svc, new InjectedSource(() =>
            {
                sourceWorker = Thread.CurrentThread;
                entered.Set();
                try
                {
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test release");
                    if (failLate) throw new IOException("private old failure");
                    return [Blank with { Wear = 11 }];
                }
                finally { cleaned.Set(); }
            }), 100);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(Assert.Single(svc.Steps).Succeeded);
            var timeout = Assert.Single(svc.StorageReliabilityFacts);
            Assert.Equal(FactAvailability.ReadError, timeout.Availability);
            Assert.Contains("逾時", timeout.UnavailableReason!);
            Assert.False(cleaned.IsSet); // outer timeout did not stop source or run cleanup early
            svc.LoadStorageReliability(Source(22));
            var current = svc.StorageReliabilityFacts;
            int lateNotifications = 0;
            svc.PropertyChanged += (_, _) => lateNotifications++;
            release.Set();
            Assert.True(cleaned.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(sourceWorker);
            Assert.True(sourceWorker!.Join(TimeSpan.FromSeconds(5)));
            Assert.Same(current, svc.StorageReliabilityFacts);
            Assert.Equal("22", Assert.Single(current, f => f.Key == "storage.reliability.0.wear").Value);
            Assert.Equal(0, lateNotifications);
            Assert.DoesNotContain(svc.StorageReliabilityFacts, f => f.Availability == FactAvailability.ReadError);
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
        svc.LoadStorageReliability(Source(22));
        var lease = svc.BeginStorageReliabilityCollection(1, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetElapsedTime(lease.Started).TotalMilliseconds >= 2,
            TimeSpan.FromSeconds(5)));
        Assert.False(svc.CollectStorageReliability(lease, Source(11)));
        var timeout = Assert.Single(svc.StorageReliabilityFacts);
        Assert.Equal(FactAvailability.ReadError, timeout.Availability);
        Assert.Contains("逾時", timeout.UnavailableReason!);
        var committed = svc.StorageReliabilityFacts;
        svc.RevokeStorageReliabilityCollection(lease, timedOut: true);
        Assert.Same(committed, svc.StorageReliabilityFacts);
    }

    [Fact]
    public void CompletedLeaseCannotBeOverwrittenByDelayedTimeoutCallback()
    {
        var svc = new EvidenceLabService();
        var lease = svc.BeginStorageReliabilityCollection(0, CancellationToken.None);
        Assert.True(svc.CollectStorageReliability(lease, Source(22)));
        var expected = svc.StorageReliabilityFacts;
        svc.RevokeStorageReliabilityCollection(lease, timedOut: true);
        Assert.Same(expected, svc.StorageReliabilityFacts);
        Assert.Throws<NotSupportedException>(() => ((IList<HardwareFact>)expected).Clear());
    }

    [Fact]
    public void SupersededLeaseCannotPublishSuccessFailureOrTimeout()
    {
        var svc = new EvidenceLabService();
        var old = svc.BeginStorageReliabilityCollection(0, CancellationToken.None);
        svc.LoadStorageReliability(Source(22));
        var expected = svc.StorageReliabilityFacts;
        svc.CollectStorageReliability(old, Source(11));
        svc.CollectStorageReliability(old, new InjectedSource(() => throw new IOException("old")));
        svc.RevokeStorageReliabilityCollection(old, timedOut: true);
        Assert.Same(expected, svc.StorageReliabilityFacts);
    }

    [Fact]
    public void CurrentSourceFailureRetainsExistingReadErrorContract()
    {
        var svc = new EvidenceLabService();
        var lease = svc.BeginStorageReliabilityCollection(0, CancellationToken.None);
        svc.CollectStorageReliability(lease, new InjectedSource(() => throw new IOException("injected read failure")));
        var failure = Assert.Single(svc.StorageReliabilityFacts);
        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Null(failure.NumericValue);
        Assert.Contains("IOException", failure.UnavailableReason!);
        Assert.Contains("injected read failure", failure.UnavailableReason!); // existing service diagnostic contract retained
    }

    [Fact]
    public void CanceledLeaseCannotPublishOrNotifyAndDoesNotCancelInjectedSource()
    {
        var svc = new EvidenceLabService();
        svc.LoadStorageReliability(Source(22));
        var previous = svc.StorageReliabilityFacts;
        using var cancellation = new CancellationTokenSource();
        var lease = svc.BeginStorageReliabilityCollection(0, cancellation.Token);
        int notices = 0;
        bool sourceCompleted = false;
        svc.PropertyChanged += (_, _) => notices++;
        Assert.Throws<OperationCanceledException>(() => svc.CollectStorageReliability(lease, new InjectedSource(() =>
        {
            cancellation.Cancel();
            sourceCompleted = true;
            return [Blank with { Wear = 11 }];
        })));
        svc.RevokeStorageReliabilityCollection(lease, timedOut: true);
        Assert.True(sourceCompleted);
        Assert.Same(previous, svc.StorageReliabilityFacts);
        Assert.Equal(0, notices);
    }

    [Fact]
    public void MeasuredSourceFailureRecordsFailedStep()
    {
        var svc = new EvidenceLabService();
        EvidenceCollection.MeasureStorageReliability(svc, new InjectedSource(() => throw new IOException("injected")), 0);
        Assert.False(Assert.Single(svc.Steps).Succeeded);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(svc.StorageReliabilityFacts).Availability);
    }

    [Fact]
    public void NotificationRunsOutsideGateAndMayStartNewGeneration()
    {
        var svc = new EvidenceLabService();
        bool reentered = false;
        svc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EvidenceLabService.StorageReliabilityFacts) || reentered) return;
            reentered = true;
            svc.LoadStorageReliability(Source(22));
        };
        svc.LoadStorageReliability(Source(11));
        Assert.True(reentered);
        Assert.DoesNotContain(svc.StorageReliabilityFacts, f => f.Availability == FactAvailability.ReadError);
    }
    [Fact]
    public void DelayedProviderAvailabilityFailureIsPublishedAndMeasuredFailed()
    {
        InjectedSource? source = null;
        source = new InjectedSource(() =>
        {
            source!.Available = false;
            source.UnavailableReason = "injected provider unavailable after read";
            return [];
        });
        var svc = new EvidenceLabService();
        EvidenceCollection.MeasureStorageReliability(svc, source, 0);
        var failure = Assert.Single(svc.StorageReliabilityFacts);
        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.Equal("injected provider unavailable after read", failure.UnavailableReason);
        Assert.False(Assert.Single(svc.Steps).Succeeded);
    }

    [Fact]
    public void SuccessfulEmptyProviderRetainsNotApplicableRatherThanFailure()
    {
        var svc = new EvidenceLabService();
        EvidenceCollection.MeasureStorageReliability(svc, new InjectedSource(() => []), 0);
        Assert.Equal(FactAvailability.NotApplicable, Assert.Single(svc.StorageReliabilityFacts).Availability);
        Assert.True(Assert.Single(svc.Steps).Succeeded);
    }

}
