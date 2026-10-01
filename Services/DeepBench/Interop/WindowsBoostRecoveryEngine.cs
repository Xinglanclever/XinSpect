using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XinSpect;

/// <summary>
/// Windows 使用者模式脈衝引擎：以 managed 全核心 workers 施加短時負載，
/// 同時呼叫 CallNtPowerInformation(ProcessorInformation) 取樣快照。
/// 不改電源計劃、不設優先權、不鎖核心；API 失敗或 CurrentMhz 無法判定時如實拒收。
/// </summary>
public sealed class WindowsBoostRecoveryEngine : IBoostRecoveryEngine
{
    private const int ProcessorInformation = 11;
    private const int ProcessorPowerRecordSize = 24;
    private const int LoadSliceIterations = 4096;

    private readonly SemaphoreSlim _runLock = new(1, 1);

    public async Task<BoostRecoveryRun> MeasureAsync(
        BoostRecoveryWorkload workload,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.Rounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.IdleMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.LoadMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.RecoveryMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.SamplingIntervalMs, 1);

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        int processorCount = Environment.ProcessorCount;
        nint buffer = Marshal.AllocHGlobal(processorCount * ProcessorPowerRecordSize);
        PulseGate gate = new();
        List<Thread> workers = [];
        try
        {
            for (int index = 0; index < processorCount; index++)
            {
                var worker = new PulseWorker(gate);
                var thread = new Thread(worker.Run)
                {
                    IsBackground = true,
                    Name = $"XinSpect Boost Pulse {index + 1}"
                };
                workers.Add(thread);
                thread.Start();
            }

            var rounds = new List<BoostRecoveryRound>(workload.Rounds);
            for (int round = 0; round < workload.Rounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rounds.Add(await ObserveRoundAsync(
                    workload,
                    processorCount,
                    buffer,
                    (uint)(processorCount * ProcessorPowerRecordSize),
                    gate,
                    cancellationToken).ConfigureAwait(false));
            }

            return new BoostRecoveryRun(processorCount, rounds);
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

    private static async Task<BoostRecoveryRound> ObserveRoundAsync(
        BoostRecoveryWorkload workload,
        int processorCount,
        nint buffer,
        uint bufferBytes,
        PulseGate gate,
        CancellationToken cancellationToken)
    {
        _ = processorCount;
        long roundStart = Stopwatch.GetTimestamp();
        List<BoostRecoverySample> samples = [];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double elapsedMs = Stopwatch.GetElapsedTime(roundStart, Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (elapsedMs >= workload.IdleMs + workload.LoadMs + workload.RecoveryMs)
                break;

            bool loadActive = elapsedMs >= workload.IdleMs && elapsedMs < workload.IdleMs + workload.LoadMs;
            gate.SetLoad(loadActive);
            long queryStart = Stopwatch.GetTimestamp();
            int status = CallNtPowerInformation(
                ProcessorInformation,
                0,
                0,
                buffer,
                bufferBytes);
            long queryEnd = Stopwatch.GetTimestamp();
            if (status != 0)
            {
                throw new BoostRecoveryUnsupportedException(
                    $"CallNtPowerInformation returned {status}；不輸出部分 boost recovery 結果。");
            }

            samples.Add(CreateSample(
                loadActive ? BoostRecoveryPhase.Load
                    : elapsedMs >= workload.IdleMs + workload.LoadMs ? BoostRecoveryPhase.Recovery
                    : BoostRecoveryPhase.Idle,
                Stopwatch.GetElapsedTime(roundStart, queryEnd).TotalMilliseconds,
                processorCount,
                buffer));
            await Task.Delay(workload.SamplingIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        return new BoostRecoveryRound(samples);
    }

    private static BoostRecoverySample CreateSample(
        BoostRecoveryPhase phase,
        double timeMs,
        int processorCount,
        nint buffer)
    {
        double currentMin = double.MaxValue;
        double currentMax = 0;
        double limitMin = double.MaxValue;
        double limitMax = 0;
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
                throw new BoostRecoveryUnsupportedException(
                    $"電源 API 回報的核心索引或 MaxMhz 不完整（processor {number}）；不輸出脈衝曲線。");
            }

            if (currentMhz == 0)
            {
                throw new BoostRecoveryUnsupportedException(
                    $"電源 API 無法判定 processor {number} 的 CurrentMhz；不猜測脈衝頻率。");
            }

            currentMin = Math.Min(currentMin, currentMhz);
            currentMax = Math.Max(currentMax, currentMhz);
            limitMin = Math.Min(limitMin, mhzLimit);
            limitMax = Math.Max(limitMax, mhzLimit);
            _ = idleState;
        }

        return new BoostRecoverySample(phase, timeMs, currentMin, currentMax, limitMin, limitMax);
    }

    private sealed class PulseGate
    {
        private int _state;

        public void SetLoad(bool active) => Volatile.Write(ref _state, active ? 2 : 1);

        public void Stop() => Volatile.Write(ref _state, 0);

        public int State => Volatile.Read(ref _state);
    }

    private sealed class PulseWorker(PulseGate gate)
    {
        private long _checksum;

        public void Run()
        {
            long local = 1;
            while (gate.State != 0)
            {
                if (gate.State == 2)
                {
                    for (int index = 0; index < LoadSliceIterations; index++)
                    {
                        local = local * 1_664_525L + index + 1_013_904_223L;
                        local ^= local >> 7;
                    }

                    _checksum = unchecked(_checksum + local);
                }
                else
                {
                    Thread.Yield();
                }
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
