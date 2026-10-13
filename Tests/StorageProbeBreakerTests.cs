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
        int wired = Regex.Matches(source, @"GuardedRead(Value)?\s*\(").Count;
        Assert.True(wired >= 7,
            $"只掃到 {wired} 處 GuardedRead／GuardedReadValue 呼叫（預期至少 7 條受保護的協定查詢）；"
            + "新增受保護查詢時請一起接上斷路器");
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
