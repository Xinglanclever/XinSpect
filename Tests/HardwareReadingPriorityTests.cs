using Xunit;

namespace XinSpect.Tests;

public class HardwareReadingPriorityTests
{
    [Fact]
    public void CpuDirectReadWinsConflictingWmiAndReport()
    {
        var result = HardwareReadingPriority.ResolveCpu(
            new CpuDetail { Specification = "CPUID identity", CoreVoltage = "1.1 V", L2 = "2 MB" },
            new CpuStatic { Name = "firmware identity", CurrentVoltage = 1.2, L2Cache = "1 MB" },
            new CpuDetail { Specification = "report identity", CoreVoltage = "1.3 V", L2 = "4 MB" });
        Assert.Equal("CPUID identity", result.Specification);
        Assert.Equal("1.1 V", result.CoreVoltage);
        Assert.Equal("2 MB", result.L2);
    }

    [Fact]
    public void CpuWmiWinsReportOnlyForEquivalentFields()
    {
        var result = HardwareReadingPriority.ResolveCpu(new(),
            new CpuStatic { Name = "WMI name", L3Cache = "30 MB", CurrentVoltage = 1.2,
                Socket = "CPU0", MaxClockMHz = 5000 },
            new CpuDetail { Specification = "report name", L3 = "16 MB", CoreVoltage = "1.4 V",
                Package = "LGA1700", StockFreq = "3400 MHz" });
        Assert.Equal("WMI name", result.Specification);
        Assert.Equal("30 MB", result.L3);
        Assert.Contains("WMI", result.CoreVoltage);
        Assert.Equal("LGA1700", result.Package);
        Assert.Equal("3400 MHz", result.StockFreq);
    }

    [Fact]
    public void CpuReportFillsMissingFieldsWithoutErasingDirectReads()
    {
        var result = HardwareReadingPriority.ResolveCpu(
            new CpuDetail { CoreSpeed = "4100 MHz" }, new(),
            new CpuDetail { Specification = "CPU", Microcode = "0x123", CoreSpeed = "4000 MHz" });
        Assert.True(result.Loaded);
        Assert.Equal("4100 MHz", result.CoreSpeed);
        Assert.Equal("0x123", result.Microcode);
    }

    [Fact]
    public void EmptyCpuReportDoesNotRemoveNativeIdentity()
    {
        var result = HardwareReadingPriority.ResolveCpu(new CpuDetail { Specification = "CPU" }, new(), null);
        Assert.True(result.Loaded);
        Assert.Equal("CPU", result.Specification);
        Assert.Equal("—", result.CoreVoltage);
    }

    [Fact]
    public void CurrentKernelMemoryReadsWinWmiAndReport()
    {
        var direct = Current("kernel", 1800, "18");
        var report = Current("CPU-Z", 1700, "20");
        report.TRCD = "22";
        var result = HardwareReadingPriority.ResolveMemory(direct, Current("WMI", 1600, "—"), report);
        Assert.Equal(1800, result.DramFrequencyMHz);
        Assert.Equal("18", result.CL);
        Assert.Equal("22", result.TRCD);
        Assert.False(result.IsJedecReference);
        Assert.Contains("kernel", result.SourceText);
    }

    [Fact]
    public void WmiConfiguredRateWinsCpuZButCpuZFillsCurrentTimings()
    {
        var result = HardwareReadingPriority.ResolveMemory(null,
            new MemoryTimings { SourceText = "WMI", DramFrequencyMHz = 1600, DataRateText = "DDR4-3200" },
            Current("CPU-Z", 1799, "18"));
        Assert.Equal(1600, result.DramFrequencyMHz);
        Assert.Equal("DDR4-3200", result.DataRateText);
        Assert.Equal("18", result.CL);
        Assert.Contains("WMI", result.SourceText);
        Assert.Contains("CPU-Z", result.SourceText);
    }

