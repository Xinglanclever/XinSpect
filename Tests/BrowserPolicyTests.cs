using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 內建瀏覽器的規則：什麼能載入、什麼要擋、下載存哪裡叫什麼名字。
///
/// <para>
/// 這些規則原本散在 <c>BrowserView</c> 的事件處理常式裡，沒有任何測試看得見——
/// 而「file:// 能不能開」這種問題，錯了不會當掉，只會安靜地把本機檔案顯示出來。
/// </para>
/// </summary>
public class BrowserPolicyTests
{
    private static string KindOf(string input) => BrowserPolicy.Resolve(input).Kind.ToString();

    // ── 網址列解析 ────────────────────────────────────────────────────────

    [Fact]
    public void 空白輸入回首頁()
    {
        var r = BrowserPolicy.Resolve("   ");
        Assert.Equal(BrowserInputKind.Home, r.Kind);
        Assert.Null(r.Target);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://localhost:8080/status")]
    public void 完整網址直接開啟(string input)
    {
        var r = BrowserPolicy.Resolve(input);
        Assert.Equal(BrowserInputKind.Url, r.Kind);
        Assert.NotNull(r.Target);
        Assert.Equal(input, r.Target!.AbsoluteUri);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("www.microsoft.com/zh-tw")]
    public void 看起來像網域就補上https(string input)
    {
        var r = BrowserPolicy.Resolve(input);
        Assert.Equal(BrowserInputKind.Url, r.Kind);
        Assert.Equal("https", r.Target!.Scheme);
    }

    [Theory]
    [InlineData("cpu-z 怎麼看記憶體")]
    [InlineData("hello")]
    [InlineData("核心溫度 90 度")]
    public void 不是網址就丟給搜尋(string input)
    {
        var r = BrowserPolicy.Resolve(input);
        Assert.Equal(BrowserInputKind.Search, r.Kind);
        Assert.Equal(input, r.Query);
        Assert.StartsWith("https://www.bing.com/search?q=", BrowserPolicy.SearchBase);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(document.cookie)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("about:config")]
    [InlineData("view-source:https://example.com")]
    [InlineData("ms-settings:about")]
    [InlineData("steam://run/570")]
    public void 不是網頁的協定一律擋下並說出原因(string input)
    {
        var r = BrowserPolicy.Resolve(input);
        Assert.Equal(BrowserInputKind.Blocked, r.Kind);
        Assert.Null(r.Target);
        Assert.False(string.IsNullOrWhiteSpace(r.Reason), "被擋下時一定要說得出原因");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"D:/games/foo.exe")]
    public void 本機磁碟路徑要擋下而且講清楚去哪裡開(string input)
    {
        var r = BrowserPolicy.Resolve(input);
        Assert.Equal(BrowserInputKind.Blocked, r.Kind);
        Assert.Contains("本機檔案", r.Reason);
    }

    [Fact]
    public void 主機加埠號不算自訂協定()
    {
        var r = BrowserPolicy.Resolve("localhost:3000");
        Assert.Equal(BrowserInputKind.Url, r.Kind);
        Assert.Equal("http://localhost:3000/", r.Target!.AbsoluteUri);
    }

    // ── 導覽白名單 ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("about:blank", true)]          // 起始頁以 NavigateToString 載入
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("about:config", false)]
    [InlineData("ftp://example.com", false)]
    public void 只有http與https能導覽(string uri, bool allowed)
    {
        Assert.Equal(allowed, BrowserPolicy.IsNavigationAllowed(new Uri(uri)));
    }

    [Fact]
    public void 空網址不算可導覽()
    {
        Assert.False(BrowserPolicy.IsNavigationAllowed(null));
    }

    [Fact]
    public void 每個被擋的協定都有專屬說明()
    {
        foreach (string scheme in new[] { "javascript", "data", "file", "about", "magnet", "steam" })
        {
            string text = BrowserPolicy.DescribeBlocked(scheme);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.True(text.Contains(scheme, StringComparison.OrdinalIgnoreCase) || text.Contains("自訂協定"),
                $"「{scheme}」的說明沒提到它自己：{text}");
        }
    }

    // ── 下載 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "cmd.exe")]
    [InlineData("report:final?.pdf", "report_final_.pdf")]
    [InlineData("   ", "download")]
    [InlineData("", "download")]
    public void 檔名會去掉路徑與非法字元(string suggested, string expected)
    {
        Assert.Equal(expected, BrowserPolicy.SanitizeFileName(suggested));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.txt")]
    [InlineData("com1.log")]
    public void 保留名稱會被加上前置字元(string suggested)
    {
        string safe = BrowserPolicy.SanitizeFileName(suggested);
        string stem = Path.GetFileNameWithoutExtension(safe);
        Assert.NotEqual("CON", stem, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("NUL", stem, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("com1", stem, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("_", safe);
    }

    [Fact]
    public void 過長檔名會被截短但保留副檔名()
    {
        string name = new string('a', 300) + ".bin";
        string safe = BrowserPolicy.SanitizeFileName(name);
        Assert.True(safe.Length <= 120, $"長度 {safe.Length}");
        Assert.EndsWith(".bin", safe);
    }

    [Fact]
    public void 同名檔案不覆蓋而是加序號()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\dl\a.zip" };
        Assert.Equal(@"C:\dl\a (1).zip", BrowserPolicy.UniquePath(@"C:\dl", "a.zip", existing.Contains));

        existing.Add(@"C:\dl\a (1).zip");
        Assert.Equal(@"C:\dl\a (2).zip", BrowserPolicy.UniquePath(@"C:\dl", "a.zip", existing.Contains));

        Assert.Equal(@"C:\dl\b.zip", BrowserPolicy.UniquePath(@"C:\dl", "b.zip", existing.Contains));
    }

    [Fact]
    public void 下載資料夾在自己的應用程式資料底下()
    {
        string folder = BrowserPolicy.DownloadFolder;
        Assert.Contains("XinSpect", folder);
        Assert.Contains("Downloads", folder);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder);
    }

    [Fact]
    public void 進入點分類的每一種都有值()
    {
        // 枚舉長度變了就要回來想一次「新的那一種要不要擋」。
        foreach (BrowserInputKind k in Enum.GetValues<BrowserInputKind>())
            Assert.True(Enum.IsDefined(k));
    }
}
