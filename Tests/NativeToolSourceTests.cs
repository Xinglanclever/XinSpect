using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// OS 內建工具接縫（<see cref="INativeToolSource"/>／<see cref="NativeToolSection"/>）。
///
/// 這一組測的是<b>契約</b>而不是輸出內容：宣告了哪幾段、命令怎麼寫、說明有沒有寫、
/// 以及「可用／不可用」兩態說不說得出話。**刻意不開任何行程**——
/// 這條接縫的整個用途就是讓上層能注入假來源，測試若自己去跑 powercfg 就把這條路堵死了。
/// </summary>
public class NativeToolSourceTests
{
    /// <summary>假的接縫實作者：證明這個介面可以被注入，而不必牽扯真工具。</summary>
    private sealed class FakeNativeTool : INativeToolSource
    {
        public bool Available { get; init; } = true;
        public string? UnavailableReason { get; init; }
        public IReadOnlyList<NativeToolSection> Sections { get; init; } = [];
        public IReadOnlyList<NativeToolSection> Run() => Sections;
    }

    [Fact]
    public void 睡眠診斷宣告五段查詢且每一段的四欄都不得是空的()
    {
        var sections = SleepDiagnosticsService.Describe();

        Assert.Equal(5, sections.Count);
        Assert.All(sections, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title), "標題不得為空");
            Assert.False(string.IsNullOrWhiteSpace(s.Command), "命令不得為空");
            Assert.False(string.IsNullOrWhiteSpace(s.What), "說明不得為空——沒有說明的原始輸出等於沒查");
        });
    }

    [Fact]
    public void 睡眠診斷的每一段命令都必須是powercfg且照抄得出參數()
    {
        var commands = SleepDiagnosticsService.Describe().Select(s => s.Command).ToArray();

        Assert.All(commands, c => Assert.StartsWith("powercfg ", c, StringComparison.Ordinal));
        Assert.Contains("powercfg /a", commands);
        Assert.Contains("powercfg /lastwake", commands);
        Assert.Contains("powercfg /waketimers", commands);
        Assert.Contains("powercfg /requests", commands);
        Assert.Contains("powercfg /devicequery wake_armed", commands);
    }

    [Fact]
    public void 宣告不等於執行宣告出來的區段不得帶著輸出()
    {
        // 這一條釘住「宣告與執行分離」：Describe() 是給測試看的，
        // 它一旦偷偷去跑行程，測試就會變成在測這台機器而不是在測程式。
        Assert.All(SleepDiagnosticsService.Describe(),
            s => Assert.Equal("", s.Output));
    }

    [Fact]
    public void 單段描述的命令欄由參數組合而成()
    {
        var one = SleepDiagnosticsService.Describe("測試段", ["/foo", "bar"], "說明");

        Assert.Equal("powercfg /foo bar", one.Command);
        Assert.Equal("測試段", one.Title);
        Assert.Equal("說明", one.What);
        Assert.Equal("", one.Output);
    }

    [Fact]
    public void 真來源的可用與不可用必須一致可用就沒有原因不可用就必須說出原因()
    {
        var svc = new SleepDiagnosticsService();

        if (svc.Available)
        {
            Assert.Null(svc.UnavailableReason);
        }
        else
        {
            // 不可用時最忌諱靜靜地空著：畫面上的「（沒有輸出）」會被誤讀成
            // 「沒有東西阻止睡眠」——那是結論，不是狀態。
            Assert.False(string.IsNullOrWhiteSpace(svc.UnavailableReason));
            Assert.Contains("powercfg", svc.UnavailableReason);
        }
    }

    [Fact]
    public void powercfg路徑只查檔案在不在不猜()
    {
        string? path = SleepDiagnosticsService.PowercfgPath;

        if (path is not null)
        {
            Assert.True(File.Exists(path));
            Assert.Equal("powercfg.exe", Path.GetFileName(path), ignoreCase: true);
        }

        // 有路徑 ⇔ 可用，兩者必須同進同出（同一台機器上兩次查詢結果可能變動，
        // 所以這裡只驗一致性，不把當下的值寫死）。
        var svc = new SleepDiagnosticsService();
        Assert.Equal(path is not null, svc.Available);
    }

    [Fact]
    public void 假來源可注入接縫本身不必依賴任何原生工具()
    {
        var section = new NativeToolSection
        {
            Title = "假的一段",
            Command = "fake.exe --check",
            What = "示範用",
            Output = "OK",
        };

        INativeToolSource source = new FakeNativeTool { Sections = [section] };

        Assert.True(source.Available);
        Assert.Null(source.UnavailableReason);
        Assert.Same(section, Assert.Single(source.Run()));
    }

    [Fact]
    public void 不可用的假來源要說得出原因而不是空著()
    {
        INativeToolSource source = new FakeNativeTool
        {
            Available = false,
            UnavailableReason = "這台機器沒有這個工具",
        };

        Assert.False(source.Available);
        Assert.False(string.IsNullOrWhiteSpace(source.UnavailableReason));
        Assert.Empty(source.Run());
    }
}
