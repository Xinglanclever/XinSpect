using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>Owns Ceiling's linked stop source and every successfully started load worker through real Join.</summary>
internal sealed class CeilingLoadSession
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Func<bool> _allowed;
    private readonly Func<CancellationToken, Action, Thread> _createWorker;
    private readonly Action<int>? _beforeStart;

    internal CeilingLoadSession(Func<bool>? allowed = null,
        Func<CancellationToken, Action, Thread>? createWorker = null, Action<int>? beforeStart = null)
    {
        _allowed = allowed ?? (() => HardwareOperationGate.CanRunAdvancedDiagnostics);
        _createWorker = createWorker ?? ((_, body) => new Thread(() => body())
        {
            IsBackground = true,
            Priority = ThreadPriority.Normal,
            Name = "XinSpect 天花板負載",
        });
        _beforeStart = beforeStart;
    }

    internal void CheckAuthorization(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ReadPolicy(_allowed))
            throw new OperationCanceledException("天花板負載未執行／已停止：第一級進階診斷已關閉。", token);
        token.ThrowIfCancellationRequested();
    }

    private static bool ReadPolicy(Func<bool> allowed)
    {
        try { return allowed(); }
        catch (OperationCanceledException error)
        {
            // A callback fault is not our caller/session cancellation, even if a token
            // happens to be canceled concurrently. Keep its original exception as evidence.
            throw new InvalidOperationException("Ceiling authorization callback failed.", error);
        }
    }

    // Only Check creates this marker. Arbitrary provider OCEs cannot masquerade as stop.
    private sealed class LoadStoppedException(CancellationToken token)
        : OperationCanceledException("天花板負載已停止：取消要求或第一級進階診斷已關閉。", token);

    internal async Task<TResult> ObserveAsync<TResult>(CeilingLoad kind, int threadCount,
        Func<CancellationToken, Action, Task<TResult>> observe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observe);
        CheckAuthorization(cancellationToken);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind != CeilingLoad.Baseline && threadCount < 1)
            throw new ArgumentOutOfRangeException(nameof(threadCount));
        int workerCount = kind == CeilingLoad.Baseline ? 0 : threadCount;
        LoadGate? activeGate = null;
        return await DeepBenchWorkerLifetime.RunAsync<LoadGate, TResult>(
            _runLock, workerCount, () => 0, _ => { },
            () =>
            {
                CheckAuthorization(cancellationToken);
                return activeGate = new LoadGate(cancellationToken, _allowed);
            },
            gate => gate.Stop(), gate => gate.Dispose(),
            (gate, _) =>
            {
                gate.Check();
                return _createWorker(gate.Token, () => gate.Run(kind))
                    ?? throw new InvalidOperationException("Ceiling worker factory did not return a thread.");
            },
            async (gate, _) =>
            {
                gate.Check();
                TResult result = await observe(gate.Token, gate.Check).ConfigureAwait(false);
                gate.Check();
                return result;
            }, cancellationToken,
            beforeStart: index =>
            {
                activeGate!.Check();
                _beforeStart?.Invoke(index);
                // Factory and test seams can synchronously revoke the policy.
                activeGate.Check();
            }).ConfigureAwait(false);
    }

    private sealed class LoadGate : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _callerToken;
        private readonly Func<bool> _allowed;
        private readonly ConcurrentQueue<Exception> _faults = new();
        internal CancellationToken Token { get; }

        internal LoadGate(CancellationToken callerToken, Func<bool> allowed)
        {
            _callerToken = callerToken;
            _allowed = allowed;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            Token = _stop.Token;
        }

        internal void Check()
        {
            ThrowFault();
            if (_callerToken.IsCancellationRequested) throw new LoadStoppedException(_callerToken);
            if (!ReadPolicy(_allowed)) throw new LoadStoppedException(_callerToken);
            if (_callerToken.IsCancellationRequested) throw new LoadStoppedException(_callerToken);
            if (Token.IsCancellationRequested) throw new LoadStoppedException(Token);
            ThrowFault();
        }

        internal void Run(CeilingLoad kind)
        {
            try
            {
                Check();
                RunWorkload(kind, Token, Check);
            }
            catch (LoadStoppedException)
            {
                // A worker may observe revocation before the observer does.
                // Publish stop to its peers without classifying expected cancellation as a fault.
                try { Stop(); }
                catch (Exception stopError) { _faults.Enqueue(stopError); }
            }
            catch (Exception error)
            {
                _faults.Enqueue(error);
                try { Stop(); }
                catch (Exception stopError) { _faults.Enqueue(stopError); }
            }
        }

        internal void Stop() => _stop.Cancel();
        private void ThrowFault()
        {
            if (_faults.TryPeek(out Exception? error)) ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void Dispose()
        {
            _stop.Dispose();
            // Called only after every successful Start has genuinely joined.
            // Do not publish a success when a worker fault arrived during final drain.
            if (!_faults.IsEmpty) throw new AggregateException("Ceiling worker fault after drain.", _faults.ToArray());
        }

        /// <summary>
        /// 自製 CPU 負載。這是本頁唯一「主動做事」的部分：溫度牆、功耗牆與向量降頻在閒置時完全看不見。
        /// </summary>
        /// <remarks>
        /// <b>優先權刻意留在 <see cref="ThreadPriority.Normal"/>，執行緒數也刻意比邏輯處理器少兩個。</b>
        /// 實測用 <c>Highest</c> 跑滿全部邏輯處理器時，取樣執行緒被餓死到 9.08 秒只取得 3 個樣本，
        /// 而且窗長超出預定值 50%——量測工具把自己量壞了。降回一般優先權後取樣完全準時。
        ///
        /// 每個迴圈都把結果餵回下一輪，並以一個永不成立的條件讀取它，
        /// 免得 JIT 判定整段沒有副作用而消掉——那樣就會量到「滿載卻沒升溫」。
        /// </remarks>
        private static void RunWorkload(CeilingLoad kind, CancellationToken ct, Action check)
        {
            switch (kind)
            {
                case CeilingLoad.Integer: Integer(ct, check); break;
                case CeilingLoad.Avx2: Avx2Chain(ct, check); break;
                case CeilingLoad.Avx512: Avx512Chain(ct, check); break;
            }
        }

        /// <summary>純整數乘加鏈：只用整數乘法器與 ALU，完全不觸發向量單元的頻率授權降級。</summary>
        private static void Integer(CancellationToken ct, Action check)
        {
            ulong a = 0x9E3779B97F4A7C15, b = 0xBF58476D1CE4E5B9, c = 0x94D049BB133111EB, d = 1;
            while (!ct.IsCancellationRequested)
            {
                check();
                for (int k = 0; k < 300_000; k++)
                {
                    a = a * 6364136223846793005UL + 1442695040888963407UL;
                    b = b * 2862933555777941757UL + 3037000493UL;
                    c = c * 3935559000370003845UL + 2691343689449507681UL;
                    d ^= (a >> 13) ^ (b >> 17) ^ (c >> 19);
                }
                if (d == 0) return;   // 永不成立；只為讓上面那段有可觀察的結果，不會被最佳化掉
            }
        }

        /// <summary>256 位元浮點乘法鏈。四條互不相依的鏈填滿乘法管線，把功耗與溫度真的推上去。</summary>
        private static void Avx2Chain(CancellationToken ct, Action check)
        {
            var m = Vector256.Create(1.0000000001);
            while (!ct.IsCancellationRequested)
            {
                check();
                var (x, y, z, w) = (Vector256.Create(1.5), Vector256.Create(2.5),
                                    Vector256.Create(3.5), Vector256.Create(4.5));
                for (int k = 0; k < 60_000; k++)
                {
                    x = Avx.Multiply(x, m); y = Avx.Multiply(y, m);
                    z = Avx.Multiply(z, m); w = Avx.Multiply(w, m);
                }
                if (x.GetElement(0) + y.GetElement(1) + z.GetElement(2) + w.GetElement(3) < 0) return;
            }
        }

        /// <summary>512 位元浮點乘法鏈。呼叫端已確認 <see cref="Avx512F.IsSupported"/>。</summary>
        private static void Avx512Chain(CancellationToken ct, Action check)
        {
            var m = Vector512.Create(1.0000000001);
            while (!ct.IsCancellationRequested)
            {
                check();
                var (x, y, z, w) = (Vector512.Create(1.5), Vector512.Create(2.5),
                                    Vector512.Create(3.5), Vector512.Create(4.5));
                for (int k = 0; k < 60_000; k++)
                {
                    x = Avx512F.Multiply(x, m); y = Avx512F.Multiply(y, m);
                    z = Avx512F.Multiply(z, m); w = Avx512F.Multiply(w, m);
                }
                if (x.GetElement(0) + y.GetElement(1) + z.GetElement(2) + w.GetElement(3) < 0) return;
            }
        }

    }
}
