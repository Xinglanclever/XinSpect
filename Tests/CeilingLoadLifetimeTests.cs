using System.Collections.Concurrent;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

// Every thread is fake/idle. Body-fault tests throw at the first policy check,
// before any Integer/AVX slice. No MainViewModel, MSR reader, native or CPU load.
[Collection(HardwareOperationPolicyCollection.Name)]
public sealed class CeilingLoadLifetimeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ClosedPolicyPublicStartRefusesBeforePureMeasurementSeam()
    {
        HardwareOperationGate.Bind(new SettingsService(loadFromDisk: false));
        int measurements = 0, factories = 0;
        var session = new CeilingLoadSession(createWorker: (_, _) =>
        {
            factories++;
            throw new InvalidOperationException("worker forbidden");
        });
        var service = new CeilingService(session, (_, _, _, _) =>
        {
            measurements++;
            throw new InvalidOperationException("native route would be forbidden");
        });
        try
        {
            service.Start();
            await service.CurrentRun.WaitAsync(Budget);
            Assert.False(service.IsRunning);
            Assert.Equal(0, measurements);
            Assert.Equal(0, factories);
            Assert.Contains("第一級", service.Status);
            Assert.Contains("未執行", service.Status);
        }
        finally { HardwareOperationGate.Bind(null); }
    }

    [Fact]
    public async Task PublicStaticEntryRemainsPassiveWithClosedPolicy()
    {
        int measurements = 0, factories = 0;
        var session = new CeilingLoadSession(() => false, (_, _) =>
        {
            factories++;
            throw new InvalidOperationException("worker forbidden");
        });
        var service = new CeilingService(session, (withLoad, _, _, _) =>
        {
            Assert.False(withLoad);
            Interlocked.Increment(ref measurements);
            return new CeilingService.Result { StatusText = "pure static fixture" };
        });
        service.LoadStatic();
        await service.CurrentRun.WaitAsync(Budget);
        Assert.Equal(1, measurements);
        Assert.Equal(0, factories);
        Assert.Equal("pure static fixture", service.Status);
        Assert.False(service.IsRunning);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task PartialFactoryAndStartBoundaryFaultDrainsOwnedWorkersAndAllowsReuse(
        int failingIndex, bool factoryFailure)
    {
        using var release = new ManualResetEventSlim(false);
        var stopped = Signal();
        var errors = new ConcurrentQueue<Exception>();
        var threads = new List<Thread>();
        var failure = new InvalidOperationException("injected factory/start-boundary failure");
        bool failing = true, measured = false;
        int factoryIndex = 0;
        CancellationToken firstToken = default;
        var session = new CeilingLoadSession(() => true, (token, _) =>
        {
            int index = factoryIndex++;
            if (failing && factoryFailure && index == failingIndex) throw failure;
            if (index == 0) firstToken = token;
            Thread worker = IdleWorker(token, release, stopped, errors);
            threads.Add(worker);
            return worker;
        }, index =>
        {
            if (failing && !factoryFailure && index == failingIndex) throw failure;
        });
        Task<int> pending = Task.Run(() => session.ObserveAsync(CeilingLoad.Integer, 2,
            (_, _) => { measured = true; return Task.FromResult(42); }, CancellationToken.None));
        try
        {
            if (failingIndex == 1)
            {
                await stopped.Task.WaitAsync(Budget);
                Assert.False(pending.IsCompleted);
                // The linked source is still owned while a successfully started worker lives.
                Assert.True(firstToken.WaitHandle.WaitOne(0));
            }
            release.Set();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Budget)));
            Assert.False(measured);
            Assert.Equal(factoryFailure ? failingIndex : failingIndex + 1, threads.Count);
            Assert.All(threads, worker => Assert.False(worker.IsAlive));
            if (!factoryFailure)
                Assert.True((threads[failingIndex].ThreadState & ThreadState.Unstarted) != 0);
            if (threads.Count > 0) Assert.Throws<ObjectDisposedException>(() => { _ = firstToken.WaitHandle; });
            Assert.Empty(errors);

            failing = false;
            factoryIndex = 0;
            Assert.Equal(42, await session.ObserveAsync(CeilingLoad.Integer, 1,
                (_, _) => Task.FromResult(42), CancellationToken.None).WaitAsync(Budget));
            Assert.All(threads, worker => Assert.False(worker.IsAlive));
            Assert.Empty(errors);
        }
        finally
        {
            release.Set();
            try { await pending; } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationInsideFactoryOrBeforeStartNeverStartsReturnedWorker(bool insideBeforeStart)
    {
        bool allowed = true;
        Thread? worker = null;
        var session = new CeilingLoadSession(() => Volatile.Read(ref allowed), (_, _) =>
        {
            worker = new Thread(() => throw new Exception("must stay unstarted")) { IsBackground = true };
            if (!insideBeforeStart) Volatile.Write(ref allowed, false);
            return worker;
        }, _ => { if (insideBeforeStart) Volatile.Write(ref allowed, false); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ObserveAsync<int>(
            CeilingLoad.Integer, 1, (_, _) => throw new Exception("observe forbidden"), CancellationToken.None));
        Assert.NotNull(worker);
        Assert.False(worker!.IsAlive);
        Assert.True((worker.ThreadState & ThreadState.Unstarted) != 0);
    }

    [Fact]
    public async Task BaselineCreatesNoWorkerAndCompletedWindowsCanBeReused()
    {
        int factories = 0, observed = 0;
        var session = new CeilingLoadSession(() => true, (_, _) =>
        {
            factories++;
            throw new Exception("baseline worker forbidden");
        });
        for (int index = 0; index < 2; index++)
        {
            Assert.Equal(7, await session.ObserveAsync(CeilingLoad.Baseline, 123,
                (token, check) =>
                {
                    check();
                    Assert.False(token.IsCancellationRequested);
                    observed++;
                    return Task.FromResult(7);
                }, CancellationToken.None).WaitAsync(Budget));
        }
        Assert.Equal(0, factories);
        Assert.Equal(2, observed);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("policy")]
    [InlineData("observe")]
    public async Task CallerCancellationPolicyRevocationAndObservationFailureDrainBeforeReturning(string cause)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim(false);
        var stopped = Signal();
        var observing = Signal();
        var failObservation = Signal();
        var errors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("injected observation failure");
        bool allowed = true;
        Thread? worker = null;
        CancellationToken ownedToken = default;
        var session = new CeilingLoadSession(() => Volatile.Read(ref allowed), (token, _) =>
        {
            ownedToken = token;
            return worker = IdleWorker(token, release, stopped, errors);
        });
        Task<int> pending = Task.Run(() => session.ObserveAsync(CeilingLoad.Integer, 1,
            async (token, check) =>
            {
                observing.TrySetResult(true);
                await failObservation.Task.WaitAsync(Budget);
                if (cause == "observe") throw failure;
                check();
                return 7;
            }, cancellation.Token));
        try
        {
            await observing.Task.WaitAsync(Budget);
            if (cause == "caller")
            {
                cancellation.Cancel();
                // Linked cancellation reaches workers even while the observer is still blocked.
                await stopped.Task.WaitAsync(Budget);
            }
            if (cause == "policy") Volatile.Write(ref allowed, false);
            failObservation.TrySetResult(true);
            await stopped.Task.WaitAsync(Budget);
            Assert.False(pending.IsCompleted);
            Assert.True(ownedToken.WaitHandle.WaitOne(0));
            release.Set();
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Budget));
            if (cause == "observe") Assert.Same(failure, actual);
            else Assert.IsAssignableFrom<OperationCanceledException>(actual);
            Assert.False(worker!.IsAlive);
            Assert.Throws<ObjectDisposedException>(() => { _ = ownedToken.WaitHandle; });
            Assert.Empty(errors);
        }
        finally
        {
            failObservation.TrySetResult(true);
            release.Set();
            try { await pending; } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyAndLateWorkerFaultsRemainVisibleWithoutExecutingArithmeticSlice(bool late)
    {
        using var release = new ManualResetEventSlim(false);
        var entered = Signal();
        var stopped = Signal();
        var errors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("injected worker policy fault before slice");
        int workerId = 0;
        Thread? worker = null;
        var session = new CeilingLoadSession(
            () => Environment.CurrentManagedThreadId == Volatile.Read(ref workerId) ? throw failure : true,
            (token, body) => worker = SafeWorker(() =>
            {
                Volatile.Write(ref workerId, Environment.CurrentManagedThreadId);
                entered.TrySetResult(true);
                if (late)
                {
                    Assert.True(token.WaitHandle.WaitOne(Budget));
                    stopped.TrySetResult(true);
                    Assert.True(release.Wait(Budget));
                }
                body();
                // The production body owns exception transport, so it must not escape this thread.
            }, errors));
        Task<int> pending = Task.Run(() => session.ObserveAsync(CeilingLoad.Integer, 1,
            async (_, check) =>
            {
                await entered.Task.WaitAsync(Budget);
                return 42;
            }, CancellationToken.None));
        try
        {
            if (late)
            {
                await stopped.Task.WaitAsync(Budget);
                Assert.False(pending.IsCompleted);
                release.Set();
            }
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Budget));
            IEnumerable<Exception> actualErrors = actual is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions : new[] { actual };
            Assert.Contains(failure, actualErrors);
            Assert.False(worker!.IsAlive);
            Assert.Empty(errors);
        }
        finally
        {
            release.Set();
            try { await pending; } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerFirstAndControllerFirstPolicyRevocationAreExpectedCancellationAfterDrain(bool workerFirst)
    {
        using var beginBody = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var observing = Signal();
        var inspectPolicy = Signal();
        var bodyReturned = Signal();
        var errors = new ConcurrentQueue<Exception>();
        bool allowed = true;
        Thread? worker = null;
        CancellationToken ownedToken = default;
        var session = new CeilingLoadSession(() => Volatile.Read(ref allowed), (token, body) =>
        {
            ownedToken = token;
            return worker = SafeWorker(() =>
            {
                if (workerFirst) Assert.True(beginBody.Wait(Budget));
                else Assert.True(token.WaitHandle.WaitOne(Budget));
                // Policy is already false. The body's first Check cannot reach an arithmetic slice.
                body();
                bodyReturned.TrySetResult(true);
                Assert.True(token.WaitHandle.WaitOne(0));
                Assert.True(release.Wait(Budget));
                Assert.True(token.WaitHandle.WaitOne(0));
            }, errors);
        });
        Task<int> pending = Task.Run(() => session.ObserveAsync(CeilingLoad.Integer, 1,
            async (_, check) =>
            {
                observing.TrySetResult(true);
                await inspectPolicy.Task.WaitAsync(Budget);
                check();
                return 42;
            }, CancellationToken.None));
        try
        {
            await observing.Task.WaitAsync(Budget);
            Volatile.Write(ref allowed, false);
            if (workerFirst)
            {
                beginBody.Set();
                await bodyReturned.Task.WaitAsync(Budget);
                // A worker detecting policy=false must signal its peers even with observer blocked.
                Assert.True(ownedToken.WaitHandle.WaitOne(0));
                inspectPolicy.TrySetResult(true);
            }
            else
            {
                inspectPolicy.TrySetResult(true);
                await bodyReturned.Task.WaitAsync(Budget);
            }
            Assert.False(pending.IsCompleted);
            Assert.True(worker!.IsAlive);
            Assert.True(ownedToken.WaitHandle.WaitOne(0));
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Budget));
            Assert.False(worker.IsAlive);
            Assert.Empty(errors);
        }
        finally
        {
            Volatile.Write(ref allowed, false);
            beginBody.Set();
            inspectPolicy.TrySetResult(true);
            release.Set();
            try { await pending; } catch { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyCallbackThrownCancellationExceptionIsAVisibleFaultEvenAfterOwnerStop(bool late)
    {
        using var beginBody = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var observing = Signal();
        var finishObservation = Signal();
        var bodyReturned = Signal();
        var errors = new ConcurrentQueue<Exception>();
        var callbackFault = new OperationCanceledException("injected policy provider OCE");
        int workerId = 0;
        Thread? worker = null;
        var session = new CeilingLoadSession(
            () => Environment.CurrentManagedThreadId == Volatile.Read(ref workerId) ? throw callbackFault : true,
            (token, body) => worker = SafeWorker(() =>
            {
                Volatile.Write(ref workerId, Environment.CurrentManagedThreadId);
                if (late) Assert.True(token.WaitHandle.WaitOne(Budget));
                else Assert.True(beginBody.Wait(Budget));
                // This throws in the policy callback at the first Check, before any arithmetic.
                body();
                bodyReturned.TrySetResult(true);
                Assert.True(release.Wait(Budget));
            }, errors));
        Task<int> pending = Task.Run(() => session.ObserveAsync(CeilingLoad.Integer, 1,
            async (_, _) =>
            {
                observing.TrySetResult(true);
                await finishObservation.Task.WaitAsync(Budget);
                return 42;
            }, CancellationToken.None));
        try
        {
            await observing.Task.WaitAsync(Budget);
            if (late) finishObservation.TrySetResult(true);
            else beginBody.Set();
            await bodyReturned.Task.WaitAsync(Budget);
            Assert.False(pending.IsCompleted);
            finishObservation.TrySetResult(true);
            release.Set();
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Budget));
            Assert.False(actual is OperationCanceledException);
            Assert.Contains(callbackFault, WalkErrors(actual));
            Assert.False(worker!.IsAlive);
            Assert.Empty(errors);
        }
        finally
        {
            beginBody.Set();
            finishObservation.TrySetResult(true);
            release.Set();
            try { await pending; } catch { }
        }
    }

    [Fact]
    public async Task ControllerPolicyCallbackThrownCancellationExceptionIsNotAnExpectedStop()
    {
        var callbackFault = new OperationCanceledException("injected admission provider OCE");
        var session = new CeilingLoadSession(() => throw callbackFault,
            (_, _) => throw new Exception("worker forbidden"));
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ObserveAsync(CeilingLoad.Baseline, 0, (_, _) => Task.FromResult(42), CancellationToken.None));
        Assert.Same(callbackFault, actual.InnerException);
    }

    [Fact]
    public async Task ProductionBodyEnteredOnlyAfterStopTreatsCancellationAsCleanDrainWithoutSlice()
    {
        var errors = new ConcurrentQueue<Exception>();
        Thread? worker = null;
        var session = new CeilingLoadSession(() => true, (token, body) =>
            worker = SafeWorker(() =>
            {
                Assert.True(token.WaitHandle.WaitOne(Budget));
                // Check sees the already-stopped token before RunWorkload can execute.
                body();
            }, errors));
        int result = await session.ObserveAsync(CeilingLoad.Integer, 1,
            (_, _) => Task.FromResult(42), CancellationToken.None).WaitAsync(Budget);
        Assert.Equal(42, result);
        Assert.False(worker!.IsAlive);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task PublicStopUsesLinkedWorkerTokenAndReportsCompletionOnlyAfterDrain()
    {
        using var release = new ManualResetEventSlim(false);
        var stopped = Signal();
        var observing = Signal();
        var errors = new ConcurrentQueue<Exception>();
        Thread? worker = null;
        var session = new CeilingLoadSession(() => true, (token, _) =>
            worker = IdleWorker(token, release, stopped, errors));
        var service = new CeilingService(session, (_, _, _, callerToken) =>
        {
            session.ObserveAsync(CeilingLoad.Integer, 1, async (token, check) =>
            {
                observing.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }, callerToken).GetAwaiter().GetResult();
            return new CeilingService.Result { StatusText = "unexpected success" };
        });
        service.Start();
        Task pending = service.CurrentRun;
        try
        {
            await observing.Task.WaitAsync(Budget);
            service.Stop();
            await stopped.Task.WaitAsync(Budget);
            Assert.False(pending.IsCompleted);
            Assert.True(service.IsRunning);
            release.Set();
            await pending.WaitAsync(Budget);
            Assert.False(worker!.IsAlive);
            Assert.False(service.IsRunning);
            Assert.Contains("已停止", service.Status);
            Assert.Empty(errors);
        }
        finally
        {
            service.Stop();
            release.Set();
            try { await pending; } catch { }
        }
    }

    [Fact]
    public async Task SessionCompletionAndConcurrentReuseWaitForWorkerDrain()
    {
        using var release = new ManualResetEventSlim(false);
        var stopped = Signal();
        var errors = new ConcurrentQueue<Exception>();
        int factories = 0;
        Thread? worker = null;
        var session = new CeilingLoadSession(() => true, (token, _) =>
        {
            Interlocked.Increment(ref factories);
            return worker = IdleWorker(token, release, stopped, errors);
        });
        Task<int> first = Task.Run(() => session.ObserveAsync(CeilingLoad.Integer, 1,
            (_, _) => Task.FromResult(42), CancellationToken.None));
        Task<int>? second = null;
        try
        {
            await stopped.Task.WaitAsync(Budget);
            second = session.ObserveAsync(CeilingLoad.Baseline, 0,
                (_, _) => Task.FromResult(7), CancellationToken.None);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref factories));
            release.Set();
            Assert.Equal(42, await first.WaitAsync(Budget));
            Assert.Equal(7, await second.WaitAsync(Budget));
            Assert.False(worker!.IsAlive);
            Assert.Empty(errors);
        }
        finally
        {
            release.Set();
            try { await first; } catch { }
            if (second is not null) { try { await second; } catch { } }
        }
    }

    [Fact]
    public void ActualWindowWiresSessionAndOldConstructorOwnerIsRemoved()
    {
        string root = RepoRoot();
        string service = File.ReadAllText(Path.Combine(root, "Services", "CeilingService.cs"));
        string session = File.ReadAllText(Path.Combine(root, "Services", "CeilingLoadSession.cs"));
        Assert.Contains("_loadSession.ObserveAsync(kind, c.LoadThreads,", service);
        Assert.Contains("ObserveWindow(c, label, kind, report, progFrom, progTo, token, check)", service);
        Assert.Contains("DeepBenchWorkerLifetime.RunAsync<LoadGate, TResult>", session);
        Assert.DoesNotContain("LoadGenerator", service);
        Assert.DoesNotContain("Join(3000)", service);
        Assert.Contains("Motion.Suspend()", service);
    }

    private static IEnumerable<Exception> WalkErrors(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        {
            foreach (Exception child in aggregate.InnerExceptions)
                foreach (Exception item in WalkErrors(child)) yield return item;
        }
        else if (error.InnerException is Exception child)
        {
            foreach (Exception item in WalkErrors(child)) yield return item;
        }
    }

    private static Thread IdleWorker(CancellationToken token, ManualResetEventSlim release,
        TaskCompletionSource<bool> stopped, ConcurrentQueue<Exception> errors) => SafeWorker(() =>
    {
        Assert.True(token.WaitHandle.WaitOne(Budget));
        stopped.TrySetResult(true);
        Assert.True(release.Wait(Budget));
        // The source must remain undisposed until this real worker exits.
        Assert.True(token.WaitHandle.WaitOne(0));
    }, errors);

    private static Thread SafeWorker(Action body, ConcurrentQueue<Exception> errors) =>
        new(() => { try { body(); } catch (Exception error) { errors.Enqueue(error); } }) { IsBackground = true };

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XinSpect.csproj")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
