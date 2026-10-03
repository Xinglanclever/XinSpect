using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP15 系統與軟體層第一組：Windows Update 歷史的彙整契約。
/// 來源是 WUA COM（Microsoft.Update.Session）——通路層極薄以注入探測替代，
/// 彙整邏輯（最新一筆、近 30 天、失敗計數）逐項釘值；COM 不可用如實三態。
/// </summary>
public class WindowsUpdateHistoryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static XinSpect.WuHistoryEntry E(string title, uint result, DateTimeOffset? on, string cat = "安全性更新") =>
        new(title, result, on, cat);

    [Fact]
    public void 更新歷史_最新與30天與失敗計數逐項釘值()
    {
        var entries = new List<XinSpect.WuHistoryEntry>
        {
            E("2026-10 適用於 Windows 的累積更新", 2, At.AddDays(-1)),
            E("2026-09 安全性更新", 2, At.AddDays(-20), "安全性更新"),
            E("2026-08 定義更新", 4, At.AddDays(-40), "定義更新"),   // 失敗
            E("2025-06 舊更新", 2, At.AddDays(-480)),
        };

        var facts = XinSpect.WindowsUpdateHistoryService.Collect(At, probe: () => entries);

        Assert.All(facts, f => Assert.Equal("系統與軟體", f.Category));
        var latest = Assert.Single(facts, f => f.Key == "wu.history.latest");
        Assert.Equal(FactAvailability.Present, latest.Availability);
        Assert.Contains("2026-10 適用於 Windows 的累積更新", latest.Value);
        Assert.Contains("2026-10-02", latest.Value);           // InstalledOn 日期入列

        var total = Assert.Single(facts, f => f.Key == "wu.history.total");
        Assert.Equal(4u, total.NumericValue);

        var recent = Assert.Single(facts, f => f.Key == "wu.history.30d");
        Assert.Equal(2u, recent.NumericValue);                  // 只有前兩筆在 30 天內

        var failed = Assert.Single(facts, f => f.Key == "wu.history.failed");
        Assert.Equal(1u, failed.NumericValue);
        Assert.Contains("2026-08 定義更新", failed.Value);       // 最近一次失敗的標題要說得出來
    }

    [Fact]
    public void 更新歷史_無時間戳與空清單都如實_()
    {
        // 日期缺失（WUA 的 date 可能為 0）——不猜日期，標題照列
        var noDate = XinSpect.WindowsUpdateHistoryService.Collect(At, probe: () => new List<XinSpect.WuHistoryEntry>
        {
            E("無日期更新", 2, null),
        });
        var latest = Assert.Single(noDate, f => f.Key == "wu.history.latest");
        Assert.Contains("無日期更新", latest.Value);
        Assert.DoesNotContain("（安裝於", latest.Value);

        // 空歷史是「有答：0 筆」，不是讀不到
        var empty = XinSpect.WindowsUpdateHistoryService.Collect(At, probe: () => new List<XinSpect.WuHistoryEntry>());
        var total = Assert.Single(empty, f => f.Key == "wu.history.total");
        Assert.Equal(FactAvailability.Present, total.Availability);
        Assert.Equal(0u, total.NumericValue);
        Assert.Contains("沒有任何記錄", total.Value);
    }

    [Fact]
    public void 更新歷史_COM不可用如實三態()
    {
        var facts = XinSpect.WindowsUpdateHistoryService.Collect(At, probe: () => null);
        Assert.NotEmpty(facts);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("COM", f.UnavailableReason);
            Assert.Null(f.NumericValue);
        });
    }
}
