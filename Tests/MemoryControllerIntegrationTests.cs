using System.Diagnostics;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace XinSpect.Tests;

public sealed class MemoryControllerIntegrationTests
{
    [Fact]
    public void ProductionPollCopiesCachedProviderOnceAndFailureAndDisposeInvalidateIt()
    {
        long now = Stopwatch.Frequency;
        float value = 1800;
        int updates = 0, closes = 0;
        bool fail = false;
        var computer = FakeComputer(() => value, () => { updates++; if (fail) throw new InvalidOperationException("synthetic provider failure"); });
        using var service = new SensorService(computer, () => { }, () => closes++, () => now);
        Assert.Equal(1, updates);
        var original = service.ControllerSnapshot;
        Assert.Equal(1800, Assert.Single(original.Clocks, r => r.Key == "mclk").NumericValue);
        Assert.Equal(FactAvailability.Unknown, Assert.Single(original.Clocks, r => r.Key == "mclk").Availability);

        value = 2000;
        Assert.Equal(1800, Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").NumericValue);
        Assert.Equal(1, updates); // Reading the projection performs no additional provider or hardware update.
        now += Stopwatch.Frequency;
        service.Poll();
        Assert.Equal(2, updates);
        Assert.Equal(2000, Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").NumericValue);
        Assert.Equal(1800, Assert.Single(original.Clocks, r => r.Key == "mclk").NumericValue);

        fail = true;
        Assert.Throws<InvalidOperationException>(service.Poll);
        Assert.All(service.ControllerSnapshot.Clocks, r =>
        {
            Assert.Null(r.NumericValue);
            Assert.Equal(FactAvailability.ReadError, r.Availability);
        });
        fail = false;
        service.Poll();
        Assert.Equal(2000, Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").NumericValue);
        service.Dispose();
        Assert.All(service.ControllerSnapshot.Clocks, r => Assert.Null(r.NumericValue));
        int completed = updates;
        service.Poll();
        Assert.Equal(completed, updates);
        Assert.Equal(1, closes);
    }

    [Fact]
    public void ProductionGetterExpiresWhenPollingStopsWithoutAnotherHardwareRead()
    {
        long now = Stopwatch.Frequency;
        int updates = 0;
        using var service = new SensorService(FakeComputer(() => 1800, () => updates++),
            () => { }, () => { }, () => now);
        now += 10 * Stopwatch.Frequency; // The slowest configured polling interval must not expire an ordinary capture.
        Assert.Equal(1800, Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").NumericValue);
        now += 21 * Stopwatch.Frequency;
        var expired = Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk");
        Assert.Null(expired.NumericValue);
        Assert.True(expired.IsExpired);
        Assert.Equal(1, updates);
        service.Poll();
        Assert.False(Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").IsExpired);
        Assert.Equal(2, updates);
    }

    [Fact]
    public void OptionalIdentifierFailureKeepsMainPollUsefulAndPublishesControllerReadError()
    {
        bool failMetadata = false;
        int updates = 0;
        long now = Stopwatch.Frequency;
        using var service = new SensorService(FakeComputer(() => 1800, () => updates++,
            () => { if (failMetadata) throw new InvalidOperationException("synthetic identifier failure"); }),
            () => { }, () => { }, () => now);
        Assert.Equal(1800, Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").NumericValue);
        long previousSequence = service.CurrentSensorFrame.Sequence;
        failMetadata = true;
        now += Stopwatch.Frequency;
        Assert.Null(Record.Exception(service.Poll));
        service.Publish();
        Assert.Equal(2, updates);
        Assert.True(service.CurrentSensorFrame.Sequence > previousSequence);
        var load = Assert.Single(service.CurrentSensorFrame.Readings, r => r.Name == "CPU Total");
        Assert.Equal(45, load.Value);
        Assert.Equal(FactAvailability.Present, load.Availability);
        Assert.Equal(3, service.ControllerSnapshot.Clocks.Count);
        Assert.All(service.ControllerSnapshot.Clocks, r =>
        {
            Assert.Null(r.NumericValue);
            Assert.Equal(FactAvailability.ReadError, r.Availability);
            Assert.Contains("InvalidOperationException", r.Reason);
            Assert.Contains("不是已確認無硬體", r.Reason);
        });
        failMetadata = false;
        service.Poll();
        Assert.Equal(1800, Assert.Single(service.ControllerSnapshot.Clocks, r => r.Key == "mclk").NumericValue);
    }

    private static IComputer FakeComputer(Func<float> read, Action update, Action? identifierRead = null)
    {
        var sensor = DispatchProxy.Create<ISensor, ProviderProxy>();
        ((ProviderProxy)(object)sensor).Call = (method, _) => method.Name switch
        {
            "get_Name" => "Memory",
            "get_Identifier" => ReadIdentifier(identifierRead),
            "get_SensorType" => SensorType.Clock,
            "get_Value" => (float?)read(),
            _ => throw new InvalidOperationException("Unexpected synthetic sensor call: " + method.Name),
        };
        var load = DispatchProxy.Create<ISensor, ProviderProxy>();
        ((ProviderProxy)(object)load).Call = (method, _) => method.Name switch
        {
            "get_Name" => "CPU Total",
            "get_Identifier" => new Identifier("amdcpu", "0", "load", "0"),
            "get_SensorType" => SensorType.Load,
            "get_Value" => (float?)45,
            _ => throw new InvalidOperationException("Unexpected synthetic load sensor call: " + method.Name),
        };
        var hardware = DispatchProxy.Create<IHardware, ProviderProxy>();
        ((ProviderProxy)(object)hardware).Call = (method, _) => method.Name switch
        {
            "get_Name" => "Synthetic AMD CPU",
            "get_HardwareType" => HardwareType.Cpu,
            "get_Identifier" => new Identifier("amdcpu", "0"),
            "get_Sensors" => new[] { sensor, load },
            "get_SubHardware" => Array.Empty<IHardware>(),
            "Update" => Run(update),
            _ => throw new InvalidOperationException("Unexpected synthetic hardware call: " + method.Name),
        };
        var computer = DispatchProxy.Create<IComputer, ProviderProxy>();
        ((ProviderProxy)(object)computer).Call = (method, args) => method.Name switch
        {
            "get_Hardware" => new[] { hardware },
            "Accept" => Run(() => ((IVisitor)args![0]!).VisitComputer(computer)),
            "Traverse" => Run(() => ((IVisitor)args![0]!).VisitHardware(hardware)),
            _ => throw new InvalidOperationException("Unexpected synthetic computer call: " + method.Name),
        };
        return computer;
    }
    private static Identifier ReadIdentifier(Action? read)
    {
        read?.Invoke();
        return new Identifier("amdcpu", "0", "clock", "20");
    }
    private static object? Run(Action action) { action(); return null; }
    public class ProviderProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}
