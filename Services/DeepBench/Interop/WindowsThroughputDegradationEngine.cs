using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// Windows 使用者模式吞吐 engine：以 managed 全核心 workers 執行整數運算並逐窗計數，
/// 同窗呼叫 CallNtPowerInformation(ProcessorInformation) 取樣。不改電源計劃、不設優先權、不鎖核心。
/// </summary>
public sealed class WindowsThroughputDegradationEngine : IThroughputDegradationEngine
{
    private const int WorkSliceIterations = 2048;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly WindowsProcessorPowerSampler _power;
    private readonly Func<int> _workerBudget;
    private readonly Func<int, nint> _allocate;
    private readonly Action<nint> _free;
    private readonly Action<int>? _beforeStart;

    public WindowsThroughputDegradationEngine() : this(
        WindowsProcessorPowerSampler.CreateWindows(), () => Environment.ProcessorCount,
        bytes => Marshal.AllocHGlobal(bytes), Marshal.FreeHGlobal) { }

    internal WindowsThroughputDegradationEngine(WindowsProcessorPowerSampler power, Func<int> workerBudget,
        Func<int, nint> allocate, Action<nint> free, Action<int>? beforeStart = null)
    {
        _power = power;
        _workerBudget = workerBudget;
        _allocate = allocate;
        _free = free;
        _beforeStart = beforeStart;
    }

    public async Task<ThroughputDegradationRun> MeasureAsync(
        ThroughputDegradationWorkload workload,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.WarmupMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.Windows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.WindowMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.CooldownMs, 1);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            int workerCount = _workerBudget();
            ProcessorPowerScope scope = _power.CaptureScope();
            scope.ValidateWorkerCount(workerCount);
            return await DeepBenchWorkerLifetime.RunAsync<ThroughputGate, ThroughputDegradationRun>(
                _runLock,
                workerCount,
                () => _allocate(scope.BufferBytes),
                _free,
                () => new ThroughputGate(),
                gate => gate.Stop(),
                null,
                (gate, index) =>
                {
                    var worker = new ThroughputWorker(gate);
                    return new Thread(worker.Run)
                    {
                        IsBackground = true,
                        Name = $"XinSpect Throughput Window {index + 1}"
                    };
                },
                async (gate, buffer) =>
                {
                    await RunWarmupAsync(workload, gate, cancellationToken).ConfigureAwait(false);
                    List<ThroughputWindow> windows = [];
                    for (int index = 0; index < workload.Windows; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        windows.Add(await MeasureWindowAsync(
                            index,
                            workload.WindowMs,
                            scope,
                            buffer,
                            gate,
                            cancellationToken).ConfigureAwait(false));
                    }

                    await Task.Delay(workload.CooldownMs, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    _power.VerifyScope(scope);
                    return new ThroughputDegradationRun(workerCount, windows);
                },
                cancellationToken,
                beforeStart: _beforeStart,
                prepare: buffer => _ = _power.Sample(scope, buffer, true, cancellationToken)).ConfigureAwait(false);
        }
        catch (ProcessorPowerUnsupportedException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ThroughputDegradationUnsupportedException(error.Message);
        }
    }

    private static async Task RunWarmupAsync(
        ThroughputDegradationWorkload workload,
        ThroughputGate gate,
        CancellationToken cancellationToken)
    {
        gate.BeginWindow();
        gate.SetLoad(true);
        await Task.Delay(workload.WarmupMs, cancellationToken).ConfigureAwait(false);
        gate.SetLoad(false);
        gate.WaitIdle();
    }

    private async Task<ThroughputWindow> MeasureWindowAsync(
        int index,
        int windowMs,
        ProcessorPowerScope scope,
        nint buffer,
        ThroughputGate gate,
        CancellationToken cancellationToken)
    {
        gate.BeginWindow();
        long startTimestamp = Stopwatch.GetTimestamp();
        gate.SetLoad(true);

        double currentMin = double.MaxValue;
        double currentMax = 0;
        double limitMin = double.MaxValue;
        double limitMax = 0;
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessorPowerRange range = _power.Sample(scope, buffer, true, cancellationToken);
            currentMin = Math.Min(currentMin, range.CurrentMin);
            currentMax = Math.Max(currentMax, range.CurrentMax);
            limitMin = Math.Min(limitMin, range.LimitMin);
            limitMax = Math.Max(limitMax, range.LimitMax);

            double elapsedMs = Stopwatch.GetElapsedTime(start, Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (elapsedMs >= windowMs)
                break;

            int remainingMs = Math.Max(1, (int)Math.Ceiling(windowMs - elapsedMs));
            await Task.Delay(Math.Min(100, remainingMs), cancellationToken).ConfigureAwait(false);
        }

        gate.SetLoad(false);
        gate.WaitIdle();
        long endTimestamp = Stopwatch.GetTimestamp();
        double durationMs = Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds;
        double operations = Interlocked.Read(ref gate.OperationCount);
        return new ThroughputWindow(
            index,
            durationMs,
            operations,
            operations / (durationMs / 1000.0),
            currentMin,
            currentMax,
            limitMin,
            limitMax);
    }

    private sealed class ThroughputGate
    {
        private int _active;
        private int _inFlight;
        private int _stopped;
        public long OperationCount;

        public bool Active => Volatile.Read(ref _active) != 0;

        public bool Stopped => Volatile.Read(ref _stopped) != 0;

        public void BeginWindow()
        {
            Interlocked.Exchange(ref OperationCount, 0);
            Volatile.Write(ref _active, 0);
            WaitIdle();
        }

        public void SetLoad(bool active) => Volatile.Write(ref _active, active ? 1 : 0);

        public void Stop()
        {
            Volatile.Write(ref _stopped, 1);
            Volatile.Write(ref _active, 0);
        }

        public void WaitIdle()
        {
            SpinWait spin = new();
            while (Volatile.Read(ref _inFlight) != 0)
                spin.SpinOnce();
        }

        public void RunSlice(ref long checksum)
        {
            if (Volatile.Read(ref _active) == 0)
                return;

            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Volatile.Read(ref _active) != 0)
                {
                    // Carry each worker's result into its next slice; the counted work has an observable sink.
                    long local = Volatile.Read(ref checksum);
                    for (int index = 0; index < WorkSliceIterations; index++)
                    {
                        local = local * 1_664_525L + index + 1_013_904_223L;
                        local ^= local >> 7;
                    }

                    Volatile.Write(ref checksum, local);
                    Interlocked.Add(ref OperationCount, WorkSliceIterations);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class ThroughputWorker(ThroughputGate gate)
    {
        private long _checksum = 1;

        public void Run()
        {
            while (!gate.Stopped)
            {
                if (gate.Active)
                    gate.RunSlice(ref _checksum);
                else
                    Thread.Yield();
            }
        }
    }

}
