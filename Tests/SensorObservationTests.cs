using System.Diagnostics;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace XinSpect.Tests;

public sealed class SensorObservationTests
{
    private static SensorReading Reading(string id, double? value, FactAvailability state = FactAvailability.Present)
        => new(id, "gpu/0", "GPU", "Temperature", "溫度", "°C", value, state, FactTrustLevel.Reported,
            "Injected", DateTimeOffset.UtcNow, null, "來源更新時間未知");
    private static SensorFrame Frame(long sequence, params SensorReading[] readings)
        => new(sequence, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow, readings);
    private static void Add(SensorStatisticsService stats, SensorFrame frame)
        => stats.Add(frame, Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(5));

    [Fact]
    public void StatisticsUseEachDistinctFrameOnceAndSeparateIdenticalLabelsByIdentity()
    {
        var stats = new SensorStatisticsService();
        var first = Frame(1, Reading("gpu0/temp", 40), Reading("gpu1/temp", 80) with { DeviceId = "gpu/1" });
        Add(stats, first); Add(stats, first);
        Add(stats, Frame(2, Reading("gpu0/temp", 60), Reading("gpu1/temp", 100) with { DeviceId = "gpu/1" }));
        Assert.Equal(new SensorStatistics(2, 40, 60, 50, "°C", "gpu/0"), stats.Values["gpu0/temp"]);
        Assert.Equal(90, stats.Values["gpu1/temp"].Average);
    }

    [Fact]
    public void UnknownNonfiniteMissingStaleAndAmbiguousValuesNeverEnterStatistics()
    {
        var stats = new SensorStatisticsService();
        Add(stats, Frame(1, Reading("unknown", 1.3, FactAvailability.Unknown), Reading("nan", double.NaN),
            Reading("infinity", double.PositiveInfinity), Reading("missing", null),
            Reading("speculative", 70) with { Trust = FactTrustLevel.Speculative },
            Reading("duplicate", 10), Reading("duplicate", 50)));
        var stale = Frame(2, Reading("stale", 90));
        stats.Add(stale, stale.CapturedTimestamp + Stopwatch.Frequency * 6, TimeSpan.FromSeconds(5));
        Assert.Empty(stats.Values);
        Add(stats, Frame(3, Reading("real-zero", 0) with { Unit = "%" }));
        Assert.Equal(0, stats.Values["real-zero"].Average);
    }

    [Fact]
    public void ResetDoesNotReuseCurrentFrameAndChangedUnitsResetIncompatibleSeries()
    {
        var stats = new SensorStatisticsService();
        var first = Frame(1, Reading("s", 50));
        Add(stats, first); stats.Reset(1); Add(stats, first);
        Assert.Empty(stats.Values);
        Add(stats, Frame(2, Reading("s", 70)));
        Add(stats, Frame(3, Reading("s", 1.2) with { Unit = "V" }));
        Assert.Equal(new SensorStatistics(1, 1.2, 1.2, 1.2, "V", "gpu/0"), stats.Values["s"]);
        Add(stats, Frame(4, Reading("s", 100) with { Unit = "V", DeviceId = "gpu/1" }));
        Assert.Equal(1, stats.Values["s"].Count);
    }

