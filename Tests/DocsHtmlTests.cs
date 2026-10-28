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
/// 只是 <c>README.md</c>／<c>手冊.md</c> 的另一種呈現。這種東西最容易變成第二個真相：
/// 有人改了 README、忘了重跑產生器，於是 GitHub 上的說法與程式裡翻閱到的說法不同，
/// 而兩邊都沒有任何東西在看。
///
/// <para>
/// 四條線釘住它：①產生器在現行樹上是冪等的（<c>--check</c> 回零）；
/// ②HTML 檔頭記的來源 sha256 ＝ 目前 .md 的 sha256（不靠 python 也能抓漂移）；
/// ③內嵌進 exe 的那份與倉庫檔逐位元組相同；④內嵌資源真的讀得到（不是退回磁碟檔）。
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
        // 檔頭：<!-- 由 README.md 產生（sha256 xxxxxxxxxxxx）；… -->
        Assert.Contains("（sha256 " + Sha12OfRepoFile("README.md") + "）", html);
    }

    [Fact]
    public void 手冊html記的來源sha256要等於目前手冊md()
    {
        string html = ReadRepoText("手冊.html");
        Assert.Contains("（sha256 " + Sha12OfRepoFile("手冊.md") + "）", html);
    }

    [Fact]
    public void 內嵌進程式的百科與倉庫檔案逐位元組相同()
    {
        string path = Path.Combine(RepoRoot(), EncyclopediaCatalog.FileName);
        Assert.True(File.Exists(path), "倉庫根沒有 " + EncyclopediaCatalog.FileName);

        var assembly = typeof(EncyclopediaCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(EncyclopediaCatalog.ResourceName);
        Assert.True(stream is not null,
            "XinSpect.csproj 沒有把 " + EncyclopediaCatalog.FileName + " 內嵌為 "
            + EncyclopediaCatalog.ResourceName + "——單檔發佈版會讀不到百科");

        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        Assert.Equal(File.ReadAllBytes(path), ms.ToArray());
    }

    [Fact]
    public void 執行期讀到的百科是內嵌資源而不是磁碟退路()
    {
        var report = EncyclopediaCatalog.Load();
        Assert.True(report.Available, "百科讀不到：" + report.UnavailableReason);
        Assert.StartsWith("embedded:", report.Source);
        Assert.Contains("<html", report.Text!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"toc\"", report.Text!, StringComparison.Ordinal);
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
