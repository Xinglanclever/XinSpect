using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 作業系統 CVE 離線對照（SE-002）。測的是<b>框架的紀律</b>：
/// 沒有快照日期就不收、組建號數字比較（不是字串比較）、空表如實說「還沒有資料」、
/// 命中一律標「待查證」而不是「已中招」。
/// </summary>
public class CveOfflineFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 6, 0, 0, TimeSpan.Zero);

    private static OfflineAdvisory Advisory(string cve, string upTo, string? fixedIn = null) =>
        new(cve, "測試產品", upTo, fixedIn, "測試摘要", "測試來源");

    [Fact]
    public void 本版出貨的表是空的_且如實說還沒有資料而不是沒問題()
    {
        Assert.Empty(CveOfflineFactsService.ShippedTable);

        var facts = CveOfflineFactsService.Collect(At, "Windows", "10.0.26100.0", "26100",
            probe: () => (CveOfflineFactsService.ShippedTable, "—", "本版未出貨條目"));
        var f = Assert.Single(facts);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("收錄 0 條", f.Value);
        Assert.Contains("還沒有資料", f.Value);
        Assert.Contains("還沒有資料", f.Value);
        Assert.Contains("不是『沒有已知問題』", f.UnavailableReason ?? "");
    }

    [Fact]
    public void 組建號比較是數字比較_不是字串比較()
    {
        // 字串比較會把 "9000" > "26100" 判成真——這一條就是在防那個
        Assert.Equal(26100, CveOfflineFactsService.BuildNumber("10.0.26100.1"));
        Assert.Null(CveOfflineFactsService.BuildNumber("版本不明"));
        Assert.Null(CveOfflineFactsService.BuildNumber(null));

        var table = new[] { Advisory("CVE-TEST-1", "10000"), Advisory("CVE-TEST-2", "30000") };
        var hits = CveOfflineFactsService.Match(table, "26100");

        // 26100 只落在 30000 之下：30000 那條命中、10000 那條不命中
        Assert.Single(hits);
        Assert.Equal("CVE-TEST-2", hits[0].CveId);

        // 字串比較會把 "26100" < "9000" 判成真（'2' < '9'）而誤命中——數字比較不會
        Assert.Empty(CveOfflineFactsService.Match([Advisory("CVE-字串陷阱", "9000")], "26100"));
    }

    [Fact]
    public void 命中時_標Unknown待查證並印出快照日期()
    {
        var table = new[] { Advisory("CVE-TEST-1", "30000", "30100") };
        var facts = CveOfflineFactsService.Collect(At, "Windows Server 2025", "10.0.26100.1", "26100",
            probe: () => (table, "2026-10-11", null));
        var f = Assert.Single(facts);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("快照 2026-10-11", f.Value);
        Assert.Contains("命中 1 條", f.Value);
        Assert.Contains("CVE-TEST-1", f.Value);
        Assert.Contains("待查證", f.Value);
        Assert.Contains("不等於已中招", f.Value);
        Assert.Contains("覆蓋有限", f.Value);
    }

    [Fact]
    public void 讀不到組建號時_標讀取錯誤而不是假裝沒問題()
    {
        var facts = CveOfflineFactsService.Collect(At, "Windows", "不明", "unknown",
            probe: () => (CveOfflineFactsService.ShippedTable, "—", null));
        var f = Assert.Single(facts);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("無法對照", f.UnavailableReason ?? "");
    }

    [Fact]
    public void 外部表缺少快照日期_拒收並說原因()
    {
        string path = Path.Combine(Path.GetTempPath(), "cve-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"entries":[]}""");
        try
        {
            Assert.Null(CveOfflineFactsService.Load(path, out string? reason));
            Assert.Contains("snapshotDate", reason ?? "");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 外部表格式壞掉_回讀不到並帶原因()
    {
        string path = Path.Combine(Path.GetTempPath(), "cve-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ 這不是 JSON");
        try
        {
            Assert.Null(CveOfflineFactsService.Load(path, out string? reason));
            Assert.Contains("解析失敗", reason ?? "");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 合格的外部表_載得回來且欄位對得上()
    {
        string path = Path.Combine(Path.GetTempPath(), "cve-" + Guid.NewGuid().ToString("N") + ".json");
        var table = new OfflineAdvisoryTable("2026-10-11",
            [new OfflineAdvisory("CVE-TEST-9", "產品", "30000", "30100", "摘要", "來源")]);
        File.WriteAllText(path, JsonSerializer.Serialize(table,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        try
        {
            var loaded = CveOfflineFactsService.Load(path, out string? reason);

            Assert.Null(reason);
            Assert.NotNull(loaded);
            Assert.Equal("2026-10-11", loaded!.SnapshotDate);
            var only = Assert.Single(loaded.Entries);
            Assert.Equal("CVE-TEST-9", only.CveId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void 檔案不存在_回沒有表而不是錯誤()
    {
        Assert.Null(CveOfflineFactsService.Load(
            Path.Combine(Path.GetTempPath(), "不存在-" + Guid.NewGuid().ToString("N") + ".json"), out string? reason));
        Assert.Null(reason);
    }
}
