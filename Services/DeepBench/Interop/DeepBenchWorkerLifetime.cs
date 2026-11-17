using System.Runtime.ExceptionServices;

namespace XinSpect;

/// <summary>Owns the two power-observation engines' buffer, gate and successfully started workers.</summary>
internal static class DeepBenchWorkerLifetime
{
    internal static async Task<TResult> RunAsync<TGate, TResult>(
        SemaphoreSlim runLock,
        int workerCount,
        Func<nint> allocateBuffer,
        Action<nint> freeBuffer,
        Func<TGate> createGate,
        Action<TGate> stopGate,
        Action<TGate>? disposeGate,
        Func<TGate, int, Thread> createWorker,
        Func<TGate, nint, Task<TResult>> measure,
        CancellationToken cancellationToken,
        Action<int>? beforeStart = null,
        Action<ThreadInterruptedException>? onJoinInterrupted = null,
        Action<nint>? prepare = null)
        where TGate : class
    {
        await runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        nint buffer = 0;
        bool ownsBuffer = false;
        TGate? gate = null;
        Thread[]? workers = null;
        int startedCount = 0;
        TResult result = default!;
        Exception? primary = null;
        List<Exception>? cleanupErrors = null;
        bool drained = true;
        try
        {
            buffer = allocateBuffer();
            ownsBuffer = true;
            cancellationToken.ThrowIfCancellationRequested();
            prepare?.Invoke(buffer);
            cancellationToken.ThrowIfCancellationRequested();
            gate = createGate();
            workers = new Thread[workerCount];
            for (int index = 0; index < workerCount; index++)
            {
                Thread worker = createWorker(gate, index);
                beforeStart?.Invoke(index);
                worker.Start();
                // The preallocated slot cannot allocate or throw after ownership begins.
                workers[startedCount++] = worker;
            }

            result = await measure(gate, buffer).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            primary = error;
        }
        finally
        {
            if (gate is not null)
            {
                try { stopGate(gate); }
                catch (Exception error) { RecordCleanup(error); }

                for (int index = 0; index < startedCount; index++)
                {
                    Thread worker = workers![index];
                    while (true)
                    {
                        try { worker.Join(); break; }
                        catch (ThreadInterruptedException error)
                        {
                            // Interruption is reported only after the worker really exits.
                            RecordCleanup(error);
                            if (onJoinInterrupted is not null)
                            {
                                try { onJoinInterrupted(error); }
                                catch (Exception observerError) { RecordCleanup(observerError); }
                            }
                        }
                        catch (Exception error)
                        {
                            RecordCleanup(error);
                            // Private factories return fresh unstarted threads. An unexpected
                            // Join failure must not release resources still used by a live one.
                            drained &= !worker.IsAlive;
                            break;
                        }
                    }
                }

                if (drained && disposeGate is not null)
                {
                    try { disposeGate(gate); }
                    catch (Exception error) { RecordCleanup(error); }
                }
            }

            if (drained)
            {
                if (ownsBuffer)
                {
                    try { freeBuffer(buffer); }
                    catch (Exception error) { RecordCleanup(error); }
                }
                try { runLock.Release(); }
                catch (Exception error) { RecordCleanup(error); }
            }
        }

        if (primary is not null)
        {
            if (cleanupErrors is not null)
                throw new AggregateException("Power observation failed and cleanup reported errors.", [primary, .. cleanupErrors]);
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if (cleanupErrors is { Count: 1 })
            ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();
        if (cleanupErrors is not null)
            throw new AggregateException("Power observation cleanup reported errors.", cleanupErrors);
        return result;

        void RecordCleanup(Exception error) => (cleanupErrors ??= []).Add(error);
    }
}
