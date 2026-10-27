using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 版號 bump 的工具本身要被驗。
///
/// <para>
/// 這件事我手寫過三次，三次都出了同一種錯：重跑一次就把 ChangelogCatalog 插成三筆、
/// README 插成兩條 bullet 加兩節 11.3。根因不是粗心，是<b>「冪等」被實作成一種猜測</b>
/// ——「舊字串還在不在」。插入類操作不會消耗舊字串，所以「舊的還在」永遠成立。
/// </para>
/// <para>
/// 所以 <c>Tools/bump-version.py</c> 把操作拆成兩種語意：<c>Replace</c>（換掉，找不到就失敗）
/// 與 <c>InsertBefore</c>（插在前面，已經在就跳過）。這裡在<b>暫存副本</b>上真的跑它兩次，
/// 驗第二次什麼都不改——不碰工作樹的檔案。
/// </para>
/// </summary>
public class VersionBumpToolTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>把版號相關的檔案複製到暫存目錄，組成一個最小可用的倉庫。</summary>
    private static string MakeSandbox()
    {
        string root = RepoRoot();
        string box = Path.Combine(Path.GetTempPath(), "bump-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(box);
        Directory.CreateDirectory(Path.Combine(box, "Nav"));
        Directory.CreateDirectory(Path.Combine(box, "Views"));
        Directory.CreateDirectory(Path.Combine(box, ".github", "ISSUE_TEMPLATE"));
        Directory.CreateDirectory(Path.Combine(box, "Tools"));

        foreach (string rel in new[]
                 {
                     "XinSpect.csproj", "README.md", "README.zh-CN.md", "README.en.md", "SECURITY.md",
                     Path.Combine("Nav", "ChangelogCatalog.cs"),
                     Path.Combine("Views", "AboutView.xaml"),
                     Path.Combine(".github", "ISSUE_TEMPLATE", "bug_report.yml"),
                     Path.Combine(".github", "ISSUE_TEMPLATE", "hardware_report.yml"),
                     Path.Combine("Tools", "bump-version.py"),
                 })
        {
            string src = Path.Combine(root, rel);
            string dst = Path.Combine(box, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        return box;
    }

    private static (int ExitCode, string Output) Run(string box, string version, string itemsFile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{Path.Combine(box, "Tools", "bump-version.py")}\" "
                        + $"--version {version} --codename \"Test Code\" --title \"測試標題\" "
                        + $"--date 2026-10-27 --items \"{itemsFile}\" --root \"{box}\"",
            WorkingDirectory = box,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("bump 工具沒有結束"); }
        return (p.ExitCode, output);
    }

    private static string WriteItems()
    {
        string f = Path.Combine(Path.GetTempPath(), "items-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "測試項目一\n測試項目二\n");
        return f;
    }

    [Fact]
    public void 跑兩次不會重複插入任何條目()
    {
        string box = MakeSandbox();
        string items = WriteItems();
        try
        {
            string old = ReadVersion(Path.Combine(box, "XinSpect.csproj"));
            string next = NextMinor(old);

            var first = Run(box, next, items);
            Assert.True(first.ExitCode == 0, "第一次就跑失敗：\n" + first.Output);

            var second = Run(box, next, items);
            Assert.True(second.ExitCode == 0, "第二次跑失敗：\n" + second.Output);

            // 每一個「只該有一筆」的地方都必須剛好一筆
            string cat = File.ReadAllText(Path.Combine(box, "Nav", "ChangelogCatalog.cs"));
            Assert.Equal(1, Count(cat, $"Version = \"{next}\","));

            string md = File.ReadAllText(Path.Combine(box, "README.md"));
            Assert.Equal(1, Count(md, $"- **v{next}**（"));
            Assert.Equal(1, Count(md, $"#### v{next}（"));

            string zh = File.ReadAllText(Path.Combine(box, "README.zh-CN.md"));
            Assert.Equal(1, Count(zh, $"- **v{next} "));

            string en = File.ReadAllText(Path.Combine(box, "README.en.md"));
            Assert.Equal(1, Count(en, $"| {next} | "));
        }
        finally
        {
            try { Directory.Delete(box, true); } catch { }
            try { File.Delete(items); } catch { }
        }
    }

    [Fact]
    public void 八個地方都要被改到而且只改一次()
    {
        string box = MakeSandbox();
        string items = WriteItems();
        try
        {
            string old = ReadVersion(Path.Combine(box, "XinSpect.csproj"));
            string next = NextMinor(old);
            var r = Run(box, next, items);
            Assert.True(r.ExitCode == 0, r.Output);

            Assert.Contains($"<Version>{next}</Version>", File.ReadAllText(Path.Combine(box, "XinSpect.csproj")));
            Assert.Contains($"版本 {next} ・", File.ReadAllText(Path.Combine(box, "Views", "AboutView.xaml")));
            Assert.Contains($"（目前 {next}）", File.ReadAllText(Path.Combine(box, "SECURITY.md")));
            Assert.Contains($"placeholder: \"{next}\"",
                File.ReadAllText(Path.Combine(box, ".github", "ISSUE_TEMPLATE", "bug_report.yml")));
            Assert.Contains($"placeholder: \"{next}\"",
                File.ReadAllText(Path.Combine(box, ".github", "ISSUE_TEMPLATE", "hardware_report.yml")));

            foreach (string rel in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
                Assert.Contains($"badge/version-{next}-", File.ReadAllText(Path.Combine(box, rel)));

            // 舊版號不得殘留在徽章與下載連結裡
            foreach (string rel in new[] { "README.md", "README.zh-CN.md", "README.en.md" })
            {
                string t = File.ReadAllText(Path.Combine(box, rel));
                Assert.DoesNotContain($"badge/version-{old}-", t);
                Assert.DoesNotContain($"releases/download/v{old}/", t);
            }
        }
        finally
        {
            try { Directory.Delete(box, true); } catch { }
            try { File.Delete(items); } catch { }
        }
    }

    [Fact]
    public void 已經是目標版號時要說沒事可做而不是硬插一次()
    {
        string box = MakeSandbox();
        string items = WriteItems();
        try
        {
            string current = ReadVersion(Path.Combine(box, "XinSpect.csproj"));
            var r = Run(box, current, items);
            Assert.True(r.ExitCode == 0, r.Output);
            Assert.Contains("沒有事要做", r.Output);

            string cat = File.ReadAllText(Path.Combine(box, "Nav", "ChangelogCatalog.cs"));
            Assert.Equal(1, Count(cat, $"Version = \"{current}\","));   // 還是原本那一筆
        }
        finally
        {
            try { Directory.Delete(box, true); } catch { }
            try { File.Delete(items); } catch { }
        }
    }

    private static string ReadVersion(string csproj)
    {
        var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(csproj), "<Version>([^<]+)</Version>");
        Assert.True(m.Success);
        return m.Groups[1].Value.Trim();
    }

    private static string NextMinor(string version)
    {
        var parts = version.Split('.');
        return parts[0] + "." + (int.Parse(parts[1]) + 1).ToString("00");
    }

    private static int Count(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
