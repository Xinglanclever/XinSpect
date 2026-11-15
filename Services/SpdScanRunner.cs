namespace XinSpect;

/// <summary>Bounds the UI wait while preserving each fully read and decoded DIMM.</summary>
internal static class SpdScanRunner
{
    internal static async Task<SpdSurvey> RunAsync(
        Func<CancellationToken, Action<SpdSurvey>, SpdSurvey> read,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SpdSurvey latest = new([], [], [], []);
        var completion = new TaskCompletionSource<SpdSurvey>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.TrySetResult(read(stop.Token, result => Volatile.Write(ref latest, result))); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = "XinSpect SPD survey",
        };
        worker.Start();
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
