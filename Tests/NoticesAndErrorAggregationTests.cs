using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 第三方授權清單對帳（P0-10／EG-018）：THIRD-PARTY-NOTICES.md 的執行期元件表必須涵蓋
/// csproj 的每個 PackageReference／PackageDownload——加套件沒寫進清單＝紅燈（法務界線硬性）。
/// </summary>
public class ThirdPartyNoticesTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(dir.FullName + "/XinSpect.csproj"))
            dir = dir.Parent;
        return System.IO.Path.Combine(dir!.FullName, System.IO.Path.Combine(parts));
    }

    [Fact]
    public void 執行期套件逐項都在清單裡()
    {
        var referenced = new List<string>();
        foreach (var proj in new[] { "XinSpect.csproj", "Bridge/XtuBridge.csproj", "BlueSquadron/BlueSquadronBridge.csproj" })
        {
            string text = File.ReadAllText(RepoFile(proj));
            foreach (Match m in Regex.Matches(text, @"<PackageReference Include=""([^""]+)"""))
                if (!m.Groups[1].Value.Contains("PrivateAssets", StringComparison.Ordinal))
                    referenced.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"<PackageDownload Include=""([^""]+)"""))
                referenced.Add(m.Groups[1].Value);
        }

        string notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));
        var missing = referenced
            .Where(p => !notices.Contains(p, StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        Assert.True(missing.Count == 0,
            "清單缺以下套件（加套件要同步 THIRD-PARTY-NOTICES.md——授權義務成文是發佈界線）：" + string.Join("、", missing));
    }

    [Fact]
    public void 清單要交代授權與義務欄()
    {
        string notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));
        Assert.Contains("MPL-2.0", notices);
        Assert.Contains("LGPL", notices, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MIT", notices);   // 第三人元件的授權（WebView2／TraceEvent／NAudio 等）——保留，這是他人條款不是本專案的授權
        Assert.Contains("不構成法律意見", notices);
        Assert.Contains("WinRing0", notices, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 清單不得縮水()
    {
        string notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));
        var rows = Regex.Matches(notices, @"^\| [^|\- ]", RegexOptions.Multiline);
        Assert.True(rows.Count >= 9, "執行期＋建置期元件表被刪——只許增不許減");
    }
}

/// <summary>
/// 錯誤聚合（P0-07／UX-010 資料與 CLI 面）：分組計數、零錯誤＝空清單如實顯示、CLI 退出碼語意。
/// </summary>
public class ErrorAggregationTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    private static HardwareFact F(string key, FactAvailability avail, string? reason = null) =>
        new(key, "測試", key, "", "", "測試", FactTrustLevel.Derived, false, At, null, avail, reason);

    [Fact]
    public void 聚合_分狀態計數與逐項原因()
    {
        var agg = ErrorAggregationService.Aggregate(
        [
            F("a.ok", FactAvailability.Present),
            F("b.read", FactAvailability.ReadError, "登錄值不存在"),
            F("b.priv", FactAvailability.InsufficientPrivilege, "需要管理員"),
            F("b.ns", FactAvailability.NotSupported),
            F("c.ok", FactAvailability.Present),
        ]);
        Assert.Equal(3, agg.Total);
        Assert.Equal(1, agg.ReadError);
        Assert.Equal(1, agg.InsufficientPrivilege);
        Assert.Equal(1, agg.NotSupported);
        Assert.Equal("登錄值不存在", agg.Items.Single(i => i.Key == "b.read").Reason);
    }

    [Fact]
    public void 零錯誤_空清單如實顯示不是隱藏()
    {
        var agg = ErrorAggregationService.Aggregate([F("only", FactAvailability.Present)]);
        Assert.Equal(0, agg.Total);
        var payload = ErrorAggregationService.ToPayload(agg, At);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(payload));
        Assert.Contains("空清單如實顯示", json.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void 缺原因_如實標原因未提供()
    {
        var agg = ErrorAggregationService.Aggregate([F("x", FactAvailability.NotSupported)]);
        Assert.Equal("（原因未提供）", agg.Items[0].Reason);
    }

    [Fact]
    public void CLI錯誤聚合_空集合退出碼0()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = CliService.Run([CliService.ErrorsArg], () => [F("ok", FactAvailability.Present)], stdout, stderr);
        Assert.Equal(CliService.ExitOk, exit);
        using var j = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.Contains("空清單如實顯示", j.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void CLI錯誤聚合_有三態事實退出碼2()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = CliService.Run([CliService.ErrorsArg],
            () => [F("ok", FactAvailability.Present), F("bad", FactAvailability.ReadError, "x")], stdout, stderr);
        Assert.Equal(CliService.ExitPartial, exit);
        using var json = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
    }
}
