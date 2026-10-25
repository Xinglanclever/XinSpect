using System.IO;
using System.Windows;
using Xunit;

namespace XinSpect.Tests;

[Collection(WpfCollection.Name)]
public sealed class ReportCoverAndSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static MainViewModel Vm(bool withFacts = true)
    {
        var vm = new MainViewModel
        {
            System = new SystemSummary
            {
                HostName = "COVER-HOST", UserName = "cover-user",
                OsName = "Windows 11", OsVersion = "10.0.26100", OsArch = "x64",
                SystemManufacturer = "ASUS", SystemModel = "X299",
            },
        };
        if (withFacts)
        {
            // 注入點用 EvidenceLab 公開的一次性收錄入口（與生產路徑同一集合），不新增讀取。
            // 名稱刻意用可辨識的字串：報告的表格顯示「名稱」欄，鍵名不一定進報告。
            vm.EvidenceLab.AddSpiCompareFact(
                new HardwareFact("spi.test.a", "測試", "封面測試事實X299", "v1", "", "測試來源",
                    FactTrustLevel.Measured, false, At));
        }
        return vm;
    }

    [Fact]
    public void 封面節存在_帶工具版號與匿名機器碼()
    {
        string md = ReportService.BuildMarkdownForTests(Vm());

        Assert.Contains("## 封面與執行環境", md, StringComparison.Ordinal);
        Assert.Contains(AppInfo.Version, md, StringComparison.Ordinal);
        Assert.Contains("產生時間", md, StringComparison.Ordinal);
        Assert.Contains("sha256:", md, StringComparison.Ordinal);        // 匿名機器碼＝單向雜湊
        Assert.Contains("Windows 11", md, StringComparison.Ordinal);
        Assert.Contains("x64", md, StringComparison.Ordinal);
    }

    [Fact]
    public void 封面匿名機器碼不含原始識別_遮蔽模式下主機名稱不外洩()
    {
        var vm = Vm();
        vm.Settings.ReportMaskIdentity = true;
        string md = ReportService.BuildMarkdownForTests(vm);

        Assert.DoesNotContain("COVER-HOST", md, StringComparison.Ordinal);
        Assert.DoesNotContain("cover-user", md, StringComparison.Ordinal);
        Assert.Contains("sha256:", md, StringComparison.Ordinal);        // 派生的匿名碼仍在（不含原值）
        Assert.Contains("已啟用", md, StringComparison.Ordinal);          // 遮蔽狀態如實聲明

        // 同一部機器兩份報告的匿名碼相同（跨報告比對的用途成立）
        string md2 = ReportService.BuildMarkdownForTests(Vm());
        string? code1 = LineContaining("sha256:", md);
        string? code2 = LineContaining("sha256:", md2);
        Assert.Equal(code1, code2);
    }

    [Fact]
    public void 系統摘要頁陳述計數_不合成評分()
    {
        var vm = Vm();
        int expected = vm.EvidenceLab.AllFacts.Count;   // 分母＝本輪事實集（含派生彙總事實）
        string md = ReportService.BuildMarkdownForTests(vm);

        Assert.Contains("## 系統摘要", md, StringComparison.Ordinal);
        Assert.Contains("事實總數", md, StringComparison.Ordinal);
        Assert.Contains($"{expected} 筆", md, StringComparison.Ordinal);
        Assert.Contains("封面測試事實X299", md, StringComparison.Ordinal);               // 注入的事實從韌體安全節帶出
        Assert.Contains("逐域計數", md, StringComparison.Ordinal);
        Assert.Contains("不合成任何評分", md, StringComparison.Ordinal);
    }

    [Fact]
    public void 摘要計數跟著本輪事實集走_沒注入的事實不出現()
    {
        string md = ReportService.BuildMarkdownForTests(Vm(withFacts: false));

        Assert.Contains("## 系統摘要", md, StringComparison.Ordinal);
        Assert.Contains("事實總數", md, StringComparison.Ordinal);
        Assert.DoesNotContain("封面測試事實X299", md, StringComparison.Ordinal);
    }

    [Fact]
    public void 追蹤鍵字面落在Services原始碼裡()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Services")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string report = File.ReadAllText(Path.Combine(dir!.FullName, "Services", "ReportService.cs"));

        Assert.Contains("rep.sec.cover", report, StringComparison.Ordinal);
        Assert.Contains("rep.sec.summary", report, StringComparison.Ordinal);
    }

    private static string? LineContaining(string needle, string text) =>
        text.Split('\n').FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal))?.Trim();
}
