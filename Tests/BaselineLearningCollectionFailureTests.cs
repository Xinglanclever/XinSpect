using System.IO;
using Xunit;

namespace XinSpect.Tests;

// All collectors are injected. No EventLog, history store, snapshot directory, hardware, or UI.
public sealed class BaselineLearningCollectionFailureTests
{
    private static readonly DateTimeOffset OldAt = new(2026, 11, 1, 1, 2, 3, TimeSpan.Zero);
    private static readonly DateTimeOffset AttemptedAt = new(2026, 11, 17, 4, 5, 6, TimeSpan.Zero);
    private static HardwareFact Fact(string key, double value, DateTimeOffset at, string unit = "") =>
        new(key, "test", key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), unit,
            "injected", FactTrustLevel.Reported, false, at, value);

    private static void Seed(EvidenceLabService svc)
        => svc.LoadBaselineLearningFromHistory(() => HistorySeries.Empty, OldAt,
            (_, _) => [Fact(BaselineLearningService.BaselineKey, 88, OldAt, "samples"),
                Fact(BaselineLearningService.ScoreKey, 9, OldAt, "sigma"),
                Fact(BaselineLearningService.EventsKey, 7, OldAt, "events")],
            _ => [Fact(FleetBaselineFactsService.SummaryKey, 5, OldAt),
                Fact(FleetBaselineFactsService.PeerPrefix + "0001", 21, OldAt)]);

    private static void Freeze(EvidenceLabService svc)
        => Assert.True(svc.CompleteKnowledgePackCollection(svc.BeginKnowledgePackCollection(),
            () => new KnowledgePackLoad(null, "injected missing pack", null)));

    [Fact]
    public void QueryFailureReplacesOldSuccessInNewSnapshotAndPreservesOldFrozenEvidence()
    {
        var svc = new EvidenceLabService();
        Seed(svc);
        Freeze(svc);
        var previous = svc.ComplianceInputSnapshot;
        int baselineCalls = 0, fleetCalls = 0;
        svc.LoadBaselineLearningFromHistory(() => throw new IOException("private history path"), AttemptedAt,
            (_, _) => { baselineCalls++; return []; },
            _ => { fleetCalls++; return [Fact(FleetBaselineFactsService.SummaryKey, 10, AttemptedAt)]; });
        Freeze(svc);
        Assert.Equal(0, baselineCalls);
        Assert.Equal(1, fleetCalls);
        Assert.Equal(3, svc.BaselineFacts.Count);
        foreach (var key in new[] { BaselineLearningService.BaselineKey, BaselineLearningService.ScoreKey,
                     BaselineLearningService.EventsKey })
        {
            var current = Assert.Single(svc.ComplianceInputSnapshot, f => f.Key == key);
            Assert.Equal(FactAvailability.ReadError, current.Availability);
            Assert.Equal(AttemptedAt, current.MeasuredAtUtc);
            Assert.Equal("", current.Value);
            Assert.Null(current.NumericValue);
            Assert.DoesNotContain("private history path", current.UnavailableReason!);
            var old = Assert.Single(previous, f => f.Key == key);
            Assert.Equal(FactAvailability.Present, old.Availability);
            Assert.Equal(OldAt, old.MeasuredAtUtc);
            Assert.NotNull(old.NumericValue);
        }
        Assert.DoesNotContain(svc.FleetFacts, f => f.Key.StartsWith(FleetBaselineFactsService.PeerPrefix));
        Assert.Equal(10d, Assert.Single(svc.FleetFacts).NumericValue);
    }

    [Fact]
    public void ProjectionFailureAlsoReplacesPriorBaselineAndStillCollectsFleet()
    {
        var svc = new EvidenceLabService();
        Seed(svc);
        svc.LoadBaselineLearningFromHistory(() => HistorySeries.Empty, AttemptedAt,
            (_, _) => throw new InvalidDataException("private projection detail"),
            _ => [Fact(FleetBaselineFactsService.SummaryKey, 2, AttemptedAt)]);
        Assert.All(svc.BaselineFacts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Equal(AttemptedAt, f.MeasuredAtUtc);
            Assert.DoesNotContain("private projection detail", f.UnavailableReason!);
        });
        Assert.Equal(2d, Assert.Single(svc.FleetFacts).NumericValue);
    }

    [Fact]
    public void EmptyHistoryKeepsExistingNoDataSemanticsInsteadOfReadError()
    {
        var svc = new EvidenceLabService();
        Seed(svc);
        svc.LoadBaselineLearningFromHistory(() => HistorySeries.Empty, AttemptedAt,
            (at, series) => BaselineLearningService.Collect(at, series, () => []),
            at => FleetBaselineFactsService.Collect([], at));
        Assert.Equal(FactAvailability.NotSupported, Assert.Single(svc.BaselineFacts,
            f => f.Key == BaselineLearningService.BaselineKey).Availability);
        Assert.DoesNotContain(svc.BaselineFacts, f => f.Availability == FactAvailability.ReadError);
        Assert.Equal(FactAvailability.NotApplicable, Assert.Single(svc.FleetFacts,
            f => f.Key == FleetBaselineFactsService.SummaryKey).Availability);
    }

    [Fact]
    public void SuccessfulFactsRetainOriginalSourceTimestampUnitsAndImmutableOwnership()
    {
        var svc = new EvidenceLabService();
        var original = Fact(BaselineLearningService.BaselineKey, 11, OldAt, "original-unit");
        var baseline = new List<HardwareFact> { original };
        var fleet = new List<HardwareFact> { Fact(FleetBaselineFactsService.SummaryKey, 2, OldAt) };
        svc.LoadBaselineLearningFromHistory(() => HistorySeries.Empty, AttemptedAt,
            (_, _) => baseline, _ => fleet);
        baseline.Clear();
        fleet.Clear();
        Assert.Same(original, Assert.Single(svc.BaselineFacts));
        Assert.Equal(OldAt, original.MeasuredAtUtc);
        Assert.Equal("original-unit", original.Unit);
        Assert.Equal(OldAt, Assert.Single(svc.FleetFacts).MeasuredAtUtc);
        Assert.Throws<NotSupportedException>(() => ((IList<HardwareFact>)svc.BaselineFacts).Clear());
    }

    [Fact]
    public void FleetFailureRemovesOldPeersAndNotifiesCurrentBaseline()
    {
        var svc = new EvidenceLabService();
        Seed(svc);
        var notified = new List<string?>();
        svc.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        svc.LoadBaselineLearningFromHistory(() => HistorySeries.Empty, AttemptedAt,
            (_, _) => [Fact(BaselineLearningService.BaselineKey, 12, AttemptedAt)],
            _ => throw new IOException("private fleet path"));
        var fleet = Assert.Single(svc.FleetFacts);
        Assert.Equal(FleetBaselineFactsService.SummaryKey, fleet.Key);
        Assert.Equal(FactAvailability.ReadError, fleet.Availability);
        Assert.Null(fleet.NumericValue);
        Assert.DoesNotContain("private fleet path", fleet.UnavailableReason!);
        Assert.Equal(12d, Assert.Single(svc.BaselineFacts).NumericValue);
        Assert.Contains(nameof(EvidenceLabService.BaselineFacts), notified);
        Assert.Contains(nameof(EvidenceLabService.FleetFacts), notified);
        Assert.Contains(nameof(EvidenceLabService.AllFacts), notified);
        Assert.Contains(nameof(EvidenceLabService.FirmwareSecurityRows), notified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CancellationDoesNotReplaceOrRetimestampPublishedFacts(int phase)
    {
        var svc = new EvidenceLabService();
        Seed(svc);
        var baseline = svc.BaselineFacts;
        var fleet = svc.FleetFacts;
        int publications = 0;
        svc.PropertyChanged += (_, _) => publications++;
        using var cancellation = new CancellationTokenSource();
        if (phase == 0) cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => svc.LoadBaselineLearningFromHistory(() =>
        {
            if (phase == 1) cancellation.Cancel();
            return HistorySeries.Empty;
        }, AttemptedAt, (_, _) => [Fact(BaselineLearningService.BaselineKey, 13, AttemptedAt)], _ =>
        {
            if (phase == 2) cancellation.Cancel();
            return [Fact(FleetBaselineFactsService.SummaryKey, 3, AttemptedAt)];
        }, cancellation.Token));
        Assert.Same(baseline, svc.BaselineFacts);
        Assert.Same(fleet, svc.FleetFacts);
        Assert.Equal(OldAt, svc.BaselineFacts[0].MeasuredAtUtc);
        Assert.Equal(0, publications);
    }

    [Fact]
    public void NullHistoryAndUnrelatedCancellationExceptionAreReadFailures()
    {
        var svc = new EvidenceLabService();
        Seed(svc);
        svc.LoadBaselineLearningFromHistory(() => null!, AttemptedAt,
            (_, _) => throw new Exception("must not run"), _ => []);
        Assert.Equal(3, svc.BaselineFacts.Count);
        Assert.All(svc.BaselineFacts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        svc.LoadBaselineLearningFromHistory(() => throw new OperationCanceledException(), AttemptedAt,
            (_, _) => [], _ => []);
        Assert.All(svc.BaselineFacts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }
}
