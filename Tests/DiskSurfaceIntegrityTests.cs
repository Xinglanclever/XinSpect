using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>注入同步讀取結果，驗證掃描範圍與短讀；不開啟磁碟、不作實機負載。</summary>
public class DiskSurfaceIntegrityTests
{
    [Fact]
    public void 尾塊必須列入且只請求剩餘長度()
    {
        var requests = new List<(long Offset, int Bytes)>();
        var progress = new ProgressSink();
        var result = DiskSurfaceScanService.ReadBlocks("fake", 2560, 1024, 0,
            (offset, buffer, bytes) =>
            {
                Assert.True(buffer.Length >= bytes);
                requests.Add((offset, bytes));
                return (true, bytes);
            }, progress)!;
        Assert.Equal(new[] { (0L, 1024), (1024L, 1024), (2048L, 512) }, requests);
        Assert.Equal(3, result.TotalBlocks);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(3, result.OkCount + result.SlowCount);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public void 小於一塊的裝置也有一塊_不當成沒有資料()
    {
        var result = DiskSurfaceScanService.ReadBlocks("fake", 512, 1024, 0,
            (offset, buffer, bytes) =>
            {
                Assert.Equal(0, offset);
                Assert.Equal(512, bytes);
                Assert.Equal(512, buffer.Length);
                return (true, bytes);
            })!;
        Assert.Equal(1, result.TotalBlocks);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void 上限末塊短讀仍是錯誤_不可冒充真實尾塊()
    {
        int calls = 0;
        var result = DiskSurfaceScanService.ReadBlocks("fake", 4096, 1024, 2,
            (_, _, bytes) => (true, ++calls == 2 ? 512 : bytes))!;
        Assert.Equal(2, calls);
        Assert.Equal(2, result.TotalBlocks);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(SurfaceBlockStatus.Error, result.Blocks[^1].Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(513)]
    public void 真實尾塊也必須讀足預期長度(int returnedBytes)
    {
        var result = DiskSurfaceScanService.ReadBlocks("fake", 1536, 1024, 0,
            (offset, _, bytes) => (true, offset == 0 ? bytes : returnedBytes))!;
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(SurfaceBlockStatus.Error, result.Blocks[1].Status);
    }

    [Fact]
    public void 讀取失敗即使回足長度也不能算成功_進度仍完成()
    {
        var progress = new ProgressSink();
        var result = DiskSurfaceScanService.ReadBlocks("fake", 2048, 1024, 0,
            (_, _, bytes) => (false, bytes), progress)!;
        Assert.Equal(2, result.ErrorCount);
        Assert.Equal(0, result.OkCount + result.SlowCount);
        Assert.Equal(100, progress.Values[^1]);
    }

    [Fact]
    public void 預先取消_不得開啟裝置或呼叫讀取()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DiskSurfaceScanService.Scan("not-a-device", ct: cancel.Token));
        Assert.Throws<OperationCanceledException>(() =>
            DiskSurfaceScanService.ReadBlocks("fake", 1024, 1024, 0,
                (_, _, _) => throw new InvalidOperationException("不應讀取"), ct: cancel.Token));
    }

    [Fact]
    public void 最後一塊進行中取消_不得回報完成結果()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new ProgressSink();
        Assert.Throws<OperationCanceledException>(() =>
            DiskSurfaceScanService.ReadBlocks("fake", 1024, 1024, 0,
                (_, _, bytes) => { cancel.Cancel(); return (true, bytes); }, progress, cancel.Token));
        Assert.Empty(progress.Values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void 非法塊尺在開啟裝置前拒絕(int blockSizeKb)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiskSurfaceScanService.Scan("not-a-device", blockSizeKB: blockSizeKb));
    }

    [Fact]
    public void 負數上限拒絕_不默默變無上限()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiskSurfaceScanService.Scan("not-a-device", maxBlocks: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiskSurfaceScanService.ReadBlocks("fake", 1024, 1024, -1, (_, _, n) => (true, n)));
    }

    [Fact]
    public void 大長度以餘數計算不溢位_仍受上限限制()
    {
        int calls = 0;
        var result = DiskSurfaceScanService.ReadBlocks("fake", long.MaxValue, 1024, 2,
            (_, _, bytes) => { calls++; return (true, bytes); })!;
        Assert.Equal(2, calls);
        Assert.Equal(2, result.TotalBlocks);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public async Task 非同步讀取短讀後續讀_以10次重試預算收口()
    {
        int calls = 0;
        var result = await DiskSurfaceScanService.ReadBlocksAsync("fake", 1024, 1024, 512,
            (_, _, _) =>
            {
                calls++;
                return calls <= 3
                    ? ValueTask.FromException<int>(new IOException("transient"))
                    : ValueTask.FromResult(1024);
            });
        Assert.NotNull(result);
        Assert.Equal(4, calls);
        Assert.Equal(3, result!.RetryCount);
        Assert.Equal(1, result.OkCount);
    }

    [Fact]
    public async Task 非同步讀取重試耗盡後保留錯誤且不再讀()
    {
        int calls = 0;
        var result = await DiskSurfaceScanService.ReadBlocksAsync("fake", 1024, 1024, 512,
            (_, _, _) =>
            {
                calls++;
                return ValueTask.FromException<int>(new IOException("persistent"));
            });
        Assert.NotNull(result);
        Assert.Equal(11, calls);
        Assert.Equal(10, result!.RetryCount);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public async Task 非同步讀取可在進行中取消且不產生完成結果()
    {
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DiskSurfaceScanService.ReadBlocksAsync("fake", 2048, 1024, 512,
                (_, buffer, token) =>
                {
                    calls++;
                    cancel.Cancel();
                    return ValueTask.FromCanceled<int>(token);
                }, ct: cancel.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task 分尺只接受計畫中的四種大小()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DiskSurfaceScanService.ScanProfilesAsync("bad", [4096], repeat: false));
    }

    private sealed class ProgressSink : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
}
