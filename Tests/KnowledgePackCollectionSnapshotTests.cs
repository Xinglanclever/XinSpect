using System.Collections.ObjectModel;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

// All sources and package loads below are injected. No WMI, hardware, or UI dispatch.
public sealed class KnowledgePackCollectionSnapshotTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly StorageReliabilityRaw Blank = new(
        DeviceId: 0, Wear: null, Temperature: null, TemperatureMax: null, PowerOnHours: null,
        StartStopCycleCount: null, StartStopCycleCountMax: null, LoadUnloadCycleCount: null,
        LoadUnloadCycleCountMax: null, ReadErrorsTotal: null, ReadErrorsCorrected: null,
        ReadErrorsUncorrected: null, WriteErrorsTotal: null, WriteErrorsCorrected: null,
        WriteErrorsUncorrected: null, ReadLatencyMax: null, WriteLatencyMax: null,
        FlushLatencyMax: null, ManufactureDate: null);

    private sealed class StorageSource(uint wear) : IStorageReliabilitySource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<StorageReliabilityRaw> Read() => [Blank with { Wear = wear }];
    }

    private static KnowledgePackLoad Package(Dictionary<string, string>? entries = null)
    {
        var pack = KnowledgePackService.Build(1, "2026-11-17",
            [new KnowledgeTable(DeviceComplianceFactsService.TableName, "2026-11-17",
                entries ?? new() { ["private-control"] = "storage.reliability.0.wear" })]);
        return new(pack, null, pack.SerialNumber);
    }

    private static HardwareFact Fact(string key, string? unit = "") =>
        new(key, "test", "test", "1", unit!, "injected", FactTrustLevel.Reported, false, At, 1);

    [Fact]
    public void FinalPhaseSeesLastSourceAndRunsOnceAfterOtherSteps()
    {
        var svc = new EvidenceLabService();
        bool otherFinished = false;
        int loads = 0;
        EvidenceCollection.LoadUsermodeFacts(svc, new StorageSource(1), other =>
        {
            other.LoadStorageReliability(new StorageSource(23));
            other.RecordStep("late injected source", 1, true);
            otherFinished = true;
        }, () =>
        {
            Assert.True(otherFinished);
            loads++;
            return Package();
        });
        Assert.Equal(1, loads);
        Assert.Equal("LoadKnowledgePack", svc.Steps.Last().Name);
        Assert.Equal("23", Assert.Single(svc.ComplianceInputSnapshot,
            f => f.Key == "storage.reliability.0.wear").Value);
        Assert.DoesNotContain(svc.ComplianceInputSnapshot,
            f => f.Key.StartsWith("zr.compliance.", StringComparison.Ordinal));
        Assert.Equal(FactAvailability.NotSupported, Assert.Single(svc.ComplianceFacts,
            f => f.Key == DeviceComplianceFactsService.BaselineKey).Availability);
    }

    [Fact]
    public void RepeatedProjectionsReuseOneDeepFrozenPackageLoad()
    {
        var svc = new EvidenceLabService();
        var entries = new Dictionary<string, string> { ["private-control"] = "storage.reliability.0.wear" };
        var tables = new List<KnowledgeTable>
        {
            new(DeviceComplianceFactsService.TableName, "2026-11-17", entries)
        };
        var pack = KnowledgePackService.Build(1, "2026-11-17", tables);
        var loaded = new KnowledgePackLoad(pack, null, pack.SerialNumber);
        int loads = 0;
        long generation = svc.BeginKnowledgePackCollection();
        Assert.True(svc.CompleteKnowledgePackCollection(generation, () => { loads++; return loaded; }));
        var input = svc.ComplianceInputSnapshot;
        var compliance = svc.ComplianceFacts;
        entries["private-control"] = "zr.compliance.baseline";
        entries["extra"] = "absent";
        tables.Clear();
        for (int i = 0; i < 3; i++)
        {
            var facts = svc.AllFacts;
            _ = svc.FirmwareSecurityRows;
            Assert.Contains(facts, f => f.Key == DeviceComplianceFactsService.TableKey && f.NumericValue == 1);
            var digest = Assert.Single(facts, f => f.Key == KnowledgePackReport.DigestKey);
            Assert.Equal(FactAvailability.Present, digest.Availability);
            Assert.StartsWith(pack.SerialNumber, digest.Value);
        }
        Assert.Equal(1, loads);
        Assert.Same(input, svc.ComplianceInputSnapshot);
        Assert.Same(compliance, svc.ComplianceFacts);
        Assert.Throws<NotSupportedException>(() => ((IList<HardwareFact>)input).Clear());
    }

    [Fact]
    public void FrozenInputExcludesSelfReferencesPreservesUnitsAndRejectsDuplicateIndexKeys()
    {
        var facts = new List<HardwareFact>
        {
            Fact("zr.compliance.baseline"), Fact("zr.compliance.future"),
            Fact("duplicate"), Fact("duplicate", "%"), Fact("null.unit", null), Fact("unit", "MiB")
        };
        var frozen = EvidenceLabService.FreezeComplianceInput(facts);
        facts.Clear();
        Assert.Equal(4, frozen.Count);
        var index = DeviceComplianceFactsService.IndexUnambiguousFacts(frozen);
        Assert.False(index.ContainsKey("duplicate"));
        Assert.Null(index["null.unit"].Unit);
        Assert.Equal("MiB", index["unit"].Unit);
        Assert.DoesNotContain(frozen, f => f.Key.StartsWith("zr.compliance.", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => ((IList<HardwareFact>)frozen).Add(Fact("new")));
    }

    [Fact]
    public void BeginningNextRoundImmediatelyClearsPriorPublishedEvaluation()
    {
        var svc = new EvidenceLabService();
        long first = svc.BeginKnowledgePackCollection();
        Assert.True(svc.CompleteKnowledgePackCollection(first, () => Package()));
        Assert.NotEmpty(svc.ComplianceFacts);
        long second = svc.BeginKnowledgePackCollection();
        Assert.Empty(svc.KnowledgePackFacts);
        Assert.Empty(svc.ComplianceFacts);
        Assert.Empty(svc.ComplianceInputSnapshot);
        Assert.True(svc.CompleteKnowledgePackCollection(second, () => Package()));
        Assert.DoesNotContain(svc.ComplianceInputSnapshot,
            f => f.Key.StartsWith("zr.compliance.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LateOlderFinalizationCannotReplaceNewRound()
    {
        var svc = new EvidenceLabService();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        long first = svc.BeginKnowledgePackCollection();
        Task<bool> old = Task.Run(() => svc.CompleteKnowledgePackCollection(first, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Package();
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            long second = svc.BeginKnowledgePackCollection();
            Assert.True(svc.CompleteKnowledgePackCollection(second, () => new(null, "missing", null)));
            var expected = svc.ComplianceFacts;
            release.Set();
            Assert.False(await old);
            Assert.Same(expected, svc.ComplianceFacts);
            Assert.All(svc.ComplianceFacts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        }
        finally
        {
            release.Set();
            await old;
        }
    }

    [Fact]
    public void SameGenerationIsFinalizedAtMostOnce()
    {
        var svc = new EvidenceLabService();
        long generation = svc.BeginKnowledgePackCollection();
        int calls = 0;
        KnowledgePackLoad Load() { calls++; return Package(); }
        Assert.True(svc.CompleteKnowledgePackCollection(generation, Load));
        Assert.False(svc.CompleteKnowledgePackCollection(generation, Load));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void LoadFailurePublishesReadErrorWithoutPreviousRoundResidue()
    {
        var svc = new EvidenceLabService();
        Assert.True(svc.CompleteKnowledgePackCollection(svc.BeginKnowledgePackCollection(), () => Package()));
        Assert.True(svc.CompleteKnowledgePackCollection(svc.BeginKnowledgePackCollection(),
            () => throw new IOException("private path must not escape")));
        Assert.Empty(svc.KnowledgePackFacts);
        Assert.Empty(svc.ComplianceInputSnapshot);
        Assert.Equal(2, svc.ComplianceFacts.Count);
        Assert.All(svc.ComplianceFacts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.DoesNotContain("private path", f.UnavailableReason!);
        });
    }

    [Fact]
    public async Task StepsReadersGetStableCopiesDuringConcurrentWrites()
    {
        var svc = new EvidenceLabService();
        svc.RecordStep("original", 1, true);
        var before = svc.Steps;
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                svc.RecordStep("injected", i, true);
                if (i % 7 == 0) svc.ClearSteps();
            }
        });
        for (int i = 0; i < 100; i++)
            _ = MeasurementCostFactsService.Collect(svc.Steps, At);
        await writer;
        Assert.Equal("original", Assert.Single(before).Name);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceLabService.CollectionStep>)before).Clear());
    }

    [Fact]
    public void DeferredPhaseIncludesExtraCommittedFactsAndSnapshotSurvivesLaterWrites()
    {
        var svc = new EvidenceLabService();
        long generation = EvidenceCollection.LoadUsermodeFactsDeferred(svc, new StorageSource(1), _ => { });
        Assert.Empty(svc.ComplianceFacts);
        Assert.Empty(svc.KnowledgePackFacts);
        svc.LoadStorageReliability(new StorageSource(42)); // models a GUI-only later source commit
        EvidenceCollection.CompleteUsermodeFacts(svc, generation, () => Package());
        var frozen = svc.ComplianceInputSnapshot;
        svc.LoadStorageReliability(new StorageSource(99)); // an existing late producer can still commit
        Assert.Equal("42", Assert.Single(frozen, f => f.Key == "storage.reliability.0.wear").Value);
        Assert.Equal("99", Assert.Single(svc.AllFacts, f => f.Key == "storage.reliability.0.wear").Value);
    }

    [Fact]
    public void CancellationDuringLoadDoesNotPublishAnEvaluation()
    {
        var svc = new EvidenceLabService();
        using var cancellation = new CancellationTokenSource();
        long generation = svc.BeginKnowledgePackCollection();
        Assert.Throws<OperationCanceledException>(() => svc.CompleteKnowledgePackCollection(generation, () =>
        {
            cancellation.Cancel();
            return Package();
        }, cancellation.Token));
        Assert.Empty(svc.ComplianceFacts);
        Assert.Empty(svc.ComplianceInputSnapshot);
    }
}
