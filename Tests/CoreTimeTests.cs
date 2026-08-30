using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 逐核時間歸因的解碼測試。刻意涵蓋兩個最容易出錯的語意：
/// Kernel 已含 Idle（不扣就會把閒置當忙碌），以及 DPC／中斷是 Kernel 的子集（相加即重複計算）。
/// </summary>
public class CoreTimeTests
{
    // 一秒＝10,000,000 個 100 奈秒刻
    private const long Sec = 10_000_000L;

    [Fact]
    public void 求差_核心模式必須扣掉閒置否則閒置會被算成忙碌()
    {
        // 一秒內：閒置 0.8 秒、核心 0.9 秒（含那 0.8 秒閒置）、使用者 0.1 秒
        var a = new CoreTimeSample(0, 0, 0, 0, 0, 0);
        var b = new CoreTimeSample(8 * Sec / 10, 9 * Sec / 10, Sec / 10, 0, 0, 0);

        var r = CoreTimeDecoder.Diff(0, a, b, 1.0);
        Assert.NotNull(r);
        Assert.Equal(80.0, r!.IdlePercent, 3);
        Assert.Equal(10.0, r.KernelPercent, 3);   // 0.9 − 0.8 ＝ 0.1 秒
        Assert.Equal(10.0, r.UserPercent, 3);
        Assert.Equal(20.0, r.BusyPercent, 3);
    }

    [Fact]
    public void 求差_全閒置時忙碌為零()
    {
        var a = new CoreTimeSample(0, 0, 0, 0, 0, 0);
        var b = new CoreTimeSample(Sec, Sec, 0, 0, 0, 0);
        var r = CoreTimeDecoder.Diff(3, a, b, 1.0);
        Assert.NotNull(r);
        Assert.Equal(100.0, r!.IdlePercent, 3);
        Assert.Equal(0.0, r.BusyPercent, 3);
        Assert.Equal(0.0, r.KernelPercent, 3);
        Assert.Equal("CPU 3", r.Name);
    }

    [Fact]
    public void 求差_DPC與中斷是核心模式的子集不與其相加()
    {
        // 核心 0.5 秒（含閒置 0.3），其中 DPC 0.05、中斷 0.02
        var a = new CoreTimeSample(0, 0, 0, 0, 0, 0);
        var b = new CoreTimeSample(3 * Sec / 10, 5 * Sec / 10, 5 * Sec / 10, Sec / 20, Sec / 50, 0);
        var r = CoreTimeDecoder.Diff(0, a, b, 1.0);
        Assert.NotNull(r);
        // 閒置 30 ＋ 核心淨 20 ＋ 使用者 50 ＝ 100
        Assert.Equal(100.0, r!.IdlePercent + r.KernelPercent + r.UserPercent, 3);
        // DPC／中斷另計，本身不佔用上面那 100
        Assert.Equal(5.0, r.DpcPercent, 3);
        Assert.Equal(2.0, r.InterruptPercent, 3);
    }

    [Fact]
    public void 求差_分母為零時回空值而不是報零百分比()
    {
        var a = new CoreTimeSample(Sec, Sec, 0, 0, 0, 0);
        Assert.Null(CoreTimeDecoder.Diff(0, a, a, 1.0));
    }

    [Fact]
    public void 求差_計數器回退時回空值而不是負百分比()
    {
        var a = new CoreTimeSample(5 * Sec, 6 * Sec, 2 * Sec, 0, 0, 0);
        var b = new CoreTimeSample(4 * Sec, 7 * Sec, 3 * Sec, 0, 0, 0);   // Idle 變小
        Assert.Null(CoreTimeDecoder.Diff(0, a, b, 1.0));
    }

    [Fact]
    public void 中斷次數_以實測秒數換算而不是假設剛好一秒()
    {
        var a = new CoreTimeSample(0, 0, 0, 0, 0, 1000);
        var b = new CoreTimeSample(0, Sec, 0, 0, 0, 4000);
        var r = CoreTimeDecoder.Diff(0, a, b, 1.5);
        Assert.NotNull(r);
        Assert.Equal(2000.0, r!.InterruptsPerSecond, 3);   // 3000 次 ÷ 1.5 秒
    }

    [Fact]
    public void 中斷次數_三十二位元回捲時回零而不是天文數字()
    {
        var a = new CoreTimeSample(0, 0, 0, 0, 0, uint.MaxValue - 10);
        var b = new CoreTimeSample(0, Sec, 0, 0, 0, 40);
        var r = CoreTimeDecoder.Diff(0, a, b, 1.0);
        Assert.NotNull(r);
        Assert.Equal(0.0, r!.InterruptsPerSecond);
    }

