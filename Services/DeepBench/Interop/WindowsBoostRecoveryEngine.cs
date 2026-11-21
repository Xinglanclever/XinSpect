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
    private const int LoadSliceIterations = 4096;

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly WindowsProcessorPowerSampler _power;
    private readonly Func<int> _workerBudget;
    private readonly Func<int, nint> _allocate;
    private readonly Action<nint> _free;
    private readonly Action<int>? _beforeStart;

    public WindowsBoostRecoveryEngine() : this(
        WindowsProcessorPowerSampler.CreateWindows(), () => Environment.ProcessorCount,
        bytes => Marshal.AllocHGlobal(bytes), Marshal.FreeHGlobal) { }

    internal WindowsBoostRecoveryEngine(WindowsProcessorPowerSampler power, Func<int> workerBudget,
        Func<int, nint> allocate, Action<nint> free, Action<int>? beforeStart = null)
    {
        _power = power;
        _workerBudget = workerBudget;
        _allocate = allocate;
        _free = free;
        _beforeStart = beforeStart;
    }

    public async Task<BoostRecoveryRun> MeasureAsync(
        BoostRecoveryWorkload workload,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.Rounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.IdleMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.LoadMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.RecoveryMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workload.SamplingIntervalMs, 1);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            int workerCount = _workerBudget();
            ProcessorPowerScope scope = _power.CaptureScope();
            scope.ValidateWorkerCount(workerCount);
            return await DeepBenchWorkerLifetime.RunAsync<PulseGate, BoostRecoveryRun>(
                _runLock,
                workerCount,
                () => _allocate(scope.BufferBytes),
                _free,
                () => new PulseGate(),
                gate => gate.Stop(),
                gate => gate.Dispose(),
                (gate, index) =>
                {
                    var worker = new PulseWorker(gate);
                    return new Thread(worker.Run)
                    {
                        IsBackground = true,
                        Name = $"XinSpect Boost Pulse {index + 1}"
                    };
                },
                async (gate, buffer) =>
                {
                    var rounds = new List<BoostRecoveryRound>(workload.Rounds);
                    for (int round = 0; round < workload.Rounds; round++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        rounds.Add(await ObserveRoundAsync(
                            workload,
                            scope,
                            buffer,
                            gate,
                            cancellationToken).ConfigureAwait(false));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    _power.VerifyScope(scope);
                    return new BoostRecoveryRun(workerCount, rounds);
                },
                cancellationToken,
                beforeStart: _beforeStart,
                prepare: buffer => _ = _power.Sample(scope, buffer, false, cancellationToken)).ConfigureAwait(false);
        }
        catch (ProcessorPowerUnsupportedException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new BoostRecoveryUnsupportedException(error.Message);
        }
    }

    private async Task<BoostRecoveryRound> ObserveRoundAsync(
        BoostRecoveryWorkload workload,
        ProcessorPowerScope scope,
        nint buffer,
        PulseGate gate,
        CancellationToken cancellationToken)
    {
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
            ProcessorPowerRange range = _power.Sample(scope, buffer, false, cancellationToken);
            long queryEnd = range.QueryCompletedTimestamp;

            samples.Add(new BoostRecoverySample(
                loadActive ? BoostRecoveryPhase.Load
                    : elapsedMs >= workload.IdleMs + workload.LoadMs ? BoostRecoveryPhase.Recovery
                    : BoostRecoveryPhase.Idle,
                Stopwatch.GetElapsedTime(roundStart, queryEnd).TotalMilliseconds,
                range.CurrentMin, range.CurrentMax, range.LimitMin, range.LimitMax));
            await Task.Delay(workload.SamplingIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        return new BoostRecoveryRound(samples);
    }

    private sealed class PulseGate : IDisposable
    {
        private readonly ManualResetEventSlim _load = new(false);
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly WaitHandle[] _waitHandles;

        public PulseGate()
        {
            try { _waitHandles = [_stop.WaitHandle, _load.WaitHandle]; }
            catch
            {
                _load.Dispose();
                _stop.Dispose();
                throw;
            }
        }

        public void SetLoad(bool active)
        {
            if (active) _load.Set();
            else _load.Reset();
        }

        public void Stop()
        {
            _stop.Set();
            _load.Set();
        }

        public bool WaitForLoadOrStop() => WaitHandle.WaitAny(_waitHandles) != 0 && !_stop.IsSet;

        public bool IsLoadActive => _load.IsSet && !_stop.IsSet;

        public void Dispose()
        {
            _load.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class PulseWorker(PulseGate gate)
    {
        private long _checksum;

        public void Run()
        {
            long local = 1;
            while (gate.WaitForLoadOrStop())
            {
                while (gate.IsLoadActive)
                {
                    for (int index = 0; index < LoadSliceIterations; index++)
                    {
                        local = local * 1_664_525L + index + 1_013_904_223L;
                        local ^= local >> 7;
                    }

                    _checksum = unchecked(_checksum + local);
                }
            }

            if (_checksum == long.MinValue)
                _checksum = 1;
        }
    }

}
