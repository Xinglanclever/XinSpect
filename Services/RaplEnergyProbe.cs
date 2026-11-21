using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace XinSpect;

internal sealed record RaplProbeResult(RaplGateVerdict Verdict, string Note, ulong LastRawCount);

/// <summary>Explicit diagnostic only; all platform readers and worker creation are injectable.</summary>
internal sealed class RaplEnergyProbe
{
    internal const double StopTemperatureC = 85;
    private const int MonitorIntervalMs = 100;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Func<bool> _allowed;
    private readonly Func<int> _workerCount;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Func<CancellationToken, Action, Thread> _createWorker;
    private readonly Action<int>? _beforeStart;
    private readonly Func<long> _timestamp;

    internal RaplEnergyProbe(Func<bool>? allowed = null, Func<int>? workerCount = null,
        Func<int, CancellationToken, Task>? delay = null,
        Func<CancellationToken, Action, Thread>? createWorker = null,
        Action<int>? beforeStart = null, Func<long>? timestamp = null)
    {
        _allowed = allowed ?? (() => HardwareOperationGate.CanRunAdvancedDiagnostics);
        _workerCount = workerCount ?? (() => Math.Clamp(Environment.ProcessorCount - 2, 4, 32));
        _delay = delay ?? ((ms, token) => Task.Delay(ms, token));
        _createWorker = createWorker ?? ((_, body) => new Thread(() => body())
            { IsBackground = true, Priority = ThreadPriority.Normal, Name = "XinSpect RAPL validation" });
        _beforeStart = beforeStart;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    internal async Task<RaplProbeResult> RunAsync(Func<ulong?> readEnergy, Func<double?> readTemp,
        CancellationToken cancellationToken)
    {
        CheckAuthorization(cancellationToken);
        ulong? e1 = readEnergy();
        if (e1 is null) return new(RaplGateVerdict.NoCounter, "0x611 讀不到——本平台沒有可用的封裝能量計。", 0);
        CheckSafety(readTemp, cancellationToken);
        long baseStart = _timestamp();
        await MonitorAsync(3000, readTemp, cancellationToken).ConfigureAwait(false);
        ulong? e2 = readEnergy();
        long baseEnd = _timestamp();
        if (e2 is null) return new(RaplGateVerdict.NoCounter, "0x611 第二次讀取失敗。", e1.Value);
        double baseRate = Rate(e1.Value, e2.Value, Stopwatch.GetElapsedTime(baseStart, baseEnd).TotalSeconds);
        int count = _workerCount();
        if (count is < 1 or > 32) throw new InvalidOperationException("RAPL 負載 worker 數必須介於 1 與 32。");
        CheckSafety(readTemp, cancellationToken);

        // The shared owner does not own a native buffer here. It still owns every
        // successfully started worker and the linked stop source until a genuine Join.
        return await DeepBenchWorkerLifetime.RunAsync<LoadGate, RaplProbeResult>(
            _runLock, count, () => 0, _ => { },
            () => { CheckSafety(readTemp, cancellationToken); return new LoadGate(cancellationToken, _allowed); },
            gate => gate.Stop(), gate => gate.Dispose(),
            (gate, _) =>
            {
                CheckSafety(readTemp, cancellationToken);
                return _createWorker(gate.Token, gate.Run);
            },
            async (gate, _) =>
            {
                await MonitorAsync(300, readTemp, cancellationToken, gate).ConfigureAwait(false);
                ulong? l1 = readEnergy();
                double temp1 = CheckSafety(readTemp, cancellationToken, gate);
                long loadStart = _timestamp();
                await MonitorAsync(3000, readTemp, cancellationToken, gate).ConfigureAwait(false);
                ulong? l2 = readEnergy();
                long loadEnd = _timestamp();
                double temp2 = CheckSafety(readTemp, cancellationToken, gate);
                if (l1 is null || l2 is null)
                    return new(RaplGateVerdict.NoCounter, "負載窗讀取失敗。", l2 ?? l1 ?? e2.Value);
                double loadRate = Rate(l1.Value, l2.Value, Stopwatch.GetElapsedTime(loadStart, loadEnd).TotalSeconds);
                var (trusted, note) = CeilingDecoder.ValidateEnergyCounter(baseRate, loadRate, (int)temp1, (int)temp2, true);
                return new(trusted ? RaplGateVerdict.Trusted : RaplGateVerdict.NotResponding, note, l2.Value);
            }, cancellationToken,
            beforeStart: index => { CheckSafety(readTemp, cancellationToken); _beforeStart?.Invoke(index); }).ConfigureAwait(false);
    }

    private void CheckAuthorization(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_allowed()) throw new OperationCanceledException("RAPL 負載未執行／已停止：第一級進階診斷已關閉。", token);
    }

