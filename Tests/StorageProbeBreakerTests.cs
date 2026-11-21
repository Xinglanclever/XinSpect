using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 儲存協定查詢斷路器的契約（效能修復：本機實測 <c>SmartFailingNowFactsService.Collect</c>
/// 12,057 ms → 第二輪 12,028 ms 完全沒改善，根因是兩顆 NVMe 的健康紀錄／Identify 各卡滿 3 秒看門狗）。
/// <para>
/// 這一組<b>完全不碰真實磁碟</b>：鍵一律用假索引（8675309 起跳），避免與其他測試類別的
/// 真碟探測互相影響。時間一律注入，讓「冷卻到期」不必真的等十分鐘。
/// </para>
/// <para>
/// 最重要的兩條界線：<b>只有逾時才記</b>（快速失敗是合法結果，記下來會讓讀得到的地方也變成讀不到），
/// 以及<b>不同查詢各自記帳</b>（同一顆碟的匯流排型別是毫秒級，不能因為 NVMe 查詢逾時被連坐放棄）。
/// </para>
/// </summary>
public class StorageProbeBreakerTests
{
    private const int FakeDrive = 8675309;
    private const int OtherFakeDrive = 8675310;

    [Fact]
    public void 逾時才回報逾時_快速失敗與例外都不算()
    {
        var (slowValue, slowTimedOut) = DiskIo.GuardedProbe<string>(() => { Thread.Sleep(5000); return "太慢了"; }, 200);
        Assert.Null(slowValue);
        Assert.True(slowTimedOut, "睡超過時限的讀取必須被標成逾時——斷路器只靠這個訊號決定要不要記");

        var (nullValue, nullTimedOut) = DiskIo.GuardedProbe<byte[]>(() => null, 1000);
        Assert.Null(nullValue);
        Assert.False(nullTimedOut, "讀取自己快速回 null（裝置說不支援）不是逾時，記下來會誤傷");

        var (exValue, exTimedOut) = DiskIo.GuardedProbe<string>(() => throw new IOException("裝置沒回應"), 1000);
        Assert.Null(exValue);
        Assert.False(exTimedOut, "毫秒級就拋例外不是逾時");

        var (okValue, okTimedOut) = DiskIo.GuardedProbe(() => "ok", 1000);
        Assert.Equal("ok", okValue);
        Assert.False(okTimedOut);
    }

    [Fact]
    public void 實值型別的逾時感知版本_語意相同()
    {
        var (value, timedOut) = DiskIo.GuardedProbeValue<uint>(() => { Thread.Sleep(5000); return 17u; }, 200);

        Assert.Null(value);
        Assert.True(timedOut);
        Assert.Equal(17u, DiskIo.GuardedValue<uint>(() => 17u, 1000));   // 原 API 行為不變
    }

    [Fact]
    public void 記住之後同一鍵直接跳過_其他鍵與其他磁碟不受影響()
    {
        StorageProbeBreaker.Reset();
        try
        {
            StorageProbeBreaker.Trip(FakeDrive, "TryReadNvmeIdentify");

            Assert.True(StorageProbeBreaker.ShouldSkip(FakeDrive, "TryReadNvmeIdentify"));
            Assert.False(StorageProbeBreaker.ShouldSkip(FakeDrive, "TryGetBusType"),
                "同一顆碟的匯流排型別是毫秒級的讀得到——不能因為 NVMe 查詢逾時而被連坐放棄");
            Assert.False(StorageProbeBreaker.ShouldSkip(OtherFakeDrive, "TryReadNvmeIdentify"));
            Assert.Equal(1, StorageProbeBreaker.TrippedCount);
        }
        finally { StorageProbeBreaker.Reset(); }
    }

    [Fact]
    public void 冷卻到期就重新去問_而不是永久放棄()
    {
        StorageProbeBreaker.Reset();
        try
        {
            StorageProbeBreaker.Trip(FakeDrive, "TryReadNvmeLog.2", nowMs: 1_000);

            Assert.True(StorageProbeBreaker.ShouldSkip(FakeDrive, "TryReadNvmeLog.2", nowMs: 1_000 + StorageProbeBreaker.CooldownMs - 1));
            Assert.False(StorageProbeBreaker.ShouldSkip(FakeDrive, "TryReadNvmeLog.2", nowMs: 1_000 + StorageProbeBreaker.CooldownMs),
                "冷卻到期必須再試一次：一次逾時不足以宣稱這顆碟這輩子不會回應");
            Assert.Equal(0, StorageProbeBreaker.TrippedCount);
        }
        finally { StorageProbeBreaker.Reset(); }
    }

