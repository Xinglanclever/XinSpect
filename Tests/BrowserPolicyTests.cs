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

    /// <summary>
    /// 起始頁是「我們自己產生的一份 HTML」，WebView2 送出的導覽是
    /// <c>data:text/html;charset=utf-8;base64,…</c>——不是 about:blank。
    /// 2026-10-26 的正式版就是因為把它當成頁面裡的 data: 連結擋掉，整頁首頁變空白。
    /// 所以：只有在呼叫端明確標記「這次是我自己送的」時才放行，而且只放行 HTML。
    /// </summary>
    [Fact]
    public void 自己送出的起始頁文件可以放行_頁面裡的data連結不行()
    {
        var home = new Uri("data:text/html;charset=utf-8;base64,PGgxPkhPPC9oMT4=");

        Assert.False(BrowserPolicy.IsNavigationAllowed(home));                 // 頁面裡的 data: 照樣擋
        Assert.True(BrowserPolicy.IsNavigationAllowed(home, allowSelfDocument: true));
        Assert.True(BrowserPolicy.IsSelfDocument(home));

        var image = new Uri("data:image/png;base64,iVBORw0KGgo=");
        Assert.False(BrowserPolicy.IsSelfDocument(image));
        Assert.False(BrowserPolicy.IsNavigationAllowed(image, allowSelfDocument: true));   // 只認 HTML

        Assert.False(BrowserPolicy.IsSelfDocument(new Uri("about:blank")));
        Assert.False(BrowserPolicy.IsSelfDocument(new Uri("https://example.com/")));
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

    // ── 開發者工具與縮放 ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    [InlineData(" yes ", true)]
    [InlineData("on", true)]
    public void 開發者工具只在明確要求時開啟(string? env, bool expected)
    {
        // 這個行程是管理員權杖；DevTools 不是「一個好用的面板」，是能對頁面執行任意腳本的介面。
        Assert.Equal(expected, BrowserPolicy.DevToolsAllowed(env));
    }

    /// <summary>
    /// 原始碼守門：DevTools 只能透過 <c>DevToolsAllowed(...)</c> 決定。
    /// 直接寫 <c>= true</c> 就等於把上面那條政策繞過去，而且行為測試看不到（要真的開窗才看得出來）。
    /// </summary>
    [Fact]
    public void 開發者工具不得在原始碼裡被無條件打開()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "BrowserView.xaml.cs"));
        // 先把所有空白壓成單一空格再比對：指派跨了兩行，直接用正則會被回溯繞過。
        string flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        Assert.Contains("AreDevToolsEnabled = BrowserPolicy.DevToolsAllowed", flat, StringComparison.Ordinal);
        Assert.DoesNotContain("AreDevToolsEnabled = true", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void 縮放級距精準且夾在範圍內()
    {
        Assert.Equal(1.0, BrowserPolicy.NextZoom(1.0, 0));
        Assert.Equal(1.1, BrowserPolicy.NextZoom(1.0, +1), 6);
        Assert.Equal(0.9, BrowserPolicy.NextZoom(1.0, -1), 6);

        // 到頂就停住，不會因為連按而無限放大
        Assert.Equal(BrowserPolicy.MaxZoom, BrowserPolicy.NextZoom(BrowserPolicy.MaxZoom, +1));
        Assert.Equal(BrowserPolicy.MinZoom, BrowserPolicy.NextZoom(BrowserPolicy.MinZoom, -1));

        // 不合理的輸入回到 100%，不要讓整個頁面變成 0 倍或 NaN
        Assert.Equal(1.0, BrowserPolicy.ClampZoom(double.NaN));
        Assert.Equal(1.0, BrowserPolicy.ClampZoom(double.PositiveInfinity));
        Assert.Equal(BrowserPolicy.MinZoom, BrowserPolicy.ClampZoom(-5));

        // 連按十次放大再十次縮小必須精準回到 100%（浮點累加會差一點，這裡用格數算）
        double z = 1.0;
        for (int i = 0; i < 10; i++) z = BrowserPolicy.NextZoom(z, +1);
        Assert.Equal(BrowserPolicy.MaxZoom, z, 10);
        for (int i = 0; i < 10; i++) z = BrowserPolicy.NextZoom(z, -1);
        Assert.Equal(1.0, z, 10);
    }

    // ── 自訂首頁 ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 沒填首頁就回內建頁(string? configured)
        => Assert.Null(BrowserPolicy.ResolveHomeUrl(configured));

    [Theory]
    [InlineData("ptt.cc/bbs/index.html", "https://ptt.cc/bbs/index.html")]      // 沒打協定就補 https
    [InlineData("http://example.com/a?b=1", "http://example.com/a?b=1")]        // http 照用
    [InlineData("  https://www.chiphell.com/  ", "https://www.chiphell.com/")]  // 頭尾空白吃掉
    public void 網址首頁會被正規化(string configured, string expected)
        => Assert.Equal(expected, BrowserPolicy.ResolveHomeUrl(configured)?.ToString());

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("about:blank")]
    [InlineData("magnet:?xt=urn:btih:abc")]
    public void 不是網頁的首頁一律視為沒設定(string configured)
    {
        // 不合法＝回到內建頁，而不是留下一個每次按首頁都失敗的設定。
        Assert.Null(BrowserPolicy.ResolveHomeUrl(configured));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
