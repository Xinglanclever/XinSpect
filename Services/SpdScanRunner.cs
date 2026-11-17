namespace XinSpect;

/// <summary>Bounds the UI wait while preserving each fully read and decoded DIMM.</summary>
internal static class SpdScanRunner
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);

    internal static async Task<T?> RunExternalAsync<T>(Func<Task<T>> read,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ScanGate.Wait(0, cancellationToken)) return null;
        Task<T> operation;
        try { operation = read(); }
        catch { ScanGate.Release(); throw; }
        try { return await operation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false); }
        finally
        {
            _ = operation.ContinueWith(task =>
            {
                _ = task.Exception;
                ScanGate.Release();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static async Task<SpdSurvey> RunAsync(
        Func<CancellationToken, Action<SpdSurvey>, SpdSurvey> read,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        if (!ScanGate.Wait(0, cancellationToken))
            return new([], [], ["前次 SPD 原生讀取尚未結束；本次未重啟掃描，無法判定 DIMM 直讀結果。"], []);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SpdSurvey latest = new([], [], [], []);
        var completion = new TaskCompletionSource<SpdSurvey>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            SpdSurvey? result = null;
            Exception? failure = null;
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                result = read(stop.Token, partial => Volatile.Write(ref latest, partial));
            }
            catch (Exception ex) { failure = ex; }
            finally { ScanGate.Release(); }
            if (failure is not null) completion.TrySetException(failure);
            else completion.TrySetResult(result!);
        })
        {
            IsBackground = true,
            Name = "XinSpect SPD survey",
        };
        try { worker.Start(); }
        catch
        {
            ScanGate.Release();
            stop.Dispose();
            throw;
        }
        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stop.Cancel();
            var partial = Volatile.Read(ref latest);
            return partial with
            {
                Notes = [.. partial.Notes,
                    $"SPD 直讀超過 {timeout.TotalSeconds:0.##} 秒，已要求取消剩餘掃描；保留已完成的 SPD 模組與 Windows WMI 設定值。"],
            };
        }
        catch (OperationCanceledException)
        {
            stop.Cancel();
            throw;
        }
        finally
        {
            // A native I/O call can outlive the UI timeout. Keep its token source alive
            // until its cleanup has finished, and observe any late exception.
            _ = completion.Task.ContinueWith(task =>
            {
                _ = task.Exception;
                stop.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
