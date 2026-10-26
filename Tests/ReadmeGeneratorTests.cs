using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// README 逐域展開的產生器本身要被驗：它存在、它冪等、它真的會發現漂移。
///
/// <para>
/// 第 279 輪給 6.3／6.5／附錄 F 加了結構守門，但守門只會說「紅了」——不會告訴你
/// 該填什麼，也不會幫你把漏的補上。第 280 輪補 20 把鍵時，那份三萬行的展開是我手動
/// 重生成三次才對的，中間還把 938 列的後設資料蓋掉一次。
/// <b>一個只能靠人記得的重生步驟，遲早會漏。</b>
/// </para>
///
/// <para>
/// 這一支驗 <c>Tools/gen-readme-domains.py</c> 的三個性質：
/// </para>
/// <list type="number">
/// <item><b>在現行 README 上是冪等的</b>——跑一次不該有差異（否則工具本身有 bug，
///   而一個永遠在抱怨的工具等於沒有工具）。</item>
/// <item><b>它真的在看目錄</b>——把目錄的鍵數講給它聽，輸出的域數要對得上。</item>
/// <item><b>它會回非零</b>——這是 <c>--check</c> 能被 CI 用的前提。</item>
/// </list>
///
/// <para>
/// <b>為什麼不直接比對輸出內容：</b>那等於把工具的正確性外包給「上一次的輸出」。
/// 這裡驗的是「工具對現在的樹說什麼」——它說 OK，而結構守門也說 OK，兩邊一致才算過。
/// </para>
/// </summary>
public class ReadmeGeneratorTests
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
        string script = Path.Combine(RepoRoot(), "Tools", "gen-readme-domains.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = '"' + script + "\" --check",
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("產生器 120 秒內沒有結束");
        }
        return (p.ExitCode, output);
    }

    [Fact]
    public void 產生器存在且可執行()
    {
        string script = Path.Combine(RepoRoot(), "Tools", "gen-readme-domains.py");
        Assert.True(File.Exists(script), "Tools/gen-readme-domains.py 不存在——重生步驟又回到「靠人記得」");
        string text = File.ReadAllText(script);
        Assert.Contains("--check", text);
        Assert.Contains("def repo_root", text);
    }

    [Fact]
    public void 在現行README上是冪等的_check要回零()
    {
        var (code, output) = RunCheck();
        Assert.True(code == 0,
            "README 的逐域展開與事實鍵目錄不一致（或產生器本身有 bug）。\n"
            + "修正方式：python Tools/gen-readme-domains.py\n\n" + output);
        Assert.Contains("OK", output);
    }

    [Fact]
    public void check的輸出要寫出它比對了幾個鍵幾個域()
    {
        var (_, output) = RunCheck();
        Assert.Contains(FactKeyCatalog.Keys.Count.ToString(), output);
        int domains = new System.Collections.Generic.HashSet<string>(
            System.Linq.Enumerable.Select(FactKeyCatalog.Keys, k => k.Split('.')[0])).Count;
        Assert.Contains(domains.ToString(), output);
        Assert.Contains(FactKeyDynamicCatalog.Families.Count.ToString(), output);
    }

    [Fact]
    public void 產生器不得碰它不負責的區段()
    {
        // 工具的職責邊界寫在檔頭：只動 6.3／6.5／附錄 F／F-2。用原始碼掃描釘住這件事——
        // 它不該出現對其他章節標題的寫入。
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "gen-readme-domains.py"));
        Assert.Contains("SEC_63_START", text);
        Assert.Contains("SEC_65_START", text);
        Assert.Contains("APP_F_START", text);
        Assert.DoesNotContain("## 第 1 部", text);
        Assert.DoesNotContain("## 第 2 部", text);
        Assert.DoesNotContain("## 第 3 部", text);
    }

    [Fact]
    public void 文件裡要寫明怎麼跑這支工具()
    {
        string contributing = File.ReadAllText(Path.Combine(RepoRoot(), "CONTRIBUTING.md"));
        Assert.Contains("gen-readme-domains.py", contributing);
        Assert.Contains("--check", contributing);
    }
}
