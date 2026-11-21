using System.IO.MemoryMappedFiles;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace XinSpect.Tests;

public sealed class SensorCrossCheckTests
{
    [Fact]
    public void CpuPackagePowerDisplayUsesPlausibleLiveLhmReadingWithoutApprovingAnalysisPower()
    {
        Assert.False(RaplEnergyGate.IsProbing);
        float power = 81;
        var sensor = DispatchProxy.Create<ISensor, HardwareProxy>();
        ((HardwareProxy)(object)sensor).InvokeMethod = (method, _) => method.Name switch
        {
            "get_Name" => "CPU Package",
            "get_SensorType" => SensorType.Power,
            "get_Value" => power,
            _ => throw new InvalidOperationException("Unexpected fake sensor call: " + method.Name),
        };
        var cpu = FakeHardware("CPU X", () => { }, [sensor]);
        var computer = FakeComputer(cpu);
        using var service = new SensorService(computer, () => { }, () => { });

        Assert.Equal(81, service.CpuPackagePowerSensorW);
        Assert.Equal("81 W", service.CpuPackagePowerSensorText);
        Assert.Contains("LibreHardwareMonitor「CPU Package」", service.CpuPowerStateText);
        Assert.Contains("RAPL", service.CpuPowerStateText);
        Assert.Null(service.CpuPowerW);
        Assert.False(RaplEnergyGate.IsProbing);

        power = 99_999;
        service.Poll();
        service.Publish();
        Assert.Null(service.CpuPackagePowerSensorW);
        Assert.Equal("—", service.CpuPackagePowerSensorText);
        Assert.Contains("未回有效讀值", service.CpuPowerStateText);
        Assert.False(RaplEnergyGate.IsProbing);
    }

    [Fact]
    public void CpuPackagePowerDisplayExplainsMissingLhmSensor()
    {
        using var service = new SensorService(FakeComputer(FakeHardware("CPU X", () => { })),
            () => { }, () => { });

        Assert.Equal("—", service.CpuPackagePowerSensorText);
        Assert.Null(service.CpuPackagePowerSensorW);
        Assert.Contains("未提供 CPU Package／Package 功耗感測器", service.CpuPowerStateText);
    }

    [Fact]
    public void CpuAcquisitionCallbackCompletesBeforeLaterDeviceUpdate()
    {
        long clock = 10;
        var calls = new List<string>();
        SensorAcquisition? cpuAcquisition = null;
        var cpu = FakeHardware("cpu", () => { calls.Add("cpu update"); clock = 20; });
        var later = FakeHardware("later", () => { calls.Add("later update"); clock = 5000; });
        var visitor = new UpdateVisitor((hardware, acquisition) =>
        {
            calls.Add(hardware.Name + " callback");
            if (ReferenceEquals(hardware, cpu)) cpuAcquisition = acquisition;
        }, () => clock);

        visitor.VisitHardware(cpu);
        visitor.VisitHardware(later);

        Assert.Equal(new[] { "cpu update", "cpu callback", "later update", "later callback" }, calls);
        Assert.Equal(new SensorAcquisition(10, 20), cpuAcquisition);
    }

    [Fact]
    public void FailedTraversalClearsCpuSnapshotAndDoesNotMutateEarlierCapture()
    {
        float temperature = 60;
        bool failLater = false, closed = false;
        var sensor = DispatchProxy.Create<ISensor, HardwareProxy>();
        ((HardwareProxy)(object)sensor).InvokeMethod = (method, _) => method.Name switch
        {
            "get_Name" => "CPU Core #1",
            "get_SensorType" => SensorType.Temperature,
            "get_Value" => temperature,
            _ => throw new InvalidOperationException("Unexpected fake sensor call: " + method.Name),
        };
        var cpu = FakeHardware("CPU X", () => { }, [sensor]);
        var later = FakeHardware("later", () =>
        {
            if (failLater) throw new InvalidOperationException("Later device failed.");
        }, type: HardwareType.Memory);
        var computer = DispatchProxy.Create<IComputer, HardwareProxy>();
        ((HardwareProxy)(object)computer).InvokeMethod = (method, args) => method.Name switch
        {
            "get_Hardware" => new[] { cpu, later },
            "Accept" => RunUpdate(() => ((IVisitor)args![0]!).VisitComputer(computer)),
            "Traverse" => RunUpdate(() =>
            {
                var visitor = (IVisitor)args![0]!;
                visitor.VisitHardware(cpu);
                visitor.VisitHardware(later);
            }),
            _ => throw new InvalidOperationException("Unexpected fake computer call: " + method.Name),
        };
        using (var service = new SensorService(computer, () => { }, () => closed = true))
        {
            var original = service.CaptureCoreTemperatureSamples();
            Assert.Equal(60, Assert.Single(original).Celsius);
            temperature = 90;
            Assert.Equal(60, Assert.Single(service.CaptureCoreTemperatureSamples()).Celsius);
            failLater = true;
            Assert.Throws<InvalidOperationException>(service.Poll);
            Assert.Empty(service.CaptureCoreTemperatureSamples());
            Assert.Equal(60, Assert.Single(original).Celsius);
        }
        Assert.True(closed);
    }

