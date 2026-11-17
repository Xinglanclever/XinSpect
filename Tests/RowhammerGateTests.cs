using Xunit;

namespace XinSpect.Tests;

/// <summary>Public admission regression with fake allocator/runner only; never invokes RunPressure.</summary>
[Collection(HardwareOperationPolicyCollection.Name)]
[Trait("Category", "InjectedAdmission")]
public sealed class RowhammerGateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void BothLevelsAreRequiredAtBothPublicEntrypoints(bool advanced, bool highRisk, bool allowed)
    {
        using var fixture = new RowhammerFakeFixture(advanced, highRisk);
        if (allowed)
        {
            RowhammerProbeService.RunConsentedProbe(true, 2);
            RowhammerProbeService.RunMultiRound(true, 2, 2);
            Assert.Equal(3, fixture.AllocationCalls);
            Assert.Equal(3, fixture.WorkerCalls);
            Assert.Equal(2 * 1024 * 1024, fixture.LastLength);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunConsentedProbe(true, 2));
            Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunMultiRound(true, 2, 2));
            Assert.Equal(0, fixture.AllocationCalls);
            Assert.Equal(0, fixture.WorkerCalls);
        }
    }

    [Fact]
    public void UnboundPolicyRejectsBothBeforeAllocatorAndWorker()
    {
        using var fixture = new RowhammerFakeFixture();
        HardwareOperationGate.Bind(null);
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunConsentedProbe(true, 2));
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunMultiRound(true, 2, 2));
        Assert.Equal(0, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsentIsRequiredPerInvocationEvenAfterPreviousSuccess(bool multi)
    {
        using var fixture = new RowhammerFakeFixture();
        Invoke(multi, true);
        int allocations = fixture.AllocationCalls, workers = fixture.WorkerCalls;
        var ex = Assert.Throws<InvalidOperationException>(() => Invoke(multi, false));
        Assert.Contains("本次明確同意", ex.Message);
        Assert.Equal(allocations, fixture.AllocationCalls);
        Assert.Equal(workers, fixture.WorkerCalls);
    }

    [Fact]
    public void MissingConsentStillPrecedesModeAndSizeRejection()
    {
        using var fixture = new RowhammerFakeFixture(false, false);
        foreach (bool multi in new[] { false, true })
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Invoke(multi, false, 0));
            Assert.Contains("本次明確同意", ex.Message);
        }
        Assert.Equal(0, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 0u)]
    [InlineData(false, 2048u)]
    [InlineData(true, 4096u)]
    public void R367SizeValidationStillPrecedesAllocation(bool multi, uint target)
    {
        using var fixture = new RowhammerFakeFixture();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Invoke(multi, true, target));
        Assert.Equal("targetMegabytes", ex.ParamName);
        Assert.Equal(target, Assert.IsType<uint>(ex.ActualValue));
        Assert.Equal(0, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingEitherLevelAfterAllocationRejectsBeforeWorker(bool closeFirst)
    {
        using var fixture = new RowhammerFakeFixture();
        fixture.OnAllocate = () =>
        {
            if (closeFirst) fixture.Settings.AdvancedDiagnosticsEnabled = false;
            else fixture.Settings.HighRiskHardwareOperationsEnabled = false;
        };
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunConsentedProbe(true, 2));
        Assert.Equal(1, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosingEitherLevelInFirstRoundPreventsLaterAllocation(bool closeFirst)
    {
        using var fixture = new RowhammerFakeFixture();
        fixture.OnRun = () =>
        {
            if (closeFirst) fixture.Settings.AdvancedDiagnosticsEnabled = false;
            else fixture.Settings.HighRiskHardwareOperationsEnabled = false;
        };
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunMultiRound(true, 3, 2));
        Assert.Equal(1, fixture.AllocationCalls);
        Assert.Equal(1, fixture.WorkerCalls);
    }

    [Fact]
    public void RecoveryWritePermissionCannotAdmitPressure()
    {
        using var fixture = new RowhammerFakeFixture(false, false);
        using var recovery = HardwareOperationGate.BeginRecoveryOperation();
        Assert.True(HardwareOperationGate.CanWriteHardware);
        Assert.False(HardwareOperationGate.IsHighRiskModeEnabled);
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunConsentedProbe(true, 2));
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunMultiRound(true, 2, 2));
        Assert.Equal(0, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Fact]
    public void ZeroRoundCallStillRequiresModeAndValidSize()
    {
        using var fixture = new RowhammerFakeFixture(false, false);
        Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunMultiRound(true, 0, 2));
        fixture.Settings.AdvancedDiagnosticsEnabled = true;
        fixture.Settings.HighRiskHardwareOperationsEnabled = true;
        Assert.Throws<ArgumentOutOfRangeException>(() => RowhammerProbeService.RunMultiRound(true, 0, 0));
        Assert.Equal(0, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Fact]
    public void FakeMultiRoundPreservesNonzeroFlipAggregation()
    {
        using var fixture = new RowhammerFakeFixture();
        fixture.FlipCount = call => call == 2 ? 3 : 0;
        var result = RowhammerProbeService.RunMultiRound(true, 3, 2);
        Assert.Equal(3, result.Rounds);
        Assert.Equal(2UL * 1024 * 1024, result.AllocatedBytesPerRound);
        Assert.Equal(3, result.TotalFlips);
        Assert.True(result.AnyFlip);
        Assert.Equal(3, fixture.AllocationCalls);
        Assert.Equal(3, fixture.WorkerCalls);
    }

    [Fact]
    public void AllocationFailureRemainsObservableAndNeverStartsWorker()
    {
        using var fixture = new RowhammerFakeFixture();
        var expected = new OutOfMemoryException("synthetic allocation failure");
        fixture.OnAllocate = () => throw expected;
        var actual = Assert.Throws<OutOfMemoryException>(() => RowhammerProbeService.RunConsentedProbe(true, 2));
        Assert.Same(expected, actual);
        Assert.Equal(1, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Fact]
    public async Task FakeScopeFlowsToQueuedPublicCall()
    {
        using var fixture = new RowhammerFakeFixture();
        var result = await Task.Run(() => RowhammerProbeService.RunConsentedProbe(true, 2));
        Assert.Equal(2u * 1024 * 1024, result.AllocatedBytes);
        Assert.Equal(1, fixture.AllocationCalls);
        Assert.Equal(1, fixture.WorkerCalls);
    }

    [Fact]
    public async Task QueuedPublicCallRechecksRevokedModeBeforeAllocation()
    {
        using var fixture = new RowhammerFakeFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = Task.Run(async () =>
        {
            await release.Task;
            return RowhammerProbeService.RunConsentedProbe(true, 2);
        });
        fixture.Settings.AdvancedDiagnosticsEnabled = false;
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => { await call; });
        Assert.Equal(0, fixture.AllocationCalls);
        Assert.Equal(0, fixture.WorkerCalls);
    }

    [Fact]
    public void WorkerFailureIsObservableWithoutRetry()
    {
        using var fixture = new RowhammerFakeFixture();
        var expected = new InvalidOperationException("synthetic worker failure");
        fixture.OnRun = () => throw expected;
        var actual = Assert.Throws<InvalidOperationException>(() => RowhammerProbeService.RunMultiRound(true, 3, 2));
        Assert.Same(expected, actual);
        Assert.Equal(1, fixture.AllocationCalls);
        Assert.Equal(1, fixture.WorkerCalls);
    }

    [Fact]
    public void NestedFakeScopeRestoresPriorFakeWithoutTouchingProduction()
    {
        using var fixture = new RowhammerFakeFixture();
        using (RowhammerProbeService.UseTestExecution(_ => Array.Empty<byte>(),
            (_, bytes) => new RowhammerProbeResult(bytes, 7, 4, 0)))
        {
            var inner = RowhammerProbeService.RunConsentedProbe(true, 2);
            Assert.Equal(7u, inner.Iterations);
            Assert.Equal(4, inner.Flips);
            Assert.Equal(0, fixture.WorkerCalls);
        }
        var outer = RowhammerProbeService.RunConsentedProbe(true, 2);
        Assert.Equal(64u, outer.Iterations);
        Assert.Equal(0, outer.Flips);
        Assert.Equal(1, fixture.WorkerCalls);
    }

    private static void Invoke(bool multi, bool consent, uint target = 2)
    {
        if (multi) RowhammerProbeService.RunMultiRound(consent, 2, target);
        else RowhammerProbeService.RunConsentedProbe(consent, target);
    }
}

