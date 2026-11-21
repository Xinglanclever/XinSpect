using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class SpdScanRunnerTests
{
    [Fact]
    public async Task CancelledRequestDoesNotStartNativeRead()
    {
        bool read = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpdScanRunner.RunAsync((_, _) =>
        {
            read = true;
            return new([], [], [], []);
        }, TimeSpan.FromSeconds(1), new CancellationToken(canceled: true)));
        Assert.False(read);
    }

    [Fact]
    public async Task InvalidTimeoutDoesNotStartNativeRead()
    {
        bool read = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SpdScanRunner.RunAsync((_, _) =>
        {
            read = true;
            return new([], [], [], []);
        }, TimeSpan.FromSeconds(-2)));
        Assert.False(read);
    }

    [Fact]
    public async Task TimedOutNativeReadPreventsStartingAnotherWorker()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? worker = null;
        var first = SpdScanRunner.RunAsync((_, _) =>
        {
            worker = Thread.CurrentThread;
            entered.TrySetResult();
            try { release.Wait(); return new([], [], [], []); }
            finally { finished.TrySetResult(); }
        }, TimeSpan.FromSeconds(1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await first.WaitAsync(TimeSpan.FromSeconds(15));
            bool restarted = false;
            var second = await SpdScanRunner.RunAsync((_, _) =>
            {
                restarted = true;
                return new([], [], [], []);
            }, TimeSpan.FromSeconds(1));
            Assert.False(restarted);
            Assert.Contains(second.Notes, note => note.Contains("尚未結束"));
            bool externalStarted = false;
            var external = await SpdScanRunner.RunExternalAsync(() =>
            {
                externalStarted = true;
                return Task.FromResult(new CpuzReport());
            });
            Assert.Null(external);
            Assert.False(externalStarted);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(worker!.Join(TimeSpan.FromSeconds(15)));
        }
    }

    [Fact]
    public async Task ExternalReadHoldsGateUntilItsCleanupFinishes()
    {
        var release = new TaskCompletionSource<CpuzReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = SpdScanRunner.RunExternalAsync(() => release.Task);
        try
        {
            bool started = false;
            await SpdScanRunner.RunAsync((_, _) =>
            {
                started = true;
                return new([], [], [], []);
            }, TimeSpan.FromSeconds(1));
            Assert.False(started);
        }
        finally { release.SetResult(new()); await external; }
        Assert.NotNull(await SpdScanRunner.RunExternalAsync(() => Task.FromResult(new CpuzReport())));
    }

    [Fact]
    public async Task ExternalCancellationReturnsButRetainsGateUntilProcessCleanup()
    {
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource<CpuzReport>();
        var operation = SpdScanRunner.RunExternalAsync(() => release.Task, cancel.Token);
        try
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            bool started = false;
            Assert.Null(await SpdScanRunner.RunExternalAsync(() =>
            {
                started = true;
                return Task.FromResult(new CpuzReport());
            }));
            Assert.False(started);
        }
        finally
        {
            release.TrySetResult(new());
        }
        Assert.NotNull(await SpdScanRunner.RunExternalAsync(() => Task.FromResult(new CpuzReport())));
    }

    [Fact]
    public async Task 逾時取消剩餘掃描但保留已讀模組和原因()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        var partial = new SpdSurvey([new("test bus", 0x50, raw, SpdDecoder.Decode(raw)!)], [], ["earlier bus blocked"], []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? worker = null;
        var resultTask = SpdScanRunner.RunAsync((token, progress) =>
        {
            worker = Thread.CurrentThread;
            progress(partial);
            entered.TrySetResult();
            try
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return partial;
            }
            finally { finished.TrySetResult(); }
        }, TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(15));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(worker!.Join(TimeSpan.FromSeconds(15)));
        Assert.Single(result.Modules);
        Assert.Contains("earlier bus blocked", result.Notes);
        Assert.Contains(result.Notes, n => n.Contains("取消剩餘掃描"));
        Assert.Single(partial.Notes);
    }

    [Fact]
    public async Task 使用者取消向外傳遞且背景工作收到取消()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? worker = null;
        var task = SpdScanRunner.RunAsync((token, _) =>
        {
            worker = Thread.CurrentThread;
            entered.TrySetResult();
            try
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return new([], [], [], []);
            }
            finally { finished.TrySetResult(); }
        }, TimeSpan.FromMinutes(1), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(worker!.Join(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task 及時完成返回完整結果且不加逾時訊息()
    {
        var expected = new SpdSurvey([], [], ["complete"], []);
        var actual = await SpdScanRunner.RunAsync((_, _) => expected, TimeSpan.FromSeconds(5));
        Assert.Same(expected, actual);
    }
}