    private static IHardware FakeHardware(string name, Action update, ISensor[]? sensors = null,
        HardwareType type = HardwareType.Cpu)
    {
        var hardware = DispatchProxy.Create<IHardware, HardwareProxy>();
        ((HardwareProxy)(object)hardware).InvokeMethod = (method, _) => method.Name switch
        {
            "get_Name" => name,
            "get_HardwareType" => type,
            "get_Identifier" => new Identifier("intelcpu", "0"),
            "get_Sensors" => sensors ?? Array.Empty<ISensor>(),
            "get_SubHardware" => Array.Empty<IHardware>(),
            "Update" => RunUpdate(update),
            _ => throw new InvalidOperationException("Unexpected fake hardware call: " + method.Name),
        };
        return hardware;
    }

    private static IComputer FakeComputer(params IHardware[] hardware)
    {
        var computer = DispatchProxy.Create<IComputer, HardwareProxy>();
        ((HardwareProxy)(object)computer).InvokeMethod = (method, args) => method.Name switch
        {
            "get_Hardware" => hardware,
            "Accept" => RunUpdate(() => ((IVisitor)args![0]!).VisitComputer(computer)),
            "Traverse" => RunUpdate(() =>
            {
                var visitor = (IVisitor)args![0]!;
                foreach (var item in hardware) visitor.VisitHardware(item);
            }),
            _ => throw new InvalidOperationException("Unexpected fake computer call: " + method.Name),
        };
        return computer;
    }

    private static object? RunUpdate(Action update) { update(); return null; }

    public class HardwareProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> InvokeMethod { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => InvokeMethod(targetMethod!, args);
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 4, 0, 0, TimeSpan.Zero);

    private static SensorAcquisition Acquisition(int secondsAgo = 0)
        => new((20L - secondsAgo) * Stopwatch.Frequency, (20L - secondsAgo) * Stopwatch.Frequency);

    private static CoreTemperatureSample Lhm(string device = "CPU X", int core = 0, double c = 60,
        int secondsAgo = 0)
        => new("LHM", device, core, c, Now.AddSeconds(-secondsAgo), Acquisition(secondsAgo));

    private static ExternalReading CoreTemp(string device = "CPU X", int core = 0, double value = 62,
        string type = "Temperature", string unit = "°C", int secondsAgo = 1)
        => new("Core Temp", type, $"{device} Core #{core}", value, unit,
            DeviceIdentity: device, ChannelIndex: core, CapturedAtUtc: Now.AddSeconds(-secondsAgo),
            Acquisition: Acquisition(secondsAgo));

