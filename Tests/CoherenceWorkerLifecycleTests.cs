using System.Collections.Concurrent;
using Xunit;

namespace XinSpect.Tests;

public sealed class CoherenceWorkerLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDrainsWorkersBeforeSharedCountdownCanBeDisposed(bool duringReady)
    {
        using var cancellation = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var finished = new CountdownEvent(1);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var thread = Worker(() =>
        {
            entered.TrySetResult(true);
            if (!release.Wait(Budget)) throw new TimeoutException("fixture release");
            // Cancellation request does not synchronously run the coordinator's finally.
            // Require the real gate signal before this worker can finish its fixture work.
            Assert.True(gate.Wait(Budget));
            finished.Signal();
        }, errors);
        var running = Task.Run(() => WindowsCoherenceLockEngine.RunStartedWorkers([thread], gate, () =>
        {
            if (duringReady) cancellation.Token.WaitHandle.WaitOne();
            else { gate.Set(); finished.Wait(cancellation.Token); }
            cancellation.Token.ThrowIfCancellationRequested();
        }, cancellation.Token, () => errors.ToArray()));
        try
        {
            await entered.Task.WaitAsync(Budget);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Budget));
            Assert.False(thread.IsAlive);
            Assert.True(finished.IsSet);
            Assert.Empty(errors);
        }
        finally { release.Set(); try { await running; } catch { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorOrPartialStartFailureDrainsBeforePreservingPrimaryFault(bool partialStart)
    {
        using var gate = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("injected coordinator/start failure");
        Thread first = Worker(() => { gate.Wait(); stopped.TrySetResult(true); release.Wait(); }, errors);
        Thread second = Worker(() => gate.Wait(), errors);
        var running = Task.Run(() => WindowsCoherenceLockEngine.RunStartedWorkers([first, second], gate,
            () => throw failure, CancellationToken.None, () => errors.ToArray(),
            index => { if (partialStart && index == 1) throw failure; }));
        try
        {
            await stopped.Task.WaitAsync(Budget);
            Assert.False(running.IsCompleted);
            release.Set();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(Budget)));
            Assert.False(first.IsAlive);
            Assert.False(second.IsAlive);
        }
        finally { release.Set(); try { await running; } catch { } }
    }

    [Fact]
    public void GenuineWorkerFaultWinsOverMatchingCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var gate = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("injected affinity/workload failure");
        Thread worker = Worker(() => { cancellation.Cancel(); throw failure; }, errors);
        var actual = Assert.Throws<InvalidOperationException>(() => WindowsCoherenceLockEngine.RunStartedWorkers(
            [worker], gate, () => cancellation.Token.ThrowIfCancellationRequested(), cancellation.Token, () => errors.ToArray()));
        Assert.Same(failure, actual);
        Assert.False(worker.IsAlive);
    }

    [Fact]
    public void ForeignWorkerCancellationIsNotReportedAsUserCancellation()
    {
        using var gate = new ManualResetEventSlim(false);
        var errors = new ConcurrentQueue<Exception>();
        var failure = new OperationCanceledException("foreign worker operation");
        var worker = Worker(() => throw failure, errors);
        var actual = Assert.Throws<InvalidOperationException>(() => WindowsCoherenceLockEngine.RunStartedWorkers(
            [worker], gate, () => { }, CancellationToken.None, () => errors.ToArray()));
        Assert.Same(failure, actual.InnerException);
        Assert.False(worker.IsAlive);
    }

    private static Thread Worker(Action body, ConcurrentQueue<Exception> errors)
        => new(() => { try { body(); } catch (Exception error) { errors.Enqueue(error); } }) { IsBackground = true };
}
