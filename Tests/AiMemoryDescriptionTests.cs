using Xunit;

namespace XinSpect.Tests;

// Registered production tool + injected models/strings only; no VM, device, process, network or file I/O.
public sealed class AiMemoryDescriptionTests
{
    private static MemoryTimings Current(string source = "CPU-Z report") => new()
    {
        Loaded = true, SourceText = source, CL = "18", TRCD = "20", TRP = "20", TRAS = "40",
        TRFC = "560", CommandRate = "2T", DramFrequencyMHz = 1800, DataRateText = "DDR4-3600",
        TimingFactAvailability = FactAvailability.Present, RateFactAvailability = FactAvailability.Present,
        TimingFactTrust = FactTrustLevel.Reported, RateFactTrust = FactTrustLevel.Reported,
        Status = "injected timing status",
    };

    private static MemoryTimings Jedec()
    {
        var timings = Current("SPD bus0 JEDEC");
        timings.IsJedecReference = true;
        timings.TimingFactTrust = timings.RateFactTrust = FactTrustLevel.Derived;
        timings.DramFrequencyMHz = 1600;
        timings.DataRateText = "DDR4-3200";
        return timings;
    }

    private static MemoryTimings Wmi() => StartupSequence.BuildWmiTimings([
        new MemoryModuleInfo { MemoryType = "DDR4", ConfiguredSpeedMHz = 3200, CapacityGB = 8 }
    ])!;

    private static string Invoke(MemoryTimings timings)
    {
        var toolbox = new AiToolbox();
        AiToolboxBuilder.AddMemory(toolbox, () => timings, () => "inventory fixture\n");
        return toolbox.Invoke("get_memory_detail", "{}");
    }

    [Fact]
    public void RegisteredDescriptionLimitsProvenanceClaimToTimingAndRate()
    {
        var toolbox = new AiToolbox();
        AiToolboxBuilder.AddMemory(toolbox, () => Current(), () => "");
        string description = Assert.Single(toolbox.Tools).Description;

        Assert.Contains("記憶體型別、通道與模組摘要", description);
        Assert.Contains("對時序和速率逐項標示來源、可用性與信任", description);
        Assert.DoesNotContain("型別、通道、時序與速率，逐量保留", description);
    }

    [Fact]
    public void JedecRegisteredToolDoesNotCallNominalClockActualAndKeepsUnknownProvenance()
    {
        string text = Invoke(Jedec());
        Assert.DoesNotContain("實際頻率", text);
        Assert.Contains("JEDEC 參考時脈（非目前運行值）", text);
        Assert.Contains("目前時序（僅有 JEDEC 基準）", text);
        Assert.Contains("Unknown（未確認）", text);
        Assert.Contains("Derived（衍生計算）", text);
        Assert.Contains("來源：SPD bus0 JEDEC", text);
        Assert.Contains("1600 MHz", text);
        Assert.Contains("保留內容（不得當成已確認目前值）", text);
        Assert.Contains("時序讀取狀態：injected timing status", text);
        Assert.Contains("inventory fixture", text);
    }

    [Fact]
    public void WmiOnlyToolDescribesConfiguredRateAndDerivedClockWhileTimingIsMissing()
    {
        var timings = Wmi();
        Assert.False(timings.Loaded);
        string text = Invoke(timings);
        Assert.Contains("設定資料速率（WMI 回報）：DDR4-3200（WMI 設定值）", text);
        Assert.Contains("設定時脈換算（非即時量測）：1600 MHz", text);
        Assert.Contains("Reported（來源回報）", text);
        Assert.Contains("Derived（衍生計算）", text);
        Assert.Contains("來源：Windows 設定速率（WMI）", text);
        Assert.Contains("目前時序：未取得值", text);
        Assert.Contains("Unknown（未確認）", text);
        Assert.DoesNotContain("實際頻率", text);
    }

