using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// xsc（XinSpect 終端機）的守門：獨立專案要留在版控、主專案的 glob 不得把它吃回去。
/// </summary>
/// <remarks>
/// 2026-11-04 的實際事故：XinSpectCli/ 是主專案目錄的子目錄，主 csproj 的預設
/// `**/*.cs` glob 把 Program.cs 編進主程式，兩個 Main 直接 CS0017。
/// 修法與 Bridge/ 相同——主 csproj 明確 Remove；這裡守住那個 Remove 不被拿掉。
/// </remarks>
public class XinSpectCliTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 主專案必須排除XinSpectCli的原始碼()
    {
        string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpect.csproj"));
        Assert.Contains("Compile Remove=\"XinSpectCli\\**\\*.cs\"", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI專案與進入點存在()
    {
        string root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "XinSpectCli.csproj")), "缺 XinSpectCli.csproj");
        string src = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Program.cs"));
        // Claude Code 式體驗的最低門檻：互動迴圈＋歷史＋補全＋唯讀承諾
        Assert.Contains("ReadLineWithHistory", src, StringComparison.Ordinal);
        Assert.Contains("ConsoleKey.Tab", src, StringComparison.Ordinal);
        Assert.Contains("ConsoleKey.UpArrow", src, StringComparison.Ordinal);
        Assert.Contains("唯讀", src, StringComparison.Ordinal);
        // Claude Code 式介面（2026-11-05 裁決）：圓角歡迎框、星芒 spinner、> 提示符
        string tui = File.ReadAllText(Path.Combine(root, "XinSpectCli", "Tui.cs"));
        // 吉祥物 ☼（U+263C 白日光芒＝「曦」）取代 Claude 的 ✻；破曉序列 spinner；> 提示符
        Assert.Contains("╭", tui, StringComparison.Ordinal);
        Assert.Contains("☼", tui, StringComparison.Ordinal);
        Assert.Contains("走訪 1,178 把靜態事實鍵中", tui, StringComparison.Ordinal);
        Assert.Contains("∙", tui, StringComparison.Ordinal);   // 破曉序列起始幀
        // ✻ 僅允許出現在寬度註解（VisibleLength 的字元區間註記），介面字串一律用 ☼
        // ✻ 不得出現在歡迎框本體（VisibleLength 的字元區間註解除外）
        int wStart = tui.IndexOf("public static void Welcome", StringComparison.Ordinal);
        int wEnd = tui.IndexOf("// ── 2)", StringComparison.Ordinal);
        Assert.True(wStart >= 0 && wEnd > wStart, "找不到 Welcome 方法區段");
        Assert.DoesNotContain("✻", tui[wStart..wEnd], StringComparison.Ordinal);
        Assert.DoesNotContain("xs▸", tui, StringComparison.Ordinal);
        // 指令書與百科：help 分類功能表＋單指令詳解；book 離線嵌入整本百科
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "CommandBook.cs")), "缺 CommandBook.cs");
        Assert.Contains("class CommandBook", File.ReadAllText(Path.Combine(root, "XinSpectCli", "CommandBook.cs")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "XinSpectCli", "Book.cs")), "缺 Book.cs");
        string csproj = File.ReadAllText(Path.Combine(root, "XinSpectCli", "XinSpectCli.csproj"));
        Assert.Contains("README.md", csproj, StringComparison.Ordinal);
        // 不得引入寫入通路
        Assert.DoesNotContain("WritePciConfig", src, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMsrPair", src, StringComparison.Ordinal);
    }

    [Fact]
    public void CLI要覆蓋常規版的功能面_GUI全頁對照()
    {
        // 使用者裁決：CLI 版要有常規（GUI）版的所有功能。載體是 pages／page／domain／
        // keys／settings／changelog——GUI 的 65 頁底下都是無頭服務，終端以事實查詢呈現同一批資料。
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpectCli", "Program.cs"));
        foreach (string fn in new[] { "ListPages", "PageFacts", "DomainFacts", "ListKeys", "ShowSettings", "ShowChangelog" })
            Assert.Contains(fn, src, StringComparison.Ordinal);
        // GUI 頁面清單就是對照表來源
        Assert.Contains("PageRegistry.Pages", src, StringComparison.Ordinal);
        Assert.Contains("PageRegistry.Utilities", src, StringComparison.Ordinal);
    }
}