    [Fact]
    public void JedecProfileDoesNotOverrideCurrentFrequencyOrTimings()
    {
        var reference = Current("SPD JEDEC", 1066, "15");
        reference.IsJedecReference = true;
        var result = HardwareReadingPriority.ResolveMemory(reference,
            new MemoryTimings { SourceText = "WMI", DramFrequencyMHz = 1600 }, Current("CPU-Z", 1800, "18"));
        Assert.Equal(1600, result.DramFrequencyMHz);
        Assert.Equal("18", result.CL);
        Assert.False(result.IsJedecReference);
        Assert.Equal(1066, reference.DramFrequencyMHz);
    }

    [Fact]
    public void JedecFallbackNeverBecomesCurrentTimingEvidence()
    {
        var reference = Current("SPD JEDEC", 1600, "22");
        reference.IsJedecReference = true;
        var result = HardwareReadingPriority.ResolveMemory(reference, null, null);
        Assert.True(result.IsJedecReference);
        Assert.Contains("JEDEC", result.PrimaryTimingsText);
        Assert.Null(SpdConsistencyAuditService.FromCurrentTimings(result));
    }

    [Fact]
    public void InvalidNativeFrequencyFallsBackWithoutLosingItsTimings()
    {
        var result = HardwareReadingPriority.ResolveMemory(Current("kernel", double.NaN, "16"),
            new MemoryTimings { SourceText = "WMI", DramFrequencyMHz = 1600 }, Current("CPU-Z", 1800, "18"));
        Assert.Equal(1600, result.DramFrequencyMHz);
        Assert.Equal("16", result.CL);
    }

    [Fact]
    public void MissingMemorySourcesDoNotInventNumbers()
    {
        var result = HardwareReadingPriority.ResolveMemory(null, null, null);
        Assert.False(result.Loaded);
        Assert.Equal(0, result.DramFrequencyMHz);
        Assert.Equal("—", result.CL);
        Assert.Null(SpdConsistencyAuditService.FromCurrentTimings(result));
    }

    [Fact]
    public void CurrentTimingAuditPreservesItsActualSource()
    {
        var evidence = SpdConsistencyAuditService.FromCurrentTimings(Current("native controller", 1800, "18"));
        Assert.NotNull(evidence);
        Assert.Equal("native controller", evidence.Source);
    }

    [Fact]
    public void CpuZParserLabelsItsTimings()
    {
        var report = new CpuzReport();
        CpuzReportService.Parse(["Chipset", "\tCAS# latency (CL)\t18.0"], report);
        Assert.Equal("CPU-Z 報告", report.Timings.SourceText);
        Assert.False(report.Timings.IsJedecReference);
    }

    [Fact]
    public void DifferentWmiRatesDoNotBecomeOneMachineWideRate()
    {
        var result = StartupSequence.BuildWmiTimings([
            new MemoryModuleInfo { ConfiguredSpeedMHz = 3200, CapacityGB = 8 },
            new MemoryModuleInfo { ConfiguredSpeedMHz = 2666, CapacityGB = 8 }]);
        Assert.NotNull(result);
        Assert.Equal(0, result.DramFrequencyMHz);
        Assert.Equal("—", result.DataRateText);
        Assert.Equal("16 GB（2 條）", result.MemorySizeText);
    }

    [Fact]
    public void MissingWmiTypeDoesNotBecomeDashSpeedLabel()
    {
        var result = StartupSequence.BuildWmiTimings([new MemoryModuleInfo { ConfiguredSpeedMHz = 3200 }]);
        Assert.NotNull(result);
        Assert.StartsWith("DDR-3200", result.DataRateText);
    }

    private static MemoryTimings Current(string source, double frequency, string cl) => new()
    {
        Loaded = true, SourceText = source, DramFrequencyMHz = frequency,
        MemoryTypeText = "DDR4", DataRateText = "DDR4-3600", CL = cl,
    };
}
