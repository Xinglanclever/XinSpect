using System;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// NVMe 紀錄頁改用可取消的 overlapped I/O 之後的契約（2026-10-14）。
/// </summary>
/// <remarks>
/// <para>
/// 背景：<c>DiskIo.Guarded</c> 的「逾時就不等它」只保證呼叫端能繼續；卡在
/// <c>DeviceIoControl</c> 核心等待裡的那條執行緒永遠不回來。2026-10-14 的全套測試傾印
/// （<c>work/hangdiag.ps1</c>、<c>work/testhost11820.dmp</c>）顯示數十條執行緒都停在
/// <c>ReadNvmeLogCore @ 321</c>，整份執行緒集區被吃光，行程連測試結果都印不出來。
/// </para>
/// <para>
/// 這一組是<b>靜態＋行為</b>的雙軌守門：行為面驗「不可讀的碟快速回 null 且不卡」，
/// 靜態面驗「沒有人再把 NVMe 查詢接回洩執行緒的看門狗」。
/// </para>
/// </remarks>
public class NvmeOverlappedIoTests
{
    /// <summary>不存在的磁碟索引必須毫秒級回 null，不能走完任何逾時。</summary>
    [Fact]
    public void 不存在的磁碟_快速回讀不到()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var log = StorageSmartService.TryReadNvmeLog(-4242);
        sw.Stop();

        Assert.Null(log);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"不存在的裝置應該立刻失敗（實際上花了 {sw.ElapsedMilliseconds} ms）");
    }

    /// <summary>
    /// 一次讀不到之後，冷卻期間必須直接跳過——這是「不再製造新的卡死執行緒」的唯一防線。
    /// </summary>
    [Fact]
    public void 讀不到之後冷卻期間直接跳過_不再問()
    {
        StorageProbeBreaker.Reset();
        try
        {
            const int fake = 8675321;
            StorageProbeBreaker.Trip(fake, "TryReadNvmeLog.2");
            long before = StorageProbeBreaker.SkippedCount;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = StorageSmartService.TryReadNvmeLog(fake);
            sw.Stop();

            Assert.Null(log);
            Assert.True(StorageProbeBreaker.SkippedCount > before, "冷卻期間必須計一次跳過");
            Assert.True(sw.ElapsedMilliseconds < 50,
                $"跳過必須是立刻的（實際 {sw.ElapsedMilliseconds} ms）——跳過卻還去開檔就等於沒跳過");
        }
        finally { StorageProbeBreaker.Reset(); }
    }

    [Fact]
    public void NVMe查詢不再呼叫洩執行緒的看門狗()
    {
        string source = ReadRepoFile(System.IO.Path.Combine("Services", "StorageSmartService.cs"));

        // ReadNvmeLogCore 必須是 overlapped 版：開檔帶 FILE_FLAG_OVERLAPPED、逾時帶 CancelIoEx。
        Assert.Contains("FileFlagOverlapped", source);
        Assert.Contains("CancelIoEx", source);
        Assert.Contains("ErrorIOPending", source);

        int coreStart = source.IndexOf("private static (byte[]? Log, bool TimedOut) ReadNvmeLogCore", StringComparison.Ordinal);
        Assert.True(coreStart > 0, "找不到 ReadNvmeLogCore");
        string core = source.Substring(coreStart, Math.Min(3000, source.Length - coreStart));
        Assert.DoesNotContain("OpenDrive(index)", core);
        Assert.Contains("OpenDriveOverlapped(index)", core);

        // 也不得再套 DiskIo.Guarded／外層 Task.Wait 計時器——那兩條都會多留一條不回來的執行緒。
        int entryStart = source.IndexOf("public static byte[]? TryReadNvmeLog", StringComparison.Ordinal);
        Assert.True(entryStart > 0, "找不到 TryReadNvmeLog");
        string entry = source.Substring(entryStart, 1200);
        Assert.DoesNotContain("Task.Wait", entry);
        Assert.Contains("CancelIoEx", core);
    }

    [Fact]
    public void 逾時上限是可取消的短等待_不是無限期()
    {
        string source = ReadRepoFile(System.IO.Path.Combine("Services", "StorageSmartService.cs"));
        Assert.Contains("NvmeLogTimeoutMs = 3000", source);
        Assert.Contains("WaitForSingleObject", source);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "XinSpect.csproj")))
                return System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, relativePath));
            dir = dir.Parent;
        }
        throw new System.IO.DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
