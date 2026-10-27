using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 每一支會被編進產品的原始碼都要在版控裡。
///
/// <para>
/// 這個專案踩過兩次同一種坑：<c>dotnet publish</c> 從<b>工作樹</b>編譯，未追蹤的 <c>.cs</c>
/// 會被一起吃進 exe，所以本機看起來一切正常——而 tag 的乾淨 clone 建置直接紅
/// （v2.66 是 <c>CliService</c> 引用 <c>ErrorAggregationService</c>；v3.02 是
/// <c>AiUsage.cs</c>）。兩次都是「推上去之後才發現」。
/// </para>
/// <para>
/// 這條測試把那個發現時機提前到測試階段：磁碟上有、<c>git</c> 沒有的 <c>.cs</c> 就是紅燈。
/// <c>work/</c> 目錄排除（那是腳本與探針，被 <c>Directory.Build.props</c> 排除在編譯之外）。
/// </para>
/// </summary>
public class TrackedSourcesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static bool IsGitRepo(string root)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse --is-inside-work-tree",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        string output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(30_000);
        return p.ExitCode == 0 && output == "true";
    }

    [Fact]
    public void 會被編進產品的原始碼都要在版控裡()
    {
        string root = RepoRoot();
        if (!IsGitRepo(root))
        {
            // 從原始碼壓縮包建置時沒有 .git——這條檢查沒有意義，但不該讓整個套件紅。
            Assert.True(true, "不在 git 工作樹裡，跳過（發佈流程另外會擋）");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "ls-files --others --exclude-standard -- *.cs",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, "git ls-files 失敗：" + p.StandardError.ReadToEnd());

        var offenders = output.Split('\n')
            .Select(l => l.Trim().Replace('\\', '/'))
            .Where(l => l.Length > 0)
            .Where(l => !l.StartsWith("work/", StringComparison.Ordinal))
            .Where(l => !l.Contains("/obj/", StringComparison.Ordinal))
            .Where(l => !l.Contains("/bin/", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("obj/", StringComparison.Ordinal) && !l.StartsWith("bin/", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("publish-", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "以下原始碼沒有進版控——本機能建置（publish 從工作樹編譯），但乾淨 clone 會 CS0246：\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 發佈腳本必須在推標籤<b>之前</b>擋這件事。上面的測試會在測試階段紅，
    /// 但測試是「跑的時候才看」；發佈腳本才是最後一道閘門。
    /// </summary>
    [Fact]
    public void 發佈腳本要在推送前擋住未入版控的原始碼()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "Tools", "release.ps1"));
        Assert.Contains("git ls-files --others", script, StringComparison.Ordinal);

        // 這道閘門必須在 push 之前（否則等於沒有）
        int gate = script.IndexOf("git ls-files --others", StringComparison.Ordinal);
        int push = script.IndexOf("push origin main", StringComparison.Ordinal);
        Assert.True(push > 0, "找不到 push origin main");
        Assert.True(gate < push, "未入版控的檢查排在 push 之後——那時已經來不及了");
    }
}