    private double CheckSafety(Func<double?> readTemp, CancellationToken token, LoadGate? gate = null)
    {
        gate?.ThrowWorkerFault();
        CheckAuthorization(token);
        double? temperature = readTemp();
        // A source delegate may synchronously revoke policy or cancellation.
        // Recheck after it returns, especially immediately before Thread.Start.
        CheckAuthorization(token);
        gate?.ThrowWorkerFault();
        if (temperature is not double value || !double.IsFinite(value) || value <= 0 || value >= StopTemperatureC)
            throw new InvalidOperationException($"RAPL 負載未執行／已停止：溫度未知、無效或達到 {StopTemperatureC:0} °C 應用程式中止線。");
        return value;
    }

    private async Task MonitorAsync(int milliseconds, Func<double?> readTemp, CancellationToken token, LoadGate? gate = null)
    {
        for (int remaining = milliseconds; remaining > 0;)
        {
            CheckSafety(readTemp, token, gate);
            int step = Math.Min(remaining, MonitorIntervalMs);
            try { await _delay(step, gate?.Token ?? token).ConfigureAwait(false); }
            catch
            {
                gate?.ThrowWorkerFault();
                throw;
            }
            CheckSafety(readTemp, token, gate);
            remaining -= step;
        }
    }

    private static double Rate(ulong a, ulong b, double seconds)
    {
        // Intel SDM Vol3B 253669-087,16.10.3/Fig16-37: PKG_ENERGY_STATUS is bits31:0.
        // Share the same modulo-2^32 delta as CeilingService; ulong is the MSR transport width.
        uint delta = CeilingDecoder.Delta32(a, b);
        return seconds > 0 ? delta / seconds : 0;
    }

    private sealed class LoadGate : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Func<bool> _allowed;
        private readonly ConcurrentQueue<Exception> _faults = new();
        private long _checksum;
        internal CancellationToken Token { get; }

        internal LoadGate(CancellationToken token, Func<bool> allowed)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _stop.Token;
            _allowed = allowed;
        }

        internal void Run()
        {
            long local = 1;
            try
            {
                while (!Token.IsCancellationRequested)
                {
                    if (!_allowed())
                        throw new OperationCanceledException("RAPL 負載已停止：第一級進階診斷已關閉。", Token);
                    for (int k = 0; k < 20000; k++)
                        local = unchecked(local * 1_664_525L + k + 1_013_904_223L);
                    Volatile.Write(ref _checksum, local);
                }
            }
            catch (Exception error)
            {
                _faults.Enqueue(error);
                try { Stop(); }
                catch (Exception stopError) { _faults.Enqueue(stopError); }
            }
        }

        internal void Stop() => _stop.Cancel();
        internal void ThrowWorkerFault()
        {
            if (_faults.TryPeek(out Exception? error)) ExceptionDispatchInfo.Capture(error).Throw();
        }
        public void Dispose()
        {
            _stop.Dispose();
            // The owner calls Dispose only after every Join. A fault arriving while
            // the observer was finishing must remain visible after drain as well.
            if (!_faults.IsEmpty) throw new AggregateException("RAPL worker fault after drain.", _faults.ToArray());
        }
    }
}
