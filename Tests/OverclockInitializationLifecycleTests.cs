using Xunit;
using System.Collections.Concurrent;

namespace XinSpect.Tests;

// All initialization/VRM completion and engine methods are injected; no SDK, bus, or user files.
[Collection(CrashFolderCollection.Name)]
public sealed class OverclockInitializationLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializationOwnedCleanupAwaitsWorkerWithoutBlockingItsDispatcher(bool lateResult)
    {
        using var context = new PumpingContext();
        var cleanupEngine = new LifecycleEngine { BlockDispose = true };
        var replacement = new LifecycleEngine();
        var result = new TaskCompletionSource<IOcEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = lateResult ? new OverclockService(new OcSettings())
            : new OverclockService(new OcSettings(), cleanupEngine);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializing = context.Run(async () =>
        {
            var operation = service.InitializeAsync(() => { started.SetResult(true); return result.Task; }, NoVrm, false);
            await operation;
        });
        try
        {
            await started.Task.WaitAsync(Budget);
            // Disposal itself is initiated without this fixture dispatcher; only the initializer
            // owns its captured context, so unrelated fire-and-forget teardown cannot outlive it.
            if (lateResult) await Task.Run(service.Dispose).WaitAsync(Budget);
            result.SetResult(lateResult ? cleanupEngine : replacement);
            await cleanupEngine.DisposeEntered.Task.WaitAsync(Budget);
            // This runs on the same pumping context as InitializeAsync. A synchronous Dispose
            // on that context would prevent the ping until after ReleaseDispose was set.
            bool ping = false;
            await context.Run(() => { ping = true; return Task.CompletedTask; }).WaitAsync(Budget);
            Assert.True(ping);
            Assert.False(initializing.IsCompleted);
            Assert.False(cleanupEngine.Disposed.Task.IsCompleted);
            Assert.NotEqual(context.ThreadId, cleanupEngine.DisposalThreadId);
            Assert.False(service.CanApply);
            cleanupEngine.ReleaseDispose.Set();
            await initializing.WaitAsync(Budget);
            await cleanupEngine.Disposed.Task.WaitAsync(Budget);
        }
        finally
        {
            cleanupEngine.ReleaseDispose.Set();
            service.Dispose();
            await initializing.WaitAsync(Budget);
            if (!lateResult) await replacement.Disposed.Task.WaitAsync(Budget);
        }
        Assert.Equal(1, cleanupEngine.DisposeCount);
    }

    [Fact]
    public async Task DisposeDuringPriorCleanupPreventsStaleCategorizationAndBootDispatch()
    {
        var originalKnob = new OcKnob(1u, "Performance Core Ratio", "Processor", "",
            OcKnobKind.CoreRatio, 1, 100, 40, 40, 40, true, false, false, true);
        var replacementKnob = new OcKnob(2u, "Performance Core Ratio", "Processor", "",
            OcKnobKind.CoreRatio, 1, 100, 50, 50, 50, true, false, false, true);
        var prior = new LifecycleEngine { BlockDispose = true, Knobs = [originalKnob] };
        var replacement = new LifecycleEngine { Knobs = [replacementKnob] };
        var service = new OverclockService(new OcSettings(), prior);
        Assert.Same(originalKnob, service.CoreRatioKnob);
        int collectionChanges = 0, vrmCalls = 0;
        service.CoreKnobs.CollectionChanged += (_, _) => Interlocked.Increment(ref collectionChanges);
        var initializing = service.InitializeAsync(() => Task.FromResult<IOcEngine>(replacement),
            () => { vrmCalls++; return NoVrm(); }, false);
        try
        {
            await prior.DisposeEntered.Task.WaitAsync(Budget);
            service.Dispose();
            Assert.False(initializing.IsCompleted);
            Assert.False(replacement.Disposed.Task.IsCompleted);
            prior.ReleaseDispose.Set();
            await initializing.WaitAsync(Budget);
            // Teardown queues behind the retained slot: its completion also proves slot release.
            await replacement.Disposed.Task.WaitAsync(Budget);
            Assert.Equal(0, collectionChanges);
            Assert.Same(originalKnob, service.CoreRatioKnob);
            Assert.Equal(0, replacement.BootCalls);
            Assert.Equal(0, vrmCalls);
            Assert.Equal(1, prior.DisposeCount);
            Assert.Equal(1, replacement.DisposeCount);
            Assert.False(service.Initializing);
        }
        finally
        {
            prior.ReleaseDispose.Set();
            service.Dispose();
            await initializing.WaitAsync(Budget);
            await replacement.Disposed.Task.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task LateEngineResultAfterDisposeIsDisposedWithoutPublishingOrBootWrite()
    {
        var result = new TaskCompletionSource<IOcEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new LifecycleEngine();
        var service = new OverclockService(new OcSettings());
        int vrmCalls = 0;
        var initializing = service.InitializeAsync(() => result.Task,
            () => { vrmCalls++; return Task.FromResult<VrmControllerService?>(null); }, false);
        Assert.True(service.Initializing);
        Assert.False(service.CanApply);
        service.Dispose();
        Assert.False(initializing.IsCompleted);
        Assert.Equal(0, engine.DisposeCount);
        result.SetResult(engine);
        await initializing.WaitAsync(Budget);
        await engine.Disposed.Task.WaitAsync(Budget);
        Assert.Equal(1, engine.DisposeCount);
        Assert.Equal(0, engine.BootCalls);
        Assert.Equal(0, vrmCalls);
        Assert.False(service.EngineReady);
        Assert.False(service.Initializing);
        Assert.False(service.CanApply);
    }

    [Fact]
    public async Task DisposeDuringDispatchedBootRestoreWaitsForItsActualCompletion()
    {
        var engine = new LifecycleEngine { BlockBoot = true };
        var service = new OverclockService(new OcSettings());
        int vrmCalls = 0;
        var initializing = service.InitializeAsync(() => Task.FromResult<IOcEngine>(engine),
            () => { vrmCalls++; return Task.FromResult<VrmControllerService?>(null); }, false);
        try
        {
            await engine.BootEntered.Task.WaitAsync(Budget);
            service.Dispose();
            Assert.False(engine.Disposed.Task.IsCompleted);
            Assert.False(initializing.IsCompleted);
            engine.ReleaseBoot.Set();
            await initializing.WaitAsync(Budget);
            await engine.Disposed.Task.WaitAsync(Budget);
            Assert.Equal(1, engine.BootCalls);
            Assert.Equal(1, engine.DisposeCount);
            Assert.False(engine.DisposedWhileBootActive);
            Assert.Equal(0, vrmCalls);
            Assert.False(service.Initializing);
        }
        finally { engine.ReleaseBoot.Set(); service.Dispose(); }
    }

    [Fact]
    public async Task DisposeDuringVrmFactoryWaitsForFactoryAndDisposesAdoptedEngine()
    {
        var engine = new LifecycleEngine();
        var vrmEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vrmResult = new TaskCompletionSource<VrmControllerService?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new OverclockService(new OcSettings());
        var initializing = service.InitializeAsync(() => Task.FromResult<IOcEngine>(engine),
            () => { vrmEntered.SetResult(true); return vrmResult.Task; }, false);
        await vrmEntered.Task.WaitAsync(Budget);
        service.Dispose();
        Assert.False(engine.Disposed.Task.IsCompleted);
        Assert.False(initializing.IsCompleted);
        vrmResult.SetResult(null);
        await initializing.WaitAsync(Budget);
        await engine.Disposed.Task.WaitAsync(Budget);
        Assert.Equal(1, engine.DisposeCount);
        Assert.False(service.Initializing);
        Assert.False(service.CanApply);
    }

    [Fact]
    public async Task ConcurrentAndPostDisposeInitializationCannotDispatchAnotherFactory()
    {
        var engine = new LifecycleEngine();
        var result = new TaskCompletionSource<IOcEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new OverclockService(new OcSettings());
        int calls = 0;
        Task<IOcEngine> Factory() { calls++; return result.Task; }
        var first = service.InitializeAsync(Factory, NoVrm, false);
        await service.InitializeAsync(Factory, NoVrm, false).WaitAsync(Budget);
        Assert.Equal(1, calls);
        service.Dispose();
        await service.InitializeAsync(Factory, NoVrm, false).WaitAsync(Budget);
        Assert.Equal(1, calls);
        result.SetResult(engine);
        await first.WaitAsync(Budget);
        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public async Task FailedInitializationReleasesAdmissionForARealRetry()
    {
        var service = new OverclockService(new OcSettings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InitializeAsync(
            () => Task.FromException<IOcEngine>(new InvalidOperationException("injected factory failure")), NoVrm, false));
        Assert.False(service.Initializing);
        Assert.True(service.CanApply);
        var engine = new LifecycleEngine();
        await service.InitializeAsync(() => Task.FromResult<IOcEngine>(engine), NoVrm, false).WaitAsync(Budget);
        Assert.True(service.EngineReady);
        Assert.Equal(1, engine.BootCalls);
        service.Dispose();
        await engine.Disposed.Task.WaitAsync(Budget);
        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public async Task BootPreferenceFailureIsDiagnosedAndInitializationContinues()
    {
        bool savedSink = Diag.FileSinkEnabled;
        Diag.FileSinkEnabled = false;
        var engine = new LifecycleEngine { ThrowBoot = true };
        var service = new OverclockService(new OcSettings());
        int vrmCalls = 0;
        try
        {
            await service.InitializeAsync(() => Task.FromResult<IOcEngine>(engine),
                () => { vrmCalls++; return NoVrm(); }, false).WaitAsync(Budget);
            Assert.Equal(1, engine.BootCalls);
            Assert.Equal(1, vrmCalls);
            Assert.True(service.EngineReady);
            Assert.False(service.Initializing);
            Assert.True(service.CanApply);
            Assert.Equal("此平台不支援。", service.SpeedOptimizerStatusText);
            Assert.Contains(Diag.Entries, entry => entry.Site == "Overclock boot restore preference"
                && entry.Message == "injected optional boot preference failure"
                && entry.Consequence.Contains("without claiming it was applied"));
            Assert.Equal(0, engine.DisposeCount);
        }
        finally
        {
            try
            {
                service.Dispose();
                await engine.Disposed.Task.WaitAsync(Budget);
            }
            finally { Diag.FileSinkEnabled = savedSink; }
        }
        Assert.Equal(1, engine.DisposeCount);
    }

    private static Task<VrmControllerService?> NoVrm() => Task.FromResult<VrmControllerService?>(null);

    [Fact]
    public async Task RecoveryRevokesAnEngineResultThatHasNotBeenAdopted()
    {
        var service = new OverclockService(new OcSettings());
        var engine = new LifecycleEngine();
        var result = new TaskCompletionSource<IOcEngine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializing = service.InitializeAsync(() => result.Task, NoVrm, false);
        var recovering = service.RollbackToStableAsync("initialization fixture revocation");
        Assert.False(recovering.IsCompleted);
        result.SetResult(engine);
        await initializing.WaitAsync(Budget);
        await recovering.WaitAsync(Budget);
        Assert.Equal(0, engine.BootCalls);
        Assert.Equal(1, engine.DisposeCount);
        Assert.False(service.EngineReady);
        Assert.False(service.Initializing);
        service.Dispose();
    }

    private sealed class LifecycleEngine : IOcEngine
    {
        private int _disposeCount, _bootCalls, _bootActive;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public int BootCalls => Volatile.Read(ref _bootCalls);
        public bool BlockBoot { get; init; }
        public bool ThrowBoot { get; init; }
        public bool BlockDispose { get; init; }
        public int DisposalThreadId { get; private set; }
        public bool DisposedWhileBootActive { get; private set; }
        public TaskCompletionSource<bool> BootEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseBoot { get; } = new(false);
        public ManualResetEventSlim ReleaseDispose { get; } = new(false);
        public TaskCompletionSource<bool> DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "injected lifecycle engine";
        public OcEngineStatus Status => OcEngineStatus.Ready;
        public string StatusMessage => "fixture";
        public bool CoreTunable => false;
        public bool BclkTunable => false;
        public bool MemoryTunable => false;
        public bool CacheTunable => false;
        public bool SpeedOptimizerSupported => false;
        public int ProcessorFamily => 0;
        public bool WatchdogPresent => false;
        public bool WatchdogRunning => false;
        public bool WatchdogFailed => false;
        public int SpeedOptimizerState => 0;
        public IReadOnlyList<OcKnob> Knobs { get; init; } = [];
        public bool Initialize() => throw new InvalidOperationException("Native initialization is forbidden in this fixture.");
        public void RefreshActives() { }
        public double? ReadCoreVoltage() => null;
        public double? ReadMonitor(params string[] names) => null;
        public OcApplyResult Apply(OcKnob knob, double value) => throw new InvalidOperationException("Unexpected mutation");
        public bool Discard() => throw new InvalidOperationException("Unexpected mutation");
        public OcApplyResult RestoreDefaults() => throw new InvalidOperationException("Unexpected mutation");
        public OcApplyResult SetSpeedOptimizer(bool on, bool extreme) => throw new InvalidOperationException("Unexpected mutation");
        public void SetBootRestore(bool on)
        {
            Interlocked.Increment(ref _bootCalls);
            Interlocked.Exchange(ref _bootActive, 1);
            try
            {
                BootEntered.TrySetResult(true);
                if (ThrowBoot) throw new InvalidOperationException("injected optional boot preference failure");
                if (BlockBoot && !ReleaseBoot.Wait(Budget)) throw new TimeoutException("Fixture boot operation not released.");
            }
            finally { Interlocked.Exchange(ref _bootActive, 0); }
        }
        public void Dispose()
        {
            DisposalThreadId = Environment.CurrentManagedThreadId;
            DisposeEntered.TrySetResult(true);
            if (BlockDispose && !ReleaseDispose.Wait(Budget)) throw new TimeoutException("Fixture disposal not released.");
            if (Volatile.Read(ref _bootActive) != 0) DisposedWhileBootActive = true;
            Interlocked.Increment(ref _disposeCount);
            Disposed.TrySetResult(true);
        }
    }

    private sealed class PumpingContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;
        public PumpingContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true, Name = "Overclock initialization test dispatcher" };
            _thread.Start();
        }
        public override void Post(SendOrPostCallback callback, object? state)
            => _queue.Add(() => callback(state));
        public Task Run(Func<Task> action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }, null);
            return completion.Task;
        }
        public void Dispose()
        {
            _queue.CompleteAdding();
            if (!_thread.Join(Budget)) throw new TimeoutException("Fixture dispatcher did not finish.");
            _queue.Dispose();
        }
    }
}
