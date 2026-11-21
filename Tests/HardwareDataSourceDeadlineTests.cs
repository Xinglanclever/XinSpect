using System.IO;
using Xunit;

namespace XinSpect.Tests;

public class HardwareDataSourceDeadlineTests
{
    private static HardwareDataSourceDescriptor Descriptor(bool canCancel = false) => new(
        "test.deadline", "1.0.0", "test.deadline",
        [new("cpu.cores", "CPU", "Cores", "count")],
        HardwareDataSourcePermission.StandardUser, TimeSpan.FromMilliseconds(30), canCancel);

    [Theory]
    [InlineData(29, false, HardwareDataSourceRunStatus.Completed)]
    [InlineData(29, true, HardwareDataSourceRunStatus.Completed)]
    [InlineData(30, false, HardwareDataSourceRunStatus.TimedOut)]
    [InlineData(30, true, HardwareDataSourceRunStatus.TimedOut)]
    [InlineData(31, false, HardwareDataSourceRunStatus.TimedOut)]
    [InlineData(31, true, HardwareDataSourceRunStatus.TimedOut)]
    public async Task TimerCallbackDelayCannotAdmitLateResults(int elapsedMs, bool canCancel,
        HardwareDataSourceRunStatus expected)
    {
        var clock = new DeferredTimerClock();
        var source = new Source(Descriptor(canCancel), (_, token) =>
        {
            Assert.False(token.IsCancellationRequested); // Fake timer has not dispatched.
            clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));
            return Task.FromResult<IReadOnlyList<HardwareFact>>([]);
        });
        var result = await HardwareDataSourceRunner.RunAsync(source, clock);
        Assert.Equal(expected, result.Status);
        if (expected == HardwareDataSourceRunStatus.TimedOut)
        {
            Assert.Equal("Timeout", result.FailureCode);
            Assert.All(result.Facts, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
        }
    }

    [Fact]
    public async Task LateFaultUsesTimeoutInsteadOfLeakingItsException()
    {
        var clock = new DeferredTimerClock();
        var source = new Source(Descriptor(), (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(31));
            throw new IOException("private device path");
        });
        var result = await HardwareDataSourceRunner.RunAsync(source, clock);
        Assert.Equal(HardwareDataSourceRunStatus.TimedOut, result.Status);
        Assert.Equal("Timeout", result.FailureCode);
        Assert.DoesNotContain("private", Assert.Single(result.Facts).UnavailableReason);
    }

    [Fact]
    public async Task TimelySourceDoesNotExpireWhenCallerResumesLate()
    {
        var clock = new DeferredTimerClock
        {
            BeforeSecondUtcRead = c => c.Advance(TimeSpan.FromMinutes(1)),
        };
        var source = new Source(Descriptor(), (_, _) => Task.FromResult<IReadOnlyList<HardwareFact>>([]));
        var result = await HardwareDataSourceRunner.RunAsync(source, clock);
        Assert.True(clock.GetTimestamp() > TimeSpan.FromMilliseconds(30).Ticks);
        Assert.Equal(HardwareDataSourceRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task CallerCancellationWinsEvenIfSourceReturnsAResult()
    {
        using var cancel = new CancellationTokenSource();
        var clock = new DeferredTimerClock();
        var source = new Source(Descriptor(), (_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult<IReadOnlyList<HardwareFact>>([]);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HardwareDataSourceRunner.RunAsync(source, clock, cancel.Token));
    }

    [Fact]
    public async Task WallClockRollbackDoesNotExtendMonotonicBudget()
    {
        var clock = new DeferredTimerClock();
        var source = new Source(Descriptor(), (_, _) =>
        {
            clock.UtcShift = TimeSpan.FromHours(-3);
            clock.Advance(TimeSpan.FromMilliseconds(31));
            return Task.FromResult<IReadOnlyList<HardwareFact>>([]);
        });
        var result = await HardwareDataSourceRunner.RunAsync(source, clock);
        Assert.True(result.CompletedAtUtc < result.StartedAtUtc);
        Assert.Equal(HardwareDataSourceRunStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task BudgetExpiredBeforeWorkerStartsDoesNotInvokeSource()
    {
        bool invoked = false;
        var clock = new DeferredTimerClock
        {
            BeforeSecondTimestampRead = c => c.Advance(TimeSpan.FromMilliseconds(31)),
        };
        var source = new Source(Descriptor(), (_, _) =>
        {
            invoked = true;
            return Task.FromResult<IReadOnlyList<HardwareFact>>([]);
        });
        var result = await HardwareDataSourceRunner.RunAsync(source, clock);
        Assert.False(invoked);
        Assert.Equal(HardwareDataSourceRunStatus.TimedOut, result.Status);
    }

    private sealed class Source(HardwareDataSourceDescriptor descriptor,
        Func<DateTimeOffset, CancellationToken, Task<IReadOnlyList<HardwareFact>>> collect) : IHardwareDataSource
    {
        public HardwareDataSourceDescriptor Descriptor => descriptor;
        public Task<IReadOnlyList<HardwareFact>> CollectAsync(DateTimeOffset at, CancellationToken token)
            => collect(at, token);
    }

    /// <summary>Advance monotonic time while deliberately holding all timer callbacks.</summary>
    private sealed class DeferredTimerClock : TimeProvider
    {
        private long _ticks;
        private int _utcReads, _timestampReads;
        public TimeSpan UtcShift { get; set; }
        public Action<DeferredTimerClock>? BeforeSecondUtcRead { get; init; }
        public Action<DeferredTimerClock>? BeforeSecondTimestampRead { get; init; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _timestampReads) == 2) BeforeSecondTimestampRead?.Invoke(this);
            return Interlocked.Read(ref _ticks);
        }
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _utcReads) == 2) BeforeSecondUtcRead?.Invoke(this);
            return new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero) + UtcShift;
        }
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new DeferredTimer();

        private sealed class DeferredTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
