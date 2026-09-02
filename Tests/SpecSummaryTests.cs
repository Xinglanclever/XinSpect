using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「複製規格摘要」的文字。
///
/// 這是給一般使用者用的功能：貼到論壇或聊天室問人「我的電腦跑不動這個，該換什麼」。
/// 所以守兩件事——<b>不得夾帶任何可識別這台機器的東西</b>（電腦名稱、使用者名稱、序號、UUID），
/// 以及<b>讀不到的欄位要寫「—」而不是省略</b>：省略會讓看的人以為那一項不存在，
/// 而「沒有獨立顯示卡」與「讀不到顯示卡」是兩件不同的事。
/// </summary>
public class SpecSummaryTests
{
    private static SpecFacts Full() => new()
    {
        Os = "Windows 11 專業版", OsVersion = "26100.1234",
        Cpu = "Intel Core i9-7980XE", Cores = 18, Threads = 36, CpuMaxMHz = 4200,
        Board = "ASUS PRIME X299-A", Bios = "1401",
        RamGB = 64, RamDetail = "DDR4-3200 ・ 4 條",
        Gpu = "NVIDIA GeForce RTX 3080",
        SystemDisk = "Samsung SSD 980 PRO 1TB",
        Display = "1920 × 1080 ・ 60 Hz",
    };

    [Fact]
    public void 完整資料時每一行都有內容()
    {
        string s = SpecSummary.Build(Full());

        Assert.Contains("i9-7980XE", s);
        Assert.Contains("18 核 36 執行緒", s);
        Assert.Contains("4.2 GHz", s);
        Assert.Contains("64 GB", s);
        Assert.Contains("DDR4-3200", s);
        Assert.Contains("RTX 3080", s);
        Assert.Contains("X299-A", s);
        Assert.Contains("BIOS 1401", s);
        Assert.Contains("980 PRO", s);
    }

    [Fact]
    public void 貼得進聊天室_行數不超過十行()
    {
        int lines = SpecSummary.Build(Full()).Split('\n').Length;
        Assert.InRange(lines, 5, 10);
    }

    [Fact]
    public void 讀不到的欄位寫破折號而不是省略或零()
    {
        string s = SpecSummary.Build(new SpecFacts());

        // 每一行都還在，只是值是「—」
        Assert.Contains("作業系統：—", s);
        Assert.Contains("處理器：—", s);
        Assert.Contains("記憶體：—", s);
        Assert.Contains("顯示卡：—", s);
        // 不得出現 0 GB／0 核 這種假數字
        Assert.DoesNotContain("0 GB", s);
        Assert.DoesNotContain("0 核", s);
        Assert.DoesNotContain("0 GHz", s);
    }

    [Fact]
    public void 沒有補述時不留一對空括號()
    {
        string s = SpecSummary.Build(Full() with { OsVersion = "", Bios = "", RamDetail = "" });
        Assert.DoesNotContain("（）", s);
    }

    [Fact]
    public void 不得夾帶可識別這台機器的欄位()
    {
        // 型別上就不該有這些欄位——加進來會被一起貼到論壇上
        var names = typeof(SpecFacts).GetProperties().Select(p => p.Name).ToList();
        foreach (string forbidden in new[] { "HostName", "UserName", "Serial", "Uuid", "ProcessorId", "MacAddress" })
            Assert.DoesNotContain(forbidden, names);
    }

    [Fact]
    public void 摘要開頭要標明來源_貼出去時看得出是哪個工具產生的()
    {
        Assert.StartsWith("【曦覽 XinSpect 規格摘要】", SpecSummary.Build(Full()));
    }
}
