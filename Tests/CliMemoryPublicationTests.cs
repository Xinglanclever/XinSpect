using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

// Offline source operations only. Exercises the shared real headless composition, not injected AllFacts.
public sealed class CliMemoryPublicationTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    private static PhysicalMemoryRawRow Row(int? configured = 4400, int nominal = 5600,
        string manufacturer = "fixture vendor", int type = 26)
        => new(field => field switch
        {
            "Manufacturer" => manufacturer,
            "Speed" => nominal,
            "ConfiguredClockSpeed" => configured,
            "SMBIOSMemoryType" => type,
            "Capacity" => 8UL * 1024 * 1024 * 1024,
            _ => throw new InvalidOperationException("unexpected fixture column"),
        });

    private static PhysicalMemorySnapshot Read(params PhysicalMemoryRawRow[] rows)
        => PhysicalMemorySnapshotSource.ReadRows(() => rows);

    private static IReadOnlyList<HardwareFact> Governance(DateTimeOffset at,
        PlatformGovernanceFactsService.MixMatchState summary)
        => [PlatformGovernanceFactsService.MixMatchFact(at, () => summary)];

    private static PlatformGovernanceFactsService.PublicationBatch Batch(int rate)
        => PlatformGovernanceFactsService.CollectPublicationBatch(At, () => Read(Row(rate)), Governance);

    private static HardwareFact Rate(IEnumerable<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == "memory.rate");

    private static void Configured(HardwareFact rate, int expected = 4400)
    {
        Assert.Equal(FactAvailability.Present, rate.Availability);
        Assert.Equal(FactTrustLevel.Reported, rate.Trust);
        Assert.Equal("Windows 設定速率（WMI）", rate.Source);
        Assert.Equal("設定資料速率（WMI 回報）", rate.Name);
        Assert.Contains("-" + expected + "（WMI 設定值）", rate.Value);
        Assert.Null(rate.NumericValue); // R380 text projection does not invent a numeric measurement.
    }

    private sealed class NoStorage : IStorageReliabilitySource
    {
        public bool Available => false;
        public string? UnavailableReason => "offline fixture: no storage provider";
        public IReadOnlyList<StorageReliabilityRaw> Read() => throw new InvalidOperationException("must not read storage");
    }

    private static IReadOnlyList<HardwareFact> Collect(Func<PhysicalMemorySnapshot> read,
        Action<string>? stage = null)
        => EvidenceCollection.CollectForCli(lab => stage?.Invoke("driver"), new NoStorage(),
            lab => stage?.Invoke("other"), () => { stage?.Invoke("memory"); return read(); },
            (at, summary) => { stage?.Invoke("governance"); return Governance(at, summary); });

    [Fact]
    public void SharedBatchReadsOnceAndKeepsNominalSummarySeparateFromConfiguredRate()
    {
        int reads = 0;
        var batch = PlatformGovernanceFactsService.CollectPublicationBatch(At,
            () => { reads++; return Read(Row(4400, 4800, "A"), Row(4400, 5600, "B")); }, Governance);
        Assert.Equal(1, reads);
        Assert.True(batch.MemoryReadComplete);
        var summary = Assert.Single(batch.GovernanceFacts);
        Assert.Contains("模組 2、製造商 2 家", summary.Value);
        Assert.Contains("速度跨度 800", summary.Value);
        Configured(Rate(batch.MemoryTimingFacts));
        Assert.DoesNotContain(batch.MemoryTimingFacts, f => f.Key == "memory.current");
        var reversed = PhysicalMemorySnapshotSource.ProjectConfiguredRate(Read(Row(4400, 5600, "B"), Row(4400, 4800, "A")), At);
        Assert.Equal(Rate(batch.MemoryTimingFacts), Rate(reversed));
    }

    [Theory]
    [InlineData(null, 4400)]
    [InlineData(0, 4400)]
    [InlineData(-1, 4400)]
    [InlineData(3200, 4400)]
    public void MissingNonpositiveOrMixedConfiguredRatesNeverFallBackToNominalSpeed(int? first, int second)
    {
        var snapshot = Read(Row(first, 5600), Row(second, 5600));
        Assert.True(snapshot.Complete);
        var rate = Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(snapshot, At));
        Assert.Equal(FactAvailability.Unknown, rate.Availability);
        Assert.Null(rate.NumericValue);
        Assert.Equal("", rate.Value);
        Assert.Contains("標稱 Speed", rate.UnavailableReason);
    }

    [Fact]
    public void EmptyCompleteEnumerationDoesNotClaimAbsentHardwareOrCurrentTiming()
    {
        var facts = PhysicalMemorySnapshotSource.ProjectConfiguredRate(Read(), At);
        var rate = Assert.Single(facts);
        Assert.Equal("memory.rate", rate.Key);
        Assert.Equal(FactAvailability.Unknown, rate.Availability);
        Assert.Contains("未推定沒有記憶體", rate.UnavailableReason);
    }

    [Theory]
    [InlineData(false, FactAvailability.ReadError)]
    [InlineData(true, FactAvailability.InsufficientPrivilege)]
    public void SourceFailureBeforeFirstRowPublishesExplicitSanitizedUnavailability(bool denied, FactAvailability expected)
    {
        var snapshot = PhysicalMemorySnapshotSource.ReadRows(() => denied
            ? throw new UnauthorizedAccessException("SECRET-USER-PATH")
            : throw new IOException("SECRET-USER-PATH"));
        Assert.False(snapshot.Complete);
        Assert.Empty(snapshot.Modules);
        Assert.Equal(new PlatformGovernanceFactsService.MixMatchState(0, 0, 0, 0), snapshot.LegacySummary);
        var rate = Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(snapshot, At));
        Assert.Equal(expected, rate.Availability);
        Assert.Equal(FactTrustLevel.Unknown, rate.Trust);
        Assert.DoesNotContain("SECRET-USER-PATH", rate.UnavailableReason);
    }

    [Fact]
    public void UnknownMemoryTypeRetainsConfiguredRateWithoutInventingDdrType()
    {
        var rate = Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(Read(Row(type: 0)), At));
        Configured(rate);
        Assert.StartsWith("記憶體-4400", rate.Value);
        Assert.DoesNotContain("DDR", rate.Value);
    }

    private static IEnumerable<PhysicalMemoryRawRow> Partial(bool denied, Action disposed)
    {
        try
        {
            yield return Row();
            if (denied) throw new UnauthorizedAccessException("SECRET-ERROR");
            throw new IOException("SECRET-ERROR");
        }
        finally { disposed(); }
    }

    [Theory]
    [InlineData(false, FactAvailability.ReadError)]
    [InlineData(true, FactAvailability.InsufficientPrivilege)]
    public void PartialEnumerationKeepsOldSummaryCountersButCannotPublishPresentRate(bool denied, FactAvailability expected)
    {
        int disposals = 0;
        var snapshot = PhysicalMemorySnapshotSource.ReadRows(() => Partial(denied, () => disposals++));
        Assert.False(snapshot.Complete);
        Assert.Equal(1, disposals);
        Assert.Equal(new PlatformGovernanceFactsService.MixMatchState(1, 1, 0, 0), snapshot.LegacySummary);
        Assert.Single(snapshot.Modules); // A tempting uniform partial subset must still be rejected.
        var rate = Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(snapshot, At));
        Assert.Equal(expected, rate.Availability);
        Assert.Equal("", rate.Value);
        Assert.Null(rate.NumericValue);
        Assert.DoesNotContain("SECRET-ERROR", rate.UnavailableReason);
    }

    [Fact]
    public void NewFieldFailureDoesNotChangeOldNominalSummaryOrCertifyPartialModuleSet()
    {
        var bad = Row(4400, 4800, "A");
        var fieldFailure = new PhysicalMemoryRawRow(field => field == "ConfiguredClockSpeed"
            ? throw new FormatException("SECRET-FIELD") : bad.Read(field));
        var snapshot = Read(fieldFailure, Row(4400, 5600, "B"));
        Assert.Equal(new PlatformGovernanceFactsService.MixMatchState(2, 2, 0, 800), snapshot.LegacySummary);
        Assert.False(snapshot.Complete);
        var rate = Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(snapshot, At));
        Assert.Equal(FactAvailability.ReadError, rate.Availability);
        Assert.DoesNotContain("SECRET-FIELD", rate.UnavailableReason);
    }

    [Fact]
    public void NominalFieldFailureRetainsOriginalIncrementAndManufacturerUpdateOrder()
    {
        var bad = new PhysicalMemoryRawRow(field => field switch
        {
            "Manufacturer" => "A",
            "Speed" => "invalid nominal speed",
            _ => throw new InvalidOperationException("must not consume new fields after old field failure"),
        });
        var snapshot = Read(bad, Row());
        Assert.False(snapshot.Complete);
        Assert.Equal(new PlatformGovernanceFactsService.MixMatchState(1, 1, 0, 0), snapshot.LegacySummary);
        Assert.Empty(snapshot.Modules);
    }

    [Fact]
    public void RowCapIsIncompleteInsteadOfCertifyingTheRetainedUniformSubset()
    {
        var snapshot = PhysicalMemorySnapshotSource.ReadRows(
            () => Enumerable.Repeat(Row(), PhysicalMemorySnapshotSource.MaxRows + 1));
        Assert.Equal(PhysicalMemorySnapshotSource.MaxRows, snapshot.Modules.Count);
        Assert.False(snapshot.Complete);
        Assert.Equal(FactAvailability.ReadError, Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(snapshot, At)).Availability);
    }

    [Fact]
    public void RefreshRemovesOldPresentBeforeNewSourceCompletesAndFailureDoesNotRestoreIt()
    {
        var lab = new EvidenceLabService();
        var first = lab.BeginPlatformGovernanceCollection(0, CancellationToken.None);
        Assert.True(lab.CollectPlatformGovernance(first, () => Batch(4400)));
        Configured(Rate(lab.AllFacts));
        var next = lab.BeginPlatformGovernanceCollection(0, CancellationToken.None);
        Assert.Empty(lab.PlatformGovernanceFacts);
        Assert.Equal(FactAvailability.Unknown, Rate(lab.AllFacts).Availability);
        Assert.Equal("", Rate(lab.AllFacts).Value);
        Assert.False(lab.CollectPlatformGovernance(next, () => throw new IOException("SECRET-PATH")));
        var failure = Rate(lab.AllFacts);
        Assert.Equal(FactAvailability.ReadError, failure.Availability);
        Assert.DoesNotContain("SECRET-PATH", failure.UnavailableReason);
        Assert.Empty(lab.PlatformGovernanceFacts);
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }

    [Fact]
    public void NewRefreshEntryRejectsOldPlatformCompletionWhileNewOtherSourceIsBlocked()
    {
        using var oldEntered = new ManualResetEventSlim(); using var oldRelease = new ManualResetEventSlim();
        using var nextEntered = new ManualResetEventSlim(); using var nextRelease = new ManualResetEventSlim();
        var lab = new EvidenceLabService();
        Task? old = null; Task? next = null;
        try
        {
            old = Task.Run(() => EvidenceCollection.LoadUsermodeFactsWithPlatform(lab, new NoStorage(), _ => { }, () =>
            {
                oldEntered.Set();
                if (!oldRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("old fixture release");
                return Read(Row(4400));
            }, Governance, 0));
            Assert.True(oldEntered.Wait(TimeSpan.FromSeconds(5)));
            next = Task.Run(() => EvidenceCollection.LoadUsermodeFactsWithPlatform(lab, new NoStorage(), _ =>
            {
                nextEntered.Set();
                if (!nextRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("next fixture release");
            }, () => Read(Row(3200)), Governance, 0));
            Assert.True(nextEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(FactAvailability.Unknown, Rate(lab.AllFacts).Availability);
            oldRelease.Set();
            Assert.True(old.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(FactAvailability.Unknown, Rate(lab.AllFacts).Availability);
            Assert.Empty(lab.PlatformGovernanceFacts);
            nextRelease.Set();
            Assert.True(next.Wait(TimeSpan.FromSeconds(5)));
            Configured(Rate(lab.AllFacts), 3200);
        }
        finally
        {
            oldRelease.Set(); nextRelease.Set();
            if (old is not null) Assert.True(old.Wait(TimeSpan.FromSeconds(5)));
            if (next is not null) Assert.True(next.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void OldRefreshReachingPlatformLateCannotAllocateNewGenerationOrInvokeItsSource()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var lab = new EvidenceLabService();
        int staleReads = 0;
        Task? old = null;
        try
        {
            old = Task.Run(() => EvidenceCollection.LoadUsermodeFactsWithPlatform(lab, new NoStorage(), _ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture release");
            }, () => { Interlocked.Increment(ref staleReads); return Read(Row(4400)); }, Governance, 0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            EvidenceCollection.LoadUsermodeFactsWithPlatform(lab, new NoStorage(), _ => { },
                () => Read(Row(3200)), Governance, 0);
            var expected = Rate(lab.AllFacts);
            Configured(expected, 3200);
            release.Set();
            Assert.True(old.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, staleReads);
            Assert.Equal(expected, Rate(lab.AllFacts));
        }
        finally
        {
            release.Set();
            if (old is not null) Assert.True(old.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void RefreshDelayDoesNotSpendThePlatformStepsOwnBudgetAndLeaseCannotBeReused()
    {
        var clock = new Clock();
        var lab = new EvidenceLabService();
        var lease = lab.BeginPlatformGovernanceCollection(10, CancellationToken.None, clock);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(lab.CollectPlatformGovernance(lease, () => Batch(4400)));
        Assert.False(lab.CollectPlatformGovernance(lease, () => throw new InvalidOperationException("must not reuse")));
        Configured(Rate(lab.AllFacts));
    }

    [Fact]
    public void CompletionAtDeadlineIsRejectedEvenWithoutTimeoutCallback()
    {
        var clock = new Clock();
        var lab = new EvidenceLabService();
        var lease = lab.BeginPlatformGovernanceCollection(10, CancellationToken.None, clock);
        Assert.False(lab.CollectPlatformGovernance(lease, () =>
        {
            var batch = Batch(4400);
            clock.Advance(TimeSpan.FromMilliseconds(10));
            return batch;
        }));
        Assert.Equal(FactAvailability.ReadError, Rate(lab.AllFacts).Availability);
        Assert.Contains("逾時", Rate(lab.AllFacts).UnavailableReason);
    }

    [Fact]
    public void RevokedOrSupersededLeaseCannotInvokeNewSourceOrReplaceCurrentBatch()
    {
        var lab = new EvidenceLabService();
        var old = lab.BeginPlatformGovernanceCollection(0, CancellationToken.None);
        var current = lab.BeginPlatformGovernanceCollection(0, CancellationToken.None);
        Assert.True(lab.CollectPlatformGovernance(current, () => Batch(3200)));
        var expectedRate = Rate(lab.AllFacts);
        int calls = 0;
        Assert.False(lab.CollectPlatformGovernance(old, () => { calls++; return Batch(4400); }));
        lab.RevokePlatformGovernanceCollection(old, timedOut: true);
        Assert.Equal(0, calls);
        Assert.Equal(expectedRate, Rate(lab.AllFacts));
        lab.RevokePlatformGovernanceCollection(current, timedOut: true);
        Assert.Equal(expectedRate, Rate(lab.AllFacts));
    }

    [Fact]
    public void CancellationDuringReadCannotLeavePreviousOrNewPresentRate()
    {
        var lab = new EvidenceLabService();
        var first = lab.BeginPlatformGovernanceCollection(0, CancellationToken.None);
        Assert.True(lab.CollectPlatformGovernance(first, () => Batch(4400)));
        using var cancellation = new CancellationTokenSource();
        var next = lab.BeginPlatformGovernanceCollection(0, cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => lab.CollectPlatformGovernance(next, () =>
        {
            cancellation.Cancel();
            return Batch(3200);
        }));
        Assert.Equal(FactAvailability.Unknown, Rate(lab.AllFacts).Availability);
        Assert.Equal("", Rate(lab.AllFacts).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OuterTimeoutRevokesLateSuccessOrFailureAndDoesNotDisposeRunningSource(bool failLate)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        Thread? worker = null;
        var lab = new EvidenceLabService();
        try
        {
            EvidenceCollection.MeasurePlatformGovernance(lab, () =>
            {
                worker = Thread.CurrentThread;
                entered.Set();
                try
                {
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture release");
                    if (failLate) throw new IOException("old fixture failure");
                    return Read(Row());
                }
                finally { disposed.Set(); }
            }, Governance, 100);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(disposed.IsSet);
            Assert.False(Assert.Single(lab.Steps).Succeeded);
            Assert.Equal(FactAvailability.ReadError, Rate(lab.AllFacts).Availability);
            var current = lab.BeginPlatformGovernanceCollection(0, CancellationToken.None);
            Assert.True(lab.CollectPlatformGovernance(current, () => Batch(3200)));
            var expected = Rate(lab.AllFacts);
            int lateNotifications = 0;
            lab.PropertyChanged += (_, _) => lateNotifications++;
            release.Set();
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(worker);
            Assert.True(worker!.Join(TimeSpan.FromSeconds(5)));
            Assert.Equal(expected, Rate(lab.AllFacts));
            Assert.Equal(0, lateNotifications);
        }
        finally
        {
            release.Set();
            if (worker is not null) Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void SourceTimeoutHoldsSingleFlightUntilActualEnumeratorCleanup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        Thread? worker = null;
        int reads = 0;
        IEnumerable<PhysicalMemoryRawRow> Blocking()
        {
            worker = Thread.CurrentThread;
            Interlocked.Increment(ref reads);
            entered.Set();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture release");
                yield return Row();
            }
            finally { disposed.Set(); }
        }
        try
        {
            var timedOut = PhysicalMemorySnapshotSource.ReadBounded(Blocking, 100);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(timedOut.Complete);
            Assert.False(disposed.IsSet);
            var blocked = PhysicalMemorySnapshotSource.ReadBounded(() =>
            {
                Interlocked.Increment(ref reads);
                return new[] { Row() };
            }, 100);
            Assert.False(blocked.Complete);
            Assert.Contains("未重發查詢", blocked.Reason);
            Assert.Equal(1, reads);
            release.Set();
            Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotNull(worker);
            Assert.True(worker!.Join(TimeSpan.FromSeconds(5)));
            var next = PhysicalMemorySnapshotSource.ReadBounded(() => new[] { Row(3200) }, 5000);
            Assert.True(next.Complete);
            Configured(Rate(PhysicalMemorySnapshotSource.ProjectConfiguredRate(next, At)), 3200);
        }
        finally
        {
            release.Set();
            if (worker is not null) Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void SharedRealCollectorPublishesRateAndAllFactsDoesNotRereadSource()
    {
        int reads = 0;
        var stages = new List<string>();
        var facts = Collect(() => { reads++; return Read(Row()); }, stages.Add);
        Assert.Equal(new[] { "driver", "other", "memory", "governance" }, stages);
        Assert.Equal(1, reads);
        Configured(Rate(facts));
        Assert.Single(facts.Where(f => f.Key.StartsWith("memory.", StringComparison.Ordinal)));
        var lab = new EvidenceLabService();
        EvidenceCollection.MeasurePlatformGovernance(lab, () => { reads++; return Read(Row()); }, Governance, 0);
        _ = lab.AllFacts; _ = lab.AllFacts; _ = lab.FirmwareSecurityRows;
        Assert.Equal(2, reads);
        Configured(Rate(lab.AllFacts));
    }

    [Fact]
    public void EvidenceJsonUsesRealComposedCollectorAndKeepsConfiguredProvenance()
    {
        int reads = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = CliService.Run(["--json", "evidence", "--query", "memory."],
            () => Collect(() => { reads++; return Read(Row()); }), output, error);
        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal("", error.ToString());
        Assert.Equal(1, reads);
        using var json = JsonDocument.Parse(output.ToString());
        var rate = Assert.Single(json.RootElement.GetProperty("facts").EnumerateArray());
        Assert.Equal("memory.rate", rate.GetProperty("key").GetString());
        Assert.Equal("Present", rate.GetProperty("availability").GetString());
        Assert.Equal("Reported", rate.GetProperty("trust").GetString());
        Assert.Equal("Windows 設定速率（WMI）", rate.GetProperty("source").GetString());
        Assert.Contains("4400", rate.GetProperty("value").GetString());
    }

    [Fact]
    public void BridgeSnapshotPreservesConfiguredRateFromRealComposedCollector()
    {
        int reads = 0;
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "collect_snapshot"],
            () => Collect(() => { reads++; return Read(Row()); }), output, error);
        Assert.True(code is CliService.ExitOk or CliService.ExitPartial);
        Assert.Equal("", error.ToString());
        Assert.Equal(1, reads);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(output.ToString()));
        var snapshot = HardwareSnapshotService.LoadFromStream(stream);
        var rate = Assert.Single(snapshot.Facts, f => f.Key == "memory.rate");
        Assert.Equal(FactAvailability.Present, rate.Availability);
        Assert.Equal(FactTrustLevel.Reported, rate.Trust);
        Assert.Equal("Windows 設定速率（WMI）", rate.Source);
        Assert.Contains("WMI 設定值", rate.Value);
        Assert.DoesNotContain(snapshot.Facts, f => f.Key == "memory.current");
    }

    [Fact]
    public void BridgeSnapshotPreservesFailedBatchAvailabilityAndReasonWithoutStaleSuccess()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        int code = CliService.Run([CliService.AgentBridgeArg, "collect_snapshot"],
            () => Collect(() => PhysicalMemorySnapshotSource.ReadRows(
                () => throw new UnauthorizedAccessException("SECRET-PATH"))), output, error);
        Assert.True(code is CliService.ExitOk or CliService.ExitPartial);
        Assert.Equal("", error.ToString());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(output.ToString()));
        var snapshot = HardwareSnapshotService.LoadFromStream(stream);
        var rate = Assert.Single(snapshot.Facts, f => f.Key == "memory.rate");
        Assert.Equal(FactAvailability.InsufficientPrivilege, rate.Availability);
        Assert.Equal(FactTrustLevel.Unknown, rate.Trust);
        Assert.Equal("", rate.Value);
        Assert.Contains("UnauthorizedAccessException", rate.UnavailableReason);
        Assert.DoesNotContain("SECRET-PATH", rate.UnavailableReason);
    }

    [Fact]
    public void EvidencePackContainsComposedRateAndVerifiedPayloadHashWithoutReadingUserAuditLog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xinspect-cli-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            int reads = 0;
            using var output = new StringWriter(); using var error = new StringWriter();
            int code = CliService.Run([CliService.EvidencePackArg, directory],
                () => Collect(() => { reads++; return Read(Row()); }), output, error,
                evidencePackChainPath: Path.Combine(directory, "missing-fixture-chain.jsonl"));
            Assert.True(code is CliService.ExitOk or CliService.ExitPartial);
            Assert.Equal("", error.ToString());
            Assert.Equal(1, reads);
            using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "evidence.json")));
            var rate = Assert.Single(evidence.RootElement.GetProperty("facts").EnumerateArray(),
                f => f.GetProperty("key").GetString() == "memory.rate");
            Assert.Equal("Reported", rate.GetProperty("trust").GetString());
            Assert.Equal("Present", rate.GetProperty("availability").GetString());
            Assert.Contains("WMI 設定值", rate.GetProperty("value").GetString());
            Assert.DoesNotContain(evidence.RootElement.GetProperty("facts").EnumerateArray(),
                f => f.GetProperty("key").GetString() == "memory.current");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, EvidencePackBuilder.ManifestFileName)));
            var entry = Assert.Single(manifest.RootElement.GetProperty("files").EnumerateArray(),
                f => f.GetProperty("file").GetString() == "evidence.json");
            Assert.Equal(EvidencePackBuilder.Sha256OfFile(Path.Combine(directory, "evidence.json")), entry.GetProperty("sha256").GetString());
            Assert.False(File.Exists(Path.Combine(directory, "audit-chain.jsonl")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void BothExecutableWrappersAndDefaultBindingsUseTheTestedSharedComposition()
    {
        string root = RepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        string program = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Program.cs"));
        Assert.Matches(@"private static IReadOnlyList<HardwareFact> CollectForCli\(\)\s*=> EvidenceCollection\.CollectForCli\(\);", app);
        Assert.Matches(@"private static IReadOnlyList<HardwareFact> Collect\(\)\s*=> EvidenceCollection\.CollectForCli\(\);", program);
        string composition = File.ReadAllText(Path.Combine(root, "Services", "EvidenceCollection.cs"));
        Assert.Matches(@"public static IReadOnlyList<HardwareFact> CollectForCli\(\)\s*=> CollectForCli\(ReloadInto, null, LoadOtherUsermodeFacts,\s*PhysicalMemorySnapshotSource\.ReadForThisMachine", composition);
        Assert.Matches(@"public static void LoadUsermodeFacts\(EvidenceLabService svc, IStorageReliabilitySource\? storageReliability = null\)\s*=> LoadUsermodeFactsWithPlatform", composition);
        Assert.Matches(@"LoadUsermodeFactsDeferred\(EvidenceLabService svc\)\s*\{\s*var lease = svc.BeginPlatformGovernanceCollection", composition);
        string startup = File.ReadAllText(Path.Combine(root, "ViewModels", "StartupSequence.cs"));
        Assert.Matches(@"BuildWmiTimings\(IReadOnlyList<MemoryModuleInfo> mods\)\s*=> WmiMemoryTimings\.Build\(mods\);", startup);
        string source = File.ReadAllText(Path.Combine(root, "Services", "PhysicalMemorySnapshotSource.cs"));
        Assert.Equal(1, Regex.Matches(source, "new ManagementObjectSearcher\\(").Count);
        Assert.DoesNotContain("CpuzReportService", composition);
        Assert.DoesNotContain("GetMemoryModules", composition);
        Assert.DoesNotContain("CpuzReportService", source);
        Assert.DoesNotContain("GetMemoryModules", source);
    }

    private static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "XinSpect.csproj"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return folder!.FullName;
    }
}
