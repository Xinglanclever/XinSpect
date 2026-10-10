using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// v2.60 的兩件 DeepSeek §8 遺留：事件標註的公開出口（<see cref="BaselineLearningService.QueryAnnotations"/>）
/// 與磁碟深掃的明示入口（CLI <c>--disk-usage [--deep]</c>）。
/// 界線釘死：讀不到≠0 筆、深掃只讀不刪、命令沒有任何刪除路徑。
/// </summary>
public class EventAnnotationAndDiskUsageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    // ── 事件標註出口 ─────────────────────────────────────────────────────

    [Fact]
    public void 標註出口_把四類事件分類成標註()
    {
        var events = new[]
        {
            new AnnotatedEvent(At.AddDays(-2), "System", 41, "系統已重新開機而未先正常關機"),
            new AnnotatedEvent(At.AddDays(-1), "Microsoft-Windows-WHEA-Logger/Operational", 17, "已更正硬體錯誤"),
            new AnnotatedEvent(At.AddHours(-3), "System", 6008, "上一次關機是意外的"),
        };
        var annotations = BaselineLearningService.QueryAnnotations(TimeSpan.FromDays(7), () => events);
        Assert.NotNull(annotations);
        Assert.Equal(3, annotations!.Count);
        Assert.All(annotations, a => Assert.False(string.IsNullOrWhiteSpace(a.Kind)));
        Assert.All(annotations, a => Assert.False(string.IsNullOrWhiteSpace(a.Label)));
    }

    [Fact]
    public void 標註出口_讀不到事件回null_呼叫端才能如實標示()
    {
        var annotations = BaselineLearningService.QueryAnnotations(TimeSpan.FromDays(7),
            () => throw new InvalidOperationException("channel missing"));
        Assert.Null(annotations);
    }

    [Fact]
    public void 標註出口_沒有事件回空清單_不與讀不到混同()
    {
        var annotations = BaselineLearningService.QueryAnnotations(TimeSpan.FromDays(7), () => []);
        Assert.NotNull(annotations);
        Assert.Empty(annotations!);
    }

    [Fact]
    public void 未分類的事件不冒充標註()
    {
        var events = new[] { new AnnotatedEvent(At, "System", 9999, "不相關的事件") };
        var annotations = BaselineLearningService.QueryAnnotations(TimeSpan.FromDays(1), () => events);
        Assert.NotNull(annotations);
        Assert.Empty(annotations!);
    }

    // ── 深掃請求 ─────────────────────────────────────────────────────────

    [Fact]
    public void 深掃請求_加入使用者設定檔且預設根不含它()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var deep = DiskFullFactsService.DeepRequest();
        var normal = DiskFullFactsService.DefaultRequest();

        Assert.Contains(profile, deep.Roots);
        Assert.DoesNotContain(profile, normal.Roots);
        Assert.True(deep.MaxDepth > normal.MaxDepth, "深掃的深度上限要高於自動收集");
    }

    // ── CLI --disk-usage ────────────────────────────────────────────────

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = CliService.Run(args, () => [], stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void 磁碟命令_預設根不含使用者設定檔且明說只讀不刪()
    {
        var (exit, stdout, _) = Run(CliService.DiskUsageArg);
        Assert.True(exit is CliService.ExitOk or CliService.ExitPartial);
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal("disk-usage", json.RootElement.GetProperty("scope").GetString());
        Assert.False(json.RootElement.GetProperty("deep").GetBoolean());
        Assert.Contains("只讀排行，不刪任何東西", json.RootElement.GetProperty("note").GetString());
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // 精確比對（暫存根常含設定檔路徑當子字串——「不含設定檔」是指根清單沒有它這個根）
        var rootList = json.RootElement.GetProperty("roots").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain(profile, rootList);
    }

    [Fact]
    public void 磁碟深掃命令_加入設定檔且深度6()
    {
        var (exit, stdout, _) = Run(CliService.DiskUsageArg, CliService.DeepArg);
        Assert.True(exit is CliService.ExitOk or CliService.ExitPartial);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("deep").GetBoolean());
        Assert.Equal(6, json.RootElement.GetProperty("maxDepth").GetInt32());
        Assert.Contains("這就是它不進啟動路徑的原因", json.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void 磁碟命令_輸出到檔案與stdout一致()
    {
        string path = Path.Combine(Path.GetTempPath(), "diskusage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (exit, stdout, _) = Run(CliService.DiskUsageArg, CliService.OutArg, path);
            Assert.True(exit is CliService.ExitOk or CliService.ExitPartial);
            Assert.Equal("", stdout.Trim());
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("disk-usage", json.RootElement.GetProperty("scope").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 說明文字_列出磁碟命令與深掃開關()
    {
        var (exit, stdout, _) = Run(CliService.HelpArg);
        Assert.Equal(CliService.ExitOk, exit);
        Assert.Contains("--disk-usage", stdout);
        Assert.Contains("--deep", stdout);
        Assert.Contains("沒有任何刪除路徑", stdout);
    }
}
