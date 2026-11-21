using System.IO;
using Xunit;

namespace XinSpect.Tests;

// Injected worker bodies use gates only: no CPU/memory/storage workload or temporary file.
public sealed class GauntletMultiDomainWorkerLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancellationDoesNotCompleteOrDisposeSharedStateAfterFormerJoinTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        using var releaseStorage = new ManualResetEventSlim(false);
        var entered = Signal();
        var storageStopping = Signal();
        int arrivals = 0, exited = 0, resourceDisposed = 0, sharedStateUsable = 0;
        Action<Action<long>, ManualResetEventSlim, CancellationToken>[] workers = Enumerable.Range(0, 3)
            .Select(index => (Action<Action<long>, ManualResetEventSlim, CancellationToken>)((_, stop, token) =>
            {
                if (Interlocked.Increment(ref arrivals) == 3) entered.TrySetResult(true);
                try
                {
                    stop.Wait();
                    if (index == 2)
                    {
                        storageStopping.TrySetResult(true);
                        if (!releaseStorage.Wait(Budget)) throw new TimeoutException("Injected storage was not released.");
                        if (stop.Wait(0)) Interlocked.Exchange(ref sharedStateUsable, 1);
                    }
                    token.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (index == 2) Interlocked.Exchange(ref resourceDisposed, 1);
                    Interlocked.Increment(ref exited);
                }
            })).ToArray();
        Task<GauntletMultiDomainMeasurement> measurement = Start(workers, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            await storageStopping.Task.WaitAsync(Budget);
            // The old 2000 ms Join silently abandoned a still-running storage worker.
            await Task.Delay(TimeSpan.FromMilliseconds(2200));
            Assert.False(measurement.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref resourceDisposed));
            releaseStorage.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measurement.WaitAsync(Budget));
            Assert.Equal(1, sharedStateUsable);
            Assert.Equal(1, resourceDisposed);
            Assert.Equal(3, exited);
        }
        finally { releaseStorage.Set(); await Observe(measurement); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WorkerFailuresAreCaughtAndAttributedAfterEveryWorkerExits(int failingDomain)
    {
        using var trigger = new ManualResetEventSlim(false);
        var entered = Signal();
        int arrivals = 0, exited = 0;
        Exception failure = failingDomain switch
        {
            0 => new IOException("injected CPU-side failure"),
            1 => new OperationCanceledException("foreign cancellation is a fault"),
            _ => new GauntletMultiDomainValidationException("injected storage integrity failure"),
        };
        Action<Action<long>, ManualResetEventSlim, CancellationToken>[] workers = Enumerable.Range(0, 3)
            .Select(index => (Action<Action<long>, ManualResetEventSlim, CancellationToken>)((_, stop, _) =>
            {
                if (Interlocked.Increment(ref arrivals) == 3) entered.TrySetResult(true);
                try
                {
                    if (index == failingDomain)
                    {
                        if (!trigger.Wait(Budget)) throw new TimeoutException("Injected fault was not released.");
                        throw failure;
                    }
                    stop.Wait();
                }
                finally { Interlocked.Increment(ref exited); }
            })).ToArray();
        Task<GauntletMultiDomainMeasurement> measurement = Start(workers);
        try
        {
            await entered.Task.WaitAsync(Budget);
            trigger.Set();
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => measurement.WaitAsync(Budget));
            Assert.Same(failure, actual.InnerException);
            Assert.Contains(new[] { "CPU", "memory", "storage" }[failingDomain], actual.Message);
            if (failingDomain == 2) Assert.IsType<GauntletMultiDomainValidationException>(actual);
            else Assert.IsType<InvalidOperationException>(actual);
            Assert.Equal(3, exited);
        }
        finally { trigger.Set(); await Observe(measurement); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerFailureIsNotMaskedBySimultaneousCallerCancellation(bool foreignCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        using var trigger = new ManualResetEventSlim(false);
        var entered = Signal();
        int arrivals = 0, exited = 0;
        Exception failure = foreignCancellation
            ? new OperationCanceledException("foreign cancellation remains a worker fault")
            : new IOException("injected I/O and cancellation race");
        Action<Action<long>, ManualResetEventSlim, CancellationToken>[] workers = Enumerable.Range(0, 3)
            .Select(index => (Action<Action<long>, ManualResetEventSlim, CancellationToken>)((_, stop, token) =>
            {
                if (Interlocked.Increment(ref arrivals) == 3) entered.TrySetResult(true);
                try
                {
                    if (index == 2)
                    {
                        if (!trigger.Wait(Budget)) throw new TimeoutException("Injected fault was not released.");
                        cancellation.Cancel();
                        throw failure;
                    }
                    stop.Wait();
                    token.ThrowIfCancellationRequested();
                }
                finally { Interlocked.Increment(ref exited); }
            })).ToArray();
        Task<GauntletMultiDomainMeasurement> measurement = Start(workers, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Budget);
            trigger.Set();
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => measurement.WaitAsync(Budget));
            Assert.Same(failure, actual.InnerException);
            Assert.Equal(3, exited);
        }
        finally { trigger.Set(); await Observe(measurement); }
    }

    [Fact]
    public async Task ProgressFailureStillDrainsBlockedWorkerBeforePropagating()
    {
        using var release = new ManualResetEventSlim(false);
        var stopping = Signal();
        int exited = 0;
        var failure = new InvalidOperationException("injected progress failure");
        Action<Action<long>, ManualResetEventSlim, CancellationToken>[] workers = Enumerable.Range(0, 3)
            .Select(index => (Action<Action<long>, ManualResetEventSlim, CancellationToken>)((_, stop, _) =>
            {
                try
                {
                    stop.Wait();
                    if (index == 2)
                    {
                        stopping.TrySetResult(true);
                        if (!release.Wait(Budget)) throw new TimeoutException("Injected drain was not released.");
                        Assert.True(stop.Wait(0));
                    }
                }
                finally { Interlocked.Increment(ref exited); }
            })).ToArray();
        var context = Context(new InlineProgress(_ => throw failure), 1);
        var measurement = new WindowsMultiDomainEngine().MeasureAsync(context, CancellationToken.None, workers);
        try
        {
            await stopping.Task.WaitAsync(Budget);
            Assert.False(measurement.IsCompleted);
            release.Set();
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => measurement.WaitAsync(Budget));
            Assert.Same(failure, actual);
            Assert.Equal(3, exited);
        }
        finally { release.Set(); await Observe(measurement); }
    }

    [Fact]
    public async Task PartialWorkerStartupFailureDrainsAlreadyStartedWorker()
    {
        using var release = new ManualResetEventSlim(false);
        var stopping = Signal();
        int entered = 0, exited = 0;
        var failure = new InvalidOperationException("injected second Thread.Start boundary failure");
        Action<Action<long>, ManualResetEventSlim, CancellationToken> body = (_, stop, _) =>
        {
            Interlocked.Increment(ref entered);
            try
            {
                stop.Wait();
                stopping.TrySetResult(true);
                if (!release.Wait(Budget)) throw new TimeoutException("Injected startup drain was not released.");
                Assert.True(stop.Wait(0));
            }
            finally { Interlocked.Increment(ref exited); }
        };
        var measurement = new WindowsMultiDomainEngine().MeasureAsync(Context(), CancellationToken.None,
            [body, body, body], index => { if (index == 1) throw failure; });
        try
        {
            await stopping.Task.WaitAsync(Budget);
            Assert.False(measurement.IsCompleted);
            Assert.Equal(1, entered);
            release.Set();
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => measurement.WaitAsync(Budget));
            Assert.Same(failure, actual);
            Assert.Equal(1, exited);
        }
        finally { release.Set(); await Observe(measurement); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static GauntletMultiDomainContext Context(IProgress<DeepBenchProgress>? progress = null, int milliseconds = 60000)
        => new(new GauntletMultiDomainSettings(1, milliseconds, 0), "unused-injected-path",
            progress ?? new InlineProgress(_ => { }), CancellationToken.None);
    private static Task<GauntletMultiDomainMeasurement> Start(
        IReadOnlyList<Action<Action<long>, ManualResetEventSlim, CancellationToken>> workers,
        CancellationToken token = default)
        => new WindowsMultiDomainEngine().MeasureAsync(Context(), token, workers);
    private static async Task Observe(Task task)
    {
        try { await task.WaitAsync(Budget); }
        catch { /* Original fault/cancellation is asserted by the test; always release worker gates first. */ }
    }
    private sealed class InlineProgress(Action<DeepBenchProgress> report) : IProgress<DeepBenchProgress>
    {
        public void Report(DeepBenchProgress value) => report(value);
    }
}
