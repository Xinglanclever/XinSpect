using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>伺服器合規摘要（SV-014）：由既有角色事實聚合，不重做盤點。</summary>
public class ServerComplianceFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);

    private static HardwareFact Surface(int roles) =>
        new(RoleSurfaceFactsService.SurfaceKey, "系統與軟體", "已安裝角色", $"{roles} 個角色", "", "WMI",
            FactTrustLevel.Reported, false, At, roles);

    private static HardwareFact Role(string name, string services) =>
        new($"role.installed.{name}", "系統與軟體", name, services, "", "WMI", FactTrustLevel.Reported, false, At);

    [Fact]
    public void 角色服務狀態_解析得出執行中與停止數()
    {
        var standings = ServerComplianceFactsService.Standings(
        [
            Role("AD-Domain-Services", "已安裝（伺服器角色）；服務執行中 2、已安裝未執行 1"),
            Role("Print-Services", "已安裝；服務執行中 1、已安裝未執行 0"),
            Role("某功能", "已安裝；服務狀態未查"),
        ]);

        Assert.Equal(2, standings.Count);   // 「服務狀態未查」不當成 0，也不列入
        Assert.Equal(1, standings[0].Stopped);
        Assert.Equal(0, standings[1].Stopped);
    }

    [Fact]
    public void 摘要_要說出角色數與沒全跑的角色_並提醒裝了不等於配置好()
    {
        var f = ServerComplianceFactsService.Collect(At,
        [
            Surface(5),
            Role("AD-Domain-Services", "已安裝（伺服器角色）；服務執行中 2、已安裝未執行 1"),
            Role("Print-Services", "已安裝；服務執行中 1、已安裝未執行 0"),
        ], "Microsoft Windows NT 10.0.26100.0");

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("已安裝角色／功能 5 個", f.Value);
        Assert.Contains("1 個角色的服務沒全跑", f.Value);
        Assert.Contains("AD-Domain-Services（停 1）", f.Value);
        Assert.Contains("不等於「配置好」", f.Value);
        Assert.Equal(5, f.NumericValue);
    }

    [Fact]
    public void 沒有角色事實時_摘要標不適用不憑空生()
    {
        var f = ServerComplianceFactsService.Collect(At, [], null);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("不憑空生", f.UnavailableReason ?? "");
    }

    [Fact]
    public void 角色盤點本身不可得時_摘要跟著不可得()
    {
        var surface = Surface(0) with { Availability = FactAvailability.ReadError, UnavailableReason = "WMI 失敗" };
        var f = ServerComplianceFactsService.Collect(At, [surface], null);

        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Contains("WMI 失敗", f.UnavailableReason ?? "");
    }
}

/// <summary>「磁碟為什麼滿」（SG-002）：唯讀排行、深度上限、略過計數。</summary>
public class DiskFullFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    /// <summary>假檔案系統：路徑 → 子目錄；路徑 → 檔案（名稱, 大小）。未列出的目錄視為讀不到。</summary>
    private static DiskProbe Probe(
        Dictionary<string, string[]> dirs, Dictionary<string, (string, long)[]> files,
        HashSet<string>? denied = null)
    {
        denied ??= [];
        return new DiskProbe(
            dir =>
            {
                if (denied.Contains(dir)) throw new IOException("拒絕存取");
                if (!dirs.TryGetValue(dir, out var children)) throw new IOException("拒絕存取");
                return children;
            },
            dir =>
            {
                if (denied.Contains(dir)) throw new IOException("拒絕存取");
                return files.TryGetValue(dir, out var list)
                    ? list.Select(x => (x.Item1, x.Item2)).ToList()
                    : [];
            });
    }

    [Fact]
    public void 排行按大小遞減_總量與檔案數也算得出來()
    {
        var probe = Probe(
            new()
            {
                [@"C:\R"] = [@"C:\R\sub1", @"C:\R\sub2"],
                [@"C:\R\sub1"] = [],
                [@"C:\R\sub2"] = [],
            },
            new()
            {
                [@"C:\R"] = [("big.bin", 300)],
                [@"C:\R\sub1"] = [("a.bin", 100)],
                [@"C:\R\sub2"] = [("b.bin", 200)],
            });
        var result = DiskFullFactsService.Scan(
            new DiskFullFactsService.ScanRequest([@"C:\R"], MaxDepth: 2, TopCount: 8, BigFileBytes: 200), probe);

        Assert.Equal(3, result.Top.Count);
        Assert.Equal(@"C:\R\big.bin", result.Top[0].Path);
        Assert.Equal(300, result.Top[0].Bytes);
        Assert.Equal(@"C:\R\sub2", result.Top[1].Path);
        Assert.Equal(@"C:\R\sub1", result.Top[2].Path);
        Assert.Equal(600, result.TotalBytes);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(0, result.SkippedDirectories);
    }

    [Fact]
    public void 深度上限生效_超過的目錄不加總()
    {
        var probe = Probe(
            new()
            {
                [@"C:\R"] = [@"C:\R\a"],
                [@"C:\R\a"] = [@"C:\R\a\b"],
                [@"C:\R\a\b"] = [],
            },
            new()
            {
                [@"C:\R"] = [],
                [@"C:\R\a"] = [("x", 10)],
                [@"C:\R\a\b"] = [("y", 1000)],
            });

        Assert.Equal(10, DiskFullFactsService.Scan(
            new DiskFullFactsService.ScanRequest([@"C:\R"], MaxDepth: 1, TopCount: 4), probe).TotalBytes);
        Assert.Equal(1010, DiskFullFactsService.Scan(
            new DiskFullFactsService.ScanRequest([@"C:\R"], MaxDepth: 2, TopCount: 4), probe).TotalBytes);
    }

    [Fact]
    public void 讀不到的目錄計入略過_不當成零用量()
    {
        var probe = Probe(
            new() { [@"C:\R"] = [@"C:\R\denied"] },
            new() { [@"C:\R"] = [("keep", 5)] },
            denied: [@"C:\R\denied"]);
        var result = DiskFullFactsService.Scan(new DiskFullFactsService.ScanRequest([@"C:\R"]), probe);

        Assert.Equal(1, result.SkippedDirectories);
        Assert.Equal(5, result.TotalBytes);
    }

    [Fact]
    public void 事實要說出只讀不刪與低估風險()
    {
        var probe = Probe(
            new() { [@"C:\R"] = [] },
            new() { [@"C:\R"] = [("a", 3)] },
            denied: [@"C:\R\missing"]);
        var facts = DiskFullFactsService.Collect(At,
            new DiskFullFactsService.ScanRequest([@"C:\R"], BigFileBytes: long.MaxValue), probe);
        var f = Assert.Single(facts);

        Assert.Contains("只讀排行、不刪任何東西", f.Value);
        Assert.Contains("可能低估", f.Value);
        Assert.Equal(3, f.NumericValue);
    }

    [Fact]
    public void 全部根都讀不到時_標讀取錯誤()
    {
        var probe = Probe(new(), new(), denied: [@"C:\R"]);
        var facts = DiskFullFactsService.Collect(At, new DiskFullFactsService.ScanRequest([@"C:\R"]), probe);

        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts).Availability);
    }
}