    [Fact]
    public void FreshnessUsesMonotonicTimeAndRejectsFutureOrEmptyFrames()
    {
        var frame = Frame(1, Reading("s", 0));
        Assert.True(frame.IsFresh(frame.CapturedTimestamp, TimeSpan.FromSeconds(1)));
        Assert.False(frame.IsFresh(frame.CapturedTimestamp - 1, TimeSpan.FromSeconds(1)));
        Assert.False(frame.IsFresh(frame.CapturedTimestamp + Stopwatch.Frequency * 2, TimeSpan.FromSeconds(1)));
        Assert.False(SensorFrame.Empty.IsFresh(Stopwatch.GetTimestamp(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ProductionPollPublishesNumericIdentityAndFailureDoesNotAverageOldProviderCache()
    {
        float value = 40;
        bool fail = false;
        var sensor = Fake<ISensor>((method, _) => method.Name switch
        {
            "get_Name" => "GPU Core", "get_SensorType" => SensorType.Temperature,
            "get_Identifier" => new Identifier("gpu", "0", "temperature", "0"), "get_Value" => value,
            _ => null,
        });
        var hardware = Fake<IHardware>((method, _) => method.Name switch
        {
            "get_Name" => "GPU", "get_HardwareType" => HardwareType.GpuNvidia,
            "get_Identifier" => new Identifier("gpu", "0"), "get_Sensors" => new[] { sensor },
            "get_SubHardware" => Array.Empty<IHardware>(),
            "Update" => Run(() => { if (fail) throw new InvalidOperationException("Injected read failure"); }),
            _ => null,
        });
        IComputer computer = null!;
        computer = Fake<IComputer>((method, args) => method.Name switch
        {
            "get_Hardware" => new[] { hardware },
            "Accept" => Run(() => ((IVisitor)args![0]!).VisitComputer(computer)),
            "Traverse" => Run(() => ((IVisitor)args![0]!).VisitHardware(hardware)),
            _ => null,
        });
        using var service = new SensorService(computer, () => { }, () => { });
        var row = Assert.Single(service.AllSensors);
        Assert.Equal("lhm:/gpu/0/temperature/0", row.Id);
        Assert.Equal(1, row.SampleCount);
        value = 60; service.Refresh();
        var successfulFrame = service.CurrentSensorFrame;
        Assert.Equal(2, row.SampleCount);
        Assert.Equal("50 °C", row.AverageText);
        service.Publish(); Assert.Equal(2, row.SampleCount);
        fail = true;
        Assert.Throws<InvalidOperationException>(service.Poll);
        service.PublishSensorObservations();
        Assert.Null(row.NumericValue);
        Assert.Equal("—", row.ValueText);
        Assert.Equal(2, row.SampleCount);
        Assert.Equal(60, Assert.Single(successfulFrame.Readings).Value);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(service.CurrentSensorFrame.Readings).Availability);
        fail = false; service.ResetSensorStatistics(); service.Publish();
        Assert.Equal(0, row.SampleCount);
        value = 80; service.Refresh();
        Assert.Equal(1, row.SampleCount);
        Assert.Equal("80 °C", row.AverageText);
        service.Dispose();
        Assert.All(service.CurrentSensorFrame.Readings, r => Assert.False(r.IsUsable));
    }

    [Fact]
    public void GlobalTrustedRaplCannotApproveAnyLhmCpuPowerOrItsUnchangedCache()
    {
        // This sets only a cached test verdict, never invokes the diagnostic or any load.
        var field = typeof(RaplEnergyGate).GetField("_result", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? original = field.GetValue(null);
        try
        {
            field.SetValue(null, new RaplProbeResult(RaplGateVerdict.Trusted, "Injected unrelated counter success", 42));
            var sensors = new[]
            {
                TestSensor("CPU Package", SensorType.Power, new Identifier("intelcpu", "0", "power", "0"), () => 90),
                TestSensor("CPU Cores", SensorType.Power, new Identifier("intelcpu", "0", "power", "1"), () => 45),
                TestSensor("DRAM", SensorType.Power, new Identifier("intelcpu", "0", "power", "2"), () => 10),
            };
            var hardware = TestHardware(new Identifier("intelcpu", "0"), "CPU", sensors, () => { }, HardwareType.Cpu);
            using var service = new SensorService(TestComputer(hardware), () => { }, () => { });
            service.Refresh(); // Update returns normally but the provider retains all three prior values.
            Assert.Equal(3, service.AllSensors.Count);
            Assert.All(service.AllSensors, row =>
            {
                Assert.Equal(FactAvailability.Unknown, row.Reading!.Availability);
                Assert.NotNull(row.Reading.Value);
                Assert.Null(row.NumericValue);
                Assert.Equal(0, row.SampleCount);
                Assert.Contains("未綁定此感測器", row.Reading.Reason);
            });
        }
        finally { field.SetValue(null, original); }
    }

    [Fact]
    public void DuplicateSensorIdsRetainEachRowsOwnRawValueAndDeviceEvidence()
    {
        var duplicate = new Identifier("duplicate", "temperature", "0");
        var one = TestHardware(new Identifier("gpu", "0"), "GPU one",
            [TestSensor("GPU Core", SensorType.Temperature, duplicate, () => 40)], () => { });
        var two = TestHardware(new Identifier("gpu", "1"), "GPU two",
            [TestSensor("GPU Core", SensorType.Temperature, duplicate, () => 80)], () => { });
        using var service = new SensorService(TestComputer(one, two), () => { }, () => { });
        Assert.Equal(2, service.AllSensors.Count);
        var first = service.AllSensors[0]; var second = service.AllSensors[1];
        Assert.Equal(40, first.Reading!.Value); Assert.Equal(80, second.Reading!.Value);
        Assert.Equal("40 °C（未確認）", first.ValueText);
        Assert.Equal("80 °C（未確認）", second.ValueText);
        Assert.All(service.AllSensors, row =>
        {
            Assert.Equal(row.DeviceId, row.Reading!.DeviceId);
            Assert.Equal(row.DeviceName, row.Reading.DeviceName);
            Assert.Equal(FactAvailability.Unknown, row.Reading.Availability);
            Assert.Null(row.NumericValue); Assert.Equal(0, row.SampleCount);
            Assert.Contains("識別碼重複", row.Reading.Reason);
        });
    }

    [Fact]
    public void SlowTailProviderCannotRenewEarlierSensorFreshnessOrBackdateReadAtUtc()
    {
        long now = Stopwatch.GetTimestamp();
        bool slow = false;
        var one = TestHardware(new Identifier("gpu", "0"), "GPU one",
            [TestSensor("GPU Core", SensorType.Temperature, new Identifier("gpu", "0", "temperature", "0"), () => 40)], () => { });
        var two = TestHardware(new Identifier("gpu", "1"), "GPU two",
            [TestSensor("GPU Core", SensorType.Temperature, new Identifier("gpu", "1", "temperature", "0"), () => 80)],
            () => { if (slow) now += 35 * Stopwatch.Frequency; });
        using var service = new SensorService(TestComputer(one, two), () => { }, () => { }, () => now);
        Assert.All(service.AllSensors, row => Assert.Equal(1, row.SampleCount));
        now += Stopwatch.Frequency;
        long started = now;
        var beforeRead = DateTimeOffset.UtcNow;
        slow = true; service.Refresh();
        var afterRead = DateTimeOffset.UtcNow;
        var frame = service.CurrentSensorFrame;
        Assert.Equal(started, frame.CapturedTimestamp);
        Assert.False(frame.IsFresh(now, TimeSpan.FromSeconds(30)));
        Assert.InRange(frame.ReadAtUtc, beforeRead, afterRead);
        Assert.All(service.AllSensors, row => Assert.Equal(1, row.SampleCount));
        Assert.All(frame.Readings, reading =>
        {
            Assert.Equal(frame.ReadAtUtc, reading.ReadAtUtc);
            Assert.Null(reading.SourceUpdatedAtUtc);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrDuplicateIdentityNeverOverwritesProviderReadError(bool missingIdentity)
    {
        bool fail = false;
        Identifier? id = missingIdentity ? null : new Identifier("duplicate", "temperature", "0");
        var hardware = TestHardware(new Identifier("gpu", "0"), "GPU",
            [TestSensor("GPU Core", SensorType.Temperature, id, () => 40),
                TestSensor("GPU Hot Spot", SensorType.Temperature, id, () => 80)],
            () => { if (fail) throw new InvalidOperationException("Injected poll failure"); });
        using var service = new SensorService(TestComputer(hardware), () => { }, () => { });
        fail = true;
        Assert.Throws<InvalidOperationException>(service.Poll);
        service.PublishSensorObservations();
        Assert.All(service.AllSensors, row =>
        {
            Assert.Equal(FactAvailability.ReadError, row.Reading!.Availability);
            Assert.Null(row.Reading.Value); Assert.Null(row.NumericValue);
            Assert.Contains("provider 輪詢失敗", row.Reading.Reason);
            Assert.Contains(missingIdentity ? "識別碼不可讀" : "識別碼重複", row.Reading.Reason);
        });
    }

    [Fact]
    public void MissingIdentityDiagnosticAlsoSurvivesAnIndividualValueGetterFailure()
    {
        bool fail = false;
        var hardware = TestHardware(new Identifier("gpu", "0"), "GPU",
            [TestSensor("GPU Core", SensorType.Temperature, null,
                () => fail ? throw new InvalidOperationException("Injected Value getter failure") : 40)], () => { });
        using var service = new SensorService(TestComputer(hardware), () => { }, () => { });
        fail = true; service.Poll(); service.PublishSensorObservations();
        var reading = Assert.Single(service.CurrentSensorFrame.Readings);
        Assert.Equal(FactAvailability.ReadError, reading.Availability);
        Assert.Null(reading.Value);
        Assert.Contains("來源讀取失敗：InvalidOperationException", reading.Reason);
        Assert.Contains("識別碼不可讀", reading.Reason);
    }

    [Fact]
    public void SelectionsPersistOnlyStableBoundedDistinctIdsWithoutWritingTestUserSettings()
    {
        var settings = new SettingsService(false);
        settings.SetSensorLogSelection(["lhm:/gpu/0/temp/0", "lhm:/gpu/0/temp/0", "session/temporary", "", new string('x', 513)]);
        Assert.Equal(new[] { "lhm:/gpu/0/temp/0" }, settings.SensorLogSelection);
    }

    private static ISensor TestSensor(string name, SensorType type, Identifier? id, Func<float?> value)
        => Fake<ISensor>((method, _) => method.Name switch
        {
            "get_Name" => name, "get_SensorType" => type, "get_Identifier" => id, "get_Value" => value(), _ => null,
        });

    private static IHardware TestHardware(Identifier id, string name, ISensor[] sensors, Action update,
        HardwareType type = HardwareType.GpuNvidia)
        => Fake<IHardware>((method, _) => method.Name switch
        {
            "get_Name" => name, "get_HardwareType" => type, "get_Identifier" => id,
            "get_Sensors" => sensors, "get_SubHardware" => Array.Empty<IHardware>(), "Update" => Run(update), _ => null,
        });

    private static IComputer TestComputer(params IHardware[] hardware)
    {
        IComputer computer = null!;
        computer = Fake<IComputer>((method, args) => method.Name switch
        {
            "get_Hardware" => hardware,
            "Accept" => Run(() => ((IVisitor)args![0]!).VisitComputer(computer)),
            "Traverse" => Run(() => { foreach (var device in hardware) ((IVisitor)args![0]!).VisitHardware(device); }),
            _ => null,
        });
        return computer;
    }

    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, ObservationProxy>();
        ((ObservationProxy)(object)proxy).Handler = invoke;
        return proxy;
    }
    private static object? Run(Action action) { action(); return null; }
    public class ObservationProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
