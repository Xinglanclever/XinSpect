using System.IO;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Production SensorService poll failures with an injected provider; no hardware or user-settings access.</summary>
public sealed class SensorAlertFailedPollTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XinSpect-failed-alert-tests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void FailedPollCannotTriggerLegacyFromHighCacheOrCallAutomaticDiagnosis()
    {
        using var provider = new MemoryProvider(0);
        var custom = CreateRules(provider.Live); var alerts = new AlertService(custom);
        var settings = new SettingsService(false) { AlertsEnabled = true, MemLoadThreshold = 80 };
        int balloon = 0, diagnose = 0; alerts.Balloon = (_, _) => balloon++; alerts.Diagnose = (_, _) => diagnose++;
        alerts.Check(provider.Live, settings); Assert.Empty(alerts.Events);

        provider.Value = 90; // provider cache changed even though this whole poll will fail
        provider.FailAndPublish();
        int before = provider.ValueReads;
        alerts.CheckFailedFrame(provider.Live.CurrentSensorFrame, settings.AlertsEnabled);

        Assert.Equal(before, provider.ValueReads);
        Assert.Empty(alerts.Events); Assert.Equal(0, balloon); Assert.Equal(0, diagnose);
        var failed = Assert.Single(custom.Statuses);
        Assert.Equal(SensorAlertPhase.Unknown, failed.Phase); Assert.False(failed.IsActive); Assert.Null(failed.Value);
        Assert.DoesNotContain(custom.History, e => e.Kind is SensorAlertTransitionKind.Triggered or SensorAlertTransitionKind.Recovered);

        provider.RecoverAndPublish(90); alerts.Check(provider.Live, settings);
        Assert.Equal(2, alerts.Events.Count); // one legacy + one explicit custom rule, only after successful poll
        Assert.Equal(1, diagnose); Assert.Equal(2, balloon);
    }

    [Fact]
    public void FailedPollCannotRecoverLegacyFromLowCacheAndCustomActiveBecomesUnknown()
    {
        using var provider = new MemoryProvider(90);
        var custom = CreateRules(provider.Live); var alerts = new AlertService(custom);
        var settings = new SettingsService(false) { AlertsEnabled = true, MemLoadThreshold = 80 };
        int balloon = 0, diagnose = 0; alerts.Balloon = (_, _) => balloon++; alerts.Diagnose = (_, _) => diagnose++;
        alerts.Check(provider.Live, settings); Assert.Equal(2, alerts.Events.Count); Assert.Equal(1, diagnose);

        provider.Value = 0;
        provider.FailAndPublish();
        int before = provider.ValueReads;
        alerts.CheckFailedFrame(provider.Live.CurrentSensorFrame, settings.AlertsEnabled);

        Assert.Equal(before, provider.ValueReads); Assert.Equal(2, alerts.Events.Count);
        Assert.Equal(2, balloon); Assert.Equal(1, diagnose); Assert.True(alerts.AnyActive);
        Assert.Contains("記憶體負載（本拍失敗；尚未確認恢復）", alerts.BannerText);
        var unknown = Assert.Single(custom.Statuses);
        Assert.Equal(SensorAlertPhase.Unknown, unknown.Phase); Assert.True(unknown.IsActive); Assert.Null(unknown.Value);
        Assert.Equal(SensorAlertTransitionKind.Unknown, custom.History[0].Kind);
        Assert.DoesNotContain(custom.History, h => h.Kind == SensorAlertTransitionKind.Recovered);

        provider.RecoverAndPublish(90); alerts.Check(provider.Live, settings);
        Assert.Equal(2, alerts.Events.Count); // no false legacy recovery/retrigger during failed low cache
        Assert.Equal(2, balloon); Assert.Equal(1, diagnose);
        provider.RecoverAndPublish(0); alerts.Check(provider.Live, settings);
        Assert.False(alerts.AnyActive); Assert.Equal(SensorAlertTransitionKind.Recovered, custom.History[0].Kind);
        provider.RecoverAndPublish(90); alerts.Check(provider.Live, settings);
        Assert.Equal(4, alerts.Events.Count); // valid recovery made a later legacy/custom trigger possible
    }

    [Fact]
    public void FailedPollStillHonorsMasterDisableWithoutCallingLegacyOrDiagnosis()
    {
        using var provider = new MemoryProvider(90);
        var custom = CreateRules(provider.Live); var alerts = new AlertService(custom);
        var settings = new SettingsService(false) { AlertsEnabled = true, MemLoadThreshold = 80 };
        alerts.Check(provider.Live, settings);
        provider.FailAndPublish();
        int balloons = 0, diagnoses = 0;
        alerts.Balloon = (_, _) => balloons++; alerts.Diagnose = (_, _) => diagnoses++;
        int before = provider.ValueReads;
        alerts.CheckFailedFrame(provider.Live.CurrentSensorFrame, alertsEnabled: false);
        Assert.Equal(before, provider.ValueReads); Assert.Equal(0, diagnoses); Assert.Equal(0, balloons);
        Assert.False(alerts.AnyActive); Assert.Empty(alerts.BannerText);
        Assert.Equal(SensorAlertPhase.Disabled, Assert.Single(custom.Statuses).Phase);
        Assert.Equal(2, alerts.Events.Count);
    }

    private SensorAlertService CreateRules(SensorService live)
    {
        var reading = Assert.Single(live.CurrentSensorFrame.Readings);
        var custom = new SensorAlertService(new SensorAlertStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json")));
        Assert.True(custom.SaveRule(new SensorAlertRule
        {
            Id = Guid.NewGuid(), Name = "Explicit memory rule", SensorId = reading.Id, Unit = reading.Unit,
            Direction = SensorAlertDirection.Above, Threshold = 80, RecoveryThreshold = 70, DurationSeconds = 0, CooldownSeconds = 60
        }, out var error), error);
        return custom;
    }

    private sealed class MemoryProvider : IDisposable
    {
        public float Value;
        public int ValueReads;
        private bool _fail;
        public SensorService Live { get; }
        public MemoryProvider(float value)
        {
            Value = value;
            var sensor = Fake<ISensor>((method, _) => method.Name switch
            {
                "get_Name" => "Memory", "get_SensorType" => SensorType.Load,
                "get_Identifier" => new Identifier("ram", "load", "0"), "get_Value" => ReadValue(), _ => null
            });
            var hardware = Fake<IHardware>((method, _) => method.Name switch
            {
                "get_Name" => "Memory", "get_HardwareType" => HardwareType.Memory,
                "get_Identifier" => new Identifier("ram"), "get_Sensors" => new[] { sensor },
                "get_SubHardware" => Array.Empty<IHardware>(),
                "Update" => Invoke(() => { if (_fail) throw new InvalidOperationException("Injected whole-poll failure"); }), _ => null
            });
            IComputer computer = null!;
            computer = Fake<IComputer>((method, args) => method.Name switch
            {
                "get_Hardware" => new[] { hardware },
                "Accept" => Invoke(() => ((IVisitor)args![0]!).VisitComputer(computer)),
                "Traverse" => Invoke(() => ((IVisitor)args![0]!).VisitHardware(hardware)), _ => null
            });
            Live = new SensorService(computer, () => { }, () => { });
        }
        private float ReadValue() { ValueReads++; return Value; }
        public void FailAndPublish()
        {
            _fail = true; Assert.Throws<InvalidOperationException>(Live.Poll); Live.PublishSensorObservations();
            Assert.All(Live.CurrentSensorFrame.Readings, r => Assert.False(r.IsUsable));
        }
        public void RecoverAndPublish(float value) { _fail = false; Value = value; Live.Refresh(); }
        public void Dispose() => Live.Dispose();
    }
    private static object? Invoke(Action action) { action(); return null; }
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, FailedAlertProxy>();
        ((FailedAlertProxy)(object)proxy).Handler = invoke; return proxy;
    }
    public class FailedAlertProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
