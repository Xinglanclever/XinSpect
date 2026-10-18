using System;
using System.Linq;
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
/// 這一組是<b>靜態＋行為</b>的雙軌守門：行為面驗「不可讀的碟快速回 null 且不卡」、
/// 「真的卡住的碟也要在外層預算內交還控制權」，靜態面驗「可取消的內層還在、
/// 而外層上限用的是專屬執行緒而不是執行緒集區」。
/// </para>
/// <para>
/// <b>2026-10-18 修正（原本只有內層，禁外層）</b>：禁令的前提是「呼叫一定會走到那次等待」，
/// 而實測顯示本機 <c>PhysicalDrive 2／4</c> 卡在開檔或 IOCTL 本身，<c>CancelIoEx</c> 沒有機會送出。
/// 所以現在是「內層可取消 ＋ 外層有預算」，兩個一起才是完整防護；細節寫在
/// <see cref="StorageSmartService"/> 的 <c>TryReadNvmeLog</c> 備註。
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
    public void NVMe查詢保留可取消的內層_而且外層上限是專屬執行緒不是集區()
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
        Assert.Contains("CancelIoEx", core);

        // 掃描對象是「程式碼」，不是散文：註解裡正好寫著「刻意不用 Task.Run」——
        // 拿全文去比會把說明當成實作，這條守門就會因為自己寫對了而變紅。
        string code = string.Join('\n', source.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // 外層上限必須存在（2026-10-18：內層的前提「呼叫會走到那次等待」在實機上不成立）。
        int entryStart = code.IndexOf("public static byte[]? TryReadNvmeLog", StringComparison.Ordinal);
        Assert.True(entryStart > 0, "找不到 TryReadNvmeLog");
        string entry = code.Substring(entryStart, 1200);
        Assert.Contains("BoundedProbeValue(", entry);
        Assert.Contains("NvmeOuterBudgetMs", entry);

        // 但不得借執行緒集區：集區被卡死的查詢吃光時，整個行程連輸出都印不出來（2026-10-14 傾印）。
        Assert.DoesNotContain("Task.Run", entry);
        Assert.DoesNotContain("DiskIo.Guarded", entry);

        int probeStart = code.IndexOf("private static (T? Value, bool TimedOut) BoundedProbeValue<T>", StringComparison.Ordinal);
        Assert.True(probeStart > 0, "找不到 BoundedProbeValue");
        string probe = code.Substring(probeStart, 900);
        Assert.Contains("new System.Threading.Thread", probe);
        Assert.Contains("IsBackground = true", probe);
        Assert.Contains("t.Join(budgetMs)", probe);
    }

    /// <summary>
    /// 這一條是 2026-10-18 那個缺陷的<b>行為面</b>守門：
    /// 本機 <c>PhysicalDrive 2／4</c> 的 NVMe 查詢會卡在核心不可中斷等待裡（headless <c>--json</c>
    /// 因此跑了 35 分鐘沒有輸出、行程還殺不掉）。修好之後，任何一顆碟都必須在
    /// 外層預算內<b>交還控制權</b>——不是「讀到」，而是「讀不到也回得來」。
    /// </summary>
    /// <remarks>
    /// 用 <c>Task.Run ＋ WaitAsync</c> 包一層而不是直接呼叫：沒有外層上限的實作會讓這條測試自己卡死，
    /// 那樣失敗訊息就只是「測試沒結束」；包起來才會變成一條會說話的紅燈。
    /// 用 <c>WaitAsync</c> 而不是阻塞的 <c>Wait</c>：後者會被 xUnit1031 擋（分析器說得對——
    /// 阻塞等待在測試執行緒上等一個不回來的呼叫，正是這一組要防的形狀）。
    /// 存在且健康的機器上（磁碟不存在、毫秒級回 null）照樣綠——它守的是上限，不是速度。
    /// </remarks>
    [Fact]
    public async System.Threading.Tasks.Task 真的卡住的碟也要在外層預算內交還控制權()
    {
        StorageProbeBreaker.Reset();
        try
        {
            for (int index = 0; index <= 7; index++)
            {
                int i = index;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool finished = true;
                try
                {
                    await System.Threading.Tasks.Task.Run(() => StorageSmartService.TryReadNvmeHealth(i))
                        .WaitAsync(TimeSpan.FromMilliseconds(6000 + 3000));
                }
                catch (TimeoutException)
                {
                    finished = false;
                }
                sw.Stop();
                Assert.True(finished,
                    $"PhysicalDrive {i} 的 NVMe 健康紀錄查詢超過外層預算仍未交還控制權" +
                    "——那就不是「這顆碟讀不到」，而是整條收集鏈會被它卡死");
                Assert.True(sw.ElapsedMilliseconds < 9000, $"PhysicalDrive {i} 花了 {sw.ElapsedMilliseconds} ms");
            }
        }
        finally { StorageProbeBreaker.Reset(); }
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
