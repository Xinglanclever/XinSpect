using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// Windows 使用者模式吞吐 engine：以 managed 全核心 workers 執行整數運算並逐窗計數，
/// 同窗呼叫 CallNtPowerInformation(ProcessorInformation) 取樣。不改電源計劃、不設優先權、不鎖核心。
/// </summary>
public sealed class WindowsThroughputDegradationEngine : IThroughputDegradationEngine
{
    private const int ProcessorInformation = 11;
    private const int ProcessorPowerRecordSize = 24;
    private const int WorkSliceIterations = 2048;

    private readonly SemaphoreSlim _runLock = new(1, 1);

    public async Task<ThroughputDegradationRun> MeasureAsync(
        ThroughputDegradationWorkload workload,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.WarmupMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.Windows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.WindowMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.CooldownMs, 1);

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        nint buffer = Marshal.AllocHGlobal(processorCount * ProcessorPowerRecordSize);
        ThroughputGate gate = new();
        List<Thread> workers = [];
        try
        {
            for (int index = 0; index < processorCount; index++)
            {
                var worker = new ThroughputWorker(gate);
                var thread = new Thread(worker.Run)
                {
                    IsBackground = true,
                    Name = $"XinSpect Throughput Window {index + 1}"
                };
                workers.Add(thread);
                thread.Start();
            }

            await RunWarmupAsync(workload, gate, cancellationToken).ConfigureAwait(false);
            List<ThroughputWindow> windows = [];
            for (int index = 0; index < workload.Windows; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                windows.Add(await MeasureWindowAsync(
                    index,
                    workload.WindowMs,
                    processorCount,
                    buffer,
                    (uint)(processorCount * ProcessorPowerRecordSize),
                    gate,
                    cancellationToken).ConfigureAwait(false));
            }

            await Task.Delay(workload.CooldownMs, cancellationToken).ConfigureAwait(false);
            return new ThroughputDegradationRun(processorCount, windows);
        }
        finally
        {
            gate.Stop();
            foreach (Thread worker in workers)
                worker.Join();
            Marshal.FreeHGlobal(buffer);
            _runLock.Release();
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

    private static async Task<ThroughputWindow> MeasureWindowAsync(
        int index,
        int windowMs,
        int processorCount,
        nint buffer,
        uint bufferBytes,
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
            int status = CallNtPowerInformation(
                ProcessorInformation,
                0,
                0,
                buffer,
                bufferBytes);
            if (status != 0)
            {
                throw new ThroughputDegradationUnsupportedException(
                    $"CallNtPowerInformation returned {status}；不輸出部分 throughput degradation 結果。");
            }

            (currentMin, currentMax, limitMin, limitMax) = ReadFrequencyRange(
                currentMin,
                currentMax,
                limitMin,
                limitMax,
                processorCount,
                buffer);

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

    private static (double CurrentMin, double CurrentMax, double LimitMin, double LimitMax) ReadFrequencyRange(
        double currentMin,
        double currentMax,
        double limitMin,
        double limitMax,
        int processorCount,
        nint buffer)
    {
        for (int index = 0; index < processorCount; index++)
        {
            nint record = buffer + index * ProcessorPowerRecordSize;
            uint number = unchecked((uint)Marshal.ReadInt32(record));
            uint maxMhz = unchecked((uint)Marshal.ReadInt32(record + 4));
            uint currentMhz = unchecked((uint)Marshal.ReadInt32(record + 8));
            uint mhzLimit = unchecked((uint)Marshal.ReadInt32(record + 12));
            _ = unchecked((uint)Marshal.ReadInt32(record + 16));
            uint idleState = unchecked((uint)Marshal.ReadInt32(record + 20));
            if (number >= (uint)processorCount || maxMhz == 0)
            {
                throw new ThroughputDegradationUnsupportedException(
                    $"電源 API 回報的核心索引或 MaxMhz 不完整（processor {number}）；不輸出吞吐窗。");
            }

            if (currentMhz == 0 || mhzLimit == 0)
            {
                throw new ThroughputDegradationUnsupportedException(
                    $"電源 API 無法判定 processor {number} 的 CurrentMhz 或 MhzLimit；不猜測頻率。");
            }

            currentMin = Math.Min(currentMin, currentMhz);
            currentMax = Math.Max(currentMax, currentMhz);
            limitMin = Math.Min(limitMin, mhzLimit);
            limitMax = Math.Max(limitMax, mhzLimit);
            _ = idleState;
        }

        return (currentMin, currentMax, limitMin, limitMax);
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

        public void RunSlice()
        {
            if (Volatile.Read(ref _active) == 0)
                return;

            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Volatile.Read(ref _active) != 0)
                {
                    long local = 1;
                    for (int index = 0; index < WorkSliceIterations; index++)
                    {
                        local = local * 1_664_525L + index + 1_013_904_223L;
                        local ^= local >> 7;
                    }

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
        private long _checksum;

        public void Run()
        {
            while (!gate.Stopped)
            {
                if (gate.Active)
                    gate.RunSlice();
                else
                    Thread.Yield();
            }

            if (_checksum == long.MinValue)
                _checksum = 1;
        }
    }

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(
        int level,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize);
}
