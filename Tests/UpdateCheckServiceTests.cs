using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 檢查更新的判定層（v2.55）。全部走純函數，<b>不碰真網路</b>——網路路徑只有
/// <see cref="UpdateCheckService.CheckAsync"/> 一個方法，行為約定是「任何失敗折回查不到」。
/// 核心誠實規則：查不到不得被讀成「已是最新」——把「不知道」冒充「沒問題」是本專案的紅線。
/// </summary>
public class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("2.10", "2.9", 1)]     // 逐字串比較會判反的經典案例：必須逐段 int
    [InlineData("2.9", "2.10", -1)]
    [InlineData("2.55", "2.55", 0)]
    [InlineData("2.55", "2.55.1", -1)] // 缺段視為 0
    [InlineData("3.0", "2.99.99", 1)]
    public void 版本比較逐段數字而不是逐字串(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(UpdateCheckService.CompareVersions(a, b)));

    [Fact]
    public void 遠端較新時判定有新版並帶出版本()
    {
        var v = UpdateCheckService.Judge("2.55", """{"tag_name":"v2.56","name":"x"}""");
        Assert.True(v.UpdateAvailable);
        Assert.False(v.UpToDate);
        Assert.Equal("2.56", v.LatestVersion);
        Assert.Contains("2.56", v.Detail, StringComparison.Ordinal);
        Assert.Contains("2.55", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 遠端相同或較舊時判定已是最新且不建議降級()
    {
        Assert.True(UpdateCheckService.Judge("2.55", """{"tag_name":"v2.55"}""").UpToDate);
        // 遠端 tag 比本地還舊（例如本地是尚未發佈的開發版）：已是最新，不建議降級
        var older = UpdateCheckService.Judge("2.56", """{"tag_name":"v2.55"}""");
        Assert.True(older.UpToDate);
        Assert.False(older.UpdateAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"no_tag":true}""")]
    public void 查不到一律雙假並說明原因_不得假裝已是最新(string? json)
    {
        var v = UpdateCheckService.Judge("2.55", json);
        Assert.False(v.UpdateAvailable);
        Assert.False(v.UpToDate);          // 「不知道」不得冒充「沒問題」
        Assert.Null(v.LatestVersion);
        Assert.False(string.IsNullOrWhiteSpace(v.Detail));
        Assert.Contains("查不到", v.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 大寫V前綴也認得()
    {
        var v = UpdateCheckService.Judge("2.55", """{"tag_name":"V2.56"}""");
        Assert.True(v.UpdateAvailable);
        Assert.Equal("2.56", v.LatestVersion);
    }
}
