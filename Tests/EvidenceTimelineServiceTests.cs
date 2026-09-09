using System.IO;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

public sealed class EvidenceTimelineServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "XinSpectEvidence_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private EvidenceTimelineService NewStore() => new(_directory);

    private static EvidenceSample Sample(
        string metric, decimal value, DateTimeOffset time,
        EvidenceMetricSemantics semantics = EvidenceMetricSemantics.Gauge,
        string unit = "count", string device = "nvme:serial-001",
        EvidenceValueRole role = EvidenceValueRole.Observed,
        int? counterBits = null) => new()
    {
        Category = "storage",
        DeviceKey = device,
        Metric = metric,
        Value = value,
        Unit = unit,
        Source = "NVMe log page 0x02",
        Trust = EvidenceTrustLevel.DeviceReported,
        TimeUtc = time,
        Semantics = semantics,
        Role = role,
        CounterBits = counterBits,
        Context = new() { ["controller"] = "0" },
    };

    [Fact]
    public void Ingest_AppendsVersionedJsonLines_AndRoundTripsAllEvidence()
    {
        var store = NewStore();
        var time = new DateTimeOffset(2026, 9, 8, 1, 2, 3, TimeSpan.FromHours(8));
        store.Ingest(Sample("nvme.media_errors", 12, time,
            EvidenceMetricSemantics.MonotonicCounter));
        store.Ingest(Sample("nvme.error_log_entries", 34, time.AddMinutes(1),
            EvidenceMetricSemantics.MonotonicCounter));

        string[] lines = File.ReadAllLines(store.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Contains("\"schemaVersion\":1", line));
        Assert.All(lines, line => Assert.Contains("\"recordType\":\"sample\"", line));

        var loaded = NewStore().Query();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(time.ToUniversalTime(), loaded[0].TimeUtc);
        Assert.Equal("nvme:serial-001", loaded[0].DeviceKey);
        Assert.Equal("count", loaded[0].Unit);
        Assert.Equal("NVMe log page 0x02", loaded[0].Source);
        Assert.Equal(EvidenceTrustLevel.DeviceReported, loaded[0].Trust);
        Assert.Equal("0", loaded[0].Context["controller"]);
    }

    [Fact]
    public void Query_FiltersInclusiveRangeAndStableSeriesKeys()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        store.Ingest(Sample("nvme.media_errors", 1, t0));
        store.Ingest(Sample("nvme.media_errors", 2, t0.AddMinutes(1), device: "nvme:other"));
        store.Ingest(Sample("nvme.data_units_written", 3, t0.AddMinutes(2)));
        store.Ingest(Sample("nvme.media_errors", 4, t0.AddMinutes(3)));

        var result = store.Query(new EvidenceQuery
        {
            FromUtc = t0,
            ToUtc = t0.AddMinutes(3),
            Category = "STORAGE",
            DeviceKey = "NVME:SERIAL-001",
            Metric = "NVME.MEDIA_ERRORS",
        });

        Assert.Equal(2, result.Count);
        Assert.Equal(new decimal[] { 1, 4 }, result.Select(s => s.Value));
    }

    [Fact]
    public void Query_MaxPoints_PreservesFirstAndLast()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        for (int i = 0; i < 10; i++) store.Ingest(Sample("temperature", i, t0.AddSeconds(i)));

        var result = store.Query(new EvidenceQuery { MaxPoints = 4 });
        Assert.Equal(4, result.Count);
        Assert.Equal(0, result[0].Value);
        Assert.Equal(9, result[^1].Value);
    }

    [Fact]
    public void Downsample_PreservesBucketEnvelopeAndEndpoints()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        var samples = Enumerable.Range(0, 10)
            .Select(i => Sample("temperature", i, t0.AddSeconds(i))).ToArray();

        var buckets = EvidenceTimelineService.Downsample(samples, 3);

        Assert.Equal(3, buckets.Count);
        Assert.Equal(3, buckets[0].SampleCount);
        Assert.Equal(0, buckets[0].First);
        Assert.Equal(2, buckets[0].Last);
        Assert.Equal(0, buckets[0].Min);
        Assert.Equal(2, buckets[0].Max);
        Assert.Equal(1, buckets[0].Average);
        Assert.Equal(9, buckets[^1].Last);
    }

    [Fact]
    public void AnalyzeDelta_ReportsContinuousFirstToLastDelta()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        EvidenceSample[] samples =
        [
            Sample("nvme.data_units_written", 100, t0,
                EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.data_units_written", 145, t0.AddMinutes(1),
                EvidenceMetricSemantics.MonotonicCounter),
        ];

        var delta = EvidenceTimelineService.AnalyzeDelta(samples, TimeSpan.FromMinutes(1));
        Assert.NotNull(delta);
        Assert.Equal(45, delta.Delta);
        Assert.True(delta.IsContinuous);
        Assert.Same(samples[0], delta.First);
        Assert.Same(samples[1], delta.Last);
    }

    [Fact]
    public void AnalyzeDelta_DetectsCounterResetAndWithholdsDelta()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        EvidenceSample[] samples =
        [
            Sample("nvme.media_errors", 80, t0, EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.media_errors", 2, t0.AddMinutes(1), EvidenceMetricSemantics.MonotonicCounter),
        ];

        var delta = EvidenceTimelineService.AnalyzeDelta(samples);
        Assert.Null(delta!.Delta);
        Assert.Contains(delta.Discontinuities,
            d => d.Kind == EvidenceDiscontinuityKind.CounterReset);
    }

    [Fact]
    public void AnalyzeDelta_DetectsCounterWrapOnlyWhenWidthAndBoundarySupportIt()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        EvidenceSample[] samples =
        [
            Sample("device.counter", 250, t0, EvidenceMetricSemantics.MonotonicCounter,
                counterBits: 8),
            Sample("device.counter", 3, t0.AddSeconds(1), EvidenceMetricSemantics.MonotonicCounter,
                counterBits: 8),
        ];

        var delta = EvidenceTimelineService.AnalyzeDelta(samples);
        Assert.Null(delta!.Delta);
        Assert.Single(delta.Discontinuities);
        Assert.Equal(EvidenceDiscontinuityKind.CounterWrap, delta.Discontinuities[0].Kind);
    }

    [Fact]
    public void AnalyzeDelta_DetectsSamplingGapAndDoesNotBridgeIt()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        EvidenceSample[] samples =
        [
            Sample("nvme.error_log_entries", 10, t0, EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.error_log_entries", 20, t0.AddMinutes(10), EvidenceMetricSemantics.MonotonicCounter),
        ];

        var delta = EvidenceTimelineService.AnalyzeDelta(samples, TimeSpan.FromMinutes(1));
        Assert.Null(delta!.Delta);
        Assert.Equal(EvidenceDiscontinuityKind.Gap, Assert.Single(delta.Discontinuities).Kind);
    }

    [Fact]
    public void AnalyzeDelta_RejectsMixedSeries()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        Assert.Throws<ArgumentException>(() => EvidenceTimelineService.AnalyzeDelta(
        [
            Sample("nvme.media_errors", 1, t0),
            Sample("nvme.error_log_entries", 2, t0.AddSeconds(1)),
        ]));
    }

    [Fact]
    public void GenerateEvents_CoversLinkDegradationAndNvmeObjectiveIncreases()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        var samples = new[]
        {
            Sample("pcie.current_width", 16, t0, EvidenceMetricSemantics.LinkWidth,
                unit: "lanes", device: "pci:0000:01:00.0"),
            Sample("pcie.current_width", 8, t0.AddMinutes(1), EvidenceMetricSemantics.LinkWidth,
                unit: "lanes", device: "pci:0000:01:00.0"),
            Sample("pcie.current_speed", 16, t0, EvidenceMetricSemantics.LinkSpeed,
                unit: "GT/s", device: "pci:0000:01:00.0"),
            Sample("pcie.current_speed", 8, t0.AddMinutes(1), EvidenceMetricSemantics.LinkSpeed,
                unit: "GT/s", device: "pci:0000:01:00.0"),
            Sample("nvme.media_errors", 4, t0, EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.media_errors", 7, t0.AddMinutes(1), EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.error_log_entries", 10, t0, EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.error_log_entries", 12, t0.AddMinutes(1), EvidenceMetricSemantics.MonotonicCounter),
            Sample("nvme.data_units_written", 100, t0, EvidenceMetricSemantics.MonotonicCounter,
                unit: "data units"),
            Sample("nvme.data_units_written", 125, t0.AddMinutes(1), EvidenceMetricSemantics.MonotonicCounter,
                unit: "data units"),
            Sample("nvme.percentage_used", 2, t0, unit: "%"),
            Sample("nvme.percentage_used", 3, t0.AddMinutes(1), unit: "%"),
        };

        var events = EvidenceTimelineService.GenerateEvents(samples);
        Assert.Equal(2, events.Count(e => e.Kind == EvidenceEventKind.LinkValueDecreased));
        Assert.Equal(3, events.Count(e => e.Kind == EvidenceEventKind.CounterIncreased));
        Assert.Single(events, e => e.Kind == EvidenceEventKind.PercentageIncreased);
        Assert.Contains(events, e => e.Metric == "nvme.data_units_written" && e.Delta == 25);
    }

    [Fact]
    public void GenerateEvents_DoesNotTreatConfiguredFrequencyLimitAsObservedPState()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        var samples = new[]
        {
            Sample("cpu.frequency_limit", 4200, t0, EvidenceMetricSemantics.Ratio,
                unit: "MHz", role: EvidenceValueRole.ConfiguredLimit),
            Sample("cpu.frequency_limit", 3000, t0.AddSeconds(1), EvidenceMetricSemantics.Ratio,
                unit: "MHz", role: EvidenceValueRole.ConfiguredLimit),
        };

        Assert.Empty(EvidenceTimelineService.GenerateEvents(samples));
    }

    [Fact]
    public void GenerateEvents_ReportsEffectiveRatioAndCStateResidencyWithoutInference()
    {
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        var samples = new[]
        {
            Sample("cpu.aperf_mperf_ratio", 0.8m, t0, EvidenceMetricSemantics.Ratio,
                unit: "ratio", role: EvidenceValueRole.DerivedMeasurement),
            Sample("cpu.aperf_mperf_ratio", 1.1m, t0.AddSeconds(1), EvidenceMetricSemantics.Ratio,
                unit: "ratio", role: EvidenceValueRole.DerivedMeasurement),
            Sample("cpu.package_c6_residency", 40, t0, EvidenceMetricSemantics.ResidencyPercent,
                unit: "%"),
            Sample("cpu.package_c6_residency", 55, t0.AddSeconds(1), EvidenceMetricSemantics.ResidencyPercent,
                unit: "%"),
        };

        var events = EvidenceTimelineService.GenerateEvents(samples);
        Assert.Single(events, e => e.Kind == EvidenceEventKind.EffectiveRatioObserved);
        Assert.Single(events, e => e.Kind == EvidenceEventKind.ResidencyObserved);
    }

    [Theory]
    [InlineData(100, 250, 200, 300, 1.5)]
    [InlineData(250, 100, 200, 300, null)]
    [InlineData(100, 250, 300, 300, null)]
    public void ComputeEffectiveRatio_UsesCounterDeltasOnly(
        double aperf0, double aperf1, double mperf0, double mperf1, double? expected)
    {
        decimal? actual = EvidenceTimelineService.ComputeEffectiveRatio(
            (decimal)aperf0, (decimal)aperf1, (decimal)mperf0, (decimal)mperf1);
        Assert.Equal(expected is null ? null : (decimal?)expected.Value, actual);
    }

    [Fact]
    public void Query_RepairsInterruptedTailBeforeLaterAppend()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        store.Ingest(Sample("nvme.media_errors", 1, t0));
        File.AppendAllText(store.FilePath, "{\"schemaVersion\":1,\"recordType\":\"sample\"",
            new UTF8Encoding(false));

        Assert.Single(store.Query());

        store.Ingest(Sample("nvme.media_errors", 2, t0.AddMinutes(1)));
        var result = store.Query();
        Assert.Equal(new decimal[] { 1, 2 }, result.Select(s => s.Value));
    }

    [Fact]
    public void Query_SkipsCorruptCompleteLineAndUnknownFutureSchema()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        store.Ingest(Sample("nvme.media_errors", 1, t0));
        File.AppendAllText(store.FilePath,
            "not-json\n{\"schemaVersion\":999,\"recordType\":\"sample\",\"sample\":{}}\n",
            new UTF8Encoding(false));
        store.Ingest(Sample("nvme.media_errors", 2, t0.AddMinutes(1)));

        Assert.Equal(new decimal[] { 1, 2 }, store.Query().Select(s => s.Value));
    }

    [Fact]
    public async Task ConcurrentIngest_WritesWholeParseableLines()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        Task[] writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
                store.Ingest(Sample("worker.counter", worker * 25 + i,
                    t0.AddMilliseconds(worker * 25 + i), device: $"worker:{worker}"));
        })).ToArray();

        await Task.WhenAll(writers);

        Assert.Equal(200, store.Query().Count);
        Assert.Equal(200, File.ReadAllLines(store.FilePath).Length);
    }

    [Fact]
    public void Constructor_RejectsAnUnrelatedExistingFileInsteadOfAppendingIntoIt()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, EvidenceTimelineService.DefaultFileName);
        File.WriteAllText(path, "this belongs to another format", new UTF8Encoding(false));

        Assert.Throws<InvalidDataException>(() => NewStore());
        Assert.Equal("this belongs to another format", File.ReadAllText(path));
    }

    [Fact]
    public void BatchIngest_ValidatesEverythingBeforeWriting()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        var valid = Sample("percentage_used", 1, t0);
        var invalid = Sample("counter", 2, t0.AddSeconds(1), counterBits: 8);

        Assert.Throws<ArgumentException>(() => store.Ingest([valid, invalid]));
        Assert.Empty(store.Query());
    }

    [Fact]
    public void CounterBits_AcceptsNvme128BitDeclaration()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        store.Ingest(Sample("nvme.media_errors", 1, t0,
            EvidenceMetricSemantics.MonotonicCounter, counterBits: 128));
        Assert.Single(store.Query());
    }

    [Fact]
    public void Ingest_RejectsInvalidIdentityAndCounterMetadata()
    {
        var store = NewStore();
        var t0 = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
        Assert.Throws<ArgumentException>(() => store.Ingest(
            Sample(" ", 1, t0)));
        Assert.Throws<ArgumentException>(() => store.Ingest(
            Sample("temperature", 1, t0, counterBits: 8)));
    }
}
