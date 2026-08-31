using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>一鍵裝機：清單去重的規則，以及 winget export 匯出檔的解析。</summary>
public class WingetCatalogTests
{
    private static IEnumerable<WingetPackage> AllPackages()
        => WingetService.Catalog().SelectMany(c => c.Packages);

    [Fact]
    public void 清單沒有重複的套件ID()
    {
        var dup = AllPackages().GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                               .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dup);
    }

    [Fact]
    public void 清單不含曦覽自己就在做的檢測工具()
    {
        // 裝 CPU-Z／GPU-Z／CrystalDiskInfo 只是多一份和曦覽處理器／顯示卡／儲存裝置頁重複的答案。
        var ids = AllPackages().Select(p => p.Id.ToLowerInvariant()).ToList();
        Assert.DoesNotContain("cpuid.cpu-z", ids);
        Assert.DoesNotContain("techpowerup.gpu-z", ids);
        Assert.DoesNotContain("crystaldewworld.crystaldiskinfo", ids);
    }

    [Fact]
    public void 清單不含系統內建的軟體()
    {
        // Edge 與 Windows Terminal 都是 Windows 內建，用 winget 裝它們沒有意義。
        var ids = AllPackages().Select(p => p.Id.ToLowerInvariant()).ToList();
        Assert.DoesNotContain("microsoft.edge", ids);
        Assert.DoesNotContain("microsoft.windowsterminal", ids);
    }

    [Fact]
    public void 每一類都不超過四個且沒有只有一項的分類()
    {
        // 只有一項的分類是「該併進別類」的訊號（原本的「終端機與 SSH」就是這樣併進開發工具的）。
        foreach (var c in WingetService.Catalog())
        {
            Assert.True(c.Packages.Count >= 2, $"{c.Name} 只有 {c.Packages.Count} 項，應併入其他分類");
            Assert.True(c.Packages.Count <= 4, $"{c.Name} 有 {c.Packages.Count} 項，功能重疊的應再刪");
        }
    }

    [Fact]
    public void 每一類的推薦不超過兩個()
    {
        // 「勾選推薦」要是一份能直接按下去的乾淨清單，不是把整個分類全勾起來。
        foreach (var c in WingetService.Catalog())
            Assert.True(c.Packages.Count(p => p.Recommended) <= 2, $"{c.Name} 推薦了太多項");
    }

    [Fact]
    public void 每個套件都有名稱與說明()
    {
        foreach (var p in AllPackages())
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Desc));
            Assert.False(p.IsInstalled);      // 預設一律「不知道有沒有裝」
            Assert.False(p.IsSelected);
        }
    }

    [Fact]
    public void 已安裝狀態文字只在標記後出現()
    {
        var p = new WingetPackage { Id = "X.Y", Name = "N", Desc = "D" };
        Assert.Equal("", p.StateText);
        p.IsInstalled = true;
        Assert.Equal("已安裝", p.StateText);
    }

    // ── winget export 匯出檔 ────────────────────────────────────────────────

    private const string ExportJson = """
    {
      "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
      "CreationDate": "2026-08-31T10:00:00.000-00:00",
      "Sources": [
        {
          "Packages": [
            { "PackageIdentifier": "7zip.7zip" },
            { "PackageIdentifier": "Git.Git", "Version": "2.46.0" }
          ],
          "SourceDetails": { "Argument": "https://cdn.winget.microsoft.com/cache", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name": "winget", "Type": "Microsoft.PreIndexed.Package" }
        },
        {
          "Packages": [ { "PackageIdentifier": "XPFCC4CD725961" } ],
          "SourceDetails": { "Name": "msstore" }
        }
      ],
      "WinGetVersion": "1.9.25180"
    }
    """;

    [Fact]
    public void 匯出檔解析_取出所有來源的套件ID()
    {
        var ids = WingetService.ParseExportedIds(ExportJson).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "7zip.7zip", "Git.Git", "XPFCC4CD725961" }, ids);
    }

    [Fact]
    public void 匯出檔解析_ID比對不分大小寫()
    {
        // winget 的套件 ID 本身不分大小寫，清單裡寫 7zip.7zip、匯出檔寫 7Zip.7Zip 是同一個。
        var ids = WingetService.ParseExportedIds(ExportJson);
        Assert.True(ids.Contains("7Zip.7Zip") && ids.Contains("git.git"), "集合應以不分大小寫的比較器建立");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"Sources": "壞掉的型別"}""")]
    [InlineData("""{"Sources": [ {"Packages": {} } ]}""")]
    [InlineData("""{"Sources": [ {"Packages": [ 42, null, {"NoId": 1} ] } ]}""")]
    public void 匯出檔解析_格式不如預期時回空集合而不丟例外(string json)
    {
        // 解析失敗代表「不知道裝了什麼」，此時整頁照常可用、只是不顯示「已安裝」，
        // 不能讓一鍵裝機因為一個壞掉的匯出檔就整頁失效。
        Assert.Empty(WingetService.ParseExportedIds(json));
    }
}