    [Fact]
    public void WallClockCorrectionsCannotHideOrCreateAcquisitionGaps()
    {
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm(secondsAgo: 10) with { CapturedAtUtc = Now }], [CoreTemp(secondsAgo: 0)]));
        Assert.Single(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm() with { CapturedAtUtc = Now.AddHours(-1) }], [CoreTemp()]));
    }

    [Fact]
    public void MissingOrInvalidMonotonicBoundsCannotBeCompared()
    {
        foreach (var window in new SensorAcquisition?[] { null, new(-1, 0), new(20, 10) })
        {
            Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
                [Lhm() with { Acquisition = window }], [CoreTemp()]));
            Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
                [Lhm()], [CoreTemp() with { Acquisition = window }]));
        }
    }

    [Fact]
    public void SlowAcquisitionCannotBeStampedFreshAtCompletion()
    {
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm() with { Acquisition = new(15L * Stopwatch.Frequency, 20L * Stopwatch.Frequency) }],
            [CoreTemp(secondsAgo: 0)]));
    }

    [Fact]
    public void MaximumMonotonicValuesDoNotOverflowSubtraction()
    {
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm() with { Acquisition = new(0, 0) }],
            [CoreTemp() with { Acquisition = new(long.MaxValue, long.MaxValue) }]));
    }

    [Fact]
    public void 同裝置同核心且三秒內唯一才配對()
    {
        Assert.Equal(0, SensorCrossCheckService.LhmCoreIndex("CPU Core #1"));
        Assert.Equal(7, SensorCrossCheckService.LhmCoreIndex("CPU Core #8"));
        Assert.Equal(0, SensorCrossCheckService.LhmCoreIndex("CPU Core"));
        var result = SensorCrossCheckService.CompareCoreTemperatures([Lhm()], [CoreTemp()]);
        var pair = Assert.Single(result);
        Assert.Equal(60, pair.PrimaryCelsius);
        Assert.Equal(62, pair.ExternalCelsius);
        Assert.Equal(2, pair.DifferenceCelsius);
    }

    [Fact]
    public void 裝置或核心不同不得配對()
    {
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures([Lhm()], [CoreTemp(device: "CPU Y")]));
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures([Lhm()], [CoreTemp(core: 1)]));
    }

    [Fact]
    public void 舊樣本不得配對()
    {
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures([Lhm(secondsAgo: 4)], [CoreTemp(secondsAgo: 0)]));
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm() with { CapturedAtUtc = default }], [CoreTemp() with { CapturedAtUtc = default(DateTimeOffset) }]));
    }

    [Fact]
    public void 重複裝置核心鍵視為歧義而拒配()
    {
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm(), Lhm(c: 61)], [CoreTemp()]));
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm()], [CoreTemp(), CoreTemp(value: 63)]));
    }

    [Fact]
    public void TjMax差值與非CoreTemp來源不參與()
    {
        Assert.Equal(-1, SensorCrossCheckService.LhmCoreIndex("CPU Core #1 Distance to TjMax"));
        Assert.Equal(-1, SensorCrossCheckService.LhmCoreIndex("P-Core #1"));
        Assert.Equal(-1, SensorCrossCheckService.LhmCoreIndex("E-Core #1"));
        Assert.Equal(-1, SensorCrossCheckService.LhmCoreIndex("CPU Core #0"));
        Assert.Equal(-1, SensorCrossCheckService.LhmCoreIndex("CPU Core #99999999999"));
        Assert.Equal(-1, SensorCrossCheckService.LhmCoreIndex(null));
        var delta = CoreTemp(type: "TemperatureDelta");
        var other = CoreTemp() with { Source = "HWiNFO" };
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures([Lhm()], [delta, other]));
    }

    [Fact]
    public void 華氏轉攝氏後比較()
    {
        var result = SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm(c: 37.7777778)], [CoreTemp(value: 100, unit: "°F")]);
        Assert.Equal(0, Assert.Single(result).DifferenceCelsius, 5);
    }

    [Fact]
    public void 官方Sdk雙Cpu快照讀四核心與兩筆處理器TjMax()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, 2686);
        using var snapshot = mapping.CreateViewAccessor(0, 2686);
        WriteSdkSnapshot(snapshot, coresPerCpu: 2, cpus: 2);

        Assert.True(CoreTempSharedMem.TryRead(out var readings, out string? error, snapshot), error);
        var temps = readings.Where(r => r.SensorType == "Temperature" && !r.Label.Contains("TjMax")).ToArray();
        Assert.Equal(new double[] { 60, 61, 62, 63 }, temps.Select(r => r.Value));
        Assert.EndsWith("CPU #1 核心 #1", temps[3].Label);
        Assert.All(temps, r => { Assert.Null(r.DeviceIdentity); Assert.Null(r.ChannelIndex); });
        var tjMax = readings.Where(r => r.Label.Contains("TjMax")).ToArray();
        Assert.Equal(new double[] { 100, 105 }, tjMax.Select(r => r.Value));
        Assert.EndsWith("CPU #1 TjMax", tjMax[1].Label);
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm() with { CapturedAtUtc = temps[0].CapturedAtUtc!.Value }], readings));
        Assert.True(snapshot.CanRead); // 注入的快照仍由呼叫者管理。
    }

    [Fact]
    public void 官方Sdk單Cpu差值旗標不可當絕對溫度互配()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, 2686);
        using var snapshot = mapping.CreateViewAccessor(0, 2686);
        WriteSdkSnapshot(snapshot, coresPerCpu: 2, cpus: 1);
        snapshot.Write(2684, (byte)1);
        snapshot.Write(2685, (byte)1);
        snapshot.Write(1024, 212u);

        Assert.True(CoreTempSharedMem.TryRead(out var readings, out string? error, snapshot), error);
        var temps = readings.Where(r => r.SensorType == "TemperatureDelta").ToArray();
        Assert.Equal(new int?[] { 0, 1 }, temps.Select(r => r.ChannelIndex));
        Assert.All(temps, r => { Assert.Equal("°F", r.Unit); Assert.Equal("CPU X", r.DeviceIdentity); });
        var limit = Assert.Single(readings, r => r.SensorType == "Temperature" && r.Label.EndsWith("TjMax"));
        Assert.Equal(212, limit.Value);
        Assert.Equal("°F", limit.Unit);
        Assert.Empty(SensorCrossCheckService.CompareCoreTemperatures(
            [Lhm() with { CapturedAtUtc = temps[0].CapturedAtUtc!.Value }], readings));
    }

    [Fact]
    public void 官方Sdk畸形拓撲與短快照拒絕且沒有部分結果()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, 2686);
        using var snapshot = mapping.CreateViewAccessor(0, 2686);
        foreach (var (cores, cpus) in new (uint, uint)[]
                 { (0, 1), (1, 0), (257, 1), (256, 2), (1, 129), (uint.MaxValue, uint.MaxValue) })
        {
            WriteSdkSnapshot(snapshot, cores, cpus);
            Assert.False(CoreTempSharedMem.TryRead(out var readings, out string? error, snapshot));
            Assert.Empty(readings);
            Assert.Contains("核心數異常", error);
        }
        using var shortSnapshot = mapping.CreateViewAccessor(0, 64);
        Assert.False(CoreTempSharedMem.TryRead(out var shortReadings, out string? shortError, shortSnapshot));
        Assert.Empty(shortReadings);
        Assert.Contains("區段過小", shortError);
    }

    [Fact]
    public void 備援先收尾端TjMax再按核心索引換算差值()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, 2686);
        using var snapshot = mapping.CreateViewAccessor(0, 2686);
        WriteSdkSnapshot(snapshot, 2, 1);
        snapshot.Write(2685, (byte)1);
        snapshot.Write(1544, 30f);
        snapshot.Write(1548, 40f);
        Assert.True(CoreTempSharedMem.TryRead(out var readings, out _, snapshot));
        var entries = new[] { new CoreTempEntry(1, "Second"), new CoreTempEntry(0, "First") };
        Assert.True(CoreTempMapService.ApplyExternalReadings(entries, readings, "CPU X", 1, true, out var tjMax));
        Assert.Equal(100, tjMax);
        Assert.Equal(60, entries[0].Temperature);
        Assert.Equal(70, entries[1].Temperature);
    }

    [Fact]
    public void 華氏差值只乘五除九而華氏TjMax需減三十二()
    {
        using var mapping = MemoryMappedFile.CreateNew(null, 2686);
        using var snapshot = mapping.CreateViewAccessor(0, 2686);
        WriteSdkSnapshot(snapshot, 2, 1);
        snapshot.Write(2684, (byte)1);
        snapshot.Write(2685, (byte)1);
        snapshot.Write(1024, 212u);
        snapshot.Write(1544, 54f);
        snapshot.Write(1548, 72f);
        Assert.True(CoreTempSharedMem.TryRead(out var readings, out _, snapshot));
        var entries = new[] { new CoreTempEntry(0, "First"), new CoreTempEntry(1, "Second") };
        Assert.True(CoreTempMapService.ApplyExternalReadings(entries, readings, "CPU X", 1, true, out var tjMax));
        Assert.Equal(100, tjMax);
        Assert.Equal(70, entries[0].Temperature);
        Assert.Equal(60, entries[1].Temperature);
    }

    [Fact]
    public void 絕對華氏備援轉攝氏並保留合法零度與負溫()
    {
        var entries = new[] { new CoreTempEntry(0, "First"), new CoreTempEntry(1, "Second") };
        ExternalReading[] readings = [CoreTemp(core: 1, value: -40, unit: "°F"), CoreTemp(value: 32, unit: "°F")];
        Assert.True(CoreTempMapService.ApplyExternalReadings(entries, readings, "CPU X", 1, true, out var tjMax));
        Assert.Null(tjMax);
        Assert.Equal(0, entries[0].Temperature);
        Assert.Equal(-40, entries[1].Temperature);
    }

    [Fact]
    public void 備援拒絕多插槽未知拓撲混合核心或不同Cpu且清除舊值()
    {
        var entries = new[] { new CoreTempEntry(0, "First") };
        foreach (var (packages, knownOrder, cpu) in new[]
                 { (2, true, "CPU X"), (0, true, "CPU X"), (1, false, "CPU X"), (1, true, "CPU Y"), (1, true, "") })
        {
            entries[0].Temperature = 80;
            Assert.False(CoreTempMapService.ApplyExternalReadings(entries, [CoreTemp()], cpu,
                packages, knownOrder, out var tjMax));
            Assert.Null(entries[0].Temperature);
            Assert.Null(tjMax);
        }
        Assert.False(CoreTempMapService.ApplyExternalReadings(entries,
            [CoreTemp() with { DeviceIdentity = null, ChannelIndex = null }], "CPU X", 1, true, out _));
    }

    [Fact]
    public void 缺核重複核或未知索引不按位置填入()
    {
        var entries = new[] { new CoreTempEntry(0, "First"), new CoreTempEntry(1, "Second") };
        foreach (ExternalReading[] readings in new[]
                 { new[] { CoreTemp() }, new[] { CoreTemp(), CoreTemp() }, new[] { CoreTemp(), CoreTemp(core: 2) } })
        {
            Assert.False(CoreTempMapService.ApplyExternalReadings(entries, readings, "CPU X", 1, true, out _));
            Assert.All(entries, entry => Assert.Null(entry.Temperature));
        }
    }

    [Fact]
    public void 無TjMax不重建差值且不沿用舊溫度並拒絕非有限與未知單位()
    {
        var entries = new[] { new CoreTempEntry(0, "First") { Temperature = 80 } };
        ExternalReading limit = new("Core Temp", "Temperature", "CPU #0 TjMax", 100, "°C", DeviceIdentity: "CPU X");
        Assert.True(CoreTempMapService.ApplyExternalReadings(entries,
            [CoreTemp(value: 30, type: "TemperatureDelta"), limit], "CPU X", 1, true, out var tjMax));
        Assert.Equal(100, tjMax);
        foreach (var reading in new[] { CoreTemp(value: 30, type: "TemperatureDelta"),
                     CoreTemp(value: double.NaN), CoreTemp(value: double.PositiveInfinity), CoreTemp(unit: "K") })
        {
            Assert.False(CoreTempMapService.ApplyExternalReadings(entries, [reading], "CPU X", 1, true, out tjMax));
            Assert.Null(tjMax);
            Assert.Null(entries[0].Temperature);
        }
        Assert.False(CoreTempMapService.ApplyExternalReadings(entries,
            [CoreTemp(value: 30, type: "TemperatureDelta"), limit, limit], "CPU X", 1, true, out tjMax));
        Assert.Null(tjMax);
    }

    // 欄位位移與索引依官方 SDK GetCoreTempInfo.h／Program.cs，不使用生產常數建構期望值。
    private static void WriteSdkSnapshot(MemoryMappedViewAccessor snapshot, uint coresPerCpu, uint cpus)
    {
        snapshot.Write(1536, coresPerCpu);
        snapshot.Write(1540, cpus);
        snapshot.Write(1024, 100u);
        snapshot.Write(1028, 105u);
        byte[] name = Encoding.ASCII.GetBytes("CPU X\0");
        snapshot.WriteArray(2584, name, 0, name.Length);
        for (int i = 0; i < 4; i++) snapshot.Write(1544 + i * 4, 60f + i);
    }
}
