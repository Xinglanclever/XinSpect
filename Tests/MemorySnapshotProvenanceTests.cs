using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

// Uses the actual EvidenceLab memory producer and real priority resolver without a MainViewModel.
public sealed class MemorySnapshotProvenanceTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);

    private static MemoryTimings Current(string source, string cl = "16", double mhz = 1800) => new()
    {
        Loaded = true, SourceText = source, CL = cl, TRCD = "18", TRP = "18", TRAS = "36",
        DramFrequencyMHz = mhz, DataRateText = "DDR4-3600",
        TimingFactAvailability = FactAvailability.Present, RateFactAvailability = FactAvailability.Present,
        TimingFactTrust = FactTrustLevel.Measured, RateFactTrust = FactTrustLevel.Measured,
    };

    private static MemoryTimings Wmi() => StartupSequence.BuildWmiTimings([
        new MemoryModuleInfo { MemoryType = "DDR4", ConfiguredSpeedMHz = 3200, CapacityGB = 8 }
    ])!;

    private static MemoryTimings Jedec()
    {
        var t = Current("SPD bus0 JEDEC", "22", 1600);
        t.IsJedecReference = true;
        t.TimingFactTrust = t.RateFactTrust = FactTrustLevel.Derived;
        t.DataRateText = "DDR4-3200";
        return t;
    }

    private static IReadOnlyList<HardwareFact> Project(MemoryTimings? direct, MemoryTimings? wmi, MemoryTimings? report)
        => EvidenceLabService.CollectMemoryTimingFacts(HardwareReadingPriority.ResolveMemory(direct, wmi, report), At);

    [Fact]
    public void WmiOnlySnapshotKeepsConfiguredReportedSourceWithoutCpuZ()
    {
        var rate = Assert.Single(Project(null, Wmi(), null));
        Assert.Equal("memory.rate", rate.Key);
        Assert.Equal("Windows 設定速率（WMI）", rate.Source);
        Assert.Equal("設定資料速率（WMI 回報）", rate.Name);
        Assert.Equal(FactTrustLevel.Reported, rate.Trust);
        Assert.Equal(FactAvailability.Present, rate.Availability);
        Assert.DoesNotContain("CPU-Z", rate.Source);
    }

    [Fact]
    public void KernelCurrentTimingsAndWmiRateHaveSeparateSourcesAndTrust()
    {
        var kernel = Current("kernel controller", mhz: double.NaN);
        kernel.DataRateText = "—";
        var facts = Project(kernel, Wmi(), Current("CPU-Z competing"));
        var timing = Assert.Single(facts, x => x.Key == "memory.current");
        var rate = Assert.Single(facts, x => x.Key == "memory.rate");
        Assert.Equal("kernel controller", timing.Source);
        Assert.Equal(FactTrustLevel.Measured, timing.Trust);
        Assert.Equal("16-18-18-36", timing.Value);
        Assert.Equal("Windows 設定速率（WMI）", rate.Source);
        Assert.Equal(FactTrustLevel.Reported, rate.Trust);
    }

    [Fact]
    public void JedecOnlyValuesRemainUnknownForCurrentQuantity()
    {
        var facts = Project(Jedec(), null, null);
        Assert.Equal(2, facts.Count);
        Assert.All(facts, fact =>
        {
            Assert.Equal(FactAvailability.Unknown, fact.Availability);
            Assert.Equal(FactTrustLevel.Derived, fact.Trust);
            Assert.Contains("SPD bus0 JEDEC", fact.Source);
            Assert.Contains("JEDEC", fact.Name);
            Assert.Contains("沒有取得", fact.UnavailableReason);
            Assert.Null(fact.NumericValue);
        });
        Assert.Contains(facts, x => x.Key == "memory.current" && x.Value.StartsWith("JEDEC 基準："));
        Assert.Contains(facts, x => x.Key == "memory.rate" && x.Value == "DDR4-3200");
        var row = EvidenceFactRow.From(Assert.Single(facts, x => x.Key == "memory.rate"));
        Assert.True(row.IsUnavailable);
        Assert.False(row.IsWarning);
        Assert.Contains("未確認", row.ValueText);
        Assert.Contains("DDR4-3200", row.ValueText);
    }

    [Fact]
    public void JedecTimingsDoNotDowngradeOrRelabelWmiConfiguredRate()
    {
        var facts = Project(Jedec(), Wmi(), null);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(facts, x => x.Key == "memory.current").Availability);
        var rate = Assert.Single(facts, x => x.Key == "memory.rate");
        Assert.Equal(FactAvailability.Present, rate.Availability);
        Assert.Equal(FactTrustLevel.Reported, rate.Trust);
        Assert.Contains("WMI", rate.Source);
        Assert.DoesNotContain("JEDEC", rate.Name);
    }

    [Fact]
    public void CpuZCurrentTimingsReplaceJedecWhileWmiStillProvidesRate()
    {
        var report = new CpuzReport();
        CpuzReportService.Parse(["Chipset", "\tMemory Type\tDDR4", "\tCAS# latency (CL)\t18.0",
            "\tRAS# to CAS# delay (tRCD)\t20.0", "\tRAS# Precharge (tRP)\t20.0",
            "\tCycle Time (tRAS)\t40.0", "\tMemory Frequency\t1800.0 MHz"], report);
        var facts = Project(Jedec(), Wmi(), report.Timings);
        var timing = Assert.Single(facts, x => x.Key == "memory.current");
        Assert.Equal("CPU-Z 報告", timing.Source);
        Assert.Equal(FactTrustLevel.Reported, timing.Trust);
        Assert.Equal(FactAvailability.Present, timing.Availability);
        Assert.Equal("18-20-20-40", timing.Value);
        Assert.Equal("Windows 設定速率（WMI）", Assert.Single(facts, x => x.Key == "memory.rate").Source);
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.ReadError)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.NotApplicable)]
    public void UnconfirmedSelectedSourceCannotBecomePresent(FactAvailability state)
    {
        var direct = Current("injected controller");
        direct.TimingFactAvailability = direct.RateFactAvailability = state;
        direct.TimingFactReason = direct.RateFactReason = "fixture source reason";
        Assert.All(Project(direct, null, null), fact =>
        {
            Assert.Equal(state, fact.Availability);
            Assert.Contains("fixture source reason", fact.UnavailableReason);
            Assert.Null(fact.NumericValue);
        });
    }

    [Fact]
    public void LegacyObjectsWithoutMetadataRemainUnknown()
    {
        var legacy = new MemoryTimings { Loaded = true, SourceText = "old source", CL = "16", TRCD = "18",
            TRP = "18", TRAS = "36", DataRateText = "DDR4-3200", DramFrequencyMHz = 1600 };
        Assert.All(Project(legacy, null, null), fact =>
        {
            Assert.Equal(FactAvailability.Unknown, fact.Availability);
            Assert.Equal(FactTrustLevel.Unknown, fact.Trust);
        });
    }

    [Fact]
    public void PartialTimingCannotClaimPresentSummary()
    {
        var partial = Current("partial controller");
        partial.TRCD = "—";
        var fact = Assert.Single(Project(partial, null, null), x => x.Key == "memory.current");
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Contains("不完整", fact.UnavailableReason);
    }

    [Fact]
    public void MissingInputsDoNotInventFactsOrValues()
    {
        Assert.Empty(Project(null, null, null));
        Assert.Empty(EvidenceLabService.CollectMemoryTimingFacts(new MemoryTimings(), At));
    }

    [Fact]
    public void MixedTimingFieldsCarryBothSourcesAndWeakestState()
    {
        var kernel = Current("kernel controller");
        kernel.TRCD = "—";
        var report = Current("CPU-Z report", "20");
        report.TimingFactAvailability = FactAvailability.Unknown;
        report.TimingFactTrust = FactTrustLevel.Reported;
        report.TimingFactReason = "report unconfirmed";
        var fact = Assert.Single(Project(kernel, Wmi(), report), x => x.Key == "memory.current");
        Assert.Contains("kernel controller", fact.Source);
        Assert.Contains("CPU-Z report", fact.Source);
        Assert.Equal(FactAvailability.Unknown, fact.Availability);
        Assert.Equal(FactTrustLevel.Reported, fact.Trust);
        Assert.Equal("16-18-18-36", fact.Value);
        Assert.Contains("report unconfirmed", fact.UnavailableReason);
    }

    [Fact]
    public void RealSnapshotSerializationPreservesUnknownJedecAndWmiProvenance()
    {
        var snapshot = HardwareSnapshotService.Create("test", Project(Jedec(), Wmi(), null), false);
        using var json = JsonDocument.Parse(HardwareSnapshotService.ToJson(snapshot));
        var facts = json.RootElement.GetProperty("facts").EnumerateArray().ToArray();
        var timing = Assert.Single(facts, x => x.GetProperty("key").GetString() == "memory.current");
        var rate = Assert.Single(facts, x => x.GetProperty("key").GetString() == "memory.rate");
        Assert.Equal("Unknown", timing.GetProperty("availability").GetString());
        Assert.Contains("JEDEC", timing.GetProperty("unavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, timing.GetProperty("numericValue").ValueKind);
        Assert.Equal("Windows 設定速率（WMI）", rate.GetProperty("source").GetString());
        Assert.Equal("Reported", rate.GetProperty("trust").GetString());
        Assert.False(rate.TryGetProperty("availability", out _));
        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.RootElement.GetRawText()));
        var loaded = HardwareSnapshotService.LoadFromStream(stream);
        Assert.Equal(FactAvailability.Present, Assert.Single(loaded.Facts, x => x.Key == "memory.rate").Availability);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(loaded.Facts, x => x.Key == "memory.current").Availability);
    }
}
