using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 開機耗時分解的彙整與判讀。
///
/// 現有的可靠性歷史只有一個總時間；這裡要的是「是誰拖慢的」。
/// 守的規則：<b>degradation（比平常多花的時間）才是罪證，總時間不是</b>——
/// 一支服務本來就要花兩秒不代表它有問題，它比平常多花兩秒才有問題。
/// </summary>
public class BootBreakdownTests
{
    private static BootCulprit C(string name, string kind, long total, long degradation)
        => new() { Name = name, Kind = kind, TotalMs = total, DegradationMs = degradation };

    [Fact]
    public void 依比平常多花的時間排序而不是總時間()
    {
        var rows = BootBreakdownDecoder.Rank(
        [
            C("a.sys", "驅動", total: 9000, degradation: 100),
            C("b 服務", "服務", total: 1200, degradation: 3000),
            C("c 應用程式", "應用程式", total: 500, degradation: 800),
        ]);

        Assert.Equal("b 服務", rows[0].Name);
        Assert.Equal("c 應用程式", rows[1].Name);
        Assert.Equal("a.sys", rows[2].Name);
    }

    [Fact]
    public void 沒有多花時間的項目不列進來()
    {
        var rows = BootBreakdownDecoder.Rank([C("x", "服務", 5000, 0), C("y", "服務", 100, 250)]);
        Assert.Single(rows);
        Assert.Equal("y", rows[0].Name);
    }

    [Fact]
    public void 時間文字用秒與毫秒而不是一長串數字()
    {
        Assert.Equal("250 ms", BootBreakdownDecoder.MsText(250));
        Assert.Equal("3.20 秒", BootBreakdownDecoder.MsText(3200));
        Assert.Equal("—", BootBreakdownDecoder.MsText(0));
    }

    // ── 判決 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有資料時說沒有讀到而不是說開機很快()
    {
        var v = BootBreakdownDecoder.Judge(bootMs: 0, mainPathMs: 0, postBootMs: 0, []);
        Assert.Contains("讀不到", v.Headline);
        Assert.Equal(Severity.Neutral, v.Severity);
    }

    [Fact]
    public void 頻道不存在與頻道空白要給不同的訊息()
    {
        var missing = BootBreakdownDecoder.Judge(0, 0, 0, [], channelMissing: true);
        var empty = BootBreakdownDecoder.Judge(0, 0, 0, [], channelMissing: false);

        Assert.Contains("Server", missing.Detail);      // 說出為什麼沒有
        Assert.NotEqual(missing.Detail, empty.Detail);
        Assert.Equal(Severity.Neutral, missing.Severity);
    }

    [Fact]
    public void 有拖慢的項目時點名第一名並給出秒數()
    {
        var v = BootBreakdownDecoder.Judge(45_000, 20_000, 25_000,
            [C("SomeService", "服務", 8000, 6000)]);

        Assert.Contains("SomeService", v.Detail);
        Assert.Contains("6.00 秒", v.Detail);
        Assert.True(v.Severity is Severity.Warning or Severity.Serious);
    }

    [Fact]
    public void 開機很快又沒有拖慢項目時就說沒事()
    {
        var v = BootBreakdownDecoder.Judge(18_000, 9_000, 9_000, []);
        Assert.Equal(Severity.Good, v.Severity);
        Assert.Contains("沒有", v.Detail);
    }

    [Fact]
    public void 主路徑與登入後要分開講()
    {
        // 總時間長但主路徑很短＝慢在登入之後（啟動項），處理方式完全不同
        var v = BootBreakdownDecoder.Judge(60_000, 12_000, 48_000, []);
        Assert.Contains("登入後", v.Detail);
        Assert.Contains("啟動", v.Detail);
    }

    [Fact]
    public void 判決不宣稱能修好只指出方向()
    {
        var v = BootBreakdownDecoder.Judge(45_000, 20_000, 25_000, [C("X", "驅動", 9000, 7000)]);
        Assert.DoesNotContain("建議刪除", v.Detail);
        Assert.DoesNotContain("修好", v.Detail);
    }
}
