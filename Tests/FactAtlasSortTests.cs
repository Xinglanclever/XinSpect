using System;
using System.IO;
using System.Linq;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 事實總表的就地排序與過濾可見性（U10-UI-0003／ui.table.controls）：
/// 排序只重排已過濾的列（不改變任何一列的內容）、排序狀態併進篩選聲明、
/// 清空過濾要回全量——「篩選中：N／M」讓部分清單不得冒充全部。
/// </summary>
public class FactAtlasSortTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value,
        FactAvailability availability = FactAvailability.Present, string? reason = null)
        => new(key, "測試", $"{key} 的名稱", value, "", "單元測試", FactTrustLevel.Measured, false, At,
            null, availability, reason);

    [Fact]
    public void 依狀態排序_讀不到的排最前_未收集的排最後()
    {
        var svc = new FactAtlasService();
        svc.Load(
        [
            Fact("tpm.present", "有"),
            Fact("spi.hsfsts", "", FactAvailability.ReadError, "MMIO 讀取失敗"),
        ]);
        Assert.Equal("目錄順序", FactAtlasService.SortOptions[0]);
        svc.SortMode = "依狀態（讀不到在前）";

        var keys = svc.Keys.Select(r => r.Key).ToList();
        // 讀失敗的那把在最前；讀到的在中間；沒注入的（本次未收集）整批在最後
        Assert.Equal("spi.hsfsts", keys[0]);
        Assert.Equal("tpm.present", keys[1]);
        Assert.All(keys.Skip(2), k => Assert.Equal("本次未收集",
            svc.Keys.Single(r => r.Key == k).StateText));
        // 排序不改變內容：同一列的值與狀態跟目錄順序時一致
        Assert.Equal("有", svc.Keys.Single(r => r.Key == "tpm.present").ValueText);
    }

    [Fact]
    public void 依值排序_有可用值的排前面()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("tpm.present", "有")]);
        svc.SortMode = "依值（有值在前）";

        var keys = svc.Keys.Select(r => r.Key).ToList();
        Assert.Equal("tpm.present", keys[0]);
        Assert.All(keys.Skip(1), k => Assert.Equal("本次未收集",
            svc.Keys.Single(r => r.Key == k).StateText));
    }

    [Fact]
    public void 未知的排序模式退回目錄順序_不靜默換成別的排法()
    {
        var svc = new FactAtlasService();
        svc.Load([]);
        svc.SortMode = "完全不存在的一個模式";

        Assert.Equal("目錄順序", svc.SortMode);
        Assert.Equal(FactKeyCatalog.Keys.OrderBy(k => k, StringComparer.Ordinal),
                     svc.Keys.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void 排序狀態併進篩選聲明_過濾條件可見()
    {
        var svc = new FactAtlasService();
        svc.Load([Fact("tpm.present", "有")]);
        svc.Filter = "tpm";

        Assert.Contains("篩選中", svc.FilterNote);
        Assert.Contains("／", svc.FilterNote);                       // N／M 分母聲明
        Assert.DoesNotContain("排序", svc.FilterNote);               // 目錄順序時不囉唆

        svc.SortMode = "依狀態（讀不到在前）";
        Assert.Contains("（排序：依狀態（讀不到在前））", svc.FilterNote);

        // 清空過濾要回全量：列數回到全目錄；排序仍在時聲明帶 N／N 與排序狀態
        svc.Filter = "";
        Assert.Contains($"篩選中：{FactKeyCatalog.Keys.Count}／{FactKeyCatalog.Keys.Count}", svc.FilterNote);
        Assert.Equal(FactKeyCatalog.Keys.Count, svc.Keys.Count);

        // 排序也清掉之後，才回到「顯示全部」的無篩選聲明
        svc.SortMode = "目錄順序";
        Assert.Contains("顯示全部", svc.FilterNote);
        Assert.DoesNotContain("排序", svc.FilterNote);
    }

    [Fact]
    public void 追蹤鍵字面落在Services原始碼裡()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Services", "FactAtlasService.cs")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Services", "FactAtlasService.cs"));
        Assert.Contains("ui.table.controls", text, StringComparison.Ordinal);
    }
}
