using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 子行程執行器（第 330 輪，修 D4）：兩個串流都要在逾時之前開始讀。
/// </summary>
/// <remarks>
/// 這裡用 <c>cmd /c</c> 造幾個「會把管線塞滿」的假子行程。
/// 那些情境在舊實作下會永久卡住——**測試會先卡住，然後被 xUnit 的整體逾時砍掉**，
/// 所以每一條都加了相對短的 timeout 並斷言「有回來」。
/// </remarks>
public class ProcessRunnerTests
{
    /// <summary>寫很多 stderr 再寫一點 stdout 然後結束。</summary>
    private static (string File, List<string> Args) FloodStderr(int kilobytes) =>
        ("cmd.exe", ["/c", $"for /L %i in (1,1,{kilobytes}) do @echo ############################## 1>&2"]);

    [Fact]
    public void 基本呼叫_取回stdout與結束碼()
    {
        var run = ProcessRunner.Run("cmd.exe", ["/c", "echo hello"], 5000);
        Assert.True(run.Succeeded);
        Assert.Contains("hello", run.Stdout);
        Assert.Equal(0, run.ExitCode);
        Assert.False(run.TimedOut);
    }

    [Fact]
    public void 結束碼非零_要看得到而且不算成功()
    {
        var run = ProcessRunner.Run("cmd.exe", ["/c", "exit 3"], 5000);
        Assert.False(run.Succeeded);
        Assert.Equal(3, run.ExitCode);
        Assert.False(run.TimedOut);
    }

    [Fact]
    public void 大量stderr不會死鎖()
    {
        // 舊實作（重導 stderr 卻不讀，或先 ReadToEnd 再 WaitForExit）會卡在這裡。
        var (file, args) = FloodStderr(400);
        var sw = Stopwatch.StartNew();
        var run = ProcessRunner.Run(file, args, 20000);
        sw.Stop();
        Assert.False(run.TimedOut, "大量 stderr 讓執行器逾時——這就是死鎖的指紋");
        Assert.True(sw.ElapsedMilliseconds < 20000);
        Assert.NotNull(run.Stderr);
        Assert.True(run.Stderr!.Length > 400 * 4, $"stderr 只收到 {run.Stderr.Length} 字元，應該更多");
    }

    [Fact]
    public void 兩個串流都大量輸出也不會死鎖()
    {
        // stdout 與 stderr 同時灌：任一串流沒被讀就會卡住。
        var run = ProcessRunner.Run("cmd.exe",
            ["/c", "for /L %i in (1,1,300) do @(echo out-############################## & echo err-############################## 1>&2)"],
            20000);
        Assert.False(run.TimedOut, "兩個串流同時灌水讓執行器逾時");
        Assert.True(run.Stdout!.Length > 300 * 4);
        Assert.True(run.Stderr!.Length > 300 * 4);
    }

    [Fact]
    public void 逾時_要殺掉並回報逾時()
    {
        var sw = Stopwatch.StartNew();
        var run = ProcessRunner.Run("cmd.exe", ["/c", "ping -n 30 127.0.0.1 > nul"], 800);
        sw.Stop();
        Assert.True(run.TimedOut);
        Assert.Null(run.ExitCode);
        Assert.Contains("沒有回應", run.Reason);
        // 不該等到 ping 自己結束（30 秒）
        Assert.True(sw.ElapsedMilliseconds < 10000, $"實際花了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void 程式不存在_回報原因而不是拋例外()
    {
        var run = ProcessRunner.Run("definitely-not-a-real-program-xyz.exe", [], 1000);
        Assert.False(run.Succeeded);
        Assert.False(run.TimedOut);
        Assert.False(string.IsNullOrWhiteSpace(run.Reason));
    }

    [Fact]
    public void 參數走ArgumentList_空白與引號不會被拆錯()
    {
        var run = ProcessRunner.Run("cmd.exe", ["/c", "echo", "a b c"], 5000);
        Assert.True(run.Succeeded);
        Assert.Contains("a b c", run.Stdout);
    }

    // ── 接線守門：兩個既有的呼叫端都改用共用的執行器 ────────────────────

    [Fact]
    public void 兩個既有呼叫端都不再自己寫ReadToEnd()
    {
        string root = RepoRoot();
        foreach (string rel in new[]
                 {
                     Path.Combine("Services", "BootOrderReader.cs"),
                     Path.Combine("Services", "AdvancedSupplementFactsService.cs"),
                 })
        {
            string text = File.ReadAllText(Path.Combine(root, rel));
            Assert.DoesNotContain("StandardOutput.ReadToEnd(", text);
            Assert.DoesNotContain("StandardError.ReadToEnd(", text);
        }
    }

    [Fact]
    public void BootOrderReader走共用執行器()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "BootOrderReader.cs"));
        Assert.Contains("ProcessRunner.Run", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Rules")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