    [Fact]
    public void 診斷閘關閉時_不記也不跳()
    {
        StorageProbeBreaker.Reset();
        try
        {
            Environment.SetEnvironmentVariable(StorageProbeBreaker.OffSwitch, "off");
            StorageProbeBreaker.Trip(FakeDrive, "TryReadNvmeIdentify");
            Assert.False(StorageProbeBreaker.ShouldSkip(FakeDrive, "TryReadNvmeIdentify"),
                "閘門關閉時必須完全不經斷路器，否則 A/B 對照量不出斷路器自己的貢獻");
            Assert.Equal(0, StorageProbeBreaker.TrippedCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StorageProbeBreaker.OffSwitch, null);
            StorageProbeBreaker.Reset();
        }
    }

    [Fact]
    public void 跳過會計數_且不影響後續判定()
    {
        StorageProbeBreaker.Reset();
        try
        {
            Assert.Equal(0L, StorageProbeBreaker.SkippedCount);
            StorageProbeBreaker.CountSkip();
            StorageProbeBreaker.CountSkip();
            Assert.Equal(2L, StorageProbeBreaker.SkippedCount);
        }
        finally { StorageProbeBreaker.Reset(); }
    }

    /// <summary>
    /// 接線守門：<see cref="StorageSmartService"/> 裡每一條受看門狗保護的協定查詢都必須走斷路器。
    /// </summary>
    /// <remarks>
    /// 這一條存在的理由與 <c>WiringGuardTests</c> 相同：斷路器寫好了卻有人新增一條直接呼叫
    /// <c>DiskIo.Guarded</c> 的查詢，功能面完全看不出來——只有在真實機器上多等 3 秒、多洩一條
    /// 卡死執行緒。靜態掃描是唯一能在不碰真碟的前提下守住這件事的辦法。
    /// </remarks>
    [Fact]
    public void 受保護的查詢都經過斷路器_不得有一條繞過()
    {
        string source = ReadRepoFile(Path.Combine("Services", "StorageSmartService.cs"));

        var bypass = Regex.Matches(source, @"DiskIo\.Guarded(Value)?\s*\(");
        Assert.True(bypass.Count == 0,
            $"StorageSmartService 還有 {bypass.Count} 處直接呼叫 DiskIo.Guarded／GuardedValue——"
            + "請改走 GuardedRead／GuardedReadValue，否則這條查詢每輪都會重付逾時");

        // 定義是泛型方法（GuardedRead&lt;T&gt;），不會被這個樣式算進去；數到的是呼叫端。
        // NVMe 紀錄頁（TryReadNvmeLog）已改走可取消的 overlapped I/O，不經看門狗，所以是 6 條。
        int wired = Regex.Matches(source, @"GuardedRead(Value)?\s*\(").Count;
        Assert.True(wired >= 6,
            $"只掃到 {wired} 處 GuardedRead／GuardedReadValue 呼叫（預期至少 6 條受保護的協定查詢）；"
            + "新增受保護查詢時請一起接上斷路器");

        // NVMe 紀錄頁不適合看門狗，改走可取消的 overlapped I/O：它自己接斷路器
        // （ShouldSkip ＋ Report），否則既不經 GuardedRead、又沒進斷路器就是個漏洞。
        int nvmeAt = source.IndexOf("public static byte[]? TryReadNvmeLog", StringComparison.Ordinal);
        Assert.True(nvmeAt > 0, "找不到 TryReadNvmeLog");
        string nvme = source.Substring(nvmeAt, 1200);
        Assert.Contains("StorageProbeBreaker.ShouldSkip", nvme);
        Assert.Contains("StorageProbeBreaker.Report", nvme);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(23)]
    [InlineData(27)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(121)]
    [InlineData(1117)]
    [InlineData(1167)]
    [InlineData(1460)]
    public void SurfaceNativeReadFailureTripsOnlyVerifiedDiskAndOperation(int win32)
    {
        int disk = 8760000 + win32;
        var error = new IOException("injected read failure", unchecked((int)(0x80070000u | (uint)win32)));
        var observation = new DiskSurfaceScanService.ReadFailureObservation(disk);
        var actual = Assert.Throws<IOException>(() => observation.Read(() => throw error, CancellationToken.None));
        Assert.Same(error, actual);
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
        observation.Dispose();
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk + 10000, DiskSurfaceScanService.BreakerOperation));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, "TryGetBusType"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(995)]
    [InlineData(123)]
    public async Task SurfaceConfigurationPermissionAndAbortDoNotTrip(int win32)
    {
        int disk = 8780000 + win32;
        var error = new IOException("not device evidence", unchecked((int)(0x80070000u | (uint)win32)));
        using (var observation = new DiskSurfaceScanService.ReadFailureObservation(disk))
        {
            Assert.Same(error, Assert.Throws<IOException>(() => observation.Read(() => throw error, CancellationToken.None)));
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => observation.ReadAsync(
                () => ValueTask.FromException<int>(error), CancellationToken.None).AsTask()));
        }
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
    }

    [Fact]
    public async Task SurfaceRetryRecoveryRetainsObservedDeviceFaultForNextScan()
    {
        const int disk = 8790001;
        int calls = 0;
        using (var observation = new DiskSurfaceScanService.ReadFailureObservation(disk))
        {
            var result = await DiskSurfaceScanService.ReadBlocksAsync("fake", 512, 512, 512,
                (_, memory, token) => observation.ReadAsync(() => ++calls == 1
                    ? ValueTask.FromException<int>(new IOException("CRC", unchecked((int)0x80070017)))
                    : ValueTask.FromResult(memory.Length), token));
            Assert.Equal(2, calls);
            Assert.Equal(1, result!.RetryCount);
            Assert.Equal(0, result.ErrorCount);
        }
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
    }

    [Fact]
    public async Task SurfaceCancellationDoesNotBecomeDeviceFailure()
    {
        const int disk = 8790002;
        using var cancel = new CancellationTokenSource();
        using (var observation = new DiskSurfaceScanService.ReadFailureObservation(disk))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.ReadAsync(() =>
            {
                cancel.Cancel();
                return ValueTask.FromCanceled<int>(cancel.Token);
            }, cancel.Token).AsTask());
        }
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
    }

    [Fact]
    public void SurfaceCancelledIoErrorAndGenericManagedFailureDoNotTrip()
    {
        const int disk = 8790003;
        using var cancel = new CancellationTokenSource();
        using (var observation = new DiskSurfaceScanService.ReadFailureObservation(disk))
        {
            Assert.Throws<IOException>(() => observation.Read(() => throw new IOException("managed"), CancellationToken.None));
            Assert.Throws<IOException>(() => observation.Read(() =>
            {
                cancel.Cancel();
                throw new IOException("cancellation race", unchecked((int)0x8007045D));
            }, cancel.Token));
        }
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
    }

    [Fact]
    public void SurfaceSuccessfulOrShortReadDoesNotClearAnotherSessionsFault()
    {
        const int disk = 8790004;
        using (var observation = new DiskSurfaceScanService.ReadFailureObservation(disk))
        {
            Assert.Equal(512, observation.Read(() => 512, CancellationToken.None));
            StorageProbeBreaker.Trip(disk, DiskSurfaceScanService.BreakerOperation);
            Assert.Equal(0, observation.Read(() => 0, CancellationToken.None));
        }
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
    }

    [Fact]
    public async Task SurfaceInvalidParameterStillEscapesBeforeRetryWithoutCooldown()
    {
        const int disk = 8790005;
        int calls = 0;
        using (var observation = new DiskSurfaceScanService.ReadFailureObservation(disk))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => DiskSurfaceScanService.ReadBlocksAsync("fake", 512, 512, 512,
                (offset, memory, token) => DiskSurfaceScanService.ReadAlignedAsync(offset, memory,
                    new DiskSurfaceBackingDevice.Alignment(512, 4096), (_, _, cancellation) => observation.ReadAsync(() =>
                    {
                        calls++;
                        return ValueTask.FromException<int>(new IOException("invalid parameter", unchecked((int)0x80070057)));
                    }, cancellation), token)));
        }
        Assert.Equal(1, calls);
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation));
    }

    [Fact]
    public void ExpiredCheckCannotDeleteConcurrentNewTrip()
    {
        const int disk = 8790006;
        const long first = 1000;
        long expiry = first + StorageProbeBreaker.CooldownMs;
        int interleavings = 0;
        StorageProbeBreaker.Trip(disk, DiskSurfaceScanService.BreakerOperation, first);
        bool skipped = StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation, expiry,
            beforeExpiredRemoval: () =>
            {
                interleavings++;
                StorageProbeBreaker.Trip(disk, DiskSurfaceScanService.BreakerOperation, expiry);
            });
        Assert.Equal(1, interleavings);
        Assert.True(skipped);
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation, expiry + 1));
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation,
            expiry + StorageProbeBreaker.CooldownMs));
    }

    [Fact]
    public void ExpiredObservedEntryIsRemovedWithoutTouchingAnotherOperation()
    {
        const int disk = 8790007;
        const long first = 2000;
        long expiry = first + StorageProbeBreaker.CooldownMs;
        StorageProbeBreaker.Trip(disk, DiskSurfaceScanService.BreakerOperation, first);
        StorageProbeBreaker.Trip(disk, "other-operation", expiry);
        int removals = 0;
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation, expiry,
            beforeExpiredRemoval: () => removals++));
        Assert.Equal(1, removals);
        Assert.False(StorageProbeBreaker.ShouldSkip(disk, DiskSurfaceScanService.BreakerOperation, expiry + 1));
        Assert.True(StorageProbeBreaker.ShouldSkip(disk, "other-operation", expiry + 1));
    }

    /// <summary>讀原始碼樹裡的檔案（路徑相對於倉庫根目錄）。從測試輸出目錄往上找 csproj。</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
