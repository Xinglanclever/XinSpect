using System.Diagnostics;
using Xunit;

namespace XinSpect.Tests;

public sealed class SensorAlertEngineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    internal static long Tick(double seconds) => checked((long)((100 + seconds) * Stopwatch.Frequency));
    internal static SensorAlertRule Rule(double duration = 0, double cooldown = 60) => new()
    {
        Id = Guid.NewGuid(), Name = "機殼風扇", SensorId = "lhm/device/fan/0", Unit = "RPM",
        Direction = SensorAlertDirection.Above, Threshold = 80, RecoveryThreshold = 70,
        DurationSeconds = duration, CooldownSeconds = cooldown
    };
    internal static SensorReading Reading(double? value = 90, string id = "lhm/device/fan/0", string unit = "RPM",
        FactAvailability availability = FactAvailability.Present, FactTrustLevel trust = FactTrustLevel.Reported)
        => new(id, "device", "Device", "Same label", "Fan", unit, value, availability, trust, "injected provider", Epoch, null, "injected reason");
    internal static SensorFrame Frame(long sequence, double seconds, params SensorReading[] readings)
        => new(sequence, Tick(seconds), Epoch.AddSeconds(seconds), readings);
    private static SensorAlertEvaluation Check(SensorAlertEngine engine, SensorAlertRule rule, SensorFrame frame, double now, bool enabled = true, DateTimeOffset? utc = null)
        => engine.Evaluate(new[] { rule }, frame, enabled, Tick(now), utc ?? Epoch.AddSeconds(now));

    [Fact]
    public void DuplicateFrameCannotMatureDurationAndUtcJumpsDoNotAffectIt()
    {
        var engine = new SensorAlertEngine(); var rule = Rule(5);
        Assert.Equal(SensorAlertPhase.Pending, Check(engine, rule, Frame(1, 0, Reading()), 0).Statuses[0].Phase);
        var sample = Frame(2, 4, Reading());
        Assert.Equal(4, Check(engine, rule, sample, 4).Statuses[0].PendingSeconds);
        var repeated = Check(engine, rule, sample, 10, utc: Epoch.AddYears(2));
        Assert.Equal(SensorAlertPhase.Pending, repeated.Statuses[0].Phase);
        Assert.Empty(repeated.Transitions);
        var active = Check(engine, rule, Frame(3, 5, Reading()), 10, utc: Epoch.AddDays(-3));
        Assert.True(active.Statuses[0].IsActive);
        Assert.True(Assert.Single(active.Transitions).Notify);
    }

    [Fact]
    public void MissingWhileActiveIsUnknownAndOnlyValidRecoveryThresholdClearsLatch()
    {
        var engine = new SensorAlertEngine(); var rule = Rule();
        Check(engine, rule, Frame(1, 0, Reading()), 0);
        var missing = Check(engine, rule, Frame(2, 1), 1);
        Assert.Equal(SensorAlertPhase.Unknown, missing.Statuses[0].Phase);
        Assert.True(missing.Statuses[0].IsActive);
        Assert.DoesNotContain(missing.Transitions, t => t.Kind == SensorAlertTransitionKind.Recovered);
        Assert.True(Check(engine, rule, Frame(3, 2, Reading(75)), 2).Statuses[0].IsActive);
        var recovered = Check(engine, rule, Frame(4, 3, Reading(70)), 3);
        Assert.False(recovered.Statuses[0].IsActive);
        Assert.Equal(SensorAlertTransitionKind.Recovered, Assert.Single(recovered.Transitions).Kind);
    }

    [Fact]
    public void StaleDuplicateRetainsActiveLatchAndDoesNotGenerateRecoveryOrDuplicateUnknownHistory()
    {
        var engine = new SensorAlertEngine(); var rule = Rule(); var frame = Frame(1, 0, Reading());
        Check(engine, rule, frame, 0);
        var stale = Check(engine, rule, frame, 31);
        Assert.True(stale.Statuses[0].IsActive);
        Assert.Null(stale.Statuses[0].Value);
        Assert.Equal(SensorAlertTransitionKind.Unknown, Assert.Single(stale.Transitions).Kind);
        Assert.Empty(Check(engine, rule, frame, 32).Transitions);
    }

    [Theory]
    [InlineData(FactAvailability.Unknown, FactTrustLevel.Reported)]
    [InlineData(FactAvailability.ReadError, FactTrustLevel.Reported)]
    [InlineData(FactAvailability.NotSupported, FactTrustLevel.Measured)]
    [InlineData(FactAvailability.NotApplicable, FactTrustLevel.Reported)]
    [InlineData(FactAvailability.InsufficientPrivilege, FactTrustLevel.Reported)]
    [InlineData(FactAvailability.Present, FactTrustLevel.Unknown)]
    [InlineData(FactAvailability.Present, FactTrustLevel.Speculative)]
    public void UnvalidatedCpuAndOtherUnusableObservationsCannotTriggerOrRecover(FactAvailability availability, FactTrustLevel trust)
    {
        var engine = new SensorAlertEngine(); var rule = Rule();
        var unavailable = Check(engine, rule, Frame(1, 0, Reading(100, availability: availability, trust: trust)), 0);
        Assert.False(unavailable.Statuses[0].IsActive);
        Assert.Equal(SensorAlertPhase.Unknown, unavailable.Statuses[0].Phase);
        Check(engine, rule, Frame(2, 1, Reading()), 1);
        var unconfirmedRecovery = Check(engine, rule, Frame(3, 2, Reading(0, availability: availability, trust: trust)), 2);
        Assert.True(unconfirmedRecovery.Statuses[0].IsActive);
        Assert.DoesNotContain(unconfirmedRecovery.Transitions, c => c.Kind == SensorAlertTransitionKind.Recovered);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(null)]
    public void InvalidNumbersNeverBecomeThresholdEvidence(double? value)
    {
        var result = Check(new(), Rule(), Frame(1, 0, Reading(value)), 0);
        Assert.Equal(SensorAlertPhase.Unknown, result.Statuses[0].Phase);
        Assert.False(result.Statuses[0].IsActive);
    }

    [Fact]
    public void MissingSampleAndLongSamplingGapEachResetPendingDuration()
    {
        var engine = new SensorAlertEngine(); var rule = Rule(5);
        Check(engine, rule, Frame(1, 0, Reading()), 0);
        Check(engine, rule, Frame(2, 4), 4);
        Assert.Equal(0, Check(engine, rule, Frame(3, 5, Reading()), 5).Statuses[0].PendingSeconds);
        Assert.False(Check(engine, rule, Frame(4, 9, Reading()), 9).Statuses[0].IsActive);
        Assert.Equal(0, Check(engine, rule, Frame(5, 40, Reading()), 40).Statuses[0].PendingSeconds);
        Assert.True(Check(engine, rule, Frame(6, 45, Reading()), 45).Statuses[0].IsActive);
    }

    [Fact]
    public void ReturningInsideTriggerLimitResetsPendingWithoutWaitingForRecoveryLimit()
    {
        var engine = new SensorAlertEngine(); var rule = Rule(5);
        Check(engine, rule, Frame(1, 0, Reading()), 0);
        Assert.Equal(SensorAlertPhase.Normal, Check(engine, rule, Frame(2, 4, Reading(75)), 4).Statuses[0].Phase);
        Assert.Equal(0, Check(engine, rule, Frame(3, 5, Reading()), 5).Statuses[0].PendingSeconds);
    }

    [Fact]
    public void BelowRulesTriggerAndRecoverAtInclusiveBoundaries()
    {
        var engine = new SensorAlertEngine();
        var rule = Rule() with { Direction = SensorAlertDirection.Below, Threshold = 500, RecoveryThreshold = 600 };
        Assert.True(Check(engine, rule, Frame(1, 0, Reading(500)), 0).Statuses[0].IsActive);
        Assert.True(Check(engine, rule, Frame(2, 1, Reading(550)), 1).Statuses[0].IsActive);
        Assert.False(Check(engine, rule, Frame(3, 2, Reading(600)), 2).Statuses[0].IsActive);
    }

    [Fact]
    public void UnitChangesSameLabelsAndDuplicateIdsCannotChangeEvidenceIdentity()
    {
        var engine = new SensorAlertEngine(); var rule = Rule();
        Assert.Equal(SensorAlertPhase.Unknown, Check(engine, rule, Frame(1, 0, Reading(100, unit: "%")), 0).Statuses[0].Phase);
        Assert.Equal(SensorAlertPhase.Unknown, Check(engine, rule, Frame(2, 1, Reading(100, id: "other/fan/0")), 1).Statuses[0].Phase);
        var duplicate = Check(engine, rule, Frame(3, 2, Reading(100), Reading(0)), 2);
        Assert.False(duplicate.Statuses[0].IsActive);
        Assert.Contains("重複", duplicate.Statuses[0].Reason);
    }

    [Fact]
    public void CooldownSuppressesNotificationButKeepsReactivationEventAndState()
    {
        var engine = new SensorAlertEngine(); var rule = Rule(cooldown: 60);
        Assert.True(Assert.Single(Check(engine, rule, Frame(1, 0, Reading()), 0).Transitions).Notify);
        Check(engine, rule, Frame(2, 1, Reading(60)), 1);
        var suppressed = Check(engine, rule, Frame(3, 2, Reading()), 2);
        Assert.True(suppressed.Statuses[0].IsActive);
        Assert.False(Assert.Single(suppressed.Transitions).Notify);
        Check(engine, rule, Frame(4, 60, Reading(60)), 60);
        Assert.True(Assert.Single(Check(engine, rule, Frame(5, 61, Reading()), 61).Transitions).Notify);
    }

    [Fact]
    public void MasterSwitchDoesNotClaimRecoveryAndResumptionNeedsANewFrame()
    {
        var engine = new SensorAlertEngine(); var rule = Rule(); var frame = Frame(1, 0, Reading());
        Check(engine, rule, frame, 0);
        var off = Check(engine, rule, frame, 1, enabled: false);
        Assert.Equal(SensorAlertPhase.Disabled, off.Statuses[0].Phase); Assert.Empty(off.Transitions);
        var on = Check(engine, rule, frame, 2);
        Assert.Equal(SensorAlertPhase.Unknown, on.Statuses[0].Phase); Assert.False(on.Statuses[0].IsActive);
        Assert.True(Check(engine, rule, Frame(2, 3, Reading()), 3).Statuses[0].IsActive);
    }

    [Fact]
    public void RuleEditStartsNewEvidenceAndOutOfOrderSafeSamplesDoNotRecover()
    {
        var engine = new SensorAlertEngine(); var rule = Rule();
        Check(engine, rule, Frame(2, 1, Reading()), 1);
        var late = Check(engine, rule, Frame(1, 0, Reading(0)), 2);
        Assert.True(late.Statuses[0].IsActive); Assert.Empty(late.Transitions);
        var edited = rule with { Threshold = 100, RecoveryThreshold = 95 };
        var changed = Check(engine, edited, Frame(3, 3, Reading(90)), 3);
        Assert.False(changed.Statuses[0].IsActive); Assert.Empty(changed.Transitions);
    }

    [Fact]
    public void InvalidRuleBudgetsAndDuplicateIdsAreRejected()
    {
        var rule = Rule();
        foreach (var invalid in new[] { rule with { Threshold = double.NaN }, rule with { RecoveryThreshold = 90 },
            rule with { DurationSeconds = -1 }, rule with { CooldownSeconds = 604801 },
            rule with { SensorId = "" }, rule with { Unit = "RPM\n%" }, rule with { Id = Guid.Empty } })
            Assert.NotNull(invalid.Validate());
        Assert.Throws<ArgumentException>(() => new SensorAlertEngine().Evaluate(new[] { rule, rule }, Frame(1, 0), true, Tick(0), Epoch));
    }

    [Fact]
    public void KnownOldSourceUpdateIsNotMadeFreshByANewProviderRead()
    {
        var rule = Rule(); var frame = Frame(1, 0, Reading() with { SourceUpdatedAtUtc = Epoch.AddMinutes(-2) });
        var result = Check(new(), rule, frame, 0);
        Assert.Equal(SensorAlertPhase.Unknown, result.Statuses[0].Phase);
        Assert.False(result.Statuses[0].IsActive);
        Assert.Contains("來源更新時間", result.Statuses[0].Reason);
    }
}