    [Fact]
    public void MixedJedecTimingAndWmiRateKeepTheirSeparateScopesAndStates()
    {
        string text = Invoke(HardwareReadingPriority.ResolveMemory(Jedec(), Wmi(), null));
        string timing = Assert.Single(text.Split('\n'), line => line.StartsWith("目前時序（僅有 JEDEC 基準）"));
        string rate = Assert.Single(text.Split('\n'), line => line.StartsWith("設定資料速率（WMI 回報）"));
        string clock = Assert.Single(text.Split('\n'), line => line.StartsWith("設定時脈換算"));
        Assert.Contains("Unknown", timing);
        Assert.Contains("SPD bus0 JEDEC", timing);
        Assert.Contains("Present", rate);
        Assert.Contains("Reported", rate);
        Assert.Contains("Windows 設定速率（WMI）", rate);
        Assert.Contains("Present", clock);
        Assert.DoesNotContain("JEDEC", rate);
        Assert.DoesNotContain("JEDEC", clock);
    }

    [Fact]
    public void RealCpuZParserAndPriorityProduceReportedTimingWithIndependentWmiRate()
    {
        var report = new CpuzReport();
        CpuzReportService.Parse(["Chipset", "\tMemory Type\tDDR4", "\tCAS# latency (CL)\t18.0",
            "\tRAS# to CAS# delay (tRCD)\t20.0", "\tRAS# Precharge (tRP)\t20.0",
            "\tCycle Time (tRAS)\t40.0", "\tMemory Frequency\t1800.0 MHz"], report);
        string text = Invoke(HardwareReadingPriority.ResolveMemory(Jedec(), Wmi(), report.Timings));
        string timing = Assert.Single(text.Split('\n'), line => line.StartsWith("目前時序："));
        Assert.Contains("18-20-20-40", timing);
        Assert.Contains("CPU-Z 報告", timing);
        Assert.Contains("Reported", timing);
        Assert.Contains("Present", timing);
        Assert.Contains("設定資料速率（WMI 回報）", text);
        Assert.DoesNotContain("實際頻率", text);
    }

    [Theory]
    [InlineData(FactAvailability.Present)]
    [InlineData(FactAvailability.Unknown)]
    [InlineData(FactAvailability.NotApplicable)]
    [InlineData(FactAvailability.NotSupported)]
    [InlineData(FactAvailability.InsufficientPrivilege)]
    [InlineData(FactAvailability.ReadError)]
    public void EverySelectedSourceStateAndReasonSurvivesIntoRegisteredTool(FactAvailability state)
    {
        var timings = Current();
        timings.TimingFactSource = "timing provider";
        timings.RateFactSource = "rate provider";
        timings.TimingFactAvailability = timings.RateFactAvailability = state;
        timings.TimingFactReason = "timing fixture reason";
        timings.RateFactReason = "rate fixture reason";
        string text = Invoke(timings);
        Assert.Contains($"可用性：{state}（{FactStateLattice.Describe(state)}）", text);
        Assert.Contains("來源：timing provider", text);
        Assert.Contains("來源：rate provider", text);
        if (state == FactAvailability.Present)
            Assert.DoesNotContain("不得當成已確認目前值", text);
        else
        {
            Assert.Contains("timing fixture reason", text);
            Assert.Contains("rate fixture reason", text);
            Assert.Contains("不得當成已確認目前值", text);
        }
    }

