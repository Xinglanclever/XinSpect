using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

// All SDK/IPC and telemetry are injected. No native engine initialization, user profile writes,
// VRM detection, stress worker, or hardware operation runs in these regressions.
[Collection(HardwareOperationPolicyCollection.Name)]
public sealed class OverclockOperationCoordinationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("all")]
    [InlineData("planner")]
    [InlineData("vcore")]
    [InlineData("knob")]
    [InlineData("profile")]
    [InlineData("defaults")]
    [InlineData("discard")]
    [InlineData("optimizer")]
    public async Task RecoveryRevokesBatchWaitsForDispatchedCallAndRejectsAllNewMutations(string entry)
    {
        await using var fixture = new Fixture();
        var service = fixture.Service;
        var applying = fixture.Begin(entry);
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);

        var recovering = service.RollbackToStableAsync("fixture recovery");
        Assert.Same(recovering, service.RollbackToStableAsync("duplicate request"));
        Assert.False(applying.IsCompleted);
        Assert.False(recovering.IsCompleted);
        Assert.True(service.IsApplying);
        Assert.True(service.IsRecovering);
        Assert.False(service.CanApply);
        Assert.Contains("正在等待", service.ActionStatus);
        Assert.Contains("尚未確認", service.WatchdogStatusText);

        // Every public mutation must observe recovery admission, including the shared profile
        // path, manual defaults, optimizer, and the separate LLC entry.
        await Task.WhenAll(
            service.ApplyAll(), service.ApplyPlannerAsync(), service.ApplyVcore(),
            service.ApplyKnob(fixture.Engine.Knobs[0]), service.ImportProfileFrom(fixture.ProfilePath),
            service.RestoreDefaults(), service.DiscardAll(), service.SetSpeedOptimizer(true, false),
            service.ApplyLlc()).WaitAsync(Budget);
        Assert.Single(fixture.Engine.Mutations);
        Assert.Contains("fixture recovery", service.ActionStatus);

        fixture.Engine.ReleaseMutation.Set();
        await Task.WhenAll(applying, recovering).WaitAsync(Budget);

        Assert.True((await recovering).Ok);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
        Assert.All(fixture.Engine.MutationPermissions, Assert.True);
        Assert.False(service.IsApplying);
        Assert.False(service.IsRecovering);
        Assert.True(service.CanApply);
        Assert.False(service.WatchdogArmed);
        Assert.StartsWith("fixture recovery：", service.ActionStatus);
        Assert.DoesNotContain("duplicate request", service.ActionStatus);

        // A later user action gets a new generation; revoking the old batch is not a permanent lock.
        fixture.Engine.Knobs[0].Target = 46;
        await service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        Assert.Equal("apply:1:46", fixture.Engine.Mutations.Last());
        Assert.True(service.WatchdogArmed);
    }

    [Fact]
    public async Task RecoveryRejectsAnApplyQueuedBehindTelemetryBeforeItDispatches()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockRead = true;
        fixture.Engine.BlockMutationAt = -1;
        var telemetry = fixture.Service.TickAsync(null);
        await fixture.Engine.ReadEntered.Task.WaitAsync(Budget);
        var applying = fixture.Service.ApplyAll();
        var recovering = fixture.Service.RollbackToStableAsync("queued recovery");
        Assert.Empty(fixture.Engine.Mutations);
        Assert.False(recovering.IsCompleted);
        fixture.Engine.ReleaseRead.Set();

        await Task.WhenAll(telemetry, applying, recovering).WaitAsync(Budget);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.ToArray());
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
        Assert.False(fixture.Service.WatchdogArmed);
        Assert.StartsWith("queued recovery：", fixture.Service.ActionStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialRecoveryReportsFailureAndStillAttemptsEveryBaselineItem(bool throws)
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        fixture.Engine.RecoveryFailureId = 2;
        fixture.Engine.ThrowRecoveryFailure = throws;

        var result = await fixture.Service.RollbackToStableAsync("partial recovery").WaitAsync(Budget);

        Assert.False(result.Ok);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.ToArray());
        Assert.Contains("3/4", fixture.Service.ActionStatus);
        Assert.Contains("回復未完成", fixture.Service.ActionStatus);
        Assert.Equal(Severity.Critical, fixture.Service.ActionSeverity);
        Assert.Equal(Severity.Critical, fixture.Engine.Knobs[1].ApplySeverity);
        Assert.Equal(102, fixture.Engine.Knobs[1].Target);
        Assert.False(fixture.Service.IsApplying);
        Assert.False(fixture.Service.IsRecovering);
        Assert.False(fixture.Service.WatchdogArmed);
    }

    [Fact]
    public async Task FailureFromRevokedDispatchedCallCannotOverwriteRecoveryOrRearmWatchdog()
    {
        await using var fixture = new Fixture();
        fixture.Engine.ThrowFirstMutation = true;
        var applying = fixture.Service.ApplyAll();
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);
        var recovering = fixture.Service.RollbackToStableAsync("after dispatched failure");
        fixture.Engine.ReleaseMutation.Set();

        await Task.WhenAll(applying, recovering).WaitAsync(Budget);

        Assert.True((await recovering).Ok);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.StartsWith("after dispatched failure：", fixture.Service.ActionStatus);
        Assert.DoesNotContain("dispatched fixture failure", fixture.Service.ActionStatus);
        Assert.False(fixture.Service.WatchdogArmed);
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task RecoveryRetainsRestorativePermissionAfterPolicyRevocation()
    {
        await using var fixture = new Fixture();
        var applying = fixture.Service.ApplyAll();
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);
        fixture.Settings.AdvancedDiagnosticsEnabled = false;
        var recovering = fixture.Service.RollbackToStableAsync("policy disabled");
        fixture.Engine.ReleaseMutation.Set();

        await Task.WhenAll(applying, recovering).WaitAsync(Budget);

        Assert.True((await recovering).Ok);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.All(fixture.Engine.MutationPermissions, Assert.True);
        Assert.False(HardwareOperationGate.CanWriteHardware);
        Assert.False(fixture.Service.WatchdogArmed);
    }

    [Fact]
    public async Task WatchdogUsesMonotonicTimeAndDoesNotClaimSuccessWhileItsIpcIsBlocked()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = 2;
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        Assert.True(fixture.Service.WatchdogArmed);
        // Poll count and civil clock corrections do not consume the monotonic deadline.
        for (int i = 0; i < 60; i++) await fixture.Service.TickAsync(null).WaitAsync(Budget);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddYears(1);
        await fixture.Service.TickAsync(null).WaitAsync(Budget);
        Assert.Equal(30, fixture.Service.WatchdogSecondsLeft);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddYears(-2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        await fixture.Service.TickAsync(null).WaitAsync(Budget);
        Assert.Equal(1, fixture.Service.WatchdogSecondsLeft);
        Assert.False(fixture.Service.IsRecovering);

        // Expiry requires no TickAsync call.
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);
        var recovering = fixture.Service.RollbackToStableAsync("duplicate fixture request");
        Assert.True(fixture.Service.IsRecovering);
        Assert.False(fixture.Service.CanApply);
        Assert.False(recovering.IsCompleted);
        Assert.Contains("正在等待", fixture.Service.ActionStatus);
        Assert.DoesNotContain("已自動回復", fixture.Service.ActionStatus);
        Assert.False(fixture.Service.WatchdogArmed);

        fixture.Engine.ReleaseMutation.Set();
        await recovering.WaitAsync(Budget);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.StartsWith("看門狗逾時：", fixture.Service.ActionStatus);
        Assert.False(fixture.Service.IsRecovering);
        Assert.False(fixture.Service.WatchdogArmed);
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task WatchdogAdmitsRecoveryDuringBlockedTelemetryAndRevokesQueuedApply()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        fixture.Engine.BlockRead = true;
        var telemetry = fixture.Service.TickAsync(null);
        await fixture.Engine.ReadEntered.Task.WaitAsync(Budget);
        var applying = fixture.Service.ApplyAll();

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(fixture.Service.IsRecovering);
        Assert.False(fixture.Service.WatchdogArmed);
        Assert.Single(fixture.Engine.Mutations);
        var recovering = fixture.Service.RollbackToStableAsync("duplicate fixture request");
        Assert.False(recovering.IsCompleted);
        Assert.False(telemetry.IsCompleted);
        Assert.Contains("尚未確認", fixture.Service.WatchdogStatusText);
        fixture.Engine.ReleaseRead.Set();
        await Task.WhenAll(telemetry, applying, recovering).WaitAsync(Budget);

        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
        Assert.StartsWith("看門狗逾時：", fixture.Service.ActionStatus);
    }

    [Fact]
    public async Task WatchdogDoesNotCaptureHighRiskPermissionInItsExecutionContext()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = 2;
        // Explicit outer scope ensures arming occurs with AsyncLocal depth > 0,
        // including if a particular apply implementation scopes its inner permit narrowly.
        using (var permit = HardwareOperationGate.TryBeginHighRiskOperation())
        {
            Assert.NotNull(permit);
            await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        }
        Assert.True(fixture.Clock.LastTimer.FlowSuppressedAtCreation);
        Assert.False(fixture.Clock.LastTimer.HasCapturedContext);
        fixture.Settings.AdvancedDiagnosticsEnabled = false;
        Assert.False(HardwareOperationGate.CanWriteHardware);
        var admissionPermissions = new ConcurrentQueue<bool>();
        var completionPermissions = new ConcurrentQueue<bool>();
        fixture.Service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OverclockService.IsRecovering)) return;
            if (fixture.Service.IsRecovering)
                admissionPermissions.Enqueue(HardwareOperationGate.CanWriteHardware);
            else
                completionPermissions.Enqueue(HardwareOperationGate.CanWriteHardware);
        };

        // ManualTimer captures/runs EC like System timers. Without SuppressFlow,
        // the callback would retain the high-risk apply's AsyncLocal permission.
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);
        Assert.False(Assert.Single(admissionPermissions));
        Assert.False(HardwareOperationGate.CanWriteHardware);
        var recovering = fixture.Service.RollbackToStableAsync("duplicate fixture request");
        fixture.Engine.ReleaseMutation.Set();
        await recovering.WaitAsync(Budget);

        Assert.False(Assert.Single(completionPermissions));
        Assert.All(fixture.Engine.MutationPermissions, Assert.True);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.False(HardwareOperationGate.CanWriteHardware);
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task EarlyAndSupersededTimerCallbacksCannotExpireTheNewArm()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = 3;
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        var previous = fixture.Clock.LastTimer;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[1]).WaitAsync(Budget);
        var current = fixture.Clock.LastTimer;
        fixture.Clock.Advance(TimeSpan.FromSeconds(20), fireTimers: false);
        previous.FireQueuedCallback(); // Deliver a callback queued before Dispose.
        current.FireQueuedCallback(); // Early delivery must reschedule the remaining ten seconds.
        Assert.False(fixture.Service.IsRecovering);
        Assert.Equal(2, fixture.Engine.Mutations.Count);
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(fixture.Service.IsRecovering);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);
        var recovering = fixture.Service.RollbackToStableAsync("duplicate fixture request");
        fixture.Engine.ReleaseMutation.Set();
        await recovering.WaitAsync(Budget);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(2).ToArray());
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task DisarmedOrDisposedWatchdogIgnoresAnAlreadyQueuedCallback()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        var disarmed = fixture.Clock.LastTimer;
        await fixture.Service.RestoreDefaults().WaitAsync(Budget);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30), fireTimers: false);
        disarmed.FireQueuedCallback();
        Assert.False(fixture.Service.IsRecovering);
        Assert.Equal(2, fixture.Engine.Mutations.Count);

        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        var disposed = fixture.Clock.LastTimer;
        fixture.Service.Dispose();
        await fixture.Engine.Disposed.Task.WaitAsync(Budget);
        fixture.Clock.Advance(TimeSpan.FromSeconds(30), fireTimers: false);
        disposed.FireQueuedCallback();
        Assert.False(fixture.Service.IsRecovering);
        Assert.False(fixture.Service.WatchdogArmed);
        Assert.Equal(3, fixture.Engine.Mutations.Count);
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
    }

    [Fact]
    public async Task SiliconCharacterizationRequiresFirstLevelDiagnosticsAndUsesNoWorkerInThisTest()
    {
        int calls = 0;
        await using var fixture = new Fixture(siliconProbe: (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new OperationCanceledException("No-worker fixture.");
        });
        fixture.Settings.AdvancedDiagnosticsEnabled = false;
        Assert.False(fixture.Service.SiliconCanTest);
        await fixture.Service.RetestSilicon().WaitAsync(Budget);
        Assert.Equal(0, calls);
        Assert.False(fixture.Service.SiliconTesting);
        Assert.Contains("第一級", fixture.Service.ActionStatus);

        fixture.Settings.AdvancedDiagnosticsEnabled = true;
        // The first gate suffices for load; second-level hardware write permission is unrelated.
        fixture.Settings.HighRiskHardwareOperationsEnabled = false;
        var changed = new List<string?>();
        fixture.Service.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        fixture.Service.RefreshPolicy();
        Assert.Contains(nameof(OverclockService.SiliconCanTest), changed);
        Assert.True(fixture.Service.SiliconCanTest);
        await fixture.Service.RetestSilicon().WaitAsync(Budget);
        Assert.Equal(1, calls);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.False(fixture.Service.SiliconTesting);
    }

    [Fact]
    public async Task SiliconGateIsRecheckedBeforeProbeDispatch()
    {
        int calls = 0;
        await using var fixture = new Fixture(siliconProbe: (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new OperationCanceledException("No-worker fixture.");
        });
        fixture.Service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverclockService.SiliconTesting) && fixture.Service.SiliconTesting)
                fixture.Settings.AdvancedDiagnosticsEnabled = false;
        };
        await fixture.Service.RetestSilicon().WaitAsync(Budget);
        Assert.Equal(0, calls);
        Assert.False(fixture.Service.SiliconTesting);
        Assert.Contains("第一級", fixture.Service.SiliconDetail);
    }

    [Fact]
    public async Task VoltageOffsetIsNeverDisplayedAsAbsoluteVcoreAndMissingTelemetryClearsBadge()
    {
        await using var fixture = new Fixture(offsetOnly: true);
        Assert.Equal(OcKnobKind.VoltageOffset, fixture.Service.VcoreKnob!.Kind);
        Assert.Equal(50, fixture.Service.VcoreKnob.Active);
        fixture.Engine.CoreVoltageReading = 1.1;
        await fixture.Service.TickAsync(null).WaitAsync(Budget);
        Assert.Equal(1.1, fixture.Service.VoltmeterValue);
        fixture.Engine.CoreVoltageReading = null;
        await fixture.Service.TickAsync(null).WaitAsync(Budget);
        Assert.Equal("—", fixture.Service.VoltageValueText);
        Assert.Equal(0, fixture.Service.VoltmeterValue);
        Assert.Equal("—", fixture.Service.VoltageBadgeText);
        Assert.False(fixture.Service.VoltageBadgeFlashing);
        Assert.Equal("—", fixture.Service.CurrentText);
        Assert.Empty(fixture.Engine.Mutations);
    }

    [Fact]
    public async Task AggregateVoltageKeepsLastMeasuredValueUntilTelemetryUpdatesIt()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        Assert.NotNull(fixture.Service.VcoreKnob);
        var aggregate = fixture.Service.VcoreKnob!;
        double measuredBefore = aggregate.Active;
        aggregate.Target = 1.2;

        await fixture.Service.ApplyVcore().WaitAsync(Budget);

        Assert.Equal(new[] { "apply:3:1.2", "apply:4:1.2" }, fixture.Engine.Mutations.ToArray());
        Assert.Equal(measuredBefore, aggregate.Active);
        Assert.NotEqual(aggregate.Target, aggregate.Active);
        Assert.Contains("等待感測器", aggregate.ApplyStatus);
    }

    [Fact]
    public async Task EmptyBaselineDefaultFailureIsNotReportedAsSuccessfulRecovery()
    {
        await using var fixture = new Fixture(emptyBaseline: true);
        fixture.Engine.BlockMutationAt = -1;
        fixture.Engine.FailDefaults = true;

        var result = await fixture.Service.RollbackToStableAsync("empty baseline").WaitAsync(Budget);

        Assert.False(result.Ok);
        Assert.Equal(new[] { "defaults" }, fixture.Engine.Mutations.ToArray());
        Assert.Contains("fixture defaults failed", fixture.Service.ActionStatus);
        Assert.Equal(Severity.Critical, fixture.Service.ActionSeverity);
        Assert.False(fixture.Service.IsApplying);
    }

    [Fact]
    public async Task NormalPolicyDenialStillBlocksApplyAndOptimizerWhileManualDefaultsRemainRestorative()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        fixture.Settings.AdvancedDiagnosticsEnabled = false;

        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        Assert.Empty(fixture.Engine.Mutations);
        Assert.Contains("高風險", fixture.Service.ActionStatus);
        await fixture.Service.SetSpeedOptimizer(true, false).WaitAsync(Budget);
        Assert.Empty(fixture.Engine.Mutations);
        await fixture.Service.RestoreDefaults().WaitAsync(Budget);
        Assert.Equal(new[] { "defaults" }, fixture.Engine.Mutations.ToArray());
        Assert.True(Assert.Single(fixture.Engine.MutationPermissions));
        Assert.False(HardwareOperationGate.CanWriteHardware);
    }

    [Fact]
    public async Task FailedManualDefaultsKeepTheExistingWatchdogArmed()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        Assert.True(fixture.Service.WatchdogArmed);
        fixture.Engine.FailDefaults = true;

        await fixture.Service.RestoreDefaults().WaitAsync(Budget);

        Assert.True(fixture.Service.WatchdogArmed);
        Assert.Equal(Severity.Critical, fixture.Service.ActionSeverity);
        Assert.Contains("fixture defaults failed", fixture.Service.ActionStatus);
    }

    [Fact]
    public async Task SuccessfulRecoveryPreservesTheCriticalReasonSeverity()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;

        var result = await fixture.Service.RollbackToStableAsync("overheat fixture", Severity.Critical).WaitAsync(Budget);

        Assert.True(result.Ok);
        Assert.Equal(Severity.Critical, fixture.Service.ActionSeverity);
        Assert.StartsWith("overheat fixture：", fixture.Service.ActionStatus);
    }

    [Fact]
    public async Task ThrowingAdmissionObserverCannotStrandTheApplyGeneration()
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        int injected = 0;
        fixture.Service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverclockService.IsApplying)
                && fixture.Service.IsApplying && Interlocked.Exchange(ref injected, 1) == 0)
                throw new InvalidOperationException("fixture admission observer");
        };

        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);

        Assert.Equal(1, injected);
        Assert.Single(fixture.Engine.Mutations);
        Assert.False(fixture.Service.IsApplying);
        Assert.True(fixture.Service.CanApply);
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[1]).WaitAsync(Budget);
        Assert.Equal(2, fixture.Engine.Mutations.Count);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("pending-status")]
    [InlineData("watchdog-disarm")]
    [InlineData("completion")]
    public async Task ThrowingRecoveryObserverCannotAbandonRestorationOrItsCompletion(string phase)
    {
        await using var fixture = new Fixture();
        fixture.Engine.BlockMutationAt = -1;
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        Assert.True(fixture.Service.WatchdogArmed);
        int injected = 0;
        fixture.Service.PropertyChanged += (_, e) =>
        {
            bool point = phase switch
            {
                "admission" => e.PropertyName == nameof(OverclockService.IsApplying) && fixture.Service.IsRecovering,
                "pending-status" => e.PropertyName == nameof(OverclockService.ActionStatus)
                    && fixture.Service.ActionStatus.Contains("正在等待", StringComparison.Ordinal),
                "watchdog-disarm" => e.PropertyName == nameof(OverclockService.WatchdogArmed)
                    && !fixture.Service.WatchdogArmed,
                "completion" => e.PropertyName == nameof(OverclockService.IsRecovering) && !fixture.Service.IsRecovering,
                _ => false,
            };
            if (point && Interlocked.Exchange(ref injected, 1) == 0)
                throw new InvalidOperationException("fixture recovery observer");
        };

        var result = await fixture.Service.RollbackToStableAsync("observer recovery").WaitAsync(Budget);

        Assert.Equal(1, injected);
        Assert.True(result.Ok);
        Assert.Equal(fixture.ExpectedBaseline, fixture.Engine.Mutations.Skip(1).ToArray());
        Assert.False(fixture.Service.IsApplying);
        Assert.False(fixture.Service.IsRecovering);
        Assert.False(fixture.Service.WatchdogArmed);
        Assert.Equal(0, fixture.Service.WatchdogSecondsLeft);
        Assert.True(fixture.Service.CanApply);
        Assert.StartsWith("observer recovery：", fixture.Service.ActionStatus);
    }

    [Fact]
    public async Task DisposeWaitsForRealEngineCompletionWithoutFreeingTheSlotEarly()
    {
        await using var fixture = new Fixture();
        var applying = fixture.Service.ApplyAll();
        await fixture.Engine.MutationEntered.Task.WaitAsync(Budget);

        fixture.Service.Dispose();
        Assert.False(fixture.Service.CanApply);
        Assert.False(applying.IsCompleted);
        Assert.False(fixture.Engine.Disposed.Task.IsCompleted);
        Assert.Single(fixture.Engine.Mutations);
        fixture.Engine.ReleaseMutation.Set();
        await Task.WhenAll(applying, fixture.Engine.Disposed.Task).WaitAsync(Budget);

        Assert.Single(fixture.Engine.Mutations);
        Assert.Equal(1, fixture.Engine.MaxConcurrent);
        await fixture.Service.ApplyKnob(fixture.Engine.Knobs[0]).WaitAsync(Budget);
        Assert.Single(fixture.Engine.Mutations);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SettingsService Settings { get; }
        public FakeEngine Engine { get; }
        public OverclockService Service { get; }
        public ManualClock Clock { get; } = new();
        public string ProfilePath { get; } = Path.Combine(Path.GetTempPath(), $"xinspect-oc-{Guid.NewGuid():N}.ocp");
        public string[] ExpectedBaseline { get; }

        public Fixture(bool emptyBaseline = false, bool offsetOnly = false,
            Func<SiliconProbeService.Options, IProgress<SiliconProbeProgress>?, CancellationToken, SiliconAssessment>? siliconProbe = null)
        {
            Settings = new SettingsService(loadFromDisk: false) { AdvancedDiagnosticsEnabled = true };
            Settings.HighRiskHardwareOperationsEnabled = true;
            HardwareOperationGate.Bind(Settings);
            Engine = new FakeEngine(emptyBaseline, offsetOnly);
            Service = new OverclockService(new OcSettings { WatchdogEnabled = true }, Engine, Clock, siliconProbe);
            if (Service.VcoreKnob is { } aggregate) aggregate.Target = 1.2;
            ExpectedBaseline = Engine.Knobs.Select(k => FakeEngine.ApplyText(k.Id, k.Active)).ToArray();
            var profile = new OcProfile { Name = "fixture", Items = Engine.Knobs.Select(k =>
                new OcProfileItem { Id = k.Id, Name = k.RawName, Value = k.Target }).ToList() };
            File.WriteAllText(ProfilePath, JsonSerializer.Serialize(profile));
        }

        public Task Begin(string entry) => entry switch
        {
            "all" => Service.ApplyAll(),
            "planner" => Service.ApplyPlannerAsync(),
            "vcore" => Service.ApplyVcore(),
            "knob" => Service.ApplyKnob(Engine.Knobs[0]),
            "profile" => Service.ImportProfileFrom(ProfilePath),
            "defaults" => Service.RestoreDefaults(),
            "discard" => Service.DiscardAll(),
            "optimizer" => Service.SetSpeedOptimizer(true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };

        public async ValueTask DisposeAsync()
        {
            Engine.ReleaseMutation.Set();
            Engine.ReleaseRead.Set();
            Service.Dispose();
            try { await Engine.Disposed.Task.WaitAsync(Budget); }
            finally
            {
                HardwareOperationGate.Bind(null);
                WriteGate.Reset();
                File.Delete(ProfilePath);
            }
        }
    }

    private sealed class FakeEngine : IOcEngine
    {
        private int _active, _maxActive, _mutations, _readBlocked;
        public int MaxConcurrent => Volatile.Read(ref _maxActive);
        public int BlockMutationAt { get; set; } = 1;
        public bool BlockRead { get; set; }
        public double? CoreVoltageReading { get; set; } = 1;
        public bool ThrowFirstMutation { get; set; }
        public uint? RecoveryFailureId { get; set; }
        public bool ThrowRecoveryFailure { get; set; }
        public bool FailDefaults { get; set; }
        public ConcurrentQueue<string> Mutations { get; } = new();
        public ConcurrentQueue<bool> MutationPermissions { get; } = new();
        public TaskCompletionSource<bool> MutationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseMutation { get; } = new(false);
        public ManualResetEventSlim ReleaseRead { get; } = new(false);
        public IReadOnlyList<OcKnob> Knobs { get; }
        public string Name => "injected fixture";
        public OcEngineStatus Status => OcEngineStatus.Ready;
        public string StatusMessage => "fixture";
        public bool CoreTunable => true;
        public bool BclkTunable => true;
        public bool MemoryTunable => false;
        public bool CacheTunable => false;
        public bool SpeedOptimizerSupported => true;
        public int ProcessorFamily => 6;
        public bool WatchdogPresent => false;
        public bool WatchdogRunning => false;
        public bool WatchdogFailed => false;
        public int SpeedOptimizerState { get; private set; }

        public FakeEngine(bool emptyBaseline, bool offsetOnly)
        {
            Knobs = offsetOnly ? [new OcKnob(5, "Core Voltage Offset", "Processor", "mV",
                OcKnobKind.VoltageOffset, -200, 200, 0, 0, 50, true, false, false, true)]
                : emptyBaseline ? [] :
            [
                Knob(1, "Performance Core Ratio", "Processor", OcKnobKind.CoreRatio, 40, 45),
                Knob(2, "BCLK", "Bus", OcKnobKind.Bclk, 100, 102),
                Knob(3, "Performance Core 0 Voltage Override", "Processor.Turbo.VoltageOverride", OcKnobKind.Voltage, 1, 1.2),
                Knob(4, "Performance Core 1 Voltage Override", "Processor.Turbo.VoltageOverride", OcKnobKind.Voltage, 1, 1.2),
            ];
        }

        private static OcKnob Knob(uint id, string name, string category, OcKnobKind kind, double active, double target)
            => new OcKnob(id, name, category, "", kind, 0, 200, active, active, active, true, false, false, true)
                { Target = target };

        public static string ApplyText(uint id, double value)
            => $"apply:{id}:{value.ToString("G", CultureInfo.InvariantCulture)}";

        private T Tracked<T>(Func<T> action)
        {
            int current = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Volatile.Read(ref _maxActive); }
            while (current > observed && Interlocked.CompareExchange(ref _maxActive, current, observed) != observed);
            try { return action(); }
            finally { Interlocked.Decrement(ref _active); }
        }

        private T Mutate<T>(string description, Func<T> action) => Tracked(() =>
        {
            Mutations.Enqueue(description);
            MutationPermissions.Enqueue(HardwareOperationGate.CanWriteHardware);
            int ordinal = Interlocked.Increment(ref _mutations);
            if (ordinal == BlockMutationAt)
            {
                MutationEntered.TrySetResult(true);
                if (!ReleaseMutation.Wait(Budget)) throw new TimeoutException("Fixture mutation was not released.");
            }
            if (ordinal == 1 && ThrowFirstMutation) throw new InvalidOperationException("dispatched fixture failure");
            return action();
        });

        public bool Initialize() => throw new InvalidOperationException("Tests must not initialize a hardware engine.");
        public void RefreshActives() { }
        public double? ReadCoreVoltage() => Tracked<double?>(() =>
        {
            if (BlockRead && Interlocked.Exchange(ref _readBlocked, 1) == 0)
            {
                ReadEntered.TrySetResult(true);
                if (!ReleaseRead.Wait(Budget)) throw new TimeoutException("Fixture read was not released.");
            }
            return CoreVoltageReading;
        });
        public double? ReadMonitor(params string[] _) => Tracked<double?>(() => null);
        public OcApplyResult Apply(OcKnob knob, double value) => Mutate(ApplyText(knob.Id, value), () =>
        {
            if (RecoveryFailureId == knob.Id && value == knob.Default)
            {
                if (ThrowRecoveryFailure) throw new IOException("fixture recovery failure");
                return OcApplyResult.Fail("fixture recovery failure");
            }
            knob.Active = value;
            return OcApplyResult.Success("fixture applied", value);
        });
        public bool Discard() => Mutate("discard", () => true);
        public OcApplyResult RestoreDefaults() => Mutate("defaults", () => FailDefaults
            ? OcApplyResult.Fail("fixture defaults failed")
            : OcApplyResult.Success("fixture defaults accepted", null));
        public OcApplyResult SetSpeedOptimizer(bool on, bool extreme) => Mutate("optimizer", () =>
        {
            SpeedOptimizerState = on ? 1 : 0;
            return OcApplyResult.Success("fixture optimizer accepted", null);
        });
        public void SetBootRestore(bool on) => Tracked(() => true);
        public void Dispose() => Tracked(() => { Disposed.TrySetResult(true); return true; });
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _state = new();
        private readonly List<ManualTimer> _timers = new();
        private long _stamp;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _stamp);
        public ManualTimer LastTimer { get { lock (_state) return _timers.Last(); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_state) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed, bool fireTimers = true)
        {
            Interlocked.Add(ref _stamp, elapsed.Ticks);
            if (!fireTimers) return;
            ManualTimer[] timers;
            lock (_state) timers = _timers.ToArray();
            foreach (var timer in timers) timer.FireIfDue();
        }

        public sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private readonly object _state = new();
            private readonly ExecutionContext? _context = ExecutionContext.Capture();
            public bool FlowSuppressedAtCreation { get; } = ExecutionContext.IsFlowSuppressed();
            public bool HasCapturedContext => _context is not null;
            private bool _disposed;
            private long? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                lock (_state)
                {
                    if (_disposed) return false;
                    _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetTimestamp() + dueTime.Ticks;
                    return true;
                }
            }
            public void FireIfDue()
            {
                lock (_state)
                {
                    if (_disposed || _due is not long due || clock.GetTimestamp() < due) return;
                    _due = null;
                }
                InvokeCallback();
            }
            public void FireQueuedCallback() => InvokeCallback();
            private void InvokeCallback()
            {
                if (_context is null) callback(state);
                else ExecutionContext.Run(_context.CreateCopy(), _ => callback(state), null);
            }
            public void Dispose() { lock (_state) { _disposed = true; _due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

}
