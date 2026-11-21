using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 百科／手冊的自足 HTML：它們是「可翻閱定位」的載體，但本身不是新內容——
/// 只是 <c>README.md</c>／<c>手冊.md</c> 的另一種呈現。Desktop 不再內嵌百科；使用者明確點擊後
/// 才下載 Release 資產，以 SHA256SUMS 驗證，再快取到本機供離線閱讀。CLI 自包含百科由 CLI 專案測試。
///
/// <para>
/// 生成同步由產生器守門；本類另外驗證下載清單解析、HTML 雜湊和內容結構，並確認 Desktop 不再
/// 將百科打包為資源。
/// </para>
/// </summary>
public class DocsHtmlTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (int ExitCode, string Output) RunCheck()
    {
        string script = Path.Combine(RepoRoot(), "Tools", "gen-docs-html.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = '"' + script + "\" --check",
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(180_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("文件 HTML 產生器 180 秒內沒有結束");
        }
        return (p.ExitCode, output);
    }

    /// <summary>
    /// 產生器把來源檔案的內容（換行統一成 LF、去 BOM）雜湊後寫進 HTML 檔頭。
    /// 這裡必須用同一個算法：直接雜湊位元組會在 Windows（CRLF）與 CI（LF）上算出不同值，
    /// 讓同一份內容在兩邊一次紅一次綠。
    /// </summary>
    private static string Sha12OfRepoFile(string name)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), name), Encoding.UTF8)
            .Replace("\r\n", "\n").Replace("\r", "\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant()[..12];
    }

    private static string ReadRepoText(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), name), Encoding.UTF8);

    [Fact]
    public void 百科與手冊的HTML跟來源md同步_產生器check要回零()
    {
        var (code, output) = RunCheck();
        Assert.True(code == 0,
            "百科.html／手冊.html 與來源 .md 不同步（或產生器本身有 bug）。\n"
            + "修正方式：python Tools/gen-docs-html.py\n\n" + output);
        Assert.Contains("OK", output);
    }

    [Fact]
    public void 百科html記的來源sha256要等於目前READMEmd()
    {
        string html = ReadRepoText("百科.html");
        // 檔頭：<!-- 由 docs/百科-v3.17.md 產生（sha256 xxxxxxxxxxxx）；… -->
        Assert.Contains("（sha256 " + Sha12OfRepoFile(Path.Combine("docs", "百科-v3.17.md")) + "）", html);
    }

    [Fact]
    public void 手冊html記的來源sha256要等於目前手冊md()
    {
        string html = ReadRepoText("手冊.html");
        Assert.Contains("（sha256 " + Sha12OfRepoFile("手冊.md") + "）", html);
    }

    [Fact]
    public void 桌面版不再內嵌百科而是使用獨立Release資產()
    {
        string project = File.ReadAllText(Path.Combine(RepoRoot(), "XinSpect.csproj"));
        var assembly = typeof(EncyclopediaCatalog).Assembly;
        Assert.DoesNotContain("XinSpect.Encyclopedia.html", project, StringComparison.Ordinal);
        Assert.DoesNotContain("XinSpect.Encyclopedia.html", assembly.GetManifestResourceNames());
        Assert.Equal("XinSpect-Encyclopedia.html", EncyclopediaCatalog.AssetFileName);
        Assert.Equal("百科.html", Path.GetFileName(Path.Combine(EncyclopediaCatalog.CacheDirectory,
            EncyclopediaCatalog.FileName)));
    }

    [Fact]
    public void Release雜湊清單只接受唯一格式正確的百科項目()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Equal(hash, EncyclopediaCatalog.ParseExpectedHash("# release" + Environment.NewLine + hash + "  XinSpect-Encyclopedia.html" + Environment.NewLine));
        Assert.Throws<InvalidDataException>(() => EncyclopediaCatalog.ParseExpectedHash(hash + "  other.html" + Environment.NewLine));
        Assert.Throws<InvalidDataException>(() => EncyclopediaCatalog.ParseExpectedHash(
            hash + "  XinSpect-Encyclopedia.html" + Environment.NewLine + hash + "  XinSpect-Encyclopedia.html" + Environment.NewLine));
        Assert.Throws<InvalidDataException>(() => EncyclopediaCatalog.ParseExpectedHash("not-a-hash  XinSpect-Encyclopedia.html" + Environment.NewLine));
    }

    [Fact]
    public void 下載HTML必須通過雜湊UTF8與文件結構校驗()
    {
        byte[] valid = Encoding.UTF8.GetBytes("<!doctype html><html><head></head><body><nav id=\"toc\"></nav></body></html>");
        string hash = Convert.ToHexString(SHA256.HashData(valid)).ToLowerInvariant();
        Assert.Contains("id=\"toc\"", EncyclopediaCatalog.ValidateHtml(valid, hash), StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => EncyclopediaCatalog.ValidateHtml(valid, new string('0', 64)));
        byte[] script = Encoding.UTF8.GetBytes("<html><head></head><body><nav id=\"toc\"></nav><script>x()</script></body></html>");
        string scriptHash = Convert.ToHexString(SHA256.HashData(script)).ToLowerInvariant();
        Assert.Throws<InvalidDataException>(() => EncyclopediaCatalog.ValidateHtml(script, scriptHash));
        Assert.Throws<InvalidDataException>(() => EncyclopediaCatalog.ValidateHtml(new byte[] { 0xff }, new string('f', 64)));
    }

    [Fact]
    public void 百科HTML要有目錄與每一級標題的錨點()
    {
        string html = ReadRepoText("百科.html");
        Assert.Contains("id=\"toc\"", html);
        // README 的 h2 數量（部／章）與 h3（節）都應該落成有 id 的標題
        Assert.True(html.Count(c => c == '<') > 0);
        int h2 = CountOccurrences(html, "<h2 id=\"");
        Assert.True(h2 >= 10, "百科 HTML 的 <h2 id=…> 只有 " + h2 + " 個，目錄可能壞了");
        int anchors = CountOccurrences(html, "<a class=\"self\"");
        Assert.True(anchors >= 300, "標題錨點只有 " + anchors + " 個，定位功能不完整");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
