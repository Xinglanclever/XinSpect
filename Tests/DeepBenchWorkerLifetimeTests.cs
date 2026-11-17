using System.Collections.Concurrent;
using Xunit;

namespace XinSpect.Tests;

public sealed class DeepBenchWorkerLifetimeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CancelledAcquisitionDoesNotAllocateOrReleaseAnotherOwnersLock()
    {
        using var runLock = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int allocations = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeepBenchWorkerLifetime.RunAsync<object, int>(
            runLock, 0, () => { allocations++; return 123; }, _ => throw new Exception("unexpected free"),
            () => new object(), _ => { }, null, (_, _) => throw new Exception("unexpected worker"),
            (_, _) => Task.FromResult(1), cancellation.Token));
        Assert.Equal(0, allocations);
        Assert.Equal(0, runLock.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllocationOrGateCreationFailureReleasesOnlyAcquiredResources(bool gateFailure)
    {
        using var runLock = new SemaphoreSlim(1, 1);
        var failure = new OutOfMemoryException("injected allocation/gate failure");
        int frees = 0;
        bool measured = false;
        var actual = await Assert.ThrowsAsync<OutOfMemoryException>(() => DeepBenchWorkerLifetime.RunAsync<object, int>(
            runLock, 0, () => gateFailure ? 123 : throw failure,
            buffer => { Assert.Equal((nint)123, buffer); frees++; },
            () => throw failure, _ => { }, null, (_, _) => throw new Exception("unexpected worker"),
            (_, _) => { measured = true; return Task.FromResult(1); }, CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.False(measured);
        Assert.Equal(gateFailure ? 1 : 0, frees);
        Assert.Equal(1, runLock.CurrentCount);
        // A subsequent session can acquire the same lock without waiting.
        Assert.True(runLock.Wait(0));
        runLock.Release();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task PartialStartDrainsOnlyStartedWorkersBeforeDisposingOrReleasing(int failingIndex, bool factoryFailure)
    {
        using var runLock = new SemaphoreSlim(1, 1);
        using var gate = new FixtureGate();
        using var release = new ManualResetEventSlim(false);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerErrors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("injected start failure");
        var threads = new List<Thread>();
        int frees = 0;
        bool measured = false;
        Task<int> running = Task.Run(() => DeepBenchWorkerLifetime.RunAsync<FixtureGate, int>(
            runLock, 2, () => 123, _ => { Assert.All(threads, thread => Assert.False(thread.IsAlive)); frees++; },
            () => gate, item => item.Stop.Set(), item => item.MarkDisposed(),
            (item, index) =>
            {
                if (factoryFailure && index == failingIndex) throw failure;
                Thread thread = SafeWorker(() =>
                {
                    Assert.True(item.Stop.Wait(Budget));
                    stopped.TrySetResult(true);
                    Assert.True(release.Wait(Budget));
                    Assert.False(item.Disposed);
                }, workerErrors);
                threads.Add(thread);
                return thread;
            },
            (_, _) => { measured = true; return Task.FromResult(1); }, CancellationToken.None,
            index => { if (!factoryFailure && index == failingIndex) throw failure; }));
        try
        {
            if (failingIndex == 1)
            {
                await stopped.Task.WaitAsync(Budget);
                Assert.False(running.IsCompleted);
                Assert.False(gate.Disposed);
                Assert.Equal(0, frees);
                Assert.Equal(0, runLock.CurrentCount);
            }
            release.Set();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(Budget)));
            Assert.False(measured);
            Assert.True(gate.Disposed);
            Assert.Equal(1, frees);
            Assert.Equal(1, runLock.CurrentCount);
            Assert.Equal(factoryFailure ? failingIndex : failingIndex + 1, threads.Count);
            Assert.All(threads, thread => Assert.False(thread.IsAlive));
            if (!factoryFailure)
                Assert.True((threads[failingIndex].ThreadState & ThreadState.Unstarted) != 0);
            Assert.Empty(workerErrors);
        }
        finally
        {
            release.Set();
            try { await running; } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasurementFailureOrCancellationDrainsBeforeReturningOriginalFault(bool cancelled)
    {
        using var runLock = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        using var gate = new FixtureGate();
        using var release = new ManualResetEventSlim(false);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerErrors = new ConcurrentQueue<Exception>();
        Exception failure = cancelled ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("measurement");
        Thread? worker = null;
        int frees = 0;
        Task<int> running = Task.Run(() => DeepBenchWorkerLifetime.RunAsync<FixtureGate, int>(
            runLock, 1, () => 123, _ => { Assert.False(worker!.IsAlive); frees++; },
            () => gate, item => item.Stop.Set(), item => item.MarkDisposed(),
            (item, _) => worker = SafeWorker(() =>
            {
                Assert.True(item.Stop.Wait(Budget));
                stopped.TrySetResult(true);
                Assert.True(release.Wait(Budget));
                Assert.False(item.Disposed);
            }, workerErrors),
            (_, _) => { if (cancelled) cancellation.Cancel(); return Task.FromException<int>(failure); }, cancellation.Token));
        try
        {
            await stopped.Task.WaitAsync(Budget);
            Assert.False(running.IsCompleted);
            Assert.Equal(0, frees);
            Assert.Equal(0, runLock.CurrentCount);
            release.Set();
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => running.WaitAsync(Budget));
            Assert.Same(failure, actual);
            Assert.Equal(cancelled, running.IsCanceled);
            Assert.True(gate.Disposed);
            Assert.Equal(1, frees);
            Assert.Equal(1, runLock.CurrentCount);
            Assert.Empty(workerErrors);
        }
        finally
        {
            release.Set();
            try { await running; } catch { }
        }
    }

    [Fact]
    public async Task CleanupFaultsStillDrainAndAttemptEveryReleaseWithoutLosingPrimary()
    {
        using var runLock = new SemaphoreSlim(1, 1);
        using var gate = new FixtureGate();
        var workerErrors = new ConcurrentQueue<Exception>();
        var primary = new InvalidOperationException("measurement failure");
        var stopFailure = new InvalidOperationException("stop diagnostic after signal");
        var disposeFailure = new InvalidOperationException("dispose diagnostic");
        var freeFailure = new InvalidOperationException("free diagnostic");
        Thread? worker = null;
        var actual = await Assert.ThrowsAsync<AggregateException>(() => DeepBenchWorkerLifetime.RunAsync<FixtureGate, int>(
            runLock, 1, () => 123, _ => { Assert.False(worker!.IsAlive); throw freeFailure; },
            () => gate,
            item => { item.Stop.Set(); throw stopFailure; },
            item => { Assert.False(worker!.IsAlive); item.MarkDisposed(); throw disposeFailure; },
            (item, _) => worker = SafeWorker(() => Assert.True(item.Stop.Wait(Budget)), workerErrors),
            (_, _) => Task.FromException<int>(primary), CancellationToken.None));
        Assert.Equal(new Exception[] { primary, stopFailure, disposeFailure, freeFailure }, actual.InnerExceptions);
        Assert.True(gate.Disposed);
        Assert.Equal(1, runLock.CurrentCount);
        Assert.Empty(workerErrors);
    }

    [Fact]
    public async Task InterruptedJoinKeepsOwnershipUntilWorkerExitThenReportsInterruption()
    {
        using var runLock = new SemaphoreSlim(1, 1);
        using var gate = new FixtureGate();
        using var release = new ManualResetEventSlim(false);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource<ThreadInterruptedException>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerErrors = new ConcurrentQueue<Exception>();
        Thread? worker = null;
        int frees = 0;
        // Own this coordinator thread so an interrupt cannot escape into a pooled worker.
        Thread coordinator = new(() =>
        {
            try
            {
                DeepBenchWorkerLifetime.RunAsync<FixtureGate, int>(
                    runLock, 1, () => 123, _ => { Assert.False(worker!.IsAlive); frees++; },
                    () => gate, item => { item.Stop.Set(); stopped.TrySetResult(true); }, item => item.MarkDisposed(),
                    (item, _) => worker = SafeWorker(() =>
                    {
                        Assert.True(item.Stop.Wait(Budget));
                        Assert.True(release.Wait(Budget));
                        Assert.False(item.Disposed);
                    }, workerErrors),
                    (_, _) => Task.FromResult(1), CancellationToken.None,
                    onJoinInterrupted: error => interrupted.TrySetResult(error)).GetAwaiter().GetResult();
                outcome.TrySetResult(null);
            }
            catch (Exception error) { outcome.TrySetResult(error); }
        }) { IsBackground = true };
        coordinator.Start();
        try
        {
            await stopped.Task.WaitAsync(Budget);
            // A pending interrupt also interrupts the next Join if it has not blocked yet.
            coordinator.Interrupt();
            ThreadInterruptedException reported = await interrupted.Task.WaitAsync(Budget);
            Assert.False(outcome.Task.IsCompleted);
            Assert.False(gate.Disposed);
            Assert.Equal(0, frees);
            Assert.Equal(0, runLock.CurrentCount);
            release.Set();
            Assert.Same(reported, await outcome.Task.WaitAsync(Budget));
            Assert.True(coordinator.Join(Budget));
            Assert.True(gate.Disposed);
            Assert.Equal(1, frees);
            Assert.Equal(1, runLock.CurrentCount);
            Assert.Empty(workerErrors);
        }
        finally
        {
            release.Set();
            await outcome.Task;
            coordinator.Join();
        }
    }

    [Fact]
    public async Task SuccessReturnsMeasuredValueAfterWorkerExitAndCleanup()
    {
        using var runLock = new SemaphoreSlim(1, 1);
        using var gate = new FixtureGate();
        var workerErrors = new ConcurrentQueue<Exception>();
        Thread? worker = null;
        bool freed = false;
        int result = await DeepBenchWorkerLifetime.RunAsync<FixtureGate, int>(
            runLock, 1, () => 123, _ => { Assert.False(worker!.IsAlive); freed = true; },
            () => gate, item => item.Stop.Set(), item => item.MarkDisposed(),
            (item, _) => worker = SafeWorker(() => Assert.True(item.Stop.Wait(Budget)), workerErrors),
            (_, buffer) => { Assert.Equal((nint)123, buffer); return Task.FromResult(42); }, CancellationToken.None);
        Assert.Equal(42, result);
        Assert.True(freed);
        Assert.True(gate.Disposed);
        Assert.Equal(1, runLock.CurrentCount);
        Assert.Empty(workerErrors);
    }

    private static Thread SafeWorker(Action body, ConcurrentQueue<Exception> errors) =>
        new(() => { try { body(); } catch (Exception error) { errors.Enqueue(error); } }) { IsBackground = true };

    private sealed class FixtureGate : IDisposable
    {
        internal readonly ManualResetEventSlim Stop = new(false);
        internal bool Disposed { get; private set; }
        internal void MarkDisposed() => Disposed = true;
        public void Dispose() => Stop.Dispose();
    }
}
