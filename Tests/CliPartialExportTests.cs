using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 選擇性匯出（U10-PF-0007／exp.partial）的契約：--only 逗號分隔前綴、篩選正確（聯集）、
/// 部分報告如實聲明分母（totalCollected／partial／note「這是 N 筆中的 M 筆」）、
/// 無命中時如實說是空匯出並退 2，未指定選擇條件時不冒充「部分」。收集以注入委派取代，不碰硬體。
/// </summary>
public class CliPartialExportTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private readonly string _outPath = Path.Combine(Path.GetTempPath(), $"xincli-only-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_outPath)) File.Delete(_outPath);
    }

    private static HardwareFact Fact(string key, string value,
        FactAvailability availability = FactAvailability.Present, string? reason = null) =>
        new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Measured, false, At, null, availability, reason);

    private static HardwareFact[] Facts() =>
    [
        Fact("cpu.clock", "4200"),
        Fact("cpu.threads", "36"),
        Fact("spi.hsfsts", "鎖定"),
        Fact("platform.secure_boot", "啟用"),
        Fact("mem.channel", "2"),
    ];

    private static JsonElement RunAndParse(string[] args, out int code, out string stderrText)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        code = CliService.Run(args, Facts, stdout, stderr);
        stderrText = stderr.ToString();
        var doc = JsonDocument.Parse(stdout.ToString());
        return doc.RootElement.Clone();
    }

    [Fact]
    public void only多前綴篩選正確_聯集收錄()
    {
        var root = RunAndParse(["--json", "evidence", CliService.OnlyArg, "cpu.,platform."], out int code, out _);

        Assert.Equal(CliService.ExitOk, code);
        var keys = root.GetProperty("facts").EnumerateArray()
            .Select(f => f.GetProperty("key").GetString()!).ToHashSet();
        Assert.Equal(new HashSet<string> { "cpu.clock", "cpu.threads", "platform.secure_boot" }, keys);
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        // 選擇條件原樣回報，可對帳
        var only = root.GetProperty("only").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["cpu.", "platform."], only);
    }

    [Fact]
    public void 部分匯出如實聲明分母_不冒充全量()
    {
        var root = RunAndParse(["--json", "evidence", CliService.OnlyArg, "spi."], out _, out _);

        Assert.True(root.GetProperty("partial").GetBoolean(), "篩掉了一大半卻不標部分匯出");
        Assert.Equal(5, root.GetProperty("totalCollected").GetInt32());   // 分母＝過濾前收集到的全部
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        string note = root.GetProperty("note").GetString() ?? "";
        Assert.Contains("部分匯出", note, StringComparison.Ordinal);
        Assert.Contains("5 筆中的 1 筆", note, StringComparison.Ordinal);
    }

    [Fact]
    public void 無命中時如實_空匯出退2_不冒充沒有問題()
    {
        var root = RunAndParse(["--json", "evidence", CliService.OnlyArg, "nope."], out int code, out _);

        Assert.Equal(CliService.ExitPartial, code);
        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Equal(5, root.GetProperty("totalCollected").GetInt32());
        string note = root.GetProperty("note").GetString() ?? "";
        Assert.Contains("空匯出", note, StringComparison.Ordinal);
        Assert.Contains("沒有命中", note, StringComparison.Ordinal);
    }

    [Fact]
    public void 未指定選擇條件時不標部分_分母等於總數()
    {
        var root = RunAndParse(["--json", "evidence"], out int code, out _);

        Assert.Equal(CliService.ExitOk, code);
        Assert.False(root.GetProperty("partial").GetBoolean());
        Assert.Equal(root.GetProperty("totalCollected").GetInt32(), root.GetProperty("count").GetInt32());
        Assert.Equal(0, root.GetProperty("only").GetArrayLength());
        Assert.True(root.GetProperty("note").ValueKind is JsonValueKind.Null, "全量匯出不該帶部分匯出註記");
    }

    [Fact]
    public void only可與query併用_兩道篩選都生效()
    {
        var root = RunAndParse(["--json", "evidence", CliService.QueryArg, "cpu.", CliService.OnlyArg, "cpu.threads"],
            out int code, out _);

        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal("cpu.threads",
            root.GetProperty("facts")[0].GetProperty("key").GetString());
        Assert.Equal(5, root.GetProperty("totalCollected").GetInt32());
    }

    [Fact]
    public void 空白與重複前綴被清理_不把空字串當萬用前綴()
    {
        // 空字串前綴會命中一切——把它清掉才算誠實的選擇條件解析。
        var root = RunAndParse(["--json", "evidence", CliService.OnlyArg, " spi. , spi., "], out int code, out _);

        Assert.Equal(CliService.ExitOk, code);
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var only = root.GetProperty("only").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["spi."], only);
    }
}