    [Theory]
    [InlineData(FactAvailability.Present, FactAvailability.Unknown, FactTrustLevel.Reported, FactTrustLevel.Reported)]
    [InlineData(FactAvailability.Unknown, FactAvailability.Unknown, null, FactTrustLevel.Unknown)]
    [InlineData(FactAvailability.NotApplicable, FactAvailability.NotApplicable, FactTrustLevel.Derived, FactTrustLevel.Derived)]
    [InlineData(FactAvailability.NotSupported, FactAvailability.NotSupported, FactTrustLevel.Speculative, FactTrustLevel.Speculative)]
    [InlineData(FactAvailability.InsufficientPrivilege, FactAvailability.InsufficientPrivilege, FactTrustLevel.Measured, FactTrustLevel.Measured)]
    [InlineData(FactAvailability.ReadError, FactAvailability.ReadError, FactTrustLevel.Unknown, FactTrustLevel.Unknown)]
    public void MissingRateTextClockPreservesSourceTrustFailureStateAndReason(
        FactAvailability state, FactAvailability expectedState, FactTrustLevel? trust, FactTrustLevel expectedTrust)
    {
        var timings = Current();
        timings.DataRateText = "";
        timings.DramFrequencyMHz = 1600;
        timings.RateFactSource = "missing-rate provider";
        timings.RateFactAvailability = state;
        timings.RateFactTrust = trust;
        timings.RateFactReason = "missing-rate fixture reason";
        string text = Invoke(timings);
        string rate = Assert.Single(text.Split('\n'), line => line.StartsWith("資料速率："));
        string clock = Assert.Single(text.Split('\n'), line => line.StartsWith("DRAM 時脈"));
        Assert.Contains("資料速率：未取得值", rate);
        Assert.Contains("1600 MHz", clock);
        Assert.Contains("保留內容（不得當成已確認目前值）", clock);
        foreach (string line in new[] { rate, clock })
        {
            Assert.Contains("來源：missing-rate provider", line);
            Assert.Contains($"可用性：{expectedState}（{FactStateLattice.Describe(expectedState)}）", line);
            Assert.Contains($"信任：{expectedTrust}（{HardwareSnapshotService.TrustText(expectedTrust)}）", line);
            Assert.Contains("missing-rate fixture reason", line);
            Assert.DoesNotContain("可用性：Present", line);
        }
        Assert.Contains("缺少對應資料速率文字的確認資訊", clock);
        Assert.Contains("時脈不是獨立量測", clock);
    }

    [Theory]
    [InlineData(true, false, FactTrustLevel.Reported, FactTrustLevel.Derived)]
    [InlineData(true, false, FactTrustLevel.Speculative, FactTrustLevel.Speculative)]
    [InlineData(false, true, FactTrustLevel.Reported, FactTrustLevel.Derived)]
    [InlineData(false, true, FactTrustLevel.Unknown, FactTrustLevel.Unknown)]
    public void MissingRateTextClockRetainsConservativeConfiguredAndReferenceTrust(
        bool configured, bool reference, FactTrustLevel trust, FactTrustLevel expectedTrust)
    {
        var timings = Current();
        timings.DataRateText = "";
        timings.DramFrequencyMHz = 1600;
        timings.RateIsConfigured = configured;
        timings.RateIsJedecReference = reference;
        timings.RateFactTrust = trust;
        timings.RateFactReason = "missing scoped rate reason";
        string prefix = reference ? "JEDEC 參考時脈" : "設定時脈換算";
        string clock = Assert.Single(Invoke(timings).Split('\n'), line => line.StartsWith(prefix));
        Assert.Contains($"信任：{expectedTrust}（{HardwareSnapshotService.TrustText(expectedTrust)}）", clock);
        Assert.Contains("Unknown（未確認）", clock);
        Assert.Contains("missing scoped rate reason", clock);
        Assert.Contains("缺少對應資料速率文字的確認資訊", clock);
    }

    [Fact]
    public void LegacyLoadedValuesWithoutMetadataDoNotBecomeConfirmedCurrentQuantities()
    {
        var timings = new MemoryTimings { Loaded = true, SourceText = "legacy source",
            CL = "16", TRCD = "18", TRP = "18", TRAS = "36", DramFrequencyMHz = 1600,
            DataRateText = "DDR4-3200" };
        string text = Invoke(timings);
        Assert.Contains("Unknown（未確認）", text);
        Assert.Contains("信任：Unknown（未知）", text);
        Assert.Contains("來源：legacy source", text);
        Assert.DoesNotContain("可用性：Present", text);
        Assert.Contains("不得當成已確認目前值", text);
    }

    [Fact]
    public void LegacyJedecClockDoesNotInventDerivedTrustWhenSourceTrustIsUnknown()
    {
        var timings = Jedec();
        timings.TimingFactTrust = timings.RateFactTrust = null;
        string text = Invoke(timings);
        string clock = Assert.Single(text.Split('\n'), line => line.StartsWith("JEDEC 參考時脈"));
        Assert.Contains("信任：Unknown（未知）", clock);
        Assert.Contains("Unknown（未確認）", clock);
        Assert.DoesNotContain("信任：Derived", clock);
    }