    [Fact]
    public void 嚴重度_依DPC與中斷合計分三級()
    {
        Assert.Equal(0, Row(dpc: 0.5, isr: 0.5).Severity);
        Assert.Equal(1, Row(dpc: 2.0, isr: 1.5).Severity);
        Assert.Equal(2, Row(dpc: 8.0, isr: 3.0).Severity);
    }

    [Fact]
    public void 彙總_點名最忙與DPC最高的那顆而不是只報平均()
    {
        var rows = new[]
        {
            Row(idle: 90, dpc: 0.1, isr: 0.1, name: "CPU 0"),
            Row(idle: 10, dpc: 0.2, isr: 0.2, name: "CPU 1"),
            Row(idle: 80, dpc: 6.0, isr: 0.3, name: "CPU 2"),
        };
        string s = CoreTimeDecoder.Summarize(rows);
        Assert.Contains("最忙 CPU 1", s);
        Assert.Contains("DPC 最高 CPU 2", s);
        Assert.Contains("3 顆", s);
    }

    [Fact]
    public void 彙總_單顆中斷負擔過重時導向DPC延遲卡片()
    {
        var rows = new[] { Row(idle: 50, dpc: 9.0, isr: 2.0) };
        string s = CoreTimeDecoder.Summarize(rows);
        Assert.Contains("⚠", s);
        Assert.Contains("DPC／ISR 延遲排行", s);
    }

    [Fact]
    public void 彙總_沒有有效列時照實說而不是回零()
    {
        Assert.Contains("沒有任何有效", CoreTimeDecoder.Summarize([]));
    }

    [Fact]
    public void 閒置週期_明說單位是TSC週期不能與百分比換算()
    {
        ulong[] a = [0, 0];
        ulong[] b = [3_000_000_000, 1_000_000_000];
        string s = CoreTimeDecoder.DescribeIdleCycles(a, b, 1.0);
        Assert.Contains("TSC 週期", s);
        Assert.Contains("不能與上表的百分比互相換算", s);
        Assert.Contains("2,000.0 百萬週期", s);   // 平均 (3e9+1e9)/2
    }

    [Fact]
    public void 閒置週期_處理器數不一致或讀不到時不做推論()
    {
        Assert.StartsWith("—", CoreTimeDecoder.DescribeIdleCycles(null, [1], 1.0));
        Assert.StartsWith("—", CoreTimeDecoder.DescribeIdleCycles([1], null, 1.0));
        Assert.StartsWith("—", CoreTimeDecoder.DescribeIdleCycles([1, 2], [1], 1.0));
        Assert.StartsWith("—", CoreTimeDecoder.DescribeIdleCycles([1], [2], 0));
    }

    [Fact]
    public void 閒置週期_部分回退時排除並說明筆數()
    {
        ulong[] a = [100, 500];
        ulong[] b = [1_000_000_100, 200];
        string s = CoreTimeDecoder.DescribeIdleCycles(a, b, 1.0);
        Assert.Contains("1 顆的計數器回退，已排除", s);
    }

    [Fact]
    public void 閒置週期_全部回退時不做推論()
    {
        Assert.Contains("不做推論", CoreTimeDecoder.DescribeIdleCycles([500, 500], [1, 2], 1.0));
    }

    [Fact]
    public void 讀法說明_必須明講五個數字不能相加()
    {
        Assert.Contains("不會是 100%", CoreTimeDecoder.ReadingNotice);
        Assert.Contains("子集", CoreTimeDecoder.ReadingNotice);
    }

    [Fact]
    public void 精度說明_必須解釋離散跳動來自時鐘刻而不是四捨五入()
    {
        Assert.Contains("時鐘刻", CoreTimeDecoder.ResolutionNotice);
        Assert.Contains("不是這裡在四捨五入", CoreTimeDecoder.ResolutionNotice);
    }

    [Fact]
    public void 群組說明_數量一致時為空字串()
    {
        Assert.Equal("", CoreTimeDecoder.GroupNotice(36, 36));
        Assert.Equal("", CoreTimeDecoder.GroupNotice(40, 36));
    }

    [Fact]
    public void 群組說明_列不滿時明說是單一群組查詢的限制()
    {
        string s = CoreTimeDecoder.GroupNotice(64, 128);
        Assert.Contains("只列到 64 顆", s);
        Assert.Contains("逐群組查詢", s);
        Assert.Contains("本機無法驗證", s);
    }

    private static CoreTimeRow Row(double idle = 50, double dpc = 0, double isr = 0, string name = "CPU 0")
        => new()
        {
            Name = name,
            IdlePercent = idle,
            UserPercent = 0,
            KernelPercent = 100 - idle,
            DpcPercent = dpc,
            InterruptPercent = isr,
            InterruptsPerSecond = 0,
        };
}
