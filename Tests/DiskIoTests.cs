using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 磁碟 I/O 看門狗。
/// </summary>
/// <remarks>
/// 這組測試存在的理由是一次實測事故：2026-09-04 有一支列舉 <c>PhysicalDrive 0–7</c> 的程式
/// 卡在某顆碟的 IOCTL 裡沒有回來，那個程序連提權後的 <c>taskkill /F</c> 都殺不掉
/// （卡在核心模式的不可中斷 I/O），並一路握著測試組件的檔案鎖。
/// 同一組同步 IOCTL 就是儲存頁與機器年齡在用的——所以那是產品級風險，不是測試意外。
/// <para>
/// 本組測試**完全不碰真實磁碟**：用刻意睡很久的假讀取來驗逾時行為，
/// 這樣驗證看門狗的過程本身不可能再製造同一個問題。
/// </para>
/// </remarks>
public class DiskIoTests
{
    [Fact]
    public void 正常完成時原樣回傳()
        => Assert.Equal("ok", DiskIo.Guarded(() => "ok", 1000));

    [Fact]
    public void 逾時就放棄_並且在時限內返回()
    {
        var sw = Stopwatch.StartNew();
        var result = DiskIo.Guarded<string>(() => { Thread.Sleep(5000); return "太慢了"; }, 200);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"逾時 200 ms 的讀取必須在時限內返回，實際等了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void 讀取拋例外時回null_不得往外炸()
        => Assert.Null(DiskIo.Guarded<string>(() => throw new IOException("裝置沒回應"), 1000));

    [Fact]
    public void 逾時後那條工作自己完成也不得造成未觀察例外()
    {
        DiskIo.Guarded<string>(() => { Thread.Sleep(300); throw new IOException("遲到又失敗"); }, 50);

        Thread.Sleep(600);               // 讓那條遲到的工作跑完並拋出例外
        GC.Collect();
        GC.WaitForPendingFinalizers();   // 未觀察例外若沒被吃掉，會在這裡把行程帶走
        GC.Collect();
    }

    [Fact]
    public void 回null的讀取本身也視為讀不到()
        => Assert.Null(DiskIo.Guarded<byte[]>(() => null, 1000));
}