/// <summary>SBOM（RS-002）：CycloneDX 格式、可重現、元件排序與去重。</summary>
public class SbomTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static SbomComponent C(string type, string name, string version) => new(type, name, version, "廠商");

    [Fact]
    public void 文件要有_必備欄位()
    {
        var doc = SbomService.Build([C("device-driver", "網卡", "1.2.3"), C("library", "開源元件", "9.9")],
            "曦覽 XinSpect", "2.56", At);

        Assert.Equal("CycloneDX", doc.BomFormat);
        Assert.Equal("1.5", doc.SpecVersion);
        Assert.StartsWith("urn:uuid:", doc.SerialNumber);
        Assert.Equal(1, doc.Version);
        Assert.Equal("曦覽 XinSpect", doc.Metadata.Component.Name);
        Assert.Equal(2, doc.Components.Count);
    }

    [Fact]
    public void 元件要排序與去重_同一組輸入得同一份文件()
    {
        var doc = SbomService.Build([C("library", "b", "1"), C("library", "a", "1"), C("library", "b", "1")],
            "app", "1.0", At);

        Assert.Equal(["a", "b"], doc.Components.Select(c => c.Name));
        Assert.Equal(SbomService.Build([C("library", "b", "1"), C("library", "a", "1")], "app", "1.0", At).SerialNumber,
            doc.SerialNumber);
    }

    [Fact]
    public void JSON要解得出且含bomFormat_內容雜湊一致()
    {
        var doc = SbomService.Build([C("library", "a", "1")], "app", "1.0", At);
        string json = SbomService.ToJson(doc);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal("CycloneDX", parsed.RootElement.GetProperty("bomFormat").GetString());
        Assert.Equal(SbomService.ContentHash(json), SbomService.ContentHash(SbomService.ToJson(doc)));
        Assert.Equal(64, SbomService.ContentHash(json).Length);   // SHA-256 hex
    }

    [Fact]
    public void 事實要說出尚未寫檔與元件來源界線()
    {
        var facts = SbomService.Collect(At, "app", "1.0",
            probe: () => ([C("device-driver", "網卡", "1")], null));
        var f = Assert.Single(facts);

        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("元件 1 個", f.Value);
        Assert.Contains("尚未寫檔", f.Value);
        Assert.Contains("不含已安裝應用程式套件與授權資訊", f.Value);
    }

    [Fact]
    public void 元件來源失敗時_標讀取錯誤不給空SBOM()
    {
        var facts = SbomService.Collect(At, "app", "1.0",
            probe: () => throw new InvalidOperationException("WMI 掛了"));
        var f = Assert.Single(facts);

        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("不給一份空 SBOM", f.UnavailableReason ?? "");
    }

    [Fact]
    public void 驅動清單讀不到時_仍給作業系統元件並如實註記()
    {
        // RealComponents 在沒有 WMI 的環境會回註記；這一條只驗它不會拋且至少有一個元件
        var (components, _) = SbomService.RealComponents();

        Assert.NotEmpty(components);
        Assert.Contains(components, c => c.Type == "operating-system");
    }
}
