using System.IO;
using Xunit;

namespace XinSpect.Tests;

public sealed class SpdScanRunnerTests
{
    [Fact]
    public async Task 逾時取消剩餘掃描但保留已讀模組和原因()
    {
        var raw = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "spd-ddr4-real-dimm1.bin"));
        var partial = new SpdSurvey([new("test bus", 0x50, raw, SpdDecoder.Decode(raw)!)], [], ["earlier bus blocked"], []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultTask = SpdScanRunner.RunAsync((token, progress) =>
        {
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
        var task = SpdScanRunner.RunAsync((token, _) =>
        {
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
    }

    [Fact]
    public async Task 及時完成返回完整結果且不加逾時訊息()
    {
        var expected = new SpdSurvey([], [], ["complete"], []);
        var actual = await SpdScanRunner.RunAsync((_, _) => expected, TimeSpan.FromSeconds(5));
        Assert.Same(expected, actual);
    }
}