    [Fact]
    public void PartialPrimaryTimingRemainsUnknownAndOptionalFieldDoesNotBecomeAnIndependentSuccess()
    {
        var timings = Current();
        timings.TRCD = "—";
        string text = Invoke(timings);
        string timing = Assert.Single(text.Split('\n'), line => line.StartsWith("目前時序："));
        string trfc = Assert.Single(text.Split('\n'), line => line.StartsWith("tRFC"));
        Assert.Contains("Unknown", timing);
        Assert.Contains("不完整", timing);
        Assert.Contains("Unknown", trfc);
        Assert.Contains("不得當成已確認目前值", trfc);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidClockDoesNotCreateANumericFrequencyClaim(double clock)
    {
        var timings = Current();
        timings.DramFrequencyMHz = clock;
        string text = Invoke(timings);
        Assert.DoesNotContain(" MHz", text);
        Assert.Contains("DDR4-3600", text);
    }

    [Fact]
    public void ToolReadsLatestInjectedModelAndInventoryOnEveryInvocation()
    {
        var selected = Jedec();
        string inventory = "inventory first\n";
        var toolbox = new AiToolbox();
        AiToolboxBuilder.AddMemory(toolbox, () => selected, () => inventory);
        Assert.Contains("JEDEC 參考時脈", toolbox.Invoke("get_memory_detail", "{}"));
        selected = Wmi();
        inventory = "inventory second\n";
        string second = toolbox.Invoke("get_memory_detail", "{}");
        Assert.Contains("設定時脈換算", second);
        Assert.DoesNotContain("SPD bus0 JEDEC", second);
        Assert.DoesNotContain("inventory first", second);
        Assert.Contains("inventory second", second);
    }

    [Fact]
    public void MissingInputReportsUnknownWithoutInventingClockAndUnscopedUncoreIsExplicit()
    {
        var timings = new MemoryTimings { Status = "fixture not loaded", UncoreText = "2400 MHz" };
        string text = Invoke(timings);
        Assert.Contains("時序讀取狀態：fixture not loaded", text);
        Assert.Contains("目前時序：未取得值", text);
        Assert.Contains("資料速率：未取得值", text);
        Assert.DoesNotContain("DRAM 時脈", text);
        Assert.Contains("未提供此欄位的獨立來源與目前值確認", text);
        Assert.DoesNotContain("可用性：Present", text);
    }

    [Fact]
    public void UnloadedOptionalTimingFieldsRemainVisibleAsUnconfirmedContent()
    {
        var timings = new MemoryTimings { Loaded = false, TRFC = "560", CommandRate = "2T",
            SourceText = "partial source", TimingFactAvailability = FactAvailability.Present,
            TimingFactTrust = FactTrustLevel.Reported };
        string text = Invoke(timings);
        string trfc = Assert.Single(text.Split('\n'), line => line.StartsWith("tRFC"));
        string command = Assert.Single(text.Split('\n'), line => line.StartsWith("命令率"));
        Assert.Contains("560", trfc);
        Assert.Contains("2T", command);
        Assert.Contains("Unknown（未確認）", trfc);
        Assert.Contains("Unknown（未確認）", command);
        Assert.Contains("partial source", trfc);
    }

    [Fact]
    public void FormattingDoesNotMutateLoadedStatusOrProvenanceAndRejectsNull()
    {
        var timings = Jedec();
        string status = timings.Status;
        _ = AiToolboxBuilder.FormatMemoryTimingDescription(timings);
        Assert.True(timings.Loaded);
        Assert.Equal(status, timings.Status);
        Assert.Equal(FactAvailability.Present, timings.TimingFactAvailability);
        Assert.True(timings.IsJedecReference);
        Assert.Throws<ArgumentNullException>(() => AiToolboxBuilder.FormatMemoryTimingDescription(null!));
    }
}
