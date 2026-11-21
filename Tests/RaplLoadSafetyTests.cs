using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using Xunit;

namespace XinSpect.Tests;

[Collection(HardwareOperationPolicyCollection.Name)]
public sealed class RaplLoadSafetyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EveryPublicExplicitEntryRefusesClosedFirstLevelBeforeReading(int entry)
    {
        HardwareOperationGate.Bind(new SettingsService(loadFromDisk: false));
        int reads = 0;
        try
        {
            Func<ulong?> energy = () => { reads++; throw new Exception("reader must not run"); };
            Func<double?> temp = () => { reads++; throw new Exception("reader must not run"); };
            if (entry == 0) await Assert.ThrowsAsync<InvalidOperationException>(() => RaplEnergyGate.EnsureStarted(energy, temp));
            else if (entry == 1) await Assert.ThrowsAsync<InvalidOperationException>(() => RaplEnergyGate.RunOnceAsync(energy, temp));
            else Assert.Throws<InvalidOperationException>(() => RaplEnergyGate.RunOnce(energy, temp));
            Assert.Equal(0, reads);
            Assert.False(RaplEnergyGate.IsProbing);
        }
        finally { HardwareOperationGate.Bind(null); }
    }

    [Fact]
    public async Task PublicPreCancellationWinsBeforePolicyOrReaders()
    {
        HardwareOperationGate.Bind(null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RaplEnergyGate.EnsureStarted(
            () => throw new Exception("unexpected energy"), () => throw new Exception("unexpected temp"), cancellation.Token));
    }

    [Fact]
    public async Task InternalProductionCoreUsesDefaultHardwareGateBeforeReaders()
    {
        HardwareOperationGate.Bind(new SettingsService(loadFromDisk: false));
        try
        {
            var probe = new RaplEnergyProbe(createWorker: (_, _) => throw new Exception("worker forbidden"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(
                () => throw new Exception("energy forbidden"), () => throw new Exception("temperature forbidden"), CancellationToken.None));
        }
        finally { HardwareOperationGate.Bind(null); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(85.0)]
    [InlineData(100.0)]
    public async Task MissingInvalidOrHotTemperaturePreventsWorkerCreation(double? temperature)
    {
        int workers = 0;
        var probe = FixtureProbe((_, _) => { workers++; throw new Exception("worker forbidden"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RunAsync(() => 1, () => temperature, CancellationToken.None));
        Assert.Equal(0, workers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PartialStartFailureStopsAndJoinsOnlyStartedWorkers(int failingIndex)
    {
        using var release = new ManualResetEventSlim(false);
        var stopped = NewSignal();
        var threads = new List<Thread>();
        var threadErrors = new ConcurrentQueue<Exception>();
        var failure = new InvalidOperationException("injected Thread.Start failure");
        var probe = FixtureProbe((token, _) =>
        {
            Thread worker = new(() =>
            {
                try
                {
                    token.WaitHandle.WaitOne();
                    stopped.TrySetResult(true);
                    if (!release.Wait(Budget)) return;
                    // Source is still owned until this real worker exits.
                    token.WaitHandle.WaitOne(0);
                }
                catch (Exception error) { threadErrors.Enqueue(error); stopped.TrySetResult(true); }
            }) { IsBackground = true };
            threads.Add(worker);
            return worker;
        }, beforeStart: index => { if (index == failingIndex) throw failure; }, workerCount: 2);
        Task<RaplProbeResult> pending = Task.Run(() => probe.RunAsync(Counter(), () => 50, CancellationToken.None));
        try
        {
            if (failingIndex == 1)
            {
                await stopped.Task.WaitAsync(Budget);
                Assert.False(pending.IsCompleted);
            }
            release.Set();
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Budget)));
            Assert.Equal(failingIndex + 1, threads.Count);
            Assert.All(threads, worker => Assert.False(worker.IsAlive));
            Assert.True((threads[failingIndex].ThreadState & System.Threading.ThreadState.Unstarted) != 0);
            Assert.Empty(threadErrors);
        }
        finally { release.Set(); try { await pending; } catch { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationAfterFactoryOrInsideLastTemperatureReadPreventsThreadStart(bool insideTemperatureRead)
    {
        bool allowed = true, factoryReturned = false;
        Thread? worker = null;
        var probe = new RaplEnergyProbe(() => allowed, () => 1, (_, _) => Task.CompletedTask,
            (_, _) =>
            {
                if (!insideTemperatureRead) allowed = false;
                factoryReturned = true;
                return worker = new Thread(() => throw new Exception("unstarted worker must never execute")) { IsBackground = true };
            }, timestamp: Tick());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(Counter(),
            () => { if (insideTemperatureRead && factoryReturned) allowed = false; return 50; }, CancellationToken.None));
        Assert.NotNull(worker);
        Assert.False(worker!.IsAlive);
        Assert.True((worker.ThreadState & System.Threading.ThreadState.Unstarted) != 0);
    }

    [Theory]
    [InlineData("energy")]
    [InlineData("temperature")]
    [InlineData("cancel")]
    [InlineData("policy")]
    [InlineData("hot")]
    public async Task AnyLoadWindowFaultDrainsWorkersBeforeCompletingAndPermitsReuse(string cause)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim(false);
        var stopped = NewSignal();
        bool allowed = true;
        int energyReads = 0, delays = 0;
        bool hot = false, failTemperature = false;
        var threadErrors = new ConcurrentQueue<Exception>();
        Thread? worker = null;
        var failure = new InvalidOperationException("injected source failure");
        var probe = new RaplEnergyProbe(() => allowed, () => 1,
            (_, _) =>
            {
                if (++delays == 31)
                {
                    if (cause == "cancel") cancellation.Cancel();
                    if (cause == "policy") allowed = false;
                    if (cause == "temperature") failTemperature = true;
                    if (cause == "hot") hot = true;
                }
                return Task.CompletedTask;
            },
            (token, _) =>
            {
                return worker = new Thread(() =>
                {
                    try
                    {
                        token.WaitHandle.WaitOne();
                        stopped.TrySetResult(true);
                        if (!release.Wait(Budget)) return;
                        token.WaitHandle.WaitOne(0);
                    }
                    catch (Exception error) { threadErrors.Enqueue(error); stopped.TrySetResult(true); }
                }) { IsBackground = true };
            }, timestamp: Tick());
        Task<RaplProbeResult> pending = Task.Run(() => probe.RunAsync(
            () => ++energyReads == 3 && cause == "energy" ? throw failure : (ulong?)(energyReads * 100),
            () => failTemperature ? throw failure : hot ? 90 : 50, cancellation.Token));
        try
        {
            await stopped.Task.WaitAsync(Budget);
            Assert.False(pending.IsCompleted);
            release.Set();
            Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => pending.WaitAsync(Budget));
            if (cause is "energy" or "temperature") Assert.Same(failure, actual);
            if (cause is "cancel" or "policy") Assert.IsAssignableFrom<OperationCanceledException>(actual);
            Assert.False(worker!.IsAlive);
            Assert.Empty(threadErrors);
            // Enter the same owner again after the first fault; no hidden production retry.
            allowed = true;
            hot = false;
            failTemperature = false;
            RaplProbeResult later = await probe.RunAsync(Counter(), () => 50, CancellationToken.None).WaitAsync(Budget);
            Assert.NotEqual(RaplGateVerdict.Unknown, later.Verdict);
            Assert.False(worker!.IsAlive);
            Assert.Empty(threadErrors);
        }
        finally { release.Set(); try { await pending; } catch { } }
    }

    [Fact]
    public async Task WorkerBodyFailureIsObservedAfterRealDrainWithoutRunningLoadSlice()
    {
        var entered = NewSignal();
        int workerId = 0;
        var failure = new InvalidOperationException("injected worker policy read fault");
        Thread? worker = null;
        var probe = new RaplEnergyProbe(
            () => Environment.CurrentManagedThreadId == Volatile.Read(ref workerId) ? throw failure : true,
            () => 1,
            async (_, _) => { if (worker is not null) await entered.Task.WaitAsync(Budget); },
            (_, body) => worker = new Thread(() =>
            {
                Volatile.Write(ref workerId, Environment.CurrentManagedThreadId);
                try { body(); }
                finally { entered.TrySetResult(true); }
            }) { IsBackground = true }, timestamp: Tick());
        Exception actual = await Assert.ThrowsAnyAsync<Exception>(() => probe.RunAsync(Counter(), () => 50, CancellationToken.None).WaitAsync(Budget));
        IEnumerable<Exception> errors = actual is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : new[] { actual };
        Assert.Contains(failure, errors);
        Assert.False(worker!.IsAlive);
    }

    [Fact]
    public async Task SuccessfulInjectedObservationReturnsOnlyAfterIdleWorkersExit()
    {
        Thread? worker = null;
        var probe = FixtureProbe((token, _) => worker = new Thread(() => token.WaitHandle.WaitOne()) { IsBackground = true });
        RaplProbeResult result = await probe.RunAsync(Counter(), () => 50, CancellationToken.None).WaitAsync(Budget);
        Assert.False(worker!.IsAlive);
        Assert.NotEqual(RaplGateVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public void PassiveGuiAndCliConsumersNeverInvokeDiagnosticAndShareUnverifiedFilter()
    {
        string root = RepoRoot();
        string sensor = File.ReadAllText(Path.Combine(root, "Services", "SensorService.cs"));
        string cli = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Program.cs"));
        Assert.DoesNotContain("RaplEnergyGate.EnsureStarted(", sensor);
        Assert.DoesNotContain("RaplEnergyGate.RunOnce(", cli);
        Assert.DoesNotContain("RaplEnergyGate.RunOnceAsync(", cli);
        Assert.Contains("public double? CpuPowerW => RaplEnergyGate.ValidatedPower(Val(_cpuPower));", sensor);
        Assert.Contains("public string CpuPowerText => CpuPowerW", sensor);
        Assert.Contains("cpuPower = RaplEnergyGate.ValidatedPower(r0Power ?? cpuPower);", cli);
        Assert.Contains("能量計未驗證", cli);
        Assert.Null(RaplEnergyGate.ValidatedPower(75));
        Assert.Contains("未驗證", RaplEnergyGate.StateText);
    }

    private static RaplEnergyProbe FixtureProbe(Func<CancellationToken, Action, Thread> createWorker,
        Action<int>? beforeStart = null, int workerCount = 1) =>
        new(() => true, () => workerCount, (_, _) => Task.CompletedTask, createWorker, beforeStart, Tick());

    private static Func<long> Tick()
    {
        long now = 0;
        return () => Interlocked.Add(ref now, Stopwatch.Frequency * 3);
    }

    private static Func<ulong?> Counter()
    {
        ulong count = 0;
        return () => count += 100;
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XinSpect.csproj"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
