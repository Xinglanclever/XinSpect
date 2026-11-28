using System.Diagnostics;
using Xunit;

namespace XinSpect.Tests;

public sealed class MemoryControllerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 27, 1, 2, 3, TimeSpan.Zero);
    private static MemoryControllerClockSample Clock(string name, double? value = 1800, string cpu = "/amdcpu/0", string? id = null)
        => new(cpu, "Synthetic AMD CPU", id ?? cpu + "/clock/20", name, value, At);
    private static MemoryTimings Report(double frequency = 1799.6) => new()
    {
        Loaded = true, SourceText = "CPU-Z 報告", DramFrequencyMHz = frequency, TRFC = "560", TCCD = "8",
        TCCDL = "12", TCCDWR = "8", TCCDWRL = "16", RateFactAvailability = FactAvailability.Present,
        TimingFactAvailability = FactAvailability.Present, RateFactTrust = FactTrustLevel.Reported,
        TimingFactTrust = FactTrustLevel.Reported,
    };
    private static MemoryTimings Wmi() => new()
    {
        SourceText = "Windows 設定速率（WMI）", DramFrequencyMHz = 1600, DataRateText = "DDR4-3200（WMI 設定值）",
        RateIsConfigured = true, RateFactAvailability = FactAvailability.Present, RateFactTrust = FactTrustLevel.Reported,
    };

    [Fact]
    public void ExistingAmdProviderClocksKeepSeparateQuantitiesAndUnconfirmedCacheState()
    {
        var snapshot = MemoryControllerService.FromLhm([Clock("Memory", 3000), Clock("Uncore", 1500), Clock("Fabric", 2000)]);
        Assert.Equal(new[] { "mclk", "uclk", "fclk" }, snapshot.Clocks.Select(r => r.Key));
        Assert.Equal(new double?[] { 3000, 1500, 2000 }, snapshot.Clocks.Select(r => r.NumericValue));
        Assert.All(snapshot.Clocks, r =>
        {
            Assert.Equal(FactAvailability.Unknown, r.Availability);
            Assert.Equal(FactTrustLevel.Reported, r.Trust);
            Assert.Equal(MemoryControllerService.LhmSource, r.Source);
            Assert.Equal("/amdcpu/0", r.Scope);
            Assert.Equal(At, r.CapturedAtUtc);
            Assert.Null(r.SourceUpdatedAtUtc);
            Assert.Contains("保留舊值", r.Reason);
        });
    }

    [Fact]
    public void GpuMemoryIntelUncoreAndUnrecognisedIdentifiersNeverBecomeAmdClocks()
    {
        var snapshot = MemoryControllerService.FromLhm([
            Clock("Memory", cpu: "/nvidiagpu/0"), Clock("Uncore", cpu: "/intelcpu/0"),
            Clock("GPU Memory"), Clock("Memory", id: "/amdcpu/1/clock/0"),
            Clock("Memory", cpu: "/amdcpu/0/child"), Clock("Memory", id: "/amdcpu/0/clock/not-an-index")]);
        Assert.Empty(snapshot.Clocks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(12001d)]
    public void InvalidOrMissingClockValueClearsNumericAndPreservesReadFailure(double? value)
    {
        var snapshot = MemoryControllerService.FromLhm([Clock("Memory", value)]);
        var row = Assert.Single(snapshot.Clocks, r => r.Key == "mclk");
        Assert.Null(row.NumericValue);
        Assert.Equal(FactAvailability.ReadError, row.Availability);
        var merged = MemoryControllerService.Merge(snapshot, null);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(merged.Clocks, r => r.Key == "mclk").Availability);
    }

    [Fact]
    public void DetailProjectionOmitsSeparatorWhenReasonIsMissingAndPreservesUncataloguedSourceText()
    {
        var row = new MemoryControllerReading("mclk", "MCLK", 1800, "MHz", "", FactAvailability.Unknown,
            FactTrustLevel.Reported, "provider-name", "Memory Frequency", "cpu-0", At, null, "");
        Assert.Equal("Memory Frequency", row.DisplayDetailText);
        Assert.DoesNotContain("；", row.DisplayDetailText);

        row = row with { RawField = "記憶體\u00a0Frequency", Reason = "provider reason: 原始資料" };
        Assert.Equal("記憶體\u00a0Frequency；provider reason: 原始資料", row.DisplayDetailText);
    }

    [Fact]
    public void DetailProjectionLocalizesKnownReasonAndKeepsProviderFieldExact()
    {
        var row = new MemoryControllerReading("mclk", "MCLK", null, "MHz", "", FactAvailability.ReadError,
            FactTrustLevel.Reported, "provider-name", "VendorField", "cpu-0", At, null,
            "此輪硬體輪詢失敗或服務已關閉；不沿用先前值。");
        var oldEnglish = LanguageService.IsEnglish;
        var oldSimplified = LanguageService.IsSimplified;
        try
        {
            LanguageService.SetEnglishForTests(true);
            Assert.Equal("VendorField; This hardware poll failed or the service was closed; previous values are not reused.",
                row.DisplayDetailText);
        }
        finally
        {
            if (oldEnglish) LanguageService.SetEnglishForTests(true);
            else { LanguageService.SetEnglishForTests(false); LanguageService.SetSimplifiedForTests(oldSimplified); }
        }
    }

    [Fact]
    public void DuplicateSensorIdentityIsRejectedWithoutPickingFirstOrLastValue()
    {
        var row = Assert.Single(MemoryControllerService.FromLhm([Clock("Memory", 1800), Clock("Memory", 2000)]).Clocks, r => r.Key == "mclk");
        Assert.Null(row.NumericValue);
        Assert.Equal(FactAvailability.ReadError, row.Availability);
        Assert.Contains("重複", row.Reason);
    }

    [Fact]
    public void MultipleCpuScopesRemainSeparateAndNeverBecomeOneScalar()
    {
        var snapshot = MemoryControllerService.FromLhm([Clock("Memory", 1600), Clock("Memory", 1800, "/amdcpu/1")]);
        var memories = snapshot.Clocks.Where(r => r.Key == "mclk").ToArray();
        Assert.Equal(new[] { "/amdcpu/0", "/amdcpu/1" }, memories.Select(r => r.Scope));
        Assert.Equal(new double?[] { 1600, 1800 }, memories.Select(r => r.NumericValue));
    }

    [Fact]
    public void WmiConfiguredRateCannotFillMclkUclkOrFclk()
    {
        var result = MemoryControllerService.Merge(null, MemoryControllerService.FromTimings(null, Wmi(), null));
        Assert.All(result.Clocks, r => { Assert.Null(r.NumericValue); Assert.Equal(FactAvailability.NotSupported, r.Availability); });
        var configured = Assert.Single(result.ConfiguredRates);
        Assert.Equal("DDR4-3200（WMI 設定值）", configured.RawText);
        Assert.Contains("ConfiguredClockSpeed", configured.RawField);
        Assert.Equal(FactTrustLevel.Reported, configured.Trust);
    }

    [Fact]
    public void ResolverPreservesCpuZActualFrequencyEvenWhenWmiWinsLegacyRate()
    {
        var result = HardwareReadingPriority.ResolveMemory(null, Wmi(), Report());
        Assert.Equal(1600, result.DramFrequencyMHz);
        var actual = Assert.Single(result.Controller.Clocks);
        Assert.Equal(1799.6, actual.NumericValue);
        Assert.Equal("CPU-Z 報告", actual.Source);
        Assert.Equal("Memory Frequency", actual.RawField);
        Assert.Single(result.Controller.ConfiguredRates);
        Assert.Equal(5, result.Controller.SubTimings.Count);
    }

    [Fact]
    public void SpdAndJedecRateFlagsCannotPopulateCurrentControllerValues()
    {
        var spd = Report();
        spd.IsJedecReference = true;
        var result = MemoryControllerService.FromTimings(spd, null, null);
        Assert.Empty(result.Clocks);
        Assert.All(result.SubTimings, r => { Assert.Null(r.NumericValue); Assert.Empty(r.RawText); });
        spd.IsJedecReference = false;
        spd.RateIsJedecReference = true;
        Assert.Empty(MemoryControllerService.FromTimings(spd, null, null).Clocks);
    }

    [Theory]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void PerQuantityAvailabilityAndTrustDoNotGetPromoted(FactAvailability availability)
    {
        var report = Report();
        report.RateFactAvailability = availability;
        report.TimingFactAvailability = availability;
        report.TimingFactTrust = FactTrustLevel.Unknown;
        var result = MemoryControllerService.FromTimings(null, null, report);
        Assert.Equal(availability, Assert.Single(result.Clocks).Availability);
        Assert.All(result.SubTimings, r => { Assert.Equal(availability, r.Availability); Assert.Equal(FactTrustLevel.Unknown, r.Trust); });
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("NaN")]
    [InlineData("65536")]
    [InlineData("5.5")]
    public void MalformedSubtimingsRemainRawUnconfirmedEvidence(string raw)
    {
        var report = Report();
        report.TRFC = raw;
        var row = Assert.Single(MemoryControllerService.FromTimings(null, null, report).SubTimings, r => r.Key == "trfc");
        Assert.Null(row.NumericValue);
        Assert.Equal(raw, row.RawText);
        Assert.Equal(FactAvailability.Unknown, row.Availability);
    }

    [Fact]
    public void LiveUnknownCacheAndCpuZReportRemainNamedDistinctObservations()
    {
        var live = MemoryControllerService.FromLhm([Clock("Memory", 1800)]);
        var result = MemoryControllerService.Merge(live, MemoryControllerService.FromTimings(null, Wmi(), Report()));
        var mclk = result.Clocks.Where(r => r.Key == "mclk").ToArray();
        Assert.Equal(2, mclk.Length);
        Assert.Equal(FactAvailability.Unknown, mclk[0].Availability);
        Assert.Equal(FactAvailability.Present, mclk[1].Availability);
        Assert.NotEqual(mclk[0].Scope, mclk[1].Scope);
        Assert.Single(result.ConfiguredRates);
    }

    [Fact]
    public void ProviderCaptureExpiresOnMonotonicTimeAndCannotSurvivePollFailure()
    {
        long start = Stopwatch.Frequency;
        var snapshot = MemoryControllerService.FromLhm([Clock("Memory")], start);
        var fresh = MemoryControllerService.WithFreshness(snapshot, start + 5 * Stopwatch.Frequency, TimeSpan.FromSeconds(5));
        Assert.Same(snapshot, fresh);
        foreach (long now in new[] { start - 1, start + 6 * Stopwatch.Frequency })
        {
            var expired = MemoryControllerService.WithFreshness(snapshot, now, TimeSpan.FromSeconds(5));
            Assert.All(expired.Clocks, r => { Assert.Null(r.NumericValue); Assert.True(r.IsExpired); Assert.Equal("擷取已過期", r.StateText); });
        }
        var failed = MemoryControllerService.Invalidated(snapshot);
        Assert.All(failed.Clocks, r => { Assert.Null(r.NumericValue); Assert.Equal(FactAvailability.ReadError, r.Availability); });
        Assert.Equal(1800, Assert.Single(snapshot.Clocks, r => r.Key == "mclk").NumericValue); // immutable original
    }

    [Fact]
    public void UncoreReportNeverBecomesUclkOnUnidentifiedPlatforms()
    {
        var report = Report();
        report.UncoreText = "2400 MHz";
        var result = MemoryControllerService.Merge(null, MemoryControllerService.FromTimings(null, null, report));
        Assert.Null(Assert.Single(result.Clocks, r => r.Key == "uclk").NumericValue);
        Assert.Null(Assert.Single(result.Clocks, r => r.Key == "fclk").NumericValue);
    }
}