/// <summary>Every pressure-capable test call must remain inside this scoped fake.</summary>
internal sealed class RowhammerFakeFixture : IDisposable
{
    private readonly IDisposable _execution;
    internal SettingsService Settings { get; }
    internal int AllocationCalls { get; private set; }
    internal int WorkerCalls { get; private set; }
    internal int LastLength { get; private set; }
    internal Action? OnAllocate { get; set; }
    internal Action? OnRun { get; set; }
    internal Func<int, int> FlipCount { get; set; } = _ => 0;

    internal RowhammerFakeFixture(bool advanced = true, bool highRisk = true)
    {
        Settings = new SettingsService(loadFromDisk: false)
        {
            AdvancedDiagnosticsEnabled = advanced,
            HighRiskHardwareOperationsEnabled = highRisk,
        };
        HardwareOperationGate.Bind(Settings);
        _execution = RowhammerProbeService.UseTestExecution(length =>
        {
            AllocationCalls++;
            LastLength = length;
            OnAllocate?.Invoke();
            return Array.Empty<byte>(); // No target-sized buffer allocation.
        }, (_, bytes) =>
        {
            WorkerCalls++;
            OnRun?.Invoke();
            return new RowhammerProbeResult(bytes, 64, FlipCount(WorkerCalls), 0);
        });
    }

    public void Dispose()
    {
        _execution.Dispose();
        HardwareOperationGate.Bind(null);
    }
}
