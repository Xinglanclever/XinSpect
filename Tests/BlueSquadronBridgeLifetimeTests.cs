using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>Injected only: no bootstrap extraction, subprocess, WMI, native API or driver.</summary>
public sealed class BlueSquadronBridgeLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Wait(Task task) => task.WaitAsync(Bound);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrDisposeDuringStartRetainsOwnerAndRejectsLateSuccess(bool dispose)
    {
        var fake = new FakeClient(blockStart: true);
        var delay = new BlockingDelay();
        var ui = new QueuedUi();
        var module = new BlueSquadronModule(() => fake, ui.Enqueue, delay.Run);
        try
        {
            module.BridgeEnabled = true;
            Task worker = module.BridgeCompletion;
            await Wait(fake.StartEntered.Task);
            if (dispose) module.Dispose(); else module.BridgeEnabled = false;
            Assert.False(module.BridgeAvailable);
            Assert.Null(module.Engine);
            Assert.Equal(0, fake.DisposeCount); // Start still owns the client.
            Assert.False(worker.IsCompleted);
            fake.StartRelease.TrySetResult(true);
            await Wait(worker);
            ui.Drain();
            Assert.Equal(1, fake.DisposeCount);
            Assert.Empty(fake.Commands);
            Assert.Equal(0, delay.Count);
            Assert.False(module.BridgeAvailable);
            Assert.False(module.BridgeEnabled);
            Assert.Equal("Bridge 已停止", module.BridgeStatus);
            if (dispose)
            {
                module.BridgeEnabled = true;
                Assert.False(module.BridgeEnabled);
                Assert.Equal(1, fake.StartCount);
            }
        }
        finally { fake.StartRelease.TrySetResult(true); module.Dispose(); }
    }

    [Fact]
    public async Task StopDuringFactoryDisposesCreatedClientWithoutStartingIt()
    {
        var entered = Signal();
        var release = Signal();
        var fake = new FakeClient();
        var module = new BlueSquadronModule(() =>
        {
            entered.TrySetResult(true);
            release.Task.WaitAsync(Bound).GetAwaiter().GetResult();
            return fake;
        }, a => a(), null);
        try
        {
            module.BridgeEnabled = true;
            Task worker = module.BridgeCompletion;
            await Wait(entered.Task);
            module.StopBridge();
            release.TrySetResult(true);
            await Wait(worker);
            Assert.Equal(0, fake.StartCount);
            Assert.Equal(1, fake.DisposeCount);
            Assert.Null(module.Engine);
        }
        finally { release.TrySetResult(true); module.Dispose(); }
    }

    [Fact]
    public async Task StopDuringHandshakeRejectsStatusCapsPollAndLateUiPublication()
    {
        var fake = new FakeClient(blockInit: true);
        var delay = new BlockingDelay();
        var ui = new QueuedUi();
        var module = new BlueSquadronModule(() => fake, ui.Enqueue, delay.Run);
        try
        {
            module.BridgeEnabled = true;
            Task worker = module.BridgeCompletion;
            await Wait(fake.InitEntered.Task);
            module.BridgeEnabled = false;
            Assert.Equal(0, fake.DisposeCount);
            fake.InitRelease.TrySetResult(true);
            await Wait(worker);
            ui.Drain();
            Assert.Equal(new[] { "init" }, fake.Commands.ToArray());
            Assert.Equal(0, delay.Count);
            Assert.False(module.BridgeAvailable);
            Assert.Null(module.Engine);
            Assert.Equal(1, fake.DisposeCount);
        }
        finally { fake.InitRelease.TrySetResult(true); module.Dispose(); }
    }

    [Fact]
    public async Task QueuedSuccessAndCapturedAlertCannotResurrectStoppedSession()
    {
        var fake = new FakeClient();
        var delay = new BlockingDelay();
        var ui = new QueuedUi();
        var module = new BlueSquadronModule(() => fake, ui.Enqueue, delay.Run);
        try
        {
            module.BridgeEnabled = true;
            Task worker = module.BridgeCompletion;
            await Wait(delay.Entered.Task);
            Action<JsonElement>? capturedAlert = fake.CaptureAlert();
            Assert.NotNull(capturedAlert);
            module.BridgeEnabled = false;
            capturedAlert!(Json("{\"title\":\"stale alert\",\"detail\":\"old generation\"}"));
            await Wait(worker); // Exercises cancellation while the delay still owns its token.
            ui.Drain();
            Assert.False(module.BridgeAvailable);
            Assert.Equal("Bridge 已停止", module.BridgeStatus);
            Assert.Empty(module.ThreatTimeline);
            Assert.DoesNotContain("threats", fake.Commands);
            Assert.Equal(1, fake.DisposeCount);
        }
        finally { module.Dispose(); }
    }

    [Fact]
    public async Task RapidOffOnWaitsForRetiredOwnerBeforeCreatingFreshEngine()
    {
        var first = new FakeClient(blockStart: true);
        var second = new FakeClient();
        var delay = new BlockingDelay();
        int created = 0;
        var module = new BlueSquadronModule(() =>
            Interlocked.Increment(ref created) == 1 ? first : second, a => a(), delay.Run);
        try
        {
            module.BridgeEnabled = true;
            Task oldWorker = module.BridgeCompletion;
            await Wait(first.StartEntered.Task);
            module.BridgeEnabled = false;
            module.BridgeEnabled = true;
            Assert.Equal(1, Volatile.Read(ref created));
            first.StartRelease.TrySetResult(true);
            await Wait(oldWorker);
            await Wait(second.StartEntered.Task);
            await Wait(delay.Entered.Task);
            Assert.Equal(2, Volatile.Read(ref created));
            Assert.Equal(1, first.DisposeCount);
            Assert.Empty(first.Commands);
            Assert.Equal(0, second.DisposeCount);
            Assert.True(module.BridgeAvailable);
            Task freshWorker = module.BridgeCompletion;
            module.StopBridge();
            await Wait(freshWorker);
            Assert.Equal(1, second.DisposeCount);
        }
        finally { first.StartRelease.TrySetResult(true); module.Dispose(); }
    }

    [Fact]
    public async Task InFlightPollRetainsClientUntilActualCompletionAndDropsItsLateEvent()
    {
        var fake = new FakeClient(blockThreats: true);
        int delays = 0;
        var module = new BlueSquadronModule(() => fake, a => a(), ct =>
            Interlocked.Increment(ref delays) == 1 ? Task.CompletedTask : Task.Delay(Timeout.Infinite, ct));
        try
        {
            module.BridgeEnabled = true;
            Task worker = module.BridgeCompletion;
            await Wait(fake.ThreatsEntered.Task);
            module.StopBridge();
            Assert.Equal(0, fake.DisposeCount);
            Assert.False(worker.IsCompleted);
            fake.ThreatsRelease.TrySetResult(true);
            await Wait(worker);
            Assert.Equal(1, fake.DisposeCount);
            Assert.Single(fake.Commands.Where(c => c == "threats"));
            Assert.Empty(module.ThreatTimeline);
            Assert.False(module.BridgeAvailable);
        }
        finally { fake.ThreatsRelease.TrySetResult(true); module.Dispose(); }
    }

    [Fact]
    public async Task ThrowingStartDisposesExactlyOnceAndRetainsCurrentFailureDiagnostic()
    {
        var fake = new FakeClient { ThrowOnStart = true };
        var ui = new QueuedUi();
        var module = new BlueSquadronModule(() => fake, ui.Enqueue, null);
        try
        {
            module.BridgeEnabled = true;
            await Wait(module.BridgeCompletion);
            ui.Drain();
            Assert.Equal(1, fake.DisposeCount);
            Assert.Contains("Bridge 啟動例外", module.BridgeStatus);
            Assert.False(module.BridgeAvailable);
            Assert.Empty(fake.Commands);
        }
        finally { module.Dispose(); }
        Assert.Equal(1, fake.DisposeCount);
    }

    [Fact]
    public async Task DisposeReenteredFromAvailabilityNotificationCannotRestoreConnectedStatus()
    {
        var fake = new FakeClient();
        var delay = new BlockingDelay();
        var disposedFromNotification = Signal();
        var module = new BlueSquadronModule(() => fake, action => action(), delay.Run);
        module.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(BlueSquadronModule.BridgeAvailable)
                || !module.BridgeAvailable) return;
            Assert.Contains("Bridge 已連線", module.BridgeStatus);
            module.Dispose();
            disposedFromNotification.TrySetResult(true);
        };
        try
        {
            module.BridgeEnabled = true;
            Task worker = module.BridgeCompletion;
            await Wait(disposedFromNotification.Task);
            await Wait(worker);
            Assert.False(module.BridgeEnabled);
            Assert.False(module.BridgeAvailable);
            Assert.Equal("Bridge 已停止", module.BridgeStatus);
            Assert.Null(module.Engine);
            Assert.Equal(new[] { "init", "status" }, fake.Commands.ToArray());
            Assert.Equal(0, delay.Count);
            Assert.Equal(1, fake.DisposeCount);
            module.BridgeEnabled = true;
            Assert.False(module.BridgeEnabled);
        }
        finally { module.Dispose(); }
    }

    [Fact]
    public void DisposedRealEngineRejectsStartBeforeAnyBootstrapOrProcessWork()
    {
        // Dispose on an unstarted engine has no native/process resource to touch.
        var engine = new BlueSquadronEngine();
        engine.Dispose();
        Assert.False(engine.Start());
        Assert.False(engine.Connected);
        Assert.Contains("已處置", engine.LastError);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class BlockingDelay
    {
        public TaskCompletionSource<bool> Entered { get; } = Signal();
        public int Count;
        public Task Run(CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            Entered.TrySetResult(true);
            return Task.Delay(Timeout.Infinite, ct);
        }
    }

    private sealed class QueuedUi
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public void Enqueue(Action action) => _queue.Enqueue(action);
        public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
    }

    private sealed class FakeClient : IBlueSquadronBridgeClient
    {
        public FakeClient(bool blockStart = false, bool blockInit = false, bool blockThreats = false)
        {
            if (!blockStart) StartRelease.TrySetResult(true);
            if (!blockInit) InitRelease.TrySetResult(true);
            if (!blockThreats) ThreatsRelease.TrySetResult(true);
        }
        public BlueSquadronEngine? NativeEngine => null;
        public bool Connected { get; private set; }
        public string LastError => "fixture";
        public bool ThrowOnStart { get; init; }
        public int StartCount;
        public int DisposeCount;
        public ConcurrentQueue<string> Commands { get; } = new();
        public TaskCompletionSource<bool> StartEntered { get; } = Signal();
        public TaskCompletionSource<bool> StartRelease { get; } = Signal();
        public TaskCompletionSource<bool> InitEntered { get; } = Signal();
        public TaskCompletionSource<bool> InitRelease { get; } = Signal();
        public TaskCompletionSource<bool> ThreatsEntered { get; } = Signal();
        public TaskCompletionSource<bool> ThreatsRelease { get; } = Signal();
        public event Action<JsonElement>? AlertReceived;
        public Action<JsonElement>? CaptureAlert() => AlertReceived;
        public bool Start()
        {
            Interlocked.Increment(ref StartCount);
            StartEntered.TrySetResult(true);
            StartRelease.Task.WaitAsync(Bound).GetAwaiter().GetResult();
            if (ThrowOnStart) throw new InvalidOperationException("injected start fault");
            Connected = true;
            return true;
        }
        public JsonElement? SendCmd(string cmd, Dictionary<string, object> extra, int timeoutMs)
            => SendCmd(cmd, timeoutMs);
        public JsonElement? SendCmd(string cmd, int timeoutMs)
        {
            Commands.Enqueue(cmd);
            if (cmd == "init")
            {
                InitEntered.TrySetResult(true);
                InitRelease.Task.WaitAsync(Bound).GetAwaiter().GetResult();
                return Json("{}");
            }
            if (cmd == "threats")
            {
                ThreatsEntered.TrySetResult(true);
                ThreatsRelease.Task.WaitAsync(Bound).GetAwaiter().GetResult();
                return Json("{\"threats\":[{\"ts\":\"2026-11-17T00:00:00Z\",\"title\":\"late poll\"}]}");
            }
            return null;
        }
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Connected = false;
        }
    }
}
