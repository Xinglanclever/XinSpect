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

    [Fact]
    public async Task CancellationBetweenShortReadsStopsBeforeNextRequest()
    {
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiskSurfaceScanService.ReadBlocksAsync("fake", 1024, 1024, 512,
                (_, _, _) =>
                {
                    calls++;
                    cancel.Cancel();
                    return ValueTask.FromResult(512);
                }, ct: cancel.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProfilesAndRepeatShareTenAdditionalAttempts()
    {
        int calls = 0;
        var results = await DiskSurfaceScanService.ReadProfilesAsync("fake", 512,
            [512, 8192], true, 512, (_, _, _) =>
            {
                calls++;
                return ValueTask.FromException<int>(new IOException("persistent"));
            });
        Assert.NotNull(results);
        Assert.Equal(4, results!.Count);
        Assert.Equal(14, calls);
        Assert.Equal(10, results.Sum(result => result.Scan.RetryCount));
        Assert.All(results, result => Assert.Equal(1, result.Scan.ErrorCount));
    }

    [Fact]
    public async Task ProfileProgressIsDeliveredInOrderBeforeCompletion()
    {
        var progress = new ProgressSink();
        var results = await DiskSurfaceScanService.ReadProfilesAsync("fake", 512,
            [512, 8192], true, 512, (_, memory, _) => ValueTask.FromResult(memory.Length), progress);
        Assert.NotNull(results);
        Assert.Equal(new[] { 25, 50, 75, 100 }, progress.Values);
        Assert.All(results!, result => Assert.Equal(512, result.Scan.TotalBytes));
    }

    [Fact]
    public async Task CancellationFromFinalProgressDoesNotReturnCompletedScan()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiskSurfaceScanService.ReadBlocksAsync("fake", 512, 512, 512,
                (_, memory, _) => ValueTask.FromResult(memory.Length),
                new CancelProgress(cancel), cancel.Token));
    }

    [Fact]
    public async Task PublicAsyncScansRejectMoreThan64MiBBeforeDeviceAccess()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DiskSurfaceScanService.ScanAsync("bad", 512, DiskSurfaceScanService.MaxProfileBytes + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            DiskSurfaceScanService.ScanProfilesAsync("bad", [512], false, DiskSurfaceScanService.MaxProfileBytes + 1));
    }

    [Fact]
    public async Task ProfileEnumerationStopsAfterFiveElementsBeforeDeviceAccess()
    {
        int enumerated = 0;
        IEnumerable<int> Repeating()
        {
            while (true) { enumerated++; yield return 512; }
        }
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DiskSurfaceScanService.ScanProfilesAsync("bad", Repeating(), false));
        Assert.Equal(5, enumerated);
    }

    [Fact]
    public void CancellationBetweenMetadataQueriesStopsBeforeGeometry()
    {
        using var cancel = new CancellationTokenSource();
        int geometryCalls = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => DiskSurfaceScanService.ReadLocalGeometry("fake",
            _ => { cancel.Cancel(); return 3; },
            _ => { geometryCalls++; return (true, 512u); }, cancel.Token));
        Assert.Equal(0, geometryCalls);
    }

    [Theory]
    [InlineData(false, 512u)]
    [InlineData(true, 0u)]
    [InlineData(true, 513u)]
    [InlineData(true, 131072u)]
    public void UnknownOrMalformedSectorGeometryFailsClosed(bool success, uint bytes)
    {
        Assert.Equal(0, DiskSurfaceScanService.ReadLocalGeometry("fake", _ => 3,
            _ => (success, bytes), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotReleaseReadBufferBeforePendingIoCompletes()
    {
        using var cancel = new CancellationTokenSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Memory<byte> pendingBuffer = default;
        int calls = 0;
        var scan = DiskSurfaceScanService.ReadBlocksAsync("fake", 512, 512, 512,
            (_, memory, _) => { calls++; pendingBuffer = memory; return new ValueTask<int>(completion.Task); }, ct: cancel.Token);
        cancel.Cancel();
        Assert.False(scan.IsCompleted); // Caller cancellation does not mean the underlying I/O has completed.
        Assert.Equal(512, pendingBuffer.Length);
        pendingBuffer.Span.Fill(0x5a);
        completion.SetResult(512);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(1, calls);
    }

    private sealed class CancelProgress(CancellationTokenSource cancel) : IProgress<int>
    {
        public void Report(int value) => cancel.Cancel();
    }

    private sealed class ProgressSink : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
}
