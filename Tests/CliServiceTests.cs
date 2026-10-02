using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CLI 模式（WP32）的契約：退出碼語意（0 全 Present／2 部分三態／1 致命）、JSON 輸出可解析且帶
/// availability 誠實欄位、--query 前綴過濾、--out 寫檔。收集以注入委派取代，不碰硬體。
/// </summary>
public class CliServiceTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    private readonly string _outPath = Path.Combine(Path.GetTempPath(), $"xincli-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_outPath)) File.Delete(_outPath);
    }

    private static HardwareFact Fact(string key, string value,
        FactAvailability availability = FactAvailability.Present, string? reason = null) =>
        new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Measured, false, At, null, availability, reason);

    [Fact]
    public void 全部Present時退出0_JSON可解析且帶誠實欄位()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int code = CliService.Run(["--json", "evidence"],
            () => [Fact("chipset.bios_cntl", "最強保護：SMM_BWP=1")], stdout, stderr);

        Assert.Equal(CliService.ExitOk, code);
        var doc = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.Equal("evidence", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
        Assert.True(doc.RootElement.GetProperty("allPresent").GetBoolean());
        Assert.Equal("chipset.bios_cntl", doc.RootElement.GetProperty("facts")[0].GetProperty("key").GetString());
        Assert.Equal("Present", doc.RootElement.GetProperty("facts")[0].GetProperty("availability").GetString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void 部分讀不到時退出2_三態細節在輸出裡()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int code = CliService.Run(["--json", "evidence"], () =>
        [
            Fact("chipset.bios_cntl", "最強保護"),
            Fact("spi.hsfsts", "", FactAvailability.ReadError, "MMIO 讀取失敗"),
        ], stdout, stderr);

        Assert.Equal(CliService.ExitPartial, code);
        var doc = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.False(doc.RootElement.GetProperty("allPresent").GetBoolean());
        Assert.Equal("ReadError", doc.RootElement.GetProperty("facts")[1].GetProperty("availability").GetString());
        Assert.Contains("MMIO 讀取失敗", doc.RootElement.GetProperty("facts")[1].GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public void 查詢過濾與輸出檔()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int code = CliService.Run(["--json", "evidence", "--query", "platform.", "--out", _outPath], () =>
        [
            Fact("platform.hvci", "關閉"),
            Fact("platform.testsigning", "關閉"),
            Fact("chipset.bios_cntl", "最強保護"),
        ], stdout, stderr);

        Assert.Equal(CliService.ExitOk, code);
        Assert.True(File.Exists(_outPath));
        var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_outPath));
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32()); // 只剩 platform. 前綴
    }

    [Fact]
    public void 未知名稱與範圍錯誤_退出1與訊息()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(CliService.ExitError, CliService.Run(["--wat"], () => [], stdout, stderr));
        Assert.Contains("未知引數", stderr.ToString());

        Assert.Equal(CliService.ExitError, CliService.Run(["--json", "everything"], () => [], stdout, stderr));
        Assert.Contains("evidence", stderr.ToString());
    }

    [Fact]
    public void 收集拋例外時退出1_幫助文字帶退出碼語意()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(CliService.ExitError, CliService.Run(["--json", "evidence"], () => throw new InvalidOperationException("驅動爆炸"), stdout, stderr));
        Assert.Contains("驅動爆炸", stderr.ToString());

        Assert.Equal(CliService.ExitOk, CliService.Run(["--help"], () => [], stdout, stderr));
        Assert.Contains("退出碼", stdout.ToString());
    }

    // ===== --compare-flash（WP4 第二層的 CLI 面） =====

    private static HardwareFact CompareFact(string value, FactAvailability availability, double? numeric, string? reason = null) =>
        new("spi.bios_compare", "韌體安全", "BIOS 區比對（vs 參考映像）", value, "", "s",
            FactTrustLevel.Measured, false, At, numeric, availability, reason);

    [Fact]
    public void 比對模式_一致退0_有差異退2_三態退2()
    {
        System.IO.File.WriteAllBytes(_outPath, new byte[16]); // 參考映像（內容不重要，走注入的假比對）
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(CliService.ExitOk, CliService.Run(["--compare-flash", _outPath], () => [], stdout, stderr,
            compare: _ => CompareFact("一致（64 個 4KB 塊全部相同）", FactAvailability.Present, 0)));
        var doc = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        Assert.Equal("flashcompare", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal("Present", doc.RootElement.GetProperty("fact").GetProperty("availability").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("fact").GetProperty("numericValue").GetInt32());

        Assert.Equal(CliService.ExitPartial, CliService.Run(["--compare-flash", _outPath], () => [], stdout, stderr,
            compare: _ => CompareFact("差異 1 個 4KB 塊：0x20000", FactAvailability.Present, 1)));

        Assert.Equal(CliService.ExitPartial, CliService.Run(["--compare-flash", _outPath], () => [], stdout, stderr,
            compare: _ => CompareFact("", FactAvailability.NotApplicable, null, "大小不符——誠實拒比")));
    }

    [Fact]
    public void 比對模式_缺路徑與讀檔失敗退1()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        Assert.Equal(CliService.ExitError, CliService.Run(["--compare-flash"], () => [], stdout, stderr,
            compare: _ => CompareFact("x", FactAvailability.Present, 0)));
        Assert.Contains("參考映像路徑", stderr.ToString());

        string missing = Path.Combine(Path.GetTempPath(), $"xincli-missing-{Guid.NewGuid():N}.bin");
        Assert.Equal(CliService.ExitError, CliService.Run(["--compare-flash", missing], () => [], stdout, stderr,
            compare: _ => CompareFact("x", FactAvailability.Present, 0))); // 檔不存在
        Assert.Contains("讀取參考映像失敗", stderr.ToString());
    }
}
