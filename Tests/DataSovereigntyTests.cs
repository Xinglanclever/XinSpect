using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP39 資料主權的機器檢查：事實蒐集與解碼器原始碼**零網路 API**（HttpClient/WebClient/
/// TcpClient/UdpClient/上傳下載字串）——引入即紅燈。網路功能只允許出現在四個使用者主動
/// 觸發的 opt-in 檔案（回饋／AI／測速／網路延遲量測），清單與 docs/DATA-SOVEREIGNTY.md 同步。
/// </summary>
public class DataSovereigntyTests
{
    private static readonly string[] NetworkTokens =
    [
        "HttpClient", "WebClient", "FtpWebRequest", "TcpClient", "UdpClient",
        "UploadString", "DownloadString", "WebRequest.Create",
    ];

    /// <summary>使用者主動觸發的網路功能（與 docs/DATA-SOVEREIGNTY.md §2 同一清單）。</summary>
    private static readonly string[] OptInNetworkFiles =
    [
        "AiService.cs", "FeedbackService.cs", "NetworkSpeedService.cs",
        "NetworkStackLatencyAdapter.cs",
    ];

    [Fact]
    public void 事實蒐集與解碼器_零網路API()
    {
        var root = FindRepoRoot();
        var scanned = Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "XinSpect.Decoders"), "*.cs"))
            .Where(f => !OptInNetworkFiles.Any(a => f.EndsWith(a, StringComparison.Ordinal)))
            .ToList();
        Assert.NotEmpty(scanned);

        var offenders = new List<string>();
        foreach (var file in scanned)
        {
            string text = File.ReadAllText(file);
            foreach (var token in NetworkTokens.Where(text.Contains))
                offenders.Add($"{Path.GetFileName(file)}：{token}");
        }
        Assert.True(offenders.Count == 0,
            "事實蒐集／解碼層出現網路 API（違反資料主權聲明）：" + string.Join("、", offenders));
    }

    [Fact]
    public void 資料主權聲明_與允許清單同步成文()
    {
        var doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "DATA-SOVEREIGNTY.md"));
        Assert.All(OptInNetworkFiles, f => Assert.Contains(f.Replace(".cs", ""), doc));
        Assert.Contains("零網路呼叫", doc);
        Assert.Contains("刪除檔案就是刪除資料", doc);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
